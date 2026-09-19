using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Contract for the decision layer (Phase 2). Pure C#: no Dalamud types, no game memory.
// Every sub-policy is constructor-injected into DecisionPolicy and testable alone.
// See docs/REWORK_PLAN.md.

public enum ActionKind
{
    None,
    Discard,
    Pon,
    Chi,
    MinKan,
    AnKan,
    ShouMinKan,
    Riichi,      // declare riichi, discarding Tile
    Tsumo,
    Ron,
    Pass,
}

// One line of the explanation shown to the player, in the order the policy decided things.
public sealed record Reason(string Stage, string Display);

// A candidate discard with everything the UI wants to show and the policy used to rank it.
public sealed record DiscardCandidate(
    Tile Tile,
    int ShantenAfter,
    int Ukeire,                 // Σ live copies over useful kinds
    long Ukeire2,               // 2-step ukeire (0 when not computed)
    double Value,               // rough han-ish value of the kept hand
    double DealInRisk,          // 0..1 aggregated over opponents from IOpponentModel
    double Score,               // final ranking key, higher is better
    string Note);               // short human tag, e.g. "isolated honor", "genbutsu vs S"

public sealed record ActionChoice(
    ActionKind Kind,
    Tile? Tile,                           // discard (or riichi discard); call tile for pon/chi/kan
    Meld? Call,                           // the meld to form for Pon/Chi/*Kan
    string Summary,                       // one line for the header
    IReadOnlyList<Reason> Steps,
    IReadOnlyList<DiscardCandidate> Candidates)   // ranked best-first; empty for non-discard actions
{
    public static ActionChoice Pass(string why) =>
        new(ActionKind.Pass, null, null, why, [new Reason("pass", why)], []);

    public static ActionChoice NoneYet(string why) =>
        new(ActionKind.None, null, null, why, [], []);
}

public enum PushFoldStance { Push, Fold }

public sealed record CallDecision(bool Accept, ActionKind Kind, Meld? Meld, Reason Reason)
{
    public static CallDecision Decline(string why) => new(false, ActionKind.Pass, null, new Reason("call", why));
}

public interface IOpponentModel
{
    // Called once per snapshot before any query. Idempotent for the same Sequence.
    void Update(StateSnapshot state);

    // 0..1 for relative seat 1..3.
    double TenpaiProbability(int seat);

    // 0..1 chance that discarding tile deals into seat, given what is visible.
    double Danger(Tile tile, int seat);

    // Expected points lost by discarding tile, summed over opponents.
    double ExpectedDealInCost(Tile tile);
}

public interface IDiscardPolicy
{
    // Requires a discardable state (14 closed tiles, or 13 + a claimed tile with Legal.Discard).
    // Returns candidates ranked best-first; Score already includes DealInRisk weighting.
    IReadOnlyList<DiscardCandidate> Rank(StateSnapshot state, IOpponentModel opponents, CancellationToken ct);
}

public interface IPushFoldPolicy
{
    PushFoldStance Evaluate(StateSnapshot state, IOpponentModel opponents, DiscardCandidate best, out Reason reason);
}

public interface ICallPolicy
{
    // Only invoked when state.Legal has any of Pon/Chi/MinKan/AnKan/ShouMinKan.
    CallDecision Evaluate(StateSnapshot state, IOpponentModel opponents, CancellationToken ct);
}

public interface IRiichiPolicy
{
    // Only invoked when state.Can(LegalAction.Riichi) and best.ShantenAfter == 0.
    bool ShouldDeclare(StateSnapshot state, IOpponentModel opponents, DiscardCandidate best, out Reason reason);
}

public interface IPolicy
{
    // The single entry point AnalysisService calls off-thread. Must be safe to call
    // concurrently for different snapshots and must honour ct promptly.
    ActionChoice Choose(StateSnapshot state, CancellationToken ct);
}

// All tunables in one place; defaults are the shipped behaviour. JSON-loadable later.
public sealed record PolicyWeights
{
    public double TenpaiValueWeight { get; init; } = 0.35;   // mirrors HandAnalyzer.Tuning
    public double OpenYakulessPenalty { get; init; } = 0.05;
    public int UkeireWeight { get; init; } = 10_000;
    public double KeptValueWeight { get; init; } = 50;
    public int RiichiMinWall { get; init; } = 4;
    public int RiichiMinUkeire { get; init; } = 4;
    public double DealInRiskWeight { get; init; } = 1.0;     // scales ExpectedDealInCost in the ranking
    public double FoldTenpaiThreshold { get; init; } = 0.6;  // fold when an opponent's tenpai prob ≥ this and we're far
    public int FoldMinShanten { get; init; } = 2;            // ...and our best shanten-after ≥ this
    public double SujiDiscount { get; init; } = 0.5;
    public double GenbutsuDanger { get; init; } = 0.0;
    public int MinHanDoman { get; init; } = 1;               // Doman requires a yaku; keep as data
    public double FoldMinValue { get; init; } = 2;
    public double TenpaiBase { get; init; } = 0.05;
    public double TenpaiPerDiscard { get; init; } = 0.025;
    public double TenpaiPerOpenMeld { get; init; } = 0.12;
    public double TenpaiEarlyOutsideWeight { get; init; } = 0.08;
    public double TenpaiLateMiddleWeight { get; init; } = 0.12;
    public double KabeDiscount { get; init; } = 0.4;
    public double HonorDanger { get; init; } = 0.16;
    public double TerminalDanger { get; init; } = 0.12;
    public double EdgeDanger { get; init; } = 0.18;
    public double MiddleDanger { get; init; } = 0.24;
    public double OpponentBaseValue { get; init; } = 2000;
    public double OpponentMeldValue { get; init; } = 800;
    public double OpponentRiichiValue { get; init; } = 2000;
    public double OpponentDoraValue { get; init; } = 1000;

    public static PolicyWeights Default { get; } = new();
}
