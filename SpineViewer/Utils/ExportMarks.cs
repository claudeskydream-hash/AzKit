using NLog;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 资源项的「设置导出」标记：资源库「全部导出」时，只有标了的才往导出文件夹生成一份
    /// （没标的只在 .spine 旁边导出预览）。
    /// <para>
    /// 标记记在 <b>.spine 工程</b>上，不记在 skel 上：skel 是导出物，全部导出会重新生成它；
    /// 同一个工程的预览 skel 与标记始终对得上。存在 data/exportmarks.json（完整路径列表）。
    /// </para>
    /// </summary>
    public static class ExportMarks
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static readonly string FilePath = Path.Combine(App.DataDirectory, "exportmarks.json");

        /// <summary>已标记的 .spine 工程（规范化后的完整路径，不区分大小写）</summary>
        private static readonly HashSet<string> _marked = Load();

        /// <summary>skel → 它的 .spine 工程（找不到为 null）。列表显示时每项都要查，缓存住免得反复扫目录</summary>
        private static readonly Dictionary<string, string?> _projectOfSkel = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>资源项（skel）对应的 .spine 工程：同目录同名优先，否则目录里只有一个时用它</summary>
        public static string? ProjectOf(string skelPath)
        {
            var key = Normalize(skelPath);
            if (!_projectOfSkel.TryGetValue(key, out var project))
            {
                project = Directory.Exists(Path.GetDirectoryName(key)) ? AzureSailProject.FindSpineProject(key) : null;
                _projectOfSkel[key] = project is null ? null : Normalize(project);
            }
            return _projectOfSkel[key];
        }

        /// <summary>这个资源项（skel）是否已设置导出</summary>
        public static bool IsItemMarked(string skelPath) => ProjectOf(skelPath) is { } p && _marked.Contains(p);

        /// <summary>这个 .spine 工程是否已设置导出</summary>
        public static bool IsProjectMarked(string spineProject) => _marked.Contains(Normalize(spineProject));

        /// <summary>设置 / 取消一批资源项的导出标记。返回 (改了几个, 没有 .spine 工程而跳过的几个)</summary>
        public static (int changed, int noProject) SetItems(IEnumerable<string> skelPaths, bool marked)
        {
            int changed = 0, noProject = 0;
            foreach (var skel in skelPaths)
            {
                if (ProjectOf(skel) is not { } project)
                {
                    noProject++;
                    continue;
                }
                if (marked ? _marked.Add(project) : _marked.Remove(project)) changed++;
            }
            if (changed > 0) Save();
            return (changed, noProject);
        }

        /// <summary>某目录下（含子目录）已标记的工程数</summary>
        public static int CountUnder(string directory)
        {
            var dir = Normalize(directory) + "\\";
            return _marked.Count(p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        private static HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return set;
            try
            {
                foreach (var p in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [])
                    set.Add(Normalize(p));
            }
            catch (Exception ex)
            {
                _logger.Warn("读取导出标记失败: {0}", ex.Message);
            }
            return set;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(App.DataDirectory);
                var list = _marked.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _logger.Warn("保存导出标记失败: {0}", ex.Message);
            }
        }
    }
}
