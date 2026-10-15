using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LanMouse;

public partial class MainWindow : Window
{
    readonly Settings settings;
    readonly Engine engine;
    bool loading = true;

    internal MainWindow(Settings settings, Engine engine)
    {
        this.settings = settings;
        this.engine = engine;
        InitializeComponent();

        try { Logo.Source = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); } catch { }
        try { MyAddress.Text = "This PC's address: " + string.Join(", ", LocalAddresses()); } catch { }
        KeyHidden.Password = settings.Key;
        PeerAddress.Text = settings.PeerAddress;
        OptInput.IsChecked = settings.ShareInput;
        OptText.IsChecked = settings.ShareText;
        OptImages.IsChecked = settings.ShareImages;
        OptFiles.IsChecked = settings.ShareFiles;
        OptAuto.IsChecked = settings.AutoConnect;
        OptEdge.IsChecked = settings.EdgeSwitch;
        OptHotkey.IsChecked = settings.HotkeySwitch;
        OptStartup.IsChecked = Settings.StartWithWindows;

        foreach (var cb in new[] { OptInput, OptText, OptImages, OptFiles, OptAuto, OptStartup, OptEdge, OptHotkey })
        {
            cb.Checked += (_, _) => SaveOptions();
            cb.Unchecked += (_, _) => SaveOptions();
        }
        PeerAddress.LostFocus += (_, _) => { settings.PeerAddress = PeerAddress.Text.Trim(); settings.Save(); };

