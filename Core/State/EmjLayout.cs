using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongHater.Core.State;

// Memory layout of the AddonEmj struct + the AtkValues/state-code/node ids the reader
// needs, loaded from resources/layouts/<variant>.json (schema: docs/REWORK_PLAN.md,
// offsets: docs/EMJ_STRUCT.md). Every number that belongs to the game client lives
// here, never in reader code. Unknown JSON keys are ignored; missing ones stay null.
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

    // Per relative seat (0 = us, 1 = shimocha, 2 = toimen, 3 = kamicha).
    public required int?[] Scores { get; init; }

    public required int?[] DiscardCounts { get; init; }

    public required int?[] DiscardArrays { get; init; }

    public int DiscardArrayMaxLen { get; init; } = 24;

    public required int?[] PointDifferences { get; init; }

    public required int?[] ClosedTileCounts { get; init; }

    public required int?[] MeldCounts { get; init; }

    public required int?[] MeldTileIndexArrays { get; init; }

    public int MeldTileIndexSlots { get; init; } = 4;

    public int MeldTileIndexEmpty { get; init; } = -1;

    public int MeldTileIndexUnknown { get; init; } = 255;

    public required int?[] MeldFromDirectionBytes { get; init; }

    public required int?[] RiichiDiscardIndexBytes { get; init; }

    public int RiichiNone { get; init; } = 255;

    public int? DoraIndicator { get; init; }

    public int? DoraIndicatorCount { get; init; }

    public int? UraDoraIndicator { get; init; }

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

    // Bytes the struct reader must copy to cover every mapped offset.
    public int RequiredBytes
    {
        get
        {
            var end = this.HandArray + (this.HandSlots * 4);
            foreach (var o in this.Scores.Concat(this.PointDifferences))
                end = Math.Max(end, (o ?? 0) + 4);
            foreach (var o in this.DiscardCounts.Concat(this.ClosedTileCounts).Concat(this.MeldCounts).Concat(this.RiichiDiscardIndexBytes))
                end = Math.Max(end, (o ?? 0) + 1);
            foreach (var o in this.MeldTileIndexArrays)
                if (o is { } arr)
                    end = Math.Max(end, arr + (this.MeldTileIndexSlots * 4));
            foreach (var o in this.MeldFromDirectionBytes)
                if (o is { } dir)
                    end = Math.Max(end, dir + this.MeldTileIndexSlots);
            foreach (var o in this.DiscardArrays)
                if (o is { } arr)
                    end = Math.Max(end, arr + (this.DiscardArrayMaxLen * 4));
            foreach (var o in new[] { this.DoraIndicator, this.UraDoraIndicator, this.RoundWind, this.SeatWind, this.DealerSeat, this.Honba, this.RiichiSticks, this.WallRemaining })
                end = Math.Max(end, (o ?? 0) + 4);
            if (this.DoraIndicatorCount is { } dc)
                end = Math.Max(end, dc + 1);
            return Math.Min(end, MaxReadBytes);
        }
    }

    public static EmjLayout Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<LayoutDto>(json, JsonOptions)
                  ?? throw new InvalidDataException("Layout JSON is empty.");
        var o = dto.Offsets ?? throw new InvalidDataException("Layout JSON has no 'offsets'.");
        var melds = o.Melds;
        var riichi = o.RiichiFlags;
        var codes = dto.StateCodes;
        var nodes = dto.Nodes;
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
            PointDifferences = FourOffsets(o.PointDifferences),
            ClosedTileCounts = FourOffsets(melds?.ClosedTileCountBytes),
            MeldCounts = FourOffsets(melds?.CountBytes),
            MeldTileIndexArrays = FourOffsets(melds?.TileIndexArrays),
            MeldTileIndexSlots = melds?.TileIndexSlots ?? 4,
            MeldTileIndexEmpty = melds?.TileIndexEmpty ?? -1,
            MeldTileIndexUnknown = melds?.TileIndexUnknown ?? melds?.TileIndexChi ?? 255,
            MeldFromDirectionBytes = FourOffsets(melds?.FromDirectionBytes),
            RiichiDiscardIndexBytes = FourOffsets(riichi?.DiscardIndexBytes),
            RiichiNone = riichi?.None ?? 255,
            DoraIndicator = ParseHex(o.DoraIndicator),
            DoraIndicatorCount = ParseHex(o.DoraIndicatorCount),
            UraDoraIndicator = ParseHex(o.UraDoraIndicator),
            RoundWind = ParseHex(o.RoundWind),
            SeatWind = ParseHex(o.SeatWind),
            DealerSeat = ParseHex(o.DealerSeat),
            Honba = ParseHex(o.Honba),
            RiichiSticks = ParseHex(o.RiichiSticks),
            WallRemaining = ParseHex(o.WallRemaining),
            StateCodeIndex = dto.AtkValues?.StateCode ?? 0,
            WallCountIndex = dto.AtkValues?.WallCount ?? 1,
            StateCodes = new StateCodeTable(
                Deal: codes?.Deal ?? 2,
                Draw: codes?.Draw ?? 5,
                OurTurn: codes?.OurTurn ?? 6,
                Discard: codes?.Discard ?? 8,
                DiscardHighlightReset: codes?.DiscardHighlightReset ?? 12,
                Meld: codes?.Meld ?? 13,
                OthersTurn: codes?.OthersTurn ?? 15,
                CallPrompt: codes?.CallPrompt ?? 19,
                PostMeldRefresh: codes?.PostMeldRefresh ?? 21,
                CallWindowOpened: codes?.CallWindowOpened ?? 22,
                CallOptions: codes?.CallOptions ?? 23,
                PostWin: codes?.PostWin ?? 27,
                Score: codes?.Score ?? 29,
                Win: codes?.Win ?? 32),
            Nodes = new NodeTable(
                HandSlotFirst: nodes?.HandSlotFirst ?? 134,
                HandSlotDraw: nodes?.HandSlotDraw ?? 135,
                HandSlotButton: nodes?.HandSlotButton ?? 9,
                CallList: nodes?.CallList ?? "1/46/104/3",
                RecapNext: nodes?.RecapNext ?? 97,
                SeatWindTexts: FourPaths(nodes?.SeatWindTexts),
                ScoreTexts: FourPaths(nodes?.ScoreTexts),
                HonbaText: nodes?.HonbaText,
                RoundWindText: nodes?.RoundWindText,
                WallCounterDigits: nodes?.WallCounterDigits ?? [],
                ChiShapeButtons: nodes?.ChiShapeButtons ?? ["1/46/52/5", "1/46/52/6", "1/46/52/7", "1/46/52/8"],
                ChiShapeCancel: nodes?.ChiShapeCancel ?? "1/46/52/11",
                ResultBanners: FourPaths(nodes?.ResultBanners)),
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
        return TryTileFromIndex(raw - iconBase, out tile);
    }

    // 34-index (same space as icon − base; the struct's meld records use it directly).
    public static bool TryTileFromIndex(int idx, out Tile tile)
    {
        tile = default;
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

    private static string?[] FourPaths(string?[]? values)
    {
        var result = new string?[4];
        if (values is null)
            return result;
        for (var i = 0; i < 4 && i < values.Length; i++)
            result[i] = string.IsNullOrWhiteSpace(values[i]) ? null : values[i];
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
        [JsonPropertyName("pointDifferences")] public string?[]? PointDifferences { get; set; }
        [JsonPropertyName("melds")] public MeldsDto? Melds { get; set; }
        [JsonPropertyName("riichiFlags")] public RiichiDto? RiichiFlags { get; set; }
        [JsonPropertyName("doraIndicator")] public string? DoraIndicator { get; set; }
        [JsonPropertyName("doraIndicatorCount")] public string? DoraIndicatorCount { get; set; }
        [JsonPropertyName("uraDoraIndicator")] public string? UraDoraIndicator { get; set; }
        [JsonPropertyName("roundWind")] public string? RoundWind { get; set; }
        [JsonPropertyName("seatWind")] public string? SeatWind { get; set; }
        [JsonPropertyName("dealerSeat")] public string? DealerSeat { get; set; }
        [JsonPropertyName("honba")] public string? Honba { get; set; }
        [JsonPropertyName("riichiSticks")] public string? RiichiSticks { get; set; }
        [JsonPropertyName("wallRemaining")] public string? WallRemaining { get; set; }
    }

    private sealed class MeldsDto
    {
        [JsonPropertyName("tileIndexArrays")] public string?[]? TileIndexArrays { get; set; }
        [JsonPropertyName("tileIndexSlots")] public int? TileIndexSlots { get; set; }
        [JsonPropertyName("tileIndexEmpty")] public int? TileIndexEmpty { get; set; }
        [JsonPropertyName("tileIndexUnknown")] public int? TileIndexUnknown { get; set; }
        // Legacy layout key; the value was never exclusive to chi.
        [JsonPropertyName("tileIndexChi")] public int? TileIndexChi { get; set; }
        [JsonPropertyName("fromDirectionBytes")] public string?[]? FromDirectionBytes { get; set; }
        [JsonPropertyName("countBytes")] public string?[]? CountBytes { get; set; }
        [JsonPropertyName("closedTileCountBytes")] public string?[]? ClosedTileCountBytes { get; set; }
    }

    private sealed class RiichiDto
    {
        [JsonPropertyName("discardIndexBytes")] public string?[]? DiscardIndexBytes { get; set; }
        [JsonPropertyName("none")] public int? None { get; set; }
    }

    private sealed class AtkValuesDto
    {
        [JsonPropertyName("stateCode")] public int? StateCode { get; set; }
        [JsonPropertyName("wallCount")] public int? WallCount { get; set; }
    }

    private sealed class StateCodesDto
    {
        [JsonPropertyName("deal")] public int? Deal { get; set; }
        [JsonPropertyName("draw")] public int? Draw { get; set; }
        [JsonPropertyName("ourTurn")] public int? OurTurn { get; set; }
        [JsonPropertyName("discard")] public int? Discard { get; set; }
        [JsonPropertyName("discardHighlightReset")] public int? DiscardHighlightReset { get; set; }
        [JsonPropertyName("meld")] public int? Meld { get; set; }
        [JsonPropertyName("othersTurn")] public int? OthersTurn { get; set; }
        [JsonPropertyName("callPrompt")] public int? CallPrompt { get; set; }
        [JsonPropertyName("postMeldRefresh")] public int? PostMeldRefresh { get; set; }
        [JsonPropertyName("callWindowOpened")] public int? CallWindowOpened { get; set; }
        [JsonPropertyName("callOptions")] public int? CallOptions { get; set; }
        [JsonPropertyName("postWin")] public int? PostWin { get; set; }
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
        [JsonPropertyName("seatWindTexts")] public string?[]? SeatWindTexts { get; set; }
        [JsonPropertyName("scoreTexts")] public string?[]? ScoreTexts { get; set; }
        [JsonPropertyName("honbaText")] public string? HonbaText { get; set; }
        [JsonPropertyName("roundWindText")] public string? RoundWindText { get; set; }
        [JsonPropertyName("wallCounterDigits")] public string[]? WallCounterDigits { get; set; }
        [JsonPropertyName("chiShapeButtons")] public string[]? ChiShapeButtons { get; set; }
        [JsonPropertyName("chiShapeCancel")] public string? ChiShapeCancel { get; set; }
        [JsonPropertyName("resultBanners")] public string?[]? ResultBanners { get; set; }
    }
}

// AtkValues[0] values (docs/EMJ_STRUCT.md, "State codes seen").
public readonly record struct StateCodeTable(
    int Deal, int Draw, int OurTurn, int Discard, int DiscardHighlightReset, int Meld, int OthersTurn,
    int CallPrompt, int PostMeldRefresh, int CallWindowOpened, int CallOptions, int PostWin, int Score, int Win);

// Node ids / Cartographer-style paths ("1/46/104/3" = NodeIds root→…→list) the operator
// and overlay act on or read text from.
public readonly record struct NodeTable(
    int HandSlotFirst, int HandSlotDraw, int HandSlotButton, string CallList, int RecapNext,
    string?[] SeatWindTexts, string?[] ScoreTexts, string? HonbaText, string? RoundWindText, string[] WallCounterDigits,
    string[] ChiShapeButtons, string? ChiShapeCancel,
    string?[] ResultBanners);
