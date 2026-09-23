using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Operate;

// What one operate call did. Dispatched means an event reached the game — NOT that the
// game accepted it; acceptance is a later observation (AutoPlayer watches the snapshot
// sequence). Rejected means a guard refused and nothing was sent, which is the outcome
// the 2026-09-22 rework exists to make visible instead of clicking something at random.
public enum OperateStatus
{
    Rejected,
    Dispatched,
}

public sealed record OperateResult(OperateStatus Status, string Detail)
{
    // True when an event was delivered. Kept as `Ok` because that is what every caller
    // reads; the name is about dispatch, never about acceptance.
    public bool Ok => this.Status == OperateStatus.Dispatched;

    // True when the dispatch was a node activation (a hand slot or a button) rather than a
    // list row. Only such a click can tell us anything about the click STYLE: list rows
    // never went through hover, so an unanswered list row is no reason to escalate.
    public bool NodeActivation { get; init; }

    public static OperateResult Fail(string why) => new(OperateStatus.Rejected, why);

    public static OperateResult Sent(string detail, bool nodeActivation = false)
        => new(OperateStatus.Dispatched, detail) { NodeActivation = nodeActivation };
}

// Executes policy decisions against the live Emj addon: resolves the addon, maps the
// decision to the right node/list row, fires it, and tells the tracker what was
// answered. Framework-thread only. Also exposes the addon-side diagnostics the auto
// player writes into a stall dump (prompt rows, slot discardability, recap buttons).
//
// Every call answer goes through the same three steps (2026-09-22 rework):
//   1. the tracker must have an EVENT-CONFIRMED window open — panel text alone is a
//      phantom, and the panel keeps its text after every prompt closes;
//   2. CallRowResolver must find exactly one live, enabled, renderer-backed row whose
//      label matches, on a list whose whole ancestor chain is visible, with the rows
//      still carrying the options the open window advertises;
//   3. the window generation is captured BEFORE dispatch and handed to
//      MarkCallAnswered, so a native handler that synchronously opens a newer prompt
//      (a Chi row raising its shape chooser) does not get that new prompt cleared.
public sealed unsafe class EmjActuator
{
    // Recap "Next" button: NodeId 97, addon-bound ButtonClick param 7 (reference doc,
    // "End-of-round recap"). Hidden on the final results panel, where only End match remains.
    private const uint RecapNextNodeId = 97;

    // We never answer this. It is watched only so that automation stops while a modal
    // dialog owns input, and so the prompt text lands in the log. See BlockedByDialog.
    private const string YesNoAddon = "SelectYesno";

    // The addons the game puts up once the match is genuinely over, measured on 2026-09-23:
    // EmjTotalResult always, EmjRankResult only in ranked play. The match is ended by closing
    // one of them, never by a label in Emj.
    private static readonly string[] MatchOverAddons = [EmjProtocol.ResultAddon, EmjProtocol.RankResultAddon];

    private readonly IGameGui gameGui;
    private readonly EmjStateReader reader;
    private string lastDialogPrompt = string.Empty;

    public EmjActuator(IGameGui gameGui, EmjStateReader reader)
    {
        this.gameGui = gameGui;
        this.reader = reader;
    }

    public bool IsAddonOpen => this.GetAddon() != null;

    // Executes the decision once. The caller guarantees it is fresh for `state`.
    public OperateResult Execute(StateSnapshot state, ActionChoice choice)
    {
        var chooser = state.CallShapes.Count > 0;
        return choice.Kind switch
        {
            ActionKind.Discard => this.Discard(choice),
            ActionKind.Riichi => this.AnswerCall("Riichi", isWin: false),
            ActionKind.Tsumo => this.AnswerCall("Tsumo", isWin: true),
            ActionKind.Ron => this.AnswerCall("Ron", isWin: true),
            ActionKind.Pon => this.AnswerCall("Pon", isWin: false),
            ActionKind.Chi when chooser => this.ClickChiShape(state, choice),
            ActionKind.Chi => this.AnswerCall("Chi", isWin: false),
            ActionKind.MinKan or ActionKind.AnKan or ActionKind.ShouMinKan => this.AnswerCall("Kan", isWin: false),
            ActionKind.Pass when chooser => this.CancelChiShape(),
            ActionKind.Pass when state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare => this.AnswerCall("Pass", isWin: false),
            _ => OperateResult.Fail($"nothing to execute for {choice.Kind}"),
        };
    }

