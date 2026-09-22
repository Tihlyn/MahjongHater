using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.Operate;

// Fires the same AtkEvents the game's input pipeline would deliver, so the auto player
// can operate the Emj addon (discard, call, confirm) without a human at the screen.
//
// Firing strategy: reuse the events REGISTERED on the target node (correct Listener,
// Param, Target already wired by the game) and deliver exactly one semantic activation
// per click — ButtonClick > MouseClick > MouseDown+MouseUp — to avoid the double-fire a
// full down/up/click volley causes on component buttons. List rows never go down that
// path: they have their own guarded, list-shaped route (SelectRow).
//
// THREE RULES, all from confirmed 2026-09-22 defects
// (docs/research/CALL_WINDOW_AUDIT_2026_09_22.md, ADDON_INTERACTION_PLAN_2026_09_22.md):
//
//  1. NOTHING IS GUESSED. Every entry point resolves a target or returns a Dispatch with
//     Sent=false and the guard that refused. There is no synthetic click, no row 0
//     fallback and no dispatch at a node whose ancestors are hidden — the old code did
//     all three, and answered "Pass" to a finished window's leftover rows.
//  2. SCALARS ARE COPIED BEFORE DISPATCH. A handler may rebuild the UI inside
//     ReceiveEvent, so event type, param, listener and node id are read first and the
//     event/node pointers are never touched again afterwards.
//  3. HOVER IS NEVER LEFT SET. Until 2026-09-22 every click was preceded by the node's
//     MouseOver and nothing ever sent MouseOut, so the agent kept treating a hand slot as
//     hovered after the tile had left the hand; the game then crashed refreshing that
//     slot's tooltip string inside AgentEmj.Update (three crashes in 376 clicks,
//     docs/research/LIVE_ISSUES_2026_09_22.md). The default is Activation: no hover at
//     all. HoverCycle is opt-in and now requires MouseOver and MouseOut to be REGISTERED
//     ON THE SAME HOLDER with the same listener — a pair, not two unrelated events — and
//     both are sent before the activation, while the node is still valid. A synthetic
//     MouseOut aimed at the addon is never invented.
//
// Framework-thread only. The event wiring itself was verified live in the 2026-07/09
// sessions (docs/EMJ_ADDON_REFERENCE.md, "Operating the addon").
internal static unsafe class EmjOperator
{
    public enum ClickStyle
    {
        Activation,   // the activation chain alone (default: cannot leave hover state behind)
        HoverCycle,   // a matched MouseOver+MouseOut pair first, both while the node is still valid
    }

    public enum ListRoute
    {
        RegisteredEvent,  // the list's registered ListItemClick with a list-shaped payload (verified live)
        NativeSelect,     // AtkComponentList.SelectItem(index, dispatchEvent: true) — the comparison route
    }

    private const int MaxChainDepth = 32;

    // Session-sticky, framework thread only. AutoPlayer may raise Style to HoverCycle when
    // an ACTIVATION it really dispatched went unacknowledged and the user opted in; it is
    // reset to Activation at every match boundary so a one-off never outlives the match.
    public static ClickStyle Style { get; set; } = ClickStyle.Activation;

    // Which mechanism commits a list row. The registered event is what the 2026-07/09
    // sessions verified; NativeSelect is the FFXIVClientStructs binding other plugins use
    // and is offered for comparison, not adopted (ADDON_INTERACTION_PLAN §3).
    public static ListRoute Route { get; set; } = ListRoute.RegisteredEvent;

    private delegate void NodeVisitor(AtkResNode* node, nint ownerComponent);

    // What one attempt did. Sent=false means NOTHING reached the game and Detail names the
    // guard that refused; Sent=true means an event was delivered — acceptance is a separate
    // observation the caller makes on later frames.
    public readonly record struct Dispatch(bool Sent, string Detail)
    {
        public static Dispatch Reject(string why) => new(false, why);

        public static Dispatch Fired(string what) => new(true, what);
    }