        engine.Changed += () => Dispatcher.BeginInvoke(Refresh);
        engine.Progress += (text, frac) => Dispatcher.BeginInvoke(() => ShowProgress(text, frac));
        MaxHeight = SystemParameters.WorkArea.Height - 16;
        loading = false;
        Refresh();
        RefreshSignInButton();
    }

    static System.Collections.Generic.IEnumerable<string> LocalAddresses()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || nic.Description.Contains("Hyper-V") || nic.Name.StartsWith("vEthernet")) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254") && !System.Net.IPAddress.IsLoopback(a.Address))
                    yield return a.Address.ToString();
        }
    }

    void SaveOptions()
    {
        if (loading) return;
        settings.ShareInput = OptInput.IsChecked == true;
        settings.ShareText = OptText.IsChecked == true;
        settings.ShareImages = OptImages.IsChecked == true;
        settings.ShareFiles = OptFiles.IsChecked == true;
        settings.AutoConnect = OptAuto.IsChecked == true;
        settings.EdgeSwitch = OptEdge.IsChecked == true;
        settings.HotkeySwitch = OptHotkey.IsChecked == true;
        Refresh();
        try { Settings.StartWithWindows = OptStartup.IsChecked == true; } catch { }
        settings.Save();
    }

    static readonly SolidColorBrush Grey = Brush("#8A93A4"), Amber = Brush("#F5B74E"), Green = Brush("#3DDC97"), Blue = Brush("#6E8BFF");
    static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    void Refresh()
    {
        var peers = engine.Peers;
        string names = peers.Count == 0 ? "" : string.Join(", ", peers.Select(p => p.Name));
        string connectedTitle = peers.Count == 1 ? $"Connected to {peers[0].Name}" : $"Connected to {peers.Count} PCs";
        string where = peers.Count == 1 ? peers[0].Address : names;
        (string title, string sub, SolidColorBrush color) = engine.Phase switch
        {
            Phase.Starting => ("Starting...", "Preparing your pairing key", Amber),
            Phase.Searching => ("Looking for your other PCs...", "Open LAN Mouse on them too, with the same key", Amber),
            Phase.Connected when engine.Controlling => ($"Controlling {engine.ControllingName}", "Your mouse and keyboard are on that PC", Blue),
            Phase.Connected when engine.Controlled => ($"{engine.ControlledByName} is in control", "Move this PC's own mouse to take it back", Blue),
            Phase.Connected => (connectedTitle, $"{where} | move the cursor across a green line to switch", Green),
            _ => ("Not connected", "Press Connect on every PC", Grey),
        };

        var view = engine.LayoutView();
        int me = view.FirstOrDefault(v => v.IsSelf)?.Number ?? 1;
        var hotkeys = view.Where(v => v.Number <= 9).Select(v => $"Ctrl+Alt+{v.Number} = {(v.IsSelf ? "this PC" : v.Name)}");
        HotkeyHint.Text = string.Join("\n", hotkeys) + "\nNumbers follow your arrangement: left to right, then top to bottom.";
        if (engine.Phase == Phase.Connected && !engine.Controlling && !engine.Controlled)
            sub = (settings.EdgeSwitch, settings.HotkeySwitch) switch
            {
                (true, true) => $"{where} | cross a green line or press Ctrl+Alt+number",
                (false, true) => $"{where} | press Ctrl+Alt+number to switch",
                (true, false) => sub,
                _ => $"{where} | switching is off (turn it on in Settings)",
            };
        if (engine.Controlling && settings.HotkeySwitch) sub = $"Ctrl+Alt+{me} or Scroll Lock brings it back here";
        StatusTitle.Text = title;
        StatusSub.Text = sub;
        StatusDot.Fill = color;
        StatusHalo.Fill = color;

        WarningBox.Visibility = string.IsNullOrEmpty(engine.Warning) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = engine.Warning ?? "";
        ReleaseBtn.Visibility = engine.Controlling ? Visibility.Visible : Visibility.Collapsed;

        bool running = engine.Phase != Phase.Stopped;
        ConnectBtn.Content = running ? "Disconnect" : "Connect";
        ConnectBtn.Background = running ? Brush("#2A2F3A") : (Brush)FindResource("Accent");
        ConnectBtn.IsEnabled = engine.Phase != Phase.Starting;
        KeyHidden.IsEnabled = KeyShown.IsEnabled = GenBtn.IsEnabled = !running;
        PeerAddress.IsEnabled = !running;
        ForgetBtn.Visibility = view.Any(v => !v.Online) ? Visibility.Visible : Visibility.Collapsed;
        DrawDisplays();
        App.Current_.UpdateTray(engine);
    }

    // -- arrangement map: every PC's monitors to scale; drag any PC to where its screens really sit --
    void DisplayMap_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDisplays();

    // view transform (kept fixed while dragging so the map doesn't jump around under the mouse)
    double mapScale = 1, mapX, mapY;
    string? dragId;
    Point dragStart;
    (int X, int Y) dragFrom, dragPos;
    System.Collections.Generic.List<PcView> pcs = new();

    void ComputeView()
    {
        double cw = DisplayMap.ActualWidth, ch = DisplayMap.ActualHeight;
        var all = pcs.SelectMany(p => p.Monitors).ToArray();
        if (all.Length == 0) return;
        var box = Screens.Box(all);
        // Leave room to drag a PC to any side of the others.
        double pad = pcs.Max(p => Math.Max(Screens.Box(p.Monitors).Width, Screens.Box(p.Monitors).Height)) * 0.75;
        double worldW = box.Width + 2 * pad, worldH = box.Height + 2 * pad;
        mapScale = Math.Min((cw - 8) / worldW, (ch - 8) / worldH);
        mapX = cw / 2 - (box.Left + box.Width / 2.0) * mapScale;
        mapY = ch / 2 - (box.Top + box.Height / 2.0) * mapScale;
    }

    Rect ToMap(Screens.Bounds m) => new(mapX + m.Left * mapScale, mapY + m.Top * mapScale, m.Width * mapScale, m.Height * mapScale);

    /// <summary>A PC's monitors where they are drawn right now (moved along while being dragged).</summary>
    Screens.Bounds[] Drawn(PcView p) =>
        p.Id == dragId ? Screens.Shift(p.Monitors, dragPos.X - p.Pos.X, dragPos.Y - p.Pos.Y) : p.Monitors;

    void DrawDisplays()
    {
        var c = DisplayMap;
        c.Children.Clear();
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return;
        if (dragId == null) { pcs = engine.LayoutView(); ComputeView(); }

        bool connected = engine.Phase == Phase.Connected;
        string? active = connected && engine.Controlling ? engine.ControllingId
                       : connected && engine.Controlled ? null : pcs.FirstOrDefault(p => p.IsSelf)?.Id;

        // Dragged PC last, so it draws on top.
        foreach (var p in pcs.OrderBy(p => p.Id == dragId))
        {
            string name = p.IsSelf ? $"PC {p.Number}: This PC" : $"PC {p.Number}: {p.Name}";
            DrawGroup(c, p, Drawn(p), name, p.Id == active);
        }

        // Where the cursor crosses between PCs.
        if (dragId == null)
        {
            var glow = Brush("#3DDC97");
            for (int i = 0; i < pcs.Count; i++)
                for (int j = i + 1; j < pcs.Count; j++)
                    foreach (var m in pcs[i].Monitors)
                        foreach (var p in pcs[j].Monitors)
                        {
                            if (Screens.Shared(m, p) <= 0) continue;
                            var a = ToMap(m); var b = ToMap(p);
                            System.Windows.Shapes.Line line;
                            if (m.Right == p.Left || p.Right == m.Left)
                            {
                                double x = m.Right == p.Left ? a.Right : a.Left;
                                line = new() { X1 = x, X2 = x, Y1 = Math.Max(a.Top, b.Top) + 2, Y2 = Math.Min(a.Bottom, b.Bottom) - 2 };
                            }
                            else
                            {
                                double y = m.Bottom == p.Top ? a.Bottom : a.Top;
                                line = new() { Y1 = y, Y2 = y, X1 = Math.Max(a.Left, b.Left) + 2, X2 = Math.Min(a.Right, b.Right) - 2 };
                            }
                            line.Stroke = glow;
                            line.StrokeThickness = 3;
                            line.StrokeStartLineCap = line.StrokeEndLineCap = PenLineCap.Round;
                            line.IsHitTestVisible = false;
                            if (!pcs[i].Online || !pcs[j].Online) line.Opacity = 0.35;
                            c.Children.Add(line);
                        }
        }

        DisplayHint.Text = pcs.Count <= 1
            ? "Your other PCs appear here once they connect. Then drag them to where their screens really sit."
            : "Drag any PC to where its screens really sit; it snaps to the others. Green lines are where the cursor crosses. Change it on any PC and the rest follow.";
    }

    void DrawGroup(Canvas c, PcView pc, Screens.Bounds[] mons, string name, bool active)
    {
        var accent = (SolidColorBrush)FindResource("Accent");
        for (int i = 0; i < mons.Length; i++)
        {
            var r = ToMap(mons[i]);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (i == 0 || mons.Length == 1)
                label.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = pc.IsSelf ? (Brush)FindResource("Text") : Brush("#C9D3FF"), HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = Math.Max(10, r.Width - 8),
                });
            if (mons.Length > 1 || !pc.Known || !pc.Online)
                label.Children.Add(new TextBlock
                {
                    Text = !pc.Online ? "offline" : pc.Known ? $"Display {i + 1}" : "?", FontSize = 9.5, Foreground = Brush("#8A93A4"),
                    HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(10, r.Width - 8),
                });
            var box = new Border
            {
                Width = Math.Max(4, r.Width - 3), Height = Math.Max(4, r.Height - 3),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
                BorderBrush = active ? accent : pc.IsSelf ? Brush("#4A5263") : Brush("#5A63A8"),
                Background = active ? (Brush)FindResource("AccentDim") : pc.IsSelf ? Brush("#1C2029") : Brush("#1D2135"),
                Opacity = pc.Online && pc.Known ? 1 : 0.55,
                Child = label,
                Cursor = System.Windows.Input.Cursors.SizeAll,
                ToolTip = $"{(pc.IsSelf ? "This PC" : pc.Name)}, {mons[i].Width} x {mons[i].Height} (drag to move)",
            };
            Canvas.SetLeft(box, r.Left + 1.5);
            Canvas.SetTop(box, r.Top + 1.5);
            c.Children.Add(box);
        }
    }

    PcView? PcAt(Point p) => pcs.LastOrDefault(pc => pc.Monitors.Any(m => ToMap(m).Contains(p)));

    void DisplayMap_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DisplayMap);
        if (pcs.Count < 2 || PcAt(p) is not PcView pc) return;
        dragId = pc.Id;
        dragStart = p;
        dragFrom = dragPos = pc.Pos;
        DisplayMap.CaptureMouse();
        e.Handled = true;
    }

    void DisplayMap_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragId == null) return;
        var p = e.GetPosition(DisplayMap);
        dragPos = (dragFrom.X + (int)Math.Round((p.X - dragStart.X) / mapScale),
                   dragFrom.Y + (int)Math.Round((p.Y - dragStart.Y) / mapScale));
        DrawDisplays();
    }

    void DisplayMap_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dragId == null) return;
        var (id, to) = (dragId, dragPos);
        dragId = null;
        DisplayMap.ReleaseMouseCapture();
        if (to != dragFrom) engine.MovePc(id, to.X, to.Y);
        Refresh();
    }

    void DisplayMap_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragId == null) return;
        dragId = null;
        DrawDisplays();
    }

    void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the offline PCs from the arrangement?", "LAN Mouse", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            engine.ForgetOffline();
    }

    void ShowProgress(string? text, double frac)
    {
        if (text == null || frac < 0) { ProgressBox.Visibility = Visibility.Collapsed; return; }
        ProgressBox.Visibility = Visibility.Visible;
        ProgressText.Text = text;
        ProgressPct.Text = $"{frac:P0}";
        ProgressFill.Width = Math.Max(0, ((FrameworkElement)ProgressFill.Parent).ActualWidth * Math.Clamp(frac, 0, 1));
    }

    // -- key --
    void KeyHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (KeyShown.Text != KeyHidden.Password) KeyShown.Text = KeyHidden.Password;
    }

    void KeyShown_Changed(object sender, TextChangedEventArgs e)
    {
        if (KeyHidden.Password != KeyShown.Text) KeyHidden.Password = KeyShown.Text;
    }

    void Eye_Click(object sender, RoutedEventArgs e)
    {
        bool show = KeyShown.Visibility != Visibility.Visible;
        KeyShown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        KeyHidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        EyeBtn.Content = show ? "\uED1A" : "\uE7B3";
    }

    void Generate_Click(object sender, RoutedEventArgs e)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var chars = new char[19];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = i % 5 == 4 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        KeyHidden.Password = new string(chars);
        if (KeyShown.Visibility != Visibility.Visible) Eye_Click(sender, e);
        try { System.Windows.Clipboard.SetText(KeyHidden.Password); } catch { }
        App.Current_.Balloon("New key copied. Type it on your other PC.");
    }

    // -- actions --
    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (engine.Phase != Phase.Stopped) { engine.Stop(); return; }
        if (KeyHidden.Password.Length < 8)
        {
            ShowPage(true);
            KeyHidden.Focus();
            App.Current_.Balloon("Set a pairing key first (8+ characters), the same on both PCs.");
            return;
        }
        settings.Key = KeyHidden.Password;
        settings.PeerAddress = PeerAddress.Text.Trim();
        settings.Save();
        try { await engine.StartAsync(); }
        catch (Exception ex)
        {
            engine.Stop();
            System.Windows.MessageBox.Show(this, ex.Message, "LAN Mouse", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    void Release_Click(object sender, RoutedEventArgs e) => engine.ReleaseControl();

    void OpenReceived_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Engine.ReceiveDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Engine.ReceiveDir}\"") { UseShellExecute = true });
    }

    void Firewall_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath!;
        var args = "/c netsh advfirewall firewall delete rule name=\"LAN Mouse\" & " +
                   $"netsh advfirewall firewall add rule name=\"LAN Mouse\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            App.Current_.Balloon("Firewall rule added. Do the same on the other PC.");
        }
        catch { /* user said no to the admin prompt */ }
    }

    bool agentInstalled;

    void RefreshSignInButton()
    {
        agentInstalled = Agent.IsInstalled();
        SignInBtn.Content = agentInstalled ? "Remove sign-in support" : "Install sign-in support";
    }

    void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (agentInstalled)
        {
            if (MessageBox.Show(this, "Remove sign-in support from this PC?", "LAN Mouse", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SignInBtn.IsEnabled = false;
            Task.Run(() =>
            {
                bool ok = Agent.RunElevated("--uninstall-agent");
                Dispatcher.BeginInvoke(() =>
                {
                    if (ok) { settings.SignInSupport = false; settings.Save(); }
                    SignInBtn.IsEnabled = true;
                    RefreshSignInButton();
                });
            });
            return;
        }

        var msg = "Install sign-in support?\n\n" +
                  "This adds a small Windows service that lets you type your PIN or password on THIS PC from your other PC.\n\n" +
                  "Trade-off: while your PCs are connected, anyone who knows your pairing key and is on your network could use this PC's sign-in screen. They would still need your PIN or password to get in.\n\n" +
                  "You can remove it here at any time. Windows will ask for administrator permission.";
        if (MessageBox.Show(this, msg, "LAN Mouse", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SignInBtn.IsEnabled = false;
        Task.Run(() =>
        {
            bool ok = Agent.RunElevated("--install-agent") && Agent.IsInstalled();
            Dispatcher.BeginInvoke(() =>
            {
                if (ok) { settings.SignInSupport = true; settings.Save(); App.Current_.Balloon("Sign-in support installed. Do the same on your other PC to control its sign-in screen."); }
                else App.Current_.Balloon("Sign-in support was not installed.");
                SignInBtn.IsEnabled = true;
                RefreshSignInButton();
            });
        });
    }

    void ShowPage(bool settingsPage)
    {
        HomePage.Visibility = settingsPage ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsBtn.Visibility = settingsPage ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = settingsPage ? "LAN Mouse Settings" : "LAN Mouse";
        Scroller.ScrollToTop();
        if (!settingsPage) { settings.PeerAddress = PeerAddress.Text.Trim(); settings.Save(); }
    }

    void Settings_Click(object sender, RoutedEventArgs e) => ShowPage(true);
    void Back_Click(object sender, RoutedEventArgs e) => ShowPage(false);

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Current_.Exiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
