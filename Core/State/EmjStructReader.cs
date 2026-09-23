using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Copies the AddonEmj struct prefix and the leading AtkValues out of game memory.
// Framework-thread only; the only place besides EmjScanner/EmjOperator that dereferences
// the addon. Everything downstream works on the plain copies.
internal sealed unsafe class EmjStructReader
{
    private readonly byte[] buffer;

    public EmjStructReader(EmjLayout layout)
    {
        this.buffer = new byte[layout.RequiredBytes];
    }

    public int ReadBytes => this.buffer.Length;

    public bool TryRead(AtkUnitBase* addon, EmjLayout layout, out StructFrame frame)
    {
        frame = null!;
        if (addon == null)
            return false;

        try
        {
            new ReadOnlySpan<byte>((byte*)addon, this.buffer.Length).CopyTo(this.buffer);
            var count = addon->AtkValues == null ? 0 : (int)addon->AtkValuesCount;
            var stateCode = ReadInt(addon, layout.StateCodeIndex, count) ?? -1;
            var wallCount = ReadInt(addon, layout.WallCountIndex, count) ?? -1;
            frame = StructFrame.FromBytes(this.buffer, layout, stateCode, wallCount, count);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Raw hex of the struct prefix (fixture capture via /struct?hex=1).
    public string LastHex() => Convert.ToHexString(this.buffer);

    // Snapshot of the leading AtkValues for the tracker (ints + strings, typed).
    public static AtkFrame CopyAtkValues(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null)
            return AtkFrame.OfInts();

        var count = (int)addon->AtkValuesCount;
        var n = Math.Min(count, AtkFrame.MaxCopied);
        var ints = new int[n];
        var strings = new string?[n];
        var isInt = new bool[n];
        for (var i = 0; i < n; i++)
        {
            ref var v = ref addon->AtkValues[i];
            switch (v.Type)
            {
                case AtkValueType.Int:
                case AtkValueType.UInt:
                case AtkValueType.Bool:
                    ints[i] = v.Int;
                    isInt[i] = true;
                    break;
                case AtkValueType.String:
                case AtkValueType.ConstString:
                case AtkValueType.ManagedString:
                    // ManagedString was missing until 2026-09-23 and the values written with it
                    // were dropped silently. The round recap uses it for yaku names and for
                    // every per-yaku han, so a two-yaku win read as one yaku worth 0 han.
                    strings[i] = SafeString(ref v);
                    break;
            }
        }

        return new AtkFrame(count, ints, strings, isInt);
    }

    public static string SafeString(ref AtkValue v)
    {
        try { return v.String.ToString() ?? string.Empty; }
        catch { return $"ptr=0x{v.UInt:X}"; }
    }

    private static int? ReadInt(AtkUnitBase* addon, int index, int count)
    {
        if (index < 0 || index >= count)
            return null;
        ref var v = ref addon->AtkValues[index];
        return v.Type is AtkValueType.Int or AtkValueType.UInt ? v.Int : null;
    }
}