    // A text match that may be clicked: the component that owns the text, already checked
    // for a fully visible ancestor chain. Node == 0 means no usable target, and Why says so.
    public readonly record struct LabelTarget(nint Node, string Matched, string Why)
    {
        public bool Found => this.Node != 0;
    }

    // ────────────────────────────────────────── TARGETS ─────────────────────────────────────────

    // True when the node AND every ancestor up to the addon are visible. ATK gates
    // interactivity on the whole chain, so a visible label inside a hidden panel is not a
    // control: the old code checked the matched text node alone and clicked leftovers.
    public static bool IsChainVisible(AtkUnitBase* addon, AtkResNode* node, out string why)
    {
        why = string.Empty;
        if (addon == null || node == null)
        {
            why = "no addon or node";
            return false;
        }

        if (!addon->IsVisible)
        {
            why = "addon hidden";
            return false;
        }

        var depth = 0;
        for (var n = node; n != null; n = n->ParentNode)
        {
            if (++depth > MaxChainDepth)
            {
                why = "ancestor chain too deep";
                return false;
            }

            if (!n->IsVisible())
            {
                why = $"node {n->NodeId} hidden";
                return false;
            }
        }

        return true;
    }

    // Rows of a list per its own item table (AtkComponentList.ItemRendererList): the
    // authoritative label, renderer presence and enabled state per index. Pooled off-screen
    // renderers report IsVisible, so visual ranking is NOT usable (live 2026-07-05: it
    // produced row 6 of a 2-row list); only the item table is trustworthy.
    public static List<ListRow> Rows(AtkResNode* listNode)
    {
        var rows = new List<ListRow>(8);
        if (listNode == null || (ushort)listNode->Type < 1000)
            return rows;
        var comp = (AtkComponentList*)((AtkComponentNode*)listNode)->Component;
        if (comp == null || comp->ItemRendererList == null)
            return rows;

        var count = Math.Min(comp->ListLength, comp->AllocatedItemRendererListLength);
        for (var i = 0; i < count && i < 32; i++)
        {
            string label;
            try
            {
                label = comp->ItemRendererList[i].Label.ToString() ?? string.Empty;
            }
            catch
            {
                label = string.Empty;
            }

            var renderer = comp->ItemRendererList[i].AtkComponentListItemRenderer;
            // The Emj decision list never fills the item-table labels (verified live
            // 2026-09-19); the row text lives in the renderer's text child instead.
            if (label.Trim().Length == 0 && renderer != null)
                label = FirstText(&renderer->AtkComponentButton.AtkComponentBase);
            rows.Add(new ListRow(i, label.Trim(), renderer != null, renderer != null && IsRowEnabled(comp, renderer, i)));
        }

        return rows;
    }

    // The nearest ancestor component whose own chain carries ListItemClick — the list a
    // renderer belongs to. Parent links cross component boundaries in ATK.
    public static AtkResNode* FindOwningList(AtkResNode* node)
    {
        var guard = 0;
        for (var p = node != null ? node->ParentNode : null; p != null && guard++ < 24; p = p->ParentNode)
        {
            if ((ushort)p->Type >= 1000 && FindChainEvent(p, AtkEventType.ListItemClick) != null)
                return p;
        }

        return null;
    }

