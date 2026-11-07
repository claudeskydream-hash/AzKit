using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 游戏的<b>全服开服存档</b>（虚拟账号 9999992 名下）。格式照游戏服务端
    /// <c>src/Server/Game/ServerOpen/ServerOpenVo.cs</c> / <c>ServerOpenMager.cs</c>（AzureSail 工程 <c>ReadMarkDown/开服与上线.md</c>）：
    ///
    /// <code>
    ///   serveronline_set   AzKit 设的上线时间 {"OnlineMs":毫秒,"UserId":0}，优先；服务端只在没有时写 OnlineMs = 0 的占位行
    ///   serveronline       开发者账号第一次登录时服务端写的上线时间
    ///   serveropen_s{区}   区开服记录 {"ServerId":区,"OpenMs":毫秒,"OffsetHours":小时}，到点后服务端写，写下就固定
    ///   developers         开发者名单 {"UserIds":[...],"Names":[...]}（能上线、能用 GM），与服务端硬编码名单取并集；
    ///                      Names 是 AzKit 表格里的备注（服务端不用）；服务端只在没有时写空名单占位
    /// </code>
    ///
    /// 后台接口只能改已有的行，所以 serveronline_set 要等这个环境开过一局（服务端启动时自动写占位）才设得了。
    /// 时间一律按<b>北京时间</b>显示和输入（游戏按北京时间 0 点跨天）。
    /// </summary>
    public sealed partial class CloudServerOpen
    {
        /// <summary>容器账号（游戏 <c>CloudDBKeys.ServerOpenUserId</c>）</summary>
        public const long ContainerUserId = 9999992L;

        /// <summary>AzKit 设的上线时间 key</summary>
        public const string ManualKey = "serveronline_set";

        /// <summary>开发者登录写的上线时间 key</summary>
        public const string AutoKey = "serveronline";

        /// <summary>开发者名单 key</summary>
        public const string DevelopersKey = "developers";

        /// <summary>输入 / 显示的时间写法</summary>
        public const string TimeFormat = "yyyy-MM-dd HH:mm";

        private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

        /// <summary>一个开发者（UserId + 备注）</summary>
        public sealed record Developer(long UserId, string Note);

        /// <summary>一个区的开服记录</summary>
        public sealed record OpenRecord(int ServerId, long OpenMs, int OffsetHours, CloudSaveRow Row);

        public required string Env { get; init; }

        /// <summary>容器账号下的全部行（备份用）</summary>
        public required List<CloudSaveRow> Rows { get; init; }

        /// <summary>AzKit 那一行；null = 还没有（这个环境没开过局）</summary>
        public CloudSaveRow? ManualRow { get; init; }

        /// <summary>AzKit 设的上线时刻；0 = 没设（占位）</summary>
        public long ManualOnlineMs { get; init; }

        /// <summary>开发者登录写的上线时刻；0 = 没有</summary>
        public long AutoOnlineMs { get; init; }

        /// <summary>开发者登录写的是谁</summary>
        public long AutoUserId { get; init; }

        /// <summary>已写下的区开服记录（按区号排）</summary>
        public required List<OpenRecord> Records { get; init; }

        /// <summary>开发者名单那一行；null = 还没有（这个环境没开过局）</summary>
        public CloudSaveRow? DevelopersRow { get; init; }

        /// <summary>云端开发者名单</summary>
        public required List<Developer> Developers { get; init; }

        /// <summary>生效的上线时刻（AzKit 设的优先）</summary>
        public long EffectiveOnlineMs => ManualOnlineMs > 0 ? ManualOnlineMs : AutoOnlineMs;

        public static CloudServerOpen Build(string env, List<CloudSaveRow> rows)
        {
            CloudSaveRow? manual = rows.Find(r => r.Key == ManualKey);
            CloudSaveRow? auto = rows.Find(r => r.Key == AutoKey);
            JsonObject? autoObj = Parse(auto);
            CloudSaveRow? devRow = rows.Find(r => r.Key == DevelopersKey);

            List<Developer> developers = [];
            JsonObject? devObj = Parse(devRow);
            if (devObj?["UserIds"] is JsonArray ids)
            {
                JsonArray? names = devObj["Names"] as JsonArray;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (ids[i] is JsonValue v && v.TryGetValue(out long n) && n > 0)
                    {
                        string note = names is not null && i < names.Count && names[i] is JsonValue nv && nv.TryGetValue(out string? text) ? text : "";
                        developers.Add(new Developer(n, note));
                    }
                }
            }

            List<OpenRecord> records = [];
            foreach (CloudSaveRow row in rows)
            {
                Match m = OpenKeyRegex().Match(row.Key);
                if (!m.Success || Parse(row) is not { } obj)
                    continue;

                long openMs = Long(obj, "OpenMs");
                if (openMs > 0)
                    records.Add(new OpenRecord(int.Parse(m.Groups[1].Value), openMs, (int)Long(obj, "OffsetHours"), row));
            }

            records.Sort((a, b) => a.ServerId.CompareTo(b.ServerId));

            return new CloudServerOpen
            {
                Env = env,
                Rows = rows,
                ManualRow = manual,
                ManualOnlineMs = Long(Parse(manual), "OnlineMs"),
                AutoOnlineMs = Long(autoObj, "OnlineMs"),
                AutoUserId = Long(autoObj, "UserId"),
                Records = records,
                DevelopersRow = devRow,
                Developers = developers,
            };
        }

        /// <summary>写进 developers 的值（UserIds 与 Names 一一对应）</summary>
        public static string DevelopersValue(IEnumerable<Developer> developers)
        {
            JsonArray ids = [];
            JsonArray names = [];
            foreach (Developer d in developers)
            {
                ids.Add(d.UserId);
                names.Add(d.Note);
            }

            return new JsonObject { ["UserIds"] = ids, ["Names"] = names }.ToJsonString();
        }

        /// <summary>写进 serveronline_set 的值（服务端按 System.Text.Json 默认大小写读，字段名要对上）</summary>
        public static string ManualValue(long onlineMs) => new JsonObject { ["OnlineMs"] = onlineMs, ["UserId"] = 0 }.ToJsonString();

        /// <summary>毫秒 → 北京时间文本；0 显示「未定」</summary>
        public static string Format(long unixMs) => unixMs <= 0
            ? "未定"
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(Beijing).ToString(TimeFormat + ":ss", CultureInfo.InvariantCulture);

        /// <summary>北京时间文本 → 毫秒；写错返回 null</summary>
        public static long? ParseTime(string text)
        {
            if (!DateTime.TryParseExact(text.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime beijing))
                return null;
            return new DateTimeOffset(beijing, Beijing).ToUnixTimeMilliseconds();
        }

        /// <summary>现在的北京时间文本（输入框默认值）</summary>
        public static string NowText() => DateTimeOffset.UtcNow.ToOffset(Beijing).ToString(TimeFormat, CultureInfo.InvariantCulture);

        public string Describe()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long effective = EffectiveOnlineMs;
            string state = effective <= 0 ? "还没定上线时间 → 所有区未开服"
                : effective > now ? $"已定，还没到点（{(effective - now) / 60000} 分钟后上线）"
                : "已上线";

            List<string> lines =
            [
                $"环境 {Env}　容器账号 {ContainerUserId}",
                $"生效的上线时间：{Format(effective)}（{state}）",
                $"　AzKit 设的（{ManualKey}）：{(ManualRow is null ? "没有这一行 —— 这个环境还没开过局，先开一局让服务端建占位行" : Format(ManualOnlineMs))}",
                $"　开发者登录写的（{AutoKey}）：{Format(AutoOnlineMs)}" + (AutoUserId > 0 ? $"（userId {AutoUserId}）" : ""),
                $"已写下开服记录的区：{Records.Count} 个",
            ];
            foreach (OpenRecord r in Records)
                lines.Add($"　{r.ServerId} 区　{Format(r.OpenMs)}　（上线后 {r.OffsetHours} 小时）");

            lines.Add(DevelopersRow is null
                ? $"开发者名单（{DevelopersKey}）：没有这一行 —— 先开一局让服务端建占位行"
                : $"开发者名单（{DevelopersKey}）：{Developers.Count} 人，见上方表格（另有服务端硬编码的，取并集；调试环境人人都算）");

            return string.Join("\n", lines);
        }

        /// <summary>存档值是"JSON 序列化成的字符串"，解出里面那层对象；空 / 解不开返回 null</summary>
        private static JsonObject? Parse(CloudSaveRow? row)
        {
            if (row is null || row.IsEmpty)
                return null;

            string text = row.Value is JsonValue v && v.TryGetValue(out string? s) ? s : row.Value!.ToJsonString();
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static long Long(JsonObject? obj, string key) =>
            obj?[key] is JsonValue v && (v.TryGetValue(out long n) || (v.TryGetValue(out string? s) && long.TryParse(s, out n))) ? n : 0;

        [GeneratedRegex(@"^serveropen_s(\d+)$")]
        private static partial Regex OpenKeyRegex();
    }
}
