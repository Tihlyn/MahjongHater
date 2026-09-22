using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.Operate;

// Fires the same AtkEvents the game's input pipeline would deliver, so the auto player
// can operate the Emj addon (discard, call, confirm) without a human at the screen.
//
// Firing strategy: reuse the events REGISTERED on the target node (correct Listener,
// Param, Target already wired by the game) and deliver exactly one semantic activation
// per click — ButtonClick > ListItemClick > MouseClick > MouseDown+MouseUp — to avoid
// the double-fire a full down/up/click volley causes on component buttons.
//
// HOVER IS NEVER LEFT SET. Until 2026-09-22 every click was preceded by the node's
// MouseOver and nothing ever sent MouseOut, so the agent kept treating a hand slot as
// hovered after the tile had left the hand; the game then crashed refreshing that slot's
// tooltip string inside AgentEmj.Update (three crashes in 376 clicks,
// docs/research/LIVE_ISSUES_2026_09_22.md). So the default is Activation: no hover at all.
// If a click turns out not to register, the caller escalates to HoverCycle, which sends
// MouseOver and MouseOut back to back BEFORE the activation, while the node is still
// valid — nothing is ever fired at a node after the activation that invalidated it.
//
// Framework-thread only. Everything here was verified live in the 2026-07/09 sessions
// (docs/EMJ_ADDON_REFERENCE.md, "Operating the addon").
internal static unsafe class EmjOperator
{
    public enum ClickStyle
    {
        Activation,   // the activation chain alone (default: cannot leave hover state behind)
        HoverCycle,   // MouseOver + MouseOut first, both while the node is still valid
    }

    // Session-sticky: AutoPlayer raises it to HoverCycle when a click does not take effect,
    // and says so in its journal. Framework thread only, like everything else here.
    public static ClickStyle Style { get; set; } = ClickStyle.Activation;

    private delegate void NodeVisitor(AtkResNode* node, nint ownerComponent);

    // Simulates a click on a node, searching its subtree per event type (a slot's
    // MouseOver and ButtonClick live on different registered chains). Chain params are
    // authoritative — they encode the game's own wiring (a slot's ButtonClick param is
    // NOT the slot index; live-confirmed 2026-07-05). Returns every event delivered.
    public static List<string> ClickNode(AtkUnitBase* addon, AtkResNode* node)
    {
        var fired = new List<string>(3);

        if (Style == ClickStyle.HoverCycle)
        {
            var over = FindChainEventInSubtree(addon, node, AtkEventType.MouseOver, out var overHolder);
            if (over != null)
            {
                FireChain(addon, (AtkResNode*)overHolder, over, fired);
                // Released immediately, before the activation can invalidate the node.
                var out_ = FindChainEventInSubtree(addon, node, AtkEventType.MouseOut, out var outHolder);
                if (out_ != null)
                    FireChain(addon, (AtkResNode*)outHolder, out_, fired);
                else
                    FireSynthetic(addon, (AtkResNode*)overHolder, AtkEventType.MouseOut, (int)over->Param, fired);
            }
        }

        var activation = FindChainEventInSubtree(addon, node, AtkEventType.ButtonClick, out var holder);
        if (activation == null)
            activation = FindChainEventInSubtree(addon, node, AtkEventType.ListItemClick, out holder);
        if (activation == null)
            activation = FindChainEventInSubtree(addon, node, AtkEventType.MouseClick, out holder);
        if (activation != null)
        {
            FireChain(addon, (AtkResNode*)holder, activation, fired);
            return fired;
        }

        var down = FindChainEventInSubtree(addon, node, AtkEventType.MouseDown, out var downHolder);
        if (down != null)
            FireChain(addon, (AtkResNode*)downHolder, down, fired);
        var up = FindChainEventInSubtree(addon, node, AtkEventType.MouseUp, out var upHolder);
        if (up != null)
            FireChain(addon, (AtkResNode*)upHolder, up, fired);
        if (down == null && up == null)
            FireSynthetic(addon, node, AtkEventType.MouseClick, 0, fired);
        return fired;
    }

    // Selects a list row by firing the list component's addon-bound ListItemClick with
    // populated ListItemData — the addon-side handler is what commits the choice, and
    // component-level mouse simulation is ignored (live 2026-07-05: a call window's
    // Pass button ate MouseOver/Down/Up/Click and ButtonClick without effect).
    public static string? SelectListItem(AtkUnitBase* addon, AtkResNode* listNode, AtkResNode* rendererNode, int index)
    {
        var chain = FindChainEvent(listNode, AtkEventType.ListItemClick);
        if (chain == null)
            return null;

        var data = BuildMouseData(rendererNode != null ? rendererNode : listNode);
        data.ListItemData.SelectedIndex = index;
        if (rendererNode != null && (ushort)rendererNode->Type >= 1000)
            data.ListItemData.ListItemRenderer =
                (AtkComponentListItemRenderer*)((AtkComponentNode*)rendererNode)->Component;

        var listener = chain->Listener != null ? chain->Listener : (AtkEventListener*)addon;
        listener->ReceiveEvent(chain->State.EventType, (int)chain->Param, chain, &data);
        return $"ListItemClick index={index} param={chain->Param}{(listener == (AtkEventListener*)addon ? " →addon" : " →component")}";
    }

    // The nearest ancestor component whose own chain carries ListItemClick — the list
    // a renderer belongs to. Parent links cross component boundaries in ATK.
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