    // A visible, unambiguous BUTTON carrying this text (recap Next, "End match", the chi
    // chooser). Text inside a list is refused on purpose: list rows commit through the
    // list's own route, where the item table — not pooled panel text — names the row.
    public static LabelTarget FindButtonByLabel(AtkUnitBase* addon, string label)
    {
        // Collected, not first-wins: the pool holds copies of panel text, so "how many
        // controls carry this label right now" is part of deciding whether any may be
        // clicked at all. Two different owners is an ambiguity, and ambiguity is a rejection.
        var exact = new Dictionary<nint, string>();
        var prefix = new Dictionary<nint, string>();
        Walk(addon, (node, ownerComponent) =>
        {
            var txt = node->GetAsAtkTextNode();
            if (txt == null || !node->IsVisible())
                return;
            var text = EmjScanner.ReadTextNode(txt);
            if (text.Length == 0)
                return;
            var owner = ownerComponent != 0 ? ownerComponent : (nint)node;
            if (text.Equals(label, StringComparison.OrdinalIgnoreCase))
                exact.TryAdd(owner, text);
            else if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                prefix.TryAdd(owner, text);
        });

        var isExact = exact.Count > 0;
        var found = isExact ? exact : prefix;
        if (found.Count == 0)
            return new LabelTarget(0, string.Empty, $"no visible text matching '{label}'");

        // Only a target whose whole ancestor chain is on screen is a control.
        var live = found.Where(pair => IsChainVisible(addon, (AtkResNode*)pair.Key, out _)).ToList();
        if (live.Count == 0)
        {
            IsChainVisible(addon, (AtkResNode*)found.First().Key, out var hidden);
            return new LabelTarget(0, found.First().Value, $"'{label}' is not on screen ({hidden})");
        }

        // A caption and its button can both carry the text. Narrowing to the ones the game
        // would actually react to usually leaves exactly one; if it does not, refuse rather
        // than pick — this is the label path, and guessing here is what it exists to stop.
        if (live.Count > 1)
        {
            var clickable = live.Where(pair => HasAddonBoundActivation(addon, (AtkResNode*)pair.Key)).ToList();
            if (clickable.Count != 1)
                return new LabelTarget(0, label, $"'{label}' matches {live.Count} visible controls, {clickable.Count} of them clickable — ambiguous");
            live = clickable;
        }

        var target = (AtkResNode*)live[0].Key;
        var matched = live[0].Value;
        if (FindOwningList(target) != null)
            return new LabelTarget(0, matched, $"'{matched}' is a list row, not a button — answer it through the call list");
        return new LabelTarget((nint)target, matched, isExact ? "exact match" : "prefix match");
    }

    // ──────────────────────────────────────── DISPATCH ──────────────────────────────────────────

    // Simulates a click on a node, searching its subtree per event type (a slot's
    // MouseOver and ButtonClick live on different registered chains). Chain params are
    // authoritative — they encode the game's own wiring (a slot's ButtonClick param is
    // NOT the slot index; live-confirmed 2026-07-05).
    public static Dispatch ClickNode(AtkUnitBase* addon, AtkResNode* node)
    {
        if (addon == null || node == null)
            return Dispatch.Reject("no addon or node");
        var nodeId = node->NodeId;
        if (!IsChainVisible(addon, node, out var hidden))
            return Dispatch.Reject($"node {nodeId} is not on screen ({hidden})");

        var fired = new List<string>(3);
        if (Style == ClickStyle.HoverCycle)
            HoverAndRelease(addon, node, fired);

        var activation = FindChainEventInSubtree(addon, node, AtkEventType.ButtonClick, out var holder);
        if (activation != null)
        {
            FireChain(addon, (AtkResNode*)holder, activation, fired);
            return Dispatch.Fired($"node {nodeId}: {string.Join("; ", fired)}");
        }

        // A node whose activation is ListItemClick is a list row — the same precedence the
        // operator has always used (ButtonClick, then ListItemClick, then MouseClick). Route
        // it through the list's own path so it gets a list-shaped payload and an index taken
        // from the item table; the old generic branch sent MOUSE COORDINATES in the renderer
        // pointer field, because AtkEventData is a union.
        if (FindChainEventInSubtree(addon, node, AtkEventType.ListItemClick, out _) != null)
        {
            var list = FindOwningList(node);
            if (list == null)
                return Dispatch.Reject($"node {nodeId} takes ListItemClick but belongs to no list");
            var index = RowIndexOf(list, node);
            return index < 0
                ? Dispatch.Reject($"node {nodeId} is not in its list's live item table")
                : SelectRow(addon, list, index, $"node {nodeId}");
        }

        activation = FindChainEventInSubtree(addon, node, AtkEventType.MouseClick, out holder);
        if (activation != null)
        {
            FireChain(addon, (AtkResNode*)holder, activation, fired);
            return Dispatch.Fired($"node {nodeId}: {string.Join("; ", fired)}");
        }

        // Down+up only as one registered pair on one holder; half a click is not a click.
        var down = FindChainEventInSubtree(addon, node, AtkEventType.MouseDown, out var downHolder);
        var up = FindChainEventInSubtree(addon, node, AtkEventType.MouseUp, out var upHolder);
        if (down == null || up == null || downHolder != upHolder)
            return Dispatch.Reject($"node {nodeId} has no addon-bound activation (ButtonClick/MouseClick/MouseDown+Up) — not clickable right now");
        FireChain(addon, (AtkResNode*)downHolder, down, fired);
        FireChain(addon, (AtkResNode*)upHolder, up, fired);
        return Dispatch.Fired($"node {nodeId}: {string.Join("; ", fired)}");
    }

