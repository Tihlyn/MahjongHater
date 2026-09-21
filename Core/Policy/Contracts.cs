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
    string Note)                // short human tag, e.g. "isolated honor", "genbutsu vs S"
{
    // Winning tiles after this discard when ShantenAfter == 0; empty otherwise.
    public IReadOnlyList<Tile> Waits { get; init; } = [];

    // Deal-in chance of this tile against the primary threat, assuming that seat is tenpai
    // (conditional, unlike DealInRisk), with its rank and the model's one-line reason.
    public double Danger { get; init; }

    public DangerRank DangerRank { get; init; } = DangerRank.S;

    public string DangerNote { get; init; } = string.Empty;

    public int DangerSeat { get; init; } = -1;

    // Defense v2: the hand's worth in points after this discard (tenpai: expected ron
    // value over the waits; else an estimate), the chance of cashing it, and the
    // point-EV that ranks candidates inside a shanten level.
    public double ValuePoints { get; init; }

    public int MinPoints { get; init; }

    public double WinProbability { get; init; }

    public double ExpectedValue { get; init; }
}

// What the hand looks like right now, independent of the action: shown between turns
// too, when nothing is legal but the player still wants to see shanten and waits.
public sealed record HandSummary(int Shanten, int Ukeire, IReadOnlyList<Tile> Waits);

public sealed record ActionChoice(
    ActionKind Kind,
    Tile? Tile,                           // discard (or riichi discard); call tile for pon/chi/kan
    Meld? Call,                           // the meld to form for Pon/Chi/*Kan
    string Summary,                       // one line for the header
    IReadOnlyList<Reason> Steps,
    IReadOnlyList<DiscardCandidate> Candidates)   // ranked best-first; empty for non-discard actions
{
    public HandSummary? Hand { get; init; }

    public bool IsWin => this.Kind is ActionKind.Tsumo or ActionKind.Ron;

    public bool IsCall => this.Kind is ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan or ActionKind.AnKan or ActionKind.ShouMinKan;

    public bool IsDiscard => this.Kind is ActionKind.Discard or ActionKind.Riichi;

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

    // Danger with rank and reason (same probability as Danger) for the overlay and logs.
    DangerEstimate Explain(Tile tile, int seat) => new(this.Danger(tile, seat), DangerRank.F, string.Empty, string.Empty);

    // The seat to defend against first: riichi, else the highest tenpai × value; -1 when
    // nobody is a threat.
    int PrimaryThreat() => -1;

    // Estimated value in points of the seat's hand if it wins.
    double Value(int seat) => 0;

    // Suji lines still live against the seat (18 at the start), -1 when unknown.
    int LiveSuji(int seat) => -1;
}

// Which danger/push-fold implementation runs; Legacy is the v1.3 behaviour for A/B runs.
public enum DefenseModel
{
    Legacy,
    V2,
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

    // Defense v2: the danger budget for this turn. The default wraps the stance so a
    // legacy implementation still works (fold = genbutsu only, push = anything).
    PushFoldDecision Decide(StateSnapshot state, IOpponentModel opponents, IReadOnlyList<DiscardCandidate> candidates)
    {
        var primary = opponents.PrimaryThreat();
        var threats = Enumerable.Range(1, 3).Where(s => state.Seats[s].Riichi).ToList();
        if (candidates.Count == 0)
            return new PushFoldDecision(1, threats, primary, 0, new Reason("push/fold", "No candidates."));
        var stance = this.Evaluate(state, opponents, candidates[0], out var reason);
        return new PushFoldDecision(stance == PushFoldStance.Fold ? 0 : 1, threats, primary, 0, reason);
    }
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
    public int RiichiMinUkeire { get; init; } = 2;             // tanki/shanpon/kanchan waits live on 2-3 tiles
    public double DealInRiskWeight { get; init; } = 1.0;     // scales ExpectedDealInCost in the ranking
    public double FoldTenpaiThreshold { get; init; } = 0.6;  // fold when an opponent's tenpai prob ≥ this and we're far
    public int FoldMinShanten { get; init; } = 2;            // ...and our best shanten-after ≥ this
    public double SujiDiscount { get; init; } = 0.5;
    public double GenbutsuDanger { get; init; } = 0.0;
    public int MinHanDoman { get; init; } = 1;               // Doman requires a yaku; keep as data
    public double FoldMinValue { get; init; } = 2;
    // Logistic tenpai estimate for seats NOT in riichi. Fitted 2026-09-21 with
    // `Precompute learn-fit-tenpai` on 1.47 M opponent rows from Tenhou Phoenix replays
    // (archive n24, train split): log-loss 0.229 → 0.181. The literature curves the
    // previous defaults (-4.1, 0.25, 0.95, 0.3, 0.5) reproduced include riichi hands, which
    // is why they overshot closed non-riichi seats 4×. Re-fit for the FF14 population with
    // tools/fit_tenpai.py once tenpai_calibration.csv has a few thousand rows.
    public double TenpaiLogitIntercept { get; init; } = -4.861;
    public double TenpaiLogitPerDiscard { get; init; } = 0.229;
    public double TenpaiLogitPerMeld { get; init; } = 1.197;
    public double TenpaiLogitEarlyOutside { get; init; } = -0.085;
    public double TenpaiLogitLateMiddle { get; init; } = 0.137;
    public double TenpaiMaxWithoutRiichi { get; init; } = 0.9;
    public double KabeDiscount { get; init; } = 0.4;
    public double HonorDanger { get; init; } = 0.16;
    public double TerminalDanger { get; init; } = 0.12;
    public double EdgeDanger { get; init; } = 0.18;
    public double MiddleDanger { get; init; } = 0.24;
    public double OpponentBaseValue { get; init; } = 2000;
    public double OpponentMeldValue { get; init; } = 800;
    public double OpponentRiichiValue { get; init; } = 2000;
    public double OpponentDoraValue { get; init; } = 1000;

