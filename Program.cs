using System;
using System.Linq;

namespace LanMouse;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Privileged / headless modes run before any WPF starts.
        if (args.Contains("--agent-service")) { Agent.RunService(); return 0; }
        if (args.Contains("--install-agent")) return Agent.Install();
        if (args.Contains("--uninstall-agent")) return Agent.Uninstall();
        if (args.Contains("--agent-worker")) { Agent.RunWorker(); return 0; }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
