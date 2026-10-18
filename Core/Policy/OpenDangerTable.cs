using System.Text.Json;

namespace MahjongHater.Core.Policy;

// Conditional deal-in rates, deal-in costs and tenpai rates counted over Phoenix replays by
// tools/open_hand_danger.py (merged by tools/make_open_danger.py into
// resources/policy/open_danger.json). Every rate assumes the seat is tenpai. The danger model
// keeps its riichi tables and scales them by the like-for-like ratio
// open-read rate / riichi rate, so kabe, live suji and dora still apply.
public sealed class OpenDangerTable
{
    public const string DefaultResourcePath = "resources/policy/open_danger.json";

    private static readonly Lazy<OpenDangerTable> EmbeddedTable = new(() => Parse(ReadEmbedded()));

    private readonly Dictionary<string, Dictionary<string, Cell>> danger;
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, Cell>>> tenpai;

    private OpenDangerTable(Dictionary<string, Dictionary<string, Cell>> danger,
        Dictionary<string, Dictionary<string, Dictionary<string, Cell>>> tenpai, int games)
    {
        this.danger = danger;
        this.tenpai = tenpai;
        this.Games = games;
    }

    public static OpenDangerTable Default => EmbeddedTable.Value;

    public int Games { get; }

    // Ratios outside this band come from thin or near-zero cells; they never get to swing a
    // tile by more than this.
    public const double MinRatio = 0.05;
    public const double MaxRatio = 4;

    // `generic` is the tile's class as the counter names it (genbutsu, honor:vis0..3,
    // suji|nonsuji:19|28|37|456), from the seat's own discards and the public tile count.
    // `flush` is "flush:in-suit" / "flush:off-suit" / "flush:honor" for a flush read.
    public double DangerRatio(HandRead read, string generic, string? flush)
    {
        if (generic == "genbutsu" || this.Rate("riichi", generic) is not { } riichi || riichi <= 0)
            return 1;
        var context = read.TableContext;
        double? rate = flush is not null
            ? this.Rate(context, $"{flush}|{generic}") ?? this.FlushFallback(context, flush, generic)
            : this.Rate(context, generic);
        return rate is { } r ? Math.Clamp(r / riichi, MinRatio, MaxRatio) : 1;
    }

    // Average ron payment of a hit against this read (dealer and non-dealer mixed), and the
    // same by dora visible in an open hand's melds (0..3+); null when not counted.
    public double? Cost(HandRead read) => this.CellOf(read.TableContext, "ALL")?.Cost;

    public double? CostByVisibleDora(int visibleDora) => this.CellOf("open-dora-visible", Math.Clamp(visibleDora, 0, 3).ToString(System.Globalization.CultureInfo.InvariantCulture))?.Cost;

    // Cost with this many dora showing relative to the average open hand (hit-weighted over
    // 0..3+), so a read's average cost can be scaled without counting its usual dora twice.
    public double VisibleDoraFactor(int visibleDora)
    {
        double hits = 0, paid = 0;
        for (var k = 0; k <= 3; k++)
            if (this.CellOf("open-dora-visible", k.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } c)
            {
                hits += c.N * c.Rate;
                paid += c.N * c.Rate * c.Cost;
            }

        return hits > 0 && paid > 0 && this.CostByVisibleDora(visibleDora) is { } cost ? cost / (paid / hits) : 1;
    }

    // P(hit | tenpai) of the tile that finishes a seat's two dragon sets.
    public double? YakumanTileRate() => this.CellOf("two-dragon-melds", "dragon")?.Rate;

    // Likelihood ratio on the tenpai odds for a run of `streak` trailing tsumogiri discards,
    // against the average seat with the same calls and phase. 1 when the table has no cell.
    public double TenpaiOddsRatio(int openMelds, int discards, int streak)
    {
        if (streak < 0)
            return 1;
        var kind = openMelds == 0 ? "dama" : $"open{Math.Min(openMelds, 4)}";
        var phase = discards <= 6 ? "early" : discards <= 11 ? "mid" : "late";
        if (!this.tenpai.TryGetValue(kind, out var byPhase) || !byPhase.TryGetValue(phase, out var cells)
            || !cells.TryGetValue("all", out var all) || !cells.TryGetValue($"tsg{Math.Min(streak, 3)}", out var run))
            return 1;
        static double Odds(double p) => Math.Clamp(p, 1e-4, 1 - 1e-4) / (1 - Math.Clamp(p, 1e-4, 1 - 1e-4));
        return Odds(run.Rate) / Odds(all.Rate);
    }

