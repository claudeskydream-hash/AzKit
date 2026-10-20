using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using SpineViewer.Services;
using SpineViewer.Utils;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;

namespace SpineViewer.ViewModels
{
    /// <summary>
    /// [AzureSail 新增] 「线上日志」窗口：从星火后台「开发者工具」各页拉线上运行的日志与统计（凭证与云存档页共用）。
    ///
    /// <code>
    ///   开局记录  firm0_app_query_game_session  按 userId（可空 = 全部玩家）+ 时间段列出每一局；
    ///   异常局    firm0_abnormal_game_list        同上，多一列异常原因
    ///             行操作 lua_view_log = 这一局<b>完整的服务端日志</b>（Game.Logger 打的全部，同局所有玩家都在里面）
    ///   报错统计  firm0_app_query_server_log / firm0_app_query_client_log  按内容聚合的报错 + 次数
    ///   自定义统计 firm0_app_event_detail          游戏主动上报的事件（按环境 / 事件名 / userId）
    ///   玩家上传日志 firm0_app_log_download        玩家在游戏里点「上传日志」的整包客户端日志（OSS 上的 zip），下载解压到 data/playerlogs
    /// </code>
    ///
    /// 实测（2026-10-08）：编辑器调试（debug）开的局<b>不进</b>开局记录，只有 test（内测）/ formal（正式）有。
    /// 「开发日志」2026-10-09 实测已能查（内测环境），即玩家上传日志；「使发送日志」仍没接。
    /// 表与字段是从后台 /api/v1/layout 读出来的页面结构，后台改版时以那里为准。
    /// </summary>
    public class OnlineLogViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const string SessionTable = "firm0_app_query_game_session";
        private const string AbnormalTable = "firm0_abnormal_game_list";
        private const string ServerErrorTable = "firm0_app_query_server_log";
        private const string ClientErrorTable = "firm0_app_query_client_log";
        private const string EventTable = "firm0_app_event_detail";

        /// <summary>开局记录的行操作「lua日志」；行里没带操作时用它</summary>
        private const string ViewLogFunctor = "lua_view_log";

        /// <summary>每页条数</summary>
        private const int PageSize = 50;

        private readonly CloudSaveViewModel _cloud;

        /// <summary>已拉过的整局日志：session_id → 全文（同一局重复点不再请求）</summary>
        private readonly Dictionary<string, string> _logCache = [];

        public OnlineLogViewModel(CloudSaveViewModel cloud)
        {
            _cloud = cloud;
            _eventEnv = cloud.Env == "debug" ? "test" : cloud.Env;
        }

        // =============== 共用 ===============

        /// <summary>时间段可选天数</summary>
        public int[] DayOptions { get; } = [1, 3, 7, 15, 30];

        /// <summary>查最近几天（三个标签页共用）</summary>
        public int Days { get => _days; set => SetProperty(ref _days, value); }
        private int _days = 7;

        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        private string _statusText = "选好条件点「查询」。凭证用云存档页 ① 里的。";

        public bool IsIdle { get => _isIdle; private set => SetProperty(ref _isIdle, value); }
        private bool _isIdle = true;

        /// <summary>一页查询的结果（行 + 总条数），翻页用</summary>
        public sealed class PageState : ObservableObject
        {
            public int Page { get => _page; set { if (SetProperty(ref _page, value)) OnPropertyChanged(nameof(Text)); } }
            private int _page = 1;

            public int Total { get => _total; set { if (SetProperty(ref _total, value)) OnPropertyChanged(nameof(Text)); } }
            private int _total;

            public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

            public string Text => Total == 0 ? "无数据" : $"第 {Page}/{PageCount} 页 · 共 {Total} 条";
        }

        /// <summary>跑一次后台请求：查凭证、锁界面、写状态、异常转成状态文字</summary>
        private async Task RunAsync(string label, Func<CloudAdminToken, Task<string>> action)
        {
            CloudAdminToken? token = _cloud.Token;
            if (token is null || token.IsExpired)
            {
                StatusText = token is null ? "✗ 还没有凭证：先在云存档页 ① 粘贴 Cookie 并解析。" : "✗ token 已过期：重新登录星火后台复制 Cookie，在云存档页 ① 解析。";
                return;
            }

            IsIdle = false;
            StatusText = "● " + label + "……";
            _cloud.SetOperationLabel("线上日志 · " + label);
            try
            {
                StatusText = "✓ " + label + "：" + await action(token) + $"（{DateTime.Now:HH:mm:ss}）";
            }
            catch (CloudAdminException e)
            {
                _logger.Warn("[OnlineLog] {0} 失败：{1}", label, e.Message);
                StatusText = "✗ " + label + "：" + e.Message;
            }
            finally
            {
                IsIdle = true;
            }
        }

