namespace Codalus.LanguageServices.Host.Tests;

/// <summary>The language host's compacting collection after bursts of work only, not after every pause in typing.</summary>
public sealed class MemoryTrimmerTests
{
    [Fact]
    public void Small_work_does_not_collect_and_a_burst_does_once_a_minute()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        long allocated = 0;
        long fragmented = 0;
        var collections = 0;
        using var trimmer = new MemoryTrimmer(() => now, () => allocated, () => fragmented, () => collections++);

        allocated = MemoryTrimmer.BurstBytes; // a project load
        Assert.True(trimmer.Trim());

        now = now.AddMinutes(5);
        allocated += 10 * 1024 * 1024; // typing for a while
        Assert.False(trimmer.Trim());

        allocated += MemoryTrimmer.BurstBytes; // the background pass
        Assert.True(trimmer.Trim());

        now = now.AddSeconds(30);
        allocated += MemoryTrimmer.BurstBytes; // within the minute: later
        Assert.False(trimmer.Trim());

        now = now.AddMinutes(1);
        Assert.True(trimmer.Trim());

        now = now.AddMinutes(5);
        fragmented = MemoryTrimmer.BurstBytes; // little allocated, but a fragmented heap
        Assert.True(trimmer.Trim());
        Assert.Equal(4, collections);
    }
}
