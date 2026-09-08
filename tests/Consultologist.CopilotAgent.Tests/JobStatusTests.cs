using Consultologist.CopilotAgent.Core.Engine;

using Xunit;

namespace Consultologist.CopilotAgent.Tests;

public class JobStatusTests
{
    [Theory]
    [InlineData("Queued")]
    [InlineData("Scheduled")]
    [InlineData("Running")]
    [InlineData("queued")]      // case-insensitive
    [InlineData("SomethingNew")] // unknown transient status → keep polling
    public void Non_terminal_statuses_keep_the_poller_running(string status)
    {
        Assert.False(JobStatus.IsTerminal(status));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("completed")]   // case-insensitive
    public void Terminal_statuses_stop_the_poller(string status)
    {
        Assert.True(JobStatus.IsTerminal(status));
    }

    [Fact]
    public void Null_status_is_not_terminal()
    {
        Assert.False(JobStatus.IsTerminal(null!));
    }
}