        private static JsonObject Filter(string type, string value) => new()
        {
            ["type"] = type,
            ["value"] = new JsonObject { ["data"] = value, ["operator"] = "=" },
        };

        private static string Str(JsonObject row, string key) => row[key] switch
        {
            null => "",
            JsonValue v when v.TryGetValue(out string? s) => s,
            JsonNode n => n.ToJsonString().Trim('"'),
        };

        /// <summary>行上第一个操作按钮的 functor（后台给每行带了 action 数组）</summary>
        private static string Functor(JsonObject row) =>
            row["action"] is JsonArray actions && actions.FirstOrDefault()?["functor"]?.ToString() is { Length: > 0 } f ? f : "";

        // =============== ① 开局记录 ===============

        /// <summary>一局</summary>
        public sealed class SessionRow(JsonObject row, string table) : ObservableObject
        {
            /// <summary>后台返回的原始行（缓存索引里原样存它，读回来走同一个构造）</summary>
            public JsonObject Raw { get; } = row;
            public string Table { get; } = table;
            public string Time { get; } = Str(row, "server_timestamp");
            public string UserId { get; } = Str(row, "user_id");
            public string Nickname { get; } = Str(row, "nick_name");
            public string Env { get; } = Str(row, "tag_name");
            public string UserCount { get; } = Str(row, "user_count");
            public string SessionId { get; } = Str(row, "session_id");
            public string RowId { get; } = Str(row, "row_id") is { Length: > 0 } id ? id : Str(row, "session_id");
            public string Reason { get; } = Str(row, "reason");
            public string Functor { get; } = OnlineLogViewModel.Functor(row) is { Length: > 0 } f ? f : ViewLogFunctor;

            /// <summary>日志已缓存到本地</summary>
            public bool IsCached { get => _isCached; set { if (SetProperty(ref _isCached, value)) OnPropertyChanged(nameof(CachedMark)); } }
            private bool _isCached = File.Exists(LogCachePath(Str(row, "session_id")));

            public string CachedMark => IsCached ? "✓" : "";
        }

        /// <summary>userId；空 = 这段时间所有玩家开的局</summary>
        public string SessionUserId { get => _sessionUserId; set => SetProperty(ref _sessionUserId, value); }
        private string _sessionUserId = "";

        /// <summary>只看异常局（异常局记录表）</summary>
        public bool OnlyAbnormal { get => _onlyAbnormal; set => SetProperty(ref _onlyAbnormal, value); }
        private bool _onlyAbnormal;

        public ObservableCollection<SessionRow> Sessions { get; } = [];

        public PageState SessionPage { get; } = new();

        public SessionRow? SelectedSession
        {
            get => _selectedSession;
            set
            {
                if (SetProperty(ref _selectedSession, value) && value is not null)
                    _ = LoadLogAsync(value);
            }
        }
        private SessionRow? _selectedSession;

        public AsyncRelayCommand Cmd_QuerySessions => _cmd_QuerySessions ??= new(() => QuerySessionsAsync(1));
        private AsyncRelayCommand? _cmd_QuerySessions;

        public AsyncRelayCommand Cmd_SessionPrev => _cmd_SessionPrev ??= new(() => QuerySessionsAsync(SessionPage.Page - 1));
        private AsyncRelayCommand? _cmd_SessionPrev;

        public AsyncRelayCommand Cmd_SessionNext => _cmd_SessionNext ??= new(() => QuerySessionsAsync(SessionPage.Page + 1));
        private AsyncRelayCommand? _cmd_SessionNext;

        /// <summary>按翻页记下的条件查，免得改了输入框没点查询就翻页、前后两页对不上</summary>
        private (string UserId, bool Abnormal, int Days) _sessionQuery;

