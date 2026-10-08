using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Shell;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 批量导出 Spine 源文件（.spine 工程）为 .skel + .atlas + .png。
    /// 调用 Spine 编辑器自带的命令行（Spine.com），用 -u 锁定导出版本。
    /// <para>
    /// 导出配置固定为: 二进制骨骼、非必要数据保留、按骨骼各打一个图集（packTarget = perskeleton），
    /// 这样骨骼、图集、贴图三者同名，AzKit 与游戏都按同名找图集。
    /// </para>
    /// <para>
    /// Spine 是 Java 程序，每次启动约 4 秒，所以每批 <see cref="BatchSize"/> 个工程合并成一次调用；
    /// 某一批失败时把这一批逐个重导，找出出问题的那一个，不连累同批其它工程。
    /// </para>
    /// </summary>
    public class SpineSourceExportViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>每次调用 Spine 合并导出的工程数</summary>
        private const int BatchSize = 20;

        /// <summary>设置保存位置</summary>
        private static readonly string SettingsPath = Path.Combine(App.DataDirectory, "spinesourceexport.json");

        /// <summary>导出配置: 二进制 + 按骨骼打图集（默认打包设置即 pma、Linear，与项目现有资源一致）</summary>
        private const string ExportSettingsJson = """
            {
                "class": "export-binary",
                "extension": ".skel",
                "nonessential": true,
                "cleanUp": false,
                "warnings": true,
                "packAtlas": {},
                "packSource": "attachments",
                "packTarget": "perskeleton"
            }
            """;

        private readonly MainWindowViewModel _vmMain;

        public SpineSourceExportViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            LoadSettings();
            if (string.IsNullOrWhiteSpace(_spineExePath))
                _spineExePath = FindSpineExe() ?? "";
        }

        /// <summary>Spine 命令行程序路径（Spine.com）</summary>
        public string SpineExePath { get => _spineExePath; set => SetProperty(ref _spineExePath, value); }
        private string _spineExePath = "";

        /// <summary>导出用的 Spine 版本，须与游戏运行时一致</summary>
        public string SpineVersion { get => _spineVersion; set => SetProperty(ref _spineVersion, value); }
        private string _spineVersion = "4.2.43";

        /// <summary>源目录，递归查找其中所有 .spine</summary>
        public string SourceDirectory
        {
            get => _sourceDirectory;
            set { if (SetProperty(ref _sourceDirectory, value)) OnPropertyChanged(nameof(HasBatchExport)); }
        }
        private string _sourceDirectory = "";

        /// <summary>输出目录（全场唯一：批量导出、资源库「全部导出」「用 Spine 重新导出」都用它）</summary>
        public string OutputDirectory
        {
            get => _outputDirectory;
            set { if (SetProperty(ref _outputDirectory, value)) OnPropertyChanged(nameof(HasBatchExport)); }
        }
        private string _outputDirectory = "";

        /// <summary>[AzureSail 新增] 源目录与导出文件夹都设好了，资源库的「全部导出」才可用</summary>
        public bool HasBatchExport => !string.IsNullOrWhiteSpace(SourceDirectory) && Directory.Exists(SourceDirectory)
            && !string.IsNullOrWhiteSpace(OutputDirectory);

        /// <summary>
        /// [AzureSail 新增] 资源库右键「设为全部导出文件夹」：一次设好源目录与导出文件夹并保存。
        /// 导出文件夹在源目录里面时拒绝（会把导出结果当成源再扫一遍），返回原因；成功返回 null
        /// </summary>
        public string? SetBatchExport(string sourceDirectory, string outputDirectory)
        {
            var src = Path.GetFullPath(sourceDirectory).TrimEnd('\\') + "\\";
            var dst = Path.GetFullPath(outputDirectory).TrimEnd('\\') + "\\";
            if (dst.StartsWith(src, StringComparison.OrdinalIgnoreCase))
                return "导出文件夹不能放在源目录里面（会把导出结果当成源再扫一遍）：\n" + outputDirectory;
            SourceDirectory = sourceDirectory;
            OutputDirectory = outputDirectory;
            SaveSettings();
            _logger.Info("全部导出设置：{0} -> {1}", SourceDirectory, OutputDirectory);
            return null;
        }

        /// <summary>保持源目录结构；关闭则每个工程输出到 输出目录/工程名/</summary>
        public bool KeepStructure { get => _keepStructure; set => SetProperty(ref _keepStructure, value); }
        private bool _keepStructure = true;

        /// <summary>输出目录里已有比源文件新的 .skel 时跳过</summary>
        public bool SkipExported { get => _skipExported; set => SetProperty(ref _skipExported, value); }
        private bool _skipExported = true;

        public RelayCommand Cmd_BrowseSpineExe => _cmd_BrowseSpineExe ??= new(() =>
        {
            if (DialogService.ShowOpenFileDialog(out var fileName, "选择 Spine.com", "Spine 命令行|Spine.com|所有程序|*.com;*.exe"))
                SpineExePath = fileName!;
        });
        private RelayCommand? _cmd_BrowseSpineExe;

        public RelayCommand Cmd_BrowseSourceDirectory => _cmd_BrowseSourceDirectory ??= new(() =>
        {
            if (DialogService.ShowOpenFolderDialog(out var folder))
                SourceDirectory = folder!;
        });
        private RelayCommand? _cmd_BrowseSourceDirectory;

        public RelayCommand Cmd_BrowseOutputDirectory => _cmd_BrowseOutputDirectory ??= new(() =>
        {
            if (DialogService.ShowOpenFolderDialog(out var folder))
                OutputDirectory = folder!;
        });
        private RelayCommand? _cmd_BrowseOutputDirectory;

        /// <summary>
        /// 检查参数，有问题返回原因，没问题返回 null
        /// </summary>
        public string? Validate()
        {
            if (!File.Exists(SpineExePath)) return "找不到 Spine 程序: " + SpineExePath;
            if (string.IsNullOrWhiteSpace(SpineVersion)) return "请填写 Spine 版本";
            if (!Directory.Exists(SourceDirectory)) return "源目录不存在: " + SourceDirectory;
            if (string.IsNullOrWhiteSpace(OutputDirectory)) return "请选择输出目录";
            var src = Path.GetFullPath(SourceDirectory).TrimEnd('\\') + "\\";
            var dst = Path.GetFullPath(OutputDirectory).TrimEnd('\\') + "\\";
            if (dst.StartsWith(src, StringComparison.OrdinalIgnoreCase))
                return "输出目录不能放在源目录里面（会把导出结果当成源再扫一遍）";
            return null;
        }

        /// <summary>
        /// 执行批量导出（显示进度对话框，可取消）
        /// </summary>
        public void Run()
        {
            SaveSettings();
            ProgressService.RunAsync(RunTask, "批量导出 Spine 源文件");
        }

        /// <summary>
        /// [AzureSail 新增] 资源库「全部导出」（显示进度对话框，可取消），分两步：
        /// <list type="number">
        /// <item>预览：源目录里每个 .spine 都在它自己所在的文件夹里导出一份 skel + atlas + png，不管标没标导出，
        /// 方便在资源库里直接预览（已有不比工程旧的预览则跳过）；</item>
        /// <item>正式：只有「设置导出」了的工程，把第 1 步的预览拷到 导出文件夹/&lt;骨骼名&gt;/（覆盖），
        /// 目标在 res 里时补 sRGB xml。拷而不重导：两次导出参数一样，结果相同，省一次 Spine 启动。</item>
        /// </list>
        /// </summary>
        public void RunExportAll()
        {
            SaveSettings();
            ProgressService.RunAsync(ExportAllTask, "全部导出");
        }

        private void ExportAllTask(IProgressReporter reporter, CancellationToken ct)
        {
            var projects = Directory.EnumerateFiles(SourceDirectory, "*.spine", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 1. 预览：导到 .spine 所在文件夹
            var all = projects.Select(p => (Source: p, Output: Path.GetDirectoryName(p)!)).ToList();
            var todo = SkipExported ? all.Where(it => !IsPreviewUpToDate(it.Source)).ToList() : all;
            _logger.Info("全部导出：.spine 共 {0} 个，预览已是最新 {1} 个，待导出预览 {2} 个", all.Count, all.Count - todo.Count, todo.Count);
            ExportItems(todo, all.Count - todo.Count, reporter, ct, "各 .spine 所在文件夹（预览）");
            if (ct.IsCancellationRequested) return;

            // 2. 设置了导出的：拷到 导出文件夹/<骨骼名>/
            var marked = projects.Where(Utils.ExportMarks.IsProjectMarked).ToList();
            reporter.Total = Math.Max(1, marked.Count);
            reporter.Done = 0;
            var toRes = ("\\" + Path.GetFullPath(OutputDirectory) + "\\").Contains("\\res\\", StringComparison.OrdinalIgnoreCase);
            int copied = 0;
            List<string> failed = [];
            foreach (var project in marked)
            {
                if (ct.IsCancellationRequested) break;
                reporter.ProgressText = $"[{reporter.Done}/{marked.Count}] 拷到导出文件夹 {Path.GetFileName(project)}";

                var skel = Utils.AzureSailProject.PreviewSkelOf(project);
                if (skel is null)
                {
                    failed.Add(project);
                    _logger.Error("全部导出：{0} 旁边找不到导出的 skel（预览导出失败？）", project);
                }
                else
                {
                    var target = Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(skel));
                    var error = Utils.AzureSailProject.CopySpineFiles(skel, target, overwrite: true);
                    if (error is not null)
                    {
                        failed.Add(project);
                        _logger.Error("全部导出：{0} 拷到 {1} 失败：{2}", project, target, error);
                    }
                    else
                    {
                        copied++;
                        if (toRes)
                        {
                            foreach (var png in Directory.GetFiles(target, "*.png"))
                                Utils.AzureSailProject.EnsureSrgbXml(png);
                        }
                        _logger.Info("全部导出：{0} -> {1}", Path.GetFileName(skel), target);
                    }
                }
                reporter.Done++;
            }

            if (failed.Count > 0)
                _logger.Warn("全部导出完成：设置了导出的 {0} 个，已拷到导出文件夹 {1} 个，失败 {2} 个。导出文件夹 {3}", marked.Count, copied, failed.Count, OutputDirectory);
            else
                _logger.Info("全部导出完成：设置了导出的 {0} 个，已拷到导出文件夹 {1}", marked.Count, OutputDirectory);
        }

        /// <summary>工程旁边已有不比它旧的预览 skel</summary>
        private static bool IsPreviewUpToDate(string spineProject)
        {
            var skel = Utils.AzureSailProject.PreviewSkelOf(spineProject);
            return skel is not null && File.GetLastWriteTimeUtc(skel) >= File.GetLastWriteTimeUtc(spineProject);
        }

        /// <summary>
        /// 资源库右键「用 Spine 导出」用：只检查 Spine 程序、版本与输出目录（不需要源目录）
        /// </summary>
        public string? ValidateForSelected()
        {
            if (!File.Exists(SpineExePath)) return "找不到 Spine 程序: " + SpineExePath;
            if (string.IsNullOrWhiteSpace(SpineVersion)) return "请填写 Spine 版本";
            if (string.IsNullOrWhiteSpace(OutputDirectory)) return "请选择输出目录";
            return null;
        }

        /// <summary>
        /// [AzureSail 新增] 导出指定的若干 .spine 工程（资源库右键用）。
        /// 输出到 输出目录/工程相对 <paramref name="baseDirectory"/> 的目录，已导出过的也重新导出。
        /// </summary>
        public void ExportProjects(IReadOnlyList<string> spineFiles, string baseDirectory)
        {
            SaveSettings();
            var todo = spineFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Source: p, Output: GetOutputDirectory(p, baseDirectory, true)))
                .ToList();
            _logger.Info("用 Spine {0} 导出 {1} 个工程到 {2}", SpineVersion, todo.Count, OutputDirectory);
            ProgressService.RunAsync((reporter, ct) => ExportItems(todo, 0, reporter, ct), "用 Spine 导出");
        }

        /// <summary>
        /// [AzureSail 新增] 把一个 .spine 工程导出到指定目录（「配置到特效表」导入 res 外的特效用）。
        /// 在进度对话框里跑，期间界面不卡；<paramref name="owner"/> 是进度框压在谁上面。
        /// 返回 (是否导出出了 .skel, 失败原因)
        /// </summary>
        public (bool ok, string message) ExportOne(string spineFile, string outputDirectory, System.Windows.Window? owner)
        {
            if (!File.Exists(SpineExePath)) return (false, "找不到 Spine 程序: " + SpineExePath + "\n可在「文件 → 批量导出 Spine 源文件...」里设置");
            if (string.IsNullOrWhiteSpace(SpineVersion)) return (false, "请先在「文件 → 批量导出 Spine 源文件...」里填写 Spine 版本");

            var result = ProgressService.RunAsync<string>((reporter, ct) =>
            {
                reporter.Total = 1;
                reporter.Done = 0;
                reporter.ProgressText = $"用 Spine {SpineVersion} 导出 {Path.GetFileName(spineFile)} ...";

                var exportJsonPath = Path.Combine(App.CacheDirectory, "spine-export-binary.json");
                Directory.CreateDirectory(App.CacheDirectory);
                File.WriteAllText(exportJsonPath, ExportSettingsJson, new UTF8Encoding(false));

                var (ok, output) = RunSpine([(spineFile, outputDirectory)], exportJsonPath, ct);
                reporter.Done = 1;
                if (ok && HasSkel(outputDirectory)) return "";
                return LastLines(output, 8) is { Length: > 0 } tail ? tail : "Spine 没有导出任何 .skel";
            }, "用 Spine 导出", owner);

            if (result is null) return (false, "导出被取消");
            if (result.Length > 0)
            {
                _logger.Error("导出失败: {0}\n{1}", spineFile, result);
                return (false, "Spine 导出失败：\n" + result);
            }
            _logger.Info("已导出: {0} -> {1}", spineFile, outputDirectory);
            return (true, "");
        }

        private void RunTask(IProgressReporter reporter, CancellationToken ct)
        {
            var all = Directory.EnumerateFiles(SourceDirectory, "*.spine", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Source: p, Output: GetOutputDirectory(p, SourceDirectory, KeepStructure)))
                .ToList();

            var todo = SkipExported ? all.Where(it => !IsUpToDate(it.Source, it.Output)).ToList() : all;
            int skipped = all.Count - todo.Count;
            _logger.Info("Spine 源文件共 {0} 个，跳过已导出 {1} 个，待导出 {2} 个", all.Count, skipped, todo.Count);

            ExportItems(todo, skipped, reporter, ct);
        }

        /// <summary>
        /// 分批导出并汇报结果。<paramref name="skipped"/> 只用于汇总日志
        /// </summary>
        private void ExportItems(List<(string Source, string Output)> todo, int skipped, IProgressReporter reporter, CancellationToken ct, string? outputLabel = null)
        {
            var exportJsonPath = Path.Combine(App.CacheDirectory, "spine-export-binary.json");
            Directory.CreateDirectory(App.CacheDirectory);
            File.WriteAllText(exportJsonPath, ExportSettingsJson, new UTF8Encoding(false));

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;
            reporter.Total = todo.Count;
            reporter.Done = 0;

            int success = 0;
            List<string> failed = [];
            for (int start = 0; start < todo.Count && !ct.IsCancellationRequested; start += BatchSize)
            {
                var batch = todo.Skip(start).Take(BatchSize).ToList();
                reporter.ProgressText = $"[{start}/{todo.Count}] {Path.GetFileName(batch[0].Source)} ...";

                var (ok, output) = RunSpine(batch, exportJsonPath, ct);

                // 整批失败时逐个重导，找出是哪个工程出的问题
                var perItem = ok ? batch.Select(it => (it, ok: HasSkel(it.Output), output)).ToList()
                                 : batch.Select(it => (it, ok: false, output)).ToList();
                if (!ok && batch.Count > 1 && !ct.IsCancellationRequested)
                {
                    perItem.Clear();
                    foreach (var it in batch)
                    {
                        if (ct.IsCancellationRequested) break;
                        reporter.ProgressText = $"[{start}/{todo.Count}] 逐个重试 {Path.GetFileName(it.Source)}";
                        var (itemOk, itemOutput) = RunSpine([it], exportJsonPath, ct);
                        perItem.Add((it, itemOk && HasSkel(it.Output), itemOutput));
                    }
                }

                foreach (var (it, itemOk, itemOutput) in perItem)
                {
                    if (itemOk)
                    {
                        success++;
                    }
                    else
                    {
                        failed.Add(it.Source);
                        _logger.Error("导出失败: {0}\n{1}", it.Source, LastLines(itemOutput, 8));
                    }
                }

                reporter.Done = Math.Min(start + batch.Count, todo.Count);
                reporter.ProgressText = $"[{reporter.Done}/{todo.Count}] 成功 {success}，失败 {failed.Count}";
                _vmMain.ProgressValue = reporter.Done / Math.Max(1f, todo.Count);
            }

            _vmMain.ProgressState = TaskbarItemProgressState.None;
            foreach (var it in todo.Where(it => !failed.Contains(it.Source) && HasSkel(it.Output)).Take(20))
                _logger.Info("已导出: {0}", it.Output);
            if (ct.IsCancellationRequested)
                _logger.Warn("批量导出已取消：成功 {0}，失败 {1}，跳过 {2}", success, failed.Count, skipped);
            else if (failed.Count > 0)
                _logger.Warn("批量导出完成：成功 {0}，失败 {1}，跳过 {2}。输出目录 {3}", success, failed.Count, skipped, outputLabel ?? OutputDirectory);
            else
                _logger.Info("批量导出完成：成功 {0}，跳过 {1}。输出目录 {2}", success, skipped, outputLabel ?? OutputDirectory);
        }

        /// <summary>
        /// 调一次 Spine 命令行导出一批工程。返回 (退出码是否为 0, 输出文本)
        /// </summary>
        private (bool ok, string output) RunSpine(List<(string Source, string Output)> batch, string exportJsonPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(SpineExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(SpineVersion);
            foreach (var (source, output) in batch)
            {
                Directory.CreateDirectory(output);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(source);
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(output);
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(exportJsonPath);
            }

            try
            {
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                while (!proc.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested)
                    {
                        proc.Kill(true);
                        return (false, "已取消");
                    }
                }
                var text = stdout.Result + stderr.Result;
                return (proc.ExitCode == 0, text);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                return (false, ex.Message);
            }
        }

        private string GetOutputDirectory(string spineFile, string baseDirectory, bool keepStructure)
        {
            if (!keepStructure)
                return Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(spineFile));
            var rel = Path.GetRelativePath(baseDirectory, Path.GetDirectoryName(spineFile)!);
            // 工程不在 baseDirectory 之内时（相对路径以 .. 开头），退回按工程名分目录
            if (rel.StartsWith("..") || Path.IsPathRooted(rel))
                return Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(spineFile));
            return rel == "." ? OutputDirectory : Path.Combine(OutputDirectory, rel);
        }

        /// <summary>输出目录里已有不早于源文件的 .skel</summary>
        private static bool IsUpToDate(string source, string output)
        {
            if (!Directory.Exists(output)) return false;
            var srcTime = File.GetLastWriteTimeUtc(source);
            return Directory.EnumerateFiles(output, "*.skel").Any(f => File.GetLastWriteTimeUtc(f) >= srcTime);
        }

        private static bool HasSkel(string output) =>
            Directory.Exists(output) && Directory.EnumerateFiles(output, "*.skel").Any();

        private static string LastLines(string text, int count) =>
            string.Join("\n", text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(count));

        /// <summary>常见安装位置里找 Spine.com</summary>
        private static string? FindSpineExe()
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                foreach (var dir in new[] { "Program Files", "Program Files (x86)", "" })
                {
                    var path = Path.Combine(drive.RootDirectory.FullName, dir, "Spine", "Spine.com");
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        private sealed class Settings
        {
            public string? SpineExePath { get; set; }
            public string? SpineVersion { get; set; }
            public string? SourceDirectory { get; set; }
            public string? OutputDirectory { get; set; }
            public bool KeepStructure { get; set; } = true;
            public bool SkipExported { get; set; } = true;
        }

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                if (s is null) return;
                _spineExePath = s.SpineExePath ?? "";
                _spineVersion = string.IsNullOrWhiteSpace(s.SpineVersion) ? _spineVersion : s.SpineVersion;
                _sourceDirectory = s.SourceDirectory ?? "";
                _outputDirectory = s.OutputDirectory ?? "";
                _keepStructure = s.KeepStructure;
                _skipExported = s.SkipExported;
            }
            catch (Exception ex)
            {
                _logger.Warn("读取批量导出设置失败: {0}", ex.Message);
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(App.DataDirectory);
                var s = new Settings
                {
                    SpineExePath = SpineExePath,
                    SpineVersion = SpineVersion,
                    SourceDirectory = SourceDirectory,
                    OutputDirectory = OutputDirectory,
                    KeepStructure = KeepStructure,
                    SkipExported = SkipExported,
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.Warn("保存批量导出设置失败: {0}", ex.Message);
            }
        }
    }
}
