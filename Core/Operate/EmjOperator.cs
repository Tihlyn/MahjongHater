using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.Operate;

// Framework-thread native UI dispatch. Emj uses ActivateButton/SelectRow so its
// own handlers perform control, timer and focus cleanup. Events are copied before
// dispatch: handlers may mutate the event and rebuild the original node tree.
// Generic ClickNode remains for Duty Finder controls. Emj never synthesizes hover.
internal static unsafe class EmjOperator
{
    // Use the game's handler, including its UI cleanup. A bare callback only reaches
    // the agent and skips the Emj control/timer/focus transitions around it.
    public static Dispatch ActivateButton(AtkUnitBase* addon, AtkResNode* node)
    {
        if (!CanReceiveInput(addon, out var why))
            return Dispatch.Reject(why);
        if (!IsChainVisible(addon, node, out why))
            return Dispatch.Reject(why);
        var evt = FindChainEventCore(node, AtkEventType.ButtonClick,
            (nint)(AtkEventListener*)addon, true, 0, out var holder);
        if (evt == null)
            return Dispatch.Reject("no visible addon-bound ButtonClick");
        var fired = new List<string>(1);
        FireChain(addon, (AtkResNode*)holder, evt, fired);
        return Dispatch.Fired(string.Join("; ", fired));
    }

    public static bool CanReceiveInput(AtkUnitBase* addon, out string why)
    {
        why = addon == null ? "addon is closed"
            : !addon->IsVisible ? "addon is hidden"
            : addon->ShouldIgnoreInputs() ? "game is blocking addon input (animation or modal)"
            : string.Empty;
        return why.Length == 0;
    }

    // The registered discard parameter is the raw struct slot + 15. It remains
    // correct when melds leave holes in the visual/decoded hand arrays.
    public static int DiscardSlot(AtkUnitBase* addon, AtkResNode* node)
    {
        if (!IsChainVisible(addon, node, out _))
            return -1;
        var evt = FindChainEventCore(node, AtkEventType.ButtonClick,
            (nint)(AtkEventListener*)addon, true, 0, out _);
        return evt != null && evt->Param is >= 15 and <= 28 ? (int)evt->Param - 15 : -1;
    }

    // ReceiveEvent sets Handled on its argument. Never pass the registration itself:
    // the real dispatcher also uses a temporary event, not the node's chain record.
    internal static AtkEvent CopyForDispatch(AtkEvent registration)
    {
        registration.NextEvent = null;
        registration.State = new AtkEventState { EventType = registration.State.EventType };
        return registration;
    }

    public enum ClickStyle
    {
        Activation,   // the activation chain alone (default: cannot leave hover state behind)
        HoverCycle,   // a matched MouseOver+MouseOut pair first, both while the node is still valid
    }

    private const int MaxChainDepth = 32;

    // A call list offers Pon/Chi/Kan/Riichi/Ron/Tsumo plus Pass. Anything claiming more rows
    // than this is a length field we should not be trusting, not a list we should be reading.
    private const int MaxListRows = 32;

    // Session-sticky, framework thread only. AutoPlayer may raise Style to HoverCycle when
    // an ACTIVATION it really dispatched went unacknowledged and the user opted in; it is
    // reset to Activation at every match boundary so a one-off never outlives the match.
    public static ClickStyle Style { get; set; } = ClickStyle.Activation;

