using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using SpineViewer.Utils;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 左侧栏「邮件」页（写信，MailPanel）+ 右侧「邮件」页签（待发箱，MailOutboxPanel）：往游戏的<b>邮件待发箱</b>追加邮件（见 <see cref="CloudMailOutbox"/>、AzureSail 工程 <c>ReadMarkDown/邮件系统.md</c>）。
    ///
    /// <code>
    ///   读取     查虚拟账号 9999989 当前环境的待发箱；同时读工程的 ItemData 表（附件挑物品用）
    ///   发送     填标题 / 正文 / 发件人 / 有效天数 / 目标（全服、指定区、指定玩家）/ 附件 → 追加一封（顺手删掉已过期的）→ 写回
    ///   撤回     从待发箱删掉选中的：还没收到的玩家不再收到；已经收到的不动（在玩家自己的收件箱里）
    /// </code>
    ///
    /// 凭证和环境用云存档页的（① / ②）。写之前把这个账号的全部行备份到 <c>data/cloudbackup/</c>，写完重新读一遍核对。
    /// 游戏服务端每 60 秒读一次待发箱，在线玩家一分钟内收到；不在线的下次进区收到。
    /// </summary>
    public class MailViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>右侧「邮件」页签的序号（MainWindow 右侧 TabControl：0 预览 / 1 预览图 / 2 存档 / 3 笔记 / 4 邮件）</summary>
        public const int OutboxTabIndex = 4;

        /// <summary>一封邮件附件最多几种物品</summary>
        private const int MaxAttachments = 10;

        private readonly CloudSaveViewModel _cloud;

        /// <summary>最近一次读到的；换了环境就作废</summary>
        private CloudMailOutbox? _state;

        /// <summary>物品表：id → 物品（读取时读一次）</summary>
        private Dictionary<int, ItemOption> _items = [];

        /// <summary>用户手选的工程根（自动找不到时问一次）</summary>
        private string? _projectRoot;

        public MailViewModel(CloudSaveViewModel cloud)
        {
            _cloud = cloud;
        }

        /// <summary>当前环境（云存档页 ② 选的）</summary>
        public string EnvText => _cloud.Env == "formal" ? "formal 正式（线上！）" : _cloud.Env;

        // =============== 编辑中的邮件 ===============

        public string MailTitle { get => _mailTitle; set => SetProperty(ref _mailTitle, value); }
        private string _mailTitle = "";

        public string MailContent { get => _mailContent; set => SetProperty(ref _mailContent, value); }
        private string _mailContent = "";

        public string Sender { get => _sender; set => SetProperty(ref _sender, value); }
        private string _sender = "青岚宗";

        public string ExpireDaysText { get => _expireDaysText; set => SetProperty(ref _expireDaysText, value); }
        private string _expireDaysText = "30";

        /// <summary>目标下拉的文字（下标 = <see cref="CloudMailOutbox.TargetAll"/> 等）</summary>
        public string[] TargetOptions { get; } = ["全服", "指定区", "指定玩家（UserId）"];

        public int TargetIndex
        {
            get => _targetIndex;
            set
            {
                if (SetProperty(ref _targetIndex, value))
                {
                    OnPropertyChanged(nameof(NeedTargetIds));
                    OnPropertyChanged(nameof(CanIncludeNew));
                }
            }
        }
        private int _targetIndex;

        /// <summary>指定区 / 指定玩家时要填号</summary>
        public bool NeedTargetIds => TargetIndex != CloudMailOutbox.TargetAll;

        /// <summary>「之后建号的也收」只对全服 / 指定区有意义</summary>
        public bool CanIncludeNew => TargetIndex != CloudMailOutbox.TargetUsers;

        /// <summary>区号 / UserId，逗号或空格分隔</summary>
        public string TargetIdsText { get => _targetIdsText; set => SetProperty(ref _targetIdsText, value); }
        private string _targetIdsText = "";

        public bool IncludeNew { get => _includeNew; set => SetProperty(ref _includeNew, value); }
        private bool _includeNew;

        // =============== 附件 ===============

        /// <summary>物品表的一项（下拉 / 搜索用）</summary>
        public sealed record ItemOption(int Id, string Name, string Quality, string Type)
        {
            public string Display => $"{Id}  {Name}  [{Type} 品质{Quality}]";
        }

        /// <summary>附件表格的一行（id 先存文本，发送时再校验）</summary>
        public sealed class AttachRow : ObservableObject
        {
            private readonly MailViewModel _owner;

            public AttachRow(MailViewModel owner) => _owner = owner;

            public string ItemIdText
            {
                get => _itemIdText;
                set
                {
                    if (SetProperty(ref _itemIdText, value))
                        OnPropertyChanged(nameof(ItemName));
                }
            }
            private string _itemIdText = "";

            public string CountText { get => _countText; set => SetProperty(ref _countText, value); }
            private string _countText = "1";

            /// <summary>物品名（查表，只读）</summary>
            public string ItemName => int.TryParse(ItemIdText.Trim(), out int id) && _owner._items.TryGetValue(id, out ItemOption? item)
                ? item.Name
                : _owner._items.Count == 0 ? "（先点「读取」读物品表）" : "✗ 表里没有这个物品";

            public void RefreshName() => OnPropertyChanged(nameof(ItemName));
        }

        public ObservableCollection<AttachRow> Attachments { get; } = [];

        public AttachRow? SelectedAttachment { get => _selectedAttachment; set => SetProperty(ref _selectedAttachment, value); }
        private AttachRow? _selectedAttachment;

        /// <summary>物品搜索框（id 或名字的一部分）</summary>
        public string ItemSearch
        {
            get => _itemSearch;
            set
            {
                if (SetProperty(ref _itemSearch, value))
                    FilterItems();
            }
        }
        private string _itemSearch = "";

        /// <summary>搜索结果（最多 200 条）</summary>
        public ObservableCollection<ItemOption> ItemResults { get; } = [];

        public ItemOption? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
        private ItemOption? _selectedItem;

        public RelayCommand Cmd_AddItem => _cmd_AddItem ??= new(() =>
        {
            if (SelectedItem is null)
            {
                StatusText = "✗ 先在搜索结果里选一个物品";
                return;
            }

            AttachRow? same = Attachments.FirstOrDefault(a => a.ItemIdText.Trim() == SelectedItem.Id.ToString());
            if (same is not null)
            {
                SelectedAttachment = same;
                StatusText = $"「{SelectedItem.Name}」已经在附件里了，改数量即可";
                return;
            }

            if (Attachments.Count >= MaxAttachments)
            {
                StatusText = $"✗ 一封邮件最多 {MaxAttachments} 种附件";
                return;
            }

            AttachRow row = new(this) { ItemIdText = SelectedItem.Id.ToString() };
            Attachments.Add(row);
            SelectedAttachment = row;
        });
        private RelayCommand? _cmd_AddItem;

        public RelayCommand Cmd_AddBlankItem => _cmd_AddBlankItem ??= new(() =>
        {
            if (Attachments.Count >= MaxAttachments)
            {
                StatusText = $"✗ 一封邮件最多 {MaxAttachments} 种附件";
                return;
            }

            AttachRow row = new(this);
            Attachments.Add(row);
            SelectedAttachment = row;
        });
        private RelayCommand? _cmd_AddBlankItem;

        public RelayCommand Cmd_RemoveItem => _cmd_RemoveItem ??= new(() =>
        {
            if (SelectedAttachment is not null)
                Attachments.Remove(SelectedAttachment);
        });
        private RelayCommand? _cmd_RemoveItem;

        public RelayCommand Cmd_ClearForm => _cmd_ClearForm ??= new(() =>
        {
            MailTitle = "";
            MailContent = "";
            Attachments.Clear();
            TargetIdsText = "";
        });
        private RelayCommand? _cmd_ClearForm;

        // =============== 已在待发箱的 ===============

        /// <summary>待发箱表格的一行</summary>
        public sealed record SentRow(CloudMailOutbox.Mail Mail, string SendText, string ExpireText, string TargetText, string ItemsText, string State);

        public ObservableCollection<SentRow> Sent { get; } = [];

        public SentRow? SelectedSent
        {
            get => _selectedSent;
            set
            {
                if (SetProperty(ref _selectedSent, value) && value is not null)
                    Output = Describe(value.Mail);
            }
        }
        private SentRow? _selectedSent;

        public string Output { get => _output; private set => SetProperty(ref _output, value); }
        private string _output = "先点「读取」。凭证与环境用云存档页 ① ② 的。";

        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        private string _statusText = "";

        public bool IsIdle { get => _isIdle; private set => SetProperty(ref _isIdle, value); }
        private bool _isIdle = true;

        public AsyncRelayCommand Cmd_Load => _cmd_Load ??= new(() => RunAsync("读取", LoadAsync));
        private AsyncRelayCommand? _cmd_Load;

        public AsyncRelayCommand Cmd_Send => _cmd_Send ??= new(SendAsync);
        private AsyncRelayCommand? _cmd_Send;

        public AsyncRelayCommand Cmd_Withdraw => _cmd_Withdraw ??= new(WithdrawAsync);
        private AsyncRelayCommand? _cmd_Withdraw;

        /// <summary>把选中的已发邮件抄回编辑区（改改再发一封）</summary>
        public RelayCommand Cmd_CopyToForm => _cmd_CopyToForm ??= new(() =>
        {
            if (SelectedSent is null)
                return;

            CloudMailOutbox.Mail m = SelectedSent.Mail;
            MailTitle = m.Title;
            MailContent = m.Content;
            Sender = m.Sender;
            ExpireDaysText = m.ExpireDays.ToString();
            TargetIndex = m.Target;
            TargetIdsText = m.Target == CloudMailOutbox.TargetServers ? string.Join(",", m.ServerIds)
                : m.Target == CloudMailOutbox.TargetUsers ? string.Join(",", m.UserIds) : "";
            IncludeNew = m.IncludeNew;
            Attachments.Clear();
            foreach (CloudMailOutbox.Attach a in m.Items)
                Attachments.Add(new AttachRow(this) { ItemIdText = a.ItemId.ToString(), CountText = a.Count.ToString() });
            StatusText = "已抄回编辑区；改完点「发送」是另发一封（原来那封不动）";
        });
        private RelayCommand? _cmd_CopyToForm;

        // =============== 读取 ===============

        private async Task<string> LoadAsync(CloudAdminToken token)
        {
            string itemNote = LoadItems();

            List<CloudSaveRow> rows = await CloudAdminApi.QueryAsync(token, CloudMailOutbox.ContainerUserId, _cloud.Env);
            _state = CloudMailOutbox.Build(_cloud.Env, rows);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Sent.Clear();
            foreach (CloudMailOutbox.Mail m in _state.Mails.OrderByDescending(m => m.Id))
            {
                long expire = m.ExpireAtMs();
                Sent.Add(new SentRow(m, CloudMailOutbox.Format(m.SendMs), CloudMailOutbox.Format(expire), CloudMailOutbox.TargetText(m),
                    ItemsText(m.Items), now >= expire ? "已过期" : "投递中"));
            }

            OnPropertyChanged(nameof(EnvText));
            Output = _state.OutboxRow is null
                ? $"环境 {_cloud.Env}　容器账号 {CloudMailOutbox.ContainerUserId}\n没有 {CloudMailOutbox.OutboxKey} 这一行 —— 这个环境还没开过局，先开一局让服务端建占位行，再来发。"
                : _state.Broken
                    ? $"环境 {_cloud.Env}\n✗ {CloudMailOutbox.OutboxKey} 的内容解不开（JSON 坏了）。先别发：看云存档页里这一行，或从 data\\cloudbackup 的备份恢复。"
                    : $"环境 {_cloud.Env}　容器账号 {CloudMailOutbox.ContainerUserId}\n待发箱 {_state.Mails.Count} 封（过期的下次发送时顺手删掉）。\n{itemNote}\n\n选中一封看详情。";
            return $"待发箱 {_state.Mails.Count} 封，物品表 {_items.Count} 项";
        }

        /// <summary>读工程的 ItemData 表；返回说明文字</summary>
        private string LoadItems()
        {
            string? root = ResolveProjectRoot();
            if (root is null)
                return "物品表：没找到 AzureSail 工程（附件只能手填 id，不校验）";

            List<Dictionary<string, string?>>? raw = AzureSailProject.ReadTable(root, "ItemData", out string? error);
            if (raw is null)
                return "物品表读取失败：" + error;

            Dictionary<int, ItemOption> items = [];
            foreach (Dictionary<string, string?> r in raw)
            {
                if (!int.TryParse(r.GetValueOrDefault("id"), out int id) || id <= 0)
                    continue;
                items[id] = new ItemOption(id, r.GetValueOrDefault("itemName") ?? "", r.GetValueOrDefault("quaility") ?? "?",
                    r.GetValueOrDefault("itemType1") ?? "");
            }

            _items = items;
            FilterItems();
            foreach (AttachRow a in Attachments)
                a.RefreshName();
            return $"物品表：{root}\\Excels\\ItemData.xlsx，{items.Count} 项";
        }

        /// <summary>工程根：英雄导出记下的 → 之前手选的 → 问一次</summary>
        private string? ResolveProjectRoot()
        {
            foreach (string? dir in new[] { _projectRoot, _cloud.AzureSailRoot })
            {
                if (!string.IsNullOrWhiteSpace(dir) && AzureSailProject.IsProjectRoot(dir, "ItemData"))
                    return dir;
            }

            MessagePopupService.Info("请选择 AzureSail 工程根目录（里面有 Excels\\ItemData.xlsx）");
            if (!DialogService.ShowOpenFolderDialog(out string? folder) || folder is null || !AzureSailProject.IsProjectRoot(folder, "ItemData"))
                return null;
            _projectRoot = folder;
            return folder;
        }

        private void FilterItems()
        {
            string key = ItemSearch.Trim();
            ItemResults.Clear();
            foreach (ItemOption item in _items.Values.OrderBy(i => i.Id))
            {
                if (key.Length > 0 && !item.Id.ToString().Contains(key) && !item.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                ItemResults.Add(item);
                if (ItemResults.Count >= 200)
                    break;
            }
        }

        // =============== 发送 / 撤回 ===============

        private Task SendAsync()
        {
            if (!CheckLoaded())
                return Task.CompletedTask;

            if (BuildMail(out string error) is not { } mail)
            {
                StatusText = "✗ " + error;
                return Task.CompletedTask;
            }

            CloudMailOutbox state = _state!;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            mail.SendMs = now;
            mail.Id = state.NextId(now);

            if (!Confirm($"往 {state.Env} 环境的待发箱发一封邮件：\n\n{Describe(mail)}", "确认发邮件"))
                return Task.CompletedTask;

            return RunAsync("发送", async token =>
            {
                string backup = Backup(state);
                await CloudAdminApi.EditAsync(token, state.OutboxRow!, CloudMailOutbox.Value(state.Mails.Append(mail), now));
                _logger.Info("[Mail] {0} 环境发邮件 Id={1}「{2}」目标 {3} 附件 {4}，备份 {5}", state.Env, mail.Id, mail.Title,
                    CloudMailOutbox.TargetText(mail), ItemsText(mail.Items), backup);
                await LoadAsync(token);
                Output = $"已发送：\n{Describe(mail)}\n\n备份：{backup}\n在线玩家一分钟内收到（服务端每 60 秒读一次待发箱），不在线的下次进区收到。";
                return $"已发送「{mail.Title}」";
            });
        }

        private Task WithdrawAsync()
        {
            if (SelectedSent is null)
            {
                StatusText = "✗ 先在待发箱表格里选一封";
                return Task.CompletedTask;
            }

            if (!CheckLoaded())
                return Task.CompletedTask;

            CloudMailOutbox state = _state!;
            CloudMailOutbox.Mail target = SelectedSent.Mail;
            if (!Confirm($"从 {state.Env} 环境的待发箱撤回「{target.Title}」（Id {target.Id}）。\n还没收到的玩家不再收到；已经收到的在玩家自己的收件箱里，撤不回。", "确认撤回邮件"))
                return Task.CompletedTask;

            return RunAsync("撤回", async token =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string backup = Backup(state);
                await CloudAdminApi.EditAsync(token, state.OutboxRow!, CloudMailOutbox.Value(state.Mails.Where(m => m.Id != target.Id), now));
                _logger.Info("[Mail] {0} 环境撤回邮件 Id={1}「{2}」，备份 {3}", state.Env, target.Id, target.Title, backup);
                await LoadAsync(token);
                Output += $"\n\n备份：{backup}";
                return $"已撤回「{target.Title}」";
            });
        }

        /// <summary>从编辑区拼一封邮件；填错返回 null</summary>
        private CloudMailOutbox.Mail? BuildMail(out string error)
        {
            error = "";
            string title = MailTitle.Trim();
            if (title.Length == 0)
            {
                error = "标题没填";
                return null;
            }

            if (!int.TryParse(ExpireDaysText.Trim(), out int days) || days < 1 || days > 365)
            {
                error = "有效天数要填 1～365";
                return null;
            }

            CloudMailOutbox.Mail mail = new()
            {
                Title = title,
                Content = MailContent.Trim(),
                Sender = Sender.Trim(),
                ExpireDays = days,
                Target = TargetIndex,
                IncludeNew = CanIncludeNew && IncludeNew,
            };

            if (NeedTargetIds)
            {
                string[] parts = TargetIdsText.Split([',', '，', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                List<long> ids = [];
                foreach (string p in parts)
                {
                    if (!long.TryParse(p, out long id) || id <= 0)
                    {
                        error = $"「{p}」不是正整数";
                        return null;
                    }
                    if (!ids.Contains(id))
                        ids.Add(id);
                }

                if (ids.Count == 0)
                {
                    error = TargetIndex == CloudMailOutbox.TargetServers ? "指定区要填区号" : "指定玩家要填 UserId";
                    return null;
                }

                if (TargetIndex == CloudMailOutbox.TargetServers)
                {
                    if (ids.Exists(id => id > int.MaxValue))
                    {
                        error = "区号太大";
                        return null;
                    }
                    mail.ServerIds = ids.Select(id => (int)id).ToArray();
                }
                else
                {
                    mail.UserIds = ids.ToArray();
                }
            }

            List<CloudMailOutbox.Attach> items = [];
            foreach (AttachRow a in Attachments)
            {
                string idText = a.ItemIdText.Trim();
                if (idText.Length == 0)
                    continue;

                if (!int.TryParse(idText, out int itemId) || itemId <= 0)
                {
                    error = $"附件「{idText}」不是物品 id";
                    return null;
                }

                if (_items.Count > 0 && !_items.ContainsKey(itemId))
                {
                    error = $"物品表里没有 {itemId}";
                    return null;
                }

                if (!long.TryParse(a.CountText.Trim(), out long count) || count <= 0)
                {
                    error = $"附件 {itemId} 的数量要填正整数";
                    return null;
                }

                CloudMailOutbox.Attach? same = items.Find(i => i.ItemId == itemId);
                if (same is not null)
                    same.Count += count;
                else
                    items.Add(new CloudMailOutbox.Attach { ItemId = itemId, Count = count });
            }

            mail.Items = items.ToArray();
            return mail;
        }

        private string ItemsText(CloudMailOutbox.Attach[] items) => items.Length == 0
            ? "（无附件）"
            : string.Join("，", items.Select(i => (_items.TryGetValue(i.ItemId, out ItemOption? o) ? o.Name : i.ItemId.ToString()) + "×" + i.Count));

        private string Describe(CloudMailOutbox.Mail m) => string.Join("\n",
        [
            $"Id {m.Id}　发送 {CloudMailOutbox.Format(m.SendMs)}　有效 {m.ExpireDays} 天（到 {CloudMailOutbox.Format(m.ExpireAtMs())}）",
            $"目标：{CloudMailOutbox.TargetText(m)}",
            $"发件人：{m.Sender}",
            $"标题：{m.Title}",
            $"正文：\n{m.Content}",
            $"附件：{ItemsText(m.Items)}",
        ]);

        /// <summary>写之前必须先读过、且是当前环境、且有那一行、且内容没坏</summary>
        private bool CheckLoaded()
        {
            if (_state is null || _state.Env != _cloud.Env)
            {
                StatusText = "✗ 先点「读取」（换过环境也要重新读）。";
                return false;
            }

            if (_state.OutboxRow is null)
            {
                StatusText = "✗ 这个环境还没有待发箱那一行：先在这个环境开一局（服务端启动时自动建占位行），再来发。";
                return false;
            }

            if (_state.Broken)
            {
                StatusText = "✗ 待发箱内容解不开，先别写（见下方说明）。";
                return false;
            }

            return true;
        }

        private bool Confirm(string text, string caption)
        {
            text += "\n\n写之前会把这个账号的全部行备份到 data\\cloudbackup。";
            if (_cloud.Env == "formal")
                text = "‼ 这是正式环境 formal，线上玩家会收到！\n\n" + text;
            return MessagePopupService.OKCancel(text, caption);
        }

        /// <summary>跑一次后台请求：查凭证、锁界面、写状态、异常转成状态文字</summary>
        private async Task RunAsync(string label, Func<CloudAdminToken, Task<string>> action)
        {
            CloudAdminToken? token = _cloud.Token;
            if (token is null || token.IsExpired)
            {
                StatusText = token is null ? "✗ 还没有凭证：先在云存档页 ① 粘贴 Cookie 并解析。" : "✗ token 已过期：在云存档页 ① 重新获取。";
                return;
            }

            IsIdle = false;
            StatusText = "● " + label + "……";
            _cloud.SetOperationLabel("邮件 · " + label);
            try
            {
                StatusText = "✓ " + label + "：" + await action(token) + $"（{DateTime.Now:HH:mm:ss}）";
            }
            catch (CloudAdminException e)
            {
                _logger.Warn("[Mail] {0} 失败：{1}", label, e.Message);
                StatusText = "✗ " + label + "：" + e.Message;
            }
            finally
            {
                IsIdle = true;
            }
        }

        /// <summary>备份成文件（与云存档页删档前的备份同格式），返回路径</summary>
        private static string Backup(CloudMailOutbox state)
        {
            JsonArray rows = [];
            foreach (CloudSaveRow row in state.Rows)
                rows.Add(row.ToJson());

            JsonObject root = new()
            {
                ["userId"] = CloudMailOutbox.ContainerUserId.ToString(),
                ["env"] = state.Env,
                ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["rows"] = rows,
            };

            Directory.CreateDirectory(CloudSaveViewModel.BackupDirectory);
            string path = Path.Combine(CloudSaveViewModel.BackupDirectory, $"{CloudMailOutbox.ContainerUserId}_{state.Env}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return path;
        }
    }
}
