using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core;

// One hand-tile slot as seen in the node tree, in visual (left-to-right) order.
public readonly struct ScannedTileSlot
{
    public required uint NodeId { get; init; }

    public required float AbsX { get; init; }

    public required nint NodePtr { get; init; }

    // Stable identity of the rendered tile face: icon id or asset+part of the face image.
    // Null when the slot has no readable face image.
    public required string? FaceKey { get; init; }
}

// Node-tree scanning engine for the Emj addon. This is the hover-free data source:
// tile faces are identified by a stable render key (icon texture id, or ULD asset +
// active part id) which TileFaceMap learns to translate into tile identity.
//
// All methods take the addon pointer and must be called from a thread that may touch
// game memory (framework thread, or the render thread for UI overlays).
internal static unsafe class EmjScanner
{
    private const uint HandSlotZeroNodeId = 134;

    // Discard pile container components, confirmed via addon inspector:
    // 116=Player, 119=West, 122=North, 125=East.
    private static readonly uint[] PileNodeIds = [116, 119, 122, 125];

    // Call window panel (NodeID=104) → list component (NodeID=3) → button renderers.
    private const uint CallPanelNodeId = 104;

    // ─────────────────────────────────────────── HAND SLOTS ─────────────────────────────────────────────

    // Collects the visible type-1055 hand tile slots in visual order.
    // Rule (confirmed by recording analysis): skip every node parked at AbsX≈0 except
    // NodeId=134 (real slot 0); sort by AbsX; de-dup same-X preferring higher z-order.
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
                continue; // phantom overflow node parked at X=0

