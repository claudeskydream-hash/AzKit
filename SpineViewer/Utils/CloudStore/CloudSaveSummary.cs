using System.Text;
using System.Text.RegularExpressions;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 把一个账号的云存档行<b>按区服归类</b>。
    ///
    /// 游戏的存档 key 规则（游戏服务端 <c>CloudDBKeys</c>）：
    /// <code>
    ///   gate            网关段（账号级：上次选区、玩过哪些区）
    ///   xxx_s{区号}      分区存档（player / hero / skill / building / bag ……），每个区一套
    ///   其它            不归游戏存档管（如共享 UI 库写的 gsui_ 开头的 key），删档时不动
    /// </code>
    /// </summary>
    public sealed partial class CloudSaveSummary
    {
        /// <summary>网关段的 key</summary>
        public const string GateKey = "gate";

        [GeneratedRegex(@"_s(\d+)$", RegexOptions.CultureInvariant)]
        private static partial Regex ServerKeyRegex();

        /// <summary>一个区的汇总</summary>
        public sealed class ServerGroup
        {
            public required int ServerId { get; init; }

            public List<CloudSaveRow> Rows { get; } = [];

            /// <summary>有内容的段数（清过的不算）</summary>
            public int FilledCount => Rows.Count(r => !r.IsEmpty);

            public int TotalSize => Rows.Sum(r => r.Size);
        }

        public required long UserId { get; init; }

        public required string Env { get; init; }

        /// <summary>全部行</summary>
        public required List<CloudSaveRow> Rows { get; init; }

        /// <summary>按区号从小到大的分区存档</summary>
        public List<ServerGroup> Servers { get; } = [];

        /// <summary>网关段；没有为 null</summary>
        public CloudSaveRow? Gate { get; private set; }

        /// <summary>既不是网关也不是分区存档的 key（删档不动它们）</summary>
        public List<CloudSaveRow> Others { get; } = [];

        public static CloudSaveSummary Build(long userId, string env, List<CloudSaveRow> rows)
        {
            CloudSaveSummary summary = new() { UserId = userId, Env = env, Rows = rows };
            Dictionary<int, ServerGroup> byServer = [];

            foreach (CloudSaveRow row in rows)
            {
                if (row.Key == GateKey)
                {
                    summary.Gate = row;
                    continue;
                }

                Match match = ServerKeyRegex().Match(row.Key);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int serverId))
                {
                    summary.Others.Add(row);
                    continue;
                }

                if (!byServer.TryGetValue(serverId, out ServerGroup? group))
                {
                    group = new ServerGroup { ServerId = serverId };
                    byServer[serverId] = group;
                }

                group.Rows.Add(row);
            }

            summary.Servers.AddRange(byServer.Values.OrderBy(g => g.ServerId));
            return summary;
        }

        /// <summary>某个区的分区存档行；没有返回空表</summary>
        public List<CloudSaveRow> RowsOfServer(int serverId) =>
            Servers.Find(g => g.ServerId == serverId)?.Rows ?? [];

        /// <summary>"删除所有区服存档"要清的行：全部分区存档 + 网关段（其它 key 不动）</summary>
        public List<CloudSaveRow> RowsOfAllServers()
        {
            List<CloudSaveRow> rows = Servers.SelectMany(g => g.Rows).ToList();
            if (Gate is not null)
                rows.Add(Gate);
            return rows;
        }

        /// <summary>概况，几行字</summary>
        public string Describe()
        {
            StringBuilder sb = new();
            sb.Append($"账号 {UserId}（{Env}）共 {Rows.Count} 行，{Rows.Count(r => r.IsEmpty)} 行已是空值\n");
            sb.Append("网关 gate：").Append(Gate is null ? "没有" : Gate.IsEmpty ? "空（下次进入按新玩家建档）" : Gate.Size + " 字节").Append('\n');
            sb.Append($"区服 {Servers.Count} 个：")
              .Append(Servers.Count == 0 ? "无" : string.Join("，", Servers.Select(g => $"{g.ServerId} 区 {g.FilledCount}/{g.Rows.Count} 段")))
              .Append('\n');
            sb.Append("其它 key（删档不动）：").Append(Others.Count == 0 ? "无" : string.Join("、", Others.Select(r => r.Key)));
            return sb.ToString();
        }
    }
}
