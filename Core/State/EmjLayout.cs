using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongHater.Core.State;

// Memory layout of the AddonEmj struct + the AtkValues/state-code/node ids the reader
// needs, loaded from resources/layouts/<variant>.json (schema in docs/REWORK_PLAN.md).
// Every number that belongs to the game client lives here, never in reader code.
// Unknown fields are null; the reader must tolerate that.
public sealed class EmjLayout
{
    public const string DefaultResourcePath = "resources/layouts/emj.json";

    // Widest read the struct reader will ever do, regardless of layout content.
    public const int MaxReadBytes = 0x1100;

    public required string Name { get; init; }

    public required string AddonName { get; init; }

    public required int TileIconBase { get; init; }

    public required int HandArray { get; init; }

    public required int HandSlots { get; init; }

    public required int?[] Scores { get; init; }

    public required int?[] DiscardCounts { get; init; }

    public required int?[] DiscardArrays { get; init; }

    public int DiscardArrayMaxLen { get; init; } = 24;

    public int? DoraIndicator { get; init; }

    public int? UraDoraIndicator { get; init; }

    public int? Melds { get; init; }

    public int? RiichiFlags { get; init; }

    public int? RoundWind { get; init; }

    public int? SeatWind { get; init; }

    public int? DealerSeat { get; init; }

    public int? Honba { get; init; }

    public int? RiichiSticks { get; init; }

    public int? WallRemaining { get; init; }

    public int StateCodeIndex { get; init; }

    public int WallCountIndex { get; init; } = 1;

    public required StateCodeTable StateCodes { get; init; }

    public required NodeTable Nodes { get; init; }

    // Bytes the struct reader must copy to cover every mapped offset (int32 fields).
    public int RequiredBytes
    {
        get
        {
            var end = this.HandArray + (this.HandSlots * 4);
            foreach (var o in this.Scores.Concat(this.DiscardCounts))
                end = Math.Max(end, (o ?? 0) + 4);
            foreach (var o in this.DiscardArrays)
                if (o is { } arr)
                    end = Math.Max(end, arr + (this.DiscardArrayMaxLen * 4));
            foreach (var o in new[] { this.DoraIndicator, this.UraDoraIndicator, this.Melds, this.RiichiFlags, this.RoundWind, this.SeatWind, this.DealerSeat, this.Honba, this.RiichiSticks, this.WallRemaining })
                end = Math.Max(end, (o ?? 0) + 4);
            return Math.Min(end, MaxReadBytes);
        }
    }

