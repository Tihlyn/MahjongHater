using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core.Operate;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Operate;

public unsafe class NativeDispatchTests
{
    [Fact]
    public void Handler_can_mark_temporary_event_without_mutating_registration()
    {
        var original = new AtkEvent { Param = 28, Node = (AtkResNode*)0x1234, NextEvent = (AtkEvent*)0x5678 };
        original.State.EventType = AtkEventType.ButtonClick;
        var dispatch = EmjOperator.CopyForDispatch(original);
        dispatch.State.StateFlags |= AtkEventStateFlags.Handled;
        Assert.Equal(AtkEventStateFlags.None, original.State.StateFlags);
        Assert.True(original.NextEvent == (AtkEvent*)0x5678);
        Assert.True(dispatch.NextEvent == null);
        Assert.True(dispatch.Node == original.Node);
        Assert.Equal(original.Param, dispatch.Param);
        Assert.Equal(original.State.EventType, dispatch.State.EventType);
    }

    [Fact]
    public void Short_refresh_does_not_include_old_scoring_tail()
    {
        var values = stackalloc AtkValue[109];
        for (var i = 0; i < 109; i++)
            values[i] = new AtkValue { Type = AtkValueType.Int, Int = 76041 };
        values[0].Int = 12;
        values[1].Int = 0;
        var frame = EmjStructReader.CopyAtkValues(values, 2);
        Assert.Equal(2, frame.Count);
        Assert.Equal(2, frame.Copied);
        Assert.False(frame.IsInt(24));
        Assert.Equal(0, frame.Int(24));
    }
}
