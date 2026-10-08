using System.Text.Json.Nodes;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 星火后台云变量表里的<b>一行</b> = 一个账号在一个环境下的一个 key（游戏的一段存档，如 <c>hero_s104</c>）。
    /// 字段名照后台接口返回的写（<c>key</c> / <c>value</c> / <c>row_id</c> / <c>data_type</c>）。
    /// </summary>
    public sealed class CloudSaveRow
    {
        /// <summary>云变量 key</summary>
        public required string Key { get; init; }

        /// <summary>后台行号，修改按它定位（实测是真实行号，单查与全查一致）</summary>
        public required string RowId { get; init; }

        /// <summary>数据类型，AzureSail 存档都是 <c>string</c>（存档 JSON 序列化成字符串存）。写回时原样回填</summary>
        public required string DataType { get; init; }

        /// <summary>值，原样保留 JSON 结构</summary>
        public JsonNode? Value { get; init; }

        /// <summary>值的大小（字节，按 JSON 文本算）</summary>
        public int Size => Value?.ToJsonString().Length ?? 0;

        /// <summary>值是不是空（null 或空字符串）——清过档的就是空</summary>
        public bool IsEmpty => Value is null || (Value is JsonValue v && v.TryGetValue(out string? text) && string.IsNullOrEmpty(text));

        /// <summary>从接口返回的一项解析；缺 key 或 row_id 的返回 null</summary>
        public static CloudSaveRow? FromJson(JsonNode? item)
        {
            if (item is not JsonObject obj)
                return null;

            string? key = AsText(obj["key"]);
            string? rowId = AsText(obj["row_id"]);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(rowId))
                return null;

            return new CloudSaveRow
            {
                Key = key,
                RowId = rowId,
                DataType = AsText(obj["data_type"]) ?? "string",
                Value = obj["value"]?.DeepClone(),
            };
        }

        /// <summary>备份文件里的形状（与 EditCloudStore 的快照格式一致，可直接拿去 update 恢复）</summary>
        public JsonObject ToJson() => new()
        {
            ["key"] = Key,
            ["row_id"] = RowId,
            ["data_type"] = DataType,
            ["value"] = Value?.DeepClone(),
        };

        /// <summary>row_id 可能是数字也可能是字符串，统一成字符串</summary>
        private static string? AsText(JsonNode? node) => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? text) => text,
            _ => node.ToJsonString().Trim('"'),
        };
    }
}
