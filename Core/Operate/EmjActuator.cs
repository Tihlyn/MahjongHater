using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Operate;

// One operate call: whether the game received a click and what was fired. Detail is
// human-readable and lands in the auto-play journal.
public sealed record OperateResult(bool Ok, string Detail)
{
    public static OperateResult Fail(string why) => new(false, why);
}

// Executes policy decisions against the live Emj addon: resolves the addon, maps the
// decision to the right node/list row, fires it, and tells the tracker what was
// answered. Framework-thread only. Also exposes the addon-side diagnostics the auto
// player writes into a stall dump (prompt rows, slot discardability, recap buttons).
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
        var result = choice.Kind switch
        {
            ActionKind.Discard => this.Discard(choice),
            ActionKind.Riichi => this.ClickLabel("Riichi"),
            ActionKind.Tsumo => this.ClickLabel("Tsumo"),
            ActionKind.Ron => this.ClickLabel("Ron"),
            ActionKind.Pon => this.ClickLabel("Pon"),
            ActionKind.Chi when chooser => this.ClickChiShape(state, choice),
            ActionKind.Chi => this.ClickLabel("Chi"),
            ActionKind.MinKan or ActionKind.AnKan or ActionKind.ShouMinKan => this.ClickLabel("Kan"),
            ActionKind.Pass when chooser => this.ClickPath(this.reader.Layout.Nodes.ChiShapeCancel),
            ActionKind.Pass when state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare => this.ClickLabel("Pass"),
            _ => OperateResult.Fail($"nothing to execute for {choice.Kind}"),
        };

        // A list answer (call / pass / win / riichi) is final for that window; the game echoes
        // it as another type-19, which the tracker must not treat as a new prompt.
        if (result.Ok && choice.Kind != ActionKind.Discard)
            this.reader.Tracker.MarkCallAnswered(choice.IsWin);
        return result;
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
                return new OperateResult(true, $"RECOVERY: {wanted} not discardable ({first.Detail}); discarded {candidate.Tile} instead — {fallback.Detail}");
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

        var index = slots.FindIndex(sl => sl.NodePtr == (nint)target);
        if (!EmjOperator.HasAddonBoundActivation(addon, target))
            return OperateResult.Fail($"slot {index} ({tile}, node {target->NodeId}) has no addon-bound activation — not discardable right now");

        var fired = EmjOperator.ClickNode(addon, target);
        var detail = $"discard slot={index} node={target->NodeId} ({tile}): {string.Join("; ", fired)}";
        this.reader.Tracker.Note($"op {detail}");
        return new OperateResult(true, detail);
    }

    // Clicks the row/button whose visible text matches (Pon, Chi, Riichi, Pass, End match…).
    public OperateResult ClickLabel(string label)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var clicked = EmjOperator.ClickByLabel(addon, label);
        if (clicked is null)
            return OperateResult.Fail($"no visible text matching '{label}'");

        var detail = $"call \"{label}\" → \"{clicked.MatchedText}\" node {clicked.TargetNodeId}: {string.Join("; ", clicked.Fired)}";
        this.reader.Tracker.Note($"op {detail}");
        return new OperateResult(true, detail);
    }

    // Full click on a node addressed by a Cartographer-style path ("1/46/52/7").
    public OperateResult ClickPath(string? path)
    {
        var addon = this.GetAddon();
        if (addon == null)
            return OperateResult.Fail("Emj addon not open");

        var node = EmjScanner.FindNodeByPath(addon, path);
        if (node == null || !node->IsVisible())
            return OperateResult.Fail($"node {path} not found or not visible");

        var fired = EmjOperator.ClickNode(addon, node);
        var detail = $"click {path}: {string.Join("; ", fired)}";
        this.reader.Tracker.Note($"op {detail}");
        return new OperateResult(true, detail);
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
        if (next != null && next->IsVisible() && EmjOperator.HasAddonBoundActivation(addon, next))
        {
            var fired = EmjOperator.ClickNode(addon, next);
            var detail = $"recap Next (node {RecapNextNodeId}): {string.Join("; ", fired)}";
            this.reader.Tracker.Note($"op {detail}");
            return new OperateResult(true, detail);
        }

        var end = this.ClickLabel("End match");
        return end.Ok ? end : OperateResult.Fail($"neither Next nor End match is clickable ({end.Detail})");
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
        var fired = EmjOperator.ClickNode(addon, &button->OwnerNode->AtkResNode);
        var detail = $"SelectYesno \"{prompt}\" → Yes: {string.Join("; ", fired)}";
        this.reader.Tracker.Note($"op {detail}");
        return new OperateResult(true, detail);
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
        var result = this.ClickPath(buttons[index]);
        return result.Ok ? new OperateResult(true, $"chi shape {index}: {result.Detail}") : result;
    }

    // ── diagnostics for the stall dump ──

    // Visible call-panel texts plus the decision list's rows as the game holds them.
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
            lines.Add("call list: not found");
        else
            lines.Add($"call list rows (visible={listNode->IsVisible()}): [{string.Join(", ", EmjOperator.ListRows(listNode).Select(r => $"{r.Index}:{r.Label}"))}]");

        var next = EmjScanner.FindNodeById(addon, RecapNextNodeId);
        lines.Add($"recap Next visible={next != null && next->IsVisible()}");
        var chooser = this.reader.Layout.Nodes.ChiShapeButtons.Length > 0 ? EmjScanner.FindNodeByPath(addon, this.reader.Layout.Nodes.ChiShapeButtons[0]) : null;
        lines.Add($"chi chooser visible={chooser != null && chooser->IsVisible()}");
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

    private AtkUnitBase* GetAddon()
    {
        var ptr = this.gameGui.GetAddonByName(this.reader.Layout.AddonName);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->RootNode == null ? null : addon;
    }
}
