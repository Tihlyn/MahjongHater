using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Snapshot builders and operate wrappers for the /mhater debug HTTP endpoint. All of
// these touch game memory and MUST run on the framework thread (the plugin marshals).
// Returned shapes are plain (strings/numbers/lists/dictionaries) for the JSON writer.
public sealed unsafe partial class EmjStateReader
{
    // ── reads ──

    public Dictionary<string, object?> BuildDebugStatus()
    {
        var s = this.Current;
        return new Dictionary<string, object?>
        {
            ["emjOpen"] = !this.gameGui.GetAddonByName(this.Layout.AddonName).IsNull,
            ["layout"] = this.Layout.Name,
            ["layoutHealthy"] = s?.LayoutHealthy,
            ["sequence"] = s?.Sequence,
            ["phase"] = s?.Phase.ToString(),
            ["stateCode"] = s?.RawStateCode,
            ["hand"] = s?.Hand.Select(t => t.ToString()).ToList(),
            ["drawn"] = s?.DrawnTile?.ToString(),
            ["melds"] = s?.OurMelds.Select(m => $"{m.Type}[{string.Join(" ", m.Tiles)}]").ToList(),
            ["legal"] = s?.Legal.ToString(),
            ["callTile"] = s?.CallTile?.ToString(),
            ["callOptions"] = s?.CallOptions,
            ["wall"] = s?.WallRemaining,
            ["winds"] = s is null ? null : $"round={s.RoundWind} seat={s.SeatWind}",
            ["scores"] = s?.Seats.Select(x => x.Score).ToList(),
            ["discards"] = s?.Seats.Select(x => x.Discards.Count).ToList(),
            ["riichi"] = s?.OurRiichi,
            ["wins/losses"] = $"{this.tracker.WinsThisSession}/{this.tracker.LossesThisSession}",
            ["analysis"] = this.AnalysisSummaryProvider?.Invoke(),
        };
    }

    // Decoded struct frame — the in-game verification view for the layout offsets.
    public Dictionary<string, object?> BuildDebugStruct(bool includeHex)
    {
        var result = new Dictionary<string, object?>();
        if (this.LastFrame is not { } f || this.LastDecoded is not { } d)
        {
            result["error"] = "no struct frame yet (Emj closed?)";
            return result;
        }

        result["layout"] = this.Layout.Name;
        result["healthy"] = d.Healthy;
        result["iconBase"] = d.EffectiveIconBase;
        result["baseShifted"] = d.BaseShifted;
        result["stateCode"] = d.StateCode;
        result["wallCount"] = d.WallCount;
        result["atkValuesCount"] = f.AtkValuesCount;
        result["handRaw"] = f.HandSlots;
        result["hand"] = d.ClosedTiles.Select(t => t.ToString()).ToList();
        result["drawn"] = d.DrawnTile?.ToString();
        result["dora"] = d.DoraIndicator?.ToString();
        result["doraCount"] = d.DoraIndicatorCount;
        result["uraDora"] = d.UraDoraIndicator?.ToString();
        result["seats"] = d.Seats.Select((p, i) => new Dictionary<string, object?>
        {
            ["seat"] = i,
            ["closed"] = p.ClosedTileCount,
            ["melds"] = p.MeldCount,
            ["discards"] = p.DiscardCount,
            ["riichiIndex"] = p.RiichiDiscardIndex,
            ["score"] = p.Score,
            ["pointDiff"] = p.PointDifference,
            ["meldRecords"] = p.Melds.Select(m => m.IsChi ? $"chi from={m.FromDirection}" : $"{m.Tile} from={m.FromDirection}").ToList(),
            ["structDiscards"] = d.SeatDiscards[i]?.Select(t => t.ToString()).ToList(),
        }).ToList();
        result["roundWindRaw"] = f.RoundWind;
        result["seatWindRaw"] = f.SeatWind;
        result["dealerSeatRaw"] = f.DealerSeat;
        result["honba"] = f.Honba;
        result["riichiSticks"] = f.RiichiSticks;
        result["wallRemaining"] = f.WallRemaining;
        result["readBytes"] = this.structReader.ReadBytes;
        if (includeHex)
            result["hex"] = this.structReader.LastHex();
        return result;
    }

    // Struct hand vs the visible slot nodes (the click/highlight targets).
    public Dictionary<string, object?> BuildDebugHand()
    {
        var result = new Dictionary<string, object?>
        {
            ["hand"] = this.Current?.Hand.Select(t => t.ToString()).ToList(),
            ["drawn"] = this.Current?.DrawnTile?.ToString(),
            ["structClosed"] = this.LastDecoded?.ClosedTiles.Select(t => t.ToString()).ToList(),
            ["structDrawn"] = this.LastDecoded?.DrawnTile?.ToString(),
            ["callWindow"] = this.tracker.CallWindowActive,
        };

        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        var slots = EmjScanner.ScanHandSlots(addon);
        result["slotCount"] = slots.Count;
        result["slots"] = slots.Select((s, i) => new Dictionary<string, object?>
        {
            ["slot"] = i,
            ["nodeId"] = s.NodeId,
            ["absX"] = (int)s.AbsX,
            ["tile"] = this.Current is { } c && i < c.Hand.Count ? c.Hand[i].ToString() : null,
            ["discardable"] = EmjOperator.HasAddonBoundActivation(addon, (AtkResNode*)s.NodePtr),
        }).ToList();
        return result;
    }

