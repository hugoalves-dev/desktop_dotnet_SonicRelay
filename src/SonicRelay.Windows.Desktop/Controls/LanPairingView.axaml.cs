using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Offline pairing surface (issue #75), driven imperatively by a <see cref="LanPairingViewModel"/>
/// DataContext the same way <see cref="PairingView"/> is driven by its cloud counterpart.
/// </summary>
public partial class LanPairingView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LanPairingViewModel? viewModel;
    private byte[]? renderedQr;

    public LanPairingView()
    {
        InitializeComponent();
        timer.Tick += (_, _) => OnTick();
        DataContextChanged += (_, _) => Attach(DataContext as LanPairingViewModel);
        Loaded += (_, _) =>
        {
            timer.Start();
            Render();
        };
        Unloaded += (_, _) => timer.Stop();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Attach(LanPairingViewModel? value)
    {
        if (ReferenceEquals(viewModel, value)) return;
        if (viewModel is not null) viewModel.StateChanged -= OnStateChanged;
        viewModel = value;
        if (viewModel is not null) viewModel.StateChanged += OnStateChanged;
        Render();
    }

    private void OnStateChanged() => Dispatcher.UIThread.Post(Render);

    private void OnTick()
    {
        viewModel?.Tick(DateTimeOffset.UtcNow);
        RenderExpiry();
    }

    private void Open_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => viewModel?.Open();

    private void Render()
    {
        if (this.FindControl<TextBlock>("HostStatusText") is not { } hostStatus) return;
        var vm = viewModel;
        hostStatus.Text = vm?.HostStatus ?? "Local network pairing is unavailable.";
        this.FindControl<Button>("OpenButton")!.IsEnabled = vm?.IsAvailable == true;
        this.FindControl<TextBlock>("ManualCodeText")!.Text = vm?.ManualCode ?? "—";
        this.FindControl<TextBlock>("EndpointsText")!.Text =
            vm is { Endpoints.Count: > 0 } ? string.Join(Environment.NewLine, vm.Endpoints) : "—";
        RenderQr(vm?.QrCodePng);
        RenderExpiry();
        RenderDevices(vm);
    }

    private void RenderQr(byte[]? png)
    {
        if (ReferenceEquals(png, renderedQr)) return;
        renderedQr = png;
        var image = this.FindControl<Image>("QrImage")!;
        (image.Source as IDisposable)?.Dispose();
        image.Source = png is null ? null : new Bitmap(new MemoryStream(png));
    }

    private void RenderExpiry()
    {
        if (this.FindControl<TextBlock>("ExpiryText") is not { } expiry) return;
        expiry.Text = viewModel?.Window is null
            ? "Open a local pairing code to begin."
            : $"Expires in {viewModel.SecondsRemaining(DateTimeOffset.UtcNow)} s.";
    }

    private void RenderDevices(LanPairingViewModel? vm)
    {
        var panel = this.FindControl<StackPanel>("DevicesPanel")!;
        panel.Children.Clear();
        var devices = vm?.PairedDevices ?? [];
        if (devices.Count == 0)
        {
            panel.Children.Add(new TextBlock { Classes = { "metric-label" }, Text = "No phones paired over the local network." });
            return;
        }

        foreach (var device in devices)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            var seen = device.LastSeenAt is { } at ? $" · last seen {at.ToLocalTime():g}" : "";
            row.Children.Add(new TextBlock
            {
                Text = $"{device.Name} ({device.Platform}){seen}",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            var revoke = new Button { Content = "Revoke", Classes = { "ghost" } };
            var id = device.ViewerDeviceId;
            revoke.Click += async (_, _) =>
            {
                if (viewModel is not null) await viewModel.RevokeAsync(id);
            };
            Grid.SetColumn(revoke, 1);
            row.Children.Add(revoke);
            panel.Children.Add(row);
        }
    }
}
