using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The adapter's IL walker (M58): a method body's calls (Step Into Specific, the returned value), and whether a
/// stretch of it runs straight (Edit and Continue: a frame back from a call moves at the statement after it only when
/// it can't have gone another way).
/// </summary>
public sealed class IlCallsTests
{
    // ldloc.0; call 0x06000001; call 0x0A000002; nop; ldloc.0; brfalse.s +1; nop; ret
    private static readonly byte[] s_il = [0x06, 0x28, 0x01, 0x00, 0x00, 0x06, 0x28, 0x02, 0x00, 0x00, 0x0A, 0x00, 0x06, 0x2C, 0x01, 0x00, 0x2A];

    [Fact]
    public void Lists_the_calls_with_their_tokens_and_ends()
    {
        var calls = IlCalls.Of(s_il);
        Assert.Equal([(1, 6, 0x06000001), (6, 11, 0x0A000002)], calls.Select(c => (c.Offset, c.End, c.Token)));
        Assert.Equal(0x0A000002, IlCalls.ReturnedAt(s_il, 6)?.Token);
        Assert.Null(IlCalls.ReturnedAt(s_il, 12));
    }

    [Theory]
    [InlineData(0, 12, true)] // the two calls and the nop: one after another
    [InlineData(0, 11, true)]
    [InlineData(12, 15, false)] // a branch
    [InlineData(0, 16, false)]
    [InlineData(15, 17, false)] // a return
    [InlineData(2, 12, false)] // from inside the first call's token
    [InlineData(0, 3, false)] // to inside it
    [InlineData(12, 12, false)]
    [InlineData(0, 40, false)]
    public void Says_whether_a_stretch_runs_straight(int from, int to, bool straight) =>
        Assert.Equal(straight, IlCalls.IsStraight(s_il, from, to));

    [Fact]
    public void A_switch_and_the_two_byte_transfers_arent_straight()
    {
        Assert.False(IlCalls.IsStraight([0x16, 0x45, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 0, 11)); // ldc.i4.0; switch (1 target)
        Assert.False(IlCalls.IsStraight([0xFE, 0x1A, 0x00], 0, 3)); // rethrow
        Assert.True(IlCalls.IsStraight([0xFE, 0x0C, 0x01, 0x00, 0x00], 0, 5)); // ldloc 1; nop
    }
}
