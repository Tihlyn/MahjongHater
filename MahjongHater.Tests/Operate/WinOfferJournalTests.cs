using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Operate;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Operate;

public class WinOfferJournalTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private static StateSnapshot Ron(bool confirmed = true) => StateSnapshot.Empty with
    {
        Sequence = 1,
        Phase = GamePhase.CallPrompt,
        Hand = TestTiles.Parse("19m19p19s1234567z"),
        Legal = LegalAction.Ron | LegalAction.Pass,
        CallTile = Tile.Parse("1m"),
        CallFromSeat = 2,
        CallWindowConfirmed = confirmed,
        HandNumber = 3,
    };

    private static ActionChoice Choice(ActionKind kind) => new(kind, Tile.Parse("1m"), null, $"{kind}.",
        [new Reason("win", "Declare Ron: 13 yaku han meets the 1-han minimum.")], []);

    private static string Record(string line) => JsonDocument.Parse(line).RootElement.GetProperty("record").GetString()!;

    [Fact]
    public void An_offer_is_journaled_once_with_its_progress_then_closed()
    {
        var j = new WinOfferJournal();
        var first = j.Observe(Ron(), 7, null, false, true, "Waiting for a fresh decision", "nothing dispatched yet", T0);
        Assert.Equal(["offer", "update"], first.Select(Record));
        Assert.Contains("\"hand\":\"1m 9m 1p 9p 1s 9s 1z 2z 3z 4z 5z 6z 7z\"", first[0]);

        // Same frame state again: nothing new.
        Assert.Empty(j.Observe(Ron(), 7, null, false, true, "Waiting for a fresh decision", "nothing dispatched yet", T0));

        var sent = j.Observe(Ron(), 7, Choice(ActionKind.Ron), true, true, "Answered", "call \"Ron\" sent", T0);
        Assert.Equal(["update"], sent.Select(Record));
        Assert.Contains("13 yaku han", sent[0]);

        var closed = j.Observe(StateSnapshot.Empty with { Phase = GamePhase.RoundEnd }, 8, null, false, true, "", "", T0);
        Assert.Equal(["closed"], closed.Select(Record));
    }

    [Fact]
    public void A_new_window_closes_the_previous_offer()
    {
        var j = new WinOfferJournal();
        j.Observe(Ron(), 7, null, false, false, "Off", "-", T0);
        var next = j.Observe(Ron() with { CallTile = Tile.Parse("9m") }, 9, null, false, false, "Off", "-", T0);
        Assert.Equal(["closed", "offer", "update"], next.Select(Record));
    }

    [Fact]
    public void Outcome_is_written_only_for_hands_that_saw_an_offer()
    {
        var j = new WinOfferJournal();
        var result = new HandResult(T0, "East", 3, 12, "win-ron", 32000, false, 0, "human", "V2");
        Assert.Empty(j.HandEnded(result, T0));
        j.Observe(Ron(), 7, null, false, true, "x", "y", T0);
        Assert.Equal(["outcome"], j.HandEnded(result, T0).Select(Record));
        Assert.Empty(j.HandEnded(result, T0));
    }
}
