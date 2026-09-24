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
        var description = CallDescriptor.Describe(state, ct);
        var heuristic = this.inner.Evaluate(state, description, ct);
        var diagnostics = heuristic.Diagnostics.ToList();
        CallDecision Finish(CallDecision decision) => decision with { Diagnostics = diagnostics.ToArray() };
        if (!description.Valid || !IsClaimWindow(state) || !network.Supports(state) || network.FeatureVersion == LearningFeatures.LegacyVersion)
        {
            if ((state.Legal & (CallDescriptor.Claims | CallDescriptor.OwnKans)) != 0)
                diagnostics.Add(new Reason("learned-call", !description.Valid
                    ? "Not scored: call construction is invalid."
                    : (state.Legal & CallDescriptor.OwnKans) != 0
                        ? "Own-turn kan candidates use the heuristic; the learned call policy does not score them."
                        : "Not scored: position or model is outside the learned claim action space."));
            return Finish(heuristic);
        }
        ct.ThrowIfCancellationRequested();
        var tile = state.CallTile!.Value;
        var comparison = CompareActions(state, description);
        diagnostics.Add(comparison.Reason);
        if (!comparison.Matches || comparison.Legal.Count <= 1)
            return Finish(heuristic);
        var legal = comparison.Legal;
        var prediction = network.Predict(state, ct);
        var maximum = legal.Keys.Max(i => prediction[i]);
        var denominator = legal.Keys.Sum(i => Math.Exp(prediction[i] - maximum));
        var probabilities = legal.ToDictionary(kv => kv.Key, kv => Math.Exp(prediction[kv.Key] - maximum) / denominator);
        var pass = probabilities[LearningFeatures.PassAction];
        // The gate is the pass probability alone: below the threshold the best call is taken,
        // so a threshold above 0.5 calls even when pass is the single most likely action
        // (Phoenix players call on 16 % of windows; the gate is tuned on the validation split).
        var best = probabilities.Where(kv => kv.Key != LearningFeatures.PassAction).MaxBy(kv => kv.Value).Key;

        if (pass >= this.weights.LearnedCallPassThreshold)
            return Finish(CallDecision.Decline(heuristic.Accept
                ? $"Learned: pass ({pass:P0}); the heuristic would {heuristic.Kind} but Phoenix players usually let this go."
                : $"Learned: pass ({pass:P0}). {heuristic.Reason.Display}"));
        var kind = legal[best] switch { "pon" => ActionKind.Pon, "kan" => ActionKind.MinKan, _ => ActionKind.Chi };
        if (!heuristic.Accept)
        {
            // The heuristic wants a shanten gain; the network may call for tempo or defence.
            // Trust it as far as a meld of its kind and shape that keeps a yaku route.
            var viable = this.weights.LearnedCallTrust > 0
                ? this.inner.Viable(state, description, ct).FirstOrDefault(v => v.Kind == kind && (kind != ActionKind.Chi || ShapeOf(v, tile) == best))
                : null;
            return Finish(viable is not null
                ? viable with { Reason = new Reason("call", $"Learned: {legal[best]} ({probabilities[best]:P0}) for tempo; {viable.Reason.Display}") }
                : CallDecision.Decline($"Learned: would {legal[best]} ({probabilities[best]:P0}) but no yaku-preserving meld is available. {heuristic.Reason.Display}"));
        }
        var sameAction = heuristic.Kind == kind && (kind != ActionKind.Chi || ShapeOf(heuristic, tile) == best);
        var note = sameAction
            ? $" Learned agrees: {legal[best]} ({probabilities[best]:P0})."
            : $" Learned prefers {legal[best]} ({probabilities[best]:P0}); taking the heuristic's {heuristic.Kind}.";
        return Finish(heuristic with { Reason = new Reason("call", heuristic.Reason.Display + note) });
    }

    // Compare both existing learned views (legal mask and feature look-ahead) with
    // concrete validated shapes before inference. A mismatch uses validated heuristic
    // candidates only; impossible prompt flags never receive a learned probability.
    internal static CallActionComparison CompareActions(StateSnapshot state, CallDescription description)
    {
        var validated = new Dictionary<int, string> { [LearningFeatures.PassAction] = "pass" };
        var tile = state.CallTile!.Value;
        foreach (var candidate in description.Candidates)
        {
            var action = candidate.Kind switch
            {
                ActionKind.Pon => LearningFeatures.PonAction,
                ActionKind.MinKan => LearningFeatures.OpenKanAction,
                ActionKind.Chi => LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, tile, candidate.Consumed)),
                _ => -1,
            };
            if (action >= 0) validated[action] = ActionLabel(action);
        }
        var mask = new HashSet<int> { LearningFeatures.PassAction };
        if (state.Can(LegalAction.Pon)) mask.Add(LearningFeatures.PonAction);
        if (state.Can(LegalAction.MinKan)) mask.Add(LearningFeatures.OpenKanAction);
        var features = new HashSet<int> { LearningFeatures.PassAction };
        foreach (var consumed in LearningFeatures.ClaimOptions(state, tile))
        {
            var action = consumed.Length == 3 ? LearningFeatures.OpenKanAction
                : TileHelpers.SameKind(consumed[0], tile) ? LearningFeatures.PonAction
                : LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, tile, consumed));
            features.Add(action);
            if (action is not (LearningFeatures.PonAction or LearningFeatures.OpenKanAction)) mask.Add(action);
        }
        var matches = description.Valid && mask.SetEquals(validated.Keys) && features.SetEquals(validated.Keys);
        string Show(IEnumerable<int> actions) => string.Join(",", actions.Order().Select(ActionLabel));
        return new(matches, validated, new Reason("learned-call",
            $"{(matches ? "Match" : "MISMATCH")}: validated=[{Show(validated.Keys)}], learned mask=[{Show(mask)}], "
            + $"feature shapes=[{Show(features)}]. "
            + (!matches ? "Skip learned scoring; use validated heuristic candidates."
                : validated.Count <= 1 ? "No validated calls; skip learned scoring." : "Scoring only validated actions.")));
    }

    private static string ActionLabel(int action) => action switch
    {
        LearningFeatures.PassAction => "pass",
        LearningFeatures.PonAction => "pon",
        LearningFeatures.OpenKanAction => "kan",
        LearningFeatures.ChiLowAction => "chi-low",
        LearningFeatures.ChiMiddleAction => "chi-middle",
        LearningFeatures.ChiHighAction => "chi-high",
        _ => action.ToString(),
    };

    internal sealed record CallActionComparison(bool Matches, IReadOnlyDictionary<int, string> Legal, Reason Reason);

    private static int ShapeOf(CallDecision chi, Tile called) =>
        LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, called, chi.Meld!.Tiles.Where(t => !TileHelpers.SameKind(t, called)).Take(2)));

    public static bool IsClaimWindow(StateSnapshot state) =>
        state.LayoutHealthy && state.CallTile is not null && state.CallFromSeat is >= 1 and <= 3
        && (state.Legal & (LegalAction.Pon | LegalAction.Chi | LegalAction.MinKan)) != 0
        && !state.Can(LegalAction.Ron) && !state.OurRiichi
        && state.Hand.Count + 3 * state.OurMelds.Count == 13 && state.Seats.All(s => s.DiscardsVerified);
}
