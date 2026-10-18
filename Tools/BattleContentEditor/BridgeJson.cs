using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

// JSON is a transport adapter, separate from all rules. Keep dictionaries/lists and exact integers
// so the existing playtest protocol receives the same shapes as the original Mono host.
internal sealed class BridgeJson
{
    private const int MaxCharacters = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    { MaxDepth = 100, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal string Serialize(object value)
    {
        string result = JsonSerializer.Serialize(value, Options);
        if (result.Length > MaxCharacters) throw new ArgumentException("JSON 结果过大。");
        return result;
    }

    internal object DeserializeObject(string value)
    {
        if (value.Length > MaxCharacters) throw new ArgumentException("JSON 请求过大。");
        using (var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 100 }))
            return Read(document.RootElement);
    }

    private static object Read(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) result[property.Name] = Read(property.Value);
                return result;
            case JsonValueKind.Array: return element.EnumerateArray().Select(Read).ToArray();
            case JsonValueKind.String: return element.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer)) return integer;
                if (element.TryGetDecimal(out decimal number)) return number;
                throw new ArgumentException("JSON 数值超出允许范围。");
            default: throw new ArgumentException("JSON 类型无效。");
        }
    }
}
