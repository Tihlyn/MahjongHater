using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Operate;

// Every Tsumo/Ron the game offers, what we decided, what auto play did about it and how the
// hand ended, as JSON lines in win_offers.jsonl. dalamud.log rotates, and an offered yakuman
// win was lost on auto play with no trace left (2026-10). Pure: the plugin feeds it one frame
// at a time and appends whatever lines it returns.
public sealed class WinOfferJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private string? openKey;
    private string? lastProgress;
    private bool offeredThisHand;

    // `decisionFresh`: the published decision was computed for this exact state (auto play
    // only acts on a fresh one). `autoStatus`/`dispatchStatus` are AutoPlayer's own lines.
    public IReadOnlyList<string> Observe(StateSnapshot state, long callGeneration, ActionChoice? choice,
        bool decisionFresh, bool autoPlay, string autoStatus, string dispatchStatus, DateTime utc)
    {
        var lines = new List<string>();
        var ron = state.Can(LegalAction.Ron);
        var tsumo = state.Can(LegalAction.Tsumo);
        if (!ron && !tsumo)
        {
            if (this.openKey is not null)
                lines.Add(Line("closed", utc, new
                {
                    key = this.openKey,
                    phase = state.Phase.ToString(),
                    awaitingOurWin = state.AwaitingOurWin,
                    last = this.lastProgress,
                }));
            this.openKey = null;
            this.lastProgress = null;
            return lines;
        }

        var tile = ron ? state.CallTile : state.DrawnTile;
        var key = $"{callGeneration}|{(ron ? "Ron" : "Tsumo")}|{tile?.ToString() ?? "-"}|{state.HandNumber}";
        if (key != this.openKey)
        {
            if (this.openKey is not null)
                lines.Add(Line("closed", utc, new { key = this.openKey, phase = state.Phase.ToString(), replacedBy = key, last = this.lastProgress }));
            this.openKey = key;
            this.lastProgress = null;
            this.offeredThisHand = true;
            lines.Add(Line("offer", utc, new
            {
                key,
                kind = ron ? "Ron" : "Tsumo",
                alsoOffered = state.Legal.ToString(),
                tile = tile?.ToString(),
                fromSeat = state.CallFromSeat,
                confirmed = state.CallWindowConfirmed,
                phase = state.Phase.ToString(),
                hand = string.Join(" ", state.Hand),
                melds = state.OurMelds.Select(m => $"{m.Type}:{string.Join("", m.Tiles)}{(m.IsOpen ? "" : "(closed)")}").ToArray(),
                meldsVerified = state.Us.MeldsVerified,
                riichi = state.OurRiichi,
                round = state.RoundWind.ToString(),
                handNumber = state.HandNumber,
                wall = state.WallRemaining,
                dora = string.Join(" ", state.DoraIndicators),
                callOptions = state.CallOptions,
                autoPlay,
            }));
        }

        var progress = $"{(decisionFresh ? choice?.Kind.ToString() ?? "none" : "stale")} | {autoStatus} | {dispatchStatus}";
        if (progress != this.lastProgress)
        {
            this.lastProgress = progress;
            lines.Add(Line("update", utc, new
            {
                key,
                decision = decisionFresh ? choice?.Kind.ToString() : null,
                decisionFresh,
                summary = choice?.Summary,
                win = choice?.Steps.Where(s => s.Stage is "win" or DecisionPolicy.WinReadDisagrees).Select(s => s.Display).ToArray(),
                autoStatus,
                dispatchStatus,
            }));
        }

        return lines;
    }

    // How the hand ended, only for hands that saw an offer.
    public IReadOnlyList<string> HandEnded(HandResult result, DateTime utc)
    {
        if (!this.offeredThisHand)
            return [];
        this.offeredThisHand = false;
        return [Line("outcome", utc, new { outcome = result.Outcome, delta = result.Delta, round = result.Round, hand = result.HandNumber })];
    }

    private static string Line(string record, DateTime utc, object data) =>
        JsonSerializer.Serialize(new { record, utc = utc.ToString("O"), data }, Json);
}