    // Answers the open call window by clicking the row that carries `option`. Refuses —
    // loudly, without sending anything — when no event-confirmed window is open, when the
    // list is off screen, when its rows no longer describe that window, or when the row is
    // missing, duplicated, disabled or renderer-less.
    public OperateResult AnswerCall(string option, bool isWin)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail($"'{option}': Emj addon not open");

        var tracker = this.reader.Tracker;
        if (!tracker.CallWindowActive)
            return OperateResult.Fail($"'{option}': no call window is open");
        var path = this.reader.Layout.Nodes.CallList;
        var listNode = EmjScanner.FindNodeByPath(addon, path);
        if (listNode == null)
            return OperateResult.Fail($"'{option}': the call list ({path}) is not in the tree");

        var visible = EmjOperator.IsChainVisible(addon, listNode, out var hidden);
        var rows = EmjOperator.Rows(listNode, out var unreadable);
        if (unreadable.Length > 0)
            return OperateResult.Fail($"'{option}': the call list could not be read — {unreadable}");

        var choice = CallRowResolver.Resolve(rows, visible, option, tracker.CallOptions);
        if (!choice.Found)
            return OperateResult.Fail($"'{option}': {choice.Why}{(visible ? string.Empty : $" [{hidden}]")}");

        // Captured before the native handler runs: it may open the next prompt inside
        // ReceiveEvent, and the answer belongs to THIS window, not to whatever is open after.
        var generation = tracker.CallWindowGeneration;
        // The row index comes from the live item table (CallRowResolver); the game answers its
        // own call list with [11, row], so that index is sent directly rather than simulated.
        var dispatch = EmjOperator.FireCommand(addon, $"call \"{option}\"", EmjProtocol.SelectCallRow(choice.Index));
        if (!dispatch.Sent)
            return OperateResult.Fail($"'{option}': {dispatch.Detail}");

