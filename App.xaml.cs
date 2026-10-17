using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LanMouse;

public partial class App : System.Windows.Application
{
    public static App Current_ => (App)Current;
    public bool Exiting { get; private set; }

    Mutex? single;
    Settings settings = null!;
    Engine engine = null!;
    ClipboardSync clipboard = null!;
    MainWindow window = null!;
    Forms.NotifyIcon tray = null!;
    Forms.ToolStripMenuItem trayToggle = null!;
    Icon? baseIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CursorHider.RestoreNow(); // in case a previous run died while the pointer was hidden
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { CursorHider.RestoreNow(); };
        DispatcherUnhandledException += (_, a) => { Crash(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crash(a.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();

        bool minimized = e.Args.Contains("--minimized");
        try
        {
            if (!TakeSingleInstance(minimized)) { Shutdown(); return; }

            settings = Settings.Load();
            settings.Save();
            engine = new Engine(settings);
            clipboard = new ClipboardSync(engine, settings);
            engine.Clipboard = clipboard;
            engine.Notify += msg => Dispatcher.BeginInvoke(() => Balloon(msg));

            SetupTray();
            window = new MainWindow(settings, engine);
            if (!minimized) window.Show();
        }
        catch (Exception ex)
        {
            Crash(ex);
            Shutdown();
            return;
        }

        if (settings.AutoConnect && settings.Key.Length >= 8)
        {
            try
            {
                await engine.StartAsync();
                // Right after sign-in the sign-in agent may still hold the ports for a moment while it hands over.
                for (int i = 0; i < 20 && engine.Phase == Phase.Stopped && settings.SignInSupport; i++)
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    await engine.StartAsync();
                }
            }
            catch { }
        }
        else if (minimized) window.Show();
    }

    /// <summary>
    /// One copy per user. Starting it by hand replaces whatever copy is already running
    /// (an older version, or one stuck in the tray). Auto-start at login just backs off.
    /// </summary>
    bool TakeSingleInstance(bool minimized)
    {
        single = new Mutex(false, @"Local\LanMouse.v3");
        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LanMouse.v3.quit");
        if (!Acquire(0))
        {
            if (minimized) return false;
            quitSignal.Set();                       // ask the old copy to exit politely
            if (!Acquire(4000))
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("LanMouse"))
                    if (p.Id != Environment.ProcessId) try { p.Kill(); p.WaitForExit(3000); } catch { }
                if (!Acquire(4000)) throw new InvalidOperationException("Another LAN Mouse is running and could not be closed. End LanMouse.exe in Task Manager and try again.");
            }
        }
        quitSignal.Reset();
        new Thread(() => { if (quitSignal.WaitOne()) Dispatcher.BeginInvoke(ExitApp); }) { IsBackground = true }.Start();
        return true;
    }

    bool Acquire(int ms)
    {
        try { return single!.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; } // previous owner was killed; we own it now
    }

    static void Crash(Exception? ex)
    {
        try { CursorHider.RestoreNow(); } catch { }
        try
        {
            System.IO.Directory.CreateDirectory(Settings.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "crash.log"), $"[{DateTime.Now}] {ex}\r\n\r\n");
        }
        catch { }
        System.Windows.MessageBox.Show($"LAN Mouse hit an error:\n\n{ex?.Message}\n\nDetails were saved to %APPDATA%\\LanMouse\\crash.log",
            "LAN Mouse", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void SetupTray()
    {
        try { baseIcon = new Icon(GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream, 32, 32); } catch { }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open LAN Mouse", null, (_, _) => ShowWindow());
        trayToggle = new Forms.ToolStripMenuItem("Connect", null, async (_, _) =>
        {
            if (engine.Phase != Phase.Stopped) engine.Stop();
            else try { await engine.StartAsync(); } catch { ShowWindow(); }
        });
        menu.Items.Add(trayToggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        tray = new Forms.NotifyIcon { Icon = baseIcon ?? SystemIcons.Application, Text = "LAN Mouse", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };
    }

    internal void UpdateTray(Engine e)
    {
        if (tray == null) return;
        trayToggle.Text = e.Phase == Phase.Stopped ? "Connect" : "Disconnect";
        var text = e.Phase switch
        {
            Phase.Connected => $"LAN Mouse: connected to {string.Join(", ", e.Peers.Select(p => p.Name))}",
            Phase.Searching => "LAN Mouse: looking for your other PCs",
            _ => "LAN Mouse: off",
        };
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Balloon(string text)
    {
        if (tray == null) return;
        tray.BalloonTipTitle = "LAN Mouse";
        tray.BalloonTipText = text;
        tray.ShowBalloonTip(3000);
    }

    void ShowWindow()
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        try
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            engine?.Dispose();
            clipboard?.Dispose();
            window?.Close();
        }
        catch { }
        CursorHider.RestoreNow();
        try { single?.ReleaseMutex(); } catch { }
        Shutdown();
    }
}
