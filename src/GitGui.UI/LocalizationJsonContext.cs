using System.Text.Json.Serialization;

namespace GitGui.UI;

// Locale tables have a fixed schema; deserialize without runtime contract discovery.
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class LocalizationJsonContext : JsonSerializerContext;