        tracker.Note($"op {dispatch.Detail} (window #{generation}, {choice.Why})");
        tracker.NoteAnswerSent(option, isWin, generation);
        return OperateResult.Sent($"{dispatch.Detail} — {choice.Why}, window #{generation}");
    }

    // Discards the decision's tile; when that slot is not clickable (riichi lock, a
    // greyed tenpai-breaking tile after a riichi call) falls back down the candidate
    // list so the match keeps moving — the fallback is reported loudly for the log.
    public OperateResult Discard(ActionChoice choice)
    {
        if (choice.Tile is not { } wanted)
            return OperateResult.Fail("discard decision carries no tile");

        var first = this.DiscardTile(wanted);
        if (first.Ok)
            return first;

        foreach (var candidate in choice.Candidates)
        {
            if (candidate.Tile.Equals(wanted))
                continue;
            var fallback = this.DiscardTile(candidate.Tile);
            if (fallback.Ok)
                return OperateResult.Sent($"RECOVERY: {wanted} not discardable ({first.Detail}); discarded {candidate.Tile} instead — {fallback.Detail}", nodeActivation: true);
        }

        return first;
    }

    // Clicks a hand slot holding the tile, by struct semantics (EmjStateReader.FindSlotNodeForTile).
    public OperateResult DiscardTile(Tile tile)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var slots = EmjScanner.ScanHandSlots(addon);
        var target = this.reader.FindSlotNodeForTile(addon, tile, slots);
        if (target == null)
            return OperateResult.Fail($"{tile} is not in the hand or its slot is not on screen");

        // Read before dispatch: a handler may rebuild the slot nodes.
        var index = slots.FindIndex(sl => sl.NodePtr == (nint)target);
        var nodeId = target->NodeId;
        if (!EmjOperator.HasAddonBoundActivation(addon, target))
            return OperateResult.Fail($"slot {index} ({tile}, node {nodeId}) has no addon-bound activation — not discardable right now");

        // The slot index and the callback's argument are the same number: the game's own
        // ButtonClick param is slot+15 and it emits [7, slot] (2026-09-23 capture).
        var dispatch = EmjOperator.FireCommand(addon, $"discard slot {index} ({tile})", EmjProtocol.DiscardSlot(index));
        if (!dispatch.Sent)
            return OperateResult.Fail($"slot {index} ({tile}, node {nodeId}): {dispatch.Detail}");
        var detail = $"{dispatch.Detail} [node {nodeId} was clickable]";
        this.reader.Tracker.Note($"op {detail}");
        // NOT a node activation: nothing was clicked, so an unacknowledged discard says
        // nothing about the click style and must never escalate to the hover cycle.
        return OperateResult.Sent(detail);
    }

    // Full click on a node addressed by a Cartographer-style path ("1/46/52/7").
    public OperateResult ClickPath(string? path)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var node = EmjScanner.FindNodeByPath(addon, path);
        if (node == null)
            return OperateResult.Fail($"node {path} not found");

        var dispatch = EmjOperator.ClickNode(addon, node);
        if (!dispatch.Sent)
            return OperateResult.Fail($"node {path}: {dispatch.Detail}");
        var detail = $"click {path}: {dispatch.Detail}";
        this.reader.Tracker.Note($"op {detail}");
        return OperateResult.Sent(detail, nodeActivation: true);
    }

    // Recap screen: "Next" while a round recap is up, and closing the result addon once the
    // match is genuinely over.
    public OperateResult AdvanceRecap()
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        if (this.BlockedByDialog() is { } blocked)
            return blocked;

        // Node 97 still decides WHETHER a recap is up - the game removes its addon-bound
        // activation otherwise - but the advance itself is the command the button emits, [14].
        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        if (next != null && EmjOperator.HasAddonBoundActivation(addon, next))
        {
            var dispatch = EmjOperator.FireCommand(addon, "recap Next", EmjProtocol.AdvanceRecap());
            if (dispatch.Sent)
            {
                var detail = $"{dispatch.Detail} [node {RecapNextNodeId} was clickable]";
                this.reader.Tracker.Note($"op {detail}");
                return OperateResult.Sent(detail);
            }
        }

        // Ending a finished match is not a labelled button in Emj at all. The 2026-09-23
        // capture recorded the real sequence: the last recap hides the table, EmjTotalResult
        // opens, and ButtonClick param=0 on its node 26 closes everything
        // (docs/research/ADDON_PROTOCOL_2026_09_23.md). The plugin used to search the Emj node
        // pool for the English string "End match", which is in the wrong addon entirely.
        return this.CloseResultScreen();
    }

    // ── diagnostics for the stall dump ──

    // Visible call-panel texts plus the decision list's rows as the game holds them, with
    // the two facts that decide whether a row may be clicked at all.
    public List<string> DescribePrompt()
    {
        var lines = new List<string>(8);
        var addon = this.GetAddon();
        if (addon == null)
        {
            lines.Add("Emj addon not open");
            return lines;
        }

        lines.Add($"panel texts: [{string.Join(", ", EmjScanner.ScanCallButtonTexts(addon))}]");
        var listNode = EmjScanner.FindNodeByPath(addon, this.reader.Layout.Nodes.CallList);
        if (listNode == null)
        {
            lines.Add("call list: not found");
        }
        else
        {
            var visible = EmjOperator.IsChainVisible(addon, listNode, out var hidden);
            var listRows = EmjOperator.Rows(listNode, out var unreadable);
            lines.Add($"call list chainVisible={visible}{(visible ? string.Empty : $" ({hidden})")} "
                      + (unreadable.Length > 0
                          ? $"UNREADABLE: {unreadable}"
                          : "rows: [" + string.Join(", ", listRows.Select(r =>
                              $"{r.Index}:{r.Label}{(r.HasRenderer ? string.Empty : " noRenderer")}"
                              + $"{(r.EnabledDisputed ? " enabledDisputed" : r.Enabled ? string.Empty : " disabled")}")) + "]"));
        }

        var tracker = this.reader.Tracker;
        lines.Add($"window: active={tracker.CallWindowActive} generation=#{tracker.CallWindowGeneration} options=[{string.Join(", ", tracker.CallOptions)}]");
        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        lines.Add($"recap Next visible={next != null && EmjOperator.IsChainVisible(addon, next, out _)}");
        var chooser = this.reader.Layout.Nodes.ChiShapeButtons.Length > 0 ? EmjScanner.FindNodeByPath(addon, this.reader.Layout.Nodes.ChiShapeButtons[0]) : null;
        lines.Add($"chi chooser visible={chooser != null && EmjOperator.IsChainVisible(addon, chooser, out _)}");
        lines.Add($"click style={EmjOperator.Style}");
        return lines;
    }

    // Every visible hand slot: its node and whether the game would take a click on it.
    public List<string> DescribeSlots()
    {
        var lines = new List<string>(16);
        var addon = this.GetAddon();
        if (addon == null)
            return lines;

        var hand = this.reader.Current?.Hand ?? [];
        var slots = EmjScanner.ScanHandSlots(addon);
        for (var i = 0; i < slots.Count; i++)
        {
            var node = (AtkResNode*)slots[i].NodePtr;
            var tile = i < hand.Count ? hand[i].ToString() : "?";
            lines.Add($"slot {i,2} node {slots[i].NodeId,-8} x={(int)slots[i].AbsX,4} tile={tile,-3} discardable={EmjOperator.HasAddonBoundActivation(addon, node)}");
        }

        return lines;
    }

    // Refuses to act while a Yes/No dialog is on screen, and never answers one.
    //
    // This used to click Yes, gated on nothing but a six-second timer started by our own
    // result-screen close. That guard could only ever fire on the WRONG dialog: the
    // 2026-09-23 capture shows no SelectYesno at the end of a match at all (the only one in
    // the whole match was the NPC table's start-of-match challenge prompt), so any dialog
    // arriving inside that window is by definition not the one the code was written for. A
    // duty-leave confirmation landing there would have been accepted, and that costs a
    // penalty. It read the prompt text and then ignored it.
    //
    // A dialog nobody here asked for is left alone. Its wording is logged once, which is
    // also how we learn the real prompt text if an end-of-match confirmation ever does
    // appear; when one is measured, it can be answered by identity - prompt, owner and a
    // pending transaction - rather than by elapsed time.
    private OperateResult? BlockedByDialog()
    {
        var ptr = this.gameGui.GetAddonByName(YesNoAddon);
        if (ptr.IsNull)
        {
            this.lastDialogPrompt = string.Empty;
            return null;
        }

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible || addon->RootNode == null)
        {
            this.lastDialogPrompt = string.Empty;
            return null;
        }

        var dialog = (AddonSelectYesno*)addon;
        var prompt = dialog->PromptText != null ? EmjScanner.ReadTextNode(dialog->PromptText) : string.Empty;
        var why = $"a SelectYesno is open (\"{prompt}\") — the plugin does not answer dialogs, "
                  + "so it is waiting for the player";
        if (prompt != this.lastDialogPrompt)
        {
            this.lastDialogPrompt = prompt;
            this.reader.Tracker.Note($"op paused: {why}");
        }

        return OperateResult.Fail(why);
    }

    // What the recap surface actually IS, read rather than assumed. The controls here were
    // identified by trial and error - node 97 is pressed without ever reading what it says,
    // and "End match" is an English string search in a panel whose text outlives its prompts,
    // which is the same mistake the call windows were built on. This reports the game's own
    // statements (state code, which result addon is open, whether the control is addon-bound)
    // alongside the text each control carries, so the labels can be identified from logs and
    // the string search replaced by a structural target.
    public List<string> DescribeRecap(int stateCode)
    {
        var lines = new List<string>(6);
        var addon = this.GetAddon();
        if (addon == null)
        {
            lines.Add("Emj addon not open");
            return lines;
        }

        lines.Add($"state code={stateCode}; result screen={this.MatchOverScreen() ?? "none open"}; "
                  + $"tableVisible={addon->IsVisible}");

        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        if (next == null)
        {
            lines.Add($"recap Next (node {RecapNextNodeId}): not in the tree");
        }
        else
        {
            var text = FirstVisibleText(next);
            lines.Add($"recap Next (node {RecapNextNodeId}): chainVisible={EmjOperator.IsChainVisible(addon, next, out _)} "
                      + $"addonBound={EmjOperator.HasAddonBoundActivation(addon, next)} "
                      + $"text={(text.Length == 0 ? "(none)" : $"\"{text}\"")}");
        }

        lines.Add($"panel texts: [{string.Join(", ", EmjScanner.ScanCallButtonTexts(addon))}]");
        return lines;
    }

    // The first non-empty text anywhere under a node - what a button actually says.
    private static string FirstVisibleText(AtkResNode* node)
    {
        if (node == null || (ushort)node->Type < 1000)
            return string.Empty;
        var component = ((AtkComponentNode*)node)->Component;
        if (component == null)
            return string.Empty;
        var list = component->UldManager.NodeList;
        for (var i = 0; i < component->UldManager.NodeListCount && list != null; i++)
        {
            var text = list[i] == null ? null : list[i]->GetAsAtkTextNode();
            if (text == null)
                continue;
            var read = EmjScanner.ReadTextNode(text);
            if (read.Length > 0)
                return read;
        }

        return string.Empty;
    }

    // Closes the end-of-match result screen, which is what actually ends a finished match.
    // Refuses while no result screen is up: the table being in a recap phase is not evidence
    // that the match is over, and leaving a live duty costs a penalty.
    private OperateResult CloseResultScreen()
    {
        if (this.MatchOverScreen() is not { } name)
            return OperateResult.Fail("recap Next is not clickable and no match-result screen is open");

        var ptr = this.gameGui.GetAddonByName(name);
        if (ptr.IsNull)
            return OperateResult.Fail($"{name} vanished while resolving it");
        var result = (AtkUnitBase*)ptr.Address;
        var close = EmjScanner.FindNodeById(result, EmjProtocol.ResultCloseNodeId);
        if (close == null)
            return OperateResult.Fail($"{name} has no node {EmjProtocol.ResultCloseNodeId} (the close button)");
        if (!EmjOperator.HasAddonBoundActivation(result, close))
            return OperateResult.Fail($"{name} node {EmjProtocol.ResultCloseNodeId} is not clickable right now");

        var dispatch = EmjOperator.ClickNode(result, close);
        if (!dispatch.Sent)
            return OperateResult.Fail($"{name} close: {dispatch.Detail}");
        var detail = $"{name} close (node {EmjProtocol.ResultCloseNodeId}): {dispatch.Detail}";
        this.reader.Tracker.Note($"op {detail}");
        return OperateResult.Sent(detail, nodeActivation: true);
    }

    // The match-result addon the game has on screen, or null while none is.
    private string? MatchOverScreen()
    {
        foreach (var name in MatchOverAddons)
        {
            var ptr = this.gameGui.GetAddonByName(name);
            if (ptr.IsNull)
                continue;
            var unit = (AtkUnitBase*)ptr.Address;
            if (unit->RootNode != null && unit->IsVisible)
                return name;
        }

        return null;
    }

    // State 25: pick the option whose three tiles are the policy's meld (button order =
    // AtkValues order); a meld the game did not offer is a policy bug, not a click.
    private OperateResult ClickChiShape(StateSnapshot state, ActionChoice choice)
    {
        if (choice.Call is not { } wanted)
            return OperateResult.Fail("chi decision carries no meld");
        var index = -1;
        for (var i = 0; i < state.CallShapes.Count; i++)
        {
            var offered = state.CallShapes[i].Tiles.Select(TileHelpers.ToIndex).Order();
            if (offered.SequenceEqual(wanted.Tiles.Select(TileHelpers.ToIndex).Order()))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index >= state.CallShapes.Count)
            return OperateResult.Fail($"shape {string.Join(" ", wanted.Tiles)} is not among the offered {state.CallShapes.Count} shapes");

        // The chooser emits [12, shapeIndex]; the index is the order the shapes arrived in the
        // state-25 values, which is the order CallShapes preserves (2026-09-23 capture).
        var generation = this.reader.Tracker.CallWindowGeneration;
        var addon = this.GetAddon();
        var dispatch = EmjOperator.FireCommand(addon, $"chi shape {index} ({string.Join(" ", wanted.Tiles)})",
            EmjProtocol.PickChiShape(index));
        if (!dispatch.Sent)
            return OperateResult.Fail(dispatch.Detail);
        this.reader.Tracker.Note($"op {dispatch.Detail}");
        this.reader.Tracker.NoteAnswerSent("chi shape", isWin: false, generation);
        return OperateResult.Sent($"{dispatch.Detail} (window #{generation})");
    }

    // The chooser's cancel button backs out of the shape list (state 25's own "Pass"). No
    // cancel was ever performed during the capture, so the callback it emits is unknown and
    // this stays a click on the measured button (chooser node 11, ButtonClick param 8) rather
    // than a guessed command.
    private OperateResult CancelChiShape()
        => this.AnswerChooser(this.reader.Layout.Nodes.ChiShapeCancel, "chi shape cancel");

    // The shape chooser is a button panel, not a list, but it IS a call window: the same
    // generation guard applies, so answering it never clears a window opened meanwhile.
    private OperateResult AnswerChooser(string? path, string what)
    {
        var generation = this.reader.Tracker.CallWindowGeneration;
        var result = this.ClickPath(path);
        if (!result.Ok)
            return OperateResult.Fail($"{what}: {result.Detail}");
        this.reader.Tracker.NoteAnswerSent(what, isWin: false, generation);
        return OperateResult.Sent($"{what} (window #{generation}): {result.Detail}", nodeActivation: true);
    }

    private AtkUnitBase* GetAddon()
    {
        var ptr = this.gameGui.GetAddonByName(this.reader.Layout.AddonName);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->RootNode == null ? null : addon;
    }
}
