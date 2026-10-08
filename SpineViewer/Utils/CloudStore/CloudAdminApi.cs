using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 星火后台云变量表接口（鉴权靠 Cookie 里的 token，见 <see cref="CloudAdminToken"/>）。
    /// 接口形状与命令行工具 EditCloudStore 相同，已在 p_czzc / debug 上实测通过：
    ///
    /// <code>
    ///   查询   POST /api/v1/table/data   按 env + user_id 筛行，分页
    ///   修改   POST /api/v1/table/row    functor = edit，payload = { data_type, value }
    ///   业务码 字段 result（0 = 成功），失败带 msg
    /// </code>
    ///
    /// <b>没有"删除"</b>：后台只允许删列表型云变量，普通存档 key 删不了（实测"删除失败, 非列表类型不能删除"）。
    /// 删档 = 把值改成空字符串，游戏服务端读到空值按"云端没有这一段"重新建档（<c>CloudDBData.FromJson</c>）。
    ///
    /// 这件事原先做在游戏的 GM 面板里，但星火沙箱不允许游戏发 HTTP，所以挪到这里（桌面程序，没有沙箱）。
    /// </summary>
    public static class CloudAdminApi
    {
        /// <summary>后台地址</summary>
        public const string ApiBase = "https://adminapi-pd.spark.xd.com/";

        /// <summary>AzureSail 在星火后台的项目 ID（工程 <c>config.ini</c> 的 <c>score_name</c>）</summary>
        public const string Firm = "p_czzc";

        /// <summary>云变量表 ID</summary>
        public const string TableId = "firm0_score_query";

        /// <summary>每页行数。一个账号的 key 远少于这个数</summary>
        private const int PageLimit = 500;

        /// <summary>最多翻几页，防止分页字段与预期不一致时死循环</summary>
        private const int MaxPages = 20;

        private static readonly HttpClient Http = new() { BaseAddress = new Uri(ApiBase), Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>查一个账号在某环境下的全部行</summary>
        public static async Task<List<CloudSaveRow>> QueryAsync(CloudAdminToken token, long userId, string env)
        {
            List<CloudSaveRow> rows = [];

            for (int page = 1; page <= MaxPages; page++)
            {
                JsonObject body = new()
                {
                    ["firm"] = Firm,
                    ["table_id"] = TableId,
                    ["sort_key"] = "user_id",
                    ["sort_type"] = "desc",
                    ["search_key"] = null,
                    ["page"] = page,
                    ["page_limit"] = PageLimit,
                    ["search_options"] = new JsonArray
                    {
                        Filter("env", env),
                        Filter("user_id", userId.ToString()),
                    },
                };

                JsonNode root = await PostAsync(token, "api/v1/table/data", body);
                JsonArray list = (root["list"] as JsonArray ?? root["data"]?["list"] as JsonArray)
                    ?? throw new CloudAdminException("查询返回里找不到 list 字段，接口格式可能变了");

                foreach (JsonNode? item in list)
                {
                    if (CloudSaveRow.FromJson(item) is { } row)
                        rows.Add(row);
                }

                if (list.Count < PageLimit)
                    break;
            }

            return rows;
        }

        /// <summary>把一行的值改成空字符串（= 删这一段存档）</summary>
        public static Task ClearAsync(CloudAdminToken token, CloudSaveRow row) => PostAsync(token, "api/v1/table/row", new JsonObject
        {
            ["firm"] = Firm,
            ["table_id"] = TableId,
            ["row_id"] = row.RowId,
            ["functor"] = "edit",
            ["payload"] = new JsonObject
            {
                ["data_type"] = row.DataType,
                ["value"] = string.Empty,
            },
        });

        /// <summary>
        /// 每次请求结束（成功或失败）都报一次：接口、HTTP 状态、业务码、说明、耗时。界面的「状态监测」靠它。
        /// 在调用方的同步上下文里触发（WPF 界面线程发起的请求就回到界面线程）。
        /// </summary>
        public static event Action<CloudApiCall>? CallCompleted;

        private static async Task<JsonNode> PostAsync(CloudAdminToken token, string path, JsonObject body)
        {
            string api = path.EndsWith("/data", StringComparison.Ordinal) ? "查询" : "修改";
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                (JsonNode root, int status) = await SendAsync(token, path, body);
                CallCompleted?.Invoke(new CloudApiCall(DateTime.Now, api, status, "0", "成功", watch.ElapsedMilliseconds, true));
                return root;
            }
            catch (CloudAdminException e)
            {
                CallCompleted?.Invoke(new CloudApiCall(DateTime.Now, api, e.HttpStatus, e.ResultCode, e.Message, watch.ElapsedMilliseconds, false));
                throw;
            }
        }

        /// <summary>发请求并校验：HTTP 状态、JSON、业务码。失败一律抛 <see cref="CloudAdminException"/>（带上 HTTP 状态与业务码）</summary>
        private static async Task<(JsonNode Root, int Status)> SendAsync(CloudAdminToken token, string path, JsonObject body)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", "token=" + token.Token);

            string text;
            int status;
            try
            {
                using HttpResponseMessage response = await Http.SendAsync(request);
                status = (int)response.StatusCode;
                text = await response.Content.ReadAsStringAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new CloudAdminException("请求失败（" + e.GetType().Name + "：" + e.Message + "）");
            }

            if (status is 401 or 403)
                throw new CloudAdminException("后台拒绝访问：token 过期或无权访问项目 " + Firm + "，重新登录后台复制 Cookie") { HttpStatus = status };

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new CloudAdminException("返回的不是 JSON：" + Shorten(text)) { HttpStatus = status };
            }

            if (root is null)
                throw new CloudAdminException("后台返回为空") { HttpStatus = status };

            // 业务码：实测字段叫 result（0 = 成功）；兼容 code
            if ((root["result"] ?? root["code"]) is JsonValue codeNode && codeNode.ToJsonString().Trim('"') is not ("0" or "200") and var code)
                throw new CloudAdminException("后台返回错误：" + (root["msg"]?.ToString() ?? Shorten(text))) { HttpStatus = status, ResultCode = code };

            if (status is < 200 or >= 300)
                throw new CloudAdminException("HTTP " + status + "：" + Shorten(text)) { HttpStatus = status };

            return (root, status);
        }

        private static JsonObject Filter(string type, string value) => new()
        {
            ["type"] = type,
            ["value"] = new JsonObject { ["data"] = value, ["operator"] = "=" },
        };

        private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary>后台调用失败，消息直接给人看</summary>
    public sealed class CloudAdminException(string message) : Exception(message)
    {
        /// <summary>HTTP 状态码；没收到响应（网络不通、超时）为 0</summary>
        public int HttpStatus { get; init; }

        /// <summary>后台业务码（result 字段）；没有为空</summary>
        public string ResultCode { get; init; } = "";
    }

    /// <summary>一次后台请求的结果，给界面「状态监测」用</summary>
    /// <param name="Time">结束时刻</param>
    /// <param name="Api">查询 / 修改</param>
    /// <param name="HttpStatus">HTTP 状态码，0 = 没收到响应</param>
    /// <param name="ResultCode">后台业务码（0 = 成功）</param>
    /// <param name="Message">说明</param>
    /// <param name="ElapsedMs">耗时（毫秒）</param>
    /// <param name="Ok">成功没有</param>
    public sealed record CloudApiCall(DateTime Time, string Api, int HttpStatus, string ResultCode, string Message, long ElapsedMs, bool Ok);
}
