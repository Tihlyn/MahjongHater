using System.Text.Json;
using System.Text.Json.Serialization;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Tests.Policy.Golden;

// Community / book positions with a consensus answer (docs/DEFENSE_PLAN.md §3.8). The JSON
// schema is the one the Discord sweep writes (docs/research/wwyd_positions.json); this loader
// turns each situation into a StateSnapshot so DecisionPolicy can be scored against it.
public sealed record GoldenPosition(
    string Id,
    string Source,
    string? Channel,
    GoldenSituation Situation,
    GoldenConsensus Consensus,
    [property: JsonPropertyName("transcription_confidence")] string? TranscriptionConfidence)
{
    public bool IsHighConfidence => string.Equals(this.Consensus.Confidence, "high", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(this.TranscriptionConfidence, "low", StringComparison.OrdinalIgnoreCase);
}

public sealed record GoldenSituation(
    string? RoundWind,
    string? SeatWind,
    int DealerSeat,
    int Turn,
    int? WallRemaining,
    string Hand,
    string? Drawn,
    string[]? OurMelds,
    string? DoraIndicators,
    bool OurRiichi,
    int[]? Scores,
    GoldenSeat[]? Seats,
    int? HandNumber,
    int? HandsInMatch);

public sealed record GoldenSeat(int Seat, string? Discards, int RiichiIndex = -1, string[]? Melds = null);

public sealed record GoldenConsensus(
    string Action,        // discard | riichi | fold | call | pass | dama
    string? Tile,
    string? Stance,       // push | fold | borderline
    string? Confidence,   // high | medium
    string? Rationale,
    string[]? Alternatives,
    string? Dissent);

public static class GoldenPositions
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<GoldenPosition> Load(string path) =>
        JsonSerializer.Deserialize<List<GoldenPosition>>(File.ReadAllText(path), Options) ?? [];

    // Every *.json under the test project's Policy/Golden folder plus the sweep output in
    // docs/research when it exists (positions there are copied in for CI once reviewed).
    public static IReadOnlyList<(string File, GoldenPosition Position)> LoadAll()
    {
        var root = FindRepoRoot();
        var files = new List<string>();
        var golden = Path.Combine(root, "MahjongHater.Tests", "Policy", "Golden");
        if (Directory.Exists(golden))
            files.AddRange(Directory.GetFiles(golden, "*.json"));
        return files.SelectMany(f => Load(f).Select(p => (Path.GetFileName(f), p))).ToList();
    }

    public static StateSnapshot ToSnapshot(GoldenSituation s)
    {
        var hand = TestTiles.Parse(s.Hand);
        Tile? drawn = null;
        if (!string.IsNullOrWhiteSpace(s.Drawn))
        {
            drawn = Tile.Parse(s.Drawn);
            if (hand.Count + 3 * (s.OurMelds?.Length ?? 0) < 14)
                hand.Add(drawn.Value);
        }
        else if (hand.Count + 3 * (s.OurMelds?.Length ?? 0) == 14)
        {
            drawn = hand[^1];
        }

        var seats = new SeatState[4];
        var ourDiscards = Math.Max(0, s.Turn - 1);
        seats[0] = new SeatState(0, [], [], s.OurRiichi, -1, s.Scores is { Length: 4 } sc ? sc[0] : 25000) { DiscardCount = ourDiscards };
        for (var i = 1; i <= 3; i++)
        {
            var g = s.Seats?.FirstOrDefault(x => x.Seat == i);
            var discards = g?.Discards is { } d ? TestTiles.Parse(d) : [];
            var melds = (g?.Melds ?? []).Select(ParseMeld).ToList();
            var riichi = g is { RiichiIndex: >= 0 };
            seats[i] = new SeatState(i, discards, melds, riichi, g?.RiichiIndex ?? -1, s.Scores is { Length: 4 } sc2 ? sc2[i] : 25000);
        }

        var ourMelds = (s.OurMelds ?? []).Select(ParseMeld).ToList();
        var legal = LegalAction.Discard | (s.OurRiichi || ourMelds.Any(m => m.IsOpen) ? LegalAction.None : LegalAction.Riichi);
        return StateSnapshot.Empty with
        {
            Sequence = 1,
            Phase = GamePhase.OurTurn,
            Hand = hand,
            DrawnTile = drawn,
            OurMelds = ourMelds,
            Seats = seats,
            DoraIndicators = string.IsNullOrWhiteSpace(s.DoraIndicators) ? [] : TestTiles.Parse(s.DoraIndicators),
            RoundWind = ParseWind(s.RoundWind) ?? Wind.East,
            SeatWind = ParseWind(s.SeatWind) ?? Wind.East,
            DealerSeat = s.DealerSeat,
            WallRemaining = s.WallRemaining ?? Math.Max(0, 70 - 4 * ourDiscards),
            OurRiichi = s.OurRiichi,
            Legal = legal,
            HandNumber = s.HandNumber ?? 0,
            Ruleset = new RulesetOptions(true, s.HandsInMatch ?? 8),
        };
    }

    // "pon 5z", "chi 456p", "kan 5z" (open), "ankan 5z".
    public static Meld ParseMeld(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new FormatException($"meld '{text}'");
        var tiles = TestTiles.Parse(parts[1]);
        return parts[0].ToLowerInvariant() switch
        {
            "pon" => Meld.MakePon(tiles[0], true),
            "chi" => new Meld(MeldType.Chi, tiles.ToArray(), true),
            "kan" or "daiminkan" or "minkan" => new Meld(MeldType.Daiminkan, Enumerable.Repeat(tiles[0], 4).ToArray(), true),
            "ankan" => new Meld(MeldType.Ankan, Enumerable.Repeat(tiles[0], 4).ToArray(), false),
            _ => throw new FormatException($"meld kind '{parts[0]}'"),
        };
    }

    public static Wind? ParseWind(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "E" or "EAST" => Wind.East,
        "S" or "SOUTH" => Wind.South,
        "W" or "WEST" => Wind.West,
        "N" or "NORTH" => Wind.North,
        _ => null,
    };

    // Compare a policy decision with the consensus; null = agrees, else why not.
    public static string? Disagreement(GoldenPosition position, ActionChoice choice)
    {
        var c = position.Consensus;
        var tile = string.IsNullOrWhiteSpace(c.Tile) ? (Tile?)null : Tile.Parse(c.Tile);
        var stance = choice.Steps.FirstOrDefault(s => s.Stage == "push/fold")?.Display ?? string.Empty;
        var folded = stance.StartsWith("Fold", StringComparison.OrdinalIgnoreCase)
                     || choice.Steps.Any(s => s.Stage == "discard" && s.Display.StartsWith("Fold", StringComparison.OrdinalIgnoreCase));
        switch (c.Action.ToLowerInvariant())
        {
            case "discard":
                if (!choice.IsDiscard)
                    return $"expected discard {c.Tile}, got {choice.Kind}";
                if (tile is { } t && !(choice.Tile is { } chosen && TileHelpers.SameKind(chosen, t)))
                    return $"expected discard {c.Tile}, got {choice.Tile}";
                if (string.Equals(c.Stance, "fold", StringComparison.OrdinalIgnoreCase) && !folded)
                    return $"expected a fold stance ({stance})";
                if (string.Equals(c.Stance, "push", StringComparison.OrdinalIgnoreCase) && folded)
                    return $"expected a push stance ({stance})";
                return null;
            case "fold":
                // Cutting a tile at rank B or safer while keeping the hand is how a fold
                // starts (the budget only allows safe tiles); count it as agreement.
                var chosenSafe = choice.Candidates.Count > 0 && choice.Candidates[0].DangerSeat >= 1
                                 && choice.Candidates[0].DangerRank <= DangerRank.B;
                if (!folded && !chosenSafe)
                    return $"expected fold, policy pushed ({stance}) with {choice.Tile}";
                if (tile is { } ft && !(choice.Tile is { } fc && TileHelpers.SameKind(fc, ft)))
                    return $"folded with {choice.Tile}, consensus {c.Tile}";
                return null;
            case "riichi":
                if (choice.Kind != ActionKind.Riichi)
                    return $"expected riichi, got {choice.Kind} {choice.Tile}";
                if (tile is { } rt && !(choice.Tile is { } rc && TileHelpers.SameKind(rc, rt)))
                    return $"riichi on {choice.Tile}, consensus {c.Tile}";
                return null;
            case "dama":
                if (choice.Kind == ActionKind.Riichi)
                    return "expected dama, policy declared riichi";
                if (tile is { } dt && !(choice.Tile is { } dc && TileHelpers.SameKind(dc, dt)))
                    return $"dama discarding {choice.Tile}, consensus {c.Tile}";
                return null;
            case "call":
                return choice.IsCall ? null : $"expected a call, got {choice.Kind}";
            case "pass":
                return choice.Kind == ActionKind.Pass ? null : $"expected pass, got {choice.Kind}";
            default:
                return $"unknown consensus action '{c.Action}'";
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MahjongHater.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
