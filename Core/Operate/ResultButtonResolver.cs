namespace MahjongHater.Core.Operate;

internal readonly record struct ResultButton(uint NodeId, string Label, bool Clickable);

internal static class ResultButtonResolver
{
    // EmjRankResult has no node 26. Resolve its actual button, not the close control
    // measured on EmjTotalResult. Hidden/disabled labels cannot identify a live action.
    public static uint? RankEndMatch(IReadOnlyList<ResultButton> buttons)
    {
        uint? found = null;
        foreach (var button in buttons)
        {
            if (!button.Clickable || !string.Equals(button.Label.Trim(), "End match", StringComparison.OrdinalIgnoreCase))
                continue;
            if (found.HasValue)
                return null;
            found = button.NodeId;
        }

        return found;
    }
}
