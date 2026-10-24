using System.Text.Json.Serialization;
using GitGui.Core;

namespace GitGui.Infrastructure;

[JsonSerializable(typeof(AppearancePreferences.Preference))]
[JsonSerializable(typeof(UiPreferences))]
[JsonSerializable(typeof(string))]
internal partial class SettingsJsonContext : JsonSerializerContext;
