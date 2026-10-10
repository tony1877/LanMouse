using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        (string title, string sub, SolidColorBrush color) = engine.Phase switch
        {
            Phase.Starting => ("Starting...", "Preparing your pairing key", Amber),
            Phase.Searching => ("Looking for your other PC...", "Open LAN Mouse there too, with the same key", Amber),
            Phase.Connected when engine.Controlling => ($"Controlling {engine.PeerName}", "Your mouse and keyboard are on the other PC", Blue),
            Phase.Connected when engine.Controlled => ($"{engine.PeerName} is in control", "Move this PC's own mouse to take it back", Blue),
            Phase.Connected => ($"Connected to {engine.PeerName}", $"{engine.PeerAddress} | move the cursor across the green line to switch", Green),
            _ => ("Not connected", "Press Connect on both PCs", Grey),
        };
        int me = engine.MyNumber, peer = engine.PeerNumber;
        HotkeyHint.Text = $"Ctrl+Alt+{peer} jumps to the other PC, Ctrl+Alt+{me} comes back here. PC numbers follow your arrangement: 1 is the left (or upper) PC.";
        if (engine.Phase == Phase.Connected && !engine.Controlling && !engine.Controlled)
            sub = (settings.EdgeSwitch, settings.HotkeySwitch) switch
            {
                (true, true) => $"{engine.PeerAddress} | cross the green line or press Ctrl+Alt+{peer}",
                (false, true) => $"{engine.PeerAddress} | press Ctrl+Alt+{peer} to switch",
                (true, false) => sub,
                _ => $"{engine.PeerAddress} | switching is off (turn it on below)",
            };
        if (engine.Controlling && settings.HotkeySwitch) sub = $"Ctrl+Alt+{me} or Scroll Lock brings it back";
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
        DrawDisplays();
        App.Current_.UpdateTray(engine);
    }

    // -- arrangement map: both PCs' monitors to scale; drag the other PC's screens to where they really sit --
    void DisplayMap_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDisplays();

    // view transform (kept fixed while dragging so the map doesn't jump around under the mouse)
    double mapScale = 1, mapX, mapY;
    bool dragging;
    Point dragStart;
    (int X, int Y) dragFrom, dragOffset;

    (int X, int Y) CurrentOffset => dragging ? dragOffset : engine.PeerOffset;

    void ComputeView()
    {
        double cw = DisplayMap.ActualWidth, ch = DisplayMap.ActualHeight;
        var mb = Screens.Box(Screens.Monitors);
        var pb = Screens.Box(engine.PeerLayoutMonitors);
        // Room for the other PC on any side of ours.
        double worldW = mb.Width + 2.0 * pb.Width, worldH = mb.Height + 2.0 * pb.Height;
        mapScale = Math.Min((cw - 8) / worldW, (ch - 8) / worldH);
        mapX = cw / 2 - (mb.Left + mb.Width / 2.0) * mapScale;
        mapY = ch / 2 - (mb.Top + mb.Height / 2.0) * mapScale;
    }

    Rect ToMap(Screens.Bounds m) => new(mapX + m.Left * mapScale, mapY + m.Top * mapScale, m.Width * mapScale, m.Height * mapScale);

    void DrawDisplays()
    {
        var c = DisplayMap;
        c.Children.Clear();
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return;
        if (!dragging) ComputeView();

        var mine = Screens.Monitors;
        var (ox, oy) = CurrentOffset;
        var theirs = Screens.Shift(engine.PeerLayoutMonitors, ox, oy);
        bool known = engine.PeerMonitorsKnown;
        bool connected = engine.Phase == Phase.Connected;
        bool peerActive = connected && engine.Controlling;
        bool meActive = !peerActive && !(connected && engine.Controlled);
        string peerName = connected && !string.IsNullOrEmpty(engine.PeerName) ? engine.PeerName : "Other PC";

        DrawGroup(c, mine, $"PC {engine.MyNumber}: This PC", meActive, mine: true, known: true);
        DrawGroup(c, theirs, $"PC {engine.PeerNumber}: {peerName}", peerActive, mine: false, known: known);

        // Where the cursor crosses between the PCs.
        if (!dragging)
        {
            var glow = Brush("#3DDC97");
            foreach (var m in mine)
                foreach (var p in theirs)
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
                    c.Children.Add(line);
                }
        }

        int pc = engine.PeerLayoutMonitors.Length;
        DisplayHint.Text = !known
            ? "Drag the other PC to where its screen sits. Its real screens appear here once it connects."
            : $"Drag {peerName}'s {(pc == 1 ? "screen" : $"{pc} screens")} to where they sit next to yours. The green line is where the cursor crosses. Set it on either PC and the other follows.";
    }

    void DrawGroup(Canvas c, Screens.Bounds[] mons, string name, bool active, bool mine, bool known)
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
                    Foreground = mine ? (Brush)FindResource("Text") : Brush("#C9D3FF"), HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = Math.Max(10, r.Width - 8),
                });
            if (mons.Length > 1 || !known)
                label.Children.Add(new TextBlock
                {
                    Text = known ? $"Display {i + 1}" : "?", FontSize = 9.5, Foreground = Brush("#8A93A4"),
                    HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(10, r.Width - 8),
                });
            var box = new Border
            {
                Width = Math.Max(4, r.Width - 3), Height = Math.Max(4, r.Height - 3),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
                BorderBrush = active ? accent : mine ? Brush("#4A5263") : Brush("#5A63A8"),
                Background = active ? (Brush)FindResource("AccentDim") : mine ? Brush("#1C2029") : Brush("#1D2135"),
                Opacity = known ? 1 : 0.6,
                Child = label,
                Cursor = mine ? null : System.Windows.Input.Cursors.SizeAll,
                ToolTip = mine ? $"This PC, {mons[i].Width} x {mons[i].Height}"
                        : known ? $"{mons[i].Width} x {mons[i].Height} (drag to move)" : "Drag to move",
                Tag = mine ? null : "peer",
            };
            if (!known) box.BorderBrush = Brush("#4A5160");
            Canvas.SetLeft(box, r.Left + 1.5);
            Canvas.SetTop(box, r.Top + 1.5);
            c.Children.Add(box);
        }
    }

    bool OverPeer(Point p)
    {
        var (ox, oy) = engine.PeerOffset;
        return Screens.Shift(engine.PeerLayoutMonitors, ox, oy).Any(m => ToMap(m).Contains(p));
    }

    void DisplayMap_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DisplayMap);
        if (!OverPeer(p)) return;
        dragging = true;
        dragStart = p;
        dragFrom = dragOffset = engine.PeerOffset;
        DisplayMap.CaptureMouse();
        e.Handled = true;
    }

    void DisplayMap_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging) return;
        var p = e.GetPosition(DisplayMap);
        dragOffset = (dragFrom.X + (int)Math.Round((p.X - dragStart.X) / mapScale),
                      dragFrom.Y + (int)Math.Round((p.Y - dragStart.Y) / mapScale));
        DrawDisplays();
    }

    void DisplayMap_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!dragging) return;
        var target = dragOffset;
        dragging = false;
        DisplayMap.ReleaseMouseCapture();
        if (target != dragFrom) engine.MovePeer(target.X, target.Y);
        Refresh();
    }

    void DisplayMap_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        DrawDisplays();
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
