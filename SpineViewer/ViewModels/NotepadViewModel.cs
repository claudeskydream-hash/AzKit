using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NLog;
using SpineViewer.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 记事本：左侧栏「记事本」页是文件夹树（NotepadPanel），右侧「笔记」页签是编辑区（NotepadEditorPanel）。
    /// <para>
    /// 树的顶层 = 默认笔记目录 <see cref="NotesDirectory"/>（不能移除）+ 用户添加的文件夹；展开后列出里面的
    /// md / txt / json（<see cref="FileExtensions"/>）和含这类文件的子文件夹，点文件就在右侧打开编辑。
    /// </para>
    /// <para>
    /// 编辑器用 AvalonEdit（github.com/icsharpcode/AvalonEdit，MIT）。打开过的文件留在 <see cref="_opened"/> 里
    /// （各自一份撤销栈，点来点去不丢改动和撤销记录），读写编码照原文件（见 <see cref="NoteDocument.Load"/>）。
    /// </para>
    /// <para>
    /// 保存时机：Ctrl+S 存当前文件；左侧栏从记事本切走、退出程序时把改过的全部存掉（见 <see cref="SaveAll"/>）。
    /// 添加了哪些文件夹、哪些展开着、上次编辑哪个文件记在 data/notepad.json，下次启动原样恢复。
    /// </para>
    /// </summary>
    public class NotepadViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>右侧页签里「笔记」页的序号（MainWindowViewModel.ViewTabIndex：0 模型动画 / 1 预览图 / 2 存档 / 3 笔记）</summary>
        public const int EditorTabIndex = 3;

        /// <summary>树上列出、能打开编辑的文件类型</summary>
        public static readonly string[] FileExtensions = { ".md", ".txt", ".json" };

        /// <summary>扫子文件夹时跳过的目录（版本库、编译产物、依赖包，里面的 json 没人想翻）</summary>
        private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", "Library", "Temp",
        };

        /// <summary>超过这个大小的文件打开前先确认（大 json 塞进编辑器会卡）</summary>
        private const long LargeFileBytes = 10L * 1024 * 1024;

        /// <summary>新建笔记的默认存放目录（树上第一个节点）</summary>
        public static readonly string NotesDirectory = Path.Combine(App.DataDirectory, "notes");

        /// <summary>设置保存位置</summary>
        private static readonly string SettingsPath = Path.Combine(App.DataDirectory, "notepad.json");

        /// <summary>打开过的文件（键 = 小写完整路径）</summary>
        private readonly Dictionary<string, NoteDocument> _opened = new();

        /// <summary>上次展开着的文件夹（启动时照它展开；之后随展开 / 收起更新）</summary>
        private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>正在恢复上次的状态，期间不回写设置</summary>
        private bool _loading;

        public NotepadViewModel()
        {
            LoadSettings();
        }

        /// <summary>新建了一篇笔记（编辑区据此把光标放进去，新建完直接打字）</summary>
        public event Action? NoteCreated;

        /// <summary>树的顶层文件夹</summary>
        public ObservableCollection<NoteFolderNode> Folders { get; } = new();

        /// <summary>当前在编辑的文件</summary>
        public NoteDocument? SelectedDocument
        {
            get => _selectedDocument;
            private set
            {
                if (SetProperty(ref _selectedDocument, value))
                {
                    OnPropertyChanged(nameof(HasDocument));
                    SaveSettings();
                }
            }
        }
        private NoteDocument? _selectedDocument;

        /// <summary>右侧有没有在编辑的文件（没有时显示提示）</summary>
        public bool HasDocument => _selectedDocument is not null;

        /// <summary>树上选中的节点（新建笔记放在它所在的文件夹）</summary>
        private NoteTreeNode? _selectedNode;

        #region 顶部按钮命令

        /// <summary>新建笔记：放进树上选中的文件夹（选的是文件就放它旁边），什么都没选放默认笔记目录</summary>
        public RelayCommand Cmd_NewNote => _cmd_NewNote ??= new(() => NewNoteIn(FolderOfSelection()));
        private RelayCommand? _cmd_NewNote;

        /// <summary>添加一个文件夹到树上</summary>
        public RelayCommand Cmd_AddFolder => _cmd_AddFolder ??= new(AddFolder);
        private RelayCommand? _cmd_AddFolder;

        /// <summary>Ctrl+S：保存当前文件</summary>
        public RelayCommand Cmd_Save => _cmd_Save ??= new(() => { if (SelectedDocument is not null) Save(SelectedDocument); });
        private RelayCommand? _cmd_Save;

        /// <summary>重扫所有已展开的文件夹（外面增删了文件时用）</summary>
        public RelayCommand Cmd_RefreshAll => _cmd_RefreshAll ??= new(() =>
        {
            foreach (NoteFolderNode folder in Folders)
            {
                if (folder.IsExpanded)
                    folder.Reload();
            }
        });
        private RelayCommand? _cmd_RefreshAll;

        #endregion

        #region 节点操作（树上右键菜单经节点命令转到这里）

        /// <summary>树上选中了某个节点：是文件就打开到右侧</summary>
        internal void OnNodeSelected(NoteTreeNode node)
        {
            _selectedNode = node;
            if (node is NoteFileNode file)
                OpenFile(file);
        }

        internal void OnFolderExpandedChanged(NoteFolderNode folder)
        {
            if (folder.IsExpanded)
                _expanded.Add(folder.Path);
            else
                _expanded.Remove(folder.Path);
            SaveSettings();
        }

        internal bool IsRememberedExpanded(string path) => _expanded.Contains(path);

        /// <summary>在某个文件夹里建一个空 md（名字「笔记N」不重复），立刻落盘并打开</summary>
        internal void NewNoteIn(NoteFolderNode? folder)
        {
            string dir = folder?.Path ?? NotesDirectory;
            Directory.CreateDirectory(dir);

            string path;
            int index = 1;
            do
            {
                path = Path.Combine(dir, $"笔记{index}.md");
                index++;
            }
            while (File.Exists(path));

            NoteDocument doc = NoteDocument.CreateEmpty(path);
            if (!Save(doc))
                return;
            _opened[Key(path)] = doc;

            // 刷出新文件并选中它（选中会走 OnNodeSelected 打开到右侧）
            folder ??= Folders.FirstOrDefault();
            if (folder is not null)
            {
                folder.Reload();
                folder.IsExpanded = true;
                NoteFileNode? node = folder.Children.OfType<NoteFileNode>().FirstOrDefault(n => SamePath(n.Path, path));
                if (node is not null)
                    node.IsSelected = true;
            }
            SelectedDocument = doc;
            NoteCreated?.Invoke();
        }

        internal void RemoveFolder(NoteFolderNode folder)
        {
            if (!folder.CanRemove)
                return;
            Folders.Remove(folder);
            SaveSettings();
        }

        internal void SaveNode(NoteFileNode node)
        {
            if (node.Document is not null)
                Save(node.Document);
        }

        /// <summary>重命名文件（扩展名不变）；打开着的文档跟着改路径</summary>
        internal void RenameNode(NoteFileNode node)
        {
            string ext = Path.GetExtension(node.Path);
            string? name = Views.TextInputDialog.Show("重命名", "新名字（不用写扩展名）：", Path.GetFileNameWithoutExtension(node.Path));
            if (name is null)
                return;

            name = name.Trim();
            if (name.Length == 0 || name == Path.GetFileNameWithoutExtension(node.Path))
                return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessagePopupService.Warn("名字里不能有 \\ / : * ? \" < > | 这些字符");
                return;
            }

            string newPath = Path.Combine(Path.GetDirectoryName(node.Path)!, name + ext);
            if (File.Exists(newPath))
            {
                MessagePopupService.Warn($"同一个文件夹里已经有「{name}{ext}」了，换个名字。");
                return;
            }

            try
            {
                File.Move(node.Path, newPath);
            }
            catch (Exception ex)
            {
                _logger.Error("重命名失败 {0} -> {1}: {2}", node.Path, newPath, ex.Message);
                MessagePopupService.Error($"重命名失败：{ex.Message}");
                return;
            }

            if (_opened.Remove(Key(node.Path), out NoteDocument? doc))
            {
                doc.FilePath = newPath;
                _opened[Key(newPath)] = doc;
            }
            node.Path = newPath;
            SaveSettings();
        }

        /// <summary>另存为：内容写到新位置，之后编辑的是新文件（原文件不动，未保存的改动也不写进原文件）</summary>
        internal void SaveAsNode(NoteFileNode node)
        {
            NoteDocument? doc = node.Document ?? GetOrLoad(node.Path);
            if (doc is null)
                return;

            string ext = Path.GetExtension(node.Path);
            SaveFileDialog dialog = new()
            {
                Title = "另存为",
                Filter = $"{ext} 文件|*{ext}|所有文件 (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(node.Path),
                FileName = Path.GetFileName(node.Path),
                DefaultExt = ext,
                AddExtension = true,
            };
            if (dialog.ShowDialog() != true || SamePath(dialog.FileName, node.Path))
                return;

            string oldPath = doc.FilePath;
            doc.FilePath = dialog.FileName;
            if (!Save(doc))
            {
                doc.FilePath = oldPath;
                return;
            }

            // 这份文档从此归新文件；原文件从「已打开」里拿掉，下次点它按磁盘内容重新读（没存的改动跟着新文件走了）。
            // 刷新会重建树节点，原文件那一项随之和这份文档脱钩
            _opened.Remove(Key(oldPath));
            _opened[Key(doc.FilePath)] = doc;
            SelectedDocument = doc;
            Cmd_RefreshAll.Execute(null);
        }

        #endregion

        /// <summary>
        /// 把改过的文件全部存掉。左侧栏从记事本切走、退出程序时调。
        /// 只写改过的，没改的不碰文件（不刷修改时间）。
        /// </summary>
        public void SaveAll()
        {
            foreach (NoteDocument doc in _opened.Values)
            {
                if (doc.IsDirty)
                    Save(doc);
            }
        }

        /// <summary>打开过就返回那份</summary>
        internal NoteDocument? FindOpened(string path) => _opened.GetValueOrDefault(Key(path));

        private void OpenFile(NoteFileNode node)
        {
            NoteDocument? doc = GetOrLoad(node.Path);
            if (doc is null)
                return;
            node.Attach(doc);
            SelectedDocument = doc;
        }

        /// <summary>打开过的直接给；没打开过从磁盘读（太大的先确认）；读失败返回 null</summary>
        private NoteDocument? GetOrLoad(string path)
        {
            if (_opened.TryGetValue(Key(path), out NoteDocument? exist))
                return exist;

            try
            {
                long size = new FileInfo(path).Length;
                if (size > LargeFileBytes && !_loading
                    && !MessagePopupService.OKCancel($"「{Path.GetFileName(path)}」有 {size / 1024 / 1024} MB，打开会比较卡，继续？"))
                    return null;

                NoteDocument doc = NoteDocument.Load(path);
                _opened[Key(path)] = doc;
                return doc;
            }
            catch (Exception ex)
            {
                _logger.Error("打开失败 {0}: {1}", path, ex.Message);
                if (!_loading)
                    MessagePopupService.Error($"打开失败：{ex.Message}");
                return null;
            }
        }

        private bool Save(NoteDocument doc)
        {
            try
            {
                doc.Save();
            }
            catch (Exception ex)
            {
                _logger.Error("保存失败 {0}: {1}", doc.FilePath, ex.Message);
                MessagePopupService.Error($"保存「{Path.GetFileName(doc.FilePath)}」失败：{ex.Message}");
                return false;
            }

            _logger.Info("已保存 {0}", doc.FilePath);
            return true;
        }

        private void AddFolder()
        {
            OpenFolderDialog dialog = new() { Title = "选择要显示在记事本里的文件夹", Multiselect = true };
            if (dialog.ShowDialog() != true)
                return;

            foreach (string dir in dialog.FolderNames)
            {
                if (Folders.Any(f => SamePath(f.Path, dir)))
                    continue;
                NoteFolderNode node = new(this, dir, true, true, $"{Path.GetFileName(dir.TrimEnd('\\'))}  ({dir})");
                Folders.Add(node);
                node.IsExpanded = true;
            }
            SaveSettings();
        }

        /// <summary>新建笔记该放哪：选中的是文件夹就放里面，是文件就放它所在的文件夹</summary>
        private NoteFolderNode? FolderOfSelection()
        {
            if (_selectedNode is NoteFolderNode folder)
                return folder;
            if (_selectedNode is NoteFileNode file)
                return FindFolderNode(Path.GetDirectoryName(file.Path)!);
            return null;
        }

        /// <summary>在已加载的树里找某个路径的文件夹节点</summary>
        private NoteFolderNode? FindFolderNode(string path)
        {
            Stack<NoteFolderNode> stack = new(Folders);
            while (stack.Count > 0)
            {
                NoteFolderNode node = stack.Pop();
                if (SamePath(node.Path, path))
                    return node;
                foreach (NoteFolderNode child in node.Children.OfType<NoteFolderNode>())
                    stack.Push(child);
            }
            return null;
        }

        /// <summary>在已加载的树里找某个文件的节点</summary>
        private NoteFileNode? FindFileNode(string path)
        {
            NoteFolderNode? folder = FindFolderNode(Path.GetDirectoryName(path) ?? string.Empty);
            return folder?.Children.OfType<NoteFileNode>().FirstOrDefault(n => SamePath(n.Path, path));
        }

        #region 扫盘工具

        internal static bool IsNoteFile(string path) =>
            FileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>这一层的子文件夹（按名字排序；跳过隐藏、点开头和 <see cref="SkipDirectories"/>；没权限的忽略）</summary>
        internal static IEnumerable<string> SafeGetDirectories(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir)
                    .Where(d =>
                    {
                        string name = Path.GetFileName(d);
                        if (name.StartsWith('.') || SkipDirectories.Contains(name))
                            return false;
                        return (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
                    })
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>这一层里能编辑的文件（按名字排序）</summary>
        internal static IEnumerable<string> SafeGetNoteFiles(string dir)
        {
            try
            {
                return Directory.GetFiles(dir)
                    .Where(IsNoteFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>这个文件夹（含更深层）里有没有能编辑的文件 —— 没有的子文件夹不上树，免得满屏空目录</summary>
        internal static bool HasNoteFiles(string dir)
        {
            if (SafeGetNoteFiles(dir).Any())
                return true;
            return SafeGetDirectories(dir).Any(HasNoteFiles);
        }

        internal static void ShowInExplorer(string path)
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }

        private static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        #endregion

        #region 设置的保存与恢复

        private sealed class NotepadSettings
        {
            /// <summary>用户添加的文件夹（默认笔记目录不在里面，它总是第一个）</summary>
            public List<string> Folders { get; set; } = new();

            /// <summary>展开着的文件夹</summary>
            public List<string> Expanded { get; set; } = new();

            /// <summary>上次在编辑的文件</summary>
            public string? SelectedFile { get; set; }
        }

        private void LoadSettings()
        {
            _loading = true;

            NotepadSettings s = new();
            if (File.Exists(SettingsPath))
            {
                try
                {
                    s = JsonSerializer.Deserialize<NotepadSettings>(File.ReadAllText(SettingsPath)) ?? new();
                }
                catch (Exception ex)
                {
                    _logger.Warn("记事本设置读取失败，用默认 {0}", ex.Message);
                }
            }

            // 第一次用：默认笔记目录展开
            if (!File.Exists(SettingsPath))
                s.Expanded.Add(NotesDirectory);

            foreach (string dir in s.Expanded)
                _expanded.Add(dir);

            Directory.CreateDirectory(NotesDirectory);
            Folders.Add(new NoteFolderNode(this, NotesDirectory, true, false, "AzKit 笔记"));
            foreach (string dir in s.Folders)
            {
                // 文件夹被删了 / 盘没插：先不显示，但别从设置里丢（下次插上还在）
                if (Directory.Exists(dir))
                    Folders.Add(new NoteFolderNode(this, dir, true, true, $"{Path.GetFileName(dir.TrimEnd('\\'))}  ({dir})"));
                else
                    _missingFolders.Add(dir);
            }

            foreach (NoteFolderNode folder in Folders)
            {
                if (_expanded.Contains(folder.Path))
                    folder.IsExpanded = true;
            }

            if (!string.IsNullOrEmpty(s.SelectedFile) && File.Exists(s.SelectedFile))
            {
                NoteFileNode? node = FindFileNode(s.SelectedFile);
                if (node is not null)
                    node.IsSelected = true;
                else
                    SelectedDocument = GetOrLoad(s.SelectedFile);
            }

            _loading = false;
        }

        /// <summary>设置里有、但这次启动时不存在的文件夹（原样写回设置）</summary>
        private readonly List<string> _missingFolders = new();

        private void SaveSettings()
        {
            if (_loading)
                return;

            try
            {
                Directory.CreateDirectory(App.DataDirectory);
                NotepadSettings s = new()
                {
                    Folders = Folders.Where(f => f.CanRemove).Select(f => f.Path).Concat(_missingFolders).ToList(),
                    Expanded = _expanded.ToList(),
                    SelectedFile = SelectedDocument?.FilePath,
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            catch (Exception ex)
            {
                _logger.Warn("记事本设置保存失败 {0}", ex.Message);
            }
        }

        #endregion
    }
}