            candidates.Add((ni, absX, n->NodeId, (nint)n));
        }

        candidates.Sort((a, b) =>
        {
            var dx = a.AbsX.CompareTo(b.AbsX);
            if (dx != 0)
                return dx;

            // Same X: the LIVE slot has the lower NodeId (134/135/1340001-12); pooled
            // twins (1340013+) park on top of it wearing a stale face and dead event
            // chains (live 2026-07-05: the drawn slot read as its previous claim ghost
            // and /discard clicked into the void).
            var byId = a.NodeId.CompareTo(b.NodeId);
            return byId != 0 ? byId : b.Idx.CompareTo(a.Idx);
        });

        var lastAbsX = float.MinValue;
        foreach (var (_, absX, _, ptr) in candidates)
        {
            if (Math.Abs(absX - lastAbsX) < 0.5f)
                continue;
            lastAbsX = absX;

            var node = (AtkResNode*)ptr;
            result.Add(new ScannedTileSlot
            {
                NodeId = node->NodeId,
                AbsX = absX,
                NodePtr = ptr,
                FaceKey = BuildSlotFaceKey(node),
            });
        }

        return result;
    }

    // Composite face key for a hand tile slot: the signature of EVERY image node inside
    // the component (part id + UV + size + texture tail). The 42×55 backdrop image reads
    // constant (partId=0, UV 0,0) across all tiles — whichever sibling image actually
    // encodes the face, its variation lands in this key. Null when no images found.
    public static string? BuildSlotFaceKey(AtkResNode* slotNode)
    {
        var fragments = new List<string>(4);
        CollectImageSignatures(slotNode, fragments, depth: 0);
        return fragments.Count == 0 ? null : "c" + string.Join("|", fragments);
    }

    private static void CollectImageSignatures(AtkResNode* node, List<string> fragments, int depth)
    {
        if (node == null || depth > 3 || fragments.Count >= 6)
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

            for (var i = 0; i < cCount; i++)
                CollectImageSignatures(cList[i], fragments, depth + 1);
            return;
        }

        var img = node->GetAsAtkImageNode();
        if (img != null)
        {
            // Skip the 23×23 state-indicator dot; keep everything tile-face sized.
            if (node->Width >= 25 && node->Height >= 25)
            {
                try
                {
                    var partsList = img->PartsList;
                    if (partsList != null && img->PartId < partsList->PartCount)
                    {
                        ref var part = ref partsList->Parts[img->PartId];
                        var asset = part.UldAsset;
                        var iconId = GetIconId(asset);
                        fragments.Add(iconId > 0
                            ? $"i{iconId}"
                            : $"p{img->PartId}u{part.U}v{part.V}w{part.Width}h{part.Height}{TexTail(asset)}");
                    }
                }
                catch
                {
                    // Unreadable image — skip the fragment.
                }
            }

            return;
        }

        if ((ushort)node->Type == 1)
        {
            for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
                CollectImageSignatures(c, fragments, depth + 1);
        }
    }

    // ─────────────────────────────────────────── DISCARD PILES ──────────────────────────────────────────

    // Snapshot of every pile slot's face key, keyed by the image node address.
    // Used for diff-based learning/resolution: on a type-5 discard, exactly one slot
    // changes key (empty face → tile face); that new key identifies the discard without
    // knowing which face means "empty".
    //
    // Pile slot faces are NOT direct children of the pile component (a scan at one level
    // found zero) — recurse through sub-components and Res containers.
    public static Dictionary<nint, string> ScanPileFaces(AtkUnitBase* addon)
    {
        var result = new Dictionary<nint, string>(120);
        if (addon == null)
            return result;

        foreach (var pileId in PileNodeIds)
        {
            var pile = FindNodeById(addon, pileId);
            if (pile != null)
                CollectFaceImages(pile, result, depth: 0);
        }

        return result;
    }

    private static void CollectFaceImages(AtkResNode* node, Dictionary<nint, string> result, int depth)
    {
        if (node == null || depth > 4 || result.Count > 160)
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

            for (var i = 0; i < cCount; i++)
                CollectFaceImages(cList[i], result, depth + 1);
            return;
        }

        var img = node->GetAsAtkImageNode();
        if (img != null)
        {
            if (node->Width >= 25 && node->Height >= 35)
            {
                var key = BuildFaceKey(img);
                if (key is not null)
                    result[(nint)node] = key;
            }

            return;
        }

        // Res containers: faces are often nested one level deeper.
        if ((ushort)node->Type == 1)
        {
            for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
                CollectFaceImages(c, result, depth + 1);
        }
    }

    // ─────────────────────────────────────────── CALL BUTTONS ───────────────────────────────────────────

    // Reads the visible text of every node under the call window panel (NodeID=104).
    // Purely observational — call state stays event-driven (see hand-tracking notes) —
    // but the texts are gold for the /analyze reversing log.
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

    // ─────────────────────────────────────────── FACE IDENTITY ──────────────────────────────────────────

    // Locates the tile-face image inside a type-1055 tile component.
    // NodeID=9 (type=1010) sub-component holds the face; NodeID=8 is a 23×23 indicator
    // dot — require ≥30×40 images to skip it.
    public static AtkImageNode* FindFaceImage(AtkResNode* componentNode)
    {
        if (componentNode == null || (ushort)componentNode->Type < 1000)
            return null;

        var comp = ((AtkComponentNode*)componentNode)->Component;
        if (comp == null)
            return null;

        var cList = comp->UldManager.NodeList;
        var cCount = comp->UldManager.NodeListCount;
        if (cList == null)
            return null;

        for (var i = 0; i < cCount; i++)
        {
            var cn = cList[i];
            if (cn == null)
                continue;

            if ((ushort)cn->Type >= 1000)
            {
                var subComp = ((AtkComponentNode*)cn)->Component;
                if (subComp == null)
                    continue;

                var subList = subComp->UldManager.NodeList;
                var subCount = subComp->UldManager.NodeListCount;
                if (subList == null)
                    continue;

                for (var j = 0; j < subCount; j++)
                {
                    var sn = subList[j];
                    if (sn == null)
                        continue;
                    var subImg = sn->GetAsAtkImageNode();
                    if (subImg != null && sn->Width >= 30 && sn->Height >= 40)
                        return subImg;
                }

                continue;
            }

            var img = cn->GetAsAtkImageNode();
            if (img != null && cn->Width >= 30 && cn->Height >= 40)
                return img;
        }

        return null;
    }

    // Builds the stable identity key of a rendered tile face.
    // Icon-textured faces key on the icon id ("i76043" — directly decodable). Everything
    // else keys on asset + part + the ACTIVE part's UV rect + texture file tail: the
    // 2026-07-04 recording showed IconId=0xFFFFFFFF and PartId=0 constant across all
    // tiles, so the varying signal must be the UV rect and/or the texture behind it.
    public static string? BuildFaceKey(AtkImageNode* img)
    {
        if (img == null)
            return null;

        try
        {
            var partsList = img->PartsList;
            if (partsList == null || img->PartId >= partsList->PartCount)
                return null;

            ref var part = ref partsList->Parts[img->PartId];
            var asset = part.UldAsset;
            if (asset == null)
                return null;

            var iconId = GetIconId(asset);
            if (iconId > 0)
                return $"i{iconId}";

            return $"a{asset->Id}p{img->PartId}u{part.U}v{part.V}w{part.Width}h{part.Height}{TexTail(asset)}";
        }
        catch
        {
            return null;
        }
    }

    // Icon id of the texture behind a uld asset, if it is an icon resource.
    // 0 = not an icon; the game uses 0xFFFFFFFF as a "no icon" sentinel — treated as 0.
    //
    // The IconId field is only written when a texture is loaded as an icon; when the
    // game rebinds a pooled tile node to an already-loaded texture the field keeps its
    // previous value (2026-07-05: the drawn-tile slot kept reporting the tile drawn two
    // turns earlier while displaying the new one). The resource's file path names the
    // texture actually rendered, so a parseable icon path overrides the field.
    public static uint GetIconId(AtkUldAsset* asset)
    {
        if (asset == null)
            return 0;

        try
        {
            var tex = &asset->AtkTexture;
            if (tex->TextureType != TextureType.Resource || tex->Resource == null)
                return 0;

            var pathId = IconIdFromTexPath(TexFilePath(tex->Resource));
            if (pathId > 0)
                return pathId;

            var iconId = tex->Resource->IconId;
            return iconId == uint.MaxValue ? 0 : iconId;
        }
        catch
        {
            return 0;
        }
    }

    // Parses the icon id out of an icon texture path ("ui/icon/076000/076050_hr1.tex" → 76050).
    // 0 when the path is not an id-named file under an icon directory.
    internal static uint IconIdFromTexPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 0;

        var normalized = path.Replace('\\', '/');
        if (!normalized.Contains("/icon/", StringComparison.OrdinalIgnoreCase))
            return 0;

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[digits]))
            digits++;

        if (digits == 0 || (digits < name.Length && name[digits] is not ('.' or '_')))
            return 0;

        return uint.TryParse(name[..digits], out var id) ? id : 0;
    }

    // Full file path of the tex resource behind a texture, null when unreadable.
    private static string? TexFilePath(AtkTextureResource* resource)
    {
        try
        {
            var handle = resource->TexFileResourceHandle;
            if (handle == null)
                return null;

            var path = handle->ResourceHandle.FileName.ToString();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    // Compact texture identity: "t" + file name (no directories) of the tex resource.
    private static string TexTail(AtkUldAsset* asset)
    {
        try
        {
            var tex = &asset->AtkTexture;
            if (tex->TextureType != TextureType.Resource || tex->Resource == null)
                return string.Empty;

            var path = TexFilePath(tex->Resource);
            if (path is null)
                return string.Empty;

            var slash = path.LastIndexOfAny(['/', '\\']);
            return "t" + (slash >= 0 ? path[(slash + 1)..] : path);
        }
        catch
        {
            return string.Empty;
        }
    }

    // One-line reversing description of EVERY image inside a slot component (the face
    // may be any of them — the backdrop reads constant, so describe all).
    public static string DescribeFace(AtkResNode* slotNode)
    {
        var parts = new List<string>(4);
        DescribeImages(slotNode, parts, depth: 0);
        return parts.Count == 0 ? "(no images)" : string.Join("  ||  ", parts);
    }

    private static void DescribeImages(AtkResNode* node, List<string> parts, int depth)
    {
        if (node == null || depth > 3 || parts.Count >= 6)
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

            for (var i = 0; i < cCount; i++)
                DescribeImages(cList[i], parts, depth + 1);
            return;
        }

        var img = node->GetAsAtkImageNode();
        if (img != null)
        {
            try
            {
                var partsList = img->PartsList;
                if (partsList == null || img->PartId >= partsList->PartCount)
                {
                    parts.Add($"{node->Width}×{node->Height} partId={img->PartId} (no parts)");
                    return;
                }

                ref var part = ref partsList->Parts[img->PartId];
                parts.Add($"{node->Width}×{node->Height} partId={img->PartId}/{partsList->PartCount} UV=({part.U},{part.V}) {DescribeTexture(part.UldAsset)}");
            }
            catch
            {
                parts.Add("(image read failed)");
            }

            return;
        }

        if ((ushort)node->Type == 1)
        {
            for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
                DescribeImages(c, parts, depth + 1);
        }
    }

    // Full texture description for reversing dumps: type, icon id and file path.
    public static string DescribeTexture(AtkUldAsset* asset)
    {
        if (asset == null)
            return "asset=null";

        try
        {
            var tex = &asset->AtkTexture;
            var type = tex->TextureType;
            if (type != TextureType.Resource || tex->Resource == null)
                return $"texType={type}";

            var resource = tex->Resource;
            var path = "?";
            try
            {
                if (resource->TexFileResourceHandle != null)
                    path = resource->TexFileResourceHandle->ResourceHandle.FileName.ToString();
            }
            catch
            {
                path = "(unreadable)";
            }

            return $"texType={type} iconId={resource->IconId} path=\"{path}\"";
        }
        catch
        {
            return "(texture read failed)";
        }
    }

    // ────────────────────────────────────────────── HELPERS ─────────────────────────────────────────────

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
