using CommunityToolkit.Mvvm.ComponentModel;
using NLog;
using System.IO;
using SpineViewer.Utils;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 把资源库里选中的 Spine 特效配置进 AzureSail 的特效表（Excels/EffectData.xlsx）。
    /// <para>
    /// 改表不在这里直接读写 xlsx，而是调用项目自带的 skills/excel-table/scripts/xlsx_table.py ——
    /// 项目规定改表一律走它（保留条件格式、底色、列宽等样式，写前自动备份）。
    /// </para>
    /// <para>
    /// 两种写法：覆盖已有的一行（只改 spineRes / spineAnim，名字 effectName 保留不动，并清空 sceEffect，
    /// 因为两者都填时游戏会用 Spine 并告警）；或新增一行（ID = 现有最大 ID + 1，
    /// 场景、起点、时长、缩放照抄表里最后一个 Spine 特效行）。
    /// </para>
    /// <para>
    /// 选中的 Spine 不在工程 res 里时（如美术目录下的特效素材库），写表前先把它导入
    /// res/spine/Effect/&lt;名字&gt;/：已是 4.2 的直接拷 skel + atlas + 贴图；不是 4.2 的用同目录的
    /// .spine 工程经 Spine 命令行重新导出成 4.2（游戏运行时只认 4.2）。
    /// </para>
    /// </summary>
    public class EffectTableViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const string TableName = "EffectData";

        /// <summary>新增行时从模板行照抄的字段</summary>
        private static readonly string[] CopiedFields = ["sceneName", "startPos", "endPos", "duration", "scale"];

        /// <summary>表里的一行（只取对话框要显示的字段，其余字段原样保留在 Values 里）</summary>
        public sealed class EffectRow
        {
            public required Dictionary<string, string?> Values { get; init; }
            public int Id => int.TryParse(Values.GetValueOrDefault("id"), out var v) ? v : 0;
            public string Name => Values.GetValueOrDefault("effectName") ?? "";
            public string Resource => Values.GetValueOrDefault("spineRes") ?? Values.GetValueOrDefault("sceEffect") ?? "";
            public string Anim => Values.GetValueOrDefault("spineAnim") ?? "";
        }

        /// <summary>导入到的父目录（相对工程根）</summary>
        private const string ImportParent = "res\\spine\\Effect";

        private readonly string _projectRoot;

        /// <summary>选中的 skel 完整路径</summary>
        private readonly string _sourceSkel;

        /// <summary>需要重新导出时用的 .spine 工程；为 null 表示直接拷文件（已是 4.2）</summary>
        private readonly string? _sourceSpineProject;

        /// <summary>导出用（res 外的非 4.2 特效）</summary>
        private readonly SpineSourceExportViewModel _exporter;

        private EffectTableViewModel(string projectRoot, string sourceSkel, string? spineRes, string? sourceSpineProject,
            SpineSourceExportViewModel exporter, List<EffectRow> rows, List<string> animations, string? versionWarning)
        {
            _projectRoot = projectRoot;
            _sourceSkel = sourceSkel;
            _sourceSpineProject = sourceSpineProject;
            _exporter = exporter;
            Rows = rows;
            Animations = animations;
            VersionWarning = versionWarning;
            NewId = rows.Count > 0 ? rows.Max(r => r.Id) + 1 : 1;
            _selectedAnimation = animations.FirstOrDefault() ?? "";

            if (spineRes is not null)
            {
                _spineRes = spineRes;
                _effectName = Path.GetFileName(Path.GetDirectoryName(spineRes.Replace('/', '\\'))) ?? "";
            }
            else
            {
                // res 外：先要导入，目录名默认按 skel 名取，地址随目录名变
                _needImport = true;
                _importName = AzureSailProject.DefaultImportName(sourceSkel);
                _effectName = _importName;
                _spineRes = PreviewSpineRes();
            }
        }

        /// <summary>
        /// 为一个 skel 准备对话框数据。失败返回 null，并在 <paramref name="error"/> 里给出原因。
        /// <paramref name="fallbackProjectRoot"/>：skel 不在工程里时（往上找不到工程）用的工程根，一般取母骨骼导出记下的那个
        /// </summary>
        public static EffectTableViewModel? Create(string skelPath, string? fallbackProjectRoot, SpineSourceExportViewModel exporter, out string? error)
        {
            error = null;
            var projectRoot = AzureSailProject.FindProjectRoot(skelPath, TableName, fallbackProjectRoot);
            if (projectRoot is null)
            {
                error = "找不到 AzureSail 工程（向上没找到 Excels/EffectData.xlsx）。\n先在「母骨骼导出」面板里选一次工程根目录，再点这个菜单。";
                return null;
            }

            var full = Path.GetFullPath(skelPath);
            var spineRes = AzureSailProject.ToResPath(projectRoot, full);
            var info = AzureSailProject.InspectSource(full, spineRes is not null, exporter.SpineVersion, out error);
            if (info is null) return null;

            var raw = AzureSailProject.ReadTable(projectRoot, TableName, out error);
            if (raw is null) return null;
            var rows = raw.Select(v => new EffectRow { Values = v }).ToList();

            return new EffectTableViewModel(projectRoot, full, spineRes, info.SpineProject, exporter, rows, info.Animations, info.Warning);
        }

        /// <summary>写进表里的地址（相对 res，带 .skel）。要导入时是按目录名推算的预览值，导入后换成实际导出的文件</summary>
        public string SpineRes { get => _spineRes; private set => SetProperty(ref _spineRes, value); }
        private string _spineRes;

        /// <summary>选中的 Spine 在 res 外，写表前要先导入</summary>
        public bool NeedImport { get => _needImport; private set => SetProperty(ref _needImport, value); }
        private bool _needImport;

        /// <summary>导入到 res/spine/Effect/ 下的目录名</summary>
        public string ImportName
        {
            get => _importName;
            set
            {
                var old = _importName;
                if (!SetProperty(ref _importName, value)) return;
                // 名字还是跟着目录名的默认值时一起改
                if (EffectName == old) EffectName = value;
                SpineRes = PreviewSpineRes();
            }
        }
        private string _importName = "";

        /// <summary>来源文件（显示用）</summary>
        public string SourcePath => _sourceSkel;

        /// <summary>表里现有的行</summary>
        public List<EffectRow> Rows { get; }

        /// <summary>skel 里的动画名</summary>
        public List<string> Animations { get; }

        /// <summary>版本不对等提示，没有为 null</summary>
        public string? VersionWarning { get; }

        /// <summary>新增行将使用的 ID</summary>
        public int NewId { get; }

        public string NewRowText => $"新增一行（ID = {NewId}）";

        /// <summary>true = 新增一行；false = 覆盖选中的行</summary>
        public bool IsNewRow
        {
            get => _isNewRow;
            set
            {
                if (SetProperty(ref _isNewRow, value))
                    OnPropertyChanged(nameof(IsOverwrite));
            }
        }
        private bool _isNewRow = true;

        /// <summary>覆盖模式（给第二个单选按钮绑定）</summary>
        public bool IsOverwrite { get => !_isNewRow; set => IsNewRow = !value; }

        /// <summary>覆盖模式下选中的行。选中即切到覆盖模式</summary>
        public EffectRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!SetProperty(ref _selectedRow, value)) return;
                if (value is not null) IsNewRow = false;
            }
        }
        private EffectRow? _selectedRow;

        /// <summary>写进 effectName 的名字，默认取 skel 所在文件夹名</summary>
        public string EffectName { get => _effectName; set => SetProperty(ref _effectName, value); }
        private string _effectName;

        /// <summary>写进 spineAnim 的动画名，默认第一个动画</summary>
        public string SelectedAnimation { get => _selectedAnimation; set => SetProperty(ref _selectedAnimation, value); }
        private string _selectedAnimation;

        /// <summary>
        /// 写表。返回是否成功与说明
        /// </summary>
        public (bool ok, string message) Apply(System.Windows.Window? owner)
        {
            if (!IsNewRow && SelectedRow is null)
                return (false, "请选择要覆盖的行，或选「新增一行」");

            // res 外的先导入；导入成功后即使写表失败，再点也不会重复导入
            if (NeedImport)
            {
                var (imported, importError) = Import(owner);
                if (!imported) return (false, importError);
            }

            List<string> args;
            int id;
            if (IsNewRow)
            {
                id = NewId;
                args = ["add-row", TableName, "--set", $"id={id}"];
                // 其它字段照抄表里最后一个 Spine 特效行，没有就留空
                var template = Rows.LastOrDefault(r => !string.IsNullOrEmpty(r.Values.GetValueOrDefault("spineRes")));
                if (template is not null)
                {
                    foreach (var f in CopiedFields)
                    {
                        var v = template.Values.GetValueOrDefault(f);
                        if (!string.IsNullOrEmpty(v)) args.AddRange(["--set", $"{f}={v}"]);
                    }
                }
            }
            else
            {
                id = SelectedRow!.Id;
                args = ["set", TableName, "--id", id.ToString(), "--set", "sceEffect="];
            }
            // 覆盖已有行时不改名字（策划起的中文名保留），只有新增行才写 effectName
            if (IsNewRow)
                args.AddRange(["--set", $"effectName={EffectName}"]);
            args.AddRange(["--set", $"spineRes={SpineRes}", "--set", $"spineAnim={SelectedAnimation}"]);

            var (ok, output) = AzureSailProject.RunTableScript(_projectRoot, args);
            if (!ok)
            {
                _logger.Error("写特效表失败:\n{0}", output);
                return (false, "写特效表失败（Excel 里开着这张表时会写不进去）：\n" + AzureSailProject.LastLines(output, 6));
            }
            _logger.Info("特效表 {0} 已{1}第 {2} 行: {3} {4} {5}", TableName, IsNewRow ? "新增" : "覆盖", id,
                IsNewRow ? EffectName : SelectedRow!.Name, SpineRes, SelectedAnimation);
            _logger.Info("表已改，导表未跑：在工程里跑 DataTables\\gen.bat 才会生效；并在星火编辑器点「AzureSail/补全 Spine sRGB 与特效声明」");
            return (true, $"已写入特效表 ID {id}。\n\n还需两步才能在游戏里用：\n1. 星火编辑器菜单「AzureSail/补全 Spine sRGB 与特效声明」\n2. 运行 DataTables\\gen.bat 导表");
        }

        /// <summary>把 res 外的特效导入 res/spine/Effect/&lt;ImportName&gt;/，成功后 <see cref="SpineRes"/> 换成实际文件、<see cref="NeedImport"/> 置 false</summary>
        private (bool ok, string message) Import(System.Windows.Window? owner)
        {
            var target = Path.Combine(_projectRoot, ImportParent, ImportName.Trim());
            var (skel, error) = AzureSailProject.ImportSpine(_sourceSkel, _sourceSpineProject, target, _exporter, owner);
            if (skel is null) return (false, error);
            SpineRes = AzureSailProject.ToResPath(_projectRoot, skel)!;
            NeedImport = false;
            return (true, "");
        }

        /// <summary>按当前目录名推算的地址（导入前预览）</summary>
        private string PreviewSpineRes() =>
            $"spine/Effect/{ImportName.Trim()}/{Path.GetFileName(_sourceSkel)}";
    }
}
