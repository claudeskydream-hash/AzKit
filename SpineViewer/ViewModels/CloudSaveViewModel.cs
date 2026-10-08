using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using SpineViewer.Utils;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 左侧栏「云存档」页：查询、删除 AzureSail 玩家的星火云存档。
    ///
    /// <code>
    ///   ① 粘贴浏览器整段 Cookie → 解析出 token；勾「记住凭证」就加密存本机（CloudTokenStore），下次自动载入
    ///   ② 账号 userId + 环境（debug 编辑器 / test 测试 / formal 正式）
    ///   ③ 查看概况：按区服列出；删除所有区服存档；删除选中区
    /// </code>
    ///
    /// <b>删档 = 把值改成空字符串</b>（后台不让删普通 key），游戏服务端读到空值按新档重建。
    /// 删之前把该账号该环境的全部存档备份到 <c>data/cloudbackup/</c>（格式同 EditCloudStore 的快照，可用它的 update 恢复），
    /// 删完重新查询核对。原先做在游戏 GM 面板里，星火沙箱不让游戏发 HTTP，所以挪到这里。
    ///
    /// 界面分两处：左侧栏「云存档」页放凭证、账号、操作与区服列表（CloudSavePanel）；
    /// 右侧「存档」页放状态监测、结果与选中区的各段存档（CloudSaveDetailPanel）。一有操作就把右侧切到「存档」页。
    /// </summary>
    public class CloudSaveViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>界面设置（账号、环境、是否记住凭证）保存位置；token 本身另存加密文件</summary>
        private static readonly string SettingsPath = Path.Combine(App.DataDirectory, "cloudsave.json");

        /// <summary>删除前备份目录</summary>
        public static readonly string BackupDirectory = Path.Combine(App.DataDirectory, "cloudbackup");

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>右侧「存档」页在右侧标签页里的序号（动画预览 0、预览图 1、存档 2）</summary>
        private const int DetailTabIndex = 2;

        /// <summary>请求记录最多留几条</summary>
        private const int MaxCallRows = 200;

        private readonly MainWindowViewModel _vmMain;

        private CloudAdminToken? _token;

        /// <summary>最近一次查询的结果；换了账号或环境就作废</summary>
        private CloudSaveSummary? _summary;

        /// <summary>正在做的操作（进请求记录的「操作」列）</summary>
        private string _operation = "";

        public CloudSaveViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            LoadSettings();
            CloudAdminApi.CallCompleted += OnApiCall;
            LoadSavedToken();
        }

        /// <summary>区服列表的一行</summary>
        public sealed class ServerRow(CloudSaveSummary.ServerGroup group)
        {
            public CloudSaveSummary.ServerGroup Group { get; } = group;

            public int ServerId => Group.ServerId;

            public string Title => Group.ServerId + " 区";

            public string Info => Group.FilledCount == 0
                ? $"已清空（{Group.Rows.Count} 段）"
                : $"{Group.FilledCount}/{Group.Rows.Count} 段有数据 · {FormatSize(Group.TotalSize)}";
        }

        /// <summary>选中区的一段存档</summary>
        public sealed class KeyRow(CloudSaveRow row)
        {
            public CloudSaveRow Row { get; } = row;

            public string Key => Row.Key;

            public string Size => Row.IsEmpty ? "空" : FormatSize(Row.Size);

            /// <summary>值的开头一截，表格里一行看个大概</summary>
            public string Preview
            {
                get
                {
                    if (Row.IsEmpty) return "";
                    string text = Unwrap(Row.Value).Replace('\n', ' ').Replace('\r', ' ');
                    return text.Length <= 120 ? text : text[..120] + "…";
                }
            }
        }

        /// <summary>状态监测里的一条请求记录</summary>
        public sealed class CallRow(CloudApiCall call, string operation)
        {
            public string Time => call.Time.ToString("HH:mm:ss");

            public string Operation { get; } = operation;

            public string Api => call.Api;

            public string Http => call.HttpStatus == 0 ? "—" : call.HttpStatus.ToString();

            public string Result => call.Ok ? call.Message : (call.ResultCode.Length > 0 ? call.ResultCode + " · " : "") + call.Message;

            public string Elapsed => call.ElapsedMs == 0 ? "" : call.ElapsedMs + " ms";

            public bool Ok => call.Ok;
        }

        // =============== ① 凭证 ===============

        /// <summary>粘贴进来的 Cookie / token 原文；解析成功后清空，不留在屏幕上</summary>
        public string CookieText { get => _cookieText; set => SetProperty(ref _cookieText, value); }
        private string _cookieText = "";

        public string TokenStatus { get => _tokenStatus; set => SetProperty(ref _tokenStatus, value); }
        private string _tokenStatus = "未解析";

        /// <summary>解析成功后把 token 加密存本机，下次打开自动载入</summary>
        public bool RememberToken
        {
            get => _rememberToken;
            set
            {
                if (!SetProperty(ref _rememberToken, value)) return;
                if (value && _token is not null)
                    CloudTokenStore.Save(_token.Token);
                else if (!value)
                    CloudTokenStore.Delete();
                SaveSettings();
            }
        }
        private bool _rememberToken = true;

        public RelayCommand Cmd_ParseToken => _cmd_ParseToken ??= new(ParseToken);
        private RelayCommand? _cmd_ParseToken;

        public RelayCommand Cmd_ForgetToken => _cmd_ForgetToken ??= new(() =>
        {
            CloudTokenStore.Delete();
            _token = null;
            TokenStatus = "未解析（已清除本机记住的凭证）";
            AddLocalRecord("清除凭证", true, "已删除本机加密保存的 token");
        });
        private RelayCommand? _cmd_ForgetToken;

        private void ParseToken()
        {
            CloudAdminToken? token = CloudAdminToken.Parse(CookieText, out string error);
            if (token is null)
            {
                TokenStatus = "✗ 解析失败：" + error;
                AddLocalRecord("解析凭证", false, error);
                return;
            }

            _token = token;
            CookieText = "";
            if (RememberToken)
                CloudTokenStore.Save(token.Token);

            ShowTokenStatus(token, RememberToken ? "已解析并加密保存到本机" : "已解析（未保存）");
            AddLocalRecord("解析凭证", !token.IsExpired, token.IsExpired ? "token 已过期" : $"后台账号 {token.AccountName}，token {token.Masked}");
            _logger.Info("[CloudSave] 凭证已解析：后台账号 {0}，token {1}，到期 {2}", token.AccountName, token.Masked, token.ExpiresAt);
        }

        /// <summary>启动时载入本机记住的 token</summary>
        private void LoadSavedToken()
        {
            if (!RememberToken || CloudTokenStore.Load() is not { } saved)
                return;

            CloudAdminToken? token = CloudAdminToken.Parse(saved, out _);
            if (token is null)
            {
                CloudTokenStore.Delete();
                return;
            }

            _token = token;
            ShowTokenStatus(token, "已载入本机记住的凭证");
            _logger.Info("[CloudSave] 已载入本机记住的凭证：后台账号 {0}，token {1}，到期 {2}", token.AccountName, token.Masked, token.ExpiresAt);
        }

        private void ShowTokenStatus(CloudAdminToken token, string source)
        {
            string expires = token.ExpiresAt is { } at ? at.ToString("MM-dd HH:mm") : "未知";
            TokenStatus = (token.IsExpired ? $"✗ 已过期（{expires}），请重新复制 Cookie 解析" : $"✓ {source}，有效期至 {expires}")
                + $"\n后台账号 {(token.AccountName.Length > 0 ? token.AccountName : "未知")} · token {token.Masked}";
        }

        // =============== ② 账号与环境 ===============

        public string UserIdText
        {
            get => _userIdText;
            set { if (SetProperty(ref _userIdText, value)) Invalidate("账号已改，请重新查询"); }
        }
        private string _userIdText = "100";

        /// <summary>当前环境：debug / test / formal</summary>
        public string Env
        {
            get => _env;
            private set
            {
                if (!SetProperty(ref _env, value)) return;
                OnPropertyChanged(nameof(IsDebug));
                OnPropertyChanged(nameof(IsTest));
                OnPropertyChanged(nameof(IsFormal));
                Invalidate($"环境已切换为 {value}，请重新查询");
            }
        }
        private string _env = "debug";

        public bool IsDebug { get => Env == "debug"; set { if (value) Env = "debug"; } }

        public bool IsTest { get => Env == "test"; set { if (value) Env = "test"; } }

        public bool IsFormal { get => Env == "formal"; set { if (value) Env = "formal"; } }

        // =============== 状态监测 ===============

        /// <summary>当前状态一行字</summary>
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        private string _statusText = "○ 空闲";

        /// <summary>状态种类，决定颜色：idle 空闲 / busy 进行中 / ok 成功 / error 失败</summary>
        public string StatusKind { get => _statusKind; private set => SetProperty(ref _statusKind, value); }
        private string _statusKind = "idle";

        /// <summary>请求记录，最新的在最上面</summary>
        public ObservableCollection<CallRow> Calls { get; } = [];

        public RelayCommand Cmd_ClearCalls => _cmd_ClearCalls ??= new(() => Calls.Clear());
        private RelayCommand? _cmd_ClearCalls;

        private void BeginOperation(string label)
        {
            _operation = label;
            StatusKind = "busy";
            StatusText = "● 进行中：" + label;
        }

        private void UpdateOperation(string detail) => StatusText = "● 进行中：" + _operation + " · " + detail;

        private void EndOperation(bool ok, string summary)
        {
            StatusKind = ok ? "ok" : "error";
            StatusText = $"{(ok ? "✓" : "✗")} {_operation}：{summary}（{DateTime.Now:HH:mm:ss}）";
        }

        /// <summary>每次后台请求结束都记一条（CloudAdminApi.CallCompleted）</summary>
        private void OnApiCall(CloudApiCall call)
        {
            Calls.Insert(0, new CallRow(call, _operation));
            while (Calls.Count > MaxCallRows)
                Calls.RemoveAt(Calls.Count - 1);
        }

        /// <summary>不访问后台的动作（解析、清除凭证、前置检查没过）也记一条</summary>
        private void AddLocalRecord(string operation, bool ok, string message)
        {
            Calls.Insert(0, new CallRow(new CloudApiCall(DateTime.Now, "本地", 0, "", message, 0, ok), operation));
            if (!ok)
            {
                _operation = operation;
                EndOperation(false, message);
            }
        }

        // =============== ③ 查询与删除 ===============

        public ObservableCollection<ServerRow> Servers { get; } = [];

        public ServerRow? SelectedServer
        {
            get => _selectedServer;
            set
            {
                if (!SetProperty(ref _selectedServer, value)) return;
                SelectedKeys.Clear();
                SelectedKey = null;
                if (value is not null)
                {
                    foreach (CloudSaveRow row in value.Group.Rows.OrderBy(r => r.Key, StringComparer.Ordinal))
                        SelectedKeys.Add(new KeyRow(row));
                    ShowDetailTab();
                }
            }
        }
        private ServerRow? _selectedServer;

        /// <summary>右侧表格里选中的一段存档</summary>
        public KeyRow? SelectedKey
        {
            get => _selectedKey;
            set
            {
                if (SetProperty(ref _selectedKey, value))
                    OnPropertyChanged(nameof(SelectedKeyValue));
            }
        }
        private KeyRow? _selectedKey;

        /// <summary>选中那段存档的完整内容（JSON 格式化后）</summary>
        public string SelectedKeyValue
        {
            get
            {
                if (SelectedKey is null) return "在上面的表格里点一段存档，这里显示它的完整内容。";
                if (SelectedKey.Row.IsEmpty) return SelectedKey.Key + "：空（已清空，游戏下次进入按新档重建）";
                string text = Unwrap(SelectedKey.Row.Value);
                try
                {
                    return JsonNode.Parse(text)?.ToJsonString(WriteOptions) ?? text;
                }
                catch (JsonException)
                {
                    return text;
                }
            }
        }

        /// <summary>选中区的各段存档</summary>
        public ObservableCollection<KeyRow> SelectedKeys { get; } = [];

        public string Output { get => _output; set => SetProperty(ref _output, value); }
        private string _output = "先粘贴 Cookie 并点「解析」，再点「查看概况」。";

        public bool IsIdle { get => _isIdle; private set => SetProperty(ref _isIdle, value); }
        private bool _isIdle = true;

        public AsyncRelayCommand Cmd_Query => _cmd_Query ??= new(() => RunAsync("查看概况", QueryAsync));
        private AsyncRelayCommand? _cmd_Query;

        public AsyncRelayCommand Cmd_ClearAll => _cmd_ClearAll ??= new(ClearAllAsync);
        private AsyncRelayCommand? _cmd_ClearAll;

        public AsyncRelayCommand Cmd_ClearServer => _cmd_ClearServer ??= new(ClearServerAsync);
        private AsyncRelayCommand? _cmd_ClearServer;

        public RelayCommand Cmd_OpenBackupFolder => _cmd_OpenBackupFolder ??= new(() =>
        {
            Directory.CreateDirectory(BackupDirectory);
            Process.Start(new ProcessStartInfo(BackupDirectory) { UseShellExecute = true });
        });
        private RelayCommand? _cmd_OpenBackupFolder;

        /// <summary>查询并刷新列表，返回给状态栏的成败与一句话</summary>
        private async Task<(bool Ok, string Summary)> QueryAsync(CloudAdminToken token, long userId)
        {
            List<CloudSaveRow> rows = await CloudAdminApi.QueryAsync(token, userId, Env);
            int keepServer = SelectedServer?.ServerId ?? 0;

            _summary = CloudSaveSummary.Build(userId, Env, rows);
            Servers.Clear();
            foreach (CloudSaveSummary.ServerGroup group in _summary.Servers)
                Servers.Add(new ServerRow(group));
            SelectedServer = Servers.FirstOrDefault(s => s.ServerId == keepServer);

            SaveSettings();
            Output = _summary.Describe();
            return (true, $"共 {rows.Count} 行，{_summary.Servers.Count} 个区");
        }

        private Task ClearAllAsync()
        {
            if (_summary is null)
            {
                Output = "先点「查看概况」，确认是哪个账号、有哪些区，再删。";
                AddLocalRecord("删除所有区服存档", false, "还没查询");
                return Task.CompletedTask;
            }

            CloudSaveSummary target = _summary;
            if (!ConfirmClear($"账号 {target.UserId}（{target.Env}）全部 {target.Servers.Count} 个区的存档 + 网关段"))
                return Task.CompletedTask;

            return RunAsync($"删除 {target.UserId} 全部区服存档", (token, _) => ClearAsync(token, target, target.RowsOfAllServers(), "全部区服存档"));
        }

        private Task ClearServerAsync()
        {
            if (_summary is null || SelectedServer is null)
            {
                Output = _summary is null ? "先点「查看概况」，再在列表里选一个区。" : "先在区服列表里选一个区。";
                AddLocalRecord("删除选中区", false, _summary is null ? "还没查询" : "没选区");
                return Task.CompletedTask;
            }

            CloudSaveSummary target = _summary;
            int serverId = SelectedServer.ServerId;
            if (!ConfirmClear($"账号 {target.UserId}（{target.Env}）{serverId} 区的全部数据"))
                return Task.CompletedTask;

            return RunAsync($"删除 {target.UserId} 的 {serverId} 区", (token, _) => ClearAsync(token, target, target.RowsOfServer(serverId), serverId + " 区"));
        }

        /// <summary>清空若干行：先把整个账号的存档备份成文件，再逐行置空，最后重新查询核对。返回给状态栏的成败与一句话</summary>
        private async Task<(bool Ok, string Summary)> ClearAsync(CloudAdminToken token, CloudSaveSummary target, List<CloudSaveRow> rows, string what)
        {
            List<CloudSaveRow> todo = rows.FindAll(r => !r.IsEmpty);
            if (todo.Count == 0)
            {
                Output = what + "已经是空的，不用删。";
                return (true, "已经是空的，没有写");
            }

            UpdateOperation("备份中");
            string backup = Backup(target);

            int done = 0;
            List<string> failed = [];
            foreach (CloudSaveRow row in todo)
            {
                UpdateOperation($"{done + failed.Count + 1}/{todo.Count}（{row.Key}）");
                Output = $"正在清空 {what}：{done + failed.Count + 1}/{todo.Count}（{row.Key}）";
                try
                {
                    await CloudAdminApi.ClearAsync(token, row);
                    done++;
                }
                catch (CloudAdminException e)
                {
                    failed.Add(row.Key + "：" + e.Message);
                }
            }

            _logger.Info("[CloudSave] 账号 {0}（{1}）清空{2}：成功 {3}，失败 {4}，备份 {5}", target.UserId, target.Env, what, done, failed.Count, backup);

            // 重新查一遍，界面上看到的就是后台的真实状态
            UpdateOperation("回查核对");
            await QueryAsync(token, target.UserId);

            Output = $"清空{what}：成功 {done} 段" + (failed.Count > 0 ? $"，失败 {failed.Count} 段\n" + string.Join("\n", failed) : "")
                + $"\n备份：{backup}\n" + (_summary?.Describe() ?? "");

            return failed.Count == 0
                ? (true, $"成功 {done} 段，备份已存")
                : (false, $"成功 {done} 段，失败 {failed.Count} 段（详见结果）");
        }

        /// <summary>删除确认：正式环境额外强调</summary>
        private bool ConfirmClear(string description)
        {
            string text = $"将清空 {description}。\n\n删之前会把这个账号的全部存档备份到 data\\cloudbackup。\n"
                + "如果这个账号此刻正在游戏里，服务端缓存可能在落盘时把旧数据写回 —— 先退出游戏再删。";
            if (Env == "formal")
                text = "‼ 这是正式环境 formal，删的是线上玩家数据！\n\n" + text;
            return MessagePopupService.OKCancel(text, "确认删档");
        }

        /// <summary>备份成文件（与 EditCloudStore 快照同格式），返回路径</summary>
        private static string Backup(CloudSaveSummary target)
        {
            JsonArray rows = [];
            foreach (CloudSaveRow row in target.Rows)
                rows.Add(row.ToJson());

            JsonObject root = new()
            {
                ["userId"] = target.UserId.ToString(),
                ["env"] = target.Env,
                ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["rows"] = rows,
            };

            Directory.CreateDirectory(BackupDirectory);
            string path = Path.Combine(BackupDirectory, $"{target.UserId}_{target.Env}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return path;
        }

        // =============== 共用 ===============

        /// <summary>
        /// 跑一个要访问后台的动作：先检查凭证与账号，期间锁住界面、状态栏显示进行中，
        /// 结束后状态栏给出成败与一句话，异常转成结果里的一句话。
        /// </summary>
        private async Task RunAsync(string label, Func<CloudAdminToken, long, Task<(bool Ok, string Summary)>> action)
        {
            ShowDetailTab();

            if (_token is null)
            {
                Output = "先在 ① 粘贴 Cookie 并点「解析」。";
                AddLocalRecord(label, false, "还没有凭证");
                return;
            }

            if (_token.IsExpired)
            {
                Output = "token 已过期，重新登录星火后台复制 Cookie 再解析。";
                AddLocalRecord(label, false, "token 已过期");
                return;
            }

            if (!long.TryParse(UserIdText.Trim(), out long userId) || userId <= 0)
            {
                Output = "账号 userId 要填数字（编辑器默认账号是 100）。";
                AddLocalRecord(label, false, "userId 不是数字");
                return;
            }

            IsIdle = false;
            BeginOperation($"{label}（账号 {userId}，{Env}）");
            Output = $"请求后台中……（账号 {userId}，{Env}）";
            try
            {
                (bool ok, string summary) = await action(_token, userId);
                EndOperation(ok, summary);
            }
            catch (CloudAdminException e)
            {
                _logger.Warn("[CloudSave] {0} 失败：{1}", label, e.Message);
                Output = "✗ " + e.Message;
                EndOperation(false, e.Message);
            }
            finally
            {
                IsIdle = true;
            }
        }

        /// <summary>作废查询结果（换了账号 / 环境），免得删到别的号</summary>
        private void Invalidate(string message)
        {
            if (_summary is null) return;
            _summary = null;
            Servers.Clear();
            SelectedServer = null;
            Output = message;
        }

        /// <summary>把右侧切到「存档」页</summary>
        private void ShowDetailTab() => _vmMain.ViewTabIndex = DetailTabIndex;

        /// <summary>存档值本身是"JSON 序列化成的字符串"，取出里面那层文本；不是字符串就原样输出 JSON</summary>
        private static string Unwrap(JsonNode? value) =>
            value is JsonValue v && v.TryGetValue(out string? text) ? text : value?.ToJsonString() ?? "";

        private static string FormatSize(int bytes) => bytes < 1024 ? bytes + " B" : (bytes / 1024f).ToString("0.0") + " KB";

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                JsonNode? root = JsonNode.Parse(File.ReadAllText(SettingsPath));
                _userIdText = root?["UserId"]?.ToString() ?? _userIdText;
                _env = root?["Env"]?.ToString() is "debug" or "test" or "formal" ? root!["Env"]!.ToString() : _env;
                _rememberToken = root?["RememberToken"] is JsonValue remember && remember.TryGetValue(out bool flag) ? flag : _rememberToken;
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                _logger.Warn("[CloudSave] 读设置失败：{0}", e.Message);
            }
        }

        private void SaveSettings()
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(SettingsPath, new JsonObject
            {
                ["UserId"] = UserIdText.Trim(),
                ["Env"] = Env,
                ["RememberToken"] = RememberToken,
            }.ToJsonString(WriteOptions));
        }
    }
}
