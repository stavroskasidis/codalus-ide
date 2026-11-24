using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>The adapter's process says in its log why it ends (Linux CI, PR #530's run 45335957581; docs/ci-flakes.md).</summary>
public sealed class NetFxAdapterTests
{
    [Fact]
    public async Task The_log_says_the_adapter_ended_because_its_clients_stream_ended()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var trace = new StringWriter();

        var code = await NetFxAdapter.RunAsync(input, output, TextWriter.Null, trace: trace).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("[adapter] ends: the client's stream ended", trace.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_end_is_told_apart()
    {
        var pending = new TaskCompletionSource().Task;
        Assert.StartsWith("the session ended", NetFxAdapter.WhyItEnds(pending, Task.CompletedTask), StringComparison.Ordinal);
        Assert.Equal("the client's stream ended", NetFxAdapter.WhyItEnds(Task.CompletedTask, pending));
        Assert.Equal("reading the client's stream failed: IOException: The pipe broke.", NetFxAdapter.WhyItEnds(Task.FromException(new IOException("The pipe broke.")), pending));
        Assert.Equal("reading the client's stream was cancelled", NetFxAdapter.WhyItEnds(Task.FromCanceled(new CancellationToken(canceled: true)), pending));
    }
}
