using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation.Platform;

namespace SonicRelay.Windows.Desktop.Tests;

public sealed class StartupSettingsViewModelTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonic-startup-vm-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Launch_at_login_reflects_and_updates_the_service()
    {
        var service = new FakeAutoStart();
        var vm = new StartupSettingsViewModel(service, AutoStartPlatform.WindowsUnpackaged, Store());
        await vm.LoadAsync();
        Assert.False(vm.LaunchAtLogin);

        await vm.SetLaunchAtLoginAsync(true);

        Assert.True(service.Enabled);
        Assert.True(vm.LaunchAtLogin);
        Assert.True(vm.CanToggleLaunchAtLogin);
        Assert.False(vm.ShowStoreStartupHint);
    }

    [Fact]
    public async Task Existing_registration_is_shown_as_on()
    {
        var vm = new StartupSettingsViewModel(new FakeAutoStart { Enabled = true }, AutoStartPlatform.Linux, Store());

        await vm.LoadAsync();

        Assert.True(vm.LaunchAtLogin);
    }

    [Fact]
    public void Store_install_shows_the_windows_settings_hint_instead_of_a_toggle()
    {
        var vm = new StartupSettingsViewModel(null, AutoStartPlatform.WindowsPackaged, Store());

        Assert.False(vm.CanToggleLaunchAtLogin);
        Assert.True(vm.ShowStoreStartupHint);
        Assert.Equal("Turn this on in Windows Settings → Apps → Startup.", StartupSettingsViewModel.StoreStartupHint);
    }

    [Fact]
    public void Unsupported_platform_hides_launch_at_login()
    {
        var vm = new StartupSettingsViewModel(null, AutoStartPlatform.MacOs, Store());

        Assert.False(vm.CanToggleLaunchAtLogin);
        Assert.False(vm.ShowStoreStartupHint);
    }

    [Fact]
    public async Task Start_streaming_on_launch_is_off_by_default_and_persists()
    {
        var vm = new StartupSettingsViewModel(null, AutoStartPlatform.Linux, Store());
        Assert.False(vm.StartSessionOnLaunch);

        await vm.SetStartSessionOnLaunchAsync(true);

        Assert.True(vm.StartSessionOnLaunch);
        Assert.True(Store().StartSessionOnLaunch);
    }

    [Theory]
    [InlineData(new[] { "--minimized" }, true)]
    [InlineData(new[] { "--MINIMIZED" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--other" }, false)]
    public void Minimized_argument_is_recognised(string[] args, bool expected)
    {
        Assert.Equal(expected, StartupArguments.RequestsMinimized(args));
    }

    private PublisherStartupPreferenceStore Store() => new(path);

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class FakeAutoStart : IAutoStartService
    {
        public bool Enabled { get; set; }
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Enabled = enabled;
            return Task.CompletedTask;
        }
    }
}