    // Number-tile class against a seat as the counter defines it: suji when the matching far
    // end(s) are in the seat's OWN discards (both ends for 4-6), honors by public copies.
    public static string GenericClass(Tile tile, bool[] ownDiscards, int publicCopies)
    {
        var kind = TileHelpers.ToIndex(tile);
        if (ownDiscards[kind])
            return "genbutsu";
        if (tile.IsHonor)
            return $"honor:vis{Math.Clamp(publicCopies, 0, 3)}";
        var n = tile.Number;
        var base1 = kind - (n - 1);
        var suji = n <= 3 ? ownDiscards[base1 + n + 2]
            : n >= 7 ? ownDiscards[base1 + n - 4]
            : ownDiscards[base1 + n - 4] && ownDiscards[base1 + n + 2];
        var rank = n is 1 or 9 ? "19" : n is 2 or 8 ? "28" : n is 3 or 7 ? "37" : "456";
        return $"{(suji ? "suji" : "nonsuji")}:{rank}";
    }

    public static string? FlushClass(Tile tile, HandRead read) => read.Kind != ReadKind.Flush ? null
        : tile.IsHonor ? "flush:honor" : tile.Suit == read.FlushSuit ? "flush:in-suit" : "flush:off-suit";

    public static OpenDangerTable LoadDefault(string? assemblyDirectory)
    {
        if (assemblyDirectory is not null)
        {
            var path = Path.Combine(assemblyDirectory, DefaultResourcePath);
            if (File.Exists(path))
                return Parse(File.ReadAllText(path));
        }

        return Default;
    }

    public static OpenDangerTable Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static Cell CellFrom(JsonElement e) => new(e.GetProperty("n").GetInt64(), e.GetProperty("rate").GetDouble(),
            e.TryGetProperty("cost", out var c) ? c.GetDouble() : 0);
        var danger = new Dictionary<string, Dictionary<string, Cell>>(StringComparer.Ordinal);
        foreach (var ctx in (root.TryGetProperty("danger", out var d) ? d : throw new InvalidDataException("open_danger.json: danger missing")).EnumerateObject())
            danger[ctx.Name] = ctx.Value.EnumerateObject().ToDictionary(c => c.Name, c => CellFrom(c.Value), StringComparer.Ordinal);
        var tenpai = new Dictionary<string, Dictionary<string, Dictionary<string, Cell>>>(StringComparer.Ordinal);
        if (root.TryGetProperty("tenpai", out var t))
            foreach (var kind in t.EnumerateObject())
                tenpai[kind.Name] = kind.Value.EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.EnumerateObject().ToDictionary(c => c.Name, c => CellFrom(c.Value), StringComparer.Ordinal), StringComparer.Ordinal);
        if (!danger.ContainsKey("riichi"))
            throw new InvalidDataException("open_danger.json: no riichi baseline");
        return new OpenDangerTable(danger, tenpai, root.TryGetProperty("games", out var g) ? g.GetInt32() : 0);
    }

    public static string ReadEmbedded()
    {
        var asm = typeof(OpenDangerTable).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("open_danger.json", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException("Embedded open_danger.json resource missing.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Cell? CellOf(string context, string cls) =>
        this.danger.TryGetValue(context, out var cells) && cells.TryGetValue(cls, out var cell) ? cell : null;

    private double? Rate(string context, string cls) => this.CellOf(context, cls)?.Rate;

    // A thin flush × suji cell: the suit class's own shift times the flush read's suji shift.
    private double? FlushFallback(string context, string flush, string generic)
    {
        if (this.Rate(context, flush) is not { } byFlush || this.CellOf(context, "ALL") is not { } all
            || this.Rate(context, "g:" + generic) is not { } byGeneric)
            return null;
        // ALL counts genbutsu (always 0); compare against the rate over non-genbutsu tiles.
        var genbutsu = this.CellOf(context, "genbutsu")?.N ?? 0;
        var nonGenbutsu = all.N - genbutsu > 0 ? all.Rate * all.N / (all.N - genbutsu) : 0;
        return nonGenbutsu > 0 ? byGeneric * byFlush / nonGenbutsu : null;
    }

    private sealed record Cell(long N, double Rate, double Cost);
}
