using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Doman Mahjong rank and rating, as the Gold Saucer Info window shows them. Nothing in the
// game's modules exposes them (FFXIVClientStructs has no Mahjong data at all, and the Emj
// addon's own struct carries none — docs/research/EMJ_STRUCT_SURVEY.md), so the only source
// is AddonGSInfoEmj's text nodes while that window is open. The plugin therefore reads it
// opportunistically and remembers the last value, which is why the overlay can show a rank
// outside the Gold Saucer.
public sealed record MahjongRank(string Rank, string Rating, string HighestRating, string MatchesPlayed)
{
    public bool IsEmpty => this.Rank.Length == 0 && this.Rating.Length == 0;
}

public static unsafe class MahjongRankReader
{
    // The game's own name for the window; the two casings cover the spellings seen in
    // FFXIVClientStructs (AddonGSInfoEmj) and the client's class list (AddonGsInfoEmj).
    private static readonly string[] AddonNames = ["GSInfoEmj", "GsInfoEmj"];

    public static MahjongRank? TryRead(IGameGui gameGui)
    {
        foreach (var name in AddonNames)
        {
            var ptr = gameGui.GetAddonByName(name);
            if (ptr.IsNull)
                continue;
            var addon = (AddonGSInfoEmj*)ptr.Address;
            if (addon->AtkUnitBase.RootNode == null || !addon->AtkUnitBase.IsVisible)
                continue;
            var read = new MahjongRank(
                Text(addon->Rank), Text(addon->CurrentRating), Text(addon->HighestRating), Text(addon->MatchesPlayed));
            // The window builds its nodes before filling them; an empty read is not a value.
            return read.IsEmpty ? null : read;
        }

        return null;
    }

    private static string Text(AtkTextNode* node) => node is null ? string.Empty : EmjScanner.ReadTextNode(node);
}
