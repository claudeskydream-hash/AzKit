using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 母骨骼导出英雄：一个英雄 = 一个图片文件夹（图片与母骨骼的图同名）。
    /// 界面只管列表（读写 AzureSail 工程里的 tools/SpineHeroExport/heroes.json）和按钮，
    /// 真正的活全交给同目录的 export_hero.py —— 命令行与界面结果完全一致。
    /// 规则见工程里的 ReadMarkDown/换装系统.md 第六节。
    /// </summary>
    public class HeroExportViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>界面设置（工程目录）保存位置</summary>
        private static readonly string SettingsPath = Path.Combine(App.DataDirectory, "heroexport.json");

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly MainWindowViewModel _vmMain;

        /// <summary>heroes.json 原文；保存时只改 heroes，其余字段（母骨骼、武器、extras、adjust……）原样保留</summary>
        private JsonObject? _config;

        public HeroExportViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            LoadSettings();
            LoadConfig();
        }

        /// <summary>一个英雄（heroes.json 里的一项）</summary>
        public sealed class HeroRow : ObservableObject
        {
            private readonly HeroExportViewModel _owner;

            public HeroRow(HeroExportViewModel owner) { _owner = owner; }

            /// <summary>原始 JSON，保存时在它上面改，界面没管到的字段（extras、adjust）不丢</summary>
            public JsonObject Raw { get; init; } = [];

            /// <summary>英雄号，同时是 res/spine/Hero 下的目录名与文件名</summary>
            public string Id { get => _id; set { if (SetProperty(ref _id, value)) OnPropertyChanged(nameof(Status)); } }
            private string _id = "";

            /// <summary>英雄表里对应行的 id（heroes.json 的 tableId，第一次「导出到表格」时由脚本记下），没导过为空</summary>
            public string TableId => Raw["tableId"]?.ToString() ?? "";

            /// <summary>名字，导出到表格时写进 heroName</summary>
            public string Name { get => _name; set => SetProperty(ref _name, value); }
            private string _name = "";

            /// <summary>图片文件夹（或 .spine 工程），相对 AzureSail 工程根目录</summary>
            public string Source { get => _source; set { if (SetProperty(ref _source, value)) OnPropertyChanged(nameof(Status)); } }
            private string _source = "";

            public bool IsSpineProject => Source.EndsWith(".spine", StringComparison.OrdinalIgnoreCase);

            /// <summary>图在不在、导没导出过、导出后图有没有改过</summary>
            public string Status
            {
                get
                {
                    var source = _owner.ResolvePath(Source);
                    DateTime sourceTime;
                    if (IsSpineProject)
                    {
                        if (!File.Exists(source)) return "找不到工程";
                        sourceTime = File.GetLastWriteTime(source);
                    }
                    else
                    {
                        if (!Directory.Exists(source)) return "找不到文件夹";
                        // 文件夹里的图和旁边的 .spine 工程，哪个最新算哪个
                        sourceTime = Directory.EnumerateFiles(source, "*.png", SearchOption.AllDirectories)
                            .Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();
                        if (File.Exists(SpineProjectPath) && File.GetLastWriteTime(SpineProjectPath) > sourceTime)
                            sourceTime = File.GetLastWriteTime(SpineProjectPath);
                    }
                    var skel = _owner.ExportedSkelPath(Id);
                    if (skel is null || !File.Exists(skel)) return "未导出";
                    return File.GetLastWriteTime(skel) >= sourceTime ? "已导出" : "图有改动";
                }
            }

            public void RefreshStatus() => OnPropertyChanged(nameof(Status));

            /// <summary>
            /// 英雄的 Spine 工程，第一次导出时生成：现行摆法（英雄文件夹里有 images/）放在英雄文件夹里面
            /// （子骨骼/李老道/李老道.spine）；旧摆法和图片文件夹同级同名（Export/LijiuDao/ → Export/LijiuDao.spine）。
            /// 规则与 export_hero.py 的 spine_project_path 一致，改一处要改两处。
            /// </summary>
            public string SpineProjectPath
            {
                get
                {
                    var source = _owner.ResolvePath(Source);
                    if (IsSpineProject) return source;
                    var folder = source.TrimEnd('\\', '/');
                    return Directory.Exists(Path.Combine(folder, "images"))
                        ? Path.Combine(folder, Path.GetFileName(folder) + ".spine")
                        : folder + ".spine";
                }
            }
        }

        public ObservableCollection<HeroRow> Heroes { get; } = [];

        /// <summary>母骨骼 .spine（heroes.json 的 master，相对工程根目录）</summary>
        public string MasterPath => _config?["master"]?.GetValue<string>() ?? "";

        /// <summary>英雄目录：里面每个子文件夹是一个英雄（heroes.json 的 heroRoot）</summary>
        public string HeroRoot => _config?["heroRoot"]?.GetValue<string>() ?? "";

        public RelayCommand Cmd_ChangeMaster => _cmd_ChangeMaster ??= new(() =>
        {
            if (!EnsureProjectRoot() || _config is null) return;
            if (!DialogService.ShowOpenFileDialog(out var fileName, "选择母骨骼", "Spine 工程|*.spine")) return;
            _config["master"] = ToConfigPath(fileName!);
            if (!SaveConfigOrWarn()) return;
            OnPropertyChanged(nameof(MasterPath));
            AppendLog("母骨骼已换成 " + MasterPath + "。点「全部导出」让所有英雄用上新母骨骼（各英雄的 .spine 会同步，原文件备份进 _backup）");
        });
        private RelayCommand? _cmd_ChangeMaster;

        public RelayCommand Cmd_ChangeHeroRoot => _cmd_ChangeHeroRoot ??= new(() =>
        {
            if (!EnsureProjectRoot() || _config is null) return;
            if (!DialogService.ShowOpenFolderDialog(out var folder)) return;
            // 选成了某个英雄自己的文件夹（里面有 images/）或 images 本身：英雄目录是它们的上一层，自动纠正。
            // 不纠正的话 images 会被当成一个英雄扫进列表，导出出一个叫 images 的英雄、生成 images.spine（实测踩过）
            var root = folder!.TrimEnd('\\', '/');
            if (string.Equals(Path.GetFileName(root), "images", StringComparison.OrdinalIgnoreCase))
                root = Path.GetDirectoryName(root) ?? root;
            if (Directory.Exists(Path.Combine(root, "images")))
            {
                var parent = Path.GetDirectoryName(root) ?? root;
                AppendLog("选的「" + Path.GetFileName(root) + "」是一个英雄的文件夹（里面有 images），英雄目录改用它的上一层：" + parent);
                root = parent;
            }
            _config["heroRoot"] = ToConfigPath(root);
            if (!SaveConfigOrWarn()) return;
            OnPropertyChanged(nameof(HeroRoot));
            ScanHeroRoot();
        });
        private RelayCommand? _cmd_ChangeHeroRoot;

        public HeroRow? SelectedHero { get => _selectedHero; set => SetProperty(ref _selectedHero, value); }
        private HeroRow? _selectedHero;

        /// <summary>脚本输出</summary>
        public string Log { get => _log; set => SetProperty(ref _log, value); }
        private string _log = "";

        /// <summary>没有脚本在跑（按钮可点）</summary>
        public bool IsIdle { get => _isIdle; set => SetProperty(ref _isIdle, value); }
        private bool _isIdle = true;

        /// <summary>AzureSail 工程根目录（含 tools/SpineHeroExport），存在 data/heroexport.json</summary>
        private string _projectRoot = "";

        /// <summary>记下的工程根目录（可能为空）。「配置到特效表」在 res 外的资源上找不到工程时用它</summary>
        public string ProjectRoot => _projectRoot;

        private string ToolDirectory => Path.Combine(_projectRoot, "tools", "SpineHeroExport");
        private string ConfigPath => Path.Combine(ToolDirectory, "heroes.json");
        private string ScriptPath => Path.Combine(ToolDirectory, "export_hero.py");

        public string ResolvePath(string path) =>
            string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(_projectRoot) ? "" : Path.GetFullPath(Path.Combine(_projectRoot, path));

        public string? ExportedSkelPath(string heroId) =>
            string.IsNullOrWhiteSpace(heroId) || string.IsNullOrWhiteSpace(_projectRoot) ? null
            : Path.Combine(_projectRoot, "res", "spine", "Hero", heroId, heroId + ".skel");

        /// <summary>存进 heroes.json 的路径：能写成相对工程根目录就写相对</summary>
        private string ToConfigPath(string fullPath)
        {
            var rel = Path.GetRelativePath(_projectRoot, fullPath);
            return (Path.IsPathRooted(rel) ? fullPath : rel).Replace('\\', '/');
        }

        /// <summary>工程目录不对就让用户选一次（只有第一次用、或工程挪了位置时会问）</summary>
        private bool EnsureProjectRoot()
        {
            if (File.Exists(ScriptPath)) return true;
            MessagePopupService.Info("请选择 AzureSail 工程根目录（里面有 tools\\SpineHeroExport）");
            if (!DialogService.ShowOpenFolderDialog(out var folder)) return false;
            _projectRoot = folder!;
            if (!File.Exists(ScriptPath))
            {
                MessagePopupService.Error("这个目录里没有 tools\\SpineHeroExport\\export_hero.py");
                return false;
            }
            SaveSettings();
            LoadConfig();
            return true;
        }

        // ---------------------------------------------------------------- 命令

        /// <summary>新增英雄：选它的图片文件夹，自动取下一个空着的英雄号</summary>
        public RelayCommand Cmd_AddHero => _cmd_AddHero ??= new(() =>
        {
            if (!EnsureProjectRoot()) return;
            if (!DialogService.ShowOpenFolderDialog(out var folder))
                return;
            var row = new HeroRow(this)
            {
                Id = NextHeroId(),
                Name = Path.GetFileName(folder!.TrimEnd('\\', '/')),
                Source = ToConfigPath(folder!),
            };
            Heroes.Add(row);
            SelectedHero = row;
            SaveConfigOrWarn();
        });
        private RelayCommand? _cmd_AddHero;

        public RelayCommand Cmd_RemoveHero => _cmd_RemoveHero ??= new(() =>
        {
            if (SelectedHero is null) return;
            if (!MessagePopupService.OKCancel($"从列表里删掉 {SelectedHero.Id}（{SelectedHero.Name}）？\n不会删除已导出的文件，也不会改英雄表。"))
                return;
            Heroes.Remove(SelectedHero);
            SelectedHero = null;
            SaveConfigOrWarn();
        });
        private RelayCommand? _cmd_RemoveHero;

        /// <summary>每次打开窗口都重读 heroes.json：窗口外（命令行、手改）改过的配置不能被窗口里的旧数据覆盖</summary>
        public void Reload()
        {
            if (!string.IsNullOrWhiteSpace(_projectRoot)) LoadConfig();
        }

        /// <summary>用 Spine 编辑器打开英雄文件夹里的工程（还没导出过就没有）</summary>
        public RelayCommand Cmd_OpenSpine => _cmd_OpenSpine ??= new(() =>
        {
            if (SelectedHero is null) return;
            var path = SelectedHero.SpineProjectPath;
            if (!File.Exists(path))
            {
                MessagePopupService.Info("还没有 Spine 工程：先「导出看效果」一次，会在英雄文件夹里生成 " + Path.GetFileName(path));
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
        private RelayCommand? _cmd_OpenSpine;

        public RelayCommand Cmd_OpenFolder => _cmd_OpenFolder ??= new(() =>
        {
            if (SelectedHero is null) return;
            var path = ResolvePath(SelectedHero.Source);
            var folder = SelectedHero.IsSpineProject ? Path.GetDirectoryName(path) : path;
            if (Directory.Exists(folder))
                Process.Start(new ProcessStartInfo("explorer.exe", folder!) { UseShellExecute = true });
        });
        private RelayCommand? _cmd_OpenFolder;

        /// <summary>只导出到 res，看效果用</summary>
        public RelayCommand Cmd_Export => _cmd_Export ??= new(() =>
        {
            if (SelectedHero is null) { MessagePopupService.Info("先在列表里选一个英雄"); return; }
            _ = RunScriptAsync([SelectedHero.Id], SelectedHero, false);
        });
        private RelayCommand? _cmd_Export;

        /// <summary>只把母工程的骨骼、动画同步进英雄 .spine，不导出游戏文件</summary>
        public RelayCommand Cmd_SyncOnly => _cmd_SyncOnly ??= new(() =>
        {
            if (SelectedHero is null) { MessagePopupService.Info("先在列表里选一个英雄"); return; }
            if (!File.Exists(SelectedHero.SpineProjectPath))
            {
                MessagePopupService.Info("还没有 Spine 工程：先「导出看效果」一次，会在英雄文件夹里生成");
                return;
            }
            // 不传 row：只同步工程，没有新导出的 skel 可加载
            _ = RunScriptAsync([SelectedHero.Id, "--sync-only"], null, false);
        });
        private RelayCommand? _cmd_SyncOnly;

        /// <summary>
        /// 导出到表格：导出 + 英雄表有这个英雄的行就覆盖（只改 spineRes、heroName）、没有就新建 + 资源声明 + 导表编译，做完就能进游戏。
        /// 按行 id（heroes.json 的 tableId）认行，改英雄号也还是同一行。
        /// </summary>
        public RelayCommand Cmd_ExportToTable => _cmd_ExportToTable ??= new(() =>
        {
            if (SelectedHero is null) { MessagePopupService.Info("先在列表里选一个英雄"); return; }
            var target = string.IsNullOrEmpty(SelectedHero.TableId)
                ? "英雄表里找指向这套 Spine 的行覆盖，找不到就新建一行"
                : "覆盖英雄表 id " + SelectedHero.TableId + " 这一行";
            if (!MessagePopupService.OKCancel("将导出到表格（Excels/Hero.xlsx）：" + target + "，只改 Spine 路径和名字，技能、属性等不动；"
                    + "然后导表并编译两端，约一两分钟。\n请先关掉 Excel 里的 Hero.xlsx。继续？"))
                return;
            _ = RunScriptAsync([SelectedHero.Id, "--to-table"], SelectedHero, true);
        });
        private RelayCommand? _cmd_ExportToTable;

        /// <summary>
        /// 描绘网格：英雄 .spine 里换过图的部件（母骨骼里是网格的）按新图重新描绘成贴合轮廓的网格，
        /// 权重、形变动画从母网格按位置换算；写回 .spine（原文件备份进 _backup）并导出。图和母骨骼原图一样的不动。
        /// </summary>
        public RelayCommand Cmd_Trace => _cmd_Trace ??= new(() =>
        {
            if (SelectedHero is null) { MessagePopupService.Info("先在列表里选一个英雄"); return; }
            if (!File.Exists(SelectedHero.SpineProjectPath))
            {
                MessagePopupService.Info("还没有 Spine 工程：先「导出看效果」一次（第一次生成时已经自动描绘过）");
                return;
            }
            if (!MessagePopupService.OKCancel("换过图的网格部件会按新图重新描绘，在 Spine 里手调过的这些网格会被替换（原文件带时间戳备份进旁边的 _backup 文件夹）。继续？"))
                return;
            _ = RunScriptAsync([SelectedHero.Id, "--trace"], SelectedHero, false);
        });
        private RelayCommand? _cmd_Trace;

        /// <summary>母骨骼改了动画后，全部英雄重新导出</summary>
        public RelayCommand Cmd_ExportAll => _cmd_ExportAll ??= new(() =>
        {
            if (Heroes.Count == 0) return;
            _ = RunScriptAsync(["--all"], null, false);
        });
        private RelayCommand? _cmd_ExportAll;

        // ---------------------------------------------------------------- 跑脚本

        private async Task RunScriptAsync(List<string> args, HeroRow? row, bool toTable)
        {
            if (!EnsureProjectRoot()) return;
            // 脚本读的是 heroes.json，先把界面上的改动（改名、改英雄号）存进去
            if (!SaveConfigOrWarn()) return;

            IsIdle = false;
            Log = "";
            AppendLog("> python export_hero.py " + string.Join(" ", args));

            var psi = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _projectRoot,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            // Spine 命令行沿用「批量导出 Spine 源文件」里设的路径
            var spineExe = _vmMain.SpineSourceExportViewModel.SpineExePath;
            if (File.Exists(spineExe))
                psi.Environment["SPINE_CLI"] = spineExe;
            psi.ArgumentList.Add(ScriptPath);
            foreach (var a in args) psi.ArgumentList.Add(a);

            int exitCode = -1;
            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                AppendLog("无法运行 python（" + ex.Message + "）。需要装 Python 3，并能在命令行里直接用 python");
            }

            AppendLog(exitCode == 0 ? "== 完成" : "== 失败（退出码 " + exitCode + "），看上面的 [错误]");
            _logger.Info("母骨骼导出英雄 {0}：{1}", string.Join(" ", args), exitCode == 0 ? "完成" : "失败");
            // 脚本会往 heroes.json 写东西（导出到表格记下的 tableId）：重读一遍，免得下次保存用旧数据把它冲掉
            var selectedId = SelectedHero?.Id;
            LoadConfig();
            SelectedHero = Heroes.FirstOrDefault(h => h.Id == selectedId);
            IsIdle = true;

            if (exitCode != 0 || row is null) return;
            var skel = ExportedSkelPath(row.Id);
            var tableId = Heroes.FirstOrDefault(h => h.Id == row.Id)?.TableId;
            var msg = toTable
                ? $"{row.Id} 已导出到表格（英雄表 id {tableId}），导表与编译都完成了，进游戏用 GM 命令建这个英雄就能看到。\n\n现在加载到画面里看看？"
                : $"{row.Id} 已导出，加载到画面里看看？";
            if (skel is not null && File.Exists(skel) && MessagePopupService.OKCancel(msg))
                _vmMain.SpineObjectListViewModel.AddSpineObjectFromFileList([skel]);
        }

        private void AppendLog(string line)
        {
            void Append() => Log += line + Environment.NewLine;
            if (Application.Current.Dispatcher.CheckAccess()) Append();
            else Application.Current.Dispatcher.Invoke(Append);
        }

        // ---------------------------------------------------------------- heroes.json

        private void LoadConfig()
        {
            Heroes.Clear();
            _config = null;
            if (!File.Exists(ConfigPath)) return;
            try
            {
                _config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
                if (_config["heroes"] is JsonObject heroes)
                {
                    foreach (var (id, node) in heroes)
                    {
                        if (node is not JsonObject h) continue;
                        Heroes.Add(new HeroRow(this)
                        {
                            Raw = h,
                            Id = id,
                            Name = h["name"]?.GetValue<string>() ?? "",
                            Source = h["images"]?.GetValue<string>() ?? h["spine"]?.GetValue<string>() ?? "",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("读取 heroes.json 失败: {0}", ex.Message);
                AppendLog("读取 heroes.json 失败：" + ex.Message);
                return;
            }
            ScanHeroRoot();
            OnPropertyChanged(nameof(MasterPath));
            OnPropertyChanged(nameof(HeroRoot));
        }

        /// <summary>
        /// heroRoot（美术/角色/子骨骼）下每个子文件夹就是一个英雄：列表里还没有的自动加上、取下一个空着的英雄号。
        /// 美术把文件夹放进去，打开窗口就在列表里了。图放在 images/&lt;阶段&gt;/ 下（子骨骼/李老道/images/LianQiQi/），
        /// 旧的平铺摆法（图直接在英雄文件夹里）也认。
        /// </summary>
        private void ScanHeroRoot()
        {
            var rootRel = _config?["heroRoot"]?.GetValue<string>();
            var root = rootRel is null ? "" : ResolvePath(rootRel);
            if (!Directory.Exists(root)) return;

            var known = new HashSet<string>(Heroes.Select(h => ResolvePath(h.Source).TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var added = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (known.Contains(Path.GetFullPath(dir).TrimEnd('\\', '/'))) continue;
                if (Path.GetFileName(dir).StartsWith('_')) continue;  // _backup 之类不是英雄
                if (string.Equals(Path.GetFileName(dir), "images", StringComparison.OrdinalIgnoreCase)) continue;  // 英雄的图片文件夹，不是英雄
                var images = Path.Combine(dir, "images");
                var hasImages = Directory.Exists(images)
                    ? Directory.EnumerateFiles(images, "*.png", SearchOption.AllDirectories).Any()
                    : Directory.EnumerateFiles(dir, "*.png").Any();
                if (!hasImages) continue;
                var row = new HeroRow(this) { Id = NextHeroId(), Name = Path.GetFileName(dir), Source = ToConfigPath(dir) };
                Heroes.Add(row);
                added.Add(row.Id + "（" + row.Name + "）");
            }
            if (added.Count > 0 && SaveConfig(out _))
                AppendLog("英雄目录里有新英雄，已加进列表：" + string.Join("、", added));
        }

        private bool SaveConfigOrWarn()
        {
            if (SaveConfig(out var error)) return true;
            MessagePopupService.Error(error!);
            return false;
        }

        private bool SaveConfig(out string? error)
        {
            error = null;
            if (_config is null)
            {
                error = "没有读到 heroes.json：" + ConfigPath;
                return false;
            }
            var ids = Heroes.Select(h => h.Id.Trim()).ToList();
            if (ids.Any(string.IsNullOrWhiteSpace))
            {
                error = "有英雄没填英雄号";
                return false;
            }
            var dup = ids.GroupBy(i => i).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
            {
                error = "英雄号重复：" + dup.Key;
                return false;
            }

            var heroes = new JsonObject();
            foreach (var h in Heroes)
            {
                var raw = (JsonObject)h.Raw.DeepClone();
                raw["name"] = h.Name;
                raw.Remove("images");
                raw.Remove("spine");
                raw[h.IsSpineProject ? "spine" : "images"] = h.Source;
                heroes[h.Id.Trim()] = raw;
            }
            _config["heroes"] = heroes;

            try
            {
                File.WriteAllText(ConfigPath, _config.ToJsonString(WriteOptions) + "\n", new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "保存 heroes.json 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>下一个没用过的 heroNN（看列表和 res/spine/Hero 两处）</summary>
        private string NextHeroId()
        {
            var used = new HashSet<string>(Heroes.Select(h => h.Id), StringComparer.OrdinalIgnoreCase);
            var heroDir = Path.Combine(_projectRoot, "res", "spine", "Hero");
            if (Directory.Exists(heroDir))
                foreach (var d in Directory.EnumerateDirectories(heroDir)) used.Add(Path.GetFileName(d));
            for (int i = 1; ; i++)
            {
                var id = $"hero{i:00}";
                if (!used.Contains(id)) return id;
            }
        }

        // ---------------------------------------------------------------- 界面设置

        private sealed class Settings
        {
            public string? ProjectRoot { get; set; }
        }

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                _projectRoot = s?.ProjectRoot ?? "";
            }
            catch (Exception ex)
            {
                _logger.Warn("读取母骨骼导出设置失败: {0}", ex.Message);
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(App.DataDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { ProjectRoot = _projectRoot }, WriteOptions));
            }
            catch (Exception ex)
            {
                _logger.Warn("保存母骨骼导出设置失败: {0}", ex.Message);
            }
        }
    }
}
