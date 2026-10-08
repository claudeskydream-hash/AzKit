using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 一个环境下的<b>全部玩家</b>：从游戏服务端维护的两张区服名册汇总出来。
    ///
    /// 后台查询必须带 user_id（只按环境查会报「请求参数不全」，实测 &gt; / != / like 都不生效），
    /// 所以没法直接列出"表里所有账号"。游戏服务端（<c>ServerRosterManager</c>）借了两个虚拟账号存跨账号名单：
    /// <code>
    ///   9999994  roster_s{区号}  全量名册：玩家第一次进这个区时加一条，永不删除
    ///   9999995  online_s{区号}  在线名册：上线加、下线删，在线期间续心跳
    /// </code>
    /// 每条是一个列表项（一行），值是 JSON：UserId / Nickname / ServerId / 时间（Unix 秒）。
    ///
    /// <b>只列得出进过区的玩家</b>：停在登录页、从没点过「开始游戏」的账号不在名册里。
    /// <b>在线按心跳判</b>：服务端被强杀时来不及删在线项，名册里会留下僵尸项（debug 环境实测堆了几百条），
    /// 心跳超过 <see cref="StaleSeconds"/> 没续的不算在线 —— 与游戏服务端同一口径。
    /// </summary>
    public sealed class CloudPlayerRoster
    {
        /// <summary>全量名册的容器账号（游戏 <c>CloudDBKeys.ServerRosterUserId</c>）</summary>
        public const long ServerRosterUserId = 9999994L;

        /// <summary>在线名册的容器账号（游戏 <c>CloudDBKeys.OnlineRosterUserId</c>）</summary>
        public const long OnlineRosterUserId = 9999995L;

        /// <summary>在线项超过这么久没续心跳就算僵尸（秒，游戏 <c>ServerRosterManager.StaleSeconds</c>）</summary>
        public const long StaleSeconds = 1000;

        /// <summary>一个玩家（跨区汇总）</summary>
        public sealed class Player
        {
            public required long UserId { get; init; }

            /// <summary>昵称：取最近一次登录的那个区登记的</summary>
            public string Nickname { get; set; } = "";

            /// <summary>进过的区，从小到大</summary>
            public SortedSet<int> Servers { get; } = [];

            /// <summary>最早进区时间（Unix 秒）</summary>
            public long CreateTime { get; set; } = long.MaxValue;

            /// <summary>最后登录时间（Unix 秒）</summary>
            public long LastLoginTime { get; set; }

            /// <summary>当前在线的区（心跳没过期的）；不在线为空</summary>
            public SortedSet<int> OnlineServers { get; } = [];

            public bool IsOnline => OnlineServers.Count > 0;
        }

        public required string Env { get; init; }

        /// <summary>按最后登录时间从近到远</summary>
        public required List<Player> Players { get; init; }

        /// <summary>在线名册里的僵尸项条数（心跳过期没删掉的）</summary>
        public int StaleOnlineCount { get; init; }

        /// <summary>解析不了的名册行数</summary>
        public int BadRowCount { get; init; }

        /// <summary>用两张名册的行汇总</summary>
        public static CloudPlayerRoster Build(string env, List<CloudSaveRow> rosterRows, List<CloudSaveRow> onlineRows, DateTimeOffset now)
        {
            Dictionary<long, Player> players = [];
            int bad = 0;
            int stale = 0;
            long nowSeconds = now.ToUnixTimeSeconds();

            Player Get(long userId)
            {
                if (!players.TryGetValue(userId, out Player? player))
                {
                    player = new Player { UserId = userId };
                    players[userId] = player;
                }
                return player;
            }

            foreach (CloudSaveRow row in rosterRows)
            {
                if (ReadEntry(row) is not { } entry)
                {
                    bad++;
                    continue;
                }

                Player player = Get(entry.UserId);
                player.Servers.Add(entry.ServerId);
                long create = Long(entry.Node, "CreateTime");
                long login = Long(entry.Node, "LastLoginTime");
                if (create > 0 && create < player.CreateTime)
                    player.CreateTime = create;
                if (login >= player.LastLoginTime)
                {
                    player.LastLoginTime = login;
                    if (entry.Nickname.Length > 0)
                        player.Nickname = entry.Nickname;
                }
                else if (player.Nickname.Length == 0)
                {
                    player.Nickname = entry.Nickname;
                }
            }

            foreach (CloudSaveRow row in onlineRows)
            {
                if (ReadEntry(row) is not { } entry)
                {
                    bad++;
                    continue;
                }

                if (nowSeconds - Long(entry.Node, "HeartbeatTime") > StaleSeconds)
                {
                    stale++;
                    continue;
                }

                // 在线却不在全量名册里（名册还没落盘）也列出来
                Player player = Get(entry.UserId);
                player.OnlineServers.Add(entry.ServerId);
                player.Servers.Add(entry.ServerId);
                if (player.Nickname.Length == 0)
                    player.Nickname = entry.Nickname;
            }

            List<Player> list = [.. players.Values.OrderByDescending(p => p.IsOnline).ThenByDescending(p => p.LastLoginTime).ThenBy(p => p.UserId)];
            return new CloudPlayerRoster { Env = env, Players = list, StaleOnlineCount = stale, BadRowCount = bad };
        }

        /// <summary>给「结果」框的一段说明</summary>
        public string Describe()
        {
            int online = Players.Count(p => p.IsOnline);
            string text = $"环境 {Env}：共 {Players.Count} 个玩家，当前在线 {online} 个。\n"
                + "名单来自游戏的区服名册（进过区才会登记），停在登录页没进过区的账号不在里面。\n"
                + "在左侧玩家列表里点一个，自动填 userId 并查看他的存档概况。";
            if (StaleOnlineCount > 0)
                text += $"\n在线名册里有 {StaleOnlineCount} 条心跳过期的僵尸项（服务端被强杀时没来得及删），已忽略。";
            if (BadRowCount > 0)
                text += $"\n有 {BadRowCount} 条名册记录解析不了，已跳过。";
            return text;
        }

        public static string FormatTime(long unixSeconds) =>
            unixSeconds is <= 0 or long.MaxValue ? "—" : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("MM-dd HH:mm");

        private sealed record Entry(long UserId, string Nickname, int ServerId, JsonObject Node);

        /// <summary>名册项的值是"JSON 序列化成的字符串"，解开取字段；缺 UserId 返回 null</summary>
        private static Entry? ReadEntry(CloudSaveRow row)
        {
            string text = row.Value is JsonValue v && v.TryGetValue(out string? s) ? s : row.Value?.ToJsonString() ?? "";
            if (text.Length == 0)
                return null;

            JsonObject? node;
            try
            {
                node = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }

            long userId = node is null ? 0 : Long(node, "UserId");
            if (node is null || userId <= 0)
                return null;

            return new Entry(userId, node["Nickname"]?.ToString() ?? "", (int)Long(node, "ServerId"), node);
        }

        private static long Long(JsonObject node, string name) =>
            node[name] is JsonValue value && value.TryGetValue(out long number) ? number : 0;
    }
}
