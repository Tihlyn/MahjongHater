using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

// Every case here is a live defect from docs/research/CALL_WINDOW_AUDIT_2026_09_22.md or a
// rule that audit asked for. The resolver is the only place that decides WHICH row a call
// answer goes to, so these are the rules that keep the plugin from clicking a leftover.
public class CallRowResolverTests
{
    private static ListRow Row(int index, string label, bool renderer = true, bool enabled = true,
        bool disputed = false)
        => new(index, label, renderer, enabled, disputed);

    private static readonly IReadOnlyList<ListRow> ChiThenPass = [Row(0, "Chi"), Row(1, "Pass")];

    [Fact]
    public void Picks_the_row_whose_label_matches()
    {
        var pass = CallRowResolver.Resolve(ChiThenPass, listVisible: true, "Pass", ["Chi"]);
        Assert.True(pass.Found);
        Assert.Equal(1, pass.Index);

        var chi = CallRowResolver.Resolve(ChiThenPass, listVisible: true, "Chi", ["Chi"]);
        Assert.True(chi.Found);
        Assert.Equal(0, chi.Index);
    }

    // The 18:27:48 recovery: the stall dump had already logged "call list rows
    // (visible=False)" and the plugin dispatched row 1 anyway.
    [Fact]
    public void Refuses_a_list_that_is_not_on_screen()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Ron"), Row(1, "Pass")], listVisible: false, "Pass", ["Ron"]);
        Assert.False(choice.Found);
        Assert.Contains("not on screen", choice.Why);
    }

    // The panel and the list keep their contents after a prompt closes, so rows left over
    // from a finished Ron are not the Chi window the tracker has open.
    [Fact]
    public void Refuses_rows_that_do_not_carry_the_open_windows_options()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Ron"), Row(1, "Pass")], listVisible: true, "Pass", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("do not carry", choice.Why);
    }

    [Fact]
    public void Refuses_an_option_the_window_does_not_offer()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Chi"), Row(1, "Pass"), Row(2, "Ron")], listVisible: true, "Ron", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("not among the open window's options", choice.Why);
    }

    // AtkEventData is a union: without a renderer the payload's pointer field would hold
    // whatever else was written there. No renderer, no dispatch.
    [Fact]
    public void Refuses_a_row_without_an_item_renderer()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Chi"), Row(1, "Pass", renderer: false)], listVisible: true, "Pass", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("no item renderer", choice.Why);
    }

    [Fact]
    public void Refuses_a_disabled_row()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Chi", enabled: false), Row(1, "Pass")], listVisible: true, "Chi", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("disabled", choice.Why);
    }

    // The list's GetItemDisabledState and the row button's IsEnabled used to be combined with
    // a permissive OR: either one saying "usable" was enough. That was defended by the idea
    // that the game would ignore a click on a row it considered disabled - which stopped
    // being true when we started sending the addon's [11, row] command instead of clicking,
    // because there is then no widget in the path to ignore anything. Neither field has been
    // established as authoritative, so disagreement is refused rather than resolved by guess.
    [Fact]
    public void Refuses_a_row_the_list_and_its_button_disagree_about()
    {
        var choice = CallRowResolver.Resolve(
            [Row(0, "Chi", disputed: true), Row(1, "Pass")], listVisible: true, "Chi", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("disputed", choice.Why);
    }

    // A disputed row must not poison the rest of the list: Pass is still answerable.
    [Fact]
    public void A_disputed_row_does_not_block_a_clean_one()
    {
        var choice = CallRowResolver.Resolve(
            [Row(0, "Chi", disputed: true), Row(1, "Pass")], listVisible: true, "Pass", ["Chi"]);
        Assert.True(choice.Found);
        Assert.Equal(1, choice.Index);
    }

    // The old path fell back to the renderer's own index and finally to ROW 0, which turned
    // "the target is not there" into "click the first row".
    [Fact]
    public void Refuses_a_missing_label_instead_of_falling_back_to_row_zero()
    {
        var choice = CallRowResolver.Resolve(ChiThenPass, listVisible: true, "Pon", ["Chi", "Pon"]);
        Assert.False(choice.Found);
        Assert.Equal(-1, choice.Index);
        Assert.Contains("do not carry", choice.Why);   // the rows lost Pon: the window is stale
    }

    [Fact]
    public void Refuses_duplicate_labels_as_ambiguous()
    {
        var rows = new[] { Row(0, "Pass"), Row(1, "Chi"), Row(2, "Pass") };
        var choice = CallRowResolver.Resolve(rows, listVisible: true, "Pass", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("ambiguous", choice.Why);
    }

    [Fact]
    public void Refuses_when_no_window_is_open()
    {
        var choice = CallRowResolver.Resolve(ChiThenPass, listVisible: true, "Pass", []);
        Assert.False(choice.Found);
        Assert.Contains("no call window", choice.Why);
    }

    [Fact]
    public void Refuses_an_empty_list()
    {
        var choice = CallRowResolver.Resolve([], listVisible: true, "Pass", ["Chi"]);
        Assert.False(choice.Found);
        Assert.Contains("empty", choice.Why);
    }

    // Row text carries the announcement banner's "!" ("Ron!"); the option never does.
    [Fact]
    public void Matches_across_the_announcement_bang_and_whitespace()
    {
        var choice = CallRowResolver.Resolve([Row(0, " Ron! "), Row(1, "Pass")], listVisible: true, "Ron", ["Ron"]);
        Assert.True(choice.Found);
        Assert.Equal(0, choice.Index);
    }

    [Fact]
    public void Pass_is_answerable_without_being_an_advertised_option()
    {
        var choice = CallRowResolver.Resolve([Row(0, "Riichi"), Row(1, "Pass")], listVisible: true, "Pass", ["Riichi"]);
        Assert.True(choice.Found);
        Assert.Equal(1, choice.Index);
    }
}