    // Commits a list row by index. Every precondition is re-checked against the LIVE item
    // table immediately before dispatch: the list is a component, on screen with all its
    // ancestors, the index is inside the table, and the row owns a renderer. The payload is
    // zeroed and list-shaped, because AtkEventData is a union whose MouseData.PosX/PosY
    // overlap ListItemData.ListItemRenderer at offset 0 — a mouse payload leaves screen
    // coordinates in a POINTER field.
    public static Dispatch SelectRow(AtkUnitBase* addon, AtkResNode* listNode, int index, string what)
    {
        if (addon == null || listNode == null)
            return Dispatch.Reject($"{what}: no call list");
        var listId = listNode->NodeId;
        if ((ushort)listNode->Type < 1000)
            return Dispatch.Reject($"{what}: node {listId} is not a component list");
        if (!IsChainVisible(addon, listNode, out var hidden))
            return Dispatch.Reject($"{what}: list {listId} is not on screen ({hidden})");

        var comp = (AtkComponentList*)((AtkComponentNode*)listNode)->Component;
        if (comp == null || comp->ItemRendererList == null)
            return Dispatch.Reject($"{what}: list {listId} has no item table");
        var count = Math.Min(comp->ListLength, comp->AllocatedItemRendererListLength);
        if (index < 0 || index >= count)
            return Dispatch.Reject($"{what}: row {index} is outside the live item table (0..{count - 1})");
        var renderer = comp->ItemRendererList[index].AtkComponentListItemRenderer;
        if (renderer == null)
            return Dispatch.Reject($"{what}: row {index} has no item renderer");
        if (renderer->AtkComponentButton.AtkComponentBase.OwnerNode == null)
            return Dispatch.Reject($"{what}: row {index}'s renderer has no owner node");

        if (Route == ListRoute.NativeSelect)
        {
            comp->SelectItem(index, true);
            return Dispatch.Fired($"{what}: native SelectItem({index}, dispatch) on list {listId}");
        }

        var chain = FindChainEvent(listNode, AtkEventType.ListItemClick);
        if (chain == null)
            return Dispatch.Reject($"{what}: list {listId} has no registered ListItemClick");

        // Copy everything the log needs BEFORE the handler can rebuild the tree.
        var type = chain->State.EventType;
        var param = (int)chain->Param;
        var listener = chain->Listener != null ? chain->Listener : (AtkEventListener*)addon;
        var toAddon = listener == (AtkEventListener*)addon;
        var detail = $"{what}: ListItemClick row {index} param={param}{(toAddon ? " →addon" : " →component")} on list {listId}";

        var data = default(AtkEventData);
        data.ListItemData.ListItemRenderer = renderer;
        data.ListItemData.SelectedIndex = index;
        listener->ReceiveEvent(type, param, chain, &data);
        return Dispatch.Fired(detail);
    }

    // True when the node's subtree carries an addon-bound, VISIBLE ButtonClick and the whole
    // ancestor chain is on screen — a hand slot is truly discardable only then. Two
    // independent live 2026-07-05 failure modes this guards against: (1) during a claim
    // freeze the slots expose only component-internal collision events, so clicking does
    // nothing; (2) the addon-bound chain can exist but sit on a node that has gone
    // temporarily invisible (NodeFlags, not pooling) — the click fires with no error and the
    // game silently ignores it, since ATK gates interactivity on IsVisible().
    public static bool HasAddonBoundActivation(AtkUnitBase* addon, AtkResNode* node)
        => IsChainVisible(addon, node, out _)
           && FindChainEventCore(node, AtkEventType.ButtonClick, (nint)(AtkEventListener*)addon, requireVisible: true, 0, out _) != null;