    // Rows of a list per its own item table (AtkComponentList.ItemRendererList): the
    // authoritative label + renderer per index. Pooled off-screen renderers report
    // IsVisible, so visual ranking is NOT usable (live 2026-07-05: it produced row 6
    // of a 2-row list); only the item table and ListItemIndex are trustworthy.
    public static List<(int Index, string Label, nint RendererNode)> ListRows(AtkResNode* listNode)
    {
        var rows = new List<(int, string, nint)>(8);
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
            var node = renderer != null ? (nint)renderer->AtkComponentButton.AtkComponentBase.OwnerNode : 0;
            // The Emj decision list never fills the item-table labels (verified live
            // 2026-09-19); the row text lives in the renderer's text child instead.
            if (label.Trim().Length == 0 && renderer != null)
                label = FirstText(&renderer->AtkComponentButton.AtkComponentBase);
            rows.Add((i, label.Trim(), node));
        }

        return rows;
    }

    // Index of a renderer within its list, from the item table (fallback: the
    // renderer's own ListItemIndex field).
    public static int ListRendererIndex(AtkResNode* listNode, AtkResNode* rendererNode)
    {
        foreach (var (index, _, node) in ListRows(listNode))
        {
            if (node == (nint)rendererNode)
                return index;
        }

        var self = (AtkComponentListItemRenderer*)((AtkComponentNode*)rendererNode)->Component;
        return self != null && self->ListItemIndex >= 0 ? self->ListItemIndex : 0;
    }

    // Finds a visible text node matching the label and clicks the component that owns it
    // (Chi/Pon/Ron/Pass call rows, recap Next / End match). Exact match first, then
    // prefix, both case-insensitive. Null when no visible text matches.
    public static LabelClick? ClickByLabel(AtkUnitBase* addon, string label)
    {
        nint exactNode = 0, exactOwner = 0, prefixNode = 0, prefixOwner = 0;
        string? exactText = null, prefixText = null;
        Walk(addon, (node, ownerComponent) =>
        {
            var txt = node->GetAsAtkTextNode();
            if (txt == null || !node->IsVisible())
                return;
            var text = EmjScanner.ReadTextNode(txt);
            if (text.Length == 0)
                return;
            if (exactNode == 0 && text.Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                exactNode = (nint)node;
                exactOwner = ownerComponent;
                exactText = text;
            }
            else if (prefixNode == 0 && text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                prefixNode = (nint)node;
                prefixOwner = ownerComponent;
                prefixText = text;
            }
        });

        var textNode = exactNode != 0 ? exactNode : prefixNode;
        if (textNode == 0)
            return null;

        var owner = exactNode != 0 ? exactOwner : prefixOwner;
        var matched = (exactNode != 0 ? exactText : prefixText)!;
        var target = owner != 0 ? (AtkResNode*)owner : (AtkResNode*)textNode;

        // A renderer inside a list commits its choice through the list's addon-bound
        // ListItemClick, not its own button chain. The list's item table maps the
        // label to its true row index (the matched text node may sit in a pooled
        // renderer, so the table outranks the renderer we found the text in).
        var owningList = FindOwningList(target);
        if (owningList != null)
        {
            var index = -1;
            nint rendererNode = 0;
            foreach (var (i, rowLabel, node) in ListRows(owningList))
            {
                if (rowLabel.Equals(matched.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    rendererNode = node;
                    break;
                }
            }

            if (index < 0)
            {
                index = ListRendererIndex(owningList, target);
                rendererNode = (nint)target;
            }

            var selected = SelectListItem(addon, owningList, (AtkResNode*)rendererNode, index);
            if (selected is not null)
                return new LabelClick(matched, target->NodeId, [$"row {index}: {selected}"]);
        }

        return new LabelClick(matched, target->NodeId, ClickNode(addon, target));
    }

    // True when the node's subtree carries an addon-bound, VISIBLE ButtonClick — a
    // hand slot is truly discardable only then. Two independent live 2026-07-05
    // failure modes this guards against: (1) during a claim freeze the slots expose
    // only component-internal collision events, so clicking does nothing; (2) the
    // addon-bound chain can exist but sit on a node that has gone temporarily
    // invisible (NodeFlags, not pooling) — the click fires with no error and the game
    // silently ignores it, since ATK gates interactivity on IsVisible().
    public static bool HasAddonBoundActivation(AtkUnitBase* addon, AtkResNode* node)
        => FindChainEventCore(node, AtkEventType.ButtonClick, (nint)(AtkEventListener*)addon, requireVisible: true, 0, out _) != null;

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

    // ────────────────────────────────────────────── HELPERS ─────────────────────────────────────────────

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
        var data = BuildMouseData(node);
        var listener = evt->Listener != null ? evt->Listener : (AtkEventListener*)addon;
        var param = (int)evt->Param;
        listener->ReceiveEvent(evt->State.EventType, param, evt, &data);
        fired.Add($"{evt->State.EventType} param={param}{(listener == (AtkEventListener*)addon ? " →addon" : " →component")}");
    }

    private static void FireSynthetic(AtkUnitBase* addon, AtkResNode* node, AtkEventType type, int param, List<string> fired)
    {
        var data = BuildMouseData(node);
        var evt = new AtkEvent
        {
            Node = node,
            Target = node != null ? &node->AtkEventTarget : null,
            Listener = (AtkEventListener*)addon,
            Param = (uint)param,
            State = new AtkEventState { EventType = type },
        };
        addon->ReceiveEvent(type, param, &evt, &data);
        fired.Add($"{type} param={param} (synthetic)");
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

// What ClickByLabel matched and what it fired.
internal sealed record LabelClick(string MatchedText, uint TargetNodeId, List<string> Fired);
