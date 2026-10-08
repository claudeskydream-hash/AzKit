using CommunityToolkit.Mvvm.ComponentModel;
using NLog;
using SpineViewer.Utils;
using System.IO;
using System.Text;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 把资源库里选中的 Spine 配置成 AzureSail 英雄表（Excels/Hero.xlsx）里的怪物。
    /// <para>
    /// 两种写法（同「配置到特效表」）：在列表里选中一个怪物行 = 覆盖它（spineRes 换成这套 Spine，
    /// 并改名字、类型、动作名，属性、缩放等策划填的列不动）；或新增一行（id = 现有最大怪物 id + 1，其余列照抄同类型最后一行）。
    /// 打开时若已有 spineRes 指向这套 Spine 的怪物行，默认选中它，否则默认新增。
    /// </para>
    /// <para>
    /// 怪物动画名统一固定为 <see cref="StandardAnims"/>（idle / run / attack / spell / hit / die），不让选：
    /// 游戏里攻击写死播 attack（WeaponAnimation.DefaultAttackAnim）、施法缺省播 spell（SkillRules.DefaultCastAnim），
    /// 其余四个按表里的列播，表里一律写标准名。对话框只检查 skel 里有没有这几个动画。
    /// </para>
    /// <para>
    /// res 外的 Spine 写表前先导入 res/spine/&lt;Monster|Elite|Boss&gt;/&lt;目录名&gt;/（同「配置到特效表」）。
    /// 写完补贴图的 sRGB xml，并重新生成 <see cref="ManifestFile"/>：Hero 表里路径没在别处声明过的 Spine
    /// 都在这里补显式资源声明 —— 路径写在表里，编译期分析器看不见，不声明实机必缺（AGENTS §4.10）。
    /// </para>
    /// </summary>
    public class MonsterTableViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const string TableName = "Hero";

        /// <summary>怪物 id 段起点（英雄 1000 起，怪物 100000 起，AGENTS §3.4）</summary>
        private const int MonsterIdMin = 100000;

        /// <summary>生成的资源声明文件（相对工程根）</summary>
        private const string ManifestFile = "src\\Client\\Game\\GameScene\\GameView\\Character\\HeroTableSpineResourceManifest.g.cs";

        /// <summary>
        /// 怪物的标准动画：(动画名, 用途, 写进表的列)。列为 null 的不在 Hero 表里 ——
        /// attack 是游戏写死的（attackAnim 列已不用，仍写上保持一致），spell 是技能表 castAnim 留空时的缺省
        /// </summary>
        public static IReadOnlyList<(string Anim, string Usage, string? Field)> StandardAnims { get; } =
        [
            ("idle", "待机（循环）", "idleAnim"),
            ("run", "移动（循环）", "moveAnim"),
            ("attack", "普攻，时间轴上要打 attack 事件帧，否则没伤害", "attackAnim"),
            ("spell", "施法，时间轴上要打 Spell 事件帧；怪物有技能时才用", null),
            ("hit", "受击", "hitAnim"),
            ("die", "死亡（不循环，播完出掉落）", "deathAnim"),
        ];

        /// <summary>怪物类型：表里的 unitType、显示名、导入时放的 res/spine 子目录</summary>
        public sealed record UnitTypeOption(string Value, string Label, string Folder)
        {
            public override string ToString() => Label;
        }

        public static IReadOnlyList<UnitTypeOption> UnitTypes { get; } =
        [
            new("2", "普通怪", "Monster"),
            new("3", "精英", "Elite"),
            new("4", "首领", "Boss"),
        ];

        /// <summary>表里的一行（列表显示用）</summary>
        public sealed class HeroRow
        {
            public required Dictionary<string, string?> Values { get; init; }
            public int Id => int.TryParse(Values.GetValueOrDefault("id"), out var v) ? v : 0;
            public string Name => Values.GetValueOrDefault("heroName") ?? "";
            public string Type => UnitTypes.FirstOrDefault(t => t.Value == Values.GetValueOrDefault("unitType"))?.Label ?? Values.GetValueOrDefault("unitType") ?? "";
            public string Resource => Values.GetValueOrDefault("spineRes") ?? "";
        }

        private readonly string _projectRoot;
        private readonly string _sourceSkel;
        private readonly string? _sourceSpineProject;
        private readonly SpineSourceExportViewModel _exporter;

        private MonsterTableViewModel(string projectRoot, string sourceSkel, string? spineRes, AzureSailProject.SpineSourceInfo info,
            SpineSourceExportViewModel exporter, List<HeroRow> monsterRows)
        {
            _projectRoot = projectRoot;
            _sourceSkel = sourceSkel;
            _sourceSpineProject = info.SpineProject;
            _exporter = exporter;
            Animations = info.Animations;
            VersionWarning = info.Warning;
            MonsterRows = monsterRows;

            if (spineRes is not null)
            {
                _spineRes = spineRes;
                _monsterName = Path.GetFileName(Path.GetDirectoryName(spineRes.Replace('/', '\\'))) ?? "";
                _unitType = spineRes.Contains("/Boss/", StringComparison.OrdinalIgnoreCase) ? UnitTypes[2]
                    : spineRes.Contains("/Elite/", StringComparison.OrdinalIgnoreCase) ? UnitTypes[1] : UnitTypes[0];
            }
            else
            {
                _needImport = true;
                // 目录按骨骼名建（导出的 skel 以骨骼名命名），与「全部导出」拷到 导出文件夹/<骨骼名>/ 一致
                _importName = Path.GetFileNameWithoutExtension(sourceSkel);
                _monsterName = _importName;
                _unitType = UnitTypes[0];
                _spineRes = PreviewSpineRes();
            }

            // 表里已有这套 Spine 的怪物行：默认选中它（覆盖），名字、类型从那行带出来；没有就默认新增
            var existing = FindExisting(MonsterRows, _spineRes);
            if (existing is not null)
            {
                _selectedRow = existing;
                _isNewRow = false;
                _monsterName = existing.Name;
                _unitType = UnitTypes.FirstOrDefault(t => t.Value == existing.Values.GetValueOrDefault("unitType")) ?? _unitType;
            }

            // 名字一律按美术目录 美术/角色/怪物/<文件夹>/ 的文件夹名（找得到时优先于表里原来的名字）
            if (ArtMonsterName(projectRoot, sourceSkel) is { } artName)
                _monsterName = artName;

            // 标准动画逐个对照 skel（读不出动画名时不判，只提示）
            AnimChecks = StandardAnims
                .Select(a => new AnimCheck(a.Anim, a.Usage, Animations.Count == 0 ? null : Animations.Contains(a.Anim)))
                .ToList();
            MissingAnims = AnimChecks.Where(c => c.Found == false).Select(c => c.Anim).ToList();
        }

        /// <summary>为一个 skel 准备对话框数据。失败返回 null，并在 <paramref name="error"/> 里给出原因</summary>
        public static MonsterTableViewModel? Create(string skelPath, string? fallbackProjectRoot, SpineSourceExportViewModel exporter, out string? error)
        {
            var projectRoot = AzureSailProject.FindProjectRoot(skelPath, TableName, fallbackProjectRoot);
            if (projectRoot is null)
            {
                error = "找不到 AzureSail 工程（向上没找到 Excels/Hero.xlsx）。\n先在「母骨骼导出」面板里选一次工程根目录，再点这个菜单。";
                return null;
            }

            var full = Path.GetFullPath(skelPath);
            var spineRes = AzureSailProject.ToResPath(projectRoot, full);
            var info = AzureSailProject.InspectSource(full, spineRes is not null, exporter.SpineVersion, out error);
            if (info is null) return null;

            var rows = ReadMonsterRows(projectRoot, out error);
            if (rows is null) return null;

            return new MonsterTableViewModel(projectRoot, full, spineRes, info, exporter, rows);
        }

        // ---------------------------------------------------------------- 绑定

        /// <summary>来源文件（显示用）</summary>
        public string SourcePath => _sourceSkel;

        /// <summary>选中的 Spine 在 res 外，写表前要先导入</summary>
        public bool NeedImport { get => _needImport; private set => SetProperty(ref _needImport, value); }
        private bool _needImport;

        /// <summary>导入目录的父路径（随怪物类型变）</summary>
        public string ImportParentText => $"res/spine/{UnitType.Folder}/";

        /// <summary>导入到 res/spine/&lt;类型目录&gt;/ 下的目录名</summary>
        public string ImportName
        {
            get => _importName;
            set
            {
                var old = _importName;
                if (!SetProperty(ref _importName, value)) return;
                // 名字还是跟着目录名的默认值时一起改
                if (MonsterName == old) MonsterName = value;
                RefreshPreview();
            }
        }
        private string _importName = "";

        /// <summary>写进表里的地址（相对 res，带 .skel）。要导入时是推算的预览值，导入后换成实际文件</summary>
        public string SpineRes { get => _spineRes; private set { if (SetProperty(ref _spineRes, value)) OnPropertyChanged(nameof(PlanText)); } }
        private string _spineRes;

        /// <summary>写进 heroName 的名字</summary>
        public string MonsterName { get => _monsterName; set { if (SetProperty(ref _monsterName, value)) OnPropertyChanged(nameof(PlanText)); } }
        private string _monsterName;

        /// <summary>怪物类型（unitType）</summary>
        public UnitTypeOption UnitType
        {
            get => _unitType;
            set
            {
                if (value is null || !SetProperty(ref _unitType, value)) return;
                OnPropertyChanged(nameof(ImportParentText));
                OnPropertyChanged(nameof(PlanText));
                RefreshPreview();
            }
        }
        private UnitTypeOption _unitType;

        /// <summary>skel 里的动画名</summary>
        public List<string> Animations { get; }

        /// <summary>一个标准动画的检查结果。Found 为 null = 读不出 skel 的动画名，没法判</summary>
        public sealed record AnimCheck(string Anim, string Usage, bool? Found)
        {
            public string Status => Found switch { true => "✔ 有", false => "✘ 缺", null => "? 未知" };
        }

        /// <summary>六个标准动画逐个的检查结果（对话框列表用）</summary>
        public List<AnimCheck> AnimChecks { get; }

        /// <summary>skel 里缺的标准动画（空 = 齐全或没法判）</summary>
        public List<string> MissingAnims { get; }

        /// <summary>版本不对等提示，没有为 null</summary>
        public string? VersionWarning { get; }

        /// <summary>表里现有的怪物行</summary>
        public List<HeroRow> MonsterRows { get; }

        /// <summary>true = 新增一行；false = 覆盖列表里选中的行</summary>
        public bool IsNewRow
        {
            get => _isNewRow;
            set
            {
                if (!SetProperty(ref _isNewRow, value)) return;
                OnPropertyChanged(nameof(IsOverwrite));
                OnPropertyChanged(nameof(PlanText));
            }
        }
        private bool _isNewRow = true;

        /// <summary>覆盖模式（给第二个单选按钮绑定）</summary>
        public bool IsOverwrite { get => !_isNewRow; set => IsNewRow = !value; }

        public string NewRowText => $"新增一行（id = {NextMonsterId(MonsterRows)}）";

        /// <summary>要覆盖的行。在列表里选中即切到覆盖模式，类型跟着带成那行的类型</summary>
        public HeroRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!SetProperty(ref _selectedRow, value)) return;
                if (value is not null)
                {
                    IsNewRow = false;
                    UnitType = UnitTypes.FirstOrDefault(t => t.Value == value.Values.GetValueOrDefault("unitType")) ?? UnitType;
                }
                OnPropertyChanged(nameof(PlanText));
            }
        }
        private HeroRow? _selectedRow;

        /// <summary>写表时会做什么（覆盖哪行 / 新增哪个 id）</summary>
        public string PlanText
        {
            get
            {
                // 表里其它也指向这套 Spine 的行（共用一套 Spine 是允许的，只提醒）
                var sameSpine = MonsterRows.Where(r => string.Equals(r.Resource, SpineRes, StringComparison.OrdinalIgnoreCase)
                    && r != (IsNewRow ? null : SelectedRow)).Select(r => r.Id.ToString()).ToList();
                var note = sameSpine.Count > 0 ? $"\n注意：id {string.Join("、", sameSpine)} 也指向这套 Spine。" : "";

                if (!IsNewRow)
                {
                    if (SelectedRow is null)
                        return "请在下面列表里选中要覆盖的怪物行，或选「新增一行」。";
                    return $"覆盖 id {SelectedRow.Id}（{SelectedRow.Name}）：spineRes 改成这套 Spine，heroName 改为「{MonsterName}」，"
                        + "类型按上面选的，动作名改成标准名；属性、缩放、武器等其余列不动。" + note;
                }
                var template = PickTemplate(MonsterRows, UnitType.Value);
                return $"新增 id {NextMonsterId(MonsterRows)}"
                    + (template is null ? "。" : $"，属性、缩放、武器等照抄 id {template.Id}（{template.Name}），之后要改直接改表。")
                    + "\n要改已有的怪物，在下面列表里点选那一行即可覆盖它。" + note;
            }
        }

        // ---------------------------------------------------------------- 写表

        /// <summary>写表。返回是否成功与说明</summary>
        public (bool ok, string message) Apply(System.Windows.Window? owner)
        {
            if (string.IsNullOrWhiteSpace(MonsterName))
                return (false, "请填写怪物名字");
            if (!IsNewRow && SelectedRow is null)
                return (false, "请在列表里选中要覆盖的怪物行，或选「新增一行」");

            // res 外的先导入；导入成功后即使写表失败，再点也不会重复导入
            if (NeedImport)
            {
                var target = Path.Combine(_projectRoot, "res", "spine", UnitType.Folder, ImportName.Trim());
                string? skel;
                var existingSkel = Path.Combine(target, Path.GetFileName(_sourceSkel));
                if (File.Exists(existingSkel))
                {
                    // 目标里已有这套骨骼（比如「全部导出」已经拷过）：来源已是 4.2 就覆盖成最新，否则直接沿用
                    if (_sourceSpineProject is null && AzureSailProject.CopySpineFiles(_sourceSkel, target, overwrite: true) is { } copyError)
                        return (false, copyError);
                    skel = existingSkel;
                }
                else
                {
                    (skel, var importError) = AzureSailProject.ImportSpine(_sourceSkel, _sourceSpineProject, target, _exporter, owner);
                    if (skel is null) return (false, importError);
                }
                SpineRes = AzureSailProject.ToResPath(_projectRoot, skel)!;
                NeedImport = false;
            }

            // 重新读一次表再认行：对话框开着期间表可能被别处改过
            var rows = ReadMonsterRows(_projectRoot, out var readError);
            if (rows is null) return (false, readError!);

            var sets = new List<(string Field, string Value)>
            {
                ("heroName", MonsterName.Trim()),
                ("unitType", UnitType.Value),
            };
            sets.AddRange(StandardAnims.Where(a => a.Field is not null).Select(a => (a.Field!, a.Anim)));

            List<string> args;
            int id;
            var existing = IsNewRow ? null : rows.FirstOrDefault(r => r.Id == SelectedRow!.Id);
            if (!IsNewRow && existing is null)
                return (false, $"要覆盖的 id {SelectedRow!.Id} 已经不在表里了（对话框开着期间表被改过），关掉重开再试");
            if (existing is not null)
            {
                // 覆盖选中的行：连 spineRes 一起换成这套 Spine
                id = existing.Id;
                args = ["set", TableName, "--id", id.ToString(), "--set", $"spineRes={SpineRes}"];
            }
            else
            {
                id = NextMonsterId(rows);
                args = ["add-row", TableName, "--set", $"id={id}", "--set", $"spineRes={SpineRes}"];
                var template = PickTemplate(rows, UnitType.Value);
                if (template is not null)
                {
                    var skip = new HashSet<string>(sets.Select(s => s.Field)) { "id", "spineRes" };
                    foreach (var (k, v) in template.Values)
                    {
                        if (!skip.Contains(k) && !string.IsNullOrEmpty(v)) args.AddRange(["--set", $"{k}={v}"]);
                    }
                }
            }
            foreach (var (f, v) in sets) args.AddRange(["--set", $"{f}={v}"]);

            var (ok, output) = AzureSailProject.RunTableScript(_projectRoot, args);
            if (!ok)
            {
                _logger.Error("写英雄表失败:\n{0}", output);
                return (false, "写英雄表失败（Excel 里开着 Hero.xlsx 时会写不进去）：\n" + AzureSailProject.LastLines(output, 6));
            }
            _logger.Info("英雄表已{0}怪物 id {1}: {2} {3}", existing is not null ? "覆盖" : "新增", id, MonsterName, SpineRes);

            // 补 sRGB xml 与资源声明
            var declareNote = UpdateResourceDeclarations();

            return (true, $"已{(existing is not null ? "覆盖" : "新增")}英雄表怪物 id {id}（{MonsterName}）。\n{declareNote}\n\n"
                + "还需运行 DataTables\\gen.bat 导表（它会顺带编译两端），游戏里才生效。\n"
                + "新增的怪物要在关卡表 GameLevel.monsterId 里引用这个 id 才会刷出来。");
        }

        // ---------------------------------------------------------------- 资源声明

        /// <summary>
        /// 给 Hero 表里所有 spineRes 补贴图 sRGB xml，并重新生成 <see cref="ManifestFile"/>：
        /// 路径在 src/Client 其它 .cs 里没以字面量出现过的（即没人声明过的）Spine，在这里声明四件套。
        /// 返回给用户看的一句说明
        /// </summary>
        private string UpdateResourceDeclarations()
        {
            var raw = AzureSailProject.ReadTable(_projectRoot, TableName, out var error);
            if (raw is null)
                return "（读表失败，资源声明没更新：" + error + "）";

            var resDir = Path.Combine(_projectRoot, "res");
            var manifestPath = Path.Combine(_projectRoot, ManifestFile);
            var clientDir = Path.Combine(_projectRoot, "src", "Client");

            // 别处已声明的路径：src/Client 下其它 .cs 里出现过的字面量
            var otherSources = Directory.EnumerateFiles(clientDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetFullPath(f).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText)
                .ToList();
            bool DeclaredElsewhere(string resPath) => otherSources.Any(s => s.Contains("\"" + resPath + "\""));

            int xmlAdded = 0;
            var declared = new List<(string Type, string Field, string Path)>();
            var skels = raw.Select(r => r.GetValueOrDefault("spineRes")).Where(p => !string.IsNullOrEmpty(p)).Distinct()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var skelRes in skels)
            {
                var skelFull = Path.Combine(resDir, skelRes!.Replace('/', '\\'));
                var atlasFull = Path.ChangeExtension(skelFull, ".atlas");
                if (!File.Exists(skelFull) || !File.Exists(atlasFull)) continue;

                var files = new List<(string Type, string Suffix, string Full)> { ("Animation", "Skeleton", skelFull), ("Animation", "Atlas", atlasFull) };
                foreach (var page in AzureSailProject.AtlasPages(atlasFull))
                {
                    var png = Path.Combine(Path.GetDirectoryName(atlasFull)!, page);
                    if (!File.Exists(png)) continue;
                    if (AzureSailProject.EnsureSrgbXml(png)) xmlAdded++;
                    files.Add(("Texture", "Texture", png));
                    files.Add(("Animation", "TextureParameters", Path.ChangeExtension(png, ".xml")));
                }

                foreach (var (type, suffix, full) in files)
                {
                    var resPath = AzureSailProject.ToResPath(_projectRoot, full)!;
                    if (DeclaredElsewhere(resPath)) continue;
                    var stem = resPath.StartsWith("spine/", StringComparison.OrdinalIgnoreCase) ? resPath[6..] : resPath;
                    var field = new string(Path.ChangeExtension(stem, null)!.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + "_" + suffix;
                    declared.Add((type, field, resPath));
                }
            }

            var content = BuildManifest(declared);
            var changed = !File.Exists(manifestPath) || File.ReadAllText(manifestPath) != content;
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                File.WriteAllText(manifestPath, content, new UTF8Encoding(false));
                _logger.Info("已重新生成资源声明 {0}（{1} 条）", ManifestFile, declared.Count);
            }
            return $"补 sRGB xml {xmlAdded} 个；资源声明 {Path.GetFileName(ManifestFile)} {(changed ? "已更新" : "无变化")}（共 {declared.Count} 条）。";
        }

        private static string BuildManifest(List<(string Type, string Field, string Path)> declared)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated>\n");
            sb.Append("// 由 AzKit 资源库右键「配置到怪物...」生成，不要手改。\n");
            sb.Append("// Hero 表 spineRes 指向、而 src/Client 其它文件里没声明过的 Spine 四件套都在这里声明。\n");
            sb.Append("// </auto-generated>\n\n");
            sb.Append("namespace GameEntry\n{\n");
            sb.Append("    /// <summary>\n");
            sb.Append("    /// Hero 表里配置的 Spine（主要是怪物）的<b>显式资源声明</b>（.skel .atlas .png 与 sRGB .xml）。\n");
            sb.Append("    /// 路径写在表里、运行时才加载，编译期分析器看不见，不声明就不进发布清单（§4.10）。\n");
            sb.Append("    /// 手写的角色声明见 <see cref=\"CharacterResourceManifest\"/>。\n");
            sb.Append("    /// </summary>\n");
            sb.Append("    internal static class HeroTableSpineResourceManifest\n    {\n");
            foreach (var (type, field, path) in declared)
                sb.Append($"        public static readonly {type} {field} = \"{path}\";\n");
            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 辅助

        private void RefreshPreview()
        {
            if (NeedImport) SpineRes = PreviewSpineRes();
        }

        private string PreviewSpineRes() =>
            $"spine/{UnitType.Folder}/{ImportName.Trim()}/{Path.GetFileName(_sourceSkel)}";

        /// <summary>美术怪物目录：与工程根同级的 美术/角色/怪物，一个怪物一个文件夹，文件夹名就是怪物名</summary>
        private static string ArtMonsterRoot(string projectRoot) =>
            Path.Combine(Directory.GetParent(projectRoot)?.FullName ?? projectRoot, "美术", "角色", "怪物");

        /// <summary>
        /// 这套 Spine 在美术怪物目录里的文件夹名：本身就在那里面的取第一级文件夹；
        /// 在别处（比如 res 里的导出件）的，按骨骼名去美术目录里找同名 .spine / .skel。找不到返回 null
        /// </summary>
        private static string? ArtMonsterName(string projectRoot, string skelPath)
        {
            var root = ArtMonsterRoot(projectRoot);
            if (!Directory.Exists(root)) return null;

            string? FirstFolder(string path)
            {
                var rel = Path.GetRelativePath(root, path);
                if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
                var idx = rel.IndexOf('\\');
                return idx > 0 ? rel[..idx] : null;
            }

            if (FirstFolder(Path.GetFullPath(skelPath)) is { } inside) return inside;

            var name = Path.GetFileNameWithoutExtension(skelPath);
            var match = Directory.EnumerateFiles(root, name + ".spine", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(root, name + ".skel", SearchOption.AllDirectories).FirstOrDefault();
            return match is null ? null : FirstFolder(match);
        }

        private static List<HeroRow>? ReadMonsterRows(string projectRoot, out string? error)
        {
            var raw = AzureSailProject.ReadTable(projectRoot, TableName, out error);
            return raw?.Select(v => new HeroRow { Values = v }).Where(r => r.Id >= MonsterIdMin).OrderBy(r => r.Id).ToList();
        }

        /// <summary>怪物行里 spineRes 指向这套 Spine 的，多行取 id 最小的</summary>
        private static HeroRow? FindExisting(List<HeroRow> rows, string spineRes) =>
            rows.Where(r => string.Equals(r.Resource, spineRes, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Id).FirstOrDefault();

        /// <summary>新增行照抄的模板：同类型最后一行，没有就最后一个怪物行</summary>
        private static HeroRow? PickTemplate(List<HeroRow> rows, string unitType) =>
            rows.LastOrDefault(r => r.Values.GetValueOrDefault("unitType") == unitType) ?? rows.LastOrDefault();

        private static int NextMonsterId(List<HeroRow> rows) =>
            rows.Count > 0 ? rows.Max(r => r.Id) + 1 : MonsterIdMin;
    }
}