    // Raw addon callback with int values — for handlers wired at the callback layer
    // (the Duty Finder's Commence button, for one).
    public static string FireCallback(AtkUnitBase* addon, params int[] values)
    {
        var vals = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            vals[i].Type = AtkValueType.Int;
            vals[i].Int = values[i];
        }

        addon->FireCallback((uint)values.Length, vals, false);
        return $"callback [{string.Join(", ", values)}]";
    }

    // ────────────────────────────────────────── HELPERS ─────────────────────────────────────────

    // MouseOver + MouseOut as one registered pair on one holder, both before the activation.
    // Unpaired hover is what left AgentEmj describing a slot the tile had already left.
    private static void HoverAndRelease(AtkUnitBase* addon, AtkResNode* node, List<string> fired)
    {
        var over = FindChainEventInSubtree(addon, node, AtkEventType.MouseOver, out var overHolder);
        var release = FindChainEventInSubtree(addon, node, AtkEventType.MouseOut, out var outHolder);
        if (over == null || release == null || overHolder != outHolder || over->Listener != release->Listener)
        {
            fired.Add("hover skipped (no MouseOver/MouseOut pair on one holder)");
            return;
        }

        FireChain(addon, (AtkResNode*)overHolder, over, fired);
        FireChain(addon, (AtkResNode*)outHolder, release, fired);
    }

    // Index of a node's own renderer inside a list, from the live item table only. Returns
    // -1 when the table does not contain it: the old code fell through to the renderer's
    // ListItemIndex and finally to row 0, which is how a missing target became a click.
    private static int RowIndexOf(AtkResNode* listNode, AtkResNode* node)
    {
        var comp = (AtkComponentList*)((AtkComponentNode*)listNode)->Component;
        if (comp == null || comp->ItemRendererList == null)
            return -1;
        var count = Math.Min(comp->ListLength, comp->AllocatedItemRendererListLength);
        for (var i = 0; i < count && i < 32; i++)
        {
            var renderer = comp->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null)
                continue;
            var owner = renderer->AtkComponentButton.AtkComponentBase.OwnerNode;
            if (owner == null)
                continue;
            var depth = 0;
            for (var n = node; n != null && depth++ < MaxChainDepth; n = n->ParentNode)
            {
                if (n == (AtkResNode*)owner)
                    return i;
            }
        }

        return -1;
    }

    // Deliberately lenient: a row counts as disabled only when the list's own disabled flag
    // and the renderer's button agree. Neither reading has been checked against a live Emj
    // call list, and a wrong "disabled" would refuse every call — while a wrong "enabled"
    // only produces a dispatch the game ignores, which the caller now notices and reports,
    // because dispatch and acceptance are separate outcomes.
    private static bool IsRowEnabled(AtkComponentList* list, AtkComponentListItemRenderer* renderer, int index)
        => !list->GetItemDisabledState(index) || renderer->AtkComponentButton.IsEnabled;

    private static string FirstText(AtkComponentBase* comp)
    {
        var list = comp->UldManager.NodeList;
        var count = comp->UldManager.NodeListCount;
        if (list == null)
            return string.Empty;
        for (var i = 0; i < count; i++)
        {
            var n = list[i];
            var txt = n == null ? null : n->GetAsAtkTextNode();
            if (txt == null)
                continue;
            var text = EmjScanner.ReadTextNode(txt);
            if (text.Length > 0)
                return text;
        }

        return string.Empty;
    }

    private static void FireChain(AtkUnitBase* addon, AtkResNode* node, AtkEvent* evt, List<string> fired)
    {
        // Every scalar the log wants is read here, before ReceiveEvent: the handler is
        // allowed to rebuild the tree, after which evt and node may be recycled.
        var type = evt->State.EventType;
        var param = (int)evt->Param;
        var listener = evt->Listener != null ? evt->Listener : (AtkEventListener*)addon;
        var toAddon = listener == (AtkEventListener*)addon;
        fired.Add($"{type} param={param}{(toAddon ? " →addon" : " →component")}");
        var data = BuildMouseData(node);
        listener->ReceiveEvent(type, param, evt, &data);
    }

    private static AtkEventData BuildMouseData(AtkResNode* node)
    {
        var data = default(AtkEventData);
        if (node != null)
        {
            data.MouseData.PosX = (short)(node->ScreenX + (node->Width / 2f));
            data.MouseData.PosY = (short)(node->ScreenY + (node->Height / 2f));
        }

        return data;
    }

    private static AtkEvent* FindChainEvent(AtkResNode* node, AtkEventType type)
    {
        var guard = 0;
        for (var e = node->AtkEventManager.Event; e != null && guard++ < 16; e = e->NextEvent)
        {
            if (e->State.EventType == type)
                return e;
        }

        return null;
    }

    // First registered chain event of the requested TYPE in the node's subtree.
    // Ranked in four tiers — addon-bound AND visible outranks all, since that is the
    // only node the game will actually react to (live 2026-07-05: a slot's addon-bound
    // ButtonClick chain lived on an inner node that had gone temporarily INVISIBLE
    // while the outer slot stayed visible; the click "fired" with no error and the game
    // silently ignored it). Then addon-bound any-visibility, then any-listener visible,
    // then any-listener any-visibility as a last resort.
    private static AtkEvent* FindChainEventInSubtree(AtkUnitBase* addon, AtkResNode* node, AtkEventType type, out nint holder)
    {
        var listener = (nint)(AtkEventListener*)addon;
        var found = FindChainEventCore(node, type, listener, requireVisible: true, 0, out holder);
        if (found != null)
            return found;
        found = FindChainEventCore(node, type, listener, requireVisible: false, 0, out holder);
        if (found != null)
            return found;
        found = FindChainEventCore(node, type, 0, requireVisible: true, 0, out holder);
        if (found != null)
            return found;
        return FindChainEventCore(node, type, 0, requireVisible: false, 0, out holder);
    }

    private static AtkEvent* FindChainEventCore(AtkResNode* node, AtkEventType type, nint listenerFilter, bool requireVisible, int depth, out nint holder)
    {
        holder = 0;
        if (node == null || depth > 6)
            return null;

        if (!requireVisible || node->IsVisible())
        {
            var guard = 0;
            for (var e = node->AtkEventManager.Event; e != null && guard++ < 16; e = e->NextEvent)
            {
                if (e->State.EventType == type && (listenerFilter == 0 || (nint)e->Listener == listenerFilter))
                {
                    holder = (nint)node;
                    return e;
                }
            }
        }

        if ((ushort)node->Type >= 1000)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null)
                return null;
            var list = comp->UldManager.NodeList;
            var count = comp->UldManager.NodeListCount;
            for (var i = 0; i < count && list != null; i++)
            {
                var r = FindChainEventCore(list[i], type, listenerFilter, requireVisible, depth + 1, out holder);
                if (r != null)
                    return r;
            }

            return null;
        }

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
        {
            var r = FindChainEventCore(c, type, listenerFilter, requireVisible, depth + 1, out holder);
            if (r != null)
                return r;
        }

        return null;
    }

    // Flat walk over the addon's node pool plus every nested component pool.
    private static void Walk(AtkUnitBase* addon, NodeVisitor visit)
        => WalkManager(&addon->UldManager, 0, 0, visit);

    private static void WalkManager(AtkUldManager* mgr, nint ownerComponent, int depth, NodeVisitor visit)
    {
        if (mgr == null || depth > 4)
            return;
        var list = mgr->NodeList;
        var count = mgr->NodeListCount;
        if (list == null)
            return;

        for (var i = 0; i < count; i++)
        {
            var n = list[i];
            if (n == null)
                continue;

            visit(n, ownerComponent);

            if ((ushort)n->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)n)->Component;
                if (comp != null)
                    WalkManager(&comp->UldManager, (nint)n, depth + 1, visit);
            }
        }
    }
}
