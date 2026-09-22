namespace MahjongHater.Core.Operate;

// One row of a live AtkComponentList as its own item table reports it. Everything here
// is a copy taken on the framework thread before any dispatch: no pointers survive.
public readonly record struct ListRow(int Index, string Label, bool HasRenderer, bool Enabled);

// Where an answer should go, or why it must not be sent at all.
public readonly record struct RowChoice(int Index, string Why)
{
    public bool Found => this.Index >= 0;

    public static RowChoice Reject(string why) => new(-1, why);

    public static RowChoice At(int index, string why) => new(index, why);
}

// Which row of the call list answers a decision — and, more often, why no row may be
// clicked. Pure and copy-only so the rejection rules have deterministic tests; the
// pointer work lives in EmjOperator.
//
// Every rule here comes from a confirmed live defect
// (docs/research/CALL_WINDOW_AUDIT_2026_09_22.md):
//   * the panel and its list keep their rows after a prompt closes, so a hidden list
//     answered "Pass" at 18:27:48 against a finished window;
//   * the old label search fell back to renderer state and ultimately to ROW 0 when the
//     item table did not contain the match, turning "target not found" into "click the
//     first thing on the list";
//   * a row whose renderer is missing cannot carry a valid list payload at all.
// So: the list must be on screen, the wanted label must match exactly one live row, that
// row must have a renderer and be enabled, and the rows must still describe the window
// the tracker has open. Anything else is a rejection with a reason, never a guess.
public static class CallRowResolver
{
    // The row every call list carries alongside the offers.
    public const string PassRow = "Pass";

    public static string Normalize(string? label) => (label ?? string.Empty).Trim().TrimEnd('!').Trim();

    // wanted: the option to click ("Pon", "Chi", "Ron", "Riichi", "Tsumo", "Kan", "Pass").
    // expectedOptions: the options the tracker's OPEN window carries (without Pass); an
    // empty list means "no window is open", which is itself a rejection.
    public static RowChoice Resolve(IReadOnlyList<ListRow> rows, bool listVisible, string wanted,
        IReadOnlyList<string> expectedOptions)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(expectedOptions);
        var want = Normalize(wanted);
        if (want.Length == 0)
            return RowChoice.Reject("no option to click");
        if (!listVisible)
            return RowChoice.Reject("the call list is not on screen (stale rows)");
        if (rows.Count == 0)
            return RowChoice.Reject("the call list is empty");
        if (expectedOptions.Count == 0)
            return RowChoice.Reject("no call window is open");

        // The live rows must still describe the open window. A list left over from an
        // earlier prompt reads "Ron, Pass" while the tracker's window is "Chi" — clicking
        // its row 1 is exactly the 18:27:48 defect.
        var labels = rows.Select(r => Normalize(r.Label)).Where(l => l.Length > 0).ToList();
        var missing = expectedOptions.Select(Normalize)
            .Where(o => o.Length > 0 && !labels.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            return RowChoice.Reject($"the list rows [{string.Join(", ", labels)}] do not carry the open window's options [{string.Join(", ", missing)}]");

        // Pass is implicit in every window; any other answer must be one the game offered.
        if (!want.Equals(PassRow, StringComparison.OrdinalIgnoreCase)
            && !expectedOptions.Any(o => Normalize(o).Equals(want, StringComparison.OrdinalIgnoreCase)))
            return RowChoice.Reject($"'{want}' is not among the open window's options [{string.Join(", ", expectedOptions)}]");

        var matches = rows.Where(r => Normalize(r.Label).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return RowChoice.Reject($"no row labelled '{want}' in [{string.Join(", ", labels)}]");
        if (matches.Count > 1)
            return RowChoice.Reject($"'{want}' matches {matches.Count} rows ([{string.Join(", ", matches.Select(m => m.Index))}]) — ambiguous");

        var row = matches[0];
        if (!row.HasRenderer)
            return RowChoice.Reject($"row {row.Index} ('{want}') has no item renderer — a list event would carry no valid target");
        if (!row.Enabled)
            return RowChoice.Reject($"row {row.Index} ('{want}') is disabled");
        return RowChoice.At(row.Index, $"row {row.Index} of [{string.Join(", ", labels)}]");
    }
}
