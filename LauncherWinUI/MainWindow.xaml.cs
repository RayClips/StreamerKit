using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace StreamerKit;

public sealed partial class MainWindow : Window
{
    private readonly List<ServerProcess> _servers = new();
    private readonly List<HostedServer> _hosted = new();
    private readonly ObservableCollection<WebSocketHostedServer> _customServers = new();
    private readonly ObservableCollection<WebSocketClientConnection> _customClients = new();
    private readonly TwitchChat _twitch = new();
    private readonly KickChat _kick = new();
    private readonly ObsClient _obs;
    private readonly StreamlabsClient _streamlabs;
    private readonly DiscordClient _discord;

    // These must be held in fields. A DispatcherQueueTimer does not root itself the way
    // WPF's DispatcherTimer does, so a local would be collected and simply stop ticking.
    private readonly DispatcherQueueTimer _pump;
    private DispatcherQueueTimer? _flash;

    private readonly Settings _settings = Settings.Load();
    private bool _loaded;   // suppresses control events fired while restoring saved values

    public MainWindow()
    {
        InitializeComponent();
        Resize(1400, 784);

        // Make the title bar part of the app surface rather than a strip on top of it.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyIcon();

        var configPath = Path.Combine(AppContext.BaseDirectory, "servers.json");
        try
        {
            foreach (var config in ServerConfig.Load(configPath))
                _servers.Add(new ServerProcess(config));
        }
        catch (Exception ex)
        {
            ShowConfigError(ex.Message);
        }

        _hosted.Add(new HttpHostedServer(_settings.Http));
        _hosted.Add(new WebSocketHostedServer(_settings.WebSocket));
        _hosted.Add(new UdpHostedServer(_settings.Udp));
        foreach (var server in _hosted) server.ConfigChanged += SaveHostedSettings;

        foreach (var config in _settings.CustomServers) AddCustomServer(config);
        foreach (var config in _settings.CustomClients) AddCustomClient(config);
        CustomServerList.ItemsSource = _customServers;
        CustomClientList.ItemsSource = _customClients;
        UpdateCustomEmptyStates();

        ServerList.ItemsSource = _servers;
        HostedList.ItemsSource = _hosted;
        AutoStartList.ItemsSource = _servers;
        PluginList.ItemsSource = PluginItem.LoadCatalog(Path.Combine(AppContext.BaseDirectory, "plugins.json"));
        IntegrationList.ItemsSource = IntegrationItem.Load(Path.Combine(AppContext.BaseDirectory, "integrations.json"));
        PlatformList.ItemsSource = IntegrationItem.Load(Path.Combine(AppContext.BaseDirectory, "platforms.json"));

        // Twitch chat feeds the WebSocket server, which is what overlay widgets listen to.
        _twitch.Channel = _settings.TwitchChannel;
        _twitch.AutoConnect = _settings.TwitchAutoConnect;
        _twitch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(TwitchChat.Channel) or nameof(TwitchChat.AutoConnect))) return;
            _settings.TwitchChannel = _twitch.Channel;
            _settings.TwitchAutoConnect = _twitch.AutoConnect;
            _settings.Save();
        };
        _twitch.Logged += line => WebSocket?.LogExternal($"Twitch: {line}");
        _twitch.ChatMessage += async data =>
        {
            DispatchChat("Twitch", data);
            if (WebSocket is { } server) await server.BroadcastEvent("Twitch", "ChatMessage", data);
        };

        _obs = new ObsClient(_settings.Obs);
        _obs.ConfigChanged += () => { _obs.CopyTo(_settings.Obs); _settings.Save(); };
        ObsDetail.DataContext = _obs;

        _streamlabs = new StreamlabsClient(_settings.Streamlabs);
        _streamlabs.ConfigChanged += () => { _streamlabs.CopyTo(_settings.Streamlabs); _settings.Save(); };
        StreamlabsDetail.DataContext = _streamlabs;

        _discord = new DiscordClient(_settings.Discord);
        _discord.ConfigChanged += () => { _discord.CopyTo(_settings.Discord); _settings.Save(); };
        DiscordDetail.DataContext = _discord;
        _discord.ChatMessage += async data =>
        {
            DispatchChat("Discord", data);
            if (WebSocket is { } server) await server.BroadcastEvent("Discord", "ChatMessage", data);
        };

        _kick.Channel = _settings.KickChannel;
        _kick.AutoConnect = _settings.KickAutoConnect;
        _kick.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(KickChat.Channel) or nameof(KickChat.AutoConnect))) return;
            _settings.KickChannel = _kick.Channel;
            _settings.KickAutoConnect = _kick.AutoConnect;
            _settings.Save();
        };
        _kick.Logged += line => WebSocket?.LogExternal($"Kick: {line}");
        _kick.ChatMessage += async data =>
        {
            DispatchChat("Kick", data);
            if (WebSocket is { } server) await server.BroadcastEvent("Kick", "ChatMessage", data);
        };

        // After the servers and the OBS client exist: the engine reaches all of them.
        InitializeActions();

        ApplySettings();
        Greeting.Text = $"Hello, {FriendlyUserName()}!";
        FolderPath.Text = AppContext.BaseDirectory.TrimEnd('\\');
        UpdateSummary();

        // One timer drains every server's output buffer, so a chatty server can't thrash the UI thread.
        _pump = DispatcherQueue.CreateTimer();
        _pump.Interval = TimeSpan.FromMilliseconds(200);
        _pump.Tick += (_, _) =>
        {
            foreach (var server in _servers) server.FlushLog();
            foreach (var server in _hosted) server.FlushLog();
            foreach (var server in _customServers) server.FlushLog();
            foreach (var client in _customClients) client.FlushLog();
            _obs.FlushLog();
            _streamlabs.FlushLog();
            _discord.FlushLog();
            _actions.Tick();          // timer triggers ride the same pump as the logs
            _actions.FlushLogs();
            UpdateSummary();
            if (SettingsPanel.Visibility == Visibility.Visible) UpdateCrashLogLine();
        };
        _pump.Start();

        foreach (var server in _servers.Where(s => s.AutoStart))
            server.Start();
        foreach (var server in _hosted.Where(s => s.AutoStart))
            server.Start();
        foreach (var server in _customServers.Where(s => s.AutoStart))
            server.Start();
        foreach (var client in _customClients.Where(c => c.AutoStart))
            client.Connect();

        ConnectAutomatically();

        Closed += (_, _) =>
        {
            _twitch.Disconnect();
            _kick.Disconnect();
            foreach (var server in _customServers) server.Stop();
            foreach (var client in _customClients) client.Disconnect();
            _obs.Disconnect();
            _streamlabs.Disconnect();
            _discord.Disconnect();
            foreach (var server in _servers) server.StopAndWait();
            foreach (var server in _hosted) server.Stop();
        };
    }

    /// <summary>
    /// icon.ico drives alt-tab and the taskbar; icon.png is the crisp source for the
    /// in-app title bar. Both are optional - a missing file just leaves the default.
    /// </summary>
    private void ApplyIcon()
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        var png = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (!File.Exists(png)) return;

        AppIcon.Source = new BitmapImage(new Uri(png));
        HomeLogo.Source = new BitmapImage(new Uri(png));
    }

    /// <summary>
    /// Opens at the preferred size, centred on the work area.
    ///
    /// The size is clamped to the screen it actually lands on, leaving a margin: the numbers
    /// passed in are what we want on a large monitor, not something a 1366x768 laptop can
    /// honour. Without this a taller default opens with its bottom edge under the taskbar.
    /// </summary>
    private void Resize(int width, int height)
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        width = Math.Min(width, area.WorkArea.Width - 60);
        height = Math.Min(height, area.WorkArea.Height - 60);

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + (area.WorkArea.Width - width) / 2,
            area.WorkArea.Y + (area.WorkArea.Height - height) / 2));
    }

    private async void ShowConfigError(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Could not read servers.json",
            Content = message,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    /// <summary>Windows account name, tidied up for a greeting: "yusif.k" -> "Yusif".</summary>
    private static string FriendlyUserName()
    {
        var name = Environment.UserName.Split('.', '_', '-', ' ').FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return "there";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private void UpdateSummary()
    {
        var running = _servers.Count(s => s.State == ServerState.Running)
                    + _hosted.Count(s => s.State == ServerState.Running);
        var total = _servers.Count + _hosted.Count;

        Summary.Text = $"{total} servers · {running} running";
        HomeSummary.Text = running == 0
            ? $"{total} servers configured · none running right now."
            : $"{running} of {total} servers running.";

        RunningBadge.Value = running;
        RunningBadge.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";

        // The three child items under Servers each open the same detail page, bound to
        // whichever hosted server was picked.
        var detail = page switch
        {
            "http" => _hosted.OfType<HttpHostedServer>().FirstOrDefault() as HostedServer,
            "websocket" => _hosted.OfType<WebSocketHostedServer>().FirstOrDefault(),
            "udp" => _hosted.OfType<UdpHostedServer>().FirstOrDefault(),
            _ => null
        };
        if (detail is not null) DetailPanel.DataContext = detail;

        // "Servers" shows both groups; the App Servers child narrows it to just those.
        var appServersOnly = page == "appservers";
        ServersTitle.Text = appServersOnly ? "App servers" : "Servers";
        ServersSubtitle.Text = appServersOnly
            ? "Programs on this PC that StreamerKit launches and watches."
            : "Start, stop and watch the output of each local server.";
        // The page title already says "App servers" when narrowed, so drop the group heading.
        AppServersHeader.Visibility = appServersOnly ? Visibility.Collapsed : Visibility.Visible;
        HostedServersSection.Visibility = appServersOnly ? Visibility.Collapsed : Visibility.Visible;

        // Leaving a section always returns to its list, never a stale detail page.
        if (page != "platforms") PlatformDetailPanel.Visibility = Visibility.Collapsed;
        else ShowPlatformList();

        if (page != "integrations") IntegrationDetailPanel.Visibility = Visibility.Collapsed;
        else ShowIntegrationList();

        if (page != "actions") ActionDetailPanel.Visibility = Visibility.Collapsed;
        else ShowActionList();

        HomePanel.Visibility = Show(page, "home");
        ServersPanel.Visibility = page is "servers" or "appservers" ? Visibility.Visible : Visibility.Collapsed;
        PluginsPanel.Visibility = Show(page, "plugins");
        IntegrationsPanel.Visibility = Show(page, "integrations");
        PlatformsPanel.Visibility = Show(page, "platforms");
        ActionsPanel.Visibility = Show(page, "actions");
        AboutPanel.Visibility = Show(page, "about");
        CustomPanel.Visibility = Show(page, "custom");
        SettingsPanel.Visibility = Show(page, "settings");
        DetailPanel.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;

        static Visibility Show(string page, string name)
            => page == name ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Custom servers and clients ----------------

    private void AddCustomServer(Settings.HostedConfig config)
    {
        var server = new WebSocketHostedServer(config);
        server.ConfigChanged += SaveCustomSettings;
        _customServers.Add(server);
    }

    private void AddCustomClient(Settings.ClientConfig config)
    {
        var client = new WebSocketClientConnection(config);
        client.ConfigChanged += SaveCustomSettings;
        _customClients.Add(client);
    }

    private void OnAddCustomServer(object sender, RoutedEventArgs e)
    {
        // Offset the port so a new server doesn't collide with one already listening.
        var port = 8090 + _customServers.Count;
        AddCustomServer(new Settings.HostedConfig
        {
            Name = $"WebSocket Server {_customServers.Count + 1}",
            Address = "127.0.0.1",
            Port = port,
            Endpoint = "/"
        });
        SaveCustomSettings();
        UpdateCustomEmptyStates();
    }

    private void OnAddCustomClient(object sender, RoutedEventArgs e)
    {
        AddCustomClient(new Settings.ClientConfig
        {
            Name = $"WebSocket Client {_customClients.Count + 1}",
            Endpoint = "ws://127.0.0.1:8080/",
            RetryInterval = 5
        });
        SaveCustomSettings();
        UpdateCustomEmptyStates();
    }

    private void OnRemoveCustomServer(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WebSocketHostedServer server) return;

        server.Stop();                 // never leave a port held by something that's gone
        _customServers.Remove(server);
        SaveCustomSettings();
        UpdateCustomEmptyStates();
    }

    private void OnRemoveCustomClient(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not WebSocketClientConnection client) return;

        client.Disconnect();
        _customClients.Remove(client);
        SaveCustomSettings();
        UpdateCustomEmptyStates();
    }

    private void OnToggleCustomClient(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is WebSocketClientConnection client) client.Toggle();
    }

    private void UpdateCustomEmptyStates()
    {
        NoCustomServers.Visibility = _customServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCustomClients.Visibility = _customClients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveCustomSettings()
    {
        _settings.CustomServers = _customServers.Select(s =>
        {
            var config = new Settings.HostedConfig();
            s.CopyTo(config);
            return config;
        }).ToList();

        _settings.CustomClients = _customClients.Select(c =>
        {
            var config = new Settings.ClientConfig();
            c.CopyTo(config);
            return config;
        }).ToList();

        _settings.Save();
    }

    private void SaveHostedSettings()
    {
        foreach (var server in _hosted)
        {
            switch (server)
            {
                case HttpHostedServer: server.CopyTo(_settings.Http); break;
                case WebSocketHostedServer: server.CopyTo(_settings.WebSocket); break;
                case UdpHostedServer: server.CopyTo(_settings.Udp); break;
            }
        }
        _settings.Save();
    }

    private async void OnSendTestMessage(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is WebSocketHostedServer server)
            await server.SendTestMessage();
    }

    private WebSocketHostedServer? WebSocket => _hosted.OfType<WebSocketHostedServer>().FirstOrDefault();

    /// <summary>Platforms and Integrations share the row template; only one list is on screen.</summary>
    private void OnOpenRow(object sender, RoutedEventArgs e)
    {
        if (IntegrationsPanel.Visibility == Visibility.Visible) OnOpenIntegration(sender, e);
        else OnOpenPlatform(sender, e);
    }

    /// <summary>Opens one platform's page. Only Twitch and Kick have anything to configure.</summary>
    private void OnOpenPlatform(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IntegrationItem platform) return;

        PlatformDetailPanel.DataContext = platform;

        // The chat panel drives whichever reader this platform uses.
        ChatDetail.DataContext = platform.Name switch
        {
            "Twitch" => _twitch,
            "Kick" => _kick,
            _ => null
        };

        ChatDetail.Visibility = platform.Available ? Visibility.Visible : Visibility.Collapsed;
        PlatformUnavailable.Visibility = platform.Available ? Visibility.Collapsed : Visibility.Visible;
        PlatformUnavailable.Message = platform.Requirement;

        PlatformsPanel.Visibility = Visibility.Collapsed;
        PlatformDetailPanel.Visibility = Visibility.Visible;
    }

    private void OnBackToPlatforms(object sender, RoutedEventArgs e) => ShowPlatformList();

    // ---------------- Integrations ----------------

    private void OnOpenIntegration(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IntegrationItem integration) return;

        IntegrationDetailPanel.DataContext = integration;

        // Each connected integration has its own panel; the rest just explain themselves.
        ObsDetail.Visibility = Show(integration, "OBS Studio");
        StreamlabsDetail.Visibility = Show(integration, "Streamlabs");
        DiscordDetail.Visibility = Show(integration, "Discord");
        IntegrationUnavailable.Visibility = integration.Available ? Visibility.Collapsed : Visibility.Visible;
        IntegrationUnavailable.Message = integration.Requirement;

        IntegrationsPanel.Visibility = Visibility.Collapsed;
        IntegrationDetailPanel.Visibility = Visibility.Visible;
    }

    private static Visibility Show(IntegrationItem integration, string name)
        => integration.Available && integration.Name == name ? Visibility.Visible : Visibility.Collapsed;

    private void OnBackToIntegrations(object sender, RoutedEventArgs e) => ShowIntegrationList();

    private void ShowIntegrationList()
    {
        IntegrationDetailPanel.Visibility = Visibility.Collapsed;
        IntegrationsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Connects whatever the user asked to have connected on launch.
    ///
    /// One attempt each, deliberately. OBS and Streamlabs are frequently still starting when
    /// StreamerKit is already up — especially when it launches with Windows — so a failure
    /// here is common and expected; each one says why in its own log and the Connect button
    /// is right there. A retry loop would be the obvious next step, but it would also hide a
    /// genuinely wrong password or a rejected token behind "still trying", which is the one
    /// thing these panels have been careful not to do.
    /// </summary>
    private void ConnectAutomatically()
    {
        // Chat with nowhere to send it is useless, so it brings the WebSocket server up the
        // same way clicking Connect does. A source with no channel has nothing to connect to,
        // and failing on launch over that would be noise rather than news.
        foreach (var chat in new IChatSource[] { _twitch, _kick })
        {
            if (!chat.AutoConnect || chat.Channel.Length == 0) continue;

            if (WebSocket is { State: ServerState.Stopped or ServerState.Failed } server) server.Start();
            chat.Toggle();
        }

        if (_obs.AutoConnect) _obs.Connect();
        if (_streamlabs.AutoConnect) _streamlabs.Connect();
        if (_discord.AutoConnect) _discord.Connect();
    }

    private void OnToggleObs(object sender, RoutedEventArgs e) => _obs.Toggle();

    private async void OnObsProbe(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string requestType)
            await _obs.Request(requestType);
    }

    private async void OnObsSendRequest(object sender, RoutedEventArgs e)
    {
        var requestType = ObsRequestBox.Text.Trim();
        if (requestType.Length > 0) await _obs.Request(requestType);
    }

    private void OnObsClearLog(object sender, RoutedEventArgs e) => _obs.ClearLog();

    // ---------------- Streamlabs ----------------

    private void OnToggleStreamlabs(object sender, RoutedEventArgs e) => _streamlabs.Toggle();

    private async void OnStreamlabsProbe(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string call)
            await CallStreamlabs(call);
    }

    private async void OnStreamlabsSendRequest(object sender, RoutedEventArgs e)
        => await CallStreamlabs(StreamlabsRequestBox.Text.Trim());

    /// <summary>Splits "ScenesService.getScenes" into the resource and the method on it.</summary>
    private async Task CallStreamlabs(string call)
    {
        var dot = call.LastIndexOf('.');
        if (dot <= 0 || dot == call.Length - 1) return;

        await _streamlabs.Call(call[..dot], call[(dot + 1)..]);
    }

    private void OnStreamlabsClearLog(object sender, RoutedEventArgs e) => _streamlabs.ClearLog();

    // ---------------- Discord ----------------

    private void OnToggleDiscord(object sender, RoutedEventArgs e) => _discord.Toggle();

    private void OnCopyDiscordInvite(object sender, RoutedEventArgs e)
    {
        if (!_discord.HasInvite) return;
        CopyToClipboard(_discord.InviteUrl);
    }

    private async void OnDiscordProbe(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string path)
            await _discord.Send(HttpMethod.Get, path);
    }

    private async void OnDiscordSendMessage(object sender, RoutedEventArgs e)
    {
        var text = DiscordMessageBox.Text.Trim();
        if (text.Length == 0) return;

        await _discord.SendMessage(DiscordChannelBox.Text.Trim(), text);
    }

    private void OnDiscordClearLog(object sender, RoutedEventArgs e) => _discord.ClearLog();

    private void ShowPlatformList()
    {
        PlatformDetailPanel.Visibility = Visibility.Collapsed;
        PlatformsPanel.Visibility = Visibility.Visible;
    }

    private void OnToggleChat(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IChatSource source) return;

        // Chat is useless without somewhere to send it, so bring the socket up too.
        if (source.State is ServerState.Stopped or ServerState.Failed &&
            WebSocket is { State: ServerState.Stopped or ServerState.Failed } server)
            server.Start();

        source.Toggle();
    }

    private void OnGenerateToken(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is HostedServer server)
            server.Token = Guid.NewGuid().ToString("N")[..16];
    }

    // ---------------- Settings ----------------

    private void ApplySettings()
    {
        ApplyTheme(_settings.Theme);
        SelectByTag(ThemeBox, _settings.Theme);
        SelectByTag(StartPageBox, _settings.StartPage);
        StartWithWindows.IsOn = Settings.StartsWithWindows();

        AutoRestartSwitch.IsOn = _settings.AutoRestart;
        foreach (var server in _servers) server.AutoRestart = _settings.AutoRestart;
        UpdateCrashLogLine();

        AboutText.Text = $"v0.1.0 (alpha) · WinUI 3 · .NET {Environment.Version}\n"
                       + AppContext.BaseDirectory.TrimEnd('\\');

        AboutVersion.Text = "Version 0.1.0 · alpha";
        AboutBuild.Text = $"WinUI 3 (Windows App SDK) · .NET {Environment.Version} · "
                        + $"{Environment.OSVersion.VersionString}\n"
                        + AppContext.BaseDirectory.TrimEnd('\\');

        var png = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(png)) AboutIcon.Source = new BitmapImage(new Uri(png));

        if (_settings.StartPage != "home") SelectNav(_settings.StartPage);
        _loaded = true;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>()
                              .FirstOrDefault(i => (i.Tag as string) == tag) ?? box.Items.FirstOrDefault();
    }

    private void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || (ThemeBox.SelectedItem as ComboBoxItem)?.Tag is not string theme) return;
        _settings.Theme = theme;
        _settings.Save();
        ApplyTheme(theme);
    }

    private void OnStartPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || (StartPageBox.SelectedItem as ComboBoxItem)?.Tag is not string page) return;
        _settings.StartPage = page;
        _settings.Save();
    }

    private async void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        var wanted = StartWithWindows.IsOn;
        try
        {
            Settings.SetStartWithWindows(wanted);
        }
        catch (Exception ex)
        {
            StartWithWindows.IsOn = !wanted;   // put the switch back where it was
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Could not change startup setting",
                Content = ex.Message,
                CloseButtonText = "OK"
            }.ShowAsync();
        }
    }

    private void OnAutoRestartToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _settings.AutoRestart = AutoRestartSwitch.IsOn;
        _settings.Save();
        foreach (var server in _servers) server.AutoRestart = _settings.AutoRestart;
    }

    private static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "crashes.log");

    private void UpdateCrashLogLine()
    {
        if (!File.Exists(CrashLogPath))
        {
            CrashLogText.Text = "No crashes recorded.";
            return;
        }

        var info = new FileInfo(CrashLogPath);
        var crashes = File.ReadLines(CrashLogPath).Count(l => l.StartsWith('['));
        CrashLogText.Text = $"{crashes} recorded · last written {info.LastWriteTime:d MMM HH:mm}";
    }

    private void OnOpenCrashLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(CrashLogPath))
            Process.Start(new ProcessStartInfo(CrashLogPath) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppContext.BaseDirectory.TrimEnd('\\')}\""));
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not ToggleSwitch toggle) return;
        if (toggle.DataContext is not ServerProcess server) return;
        if (server.AutoStart == toggle.IsOn) return;

        server.AutoStart = toggle.IsOn;
        ServerConfig.Save(Path.Combine(AppContext.BaseDirectory, "servers.json"),
                          _servers.Select(s => s.Config));
    }

    private void OnInstallPlugin(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PluginItem plugin) plugin.Install();
    }

    private void OnRemovePlugin(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PluginItem plugin) plugin.Remove();
    }

    /// <summary>The Home tiles all just jump to the page named in their Tag.</summary>
    private void OnGoToPage(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string tag) SelectNav(tag);
    }

    private void SelectNav(string tag)
        => Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>()
                                 .FirstOrDefault(i => (i.Tag as string) == tag);

    private void OnToggleServer(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is IServerCard server) server.Toggle();
    }

    private void OnStartAll(object sender, RoutedEventArgs e)
    {
        foreach (var server in _servers.Where(s => s.State is ServerState.Stopped or ServerState.Failed))
            server.Start();
        foreach (var server in _hosted.Where(s => s.State is ServerState.Stopped or ServerState.Failed))
            server.Start();

        SelectNav("servers");   // started from Home? show what just happened
    }

    private void OnStopAll(object sender, RoutedEventArgs e)
    {
        foreach (var server in _servers) server.Stop();
        foreach (var server in _hosted) server.Stop();
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is IServerCard { Url: { } url })
            CopyToClipboard(url);
    }

    private void OnOpenUrl(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IServerCard { Url: { } url }) return;

        // udp:// has no handler, and ws:// usually doesn't either - not worth an error dialog.
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppContext.BaseDirectory.TrimEnd('\\')}\""));

    /// <summary>Puts everything worth pasting into a bug report on the clipboard.</summary>
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder()
            .AppendLine("StreamerKit v0.1.0 (alpha, WinUI 3)")
            .AppendLine($"{Environment.OSVersion.VersionString} · .NET {Environment.Version}")
            .AppendLine($"{_servers.Count} servers · {_servers.Count(s => s.State == ServerState.Running)} running")
            .AppendLine();

        foreach (var server in _servers)
        {
            report.AppendLine($"--- {server.Name} · {server.StatusText}{(server.HasUrl ? $" · {server.Url}" : "")}");
            foreach (var line in server.LogText.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8))
                report.AppendLine($"    {line}");
            report.AppendLine();
        }

        CopyToClipboard(report.ToString());
        FlashFeedbackTitle("Copied to clipboard");
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void FlashFeedbackTitle(string message)
    {
        const string original = "Help us improve this tool";
        FeedbackTitle.Text = message;

        _flash = DispatcherQueue.CreateTimer();
        _flash.Interval = TimeSpan.FromSeconds(2.5);
        _flash.IsRepeating = false;
        _flash.Tick += (_, _) => FeedbackTitle.Text = original;
        _flash.Start();
    }

    /// <summary>Keeps an expanded log pinned to the newest line as output arrives.</summary>
    private void OnLogSizeChanged(object sender, SizeChangedEventArgs e)
    {
        for (DependencyObject? node = (DependencyObject)sender; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer)
            {
                viewer.ChangeView(null, viewer.ScrollableHeight, null, true);
                return;
            }
        }
    }
}