    public Dictionary<string, object?> BuildDebugPiles()
    {
        var s = this.Current;
        return new Dictionary<string, object?>
        {
            ["seats"] = s?.Seats.Select(x => new Dictionary<string, object?>
            {
                ["seat"] = x.Seat,
                ["discards"] = x.Discards.Select(t => t.ToString()).ToList(),
                ["riichi"] = x.Riichi,
                ["score"] = x.Score,
            }).ToList(),
            ["structCounts"] = this.LastDecoded?.DiscardCounts,
            ["verified"] = s?.Seats.Select(x => x.DiscardsVerified).ToList(),
            ["notes"] = s?.Notes,
            ["seenForAnalyzer"] = s?.SeenForAnalyzer().Select(t => t.ToString()).ToList(),
        };
    }

    public Dictionary<string, object?> BuildDebugFrame(string addonName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;
        if (addon->AtkValues == null)
        {
            result["error"] = $"{addonName} has no AtkValues";
            return result;
        }

        result["atkValuesCount"] = (int)addon->AtkValuesCount;
        result["position"] = $"X={addon->X} Y={addon->Y} scale={addon->Scale:F2}";
        var rows = new List<string>((int)addon->AtkValuesCount);
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            ref var v = ref addon->AtkValues[i];
            var tileHint = TileHelpers.TryTileFromIconId(v.Int, out var t) ? $"  →{t}" : string.Empty;
            var str = v.Type is AtkValueType.String or AtkValueType.ConstString ? EmjStructReader.SafeString(ref v) : "-";
            rows.Add($"[{i,3}] {v.Type,-12} int={v.Int,11}  str={str}{tileHint}");
        }

