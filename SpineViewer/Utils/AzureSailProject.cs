using NLog;
using SpineViewer.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 「配置到特效表」「配置到怪物」共用的工程操作：找工程根、调改表脚本、
    /// 把 res 外的 Spine 导入 res、补 sRGB xml。
    /// <para>
    /// 改表一律调项目自带的 skills/excel-table/scripts/xlsx_table.py（项目规定，保留表格样式、写前自动备份）。
    /// </para>
    /// </summary>
    public static class AzureSailProject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>sRGB 参数 xml 内容，与编辑器插件 SrgbXml 写出的逐字节一致（UTF-8 无 BOM、LF）</summary>
        private const string SrgbXmlContent = "<texture>\n    <srgb enable=\"true\" />\n</texture>\n";

        public static string ScriptPath(string projectRoot) =>
            Path.Combine(projectRoot, "skills", "excel-table", "scripts", "xlsx_table.py");

        /// <summary>带 Excels/&lt;表&gt;.xlsx 与改表脚本的目录才算工程根</summary>
        public static bool IsProjectRoot(string dir, string tableName) =>
            File.Exists(Path.Combine(dir, "Excels", tableName + ".xlsx")) && File.Exists(ScriptPath(dir));

        /// <summary>从文件往上找工程根；找不到时用 <paramref name="fallback"/>（一般是母骨骼导出记下的工程根）</summary>
        public static string? FindProjectRoot(string path, string tableName, string? fallback)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            for (; dir is not null; dir = dir.Parent)
            {
                if (IsProjectRoot(dir.FullName, tableName))
                    return dir.FullName;
            }
            if (!string.IsNullOrWhiteSpace(fallback) && IsProjectRoot(fallback, tableName))
                return fallback;
            return null;
        }

        /// <summary>完整路径 → 相对 res 的地址（不在 res 里返回 null）</summary>
        public static string? ToResPath(string projectRoot, string fullPath)
        {
            var resDir = Path.Combine(projectRoot, "res") + "\\";
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(resDir, StringComparison.OrdinalIgnoreCase) ? full[resDir.Length..].Replace('\\', '/') : null;
        }

        /// <summary>选中 Spine 的读取结果：动画名、要重新导出时用的 .spine 工程（null = 不用导出）、给用户看的提示</summary>
        public sealed record SpineSourceInfo(List<string> Animations, string? SpineProject, string? Warning);

        /// <summary>
        /// 读选中的 skel：取动画名并检查版本。res 外且不是 4.2 的要找同目录 .spine 工程准备重新导出，找不到就失败；
        /// res 里的只给提示（不自动改 res 里的文件）。失败返回 null 并给出原因
        /// </summary>
        public static SpineSourceInfo? InspectSource(string fullPath, bool inRes, string exportVersion, out string? error)
        {
            error = null;
            try
            {
                using var sp = new Spine.SpineObject(fullPath);
                var animations = sp.Data.Animations.Select(a => a.Name).ToList();
                if (sp.Version == Spine.SpineVersion.V42)
                    return new(animations, null, null);
                if (inRes)
                    return new(animations, null, $"注意：这个文件是 Spine {sp.Version} 格式，游戏运行时是 4.2，放进游戏会加载失败。先用「用 Spine 重新导出」导成 4.2.43。");

                var project = FindSpineProject(fullPath);
                if (project is null)
                {
                    error = $"这个文件是 Spine {sp.Version} 格式，游戏运行时只认 4.2，而它旁边没有 .spine 工程可以重新导出：\n" + fullPath;
                    return null;
                }
                return new(animations, project, $"这个文件是 Spine {sp.Version} 格式，写表时会用同目录的 {Path.GetFileName(project)} 重新导出成 {exportVersion}。");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                if (!inRes)
                {
                    error = "读取 Spine 失败（" + ex.Message + "）：\n" + fullPath;
                    return null;
                }
                return new([], null, "读取 Spine 失败（" + ex.Message + "），动画名请手填");
            }
        }

        /// <summary>读整张表（每行字段名 → 值）。失败返回 null 并给出原因</summary>
        public static List<Dictionary<string, string?>>? ReadTable(string projectRoot, string tableName, out string? error)
        {
            error = null;
            var (ok, output) = RunTableScript(projectRoot, ["show", tableName, "--json"]);
            if (!ok)
            {
                error = "读取 " + tableName + " 表失败：\n" + output;
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(output.Trim()) ?? [];
            }
            catch (Exception ex)
            {
                error = tableName + " 表数据解析失败：" + ex.Message + "\n" + output;
                return null;
            }
        }

        /// <summary>调用改表脚本，返回 (退出码是否为 0, 标准输出 + 错误输出)</summary>
        public static (bool ok, string output) RunTableScript(string projectRoot, List<string> args)
        {
            var psi = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = projectRoot,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.ArgumentList.Add(ScriptPath(projectRoot));
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                return (proc.ExitCode == 0, proc.ExitCode == 0 ? stdout.Result : stdout.Result + stderr.Result);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex.ToString());
                return (false, "无法运行 python（" + ex.Message + "）。需要装 Python 与 openpyxl，并能在命令行里直接用 python");
            }
        }

        /// <summary>
        /// 把 res 外的 Spine 放进 <paramref name="targetDir"/>：<paramref name="spineProject"/> 为 null 时直接拷
        /// skel + 同名 atlas + 贴图页（已是 4.2）；否则用这个 .spine 工程经 Spine 命令行重新导出。
        /// 目标目录已有文件时拒绝（不覆盖别人的资源）。成功返回导入后的 skel 完整路径
        /// </summary>
        public static (string? skel, string error) ImportSpine(string sourceSkel, string? spineProject, string targetDir,
            SpineSourceExportViewModel exporter, System.Windows.Window? owner)
        {
            var name = Path.GetFileName(targetDir);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                return (null, "导入目录名不合法：" + name);
            if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
                return (null, "目录已存在且不为空，换个名字：\n" + targetDir);

            if (spineProject is null)
            {
                var copyError = CopySpineFiles(sourceSkel, targetDir);
                if (copyError is not null) return (null, copyError);
            }
            else
            {
                var (ok, message) = exporter.ExportOne(spineProject, targetDir, owner);
                if (!ok) return (null, message);
            }

            var skel = PickSkel(targetDir, Path.GetFileNameWithoutExtension(sourceSkel));
            if (skel is null)
                return (null, "导入后目录里没有 .skel：\n" + targetDir);
            _logger.Info("Spine 已导入: {0} -> {1}", sourceSkel, skel);
            return (skel, "");
        }

        /// <summary>按 skel 名转成大驼峰作默认目录名（effect_skill1_hit → EffectSkill1Hit）；素材库按数字目录分的，再接上目录号避免重名</summary>
        public static string DefaultImportName(string skelPath)
        {
            var baseName = Path.GetFileNameWithoutExtension(skelPath);
            var parts = baseName.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
            var name = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            var folder = Path.GetFileName(Path.GetDirectoryName(skelPath)) ?? "";
            if (folder.Length > 0 && folder.All(char.IsDigit)) name += folder;
            return name.Length > 0 ? name : "NewSpine";
        }

        /// <summary>skel 同目录的 .spine 工程：优先同名，否则只有一个时用它</summary>
        public static string? FindSpineProject(string skelPath)
        {
            var dir = Path.GetDirectoryName(skelPath)!;
            var same = Path.Combine(dir, Path.GetFileNameWithoutExtension(skelPath) + ".spine");
            if (File.Exists(same)) return same;
            var all = Directory.GetFiles(dir, "*.spine");
            return all.Length == 1 ? all[0] : null;
        }

        /// <summary>图集里登记的贴图页文件名（不带冒号、以图片扩展名结尾的行）</summary>
        public static List<string> AtlasPages(string atlasPath) =>
            File.ReadAllLines(atlasPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.Contains(':')
                    && (l.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();

        /// <summary>贴图旁没有同名 sRGB xml 就补一个（已有的不动）。补了返回 true</summary>
        public static bool EnsureSrgbXml(string png)
        {
            var xml = Path.ChangeExtension(png, ".xml");
            if (File.Exists(xml)) return false;
            File.WriteAllText(xml, SrgbXmlContent, new UTF8Encoding(false));
            return true;
        }

        /// <summary>导出目录里取 skel：优先和来源同名，否则取第一个</summary>
        private static string? PickSkel(string dir, string preferredName)
        {
            if (!Directory.Exists(dir)) return null;
            var all = Directory.GetFiles(dir, "*.skel");
            return all.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(preferredName, StringComparison.OrdinalIgnoreCase))
                ?? all.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>
        /// .spine 工程在它旁边导出的预览 skel：同名优先；否则目录里只有这一个工程时取最新的 skel；都没有返回 null。
        /// （按骨骼导出时 skel 用骨骼名命名，可能和工程名不同）
        /// </summary>
        public static string? PreviewSkelOf(string spineProject)
        {
            var dir = Path.GetDirectoryName(spineProject)!;
            var same = Path.Combine(dir, Path.GetFileNameWithoutExtension(spineProject) + ".skel");
            if (File.Exists(same)) return same;
            if (Directory.GetFiles(dir, "*.spine").Length != 1) return null;
            return Directory.GetFiles(dir, "*.skel").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        /// <summary>拷 skel + 同名 atlas + atlas 里的贴图页到目标目录。<paramref name="overwrite"/> 为 false 时目标已有同名文件会抛异常。失败返回原因</summary>
        public static string? CopySpineFiles(string skelPath, string target, bool overwrite = false)
        {
            var dir = Path.GetDirectoryName(skelPath)!;
            var atlas = Path.Combine(dir, Path.GetFileNameWithoutExtension(skelPath) + ".atlas");
            if (!File.Exists(atlas))
                return "找不到同名图集：\n" + atlas;

            var pages = AtlasPages(atlas);
            var missing = pages.Where(p => !File.Exists(Path.Combine(dir, p))).ToList();
            if (missing.Count > 0)
                return "图集登记的贴图找不到：\n" + string.Join("\n", missing);

            Directory.CreateDirectory(target);
            foreach (var f in pages.Select(p => Path.Combine(dir, p)).Append(skelPath).Append(atlas))
            {
                var dst = Path.Combine(target, Path.GetRelativePath(dir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, overwrite);
            }
            return null;
        }

        public static string LastLines(string text, int count) =>
            string.Join("\n", text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(count));
    }
}
