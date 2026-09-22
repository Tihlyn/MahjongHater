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

    // "End match" may ask for confirmation; while the table is open that dialog is ours.
    private const string YesNoAddon = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly EmjStateReader reader;

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
        if (tracker.CallWindowFromLabels)
            return OperateResult.Fail($"'{option}': the window is label-only — no type-19/23 confirmed it");

        var path = this.reader.Layout.Nodes.CallList;
        var listNode = EmjScanner.FindNodeByPath(addon, path);
        if (listNode == null)
            return OperateResult.Fail($"'{option}': the call list ({path}) is not in the tree");

        var visible = EmjOperator.IsChainVisible(addon, listNode, out var hidden);
        var rows = EmjOperator.Rows(listNode);
        var choice = CallRowResolver.Resolve(rows, visible, option, tracker.CallOptions);
        if (!choice.Found)
            return OperateResult.Fail($"'{option}': {choice.Why}{(visible ? string.Empty : $" [{hidden}]")}");

        // Captured before the native handler runs: it may open the next prompt inside
        // ReceiveEvent, and the answer belongs to THIS window, not to whatever is open after.
        var generation = tracker.CallWindowGeneration;
        var dispatch = EmjOperator.SelectRow(addon, listNode, choice.Index, $"call \"{option}\"");
        if (!dispatch.Sent)
            return OperateResult.Fail($"'{option}': {dispatch.Detail}");

        tracker.Note($"op {dispatch.Detail} (window #{generation}, {choice.Why})");
        tracker.MarkCallAnswered(isWin, generation);
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

        var dispatch = EmjOperator.ClickNode(addon, target);
        if (!dispatch.Sent)
            return OperateResult.Fail($"slot {index} ({tile}, node {nodeId}): {dispatch.Detail}");
        var detail = $"discard slot={index} ({tile}) {dispatch.Detail}";
        this.reader.Tracker.Note($"op {detail}");
        return OperateResult.Sent(detail, nodeActivation: true);
    }

    // Clicks a BUTTON whose visible text matches ("End match", recap controls). List rows
    // are refused here on purpose — they go through AnswerCall.
    public OperateResult ClickButton(string label)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var target = EmjOperator.FindButtonByLabel(addon, label);
        if (!target.Found)
            return OperateResult.Fail($"'{label}': {target.Why}");

        var dispatch = EmjOperator.ClickNode(addon, (AtkResNode*)target.Node);
        if (!dispatch.Sent)
            return OperateResult.Fail($"'{label}' → \"{target.Matched}\": {dispatch.Detail}");
        var detail = $"button \"{label}\" → \"{target.Matched}\" ({target.Why}): {dispatch.Detail}";
        this.reader.Tracker.Note($"op {detail}");
        return OperateResult.Sent(detail, nodeActivation: true);
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

    // Recap screen: "Next" while a round recap is up, "End match" on the final results,
    // and Yes on the confirmation the latter may raise.
    public OperateResult AdvanceRecap()
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var yes = this.ConfirmYesNo();
        if (yes is not null)
            return yes;

        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        if (next != null && EmjOperator.HasAddonBoundActivation(addon, next))
        {
            var dispatch = EmjOperator.ClickNode(addon, next);
            if (dispatch.Sent)
            {
                var detail = $"recap Next (node {RecapNextNodeId}): {dispatch.Detail}";
                this.reader.Tracker.Note($"op {detail}");
                return OperateResult.Sent(detail, nodeActivation: true);
            }
        }

        var end = this.ClickButton("End match");
        return end.Ok ? end : OperateResult.Fail($"neither Next nor End match is clickable ({end.Detail})");
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
            lines.Add($"call list chainVisible={visible}{(visible ? string.Empty : $" ({hidden})")} rows: ["
                      + string.Join(", ", EmjOperator.Rows(listNode).Select(r => $"{r.Index}:{r.Label}{(r.HasRenderer ? string.Empty : " noRenderer")}{(r.Enabled ? string.Empty : " disabled")}"))
                      + "]");
        }

        var tracker = this.reader.Tracker;
        lines.Add($"window: active={tracker.CallWindowActive} generation=#{tracker.CallWindowGeneration} labelOnly={tracker.CallWindowFromLabels} options=[{string.Join(", ", tracker.CallOptions)}]");
        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        lines.Add($"recap Next visible={next != null && EmjOperator.IsChainVisible(addon, next, out _)}");
        var chooser = this.reader.Layout.Nodes.ChiShapeButtons.Length > 0 ? EmjScanner.FindNodeByPath(addon, this.reader.Layout.Nodes.ChiShapeButtons[0]) : null;
        lines.Add($"chi chooser visible={chooser != null && EmjOperator.IsChainVisible(addon, chooser, out _)}");
        lines.Add($"click style={EmjOperator.Style} list route={EmjOperator.Route}");
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

    // Clicks Yes on a visible SelectYesno (logging its prompt), null when none is up.
    private OperateResult? ConfirmYesNo()
    {
        var ptr = this.gameGui.GetAddonByName(YesNoAddon);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible || addon->RootNode == null)
            return null;

        var dialog = (AddonSelectYesno*)addon;
        var prompt = dialog->PromptText != null ? EmjScanner.ReadTextNode(dialog->PromptText) : string.Empty;
        var button = dialog->YesButton;
        if (button == null || button->OwnerNode == null || !button->IsEnabled)
            return OperateResult.Fail($"SelectYesno \"{prompt}\" has no clickable Yes");
        var dispatch = EmjOperator.ClickNode(addon, &button->OwnerNode->AtkResNode);
        if (!dispatch.Sent)
            return OperateResult.Fail($"SelectYesno \"{prompt}\": {dispatch.Detail}");
        var detail = $"SelectYesno \"{prompt}\" → Yes: {dispatch.Detail}";
        this.reader.Tracker.Note($"op {detail}");
        return OperateResult.Sent(detail, nodeActivation: true);
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

        var buttons = this.reader.Layout.Nodes.ChiShapeButtons;
        if (index < 0 || index >= buttons.Length)
            return OperateResult.Fail($"shape {string.Join(" ", wanted.Tiles)} is not among the offered {state.CallShapes.Count} shapes");
        return this.AnswerChooser(buttons[index], $"chi shape {index}");
    }

    // The chooser's cancel button backs out of the shape list (state 25's own "Pass").
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
        this.reader.Tracker.MarkCallAnswered(isWin: false, generation);
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
