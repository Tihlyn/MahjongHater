using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Why the game may not be taking mouse input on the table, captured while it is happening.
//
// Rewritten 2026-09-23 after the live session that settled what this bug is NOT. With the
// table unclickable and focus gone:
//   * disabling the plugin entirely did not restore either, so nothing of ours is holding
//     input while it is broken;
//   * a Cartographer-injected ListItemClick still worked, and auto play still played the
//     match, so the ADDON is alive and its handlers are fine;
//   * the prompt was structurally intact - list visible, ListItemClick bound to the addon,
//     every row carrying MouseDown/MouseUp/MouseClick collision;
//   * no modal, no invisible gating unit, and IdleGuard never fired all session.
// The break is therefore in the game's own input layer, between the mouse and an addon that
// is perfectly willing to be clicked, and it persists.
//
// So this stopped being a list of suspects and became a measurement of that layer, on ONE
// frame, because the audit was right that an empty focus list alone proves nothing. The
// question it has to answer is which of these is true at the moment the table is dead:
// nobody holds focus; the table has fallen out of the depth-layer lists the game hit-tests;
// the cursor is not where the game thinks it is; the hit test finds nothing or finds a node
// belonging to something else; or Dalamud's ImGui layer is eating the mouse first.
//
// Nothing here changes game state.
internal static unsafe class UiInputReport
{
    // Units worth naming even when they are not focused: they overlay or gate the table.
    private static readonly string[] Interesting =
    [
        "ContentsFinder", "ContentsFinderConfirm", "ContentsFinderMenu", "SelectYesno",
        "SelectString", "SelectIconString", "ContextMenu", "Talk", "JournalDetail",
        "_TitleMenu", "SystemMenu", "ConfigCharacter", "Emj", "EmjIntro",
    ];

    public static List<string> Build(IGameGui gameGui, string tableAddon)
    {
        var lines = new List<string>(40);
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            lines.Add("AtkStage unavailable");
            return lines;
        }

        var manager = stage->RaptureAtkUnitManager;
        if (manager == null)
        {
            lines.Add("RaptureAtkUnitManager unavailable");
            return lines;
        }

        var units = &manager->AtkUnitManager;
        var ptr = gameGui.GetAddonByName(tableAddon);
        var table = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;

        lines.Add("-- focus --");
        var focused = &units->FocusedUnitsList;
        var focusNames = Names(focused);
        lines.Add($"focused units ({focused->Count}): [{string.Join(", ", focusNames)}]");
        lines.Add($"table '{tableAddon}' has focus: {focusNames.Contains(tableAddon)}");
        lines.Add($"AtkUnitManager.Flags=0x{units->Flags:X2}");

        // THE membership test. The game hit-tests the depth-layer lists, not "every loaded
        // unit": a unit that is visible, ready and collidable but absent from all thirteen is
        // simply never considered for a click, which looks exactly like a dead window and
        // would survive anything the plugin does or stops doing.
        lines.Add("-- depth-layer membership (what the game hit-tests) --");
        var found = new List<string>(2);
        var depthCounts = new List<string>(13);
        for (var layer = 1; layer <= 13; layer++)
        {
            var list = DepthLayer(units, layer);
            if (list == null)
                continue;
            depthCounts.Add($"L{layer}:{list->Count}");
            for (var i = 0; i < list->Count && i < 128; i++)
            {
                var unit = list->Entries[i].Value;
                if (unit != null && unit->NameString == tableAddon)
                    found.Add($"layer {layer} index {i}");
            }
        }

        lines.Add($"layer counts: {string.Join(" ", depthCounts)}");
        lines.Add(found.Count > 0
            ? $"'{tableAddon}' is in: {string.Join(", ", found)}"
            : $"'{tableAddon}' IS IN NO DEPTH LAYER — the game never hit-tests it, so no click can reach it");

        lines.Add("-- the table --");
        if (table == null)
        {
            lines.Add($"'{tableAddon}' is not open");
        }
        else
        {
            lines.Add($"open=true visible={table->IsVisible} rootVisible={(table->RootNode != null && table->RootNode->IsVisible())} "
                      + $"ready={table->IsFullyLoaded()} uldState={table->UldManager.LoadedState} "
                      + $"visibilityFlags=0x{table->VisibilityFlags:X2} showHide=0x{table->ShowHideFlags:X2} "
                      + $"depthLayer={table->DepthLayer} drawOrder={table->DrawOrderIndex}");
            lines.Add($"rect=({table->X},{table->Y}) {table->GetScaledWidth(true)}x{table->GetScaledHeight(true)} "
                      + $"scale={table->Scale:0.##} collisionNodes={table->CollisionNodeListCount} "
                      + $"hostId={table->HostId} parentId={table->ParentId}");
            lines.Add($"input blockers={table->NumBlockingAddons} shouldIgnoreInputs={table->ShouldIgnoreInputs()}");
        }

        // Same-frame pointer evidence. The audit's point stands: a null intersection proves
        // nothing on its own, because the cursor may simply be elsewhere. The cursor position
        // against the table's own rectangle is what makes the intersection readable.
        lines.Add("-- the pointer, this frame --");
        var input = UIInputData.Instance();
        if (input == null)
        {
            lines.Add("UIInputData unavailable");
        }
        else
        {
            var cx = input->CursorInputs.PositionX;
            var cy = input->CursorInputs.PositionY;
            var inside = table != null
                         && cx >= table->X && cx <= table->X + table->GetScaledWidth(true)
                         && cy >= table->Y && cy <= table->Y + table->GetScaledHeight(true);
            lines.Add($"cursor=({cx},{cy}) insideTableRect={(table == null ? "n/a" : inside.ToString())}");
        }

