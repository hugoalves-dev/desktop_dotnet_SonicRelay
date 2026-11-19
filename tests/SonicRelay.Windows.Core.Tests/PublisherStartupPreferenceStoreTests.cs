using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Core.Tests;

public sealed class PublisherStartupPreferenceStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonic-startup-{Guid.NewGuid():N}.json");

    [Fact]
    public void Start_session_on_launch_is_off_by_default()
    {
        Assert.False(new PublisherStartupPreferenceStore(path).StartSessionOnLaunch);
    }

    [Fact]
    public async Task Round_trips_start_session_on_launch()
    {
        await new PublisherStartupPreferenceStore(path).SetStartSessionOnLaunchAsync(true);

        Assert.True(new PublisherStartupPreferenceStore(path).StartSessionOnLaunch);
    }

    [Fact]
    public async Task Corrupt_file_reads_as_off()
    {
        await File.WriteAllTextAsync(path, "not json {{");

        Assert.False(new PublisherStartupPreferenceStore(path).StartSessionOnLaunch);
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
