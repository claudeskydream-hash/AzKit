using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace SpineViewer.ViewModels.Main
{
    /// <summary>
    /// 换装页 (AzureSail 魔改).
    /// <para>
    /// 做法同游戏里的换武器: 每个部位用关键词匹配出一组候选插槽,
    /// 下拉框列出这组插槽里的全部附件, 选中一个就亮它、同组其它插槽清空.
    /// 覆盖会在每帧动画之后补设, 动画里的附件关键帧改不回去.
    /// </para>
    /// <para>
    /// 关键词逗号分隔, 插槽名 (不分大小写) 包含任意一个即匹配; 前缀 <c>-</c> 表示排除.
    /// 各部位的关键词存在 <c>data/outfit-rules.json</c>.
    /// </para>
    /// </summary>
    public class OutfitViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static readonly string RulesPath = Path.Combine(App.DataDirectory, "outfit-rules.json");

        /// <summary>
        /// 默认部位与关键词, 按 hero01/hero02 的命名取的; 其它英雄命名不同, 在界面上改关键词即可
        /// </summary>
        private static readonly (string Part, string Keywords)[] DefaultRules =
        [
            ("前发刘海", "front hair, fronthair, bang, fringe, liuhai, qianfa, toufa, hair-a, hair-b, 刘海, 前发"),
            ("后发", "back hair, backhair, houfa, 后发"),
            ("眼睛", "eye, yanjing, 眼, -brow, -眉"),
            ("鼻子", "nose, bizi, 鼻"),
            ("嘴巴", "mouth, zuiba, 嘴"),
            ("上衣", "topwear, coat, shirt, cloth, shangyi, 上衣"),
            ("下衣", "bottomwear, pants, skirt, trousers, xiayi, kuzi, qunzi, 下衣, 裤, 裙"),
            ("头饰", "headwear, headdress, hat, hairpin, toushi, fashi, 头饰, 发饰"),
            // hero02: hand-weapon-dao / weapon-changgong / weapon-fazhang；hero01: weapon_jian。选一把 = 亮它、其余武器插槽清空，同游戏里 GM 的 weapon 命令
            ("武器", "weapon, wuqi, 武器"),
        ];

        private readonly ObservableCollection<OutfitPartViewModel> _parts = [];
        private SpineObjectModel? _spine;

        public OutfitViewModel()
        {
            var saved = LoadRules();
            foreach (var (part, keywords) in DefaultRules)
                _parts.Add(new(this, part, saved.TryGetValue(part, out var kw) ? kw : keywords));
        }

        public ObservableCollection<OutfitPartViewModel> Parts => _parts;

        /// <summary>
        /// 当前换装的模型, 只在属性面板选中单个模型时有值
        /// </summary>
        public SpineObjectModel? Spine
        {
            get => _spine;
            set
            {
                if (!SetProperty(ref _spine, value)) return;
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(ModelTitle));
                RebuildAll();
                RebuildAnimations();
            }
        }

        /// <summary>
        /// 当前模型的全部动画, 选中哪个就在轨道 0 循环播放哪个
        /// </summary>
        public ObservableCollection<string> Animations { get; } = [];

        public string? SelectedAnimation
        {
            get => _selectedAnimation;
            set
            {
                if (!SetProperty(ref _selectedAnimation, value)) return;
                if (!_syncingAnimation && value is not null) _spine?.SetAnimation(0, value);
            }
        }
        private string? _selectedAnimation;

        /// <summary>
        /// 切模型时回填当前动画, 不能触发重播
        /// </summary>
        private bool _syncingAnimation;

        private void RebuildAnimations()
        {
            _syncingAnimation = true;
            try
            {
                Animations.Clear();
                if (_spine is not null)
                    foreach (var name in _spine.Animations) Animations.Add(name);
                SelectedAnimation = _spine?.GetAnimation(0);
            }
            finally
            {
                _syncingAnimation = false;
            }
        }

        public bool IsAvailable => _spine is not null;

        /// <summary>
        /// 顶部标题: 当前换装的模型名
        /// </summary>
        public string ModelTitle => _spine is null ? "未选中模型（在「模型」页只选一个）" : "当前模型: " + _spine.Name;

        /// <summary>
        /// 重新匹配插槽并刷新下拉框 (切换模型、加载/卸载皮肤后调用)
        /// </summary>
        public void RebuildAll()
        {
            foreach (var p in _parts) p.Rebuild();
        }

        /// <summary>
        /// 全部部位恢复默认
        /// </summary>
        public RelayCommand Cmd_ResetAll => _cmd_ResetAll ??= new(() =>
        {
            foreach (var p in _parts) p.SelectedOption = p.Options.FirstOrDefault();
        });
        private RelayCommand? _cmd_ResetAll;

        internal void SaveRules()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
                var dict = _parts.ToDictionary(p => p.Name, p => p.Keywords);
                File.WriteAllText(RulesPath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to save outfit rules: {0}", ex.Message);
            }
        }

        private static Dictionary<string, string> LoadRules()
        {
            try
            {
                if (File.Exists(RulesPath))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RulesPath)) ?? [];
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to load outfit rules: {0}", ex.Message);
            }
            return [];
        }
    }

    /// <summary>
    /// 一个部位 (如前发刘海): 关键词 → 候选插槽 → 下拉选项
    /// </summary>
    public class OutfitPartViewModel : ObservableObject
    {
        private readonly OutfitViewModel _owner;
        private readonly string _name;
        private string _keywords;
        private string[] _slots = [];
        private OutfitOption? _selectedOption;

        /// <summary>
        /// 重建选项时回填当前状态, 不能触发换装
        /// </summary>
        private bool _syncing;

        public OutfitPartViewModel(OutfitViewModel owner, string name, string keywords)
        {
            _owner = owner;
            _name = name;
            _keywords = keywords;
        }

        public string Name => _name;

        public string Keywords
        {
            get => _keywords;
            set
            {
                if (!SetProperty(ref _keywords, value ?? "")) return;
                Rebuild();
                _owner.SaveRules();
            }
        }

        /// <summary>
        /// 匹配到的插槽, 显示在提示里方便核对关键词
        /// </summary>
        public string MatchedSlots => _slots.Length > 0 ? "匹配插槽: " + string.Join(", ", _slots) : "没有匹配的插槽, 改一下关键词";

        public bool HasSlots => _slots.Length > 0;

        public ObservableCollection<OutfitOption> Options { get; } = [];

        public OutfitOption? SelectedOption
        {
            get => _selectedOption;
            set
            {
                if (!SetProperty(ref _selectedOption, value)) return;
                if (!_syncing && value is not null) Apply(value);
            }
        }

        internal void Rebuild()
        {
            var spine = _owner.Spine;
            _syncing = true;
            try
            {
                Options.Clear();
                _slots = spine is null ? [] : spine.Slots.Where(Match).ToArray();
                if (spine is not null && _slots.Length > 0)
                {
                    Options.Add(OutfitOption.Default);
                    Options.Add(OutfitOption.Hide);
                    foreach (var slot in _slots)
                        foreach (var att in spine.GetSlotAttachmentNames(slot))
                            Options.Add(new(OutfitOptionKind.Attachment, slot, att));
                }
                SelectedOption = ReadCurrent(spine);
            }
            finally
            {
                _syncing = false;
            }
            OnPropertyChanged(nameof(MatchedSlots));
            OnPropertyChanged(nameof(HasSlots));
        }

        /// <summary>
        /// 选中一项: 同组插槽先全部清空, 再亮出选中的附件 (同换武器)
        /// </summary>
        private void Apply(OutfitOption option)
        {
            var spine = _owner.Spine;
            if (spine is null) return;

            switch (option.Kind)
            {
                case OutfitOptionKind.Default:
                    foreach (var slot in _slots) spine.ClearAttachmentOverride(slot);
                    break;
                case OutfitOptionKind.Hide:
                    foreach (var slot in _slots) spine.SetAttachmentOverride(slot, null);
                    break;
                case OutfitOptionKind.Attachment:
                    foreach (var slot in _slots)
                        if (slot != option.Slot) spine.SetAttachmentOverride(slot, null);
                    spine.SetAttachmentOverride(option.Slot!, option.Attachment);
                    break;
            }
        }

        /// <summary>
        /// 由模型上的覆盖反推当前选中项, 对不上任何一项时显示为默认
        /// </summary>
        private OutfitOption? ReadCurrent(SpineObjectModel? spine)
        {
            if (spine is null || Options.Count <= 0) return null;

            var overrides = new List<(string Slot, string? Attachment)>();
            foreach (var slot in _slots)
                if (spine.TryGetAttachmentOverride(slot, out var att)) overrides.Add((slot, att));

            if (overrides.Count <= 0) return Options[0];
            var shown = overrides.Where(o => o.Attachment is not null).ToList();
            if (shown.Count == 0 && overrides.Count == _slots.Length) return OutfitOption.Hide;
            if (shown.Count == 1)
            {
                var hit = Options.FirstOrDefault(o => o.Slot == shown[0].Slot && o.Attachment == shown[0].Attachment);
                if (hit is not null) return hit;
            }
            return Options[0];
        }

        private bool Match(string slotName)
        {
            bool included = false;
            foreach (var raw in _keywords.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.StartsWith('-'))
                {
                    var ex = raw[1..].Trim();
                    if (ex.Length > 0 && slotName.Contains(ex, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (slotName.Contains(raw, StringComparison.OrdinalIgnoreCase))
                {
                    included = true;
                }
            }
            return included;
        }
    }

    public enum OutfitOptionKind { Default, Hide, Attachment }

    /// <summary>
    /// 下拉框的一项
    /// </summary>
    public sealed record OutfitOption(OutfitOptionKind Kind, string? Slot, string? Attachment)
    {
        public static readonly OutfitOption Default = new(OutfitOptionKind.Default, null, null);
        public static readonly OutfitOption Hide = new(OutfitOptionKind.Hide, null, null);

        public override string ToString() => Kind switch
        {
            OutfitOptionKind.Default => "（默认）",
            OutfitOptionKind.Hide => "（隐藏）",
            _ => Slot == Attachment ? Slot! : $"{Slot} / {Attachment}",
        };
    }
}
