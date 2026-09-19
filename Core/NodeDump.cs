using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core;

// Plain-text node tree of an addon for the /tree debug route. Framework-thread only.
internal static unsafe class NodeDump
{
    public static List<string> Tree(AtkUnitBase* addon, uint? nodeId)
    {
        var lines = new List<string>(4096);
        if (addon == null || addon->RootNode == null)
        {
            lines.Add("Addon has no root node.");
            return lines;
        }

        var visited = new HashSet<nint>();
        if (nodeId is { } id)
        {
            var node = EmjScanner.FindNodeById(addon, id);
            if (node == null)
                lines.Add($"NodeId {id} not found in the flat NodeList.");
            else
                Recurse(node, lines, 0, visited);
        }
        else
        {
            Recurse(addon->RootNode, lines, 0, visited);
        }

        return lines;
    }

    private static void Recurse(AtkResNode* node, List<string> lines, int depth, HashSet<nint> visited)
    {
        if (node == null)
            return;
        var addr = (nint)node;
        if (!visited.Add(addr))
        {
            lines.Add($"{Indent(depth)}(already-visited 0x{addr:X})");
            return;
        }

        Line(node, lines, Indent(depth));

        if ((ushort)node->Type >= 1000)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp != null)
            {
                if (comp->UldManager.RootNode != null)
                {
                    lines.Add($"{Indent(depth + 1)}[comp-tree type={(ushort)node->Type}]");
                    Recurse(comp->UldManager.RootNode, lines, depth + 2, visited);
                }

                var cList = comp->UldManager.NodeList;
                var cCount = comp->UldManager.NodeListCount;
                if (cCount > 0 && cList != null)
                {
                    lines.Add($"{Indent(depth + 1)}[comp-flat count={cCount}]");
                    for (var ci = 0; ci < cCount && ci < 2000; ci++)
                    {
                        var cn = cList[ci];
                        if (cn == null || visited.Contains((nint)cn))
                            continue;
                        Line(cn, lines, Indent(depth + 2));
                    }
                }
            }
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            Recurse(child, lines, depth + 1, visited);
    }

    private static void Line(AtkResNode* node, List<string> lines, string prefix)
    {
        var vis = node->IsVisible() ? "V" : " ";
        lines.Add(
            $"{prefix}[{vis}] NodeId={node->NodeId,5} Type={(ushort)node->Type,5}({node->Type,-15}) " +
            $"X={node->X,7:F1} Y={node->Y,7:F1} W={node->Width,5} H={node->Height,5} " +
            $"Sc={node->ScaleX:F2}/{node->ScaleY:F2} A={node->Alpha_2,3} Rot={node->Rotation:F3} " +
            $"Flags={node->NodeFlags}");

        var imgNode = node->GetAsAtkImageNode();
        if (imgNode != null)
        {
            lines.Add($"{prefix}  [IMG] partId={imgNode->PartId} wrapMode={imgNode->WrapMode}");
            if (imgNode->PartsList != null)
            {
                var pCount = imgNode->PartsList->PartCount;
                for (var pi = 0; pi < pCount && pi < 64; pi++)
                {
                    ref var part = ref imgNode->PartsList->Parts[pi];
                    var assetId = part.UldAsset != null ? part.UldAsset->Id : 0u;
                    var active = pi == imgNode->PartId ? " <<<" : string.Empty;
                    lines.Add($"{prefix}    part[{pi,3}] assetId={assetId,10} UV=({part.U,4},{part.V,4}) {part.Width,3}×{part.Height,-3}{active}");
                }
            }

            return;
        }

        var txtNode = node->GetAsAtkTextNode();
        if (txtNode != null)
        {
            string text;
            try { text = Marshal.PtrToStringUTF8((nint)(byte*)txtNode->NodeText.StringPtr) ?? "(null)"; }
            catch { text = "(err)"; }
            lines.Add($"{prefix}  [TXT] \"{text}\"  fontSize={txtNode->FontSize}");
        }
    }

    private static string Indent(int depth) => new(' ', depth * 2);
}
