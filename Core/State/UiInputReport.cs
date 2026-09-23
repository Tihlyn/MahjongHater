using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Why the game may not be taking mouse input on the table, captured while it is happening.
//
// The table going unselectable while auto play keeps working is the signature of a broken
// INPUT path with a healthy addon: our operator dispatches AtkEvents straight to the
// addon's listeners and never touches focus, hit-testing or the message pump, so it is
// unaffected by anything that stops real clicks arriving.
//
// Nothing here changes game state. It answers the three questions that separate the
// candidates: is another unit holding focus or sitting in front of the table (including a
// unit that is open but invisible - the shape a hidden Duty Finder leaves behind), is the
// table itself still visible and collidable, and is Dalamud's own ImGui layer swallowing
// the mouse before the game ever sees it.
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
        var lines = new List<string>(24);
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

        lines.Add("-- focus --");
        var focused = &manager->AtkUnitManager.FocusedUnitsList;
        lines.Add($"focused units ({focused->Count}): [{string.Join(", ", Names(focused))}]");
        lines.Add($"table '{tableAddon}' has focus: {Names(focused).Any(n => n == tableAddon)}");

        lines.Add("-- the table --");
        var ptr = gameGui.GetAddonByName(tableAddon);
        if (ptr.IsNull)
        {
            lines.Add($"'{tableAddon}' is not open");
        }
        else
        {
            var addon = (AtkUnitBase*)ptr.Address;
            lines.Add($"open=true visible={addon->IsVisible} rootVisible={(addon->RootNode != null && addon->RootNode->IsVisible())} "
                      + $"ready={addon->IsFullyLoaded()} visibilityFlags=0x{addon->VisibilityFlags:X2} "
                      + $"showHide=0x{addon->ShowHideFlags:X2} depthLayer={addon->DepthLayer} drawOrder={addon->DrawOrderIndex}");
            lines.Add($"position=({addon->X},{addon->Y}) scale={addon->Scale:0.##} "
                      + $"collisionNodes={addon->CollisionNodeListCount} hostId={addon->HostId} parentId={addon->ParentId}");
        }

        // The decisive measurement when focus and visibility both look healthy: does the
        // game's own hit test put the cursor on the table at all? A null or foreign
        // intersecting node while the pointer is over a tile means clicks never reach the
        // addon, and no amount of addon state will explain it.
        lines.Add("-- what the game thinks the cursor is over --");
        var collision = stage->AtkCollisionManager;
        if (collision == null)
        {
            lines.Add("AtkCollisionManager unavailable");
        }
        else
        {
            var hovered = collision->IntersectingCollisionNode;
            lines.Add($"intersecting collision node: {(hovered == null ? "NONE - the game is not hit-testing anything under the cursor" : $"id {hovered->AtkResNode.NodeId}, visible={hovered->AtkResNode.IsVisible()}")}");
        }

        lines.Add($"cursor type={stage->AtkCursor.Type}");

        lines.Add("-- other units that gate or overlay input --");
        var all = &manager->AtkUnitManager.AllLoadedUnitsList;
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
    // and the table: who holds focus, and which of the gating units are open. Logged only
    // when it CHANGES, so a session shows the moment something appeared and never left.
    // Restricted to the list above on purpose - plenty of ordinary HUD units sit open and
    // invisible all match, and reporting those would bury the one that matters.
    public static string Blockers(IGameGui gameGui, string tableAddon)
    {
        var stage = AtkStage.Instance();
        var manager = stage == null ? null : stage->RaptureAtkUnitManager;
        if (manager == null)
            return "unavailable";

        var focused = string.Join("+", Names(&manager->AtkUnitManager.FocusedUnitsList));
        var open = new List<string>(4);
        var all = &manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < all->Count && i < 256; i++)
        {
            var unit = all->Entries[i].Value;
            if (unit == null)
                continue;
            var name = unit->NameString;
            if (name != tableAddon && Interesting.Contains(name))
                open.Add(name + (unit->IsVisible ? string.Empty : "(invisible)"));
        }

        var ptr = gameGui.GetAddonByName(tableAddon);
        var tableVisible = !ptr.IsNull && ((AtkUnitBase*)ptr.Address)->IsVisible;
        return $"focus=[{(focused.Length == 0 ? "none" : focused)}] table={(ptr.IsNull ? "closed" : tableVisible ? "visible" : "INVISIBLE")}"
               + $" others=[{string.Join(", ", open)}]";
    }

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
