using System;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SpotifyMini
{
    // Tray icon that toggles the popup; the app has no other window.
    public partial class App : Application
    {
        Mutex _single;
        Forms.NotifyIcon _tray;
        MainWindow _popup;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _single = new Mutex(true, "SpotifyMini-single-instance", out bool first);
            if (!first)
            {
                Shutdown();
                return;
            }

            _popup = new MainWindow();
            _popup.TrackChanged += text => _tray.Text = Truncate(text, 127);

            _tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath),
                Text = "Spotify",
                Visible = true,
            };
            _tray.MouseClick += (s, ev) =>
            {
                if (ev.Button == Forms.MouseButtons.Left) _popup.Toggle();
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Abrir", null, (s, ev) => _popup.ShowPopup());
            menu.Items.Add("Reproducir / pausar", null, (s, ev) => _ = _popup.Media.PlayPause());
            menu.Items.Add("Siguiente", null, (s, ev) => _ = _popup.Media.Next());
            menu.Items.Add("Anterior", null, (s, ev) => _ = _popup.Media.Previous());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, ev) => Quit());
            _tray.ContextMenuStrip = menu;

            _ = _popup.InitAsync();
        }

        static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        void Quit()
        {
            _tray.Visible = false;
            _tray.Dispose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null) _tray.Visible = false;
            base.OnExit(e);
        }
    }
}