    // --- Defense v2 (docs/DEFENSE_PLAN.md) ---
    public DefenseModel DefenseModel { get; init; } = DefenseModel.V2;
    // Rank boundaries on the conditional deal-in probability (Fukuchi's diagram: A+ 0.9 %,
    // B 2.9 %, C 3.4 %, D 4.8–5.5 %, E 6.3–7.1 %, F 12.3 %).
    public double RankAPlusMax { get; init; } = 0.015;
    public double RankBMax { get; init; } = 0.032;
    public double RankCMax { get; init; } = 0.042;
    public double RankDMax { get; init; } = 0.06;
    public double RankEMax { get; init; } = 0.095;
    // A seat's tenpai estimate must reach this before it counts as a threat for the
    // primary-threat choice and the danger rank shown on candidates.
    public double ThreatTenpaiFloor { get; init; } = 0.3;
    // Opponent value in points (Fukuchi: a winning riichi averages 7 000 / 9 800 dealer with
    // three aka); dama and open-hand floors; han assumed for hands without a visible yaku.
    public double RiichiValue { get; init; } = 7000;
    public double DealerRiichiValue { get; init; } = 9800;
    public double DamaValue { get; init; } = 2600;
    public double DamaDealerValue { get; init; } = 3900;
    public double OpenFloorValue { get; init; } = 1300;
    public double OpenFloorDealerValue { get; init; } = 2000;
    public double OpenHandAssumedHan { get; init; } = 1;
    public double ClosedHandAssumedHan { get; init; } = 1;      // riichi
    public double RiichiBonusHan { get; init; } = 0.6;         // ura / ippatsu / tsumo expectation
    // Win-probability model (HandValue.WinProbability) and the number of dangerous discards a
    // push is expected to cost before the hand resolves (scales the risk term in the EV).
    public double WinRateK { get; init; } = 0.8;
    public double TenpaiRateK { get; init; } = 1.0;
    public double ExtraShantenFactor { get; init; } = 0.35;
    public double PushExposureTurns { get; init; } = 3;
    // Inside a shanten level: false = the analyzer's tile-efficiency score with the risk
    // term scaled to a tie-break (RiskTieBreakPerPoint units per point at shanten >= 1,
    // TenpaiRiskTieBreakPerPoint at tenpai); true = point EV (WinProbability × ValuePoints −
    // exposure × expected loss). Measured on Phoenix replays with `learn-eval`: the analyzer
    // score agrees with human choices more often (docs/research/EVALUATION.md).
    public bool RankByPointEv { get; init; }
    public double RiskTieBreakPerPoint { get; init; } = 1.0;        // < 10 000 / max cost: never outranks a ukeire tile
    public double TenpaiRiskTieBreakPerPoint { get; init; } = 0.002; // tenpai scores are ~5–30
    // Push/fold budget (PushFoldPolicy.Decide): phases by our turn, wait quality, the
    // budgets that "cut a 10 % / 7 % / 5 % tile" map to, and situational factors.
    public int EarlyTurnMax { get; init; } = 6;
    public int MidTurnMax { get; init; } = 11;
    public int GoodWaitTiles { get; init; } = 6;
    public double WideOneShantenChance { get; init; } = 0.20;    // ukeire × 5/6 % per turn
    public double NarrowOneShantenChance { get; init; } = 0.10;
    public double BudgetTenPercent { get; init; } = 0.12;
    public double BudgetSevenPercent { get; init; } = 0.08;
    public double BudgetFivePercent { get; init; } = 0.06;
    public double TwoRiichiFactor { get; init; } = 0.5;
    public int SafeLeadPoints { get; init; } = 8000;
    public double LeadingFactor { get; init; } = 0.6;
    public double TrailingFactor { get; init; } = 1.5;
    public double PreDrawBudget { get; init; } = 0.26;          // 1500(1-x) - 8000x > -1000
    public double PreDrawBudgetVsDealer { get; init; } = 0.185;
    // Chasing a riichi with a bad wait needs this much (Fukuchi: 5 200, mangan vs the dealer).
    public double ChaseBadWaitPoints { get; init; } = 5200;
    public double ChaseBadWaitPointsVsDealer { get; init; } = 8000;
    // Learned call decisions: decline a heuristic call when the network's pass probability
    // reaches this (LearnedCallPolicy).
    public double LearnedCallPassThreshold { get; init; } = 0.5;

    public static PolicyWeights Default { get; } = new();
}