    public static EmjLayout Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<LayoutDto>(json, JsonOptions)
                  ?? throw new InvalidDataException("Layout JSON is empty.");
        var o = dto.Offsets ?? throw new InvalidDataException("Layout JSON has no 'offsets'.");
        return new EmjLayout
        {
            Name = dto.Name ?? "Emj",
            AddonName = dto.AddonName ?? "Emj",
            TileIconBase = dto.TileIconBase ?? throw new InvalidDataException("'tileIconBase' is required."),
            HandArray = ParseHex(o.HandArray) ?? throw new InvalidDataException("'offsets.handArray' is required."),
            HandSlots = o.HandSlots ?? 14,
            Scores = FourOffsets(o.Scores),
            DiscardCounts = FourOffsets(o.DiscardCounts),
            DiscardArrays = FourOffsets(o.DiscardArrays),
            DiscardArrayMaxLen = o.DiscardArrayMaxLen ?? 24,
            DoraIndicator = ParseHex(o.DoraIndicator),
            UraDoraIndicator = ParseHex(o.UraDoraIndicator),
            Melds = ParseHex(o.Melds),
            RiichiFlags = ParseHex(o.RiichiFlags),
            RoundWind = ParseHex(o.RoundWind),
            SeatWind = ParseHex(o.SeatWind),
            DealerSeat = ParseHex(o.DealerSeat),
            Honba = ParseHex(o.Honba),
            RiichiSticks = ParseHex(o.RiichiSticks),
            WallRemaining = ParseHex(o.WallRemaining),
            StateCodeIndex = dto.AtkValues?.StateCode ?? 0,
            WallCountIndex = dto.AtkValues?.WallCount ?? 1,
            StateCodes = new StateCodeTable(
                dto.StateCodes?.OurTurn ?? 6,
                dto.StateCodes?.OthersTurn ?? 15,
                dto.StateCodes?.CallPrompt ?? 19,
                dto.StateCodes?.Deal ?? 21,
                dto.StateCodes?.Score ?? 29,
                dto.StateCodes?.Win ?? 32),
            Nodes = new NodeTable(
                dto.Nodes?.HandSlotFirst ?? 134,
                dto.Nodes?.HandSlotDraw ?? 135,
                dto.Nodes?.HandSlotButton ?? 9,
                dto.Nodes?.CallList ?? "104/3",
                dto.Nodes?.RecapNext ?? 97),
        };
    }

    // Ships next to the dll (resources/layouts/emj.json); the embedded copy is the
    // fallback so a missing file never leaves the reader without offsets.
    public static EmjLayout LoadDefault(string? assemblyDirectory)
    {
        if (assemblyDirectory is not null)
        {
            var path = Path.Combine(assemblyDirectory, DefaultResourcePath);
            if (File.Exists(path))
                return Parse(File.ReadAllText(path));
        }

        return Parse(ReadEmbedded());
    }

    public static string ReadEmbedded()
    {
        var asm = typeof(EmjLayout).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("emj.json", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException("Embedded layout resource missing.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Raw icon id → tile using this layout's base (idx 34/35/36 = red 5m/5p/5s).
    public bool TryDecodeTile(int raw, out Tile tile) => TryDecodeTile(raw, this.TileIconBase, out tile);

    public static bool TryDecodeTile(int raw, int iconBase, out Tile tile)
    {
        tile = default;
        if (raw <= 0)
            return false;
        var idx = raw - iconBase;
        switch (idx)
        {
            case >= 0 and <= 33:
                tile = TileHelpers.FromIndex(idx);
                return true;
            case 34: tile = new Tile(TileSuit.Man, 5, isRedFive: true); return true;
            case 35: tile = new Tile(TileSuit.Pin, 5, isRedFive: true); return true;
            case 36: tile = new Tile(TileSuit.Sou, 5, isRedFive: true); return true;
            default: return false;
        }
    }

    // Base that decodes every populated hand slot. A client patch can shift the icon
    // ids by a few (self-heals within ±BaseSearchRadius, nearest wins, downward first);
    // null when no base fits, i.e. the array is not a tile array right now.
    public const int BaseSearchRadius = 8;

    // Below this many populated slots there is too little evidence to retune the base.
    public const int MinSlotsForRetune = 5;

    public int? ResolveBase(ReadOnlySpan<int> rawSlots, out bool shifted)
    {
        shifted = false;
        if (CountUndecodable(rawSlots, this.TileIconBase) == 0)
            return this.TileIconBase;

        var populated = 0;
        foreach (var raw in rawSlots)
            if (raw != 0)
                populated++;
        if (populated < MinSlotsForRetune)
            return null;

        for (var radius = 1; radius <= BaseSearchRadius; radius++)
        {
            if (CountUndecodable(rawSlots, this.TileIconBase - radius) == 0)
            {
                shifted = true;
                return this.TileIconBase - radius;
            }

            if (CountUndecodable(rawSlots, this.TileIconBase + radius) == 0)
            {
                shifted = true;
                return this.TileIconBase + radius;
            }
        }

        return null;
    }

    private static int CountUndecodable(ReadOnlySpan<int> rawSlots, int iconBase)
    {
        var bad = 0;
        foreach (var raw in rawSlots)
            if (raw != 0 && !TryDecodeTile(raw, iconBase, out _))
                bad++;
        return bad;
    }

    internal static int? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        return int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static int?[] FourOffsets(string?[]? values)
    {
        var result = new int?[4];
        if (values is null)
            return result;
        for (var i = 0; i < 4 && i < values.Length; i++)
            result[i] = ParseHex(values[i]);
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class LayoutDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("addonName")] public string? AddonName { get; set; }
        [JsonPropertyName("tileIconBase")] public int? TileIconBase { get; set; }
        [JsonPropertyName("offsets")] public OffsetsDto? Offsets { get; set; }
        [JsonPropertyName("atkValues")] public AtkValuesDto? AtkValues { get; set; }
        [JsonPropertyName("stateCodes")] public StateCodesDto? StateCodes { get; set; }
        [JsonPropertyName("nodes")] public NodesDto? Nodes { get; set; }
    }

    private sealed class OffsetsDto
    {
        [JsonPropertyName("handArray")] public string? HandArray { get; set; }
        [JsonPropertyName("handSlots")] public int? HandSlots { get; set; }
        [JsonPropertyName("scores")] public string?[]? Scores { get; set; }
        [JsonPropertyName("discardCounts")] public string?[]? DiscardCounts { get; set; }
        [JsonPropertyName("discardArrays")] public string?[]? DiscardArrays { get; set; }
        [JsonPropertyName("discardArrayMaxLen")] public int? DiscardArrayMaxLen { get; set; }
        [JsonPropertyName("doraIndicator")] public string? DoraIndicator { get; set; }
        [JsonPropertyName("uraDoraIndicator")] public string? UraDoraIndicator { get; set; }
        [JsonPropertyName("melds")] public string? Melds { get; set; }
        [JsonPropertyName("riichiFlags")] public string? RiichiFlags { get; set; }
        [JsonPropertyName("roundWind")] public string? RoundWind { get; set; }
        [JsonPropertyName("seatWind")] public string? SeatWind { get; set; }
        [JsonPropertyName("dealerSeat")] public string? DealerSeat { get; set; }
        [JsonPropertyName("honba")] public string? Honba { get; set; }
        [JsonPropertyName("riichiSticks")] public string? RiichiSticks { get; set; }
        [JsonPropertyName("wallRemaining")] public string? WallRemaining { get; set; }
    }

    private sealed class AtkValuesDto
    {
        [JsonPropertyName("stateCode")] public int? StateCode { get; set; }
        [JsonPropertyName("wallCount")] public int? WallCount { get; set; }
    }

    private sealed class StateCodesDto
    {
        [JsonPropertyName("ourTurn")] public int? OurTurn { get; set; }
        [JsonPropertyName("othersTurn")] public int? OthersTurn { get; set; }
        [JsonPropertyName("callPrompt")] public int? CallPrompt { get; set; }
        [JsonPropertyName("deal")] public int? Deal { get; set; }
        [JsonPropertyName("score")] public int? Score { get; set; }
        [JsonPropertyName("win")] public int? Win { get; set; }
    }

    private sealed class NodesDto
    {
        [JsonPropertyName("handSlotFirst")] public int? HandSlotFirst { get; set; }
        [JsonPropertyName("handSlotDraw")] public int? HandSlotDraw { get; set; }
        [JsonPropertyName("handSlotButton")] public int? HandSlotButton { get; set; }
        [JsonPropertyName("callList")] public string? CallList { get; set; }
        [JsonPropertyName("recapNext")] public int? RecapNext { get; set; }
    }
}

public readonly record struct StateCodeTable(int OurTurn, int OthersTurn, int CallPrompt, int Deal, int Score, int Win);

public readonly record struct NodeTable(int HandSlotFirst, int HandSlotDraw, int HandSlotButton, string CallList, int RecapNext);
