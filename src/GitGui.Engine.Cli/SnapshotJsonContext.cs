using System.Text.Json.Serialization;

namespace GitGui.Engine.Cli;

[JsonSerializable(typeof(SnapshotManifest))]
internal partial class SnapshotJsonContext : JsonSerializerContext;
