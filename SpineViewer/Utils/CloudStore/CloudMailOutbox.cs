using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 游戏的<b>邮件待发箱</b>（虚拟账号 9999989 名下的 <c>mail_outbox</c> 一行）。格式照游戏服务端
    /// <c>src/Server/Game/Mail/MailOutboxVo.cs</c>（AzureSail 工程 <c>ReadMarkDown/邮件系统.md</c>）：
    ///
    /// <code>
    ///   {"Mails":[{"Id":..,"Title":"..","Content":"..","Sender":"..","SendMs":..,"ExpireDays":30,
    ///              "Target":0 全服 / 1 指定区 / 2 指定玩家,"ServerIds":[..],"UserIds":[..],"IncludeNew":false,
    ///              "Items":[{"ItemId":..,"Count":..}]}]}
    /// </code>
    ///
    /// 服务端启动时没有这一行就写占位 <c>{"Mails":[]}</c>（后台接口只能改已有的行），之后每 60 秒读一次、有新的就给在线玩家投递；
    /// 玩家进区 / 打开邮件时也会补投。玩家按 Id 记「收过没有」，所以 Id 只增不复用（取发送时刻毫秒，和已有的比取大）。
    /// 字段名按 System.Text.Json 默认大小写读，要和服务端一字不差。
    /// </summary>
    public sealed class CloudMailOutbox
    {
        /// <summary>容器账号（游戏 <c>CloudDBKeys.MailOutboxUserId</c>）</summary>
        public const long ContainerUserId = 9999989L;

        /// <summary>待发箱 key（游戏 <c>CloudDBKeys.MailOutbox</c>）</summary>
        public const string OutboxKey = "mail_outbox";

        public const int TargetAll = 0;
        public const int TargetServers = 1;
        public const int TargetUsers = 2;

        private const long DayMs = 24L * 3600 * 1000;

        private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = false };

        /// <summary>一个附件（字段名照服务端 MailAttachVo）</summary>
        public sealed class Attach
        {
            public int ItemId { get; set; }
            public long Count { get; set; }
        }

        /// <summary>一封待发邮件（字段名照服务端 OutboxMailVo）</summary>
        public sealed class Mail
        {
            public long Id { get; set; }
            public string Title { get; set; } = "";
            public string Content { get; set; } = "";
            public string Sender { get; set; } = "";
            public long SendMs { get; set; }
            public int ExpireDays { get; set; } = 30;
            public int Target { get; set; }
            public int[] ServerIds { get; set; } = [];
            public long[] UserIds { get; set; } = [];
            public bool IncludeNew { get; set; }
            public Attach[] Items { get; set; } = [];

            /// <summary>过期时刻（服务端同样这么算）</summary>
            public long ExpireAtMs() => SendMs + Math.Max(1, ExpireDays) * DayMs;
        }

        private sealed class Box
        {
            public List<Mail> Mails { get; set; } = [];
        }

        public required string Env { get; init; }

        /// <summary>容器账号下的全部行（备份用）</summary>
        public required List<CloudSaveRow> Rows { get; init; }

        /// <summary>待发箱那一行；null = 还没有（这个环境没开过局）</summary>
        public CloudSaveRow? OutboxRow { get; init; }

        /// <summary>那一行有值但解不开（别覆盖，先人工看备份）</summary>
        public bool Broken { get; init; }

        /// <summary>当前待发的邮件（按 Id 排）</summary>
        public required List<Mail> Mails { get; init; }

        public static CloudMailOutbox Build(string env, List<CloudSaveRow> rows)
        {
            CloudSaveRow? row = rows.Find(r => r.Key == OutboxKey);
            List<Mail> mails = [];
            bool broken = false;
            if (row is not null && !row.IsEmpty)
            {
                string text = row.Value is JsonValue v && v.TryGetValue(out string? s) ? s : row.Value!.ToJsonString();
                try
                {
                    mails = JsonSerializer.Deserialize<Box>(text, ReadOptions)?.Mails ?? [];
                }
                catch (JsonException)
                {
                    broken = true;
                }
            }

            mails.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new CloudMailOutbox { Env = env, Rows = rows, OutboxRow = row, Broken = broken, Mails = mails };
        }

        /// <summary>新邮件的 Id：发送时刻毫秒，和已有的最大 Id 比取大（只增不复用）</summary>
        public long NextId(long nowMs)
        {
            long max = Mails.Count > 0 ? Mails.Max(m => m.Id) : 0;
            return Math.Max(nowMs, max + 1);
        }

        /// <summary>
        /// 写回的值：<paramref name="mails"/> 去掉已过期的，按 Id 排。
        /// 过期的服务端本来就不投，删掉只是让这一行别越攒越大。
        /// </summary>
        public static string Value(IEnumerable<Mail> mails, long nowMs)
        {
            List<Mail> keep = mails.Where(m => nowMs < m.ExpireAtMs()).OrderBy(m => m.Id).ToList();
            return JsonSerializer.Serialize(new Box { Mails = keep });
        }

        /// <summary>毫秒 → 北京时间文本</summary>
        public static string Format(long unixMs) => unixMs <= 0
            ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(Beijing).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        /// <summary>目标的说明文字</summary>
        public static string TargetText(Mail mail) => mail.Target switch
        {
            TargetServers => "区 " + string.Join(",", mail.ServerIds),
            TargetUsers => "玩家 " + string.Join(",", mail.UserIds),
            _ => "全服",
        } + (mail.IncludeNew ? "（含之后建号的）" : "");
    }
}
