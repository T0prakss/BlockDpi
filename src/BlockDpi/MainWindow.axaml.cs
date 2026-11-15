using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BlockDpi.Engine;

namespace BlockDpi;

public partial class MainWindow : Window
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    private enum State { Off, Busy, On }

    private AppSettings _settings;
    private readonly IDpiEngine _engine = EngineFactory.Create();
    private State _state = State.Off;
    private bool _exiting;
    private bool _loadingUi;

    public event Action? ExitRequested;

    public MainWindow() : this(false) { }

    public MainWindow(bool autostart)
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        bool mac = !OperatingSystem.IsWindows();
        QuicRow.IsVisible = !mac;
        ReverseRow.IsVisible = !mac;
        FakeRow.IsVisible = !mac;
        FakeTtlRow.IsVisible = !mac;
        MacNote.IsVisible = mac;
        StartWithOsTitle.Text = OperatingSystem.IsWindows() ? "Windows açılınca başlat" : "Mac açılınca başlat";
        FooterText.Text = OperatingSystem.IsWindows() ? "BlockDpi v1.0 · WinDivert" : "BlockDpi v1.0 · Yerel proxy modu";

        DnsSwitch.IsCheckedChanged += (_, _) => DnsFields.IsEnabled = DnsSwitch.IsChecked == true;
        FakeSwitch.IsCheckedChanged += (_, _) => FakeTtlRow.IsEnabled = FakeSwitch.IsChecked == true;
        BlacklistSwitch.IsCheckedChanged += (_, _) => BlacklistBox.IsEnabled = BlacklistSwitch.IsChecked == true;

        LoadSettingsToUi(_settings);
        UpdateInfo();
        SetState(State.Off);

        if (OperatingSystem.IsWindows() && !WinDivertNative.FilesPresent())
            ShowError("WinDivert dosyaları bulunamadı. Aşağıdaki butonla indirebilirsin " +
                      "(Windows Defender engellerse BlockDpi klasörünü Dışlamalar'a ekle).", showInstall: true);

        Closing += OnClosing;
        Opened += (_, _) => { Pulse.Classes.Set("on", _state == State.On); };

        if (!File.Exists(AppSettings.FilePath))
        {
            try { _settings.Save(); } catch { }
        }

        Dispatcher.UIThread.Post(() => _ = StartAsync(), DispatcherPriority.Background);
    }

    public new void Show()
    {
        base.Show();
        Pulse.Classes.Set("on", _state == State.On);
    }

    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            if (OperatingSystem.IsWindows())
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    public void Toggle()
    {
        if (_state == State.Busy) return;
        _ = _state == State.On ? StopAsync() : StartAsync();
    }

    private void OnPowerClick(object? sender, RoutedEventArgs e) => Toggle();

    private async Task StartAsync()
    {
        if (_state != State.Off) return;
        HideError();
        SetState(State.Busy);
        var s = _settings.Clone();
        try
        {
            await Task.Run(() => _engine.Start(s));
            SetState(State.On);
            TrimMemory();
        }
        catch (EngineException ex)
        {
            SetState(State.Off);
            ShowError(ex.Message, ex.WinDivertMissing);
        }
        catch (Exception ex)
        {
            SetState(State.Off);
            ShowError("Başlatılamadı: " + ex.Message);
        }
    }

    private async Task StopAsync()
    {
        if (_state != State.On) return;
        SetState(State.Busy);
        try { await Task.Run(_engine.Stop); } catch { }
        SetState(State.Off);
    }

    public void ShutdownEngine()
    {
        try { _engine.Stop(); } catch { }
    }

    private void SetState(State st)
    {
        _state = st;
        bool on = st == State.On;
        PowerCircle.Classes.Set("on", on);
        PowerIcon.Classes.Set("on", on);
        Pulse.Classes.Set("on", on && IsVisible);
        Pulse.Classes.Set("busy", st == State.Busy && IsVisible);
        StatusPill.Classes.Set("on", on);
        StatusDot.Classes.Set("on", on);

        (StatusText.Text, StatusSub.Text) = st switch
        {
            State.On => ("Aktif", "DPI atlatma çalışıyor · kapatmak için dokun"),
            State.Busy => ("Bekleyin...", "İşlem yapılıyor"),
            _ => ("Bağlı Değil", "Açmak için butona dokun")
        };
        PowerButton.IsEnabled = st != State.Busy;

        if (Application.Current is App app)
            app.SetTrayText(on ? "BlockDpi · Aktif" : "BlockDpi · Kapalı");
    }

    private void UpdateInfo()
    {
        InfoDns.Text = !_settings.DnsRedirect ? "Kapalı"
            : _settings.UseDoh ? "DoH 1.1.1.1"
            : _settings.DnsV4.StartsWith("77.88.") ? "Yandex"
            : _settings.DnsV4.StartsWith("94.140.") ? "AdGuard" : "Özel";
        InfoScope.Text = _settings.UseBlacklist ? $"{_settings.Blacklist.Count} site" : "Tüm siteler";
    }

    private void ShowError(string msg, bool showInstall = false)
    {
        ErrorText.Text = msg;
        InstallWinDivertButton.IsVisible = showInstall && OperatingSystem.IsWindows();
        ErrorBanner.IsVisible = true;
    }

    private void HideError() => ErrorBanner.IsVisible = false;

    private async void OnInstallWinDivert(object? sender, RoutedEventArgs e)
    {
        InstallWinDivertButton.IsEnabled = false;
        InstallWinDivertButton.Content = "İndiriliyor...";
        try
        {
            await WinDivertInstaller.InstallAsync();
            HideError();
            StatusSub.Text = "WinDivert kuruldu · açmak için dokun";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, showInstall: true);
        }
        finally
        {
            InstallWinDivertButton.IsEnabled = true;
            InstallWinDivertButton.Content = "WinDivert'i indir ve kur";
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        bool toSettings = !SettingsView.IsVisible;
        if (toSettings) LoadSettingsToUi(_settings);
        SettingsView.IsVisible = toSettings;
        HomeView.IsVisible = !toSettings;
        HeaderTitle.Text = toSettings ? "Ayarlar" : "BlockDpi";
        HeaderSub.Text = toSettings ? "Değişiklikler kaydedince uygulanır" : "DPI atlatıcı · VPN değil";
        SettingsIcon.Data = Avalonia.Media.Geometry.Parse(toSettings
            ? "M20,11V13H8L13.5,18.5L12.08,19.92L4.16,12L12.08,4.08L13.5,5.5L8,11H20Z"
            : "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z");
    }

    private static readonly (string Ip, int Port)[] DnsPresets =
    [
        ("77.88.8.8", 1253), ("77.88.8.8", 1253), ("77.88.8.1", 1253), ("94.140.14.14", 5353)
    ];

    private void OnDnsPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || DnsPreset.SelectedIndex < 0 || DnsPreset.SelectedIndex >= DnsPresets.Length) return;
        var (ip, port) = DnsPresets[DnsPreset.SelectedIndex];
        DnsV4Box.Text = ip;
        DnsV4PortBox.Value = port;
        if (ip.StartsWith("77.88.")) { DnsV6Box.Text = "2a02:6b8::feed:0ff"; DnsV6PortBox.Value = 1253; }
        else { DnsV6Box.Text = "2a10:50c0::ad1:ff"; DnsV6PortBox.Value = 5353; }
    }

    private void LoadSettingsToUi(AppSettings s)
    {
        _loadingUi = true;
        StartWithOsSwitch.IsChecked = s.StartWithOs;
        TraySwitch.IsChecked = s.MinimizeToTray;
        DnsSwitch.IsChecked = s.DnsRedirect;
        DnsV4Box.Text = s.DnsV4;
        DnsV4PortBox.Value = s.DnsV4Port;
        DnsV6Box.Text = s.DnsV6;
        DnsV6PortBox.Value = s.DnsV6Port;
        if (s.UseDoh)
        {
            DnsPreset.SelectedIndex = 0;
        }
        else
        {
            int preset = Array.FindIndex(DnsPresets, 1, DnsPresets.Length - 1, p => p.Ip == s.DnsV4 && p.Port == s.DnsV4Port);
            DnsPreset.SelectedIndex = preset >= 1 ? preset : 4;
        }
        HttpsFragSwitch.IsChecked = s.HttpsFragment;
        SniSplitSwitch.IsChecked = s.SplitAtSni;
        FragSizeBox.Value = s.FragmentSize;
        ReverseSwitch.IsChecked = s.ReverseFragments;
        HttpFragSwitch.IsChecked = s.HttpFragment;
        HostTrickSwitch.IsChecked = s.HttpHostTrick;
        QuicSwitch.IsChecked = s.BlockQuic;
        FakeSwitch.IsChecked = s.FakePacket;
        FakeTtlBox.Value = s.FakeTtl;
        BlacklistSwitch.IsChecked = s.UseBlacklist;
        BlacklistBox.Text = string.Join(Environment.NewLine, s.Blacklist);
        DnsFields.IsEnabled = s.DnsRedirect;
        FakeTtlRow.IsEnabled = s.FakePacket;
        BlacklistBox.IsEnabled = s.UseBlacklist;
        _loadingUi = false;
    }

    private AppSettings ReadUi()
    {
        return new AppSettings
        {
            StartWithOs = StartWithOsSwitch.IsChecked == true,
            MinimizeToTray = TraySwitch.IsChecked == true,
            DnsRedirect = DnsSwitch.IsChecked == true,
            UseDoh = DnsPreset.SelectedIndex <= 0,
            DnsV4 = (DnsV4Box.Text ?? "").Trim(),
            DnsV4Port = (int)(DnsV4PortBox.Value ?? 1253),
            DnsV6 = (DnsV6Box.Text ?? "").Trim(),
            DnsV6Port = (int)(DnsV6PortBox.Value ?? 1253),
            HttpsFragment = HttpsFragSwitch.IsChecked == true,
            SplitAtSni = SniSplitSwitch.IsChecked == true,
            FragmentSize = (int)(FragSizeBox.Value ?? 2),
            ReverseFragments = ReverseSwitch.IsChecked == true,
            HttpFragment = HttpFragSwitch.IsChecked == true,
            HttpHostTrick = HostTrickSwitch.IsChecked == true,
            BlockQuic = QuicSwitch.IsChecked == true,
            FakePacket = FakeSwitch.IsChecked == true,
            FakeTtl = (int)(FakeTtlBox.Value ?? 5),
            UseBlacklist = BlacklistSwitch.IsChecked == true,
            Blacklist = (BlacklistBox.Text ?? "")
                .Split(['\r', '\n', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var s = ReadUi();
        if (s.DnsRedirect && !System.Net.IPAddress.TryParse(s.DnsV4, out _))
        {
            HeaderSub.Text = "⚠ Geçersiz IPv4 DNS adresi";
            return;
        }
        if (s.DnsV6.Length > 0 && !System.Net.IPAddress.TryParse(s.DnsV6, out _))
        {
            HeaderSub.Text = "⚠ Geçersiz IPv6 DNS adresi";
            return;
        }

        bool autostartChanged = s.StartWithOs != _settings.StartWithOs;
        _settings = s;
        try { _settings.Save(); } catch { }
        if (autostartChanged) await Task.Run(() => Autostart.Apply(s.StartWithOs));
        UpdateInfo();

        OnSettingsClick(null, new RoutedEventArgs());

        if (_state == State.On)
        {
            await StopAsync();
            await StartAsync();
        }
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        var d = new AppSettings { StartWithOs = _settings.StartWithOs };
        LoadSettingsToUi(d);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting) return;
        if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            Pulse.Classes.Set("on", false);
            Pulse.Classes.Set("busy", false);
            Hide();
            TrimMemory();
            return;
        }
        _exiting = true;
        e.Cancel = true;
        ExitRequested?.Invoke();
    }
}

