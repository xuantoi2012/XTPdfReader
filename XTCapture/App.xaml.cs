using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace XTCapture
{
    public partial class App : Application
    {
        private Mutex? _single;
        private TrayHost? _host;
        private CancellationTokenSource? _listener;

        /// <summary>The command a start asks for: "background" (tray and corner button only), "capture", "store", "settings" or "exit".</summary>
        internal static string CommandFrom(string[] args)
        {
            foreach (string arg in args)
            {
                string name = arg.TrimStart('-', '/').ToLowerInvariant();
                if (name is "background" or "capture" or "store" or "settings" or "exit") return name;
            }
            return "store"; // started by hand (Start menu): show what there is
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string command = CommandFrom(e.Args);
            _single = new Mutex(true, @"Local\XTCapture_Instance", out bool first);
            if (!first)
            {
                // Already running: pass the command on. "background" needs nothing.
                if (command != "background") InstanceChannel.Send(command);
                Shutdown();
                return;
            }
            if (command == "exit") { Shutdown(); return; }

            DispatcherUnhandledException += (_, args) => { Log(args.Exception); args.Handled = true; };
            CaptureTheme.Apply();
            _host = new TrayHost();
            _host.ExitRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));
            _host.Start();
            _listener = InstanceChannel.Listen(cmd => Dispatcher.BeginInvoke(new Action(() => _host?.Execute(cmd))));
            if (command != "background") _host.Execute(command);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _listener?.Cancel();
            _host?.Dispose();
            try { _single?.ReleaseMutex(); } catch { /* not owned: this was a second start */ }
            _single?.Dispose();
            base.OnExit(e);
        }

        private static void Log(Exception ex)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTCapture");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "error.log"), $"{DateTime.Now:O} {ex}{Environment.NewLine}");
            }
            catch { /* nowhere to write: drop it */ }
        }
    }
}
