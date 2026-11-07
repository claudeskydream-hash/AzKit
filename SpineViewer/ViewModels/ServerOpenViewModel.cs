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
    /// [AzureSail 新增] 「开服设置」窗口：读 / 设游戏的<b>上线时间</b>（全服开服存档，见 <see cref="CloudServerOpen"/>）。
    ///
    /// <code>
    ///   读取      查虚拟账号 9999992 当前环境的全部行：生效的上线时间、AzKit 设的、开发者登录写的、各区开服记录
    ///   设上线时间 写 serveronline_set（可以是将来的时刻）；可选同时清空已写下的区开服记录（让各区按新上线时间重算）
    ///   取消上线时间 serveronline_set 改回 0（占位）；开发者登录写的那条不动
    ///   开发者表格   UserId / 备注可改，游戏昵称从这个环境的区服名册查（只读）；添加 / 删除选中 / 保存开发者
    ///                写 developers（能上线、能用 GM；和服务端硬编码名单取并集，一分钟内各局生效）
    /// </code>
    ///
    /// 凭证和环境用云存档页的（① / ②）。写之前把这个账号的全部行备份到 <c>data/cloudbackup/</c>，写完重新读一遍核对。
    /// 游戏服务端还没到上线时刻时每 60 秒重读一次，所以设完一分钟内各局生效；<b>已经过了上线时刻</b>的局不再重读，改了要等它们重启。
    /// </summary>
    public class ServerOpenViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly CloudSaveViewModel _cloud;

        /// <summary>最近一次读到的；换了环境就作废</summary>
        private CloudServerOpen? _state;

        public ServerOpenViewModel(CloudSaveViewModel cloud)
        {
            _cloud = cloud;
        }

        /// <summary>当前环境（云存档页 ② 选的）</summary>
        public string EnvText => _cloud.Env == "formal" ? "formal 正式（线上！）" : _cloud.Env;

        /// <summary>要设的上线时间（北京时间，yyyy-MM-dd HH:mm）</summary>
        public string OnlineTimeText { get => _onlineTimeText; set => SetProperty(ref _onlineTimeText, value); }
        private string _onlineTimeText = CloudServerOpen.NowText();

        /// <summary>设上线时间时顺便清空已写下的区开服记录</summary>
        public bool ClearRecords { get => _clearRecords; set => SetProperty(ref _clearRecords, value); }
        private bool _clearRecords;

        /// <summary>开发者表格的一行（UserId 先存文本，保存时再校验）</summary>
        public sealed class DeveloperRow : ObservableObject
        {
            public string UserIdText { get => _userIdText; set => SetProperty(ref _userIdText, value); }
            private string _userIdText = "";

            public string Note { get => _note; set => SetProperty(ref _note, value); }
            private string _note = "";

            /// <summary>游戏昵称（区服名册里查的，只读）</summary>
            public string Nickname { get => _nickname; set => SetProperty(ref _nickname, value); }
            private string _nickname = "";
        }

        /// <summary>开发者表格，读取时填成云端名单</summary>
        public ObservableCollection<DeveloperRow> Developers { get; } = [];

        public DeveloperRow? SelectedDeveloper { get => _selectedDeveloper; set => SetProperty(ref _selectedDeveloper, value); }
        private DeveloperRow? _selectedDeveloper;

        /// <summary>这个环境区服名册里的昵称：UserId → 昵称（读取时查一次）</summary>
        private Dictionary<long, string> _nicknames = [];

        public string Output { get => _output; private set => SetProperty(ref _output, value); }
        private string _output = "先点「读取」。凭证与环境用云存档页 ① ② 的。";

        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        private string _statusText = "";

        public bool IsIdle { get => _isIdle; private set => SetProperty(ref _isIdle, value); }
        private bool _isIdle = true;

        public AsyncRelayCommand Cmd_Load => _cmd_Load ??= new(() => RunAsync("读取", LoadAsync));
        private AsyncRelayCommand? _cmd_Load;

        public RelayCommand Cmd_FillNow => _cmd_FillNow ??= new(() => OnlineTimeText = CloudServerOpen.NowText());
        private RelayCommand? _cmd_FillNow;

        public AsyncRelayCommand Cmd_SetOnline => _cmd_SetOnline ??= new(SetOnlineAsync);
        private AsyncRelayCommand? _cmd_SetOnline;

        public AsyncRelayCommand Cmd_UnsetOnline => _cmd_UnsetOnline ??= new(UnsetOnlineAsync);
        private AsyncRelayCommand? _cmd_UnsetOnline;

        public AsyncRelayCommand Cmd_SaveDevelopers => _cmd_SaveDevelopers ??= new(SaveDevelopersAsync);
        private AsyncRelayCommand? _cmd_SaveDevelopers;

        public RelayCommand Cmd_AddDeveloper => _cmd_AddDeveloper ??= new(() =>
        {
            DeveloperRow row = Track(new DeveloperRow());
            Developers.Add(row);
            SelectedDeveloper = row;
        });
        private RelayCommand? _cmd_AddDeveloper;

        public RelayCommand Cmd_RemoveDeveloper => _cmd_RemoveDeveloper ??= new(() =>
        {
            if (SelectedDeveloper is not null)
                Developers.Remove(SelectedDeveloper);
        });
        private RelayCommand? _cmd_RemoveDeveloper;

        /// <summary>改了这一行的 UserId 就重查昵称</summary>
        private DeveloperRow Track(DeveloperRow row)
        {
            FillNickname(row);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeveloperRow.UserIdText))
                    FillNickname(row);
            };
            return row;
        }

        /// <summary>按 UserId 填一行的游戏昵称</summary>
        private void FillNickname(DeveloperRow row) =>
            row.Nickname = long.TryParse(row.UserIdText.Trim(), out long id) && _nicknames.TryGetValue(id, out string? name)
                ? (name.Length > 0 ? name : "（无昵称）")
                : "（这个环境没进过区）";

        private async Task<string> LoadAsync(CloudAdminToken token)
        {
            List<CloudSaveRow> rows = await CloudAdminApi.QueryAsync(token, CloudServerOpen.ContainerUserId, _cloud.Env);
            _state = CloudServerOpen.Build(_cloud.Env, rows);

            // 昵称查这个环境的全量区服名册（虚拟账号 9999994），只给表格看是谁
            List<CloudSaveRow> roster = await CloudAdminApi.QueryAsync(token, CloudPlayerRoster.ServerRosterUserId, _cloud.Env);
            _nicknames = CloudPlayerRoster.Build(_cloud.Env, roster, [], DateTimeOffset.Now).Players.ToDictionary(p => p.UserId, p => p.Nickname);

            Developers.Clear();
            foreach (CloudServerOpen.Developer d in _state.Developers)
            {
                Developers.Add(Track(new DeveloperRow { UserIdText = d.UserId.ToString(), Note = d.Note }));
            }

            OnPropertyChanged(nameof(EnvText));
            Output = _state.Describe();
            return "生效的上线时间 " + CloudServerOpen.Format(_state.EffectiveOnlineMs);
        }

        private Task SetOnlineAsync()
        {
            if (CloudServerOpen.ParseTime(OnlineTimeText) is not long onlineMs)
            {
                StatusText = $"✗ 时间写法不对，要写成 {CloudServerOpen.TimeFormat}（北京时间），例 2026-11-01 10:00";
                return Task.CompletedTask;
            }

            if (!CheckLoaded())
                return Task.CompletedTask;

            CloudServerOpen state = _state!;
            string extra = ClearRecords ? $"\n同时清空已写下的 {state.Records.Count} 个区开服记录（各区按新上线时间重算）。" : "";
            if (!Confirm($"把 {state.Env} 环境的上线时间设为 {CloudServerOpen.Format(onlineMs)}（北京时间）。{extra}"))
                return Task.CompletedTask;

            return RunAsync("设上线时间", token => WriteAsync(token, state, onlineMs, ClearRecords));
        }

        private Task UnsetOnlineAsync()
        {
            if (!CheckLoaded())
                return Task.CompletedTask;

            CloudServerOpen state = _state!;
            if (!Confirm($"取消 {state.Env} 环境 AzKit 设的上线时间（改回未定）。\n开发者登录写的那条不动：它有值就按它算，没有就所有区未开服。"))
                return Task.CompletedTask;

            return RunAsync("取消上线时间", token => WriteAsync(token, state, 0, false));
        }

        private Task SaveDevelopersAsync()
        {
            List<CloudServerOpen.Developer> ids = [];
            foreach (DeveloperRow row in Developers)
            {
                string text = row.UserIdText.Trim();
                if (text.Length == 0)
                    continue;

                if (!long.TryParse(text, out long id) || id <= 0)
                {
                    StatusText = $"✗ 「{text}」不是 UserId（要填正整数）";
                    return Task.CompletedTask;
                }

                if (ids.Exists(d => d.UserId == id))
                {
                    StatusText = $"✗ UserId {id} 填了两行";
                    return Task.CompletedTask;
                }

                ids.Add(new CloudServerOpen.Developer(id, row.Note.Trim()));
            }

            if (_state is null || _state.Env != _cloud.Env)
            {
                StatusText = "✗ 先点「读取」（换过环境也要重新读）。";
                return Task.CompletedTask;
            }

            if (_state.DevelopersRow is null)
            {
                StatusText = "✗ 这个环境还没有开发者名单那一行：先在这个环境开一局（服务端启动时自动建占位行），再来设。";
                return Task.CompletedTask;
            }

            CloudServerOpen state = _state;
            string list = ids.Count == 0 ? "（清空）" : string.Join(", ", ids.Select(d => d.Note.Length > 0 ? $"{d.UserId}（{d.Note}）" : d.UserId.ToString()));
            if (!Confirm($"把 {state.Env} 环境的开发者名单设为：{list}\n开发者能写上线时间、能用 GM；和服务端硬编码的名单取并集。"))
                return Task.CompletedTask;

            return RunAsync("保存开发者", async token =>
            {
                string backup = Backup(state);
                await CloudAdminApi.EditAsync(token, state.DevelopersRow!, CloudServerOpen.DevelopersValue(ids));
                _logger.Info("[ServerOpen] {0} 环境开发者名单设为 {1}，备份 {2}", state.Env, list, backup);
                await LoadAsync(token);
                Output += $"\n\n备份：{backup}\n各局一分钟内生效（服务端每 60 秒重读开发者名单）。";
                return "开发者名单已保存：" + list;
            });
        }

        /// <summary>备份 → 写 serveronline_set →（可选）清区开服记录 → 重新读核对</summary>
        private async Task<string> WriteAsync(CloudAdminToken token, CloudServerOpen state, long onlineMs, bool clearRecords)
        {
            string backup = Backup(state);
            await CloudAdminApi.EditAsync(token, state.ManualRow!, CloudServerOpen.ManualValue(onlineMs));

            int cleared = 0;
            if (clearRecords)
            {
                foreach (CloudServerOpen.OpenRecord record in state.Records)
                {
                    await CloudAdminApi.ClearAsync(token, record.Row);
                    cleared++;
                }
            }

            _logger.Info("[ServerOpen] {0} 环境上线时间设为 {1}，清空区开服记录 {2} 个，备份 {3}", state.Env, onlineMs, cleared, backup);

            await LoadAsync(token);
            Output += $"\n\n备份：{backup}\n游戏服务端还没到上线时刻的局一分钟内读到；已过上线时刻的局要重启才生效。";
            return (onlineMs > 0 ? "已设为 " + CloudServerOpen.Format(onlineMs) : "已取消") + (cleared > 0 ? $"，清空 {cleared} 个区开服记录" : "");
        }

        /// <summary>写之前必须先读过、且是当前环境、且有那一行</summary>
        private bool CheckLoaded()
        {
            if (_state is null || _state.Env != _cloud.Env)
            {
                StatusText = "✗ 先点「读取」（换过环境也要重新读）。";
                return false;
            }

            if (_state.ManualRow is null)
            {
                StatusText = "✗ 这个环境还没有上线时间那一行：先在这个环境开一局（服务端启动时自动建占位行），再来设。";
                return false;
            }

            return true;
        }

        private bool Confirm(string text)
        {
            text += "\n\n写之前会把这个账号的全部行备份到 data\\cloudbackup。";
            if (_cloud.Env == "formal")
                text = "‼ 这是正式环境 formal，改的是线上的开服时间！\n\n" + text;
            return MessagePopupService.OKCancel(text, "确认开服设置");
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
            _cloud.SetOperationLabel("开服设置 · " + label);
            try
            {
                StatusText = "✓ " + label + "：" + await action(token) + $"（{DateTime.Now:HH:mm:ss}）";
            }
            catch (CloudAdminException e)
            {
                _logger.Warn("[ServerOpen] {0} 失败：{1}", label, e.Message);
                StatusText = "✗ " + label + "：" + e.Message;
            }
            finally
            {
                IsIdle = true;
            }
        }

        /// <summary>备份成文件（与云存档页删档前的备份同格式），返回路径</summary>
        private static string Backup(CloudServerOpen state)
        {
            JsonArray rows = [];
            foreach (CloudSaveRow row in state.Rows)
                rows.Add(row.ToJson());

            JsonObject root = new()
            {
                ["userId"] = CloudServerOpen.ContainerUserId.ToString(),
                ["env"] = state.Env,
                ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["rows"] = rows,
            };

            Directory.CreateDirectory(CloudSaveViewModel.BackupDirectory);
            string path = Path.Combine(CloudSaveViewModel.BackupDirectory, $"{CloudServerOpen.ContainerUserId}_{state.Env}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return path;
        }
    }
}
