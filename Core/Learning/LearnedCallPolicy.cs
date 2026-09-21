using MahjongHater.Core.Policy;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// Learned reaction decisions (pass / pon / open kan / chi shape) on a claim window, guarded
// by the heuristic CallPolicy: a call is taken only when the heuristic finds a legal,
// yaku-preserving meld AND the network says a Phoenix player would call; a heuristic call
// the network would pass on is declined. Own-turn kan prompts stay with the heuristic.
public sealed class LearnedCallPolicy(LearnedModel network, PolicyWeights? weights = null, CallPolicy? inner = null) : ICallPolicy
{
    private readonly CallPolicy inner = inner ?? new CallPolicy();
    private readonly PolicyWeights weights = weights ?? PolicyWeights.Default;

    public CallDecision Evaluate(StateSnapshot state, IOpponentModel opponents, CancellationToken ct)
    {
        var heuristic = this.inner.Evaluate(state, opponents, ct);
        if (!IsClaimWindow(state) || !network.Supports(state) || network.FeatureVersion == LearningFeatures.LegacyVersion)
            return heuristic;
        ct.ThrowIfCancellationRequested();
        var tile = state.CallTile!.Value;
        var legal = new Dictionary<int, string> { [LearningFeatures.PassAction] = "pass" };
        if (state.Can(LegalAction.Pon)) legal[LearningFeatures.PonAction] = "pon";
        if (state.Can(LegalAction.MinKan)) legal[LearningFeatures.OpenKanAction] = "kan";
        if (state.Can(LegalAction.Chi))
            foreach (var consumed in LearningFeatures.ClaimOptions(state, tile).Where(c => c.Length == 2 && !TileHelpers.SameKind(c[0], tile)))
                legal[LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, tile, consumed))] = "chi";
        var prediction = network.Predict(state, ct);
        var maximum = legal.Keys.Max(i => prediction[i]);
        var denominator = legal.Keys.Sum(i => Math.Exp(prediction[i] - maximum));
        var probabilities = legal.ToDictionary(kv => kv.Key, kv => Math.Exp(prediction[kv.Key] - maximum) / denominator);
        var best = probabilities.MaxBy(kv => kv.Value).Key;
        var pass = probabilities[LearningFeatures.PassAction];

        if (best == LearningFeatures.PassAction || pass >= this.weights.LearnedCallPassThreshold)
            return CallDecision.Decline(heuristic.Accept
                ? $"Learned: pass ({pass:P0}); the heuristic would {heuristic.Kind} but Phoenix players usually let this go."
                : $"Learned: pass ({pass:P0}). {heuristic.Reason.Display}");
        if (!heuristic.Accept)
            return CallDecision.Decline($"Learned: would {legal[best]} ({probabilities[best]:P0}) but no yaku-preserving meld is available. {heuristic.Reason.Display}");
        var kind = legal[best] switch { "pon" => ActionKind.Pon, "kan" => ActionKind.MinKan, _ => ActionKind.Chi };
        var note = heuristic.Kind == kind
            ? $" Learned agrees: {legal[best]} ({probabilities[best]:P0})."
            : $" Learned prefers {legal[best]} ({probabilities[best]:P0}); taking the heuristic's {heuristic.Kind}.";
        return heuristic with { Reason = new Reason("call", heuristic.Reason.Display + note) };
    }

    public static bool IsClaimWindow(StateSnapshot state) =>
        state.LayoutHealthy && state.CallTile is not null && state.CallFromSeat is >= 1 and <= 3
        && (state.Legal & (LegalAction.Pon | LegalAction.Chi | LegalAction.MinKan)) != 0
        && !state.Can(LegalAction.Ron) && !state.OurRiichi
        && state.Hand.Count + 3 * state.OurMelds.Count == 13 && state.Seats.All(s => s.DiscardsVerified);
}
