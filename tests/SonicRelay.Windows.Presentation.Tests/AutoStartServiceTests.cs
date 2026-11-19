using SonicRelay.Windows.Presentation.Platform;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class AutoStartServiceTests
{
    private const string Exe = @"C:\Program Files\SonicRelay\SonicRelay.Windows.Desktop.exe";

    [Fact]
    public async Task RunKeyIsWrittenWithQuotedExeAndMinimizedFlag()
    {
        var registry = new FakeRunKey();
        var service = new WindowsRunKeyAutoStartService(registry, Exe);

        await service.SetEnabledAsync(true);

        Assert.Equal($"\"{Exe}\" --minimized", registry.Values["SonicRelayPublisher"]);
        Assert.True(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task RunKeyIsRemovedWhenDisabled()
    {
        var registry = new FakeRunKey();
        var service = new WindowsRunKeyAutoStartService(registry, Exe);
        await service.SetEnabledAsync(true);

        await service.SetEnabledAsync(false);

        Assert.False(registry.Values.ContainsKey("SonicRelayPublisher"));
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task XdgEntryIsWrittenAndRemoved()
    {
        var configHome = Directory.CreateTempSubdirectory("sonicrelay-xdg-").FullName;
        var service = new LinuxXdgAutoStartService("/opt/Sonic Relay/sonicrelay", configHome);
        var entry = Path.Combine(configHome, "autostart", "sonicrelay-publisher.desktop");

        await service.SetEnabledAsync(true);

        var text = await File.ReadAllTextAsync(entry);
        Assert.Contains("Exec=\"/opt/Sonic Relay/sonicrelay\" --minimized", text);
        Assert.Contains("X-GNOME-Autostart-enabled=true", text);
        Assert.Contains("Type=Application", text);
        Assert.True(await service.IsEnabledAsync());

        await service.SetEnabledAsync(false);

        Assert.False(File.Exists(entry));
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact]
    public void XdgConfigHomeFallsBackToDotConfig()
    {
        Assert.Equal(
            Path.Combine("/home/me", ".config"),
            LinuxXdgAutoStartService.ResolveConfigHome(xdgConfigHome: null, home: "/home/me"));
        Assert.Equal(
            "/custom",
            LinuxXdgAutoStartService.ResolveConfigHome(xdgConfigHome: "/custom", home: "/home/me"));
    }

    [Fact]
    public void FactoryReturnsNullWhereLaunchAtLoginIsNotManagedInApp()
    {
        Assert.Null(AutoStartServiceFactory.Create(Exe, AutoStartPlatform.MacOs));
        Assert.Null(AutoStartServiceFactory.Create(Exe, AutoStartPlatform.WindowsPackaged));
        Assert.IsType<LinuxXdgAutoStartService>(AutoStartServiceFactory.Create(Exe, AutoStartPlatform.Linux));
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? GetValue(string name) => Values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => Values[name] = value;
        public void DeleteValue(string name) => Values.Remove(name);
    }
}