        var collision = stage->AtkCollisionManager;
        if (collision == null)
        {
            lines.Add("AtkCollisionManager unavailable");
        }
        else
        {
            var hovered = collision->IntersectingCollisionNode;
            lines.Add(hovered == null
                ? "intersecting collision node: NONE — the game's hit test finds nothing under the cursor"
                : $"intersecting collision node: id {hovered->AtkResNode.NodeId} visible={hovered->AtkResNode.IsVisible()}");
        }

        lines.Add($"cursor type={stage->AtkCursor.Type}");

        // If Dalamud wants the mouse, the game never sees the click at all — and this is OUR
        // layer, so it is the one candidate here that we could be responsible for.
        try
        {
            var io = ImGui.GetIO();
            lines.Add($"ImGui wantCaptureMouse={io.WantCaptureMouse} wantCaptureKeyboard={io.WantCaptureKeyboard}");
        }
        catch (Exception ex)
        {
            lines.Add($"ImGui IO unavailable ({ex.GetType().Name})");
        }

        var module = RaptureAtkModule.Instance();
        lines.Add(module == null ? "RaptureAtkModule unavailable" : $"uiVisible={module->IsUiVisible}");

        lines.Add("-- other units that gate or overlay input --");
        var all = &units->AllLoadedUnitsList;
        var noted = 0;
        for (var i = 0; i < all->Count && i < 256; i++)
        {
            var unit = all->Entries[i].Value;
            if (unit == null)
                continue;
            var name = unit->NameString;
            if (name == tableAddon || !Interesting.Contains(name))
                continue;
            // ShowHideFlags bit 0 is the unit telling us it has hidden ITSELF, which is the
            // resting state of every menu that is merely loaded - ContextMenu and Talk sit
            // like that all match. Only a unit that is invisible WITHOUT having asked to be
            // is worth pointing at, so the marker does not cry wolf on normal background UI.
            var restingHidden = (unit->ShowHideFlags & 1) != 0;
            lines.Add($"{name}: visible={unit->IsVisible} visibilityFlags=0x{unit->VisibilityFlags:X2} "
                      + $"showHide=0x{unit->ShowHideFlags:X2} depthLayer={unit->DepthLayer} "
                      + $"collisionNodes={unit->CollisionNodeListCount}"
                      + (unit->IsVisible ? string.Empty
                         : restingHidden ? "   (hidden by itself: normal resting state)"
                         : "   <-- OPEN, INVISIBLE, AND NOT HIDDEN BY ITSELF"));
            noted++;
        }

        if (noted == 0)
            lines.Add("none open");
        return lines;
    }

    // A one-line signature of everything that could be standing between the player's mouse
    // and the table. Logged only when it CHANGES, so a session shows the moment something
    // appeared and never left. Depth-layer membership is in here because its disappearance is
    // the event we most want stamped with a time.
    public static string Blockers(IGameGui gameGui, string tableAddon)
    {
        var stage = AtkStage.Instance();
        var manager = stage == null ? null : stage->RaptureAtkUnitManager;
        if (manager == null)
            return "unavailable";

        var units = &manager->AtkUnitManager;
        var focused = string.Join("+", Names(&units->FocusedUnitsList));
        var open = new List<string>(4);
        var all = &units->AllLoadedUnitsList;
        for (var i = 0; i < all->Count && i < 256; i++)
        {
            var unit = all->Entries[i].Value;
            if (unit == null)
                continue;
            var name = unit->NameString;
            if (name != tableAddon && Interesting.Contains(name))
                open.Add(name + (unit->IsVisible ? string.Empty : "(invisible)"));
        }

        var layer = -1;
        for (var l = 1; l <= 13 && layer < 0; l++)
        {
            var list = DepthLayer(units, l);
            for (var i = 0; list != null && i < list->Count && i < 128; i++)
            {
                var unit = list->Entries[i].Value;
                if (unit != null && unit->NameString == tableAddon)
                {
                    layer = l;
                    break;
                }
            }
        }

        var ptr = gameGui.GetAddonByName(tableAddon);
        var tableVisible = !ptr.IsNull && ((AtkUnitBase*)ptr.Address)->IsVisible;
        return $"focus=[{(focused.Length == 0 ? "none" : focused)}] table={(ptr.IsNull ? "closed" : tableVisible ? "visible" : "INVISIBLE")}"
               + $" inputBlockers={(ptr.IsNull ? "n/a" : ((AtkUnitBase*)ptr.Address)->NumBlockingAddons.ToString())}"
               + $" depthLayer={(layer < 0 ? "NONE" : layer.ToString())}"
               + $" others=[{string.Join(", ", open)}]";
    }

    // The thirteen lists are separate named fields rather than an array, so the mapping is
    // written out once here instead of at both call sites.
    private static AtkUnitList* DepthLayer(AtkUnitManager* units, int layer) => layer switch
    {
        1 => &units->DepthLayerOneList,
        2 => &units->DepthLayerTwoList,
        3 => &units->DepthLayerThreeList,
        4 => &units->DepthLayerFourList,
        5 => &units->DepthLayerFiveList,
        6 => &units->DepthLayerSixList,
        7 => &units->DepthLayerSevenList,
        8 => &units->DepthLayerEightList,
        9 => &units->DepthLayerNineList,
        10 => &units->DepthLayerTenList,
        11 => &units->DepthLayerElevenList,
        12 => &units->DepthLayerTwelveList,
        13 => &units->DepthLayerThirteenList,
        _ => null,
    };

    private static List<string> Names(AtkUnitList* list)
    {
        var names = new List<string>(list->Count);
        for (var i = 0; i < list->Count && i < 64; i++)
        {
            var unit = list->Entries[i].Value;
            if (unit != null)
                names.Add(unit->NameString);
        }

        return names;
    }
}
