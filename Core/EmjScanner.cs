using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core;

// One hand-tile slot node in visual (left-to-right) order. Tile identity comes from the
// struct read (Core/State), never from the node — this is the click/highlight target only.
public readonly struct ScannedTileSlot
{
    public required uint NodeId { get; init; }

    public required float AbsX { get; init; }

    public required nint NodePtr { get; init; }
}

// Node-tree lookups the operator and overlay need to ACT on the Emj addon: hand slot
// nodes by visual index and the call-panel button texts. Framework/render thread only.
// Node ids: docs/EMJ_ADDON_REFERENCE.md "Node layout".
internal static unsafe class EmjScanner
{
    private const uint HandSlotZeroNodeId = 134;

    // Call window panel (NodeID=104) → list component (NodeID=3) → button renderers.
    private const uint CallPanelNodeId = 104;

    // Visible type-1055 hand tile slots in visual order. Nodes parked at AbsX≈0 (except
    // real slot 0, NodeId 134) are pooled overflow; same-X twins keep the lowest NodeId
    // (pooled twins 1340013+ wear a stale face and dead event chains).
    public static List<ScannedTileSlot> ScanHandSlots(AtkUnitBase* addon)
    {
        var result = new List<ScannedTileSlot>(16);
        if (addon == null)
            return result;

        var nodeList = addon->UldManager.NodeList;
        var nodeCount = addon->UldManager.NodeListCount;
        if (nodeList == null)
            return result;

        var candidates = new List<(int Idx, float AbsX, uint NodeId, nint Ptr)>(18);
        for (var ni = 0; ni < nodeCount; ni++)
        {
            var n = nodeList[ni];
            if (n == null || !n->IsVisible() || (ushort)n->Type != 1055)
                continue;

            var absX = NodeAbsX(n);
            if (absX < 0.5f && n->NodeId != HandSlotZeroNodeId)
                continue;

            candidates.Add((ni, absX, n->NodeId, (nint)n));
        }

        candidates.Sort((a, b) =>
        {
            var dx = a.AbsX.CompareTo(b.AbsX);
            if (dx != 0)
                return dx;
            var byId = a.NodeId.CompareTo(b.NodeId);
            return byId != 0 ? byId : b.Idx.CompareTo(a.Idx);
        });

        var lastAbsX = float.MinValue;
        foreach (var (_, absX, nodeId, ptr) in candidates)
        {
            if (Math.Abs(absX - lastAbsX) < 0.5f)
                continue;
            lastAbsX = absX;
            result.Add(new ScannedTileSlot { NodeId = nodeId, AbsX = absX, NodePtr = ptr });
        }

        return result;
    }

    // Visible text of every node under the call window panel (NodeID=104): the prompt
    // labels ("Chi", "Pon", "Pass", timer text…). Level-triggered — see the tracker.
    public static List<string> ScanCallButtonTexts(AtkUnitBase* addon)
    {
        var texts = new List<string>(8);
        if (addon == null)
            return texts;

        var panel = FindNodeById(addon, CallPanelNodeId);
        if (panel == null || (ushort)panel->Type < 1000)
            return texts;

        CollectComponentTexts(panel, texts, depth: 0);
        return texts;
    }

    private static void CollectComponentTexts(AtkResNode* node, List<string> texts, int depth)
    {
        if (node == null || depth > 6 || texts.Count > 32)
            return;

        if ((ushort)node->Type >= 1000)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null)
                return;

            var cList = comp->UldManager.NodeList;
            var cCount = comp->UldManager.NodeListCount;
            if (cList == null)
                return;

            for (var ci = 0; ci < cCount; ci++)
                CollectComponentTexts(cList[ci], texts, depth + 1);
            return;
        }

        var txt = node->GetAsAtkTextNode();
        if (txt != null && node->IsVisible())
        {
            var text = ReadTextNode(txt);
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text);
        }
    }

    // Resolves a Cartographer-style path ("1/46/104/3": NodeIds from the root down).
    // Segments resolve in the addon's flat NodeList until a component node is crossed,
    // then inside that component's own NodeList.
    public static AtkResNode* FindNodeByPath(AtkUnitBase* addon, string? path)
    {
        if (addon == null || string.IsNullOrWhiteSpace(path))
            return null;

        var list = addon->UldManager.NodeList;
        var count = (int)addon->UldManager.NodeListCount;
        AtkResNode* current = null;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(segment, out var id) || list == null)
                return null;

            current = null;
            for (var i = 0; i < count; i++)
            {
                var n = list[i];
                if (n != null && n->NodeId == id)
                {
                    current = n;
                    break;
                }
            }

            if (current == null)
                return null;

            if ((ushort)current->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)current)->Component;
                if (comp == null)
                    return null;
                list = comp->UldManager.NodeList;
                count = comp->UldManager.NodeListCount;
            }
        }

        return current;
    }

    // Trimmed text of the text node at a path, null when absent or not a text node.
    public static string? ReadTextAtPath(AtkUnitBase* addon, string? path)
    {
        var node = FindNodeByPath(addon, path);
        if (node == null)
            return null;
        var text = node->GetAsAtkTextNode();
        return text == null ? null : ReadTextNode(text);
    }

    public static Wind? ParseWindText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Contains("East", StringComparison.OrdinalIgnoreCase) || text.Contains('東')) return Wind.East;
        if (text.Contains("South", StringComparison.OrdinalIgnoreCase) || text.Contains('南')) return Wind.South;
        if (text.Contains("West", StringComparison.OrdinalIgnoreCase) || text.Contains('西')) return Wind.West;
        if (text.Contains("North", StringComparison.OrdinalIgnoreCase) || text.Contains('北')) return Wind.North;
        return null;
    }

    public static AtkResNode* FindNodeById(AtkUnitBase* addon, uint nodeId)
    {
        var list = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        if (list == null)
            return null;

        for (var i = 0; i < count; i++)
        {
            var n = list[i];
            if (n != null && n->NodeId == nodeId)
                return n;
        }

        return null;
    }

    public static float NodeAbsX(AtkResNode* node)
    {
        var x = 0f;
        for (var n = node; n != null; n = n->ParentNode)
            x += n->X;
        return x;
    }

    public static string ReadTextNode(AtkTextNode* textNode)
    {
        try
        {
            return System.Runtime.InteropServices.Marshal
                .PtrToStringUTF8((nint)(byte*)textNode->NodeText.StringPtr)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
