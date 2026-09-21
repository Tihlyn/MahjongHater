using System.Text.Json;
using System.Text.Json.Serialization;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

// Corpus interchange: one complete public StateSnapshot per JSONL line. Tiles use
// existing 1m/0p/7z notation. Explicit converters preserve red copies in melds.
public static class SnapshotJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(StateSnapshot state) => JsonSerializer.Serialize(state, Options);
    public static StateSnapshot Deserialize(string json) => JsonSerializer.Deserialize<StateSnapshot>(json, Options)
        ?? throw new JsonException("Null snapshot.");

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TileConverter());
        options.Converters.Add(new MeldConverter());
        return options;
    }

    private sealed class TileConverter : JsonConverter<Tile>
    {
        public override Tile Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Tile.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, Tile tile, JsonSerializerOptions options) => writer.WriteStringValue(tile.ToString());
    }

    private sealed class MeldConverter : JsonConverter<Meld>
    {
        public override Meld Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var data = JsonSerializer.Deserialize<MeldData>(ref reader, options) ?? throw new JsonException("Null meld.");
            var meld = new Meld(data.Type, data.Tiles, data.IsOpen);
            data.Tiles.Order().ToArray().CopyTo(meld.Tiles, 0);
            return meld;
        }
        public override void Write(Utf8JsonWriter writer, Meld meld, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new MeldData(meld.Type, meld.Tiles, meld.IsOpen), options);
    }

    private sealed record MeldData(MeldType Type, Tile[] Tiles, bool IsOpen);
}