        private Task QuerySessionsAsync(int page)
        {
            if (page == 1)
            {
                string userId = SessionUserId.Trim();
                if (userId.Length > 0 && !long.TryParse(userId, out _))
                {
                    StatusText = "✗ userId 要填数字，留空 = 全部玩家。";
                    return Task.CompletedTask;
                }
                _sessionQuery = (userId, OnlyAbnormal, Days);
            }
            else if (page < 1 || page > SessionPage.PageCount)
            {
                return Task.CompletedTask;
            }

            (string user, bool abnormal, int days) = _sessionQuery;
            string table = abnormal ? AbnormalTable : SessionTable;
            string label = (abnormal ? "查异常局" : "查开局记录") + (user.Length > 0 ? $"（{user}）" : "（全部玩家）");
            return RunAsync(label, async token =>
            {
                JsonArray options = [];
                if (user.Length > 0)
                    options.Add(Filter("user_id", user));
                DateTimeOffset end = DateTimeOffset.Now;
                (List<JsonObject> rows, int total) = await CloudAdminApi.QueryTableAsync(token, table, options, end.AddDays(-days), end, "", page, PageSize);

                Sessions.Clear();
                foreach (JsonObject row in rows)
                    Sessions.Add(new SessionRow(row, table));
                SessionPage.Page = page;
                SessionPage.Total = total;
                return total == 0 ? $"最近 {days} 天没有{(abnormal ? "异常局" : "开局记录")}（编辑器调试的局不进后台）" : $"共 {total} 局，点一局看它的服务端日志";
            });
        }

        // ---- 整局日志 ----

        /// <summary>当前这局日志的全文</summary>
        private string _logText = "";

        /// <summary>日志标题：哪一局</summary>
        public string LogTitle { get => _logTitle; private set => SetProperty(ref _logTitle, value); }
        private string _logTitle = "服务端日志（在上面点一局）";

        /// <summary>只显示含这个词的条目（不分大小写）</summary>
        public string LogFilter { get => _logFilter; set { if (SetProperty(ref _logFilter, value)) RefreshLog(); } }
        private string _logFilter = "";

        /// <summary>只看 warn / error / fatal</summary>
        public bool LogOnlyProblems { get => _logOnlyProblems; set { if (SetProperty(ref _logOnlyProblems, value)) RefreshLog(); } }
        private bool _logOnlyProblems;

        /// <summary>过滤后显示的文字</summary>
        public string LogView { get => _logView; private set => SetProperty(ref _logView, value); }
        private string _logView = "";

        public string LogInfo { get => _logInfo; private set => SetProperty(ref _logInfo, value); }
        private string _logInfo = "";

        public RelayCommand Cmd_CopyLog => _cmd_CopyLog ??= new(() =>
        {
            if (LogView.Length > 0) Clipboard.SetText(LogView);
        });
        private RelayCommand? _cmd_CopyLog;

        public RelayCommand Cmd_SaveLog => _cmd_SaveLog ??= new(() =>
        {
            if (_logText.Length == 0 || SelectedSession is null) return;
            string? path = $"{SelectedSession.Time.Replace(':', '-').Replace(' ', '_')}_{SelectedSession.UserId}_{SelectedSession.SessionId}.log";
            if (!DialogService.ShowSaveLogDialog(ref path) || path is null) return;
            File.WriteAllText(path, _logText, new UTF8Encoding(false));
            StatusText = "✓ 已另存为 " + path;
        });
        private RelayCommand? _cmd_SaveLog;

        /// <summary>先内存、再本地缓存文件，都没有才去后台拉；<paramref name="refetch"/> = 强制重拉并覆盖缓存</summary>
        private async Task LoadLogAsync(SessionRow session, bool refetch = false)
        {
            LogTitle = $"服务端日志 · {session.Time} · {session.Nickname}（{session.UserId}）· session {session.SessionId}";
            if (!refetch)
            {
                string? cached = _logCache.GetValueOrDefault(session.SessionId) ?? ReadLogCache(session.SessionId);
                if (cached is not null)
                {
                    _logCache[session.SessionId] = cached;
                    ShowLog(cached);
                    StatusText = "✓ 读的本地缓存（不访问后台）；那一局拉取时若还没结束，点「重新拉取」取最新";
                    return;
                }
            }

            ShowLog("");
            LogInfo = "拉取中……";
            await RunAsync($"拉 {session.SessionId} 的服务端日志", async token =>
            {
                string text = await CloudAdminApi.RowActionAsync(token, session.Table, session.RowId, session.Functor);
                _logCache[session.SessionId] = text;
                WriteLogCache(session, text);
                // 拉的途中又点了别的局，就不覆盖
                if (SelectedSession == session)
                    ShowLog(text);
                return $"{text.Length / 1024f:0.0} KB，已缓存到本地";
            });
        }