    // What one attempt did. Sent=false means NOTHING reached the game and Detail names the
    // guard that refused; Sent=true means an event was delivered — acceptance is a separate
    // observation the caller makes on later frames.
    public readonly record struct Dispatch(bool Sent, string Detail)
    {
        public static Dispatch Reject(string why) => new(false, why);

        public static Dispatch Fired(string what) => new(true, what);
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
    //
    // `why` is empty when the rows are trustworthy, and otherwise says what stopped us. An
    // empty list WITH a reason is not the same thing as a list that has no rows, and callers
    // must not report it as one.
    //
    // The type check is the point of this rewrite. It used to accept any node with
    // Type >= 1000 and cast its component to AtkComponentList, but that number only says
    // "some component". A different component at the same path, or a layout change, and the
    // ItemRendererList pointer, both length fields and every Label read below become
    // unrelated memory dereferenced on the framework thread. GetComponentType() is the
    // component own statement about what it is, so that is what we ask it.
    public static List<ListRow> Rows(AtkResNode* listNode, out string why)
    {
        why = string.Empty;
        var rows = new List<ListRow>(8);
        if (listNode == null)
        {
            why = "no list node";
            return rows;
        }

        if ((ushort)listNode->Type < 1000)
        {
            why = $"node {listNode->NodeId} is not a component node (type {(ushort)listNode->Type})";
            return rows;
        }

        var component = ((AtkComponentNode*)listNode)->Component;
        if (component == null)
        {
            why = $"node {listNode->NodeId} has no component";
            return rows;
        }

        var kind = component->GetComponentType();
        if (kind != ComponentType.List)
        {
            why = $"node {listNode->NodeId} is a {kind} component, not a List - refusing to read it as one";
            return rows;
        }

        var comp = (AtkComponentList*)component;
        if (comp->ItemRendererList == null)
        {
            why = $"list {listNode->NodeId} has no item-renderer table";
            return rows;
        }

        // Both lengths must agree that an index exists before it is dereferenced: ListLength
        // is what the list says it holds, AllocatedItemRendererListLength is what it actually
        // allocated, and an absurd value in either is a reason to read nothing at all.
        int declared = comp->ListLength;
        int allocated = comp->AllocatedItemRendererListLength;
        if (declared < 0 || allocated < 0)
        {
            why = $"list {listNode->NodeId} reports a negative length (declared {declared}, allocated {allocated})";
            return rows;
        }

        var count = Math.Min(declared, allocated);
        if (count > MaxListRows)
        {
            why = $"list {listNode->NodeId} reports {count} rows, beyond the {MaxListRows} a call list can have";
            return rows;
        }

        for (var i = 0; i < count; i++)
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
            // 2026-09-19); the row text lives in the renderer text child instead.
            if (label.Trim().Length == 0 && renderer != null)
                label = FirstText(&renderer->AtkComponentButton.AtkComponentBase);

            if (renderer == null)
            {
                rows.Add(new ListRow(i, label.Trim(), false, false));
                continue;
            }

            var listSaysDisabled = comp->GetItemDisabledState(i);
            var buttonSaysEnabled = renderer->AtkComponentButton.IsEnabled;
            rows.Add(new ListRow(i, label.Trim(), true,
                Enabled: !listSaysDisabled && buttonSaysEnabled,
                EnabledDisputed: listSaysDisabled == buttonSaysEnabled));
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

    // Enter the addon's list handler with a validated index and a list-shaped
    // payload. Mouse coordinates overlap a pointer in this union and cannot be used.
    public static Dispatch SelectRow(AtkUnitBase* addon, AtkResNode* listNode, int index, string what)
    {
        if (!CanReceiveInput(addon, out var blocked))
            return Dispatch.Reject($"{what}: {blocked}");
        var rows = Rows(listNode, out var unreadable);
        if (unreadable.Length > 0)
            return Dispatch.Reject($"{what}: {unreadable}");
        var row = rows.FirstOrDefault(r => r.Index == index);
        if (!row.HasRenderer || !row.Enabled || row.EnabledDisputed)
            return Dispatch.Reject($"{what}: row {index} is not enabled and renderer-backed");
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

        var chain = FindChainEvent(listNode, AtkEventType.ListItemClick);
        if (chain == null || chain->Listener != (AtkEventListener*)addon)
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
        var dispatched = CopyForDispatch(*chain);
        listener->ReceiveEvent(type, param, &dispatched, &data);
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

    // Duty Finder's measured callback route. Emj actions use native handlers above.
    // The third argument really is close: disassembly shows conditional Hide/Close
    // after the agent returns. It is not a generic "update UI state" flag.
    public static string FireCallback(AtkUnitBase* addon, bool close, params int[] values)
    {
        if (addon == null)
            return "no addon";
        var vals = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            vals[i].Type = AtkValueType.Int;
            vals[i].Int = values[i];
        }

        addon->FireCallback((uint)values.Length, vals, close);
        return $"callback [{string.Join(", ", values)}] close={close}";
    }

    public static string FireCallback(AtkUnitBase* addon, params int[] values)
        => FireCallback(addon, false, values);

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
        var dispatched = CopyForDispatch(*evt);
        listener->ReceiveEvent(type, param, &dispatched, &data);
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

        if (requireVisible && !node->IsVisible())
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

}
