namespace MahjongHater.Core.State;

// Plain copy of the leading AtkValues of one refresh/receive event, so the tracker never
// touches game memory and tests can replay recorded frames.
public sealed class AtkFrame
{
    // Highest slot the tracker reads is [37] (deal hand); the rest of the prefix exists so a
    // round-end hand reveal (up to 4 × 14 icons) can be captured and its layout confirmed.
    public const int MaxCopied = 96;

    private readonly int[] ints;
    private readonly string?[] strings;
    private readonly bool[] isInt;

    public AtkFrame(int count, int[] ints, string?[] strings, bool[] isInt)
    {
        this.Count = count;
        this.ints = ints;
        this.strings = strings;
        this.isInt = isInt;
    }

    // AtkValuesCount as reported by the addon (may exceed the copied prefix).
    public int Count { get; }

    public int Copied => this.ints.Length;

    public int Int(int i) => i >= 0 && i < this.ints.Length ? this.ints[i] : 0;

    public string? Str(int i) => i >= 0 && i < this.strings.Length ? this.strings[i] : null;

    public bool IsInt(int i) => i >= 0 && i < this.isInt.Length && this.isInt[i];

    public int EventType => this.Int(0);

    // Test/replay helper: ints only, every slot typed Int.
    public static AtkFrame OfInts(params int[] values)
    {
        var flags = new bool[values.Length];
        Array.Fill(flags, true);
        return new AtkFrame(values.Length, values, new string?[values.Length], flags);
    }

    public AtkFrame WithString(int index, string value)
    {
        var s = (string?[])this.strings.Clone();
        var f = (bool[])this.isInt.Clone();
        if (index >= s.Length)
        {
            Array.Resize(ref s, index + 1);
            Array.Resize(ref f, index + 1);
        }

        s[index] = value;
        f[index] = false;
        var i = this.ints;
        if (index >= i.Length)
            Array.Resize(ref i, index + 1);
        return new AtkFrame(Math.Max(this.Count, index + 1), i, s, f);
    }
}