        // ---- 本地缓存（data/onlinelogs：每局一个 .log + 一份索引） ----

        /// <summary>缓存目录</summary>
        public static readonly string LogCacheDirectory = Path.Combine(App.DataDirectory, "onlinelogs");

        /// <summary>索引：session_id → { table, row（后台原始行）, cachedAt }，「本地缓存」列表靠它</summary>
        private static string LogIndexPath => Path.Combine(LogCacheDirectory, "index.json");

        private static string LogCachePath(string sessionId) => Path.Combine(LogCacheDirectory, sessionId + ".log");

        private static string? ReadLogCache(string sessionId)
        {
            string path = LogCachePath(sessionId);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static JsonObject ReadLogIndex()
        {
            if (!File.Exists(LogIndexPath)) return [];
            try
            {
                return JsonNode.Parse(File.ReadAllText(LogIndexPath)) as JsonObject ?? [];
            }
            catch (System.Text.Json.JsonException e)
            {
                _logger.Warn("[OnlineLog] 缓存索引读不了，按空的处理：{0}", e.Message);
                return [];
            }
        }

        private void WriteLogCache(SessionRow session, string text)
        {
            Directory.CreateDirectory(LogCacheDirectory);
            File.WriteAllText(LogCachePath(session.SessionId), text, new UTF8Encoding(false));

            JsonObject index = ReadLogIndex();
            index[session.SessionId] = new JsonObject
            {
                ["table"] = session.Table,
                ["row"] = session.Raw.DeepClone(),
                ["cachedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            File.WriteAllText(LogIndexPath, index.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            session.IsCached = true;
        }

        /// <summary>不联网列出本地缓存过的局（token 过期也能看）</summary>
        public RelayCommand Cmd_ShowCached => _cmd_ShowCached ??= new(() =>
        {
            List<SessionRow> rows = [];
            foreach ((string sessionId, JsonNode? entry) in ReadLogIndex())
            {
                if (entry?["row"] is not JsonObject row || !File.Exists(LogCachePath(sessionId))) continue;
                rows.Add(new SessionRow((JsonObject)row.DeepClone(), entry["table"]?.ToString() ?? SessionTable));
            }

            Sessions.Clear();
            foreach (SessionRow row in rows.OrderByDescending(r => r.Time, StringComparer.Ordinal))
                Sessions.Add(row);
            SessionPage.Page = 1;
            SessionPage.Total = 0;
            StatusText = rows.Count == 0 ? "本地还没有缓存的日志（点过的局才会缓存）" : $"✓ 本地缓存 {rows.Count} 局（不访问后台；翻页按钮对它无效）";
        });
        private RelayCommand? _cmd_ShowCached;

        /// <summary>强制从后台重拉选中这局并覆盖缓存（拉取时那局还没结束、日志不全时用）</summary>
        public AsyncRelayCommand Cmd_RefetchLog => _cmd_RefetchLog ??= new(() =>
            SelectedSession is null ? Task.CompletedTask : LoadLogAsync(SelectedSession, refetch: true));
        private AsyncRelayCommand? _cmd_RefetchLog;

        /// <summary>清空本地缓存（文件 + 内存），之后再点哪一局都重新从后台拉</summary>
        public RelayCommand Cmd_ClearCache => _cmd_ClearCache ??= new(() =>
        {
            int count = Directory.Exists(LogCacheDirectory) ? Directory.GetFiles(LogCacheDirectory, "*.log").Length : 0;
            if (count == 0 && _logCache.Count == 0)
            {
                StatusText = "本地没有缓存，不用清";
                return;
            }
            if (!MessagePopupService.OKCancel($"将删除本地缓存的 {count} 局日志（{LogCacheDirectory}）。\n之后再点哪一局都会重新从后台拉取。", "清空本地缓存"))
                return;

            if (Directory.Exists(LogCacheDirectory))
                Directory.Delete(LogCacheDirectory, true);
            _logCache.Clear();
            foreach (SessionRow row in Sessions)
                row.IsCached = false;
            ShowLog("");
            LogTitle = "服务端日志（在上面点一局）";
            _logger.Info("[OnlineLog] 已清空本地缓存 {0} 局", count);
            StatusText = $"✓ 已清空本地缓存 {count} 局；再点哪一局都会重新从后台拉取";
        });
        private RelayCommand? _cmd_ClearCache;

        public RelayCommand Cmd_OpenCacheFolder => _cmd_OpenCacheFolder ??= new(() =>
        {
            Directory.CreateDirectory(LogCacheDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogCacheDirectory) { UseShellExecute = true });
        });
        private RelayCommand? _cmd_OpenCacheFolder;

        private void ShowLog(string text)
        {
            _logText = text;
            RefreshLog();
        }

        private void RefreshLog()
        {
            (LogView, int shown, int total) = FilterEntries(_logText, LogFilter.Trim(), LogOnlyProblems);
            LogInfo = _logText.Length == 0 ? "" : shown == total ? $"共 {total} 条" : $"显示 {shown} / {total} 条";
        }

        /// <summary>
        /// 按条目过滤：以 "[" 开头的行是一条新日志，后面不带时间的行（异常堆栈等）跟着上一条走，
        /// 这样筛出一条 error 时它的堆栈也一起显示。
        /// </summary>
        private static (string Text, int Shown, int Total) FilterEntries(string text, string keyword, bool onlyProblems)
        {
            if (text.Length == 0) return ("", 0, 0);

            StringBuilder sb = new();
            int shown = 0, total = 0;
            bool keep = false;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith('[') || total == 0)
                {
                    total++;
                    keep = (keyword.Length == 0 || line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        && (!onlyProblems || IsProblem(line));
                    if (keep) shown++;
                }
                else if (keyword.Length > 0 && !keep && line.Contains(keyword, StringComparison.OrdinalIgnoreCase) && !onlyProblems)
                {
                    // 关键词只出现在堆栈里：堆栈这一行也要能被搜到
                    keep = true;
                    shown++;
                }

                if (keep) sb.Append(line).Append('\n');
            }
            return (sb.ToString(), shown, total);
        }

        private static bool IsProblem(string line) =>
            line.Contains("][warn", StringComparison.OrdinalIgnoreCase)
            || line.Contains("][error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("][fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("][critical", StringComparison.OrdinalIgnoreCase);

        // =============== ② 报错统计 ===============

        /// <summary>一种报错（后台按内容聚合）</summary>
        public sealed class ErrorRow(JsonObject row)
        {
            public string Text { get; } = Str(row, "server_log") is { Length: > 0 } s ? s : Str(row, "stack_trace");
            public string Count { get; } = Str(row, "cnt");
            /// <summary>后台有时只给次数、内容是空的（实测客户端报错出现过）</summary>
            public string FirstLine => Text.Length == 0 ? "（后台没给报错内容，只有次数）" : Text.Split('\n', 2)[0];
        }

        /// <summary>true = 服务端报错，false = 客户端报错</summary>
        public bool ErrorServerSide { get => _errorServerSide; set => SetProperty(ref _errorServerSide, value); }
        private bool _errorServerSide = true;

        public string ErrorKeyword { get => _errorKeyword; set => SetProperty(ref _errorKeyword, value); }
        private string _errorKeyword = "";

        public ObservableCollection<ErrorRow> Errors { get; } = [];

        public PageState ErrorPage { get; } = new();

        public ErrorRow? SelectedError
        {
            get => _selectedError;
            set { if (SetProperty(ref _selectedError, value)) OnPropertyChanged(nameof(ErrorDetail)); }
        }
        private ErrorRow? _selectedError;

        public string ErrorDetail => SelectedError is null ? "在上面点一条报错，这里显示完整内容。"
            : SelectedError.Text.Length == 0 ? SelectedError.FirstLine : SelectedError.Text;

        public AsyncRelayCommand Cmd_QueryErrors => _cmd_QueryErrors ??= new(() => QueryErrorsAsync(1));
        private AsyncRelayCommand? _cmd_QueryErrors;

        public AsyncRelayCommand Cmd_ErrorPrev => _cmd_ErrorPrev ??= new(() => QueryErrorsAsync(ErrorPage.Page - 1));
        private AsyncRelayCommand? _cmd_ErrorPrev;

        public AsyncRelayCommand Cmd_ErrorNext => _cmd_ErrorNext ??= new(() => QueryErrorsAsync(ErrorPage.Page + 1));
        private AsyncRelayCommand? _cmd_ErrorNext;

        private (bool Server, string Keyword, int Days) _errorQuery;

        private Task QueryErrorsAsync(int page)
        {
            if (page == 1)
                _errorQuery = (ErrorServerSide, ErrorKeyword.Trim(), Days);
            else if (page < 1 || page > ErrorPage.PageCount)
                return Task.CompletedTask;

            (bool server, string keyword, int days) = _errorQuery;
            return RunAsync(server ? "查服务端报错" : "查客户端报错", async token =>
            {
                DateTimeOffset end = DateTimeOffset.Now;
                (List<JsonObject> rows, int total) = await CloudAdminApi.QueryTableAsync(token, server ? ServerErrorTable : ClientErrorTable,
                    [], end.AddDays(-days), end, keyword, page, PageSize);

                Errors.Clear();
                foreach (JsonObject row in rows)
                    Errors.Add(new ErrorRow(row));
                ErrorPage.Page = page;
                ErrorPage.Total = total;
                return total == 0 ? $"最近 {days} 天没有报错" : $"共 {total} 种";
            });
        }

        // =============== ③ 自定义统计 ===============

        /// <summary>游戏上报的一条事件</summary>
        public sealed class EventRow(JsonObject row)
        {
            public string Time { get; } = Str(row, "create_at");
            public string Env { get; } = Str(row, "env");
            public string EventName { get; } = Str(row, "event_name");
            public string UserId { get; } = Str(row, "user_id");
            public string Attrs { get; } = Str(row, "attrs_str");
        }

        public string[] EnvOptions { get; } = ["test", "formal", "debug"];

        public string EventEnv { get => _eventEnv; set => SetProperty(ref _eventEnv, value); }
        private string _eventEnv;

        public string EventName { get => _eventName; set => SetProperty(ref _eventName, value); }
        private string _eventName = "";

        public string EventUserId { get => _eventUserId; set => SetProperty(ref _eventUserId, value); }
        private string _eventUserId = "";

        public ObservableCollection<EventRow> Events { get; } = [];

        public PageState EventPage { get; } = new();

        public AsyncRelayCommand Cmd_QueryEvents => _cmd_QueryEvents ??= new(() => QueryEventsAsync(1));
        private AsyncRelayCommand? _cmd_QueryEvents;

        public AsyncRelayCommand Cmd_EventPrev => _cmd_EventPrev ??= new(() => QueryEventsAsync(EventPage.Page - 1));
        private AsyncRelayCommand? _cmd_EventPrev;

        public AsyncRelayCommand Cmd_EventNext => _cmd_EventNext ??= new(() => QueryEventsAsync(EventPage.Page + 1));
        private AsyncRelayCommand? _cmd_EventNext;

        private (string Env, string Name, string UserId, int Days) _eventQuery;

        private Task QueryEventsAsync(int page)
        {
            if (page == 1)
                _eventQuery = (EventEnv, EventName.Trim(), EventUserId.Trim(), Days);
            else if (page < 1 || page > EventPage.PageCount)
                return Task.CompletedTask;

            (string env, string name, string user, int days) = _eventQuery;
            return RunAsync($"查自定义统计（{env}）", async token =>
            {
                JsonArray options = [Filter("env", env)];
                if (name.Length > 0) options.Add(Filter("event_name", name));
                if (user.Length > 0) options.Add(Filter("user_id", user));
                DateTimeOffset end = DateTimeOffset.Now;
                (List<JsonObject> rows, int total) = await CloudAdminApi.QueryTableAsync(token, EventTable, options, end.AddDays(-days), end, "", page, PageSize);

                Events.Clear();
                foreach (JsonObject row in rows)
                    Events.Add(new EventRow(row));
                EventPage.Page = page;
                EventPage.Total = total;
                return total == 0 ? $"最近 {days} 天没有事件（游戏要先用星火的自定义统计接口上报）" : $"共 {total} 条";
            });
        }

        // =============== ④ 玩家上传日志（开发日志） ===============

        /// <summary>
        /// 玩家在游戏里点「上传日志」后的一份日志包。后台只给一个 OSS 地址（不带协议头），
        /// 包里是那台设备的<b>完整客户端日志</b>：client/（游戏 C# 日志，含完整异常栈）、game/（引擎，含 Shader 缺变体）、
        /// lua/、Network/、downloader/……比服务端日志里转发的那几行全得多，手机特有的问题靠它查。
        /// </summary>
        public sealed class PlayerLogRow(JsonObject row) : ObservableObject
        {
            public string RowId { get; } = Str(row, "row_id");
            public string Time { get; } = Str(row, "time");
            public string Env { get; } = Str(row, "env");
            public string UserId { get; } = Str(row, "user_id");
            public string Url { get; } = Str(row, "url");

            /// <summary>本地目录名：上传时间_userId_行号（时间在前，按名字排序就是按时间排）</summary>
            public string FolderName => $"{Time.Replace(':', '-').Replace(' ', '_')}_{UserId}_{RowId}";

            public string LocalPath => Path.Combine(PlayerLogDirectory, FolderName);

            public bool IsDownloaded { get => _isDownloaded; set { if (SetProperty(ref _isDownloaded, value)) OnPropertyChanged(nameof(DownloadedMark)); } }
            private bool _isDownloaded;

            public string DownloadedMark => IsDownloaded ? "✓" : "";

            public void RefreshDownloaded() => IsDownloaded = Directory.Exists(LocalPath);
        }

        /// <summary>玩家上传日志的本地目录：每份一个子目录（解压后的内容），原始 zip 不留</summary>
        public static readonly string PlayerLogDirectory = Path.Combine(App.DataDirectory, "playerlogs");

        private const string PlayerLogTable = "firm0_app_log_download";

        /// <summary>环境筛选：后台的取值就是这两个中文名，空 = 全部</summary>
        public string[] PlayerLogEnvOptions { get; } = ["全部", "星火对战平台内测环境", "正式环境"];

        public string PlayerLogEnv { get => _playerLogEnv; set => SetProperty(ref _playerLogEnv, value); }
        private string _playerLogEnv = "全部";

        public string PlayerLogUserId { get => _playerLogUserId; set => SetProperty(ref _playerLogUserId, value); }
        private string _playerLogUserId = "";

        public ObservableCollection<PlayerLogRow> PlayerLogs { get; } = [];

        public PageState PlayerLogPage { get; } = new();

        public PlayerLogRow? SelectedPlayerLog { get => _selectedPlayerLog; set => SetProperty(ref _selectedPlayerLog, value); }
        private PlayerLogRow? _selectedPlayerLog;

        public AsyncRelayCommand Cmd_QueryPlayerLogs => _cmd_QueryPlayerLogs ??= new(() => QueryPlayerLogsAsync(1));
        private AsyncRelayCommand? _cmd_QueryPlayerLogs;

        public AsyncRelayCommand Cmd_PlayerLogPrev => _cmd_PlayerLogPrev ??= new(() => QueryPlayerLogsAsync(PlayerLogPage.Page - 1));
        private AsyncRelayCommand? _cmd_PlayerLogPrev;

        public AsyncRelayCommand Cmd_PlayerLogNext => _cmd_PlayerLogNext ??= new(() => QueryPlayerLogsAsync(PlayerLogPage.Page + 1));
        private AsyncRelayCommand? _cmd_PlayerLogNext;

        private (string Env, string UserId, int Days) _playerLogQuery;

        private Task QueryPlayerLogsAsync(int page)
        {
            if (page == 1)
            {
                string userId = PlayerLogUserId.Trim();
                if (userId.Length > 0 && !long.TryParse(userId, out _))
                {
                    StatusText = "✗ userId 要填数字，留空 = 全部玩家。";
                    return Task.CompletedTask;
                }
                _playerLogQuery = (PlayerLogEnv, userId, Days);
            }
            else if (page < 1 || page > PlayerLogPage.PageCount)
            {
                return Task.CompletedTask;
            }

            (string env, string user, int days) = _playerLogQuery;
            return RunAsync("查玩家上传日志", async token =>
            {
                JsonArray options = [];
                if (env != "全部") options.Add(Filter("env", env));
                if (user.Length > 0) options.Add(Filter("user_id", user));
                DateTimeOffset end = DateTimeOffset.Now;
                (List<JsonObject> rows, int total) = await CloudAdminApi.QueryTableAsync(token, PlayerLogTable, options, end.AddDays(-days), end, "", page, PageSize);

                PlayerLogs.Clear();
                foreach (JsonObject row in rows)
                {
                    PlayerLogRow item = new(row);
                    item.RefreshDownloaded();
                    PlayerLogs.Add(item);
                }
                PlayerLogPage.Page = page;
                PlayerLogPage.Total = total;
                return total == 0 ? $"最近 {days} 天没有玩家上传日志" : $"共 {total} 份，选一份点「下载」";
            });
        }

        /// <summary>下载选中的日志包并解压到本地目录；已经下过就直接打开</summary>
        public AsyncRelayCommand Cmd_DownloadPlayerLog => _cmd_DownloadPlayerLog ??= new(async () =>
        {
            PlayerLogRow? item = SelectedPlayerLog;
            if (item is null)
            {
                StatusText = "先在列表里选一份日志。";
                return;
            }

            item.RefreshDownloaded();
            if (item.IsDownloaded)
            {
                OpenFolder(item.LocalPath);
                StatusText = "✓ 已经下载过，直接打开了本地目录（要重下先点「删除本地」）";
                return;
            }
            if (item.Url.Length == 0)
            {
                StatusText = "✗ 这一行后台没给下载地址。";
                return;
            }

            IsIdle = false;
            StatusText = $"● 下载 {item.Time} {item.UserId} 的日志包……";
            string tempZip = Path.Combine(Path.GetTempPath(), "azkit_playerlog_" + item.RowId + ".zip");
            try
            {
                string url = item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? item.Url : "https://" + item.Url;
                byte[] data = await PlayerLogHttp.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempZip, data);

                // 先解到临时目录再改名，半路失败不会留下一个看着像下完了的目录
                string partial = item.LocalPath + ".partial";
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, partial);
                Directory.Move(partial, item.LocalPath);

                item.RefreshDownloaded();
                _logger.Info("[OnlineLog] 玩家日志已下载 {0}（{1} KB）", item.LocalPath, data.Length / 1024);
                OpenFolder(item.LocalPath);
                StatusText = $"✓ 已下载并解压（{data.Length / 1024f / 1024f:0.0} MB）→ {item.LocalPath}；游戏日志在 client\\，引擎（含 Shader 缺变体）在 game\\";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                _logger.Warn("[OnlineLog] 玩家日志下载失败：{0}", e.Message);
                StatusText = "✗ 下载失败：" + e.Message;
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                IsIdle = true;
            }
        });
        private AsyncRelayCommand? _cmd_DownloadPlayerLog;

        /// <summary>删掉选中这份的本地目录（想重新下载时用）</summary>
        public RelayCommand Cmd_DeletePlayerLog => _cmd_DeletePlayerLog ??= new(() =>
        {
            PlayerLogRow? item = SelectedPlayerLog;
            if (item is null || !Directory.Exists(item.LocalPath)) return;
            if (!MessagePopupService.OKCancel($"删除本地目录 {item.LocalPath}？", "删除本地日志")) return;
            Directory.Delete(item.LocalPath, true);
            item.RefreshDownloaded();
            StatusText = "✓ 已删除本地目录，再点「下载」会重新从后台下";
        });
        private RelayCommand? _cmd_DeletePlayerLog;

        public RelayCommand Cmd_OpenPlayerLogFolder => _cmd_OpenPlayerLogFolder ??= new(() =>
        {
            Directory.CreateDirectory(PlayerLogDirectory);
            OpenFolder(PlayerLogDirectory);
        });
        private RelayCommand? _cmd_OpenPlayerLogFolder;

        /// <summary>日志包放在公开的 OSS 上，不需要后台凭证；包可能有十几 MB，超时放宽</summary>
        private static readonly HttpClient PlayerLogHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

        private static void OpenFolder(string path) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
