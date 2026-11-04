using System.Globalization;
using System.Text.Json;
using DshSuperagent.Domain.Sources;

namespace DshSuperagent.Providers.Common;

public static class SourceJson
{
    public static string? String(this JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    public static JsonElement Child(this JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) ? value : default;
    public static bool Flag(this JsonElement element, string name) => element.Child(name).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(this JsonElement element) => element.ValueKind == JsonValueKind.Array
        ? element.EnumerateArray() : [];
    public static string Required(this Source source, string key) => source.GetConfig(key) is { Length: > 0 } text
        ? text.Trim() : throw new InvalidOperationException($"Source 缺少 {key} 配置。");
    public static bool Option(this Source source, string key, bool fallback = false) => bool.TryParse(source.GetConfig(key), out var value) ? value : fallback;
    public static int Number(this Source source, string key, int fallback, int min, int max) =>
        int.TryParse(source.GetConfig(key), out var value) ? Math.Clamp(value, min, max) : fallback;
    public static DateTimeOffset Time(string? value) => long.TryParse(value, out var milliseconds)
        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
        : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : throw new FormatException("来源消息缺少有效时间戳。");
    public static JsonElement Parse(string text)
    {
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }
}
