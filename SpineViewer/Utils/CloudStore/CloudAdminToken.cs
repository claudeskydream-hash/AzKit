using System.Text;
using System.Text.Json.Nodes;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 星火后台的<b>登录凭证</b>：从粘贴的文本里解析出 token，并读出里面的后台账号名与过期时间给人看。
    ///
    /// 认三种写法（与命令行工具 EditCloudStore 一致）：
    /// <code>
    ///   eyJ...                                                     只有 token
    ///   token=eyJ...;                                              带名字
    ///   code_challenge=..; verifier=..; referer=..; token=eyJ...   浏览器里整段 Cookie，只取 token 那一段
    /// </code>
    ///
    /// <b>token 只放内存</b>：不写文件、不进日志（日志里只出现头尾几位）。
    /// </summary>
    public sealed class CloudAdminToken
    {
        /// <summary>发给后台的 token 原文</summary>
        public string Token { get; }

        /// <summary>后台账号名（JWT 里的 userinfo.name），解不出来为空</summary>
        public string AccountName { get; }

        /// <summary>过期时间（本地时间）；解不出来为 null</summary>
        public DateTime? ExpiresAt { get; }

        private CloudAdminToken(string token, string accountName, DateTime? expiresAt)
        {
            Token = token;
            AccountName = accountName;
            ExpiresAt = expiresAt;
        }

        /// <summary>给人看的 token：只露头尾几位</summary>
        public string Masked => Token.Length <= 10 ? "****" : Token[..4] + "…" + Token[^4..];

        /// <summary>已经过期了没有（解不出过期时间按没过期算，让后台来判）</summary>
        public bool IsExpired => ExpiresAt is { } at && at <= DateTime.Now;

        /// <summary>解析。成功返回凭证；失败返回 null，<paramref name="error"/> 说明原因</summary>
        public static CloudAdminToken? Parse(string? text, out string error)
        {
            string token = Extract(text ?? string.Empty);
            if (token.Length == 0)
            {
                error = string.IsNullOrWhiteSpace(text) ? "输入框是空的" : "没找到 token=… 这一段（整段 Cookie 里要包含 token）";
                return null;
            }

            // JWT 是三段：头.载荷.签名。不是这个形状的大概率粘错了
            string[] parts = token.Split('.');
            if (parts.Length != 3)
            {
                error = "token 不是 JWT 格式（应为 xxx.yyy.zzz 三段），检查是否粘贴完整";
                return null;
            }

            (string name, DateTime? expires) = ReadPayload(parts[1]);
            error = string.Empty;
            return new CloudAdminToken(token, name, expires);
        }

        /// <summary>从文本里取出 token：整段 Cookie 时只取 token 那一段</summary>
        private static string Extract(string text)
        {
            string trimmed = text.Trim();
            if (!trimmed.Contains('='))
                return trimmed;

            foreach (string part in trimmed.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part[..eq].Trim().Equals("token", StringComparison.OrdinalIgnoreCase))
                    return part[(eq + 1)..].Trim();
            }

            return string.Empty;
        }

        /// <summary>解 JWT 载荷，取账号名和过期时间。解不出来不算错（后台会判 token 是否有效）</summary>
        private static (string Name, DateTime? Expires) ReadPayload(string payload)
        {
            string base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return (string.Empty, null);
            }

            JsonNode? root = JsonNode.Parse(Encoding.UTF8.GetString(bytes));
            string name = root?["userinfo"]?["name"]?.ToString() ?? string.Empty;
            DateTime? expires = root?["exp"] is JsonValue exp && exp.TryGetValue(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                : null;
            return (name, expires);
        }
    }
}