        result["values"] = rows;
        return result;
    }

    public Dictionary<string, object?> BuildDebugPrompt()
    {
        var result = new Dictionary<string, object?>
        {
            ["active"] = this.tracker.CallWindowActive,
            ["isClaim"] = this.tracker.CallIsClaim,
            ["options"] = this.tracker.CallOptions.ToList(),
            ["callTile"] = this.tracker.CallTile?.ToString(),
            ["callFromSeat"] = this.tracker.CallFromSeat,
            ["lastOpponentDiscard"] = this.tracker.LastOpponentDiscard?.ToString(),
            ["legal"] = this.Current?.Legal.ToString(),
        };

        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        result["buttonTextsRaw"] = EmjScanner.ScanCallButtonTexts(addon);
        var listNode = EmjScanner.FindNodeByPath(addon, this.Layout.Nodes.CallList);
        result["listRows"] = listNode == null ? null : EmjOperator.ListRows(listNode).Select(r => $"{r.Index}: {r.Label}").ToList();
        return result;
    }

    public List<string> BuildDebugEvents(int tail) => this.tracker.DebugEvents(tail);

    public List<string> BuildDebugTree(uint? nodeId, string addonName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        return addon == null ? [result["error"]?.ToString() ?? "addon unavailable"] : NodeDump.Tree(addon, nodeId);
    }

    // ── operate (EmjOperator wrappers) ──

    public Dictionary<string, object?> DebugListAddons()
        => new() { ["addons"] = EmjOperator.ListAddons() };

    public Dictionary<string, object?> DebugListNodes(string addonName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;
        result["interactables"] = EmjOperator.ListInteractables(addon);
        return result;
    }

    public Dictionary<string, object?> DebugFireEvent(string addonName, string nodeSpec, int eventType, int? param)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var node = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (node == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var fired = EmjOperator.FireEvent(addon, node, (ushort)eventType, param);
        this.tracker.Note($"op fire {addonName} node={nodeSpec}: {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugClickNode(string addonName, string nodeSpec, int? param)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var node = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (node == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var fired = EmjOperator.ClickNode(addon, node, param);
        this.tracker.Note($"op click {addonName} node={nodeSpec}: {string.Join("; ", fired)}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugClickLabel(string addonName, string label)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var clicked = EmjOperator.ClickByLabel(addon, label);
        if (clicked is null)
        {
            result["error"] = $"no visible text matching '{label}' in {addonName}";
            return result;
        }

        this.tracker.Note($"op call \"{label}\" → {clicked["matchedText"]} {clicked["target"]}: {string.Join("; ", (List<string>)clicked["fired"]!)}");
        result["clicked"] = clicked;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugListClick(string addonName, string nodeSpec, int index)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var listNode = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (listNode == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var renderer = EmjOperator.ListRendererAt(listNode, index);
        var fired = EmjOperator.SelectListItem(addon, listNode, renderer, index);
        if (fired is null)
        {
            result["error"] = $"node '{nodeSpec}' has no registered ListItemClick";
            return result;
        }

        this.tracker.Note($"op listclick {addonName} node={nodeSpec} index={index}: {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugFireCallback(string addonName, int[] values)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var fired = EmjOperator.FireCallback(addon, values);
        this.tracker.Note($"op {addonName} {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    // Discards by visual slot index or tile name ("8s"). The struct hand is in visual
    // order (sorted slots, then the draw), so its index is the slot node index.
    public Dictionary<string, object?> DebugDiscard(int? slot, string? tileName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        var hand = this.Current?.Hand ?? [];
        var slots = EmjScanner.ScanHandSlots(addon);
        var index = slot ?? -1;
        if (index < 0 && tileName is not null)
        {
            Tile wanted;
            try
            {
                wanted = Tile.Parse(tileName);
            }
            catch (Exception ex)
            {
                result["error"] = $"unparseable tile '{tileName}': {ex.Message}";
                return result;
            }

            index = FindVisualIndex(hand, wanted);
            if (index < 0)
            {
                result["error"] = $"{wanted} is not in the hand [{string.Join(" ", hand)}]";
                return result;
            }
        }

        if (index < 0 || index >= slots.Count)
        {
            result["error"] = $"slot {index} out of range (0-{slots.Count - 1})";
            return result;
        }

        var tile = index < hand.Count ? hand[index].ToString() : "?";
        if (!EmjOperator.HasAddonBoundActivation(addon, (AtkResNode*)slots[index].NodePtr))
        {
            result["error"] = $"slot {index} ({tile}) has no addon-bound activation — "
                + "not discardable right now (claim window open / not your turn / ghost slot)";
            return result;
        }

        // Registered chain params are authoritative (ButtonClick param is slot+15).
        var fired = EmjOperator.ClickNode(addon, (AtkResNode*)slots[index].NodePtr, null);
        this.tracker.Note($"op discard slot={index} ({tile}): {string.Join("; ", fired)}");
        result["slot"] = index;
        result["tile"] = tile;
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    // Hovers a hand slot (MouseOver only) and returns the addon's tile-name response —
    // an independent cross-check of the struct read for that slot.
    public Dictionary<string, object?> DebugHoverSlot(int slot)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        var slots = EmjScanner.ScanHandSlots(addon);
        if (slot < 0 || slot >= slots.Count)
        {
            result["error"] = $"slot {slot} out of range (0-{slots.Count - 1})";
            return result;
        }

        var fired = EmjOperator.FireEvent(addon, (AtkResNode*)slots[slot].NodePtr, (ushort)AtkEventType.MouseOver, slot);
        var name = addon->AtkValuesCount >= 2 ? EmjStructReader.SafeString(ref addon->AtkValues[1]) : "-";
        this.tracker.Note($"op hover slot={slot}: {fired} → \"{name}\"");
        result["slot"] = slot;
        result["fired"] = fired;
        result["atkValues1"] = name;
        result["structTile"] = this.Current is { } c && slot < c.Hand.Count ? c.Hand[slot].ToString() : null;
        return result;
    }

    // Last matching index: with a duplicate kind the rightmost copy (the draw) is the
    // natural discard and keeps the sorted part of the hand intact.
    public static int FindVisualIndex(IReadOnlyList<Tile> hand, Tile wanted)
    {
        for (var i = hand.Count - 1; i >= 0; i--)
            if (hand[i].Equals(wanted))
                return i;
        for (var i = hand.Count - 1; i >= 0; i--)
            if (TileHelpers.SameKind(hand[i], wanted))
                return i;
        return -1;
    }

    private void AppendOperateState(Dictionary<string, object?> result)
    {
        result["hand"] = this.Current?.Hand.Select(t => t.ToString()).ToList();
        result["callWindowActive"] = this.tracker.CallWindowActive;
        result["callTile"] = this.tracker.CallTile?.ToString();
    }

    private AtkUnitBase* GetDebugAddon(Dictionary<string, object?> result)
        => this.GetDebugAddonByName(this.Layout.AddonName, result);

    private AtkUnitBase* GetDebugAddonByName(string addonName, Dictionary<string, object?> result)
    {
        var addonPtr = this.gameGui.GetAddonByName(addonName);
        if (addonPtr.IsNull)
        {
            result["error"] = $"{addonName} addon not open";
            return null;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null)
        {
            result["error"] = $"{addonName} addon not ready";
            return null;
        }

        return addon;
    }
}
