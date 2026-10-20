using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 记事本左栏树的节点基类：文件夹 <see cref="NoteFolderNode"/> / 文件 <see cref="NoteFileNode"/>。
    /// 右键菜单直接绑节点上的命令，命令转回 <see cref="NotepadViewModel"/> 干活。
    /// </summary>
    public abstract class NoteTreeNode : ObservableObject
    {
        protected NoteTreeNode(NotepadViewModel owner, string path)
        {
            Owner = owner;
            _path = path;
        }

        public NotepadViewModel Owner { get; }

        /// <summary>磁盘上的完整路径</summary>
        public string Path
        {
            get => _path;
            set
            {
                if (SetProperty(ref _path, value))
                    OnPathChanged();
            }
        }
        private string _path;

        protected virtual void OnPathChanged() { }

        /// <summary>树上显示的文字</summary>
        public abstract string Header { get; }

        /// <summary>选中状态（与 TreeViewItem.IsSelected 双向绑定，代码里可以借它选中某一项）</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value) && value)
                    Owner.OnNodeSelected(this);
            }
        }
        private bool _isSelected;

        /// <summary>在资源管理器里显示</summary>
        public RelayCommand Cmd_ShowInExplorer => _cmd_ShowInExplorer ??= new(() => NotepadViewModel.ShowInExplorer(Path));
        private RelayCommand? _cmd_ShowInExplorer;
    }

    /// <summary>
    /// 文件夹节点：展开时才扫盘（懒加载），只列 <see cref="NotepadViewModel.FileExtensions"/> 里的文件，
    /// 以及里面（含更深层）有这类文件的子文件夹。
    /// </summary>
    public class NoteFolderNode : NoteTreeNode
    {
        private bool _loaded;

        /// <param name="isRoot">是不是树的顶层（默认笔记目录或用户添加的文件夹）</param>
        /// <param name="removable">能不能从树上移除（默认笔记目录不能）</param>
        /// <param name="title">顶层显示名；子文件夹传 null 用文件夹名</param>
        public NoteFolderNode(NotepadViewModel owner, string path, bool isRoot, bool removable, string? title = null)
            : base(owner, path)
        {
            IsRoot = isRoot;
            CanRemove = removable;
            _title = title;

            // 没展开前塞一个占位子项，树上才有展开箭头；第一次展开时 Reload 换成真内容
            Children.Add(new NoteLoadingNode(owner));
        }

        private readonly string? _title;

        public bool IsRoot { get; }

        public bool CanRemove { get; }

        public override string Header => _title ?? System.IO.Path.GetFileName(Path);

        public ObservableCollection<NoteTreeNode> Children { get; } = new();

        /// <summary>展开状态（双向绑定）：第一次展开时扫盘，展开 / 收起都记进设置</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!SetProperty(ref _isExpanded, value))
                    return;
                if (value && !_loaded)
                    Reload();
                Owner.OnFolderExpandedChanged(this);
            }
        }
        private bool _isExpanded;

        /// <summary>重新扫这一层（新建、改名后，或右键「刷新」）。已展开的子文件夹会保持展开并跟着重扫。</summary>
        public void Reload()
        {
            _loaded = true;

            HashSet<string> expanded = Children.OfType<NoteFolderNode>()
                .Where(f => f.IsExpanded)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Children.Clear();
            if (!Directory.Exists(Path))
                return;

            foreach (string dir in NotepadViewModel.SafeGetDirectories(Path).Where(NotepadViewModel.HasNoteFiles))
            {
                NoteFolderNode child = new(Owner, dir, false, false);
                Children.Add(child);
                if (expanded.Contains(dir) || Owner.IsRememberedExpanded(dir))
                    child.IsExpanded = true;
            }

            foreach (string file in NotepadViewModel.SafeGetNoteFiles(Path))
            {
                Children.Add(new NoteFileNode(Owner, file));
            }
        }

        /// <summary>在这里新建笔记</summary>
        public RelayCommand Cmd_NewNote => _cmd_NewNote ??= new(() => Owner.NewNoteIn(this));
        private RelayCommand? _cmd_NewNote;

        public RelayCommand Cmd_Refresh => _cmd_Refresh ??= new(() => { Reload(); IsExpanded = true; });
        private RelayCommand? _cmd_Refresh;

        /// <summary>从树上移除（只是不再显示，不删磁盘上的东西）</summary>
        public RelayCommand Cmd_Remove => _cmd_Remove ??= new(() => Owner.RemoveFolder(this), () => CanRemove);
        private RelayCommand? _cmd_Remove;
    }

    /// <summary>文件夹没展开前的占位项（显示「…」）</summary>
    public class NoteLoadingNode : NoteTreeNode
    {
        public NoteLoadingNode(NotepadViewModel owner) : base(owner, string.Empty) { }

        public override string Header => "…";
    }

    /// <summary>文件节点：点中就在右侧打开；打开过的文件改了没存，名字后面带 *</summary>
    public class NoteFileNode : NoteTreeNode
    {
        public NoteFileNode(NotepadViewModel owner, string path) : base(owner, path)
        {
            // 同一个文件在别处已经打开过（比如两个添加的文件夹有重叠）就沿用那一份，* 也跟着显示
            Attach(owner.FindOpened(path));
        }

        /// <summary>打开后对应的文档（没打开过是 null）</summary>
        public NoteDocument? Document { get; private set; }

        public override string Header => Document?.Header ?? System.IO.Path.GetFileName(Path);

        protected override void OnPathChanged() => OnPropertyChanged(nameof(Header));

        /// <summary>挂上文档，之后它的 * 变化同步到树上</summary>
        public void Attach(NoteDocument? doc)
        {
            if (doc is null || Document == doc)
                return;
            Document = doc;
            doc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NoteDocument.Header))
                    OnPropertyChanged(nameof(Header));
            };
            OnPropertyChanged(nameof(Header));
        }

        public RelayCommand Cmd_Save => _cmd_Save ??= new(() => Owner.SaveNode(this));
        private RelayCommand? _cmd_Save;

        public RelayCommand Cmd_Rename => _cmd_Rename ??= new(() => Owner.RenameNode(this));
        private RelayCommand? _cmd_Rename;

        public RelayCommand Cmd_SaveAs => _cmd_SaveAs ??= new(() => Owner.SaveAsNode(this));
        private RelayCommand? _cmd_SaveAs;
    }

    /// <summary>
    /// [AzureSail 新增] 记事本里打开的一个文件：路径 + AvalonEdit 文档 + 读进来时的编码。
    /// 「改没改」直接问撤销栈（<see cref="UndoStack.IsOriginalFile"/>）：撤销回保存时的样子就算没改。
    /// </summary>
    public class NoteDocument : ObservableObject
    {
        static NoteDocument()
        {
            // GBK / GB18030 要靠代码页提供程序（.NET 默认只认 UTF 系列）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private NoteDocument(string filePath, string text, Encoding encoding)
        {
            _filePath = filePath;
            FileEncoding = encoding;
            Document = new TextDocument(text);
            Document.UndoStack.SizeLimit = 1000;
            Document.UndoStack.MarkAsOriginalFile();
            Document.UndoStack.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                {
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(Header));
                }
            };
        }

        /// <summary>新建的空笔记：UTF-8 不带 BOM</summary>
        public static NoteDocument CreateEmpty(string path) => new(path, string.Empty, new UTF8Encoding(false));

        /// <summary>
        /// 从磁盘读。编码按原样认出来、保存时原样写回，免得存一次把别人的文件改了编码：
        /// 有 BOM 按 BOM；没有就先按严格 UTF-8 解，解不开的当 GB18030（老的中文 txt 多是 GBK）。
        /// </summary>
        public static NoteDocument Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new(path, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new(path, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new(path, Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));

            try
            {
                return new(path, new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false));
            }
            catch (DecoderFallbackException)
            {
                Encoding gb = Encoding.GetEncoding("GB18030");
                return new(path, gb.GetString(bytes), gb);
            }
        }

        /// <summary>编辑器绑定的文档（自带撤销栈）</summary>
        public TextDocument Document { get; }

        /// <summary>读进来时的编码（含有没有 BOM），保存时照它写</summary>
        public Encoding FileEncoding { get; }

        /// <summary>对应的磁盘文件</summary>
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (SetProperty(ref _filePath, value))
                {
                    OnPropertyChanged(nameof(Header));
                    OnPropertyChanged(nameof(Highlighting));
                }
            }
        }
        private string _filePath;

        /// <summary>按扩展名挑高亮：md → MarkDown、json → Json，txt 等没有的就纯文本</summary>
        public IHighlightingDefinition? Highlighting
        {
            get
            {
                string ext = Path.GetExtension(_filePath);
                if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                    return HighlightingManager.Instance.GetDefinition("Json");
                return HighlightingManager.Instance.GetDefinitionByExtension(ext);
            }
        }

        /// <summary>有没有没保存的改动</summary>
        public bool IsDirty => !Document.UndoStack.IsOriginalFile;

        /// <summary>树上与标题条上显示的文字：文件名（带扩展名），改过的带个 *</summary>
        public string Header => IsDirty ? Path.GetFileName(_filePath) + " *" : Path.GetFileName(_filePath);

        /// <summary>写盘（照原编码），成功后把撤销栈当前位置记为「已保存」</summary>
        public void Save()
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, Document.Text, FileEncoding);
            Document.UndoStack.MarkAsOriginalFile();
        }
    }
}
