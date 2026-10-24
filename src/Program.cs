// SteamOffline - minimalist monochrome toggle for Steam's network access.
//
// Blocks the Steam client's own binaries with Windows Firewall rules so Steam
// falls back to Offline Mode. Game executables are never touched.
//
// Built against .NET Framework 4.8 with csc.exe (C# 5), UI composed in code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;

// System.Windows.Shapes also defines Path; this file only ever means the IO one.
using Path = System.IO.Path;

namespace SteamOffline
{
    // ---------------------------------------------------------------- theme
    internal static class Theme
    {
        public static readonly Brush Canvas   = Freeze(Color("#FBFBFA"));
        public static readonly Brush Surface  = Freeze(Color("#FFFFFF"));
        public static readonly Brush Ink      = Freeze(Color("#111111"));
        public static readonly Brush InkSoft  = Freeze(Color("#333333"));
        public static readonly Brush Muted    = Freeze(Color("#787774"));
        public static readonly Brush Line     = Freeze(Color("#EAEAEA"));
        public static readonly Brush Paper    = Freeze(Color("#F7F6F3"));

        // Colour is a scarce resource here: washed-out pastels carry the state
        // and nothing else. Everything structural stays monochrome.
        public static readonly System.Windows.Media.Color PaleGreen   = Color("#EDF3EC");
        public static readonly System.Windows.Media.Color EdgeGreen   = Color("#DCE7DA");
        public static readonly System.Windows.Media.Color DeepGreen   = Color("#346538");
        public static readonly System.Windows.Media.Color PaleRed     = Color("#FDEBEC");
        public static readonly System.Windows.Media.Color EdgeRed     = Color("#F5D7D9");
        public static readonly System.Windows.Media.Color DeepRed     = Color("#9F2F2D");
        public static readonly System.Windows.Media.Color PaleNeutral = Color("#F7F6F3");
        public static readonly System.Windows.Media.Color EdgeNeutral = Color("#EAEAEA");
        public static readonly System.Windows.Media.Color InkColor    = Color("#111111");

        public static readonly FontFamily Sans   = new FontFamily("Segoe UI Variable Display, Segoe UI, Helvetica Neue");
        public static readonly FontFamily Serif  = new FontFamily("Instrument Serif, Playfair Display, Constantia, Georgia");
        public static readonly FontFamily Mono   = new FontFamily("Cascadia Mono, Consolas, SF Mono");

        public static System.Windows.Media.Color Color(string hex)
        {
            return (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
        }

        private static Brush Freeze(System.Windows.Media.Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // WPF has no letter-spacing; thin spaces fake the wide tracking the
        // editorial eyebrow labels need.
        public static string Tracked(string s)
        {
            return string.Join(" ", s.ToCharArray().Select(c => c.ToString()).ToArray());
        }

        public static readonly KeySpline Ease = new KeySpline(0.16, 1.0, 0.3, 1.0);
    }

    // ------------------------------------------------------------- firewall
    internal static class Firewall
    {
        public const string RulePrefix = "SteamOfflineMode";

        private const int TimeoutMs = 90000;

        // netsh's output is localised and its exit codes are unreliable, so the
        // NetSecurity cmdlets are driven instead and asked for a fixed marker.
        //
        // Both pipes are drained asynchronously: reading one to the end before
        // touching the other deadlocks as soon as the child fills the pipe it is
        // writing to while the parent is blocked on the other one.
        internal static string RunPowerShell(string script)
        {
            // Progress records are serialised to stderr as CLIXML the moment the
            // pipes are redirected; silencing them keeps that channel nearly empty.
            string wrapped =
                "$ProgressPreference = 'SilentlyContinue'; " +
                "try { " + script + " } catch { 'RESULT:ERR ' + $_.Exception.Message }";

            var psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                            Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            var outBuf = new StringBuilder();
            var errBuf = new StringBuilder();

            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (outBuf) outBuf.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (errBuf) errBuf.AppendLine(e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(TimeoutMs))
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    throw new TimeoutException(
                        "The firewall command did not finish within 90 seconds and was cancelled.");
                }
                p.WaitForExit();   // flushes the async readers

                string stdout, stderr;
                lock (outBuf) stdout = outBuf.ToString();
                lock (errBuf) stderr = errBuf.ToString();

                int err = stdout.IndexOf("RESULT:ERR", StringComparison.Ordinal);
                if (err >= 0)
                    throw new InvalidOperationException(
                        stdout.Substring(err + "RESULT:ERR".Length).Trim());

                if (stdout.IndexOf("RESULT:", StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(stderr)
                            ? "PowerShell returned no result (exit code " +
                              p.ExitCode.ToString(CultureInfo.InvariantCulture) + ")."
                            : stderr.Trim());

                return stdout;
            }
        }

        private static string PsQuote(string s)
        {
            return "'" + s.Replace("'", "''") + "'";
        }

        public static bool IsBlocked()
        {
            string script =
                "if (Get-NetFirewallRule -DisplayName '" + RulePrefix + "*' -ErrorAction SilentlyContinue) " +
                "{ 'RESULT:BLOCKED' } else { 'RESULT:FREE' }";
            return RunPowerShell(script).IndexOf("RESULT:BLOCKED", StringComparison.Ordinal) >= 0;
        }

        public static void Unblock()
        {
            string script =
                "$r = @(Get-NetFirewallRule -DisplayName '" + RulePrefix + "*' -ErrorAction SilentlyContinue); " +
                "if ($r.Count) { $r | Remove-NetFirewallRule -ErrorAction Stop }; 'RESULT:OK'";
            RunPowerShell(script);
        }

        public static void Block(IList<string> targets)
        {
            var sb = new StringBuilder();
            // Clear first so repeated runs cannot stack duplicate rules.
            sb.Append("$r = @(Get-NetFirewallRule -DisplayName '").Append(RulePrefix)
              .Append("*' -ErrorAction SilentlyContinue); ")
              .Append("if ($r.Count) { $r | Remove-NetFirewallRule -ErrorAction Stop }; ");

            for (int i = 0; i < targets.Count; i++)
            {
                string exe = targets[i];
                string leaf = Path.GetFileName(exe);
                foreach (string dir in new[] { "Outbound", "Inbound" })
                {
                    string name = RulePrefix + " - " + leaf + " [" + (i + 1) + "/" + dir + "]";
                    sb.Append("New-NetFirewallRule -DisplayName ").Append(PsQuote(name))
                      .Append(" -Group ").Append(PsQuote(RulePrefix))
                      .Append(" -Direction ").Append(dir)
                      .Append(" -Program ").Append(PsQuote(exe))
                      .Append(" -Action Block -Profile Any -Enabled True -ErrorAction Stop | Out-Null; ");
                }
            }
            sb.Append("'RESULT:OK'");
            RunPowerShell(sb.ToString());
        }
    }

    // ---------------------------------------------------------------- steam
    internal static class Steam
    {
        private static readonly string[] ClientExeNames =
        {
            "steam.exe", "steamwebhelper.exe", "steamservice.exe",
            "steamerrorreporter.exe", "steamerrorreporter64.exe"
        };

        public static string FindRoot()
        {
            var candidates = new List<string>();

            AddFromKey(candidates, Registry.CurrentUser, @"Software\Valve\Steam");
            AddFromKey(candidates, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam");
            AddFromKey(candidates, Registry.LocalMachine, @"SOFTWARE\Valve\Steam");

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string dir = c.Replace('/', '\\').TrimEnd('\\');
                try
                {
                    if (File.Exists(Path.Combine(dir, "steam.exe")))
                        return Path.GetFullPath(dir);
                }
                catch { }
            }
            throw new FileNotFoundException("Steam not found on this machine.");
        }

        private static void AddFromKey(List<string> into, RegistryKey hive, string path)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(path))
                {
                    if (k == null) return;
                    string sp = k.GetValue("SteamPath") as string;
                    string ip = k.GetValue("InstallPath") as string;
                    string se = k.GetValue("SteamExe") as string;
                    if (!string.IsNullOrEmpty(sp)) into.Add(sp);
                    if (!string.IsNullOrEmpty(ip)) into.Add(ip);
                    if (!string.IsNullOrEmpty(se)) into.Add(Path.GetDirectoryName(se.Replace('/', '\\')));
                }
            }
            catch { }
        }

        // Client binaries only. Games live under steamapps\ and stay reachable.
        public static List<string> FindTargets(string root)
        {
            var found = new List<string>();
            string main = Path.Combine(root, "steam.exe");
            if (File.Exists(main)) found.Add(main);

            foreach (string sub in new[] { "bin", "package" })
            {
                string dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories))
                    {
                        string leaf = Path.GetFileName(f).ToLowerInvariant();
                        if (ClientExeNames.Contains(leaf)) found.Add(f);
                    }
                }
                catch { }
            }

            return found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool IsRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }

        public static void Stop()
        {
            foreach (string n in new[] { "steam", "steamwebhelper", "steamerrorreporter" })
            {
                foreach (Process p in Process.GetProcessesByName(n))
                {
                    try { p.Kill(); } catch { }
                }
            }
            System.Threading.Thread.Sleep(900);
        }

        public static void Start(string root)
        {
            // This app runs elevated; launching through explorer.exe hands Steam
            // back to the normal user token instead of inheriting admin.
            string exe = Path.Combine(root, "steam.exe");
            var psi = new ProcessStartInfo("explorer.exe", "\"" + exe + "\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
    }

    // ----------------------------------------------------------- main window
    internal sealed class MainWindow : Window
    {
        // Window client 440 - shell margin 2x14 - shell border 2 - body padding
        // 2x40. Anything wider silently clips against the right edge.
        private const double ContentWidth = 330;

        private readonly TextBlock _state   = new TextBlock();
        private readonly TextBlock _caption = new TextBlock();
        private readonly Border    _dot     = new Border();
        private readonly Border    _card    = new Border();

        // Unfrozen so the state change can be animated rather than swapped.
        private readonly SolidColorBrush _cardFill   = new SolidColorBrush(Theme.PaleNeutral);
        private readonly SolidColorBrush _cardEdge   = new SolidColorBrush(Theme.EdgeNeutral);
        private readonly SolidColorBrush _accent     = new SolidColorBrush(Theme.InkColor);
        private readonly Border    _action  = new Border();
        private readonly TextBlock _actionText = new TextBlock();
        private readonly Border    _switch  = new Border();
        private readonly Ellipse   _knob    = new Ellipse();
        private readonly TranslateTransform _knobShift = new TranslateTransform();
        private readonly Border    _hairlineHost = new Border();
        private readonly Rectangle _hairline = new Rectangle();
        private readonly TranslateTransform _hairShift = new TranslateTransform();
        private readonly ScaleTransform _actionScale = new ScaleTransform(1, 1);
        private readonly TextBlock _meta = new TextBlock();

        private StackPanel _body;
        private string _root;
        private List<string> _targets = new List<string>();
        private bool _blocked;
        private bool _restart = true;
        private bool _busy;
        private bool _ready;
        private bool _refreshing;
        private int _textGen;

        public MainWindow()
        {
            Title = "Steam Offline";
            Width = 440;
            Height = 506;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = BuildShell();

            MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            Loaded += OnLoaded;
            Activated += OnActivated;
        }

        // ------------------------------------------------------------ layout
        private UIElement BuildShell()
        {
            var shell = new Border();
            shell.Background = Theme.Canvas;
            shell.BorderBrush = Theme.Line;
            shell.BorderThickness = new Thickness(1);
            shell.CornerRadius = new CornerRadius(12);
            shell.Margin = new Thickness(14);

            var shadow = new DropShadowEffect();
            shadow.BlurRadius = 28;
            shadow.ShadowDepth = 2;
            shadow.Direction = 270;
            shadow.Opacity = 0.05;          // ultra-diffuse, barely there
            shadow.Color = Colors.Black;
            shell.Effect = shadow;

            var root = new Grid();
            root.Children.Add(BuildAmbient());
            root.Children.Add(BuildContent());
            shell.Child = root;
            return shell;
        }

        // A single very slow radial drift keeps the flat canvas from reading dead.
        private UIElement BuildAmbient()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
            brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 1));

            var blob = new Ellipse();
            blob.Width = 460;
            blob.Height = 460;
            blob.Fill = brush;
            blob.Opacity = 0.035;
            blob.IsHitTestVisible = false;
            blob.HorizontalAlignment = HorizontalAlignment.Left;
            blob.VerticalAlignment = VerticalAlignment.Top;

            var shift = new TranslateTransform(-120, -40);
            blob.RenderTransform = shift;

            var host = new Grid();
            host.ClipToBounds = true;
            host.IsHitTestVisible = false;
            host.Children.Add(blob);

            Drift(shift, TranslateTransform.XProperty, -120, 180, 29);
            Drift(shift, TranslateTransform.YProperty, -40, 260, 37);
            return host;
        }

        private static void Drift(TranslateTransform t, DependencyProperty prop, double from, double to, double seconds)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds));
            a.AutoReverse = true;
            a.RepeatBehavior = RepeatBehavior.Forever;
            a.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            t.BeginAnimation(prop, a);
        }

        private UIElement BuildContent()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // chrome
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // meta

            grid.Children.Add(BuildChrome());

            _body = new StackPanel();
            _body.Margin = new Thickness(40, 10, 40, 0);
            _body.Children.Add(BuildStateCard());
            _body.Children.Add(BuildHairline());
            _body.Children.Add(BuildAction());
            _body.Children.Add(BuildSwitchRow());
            Grid.SetRow(_body, 1);
            grid.Children.Add(_body);

            var meta = BuildMeta();
            Grid.SetRow(meta, 2);
            grid.Children.Add(meta);

            return grid;
        }

        private UIElement BuildChrome()
        {
            var bar = new Grid();
            bar.Height = 46;
            bar.Margin = new Thickness(18, 0, 10, 0);

            var mark = new TextBlock();
            mark.Text = Theme.Tracked("STEAM OFFLINE");
            mark.FontFamily = Theme.Mono;
            mark.FontSize = 9.5;
            mark.Foreground = Theme.Muted;
            mark.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(mark);

            var close = new TextBlock();
            close.Text = "×";
            close.FontFamily = Theme.Sans;
            close.FontSize = 18;
            close.Foreground = Theme.Muted;
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Padding = new Thickness(12, 2, 12, 6);
            close.Cursor = Cursors.Hand;
            close.MouseEnter += delegate { close.Foreground = Theme.Ink; };
            close.MouseLeave += delegate { close.Foreground = Theme.Muted; };
            close.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Close(); };
            bar.Children.Add(close);

            return bar;
        }

        private static TextBlock Eyebrow(string text)
        {
            var t = new TextBlock();
            t.Text = Theme.Tracked(text);
            t.FontFamily = Theme.Mono;
            t.FontSize = 9.5;
            t.Foreground = Theme.Muted;
            t.Margin = new Thickness(0, 0, 0, 10);
            return t;
        }

        // The pastel wash is the loudest thing in the window on purpose - it is
        // the one piece of information the user opened the app for.
        private UIElement BuildStateCard()
        {
            _card.Width = ContentWidth;
            _card.HorizontalAlignment = HorizontalAlignment.Left;
            _card.Margin = new Thickness(0, 18, 0, 0);
            _card.CornerRadius = new CornerRadius(10);
            _card.Background = _cardFill;
            _card.BorderBrush = _cardEdge;
            _card.BorderThickness = new Thickness(1);
            _card.Padding = new Thickness(22, 18, 22, 20);

            var stack = new StackPanel();
            stack.Children.Add(Eyebrow("NETWORK STATE"));
            stack.Children.Add(BuildStateLine());
            stack.Children.Add(BuildCaption());
            _card.Child = stack;
            return _card;
        }

        private UIElement BuildStateLine()
        {
            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            _dot.Width = 10;
            _dot.Height = 10;
            _dot.CornerRadius = new CornerRadius(5);
            _dot.BorderBrush = _accent;
            _dot.BorderThickness = new Thickness(1.4);
            _dot.Background = _accent;
            _dot.VerticalAlignment = VerticalAlignment.Center;
            _dot.Margin = new Thickness(0, 4, 14, 0);
            row.Children.Add(_dot);

            _state.Text = "—";
            _state.FontFamily = Theme.Serif;
            _state.FontSize = 46;
            _state.Foreground = _accent;
            _state.LineHeight = 50;
            _state.Margin = new Thickness(0, -6, 0, 0);
            row.Children.Add(_state);

            return row;
        }

        private UIElement BuildCaption()
        {
            _caption.Text = "Reading firewall state";
            _caption.FontFamily = Theme.Sans;
            _caption.FontSize = 12.5;
            _caption.Foreground = Theme.Muted;
            _caption.LineHeight = 20;
            _caption.TextWrapping = TextWrapping.Wrap;
            _caption.Margin = new Thickness(0, 10, 0, 0);
            return _caption;
        }

        // Indeterminate sweep, only visible while a firewall write is in flight.
        private UIElement BuildHairline()
        {
            _hairlineHost.Height = 1;
            _hairlineHost.Width = ContentWidth;
            _hairlineHost.Margin = new Thickness(0, 26, 0, 0);
            _hairlineHost.Background = Theme.Line;
            _hairlineHost.ClipToBounds = true;
            _hairlineHost.HorizontalAlignment = HorizontalAlignment.Left;

            _hairline.Height = 1;
            _hairline.Width = 110;
            _hairline.Fill = Theme.Ink;
            _hairline.HorizontalAlignment = HorizontalAlignment.Left;
            _hairline.Opacity = 0;
            _hairline.RenderTransform = _hairShift;

            var host = new Grid();
            host.Children.Add(_hairline);
            _hairlineHost.Child = host;
            return _hairlineHost;
        }

        private UIElement BuildAction()
        {
            _action.Height = 56;
            _action.Width = ContentWidth;
            _action.CornerRadius = new CornerRadius(6);
            _action.Background = Theme.Ink;
            _action.BorderBrush = Theme.Ink;
            _action.BorderThickness = new Thickness(1);
            _action.Margin = new Thickness(0, 26, 0, 0);
            _action.Cursor = Cursors.Hand;
            _action.HorizontalAlignment = HorizontalAlignment.Left;
            _action.RenderTransformOrigin = new Point(0.5, 0.5);
            _action.RenderTransform = _actionScale;

            _actionText.Text = Theme.Tracked("WAIT");
            _actionText.FontFamily = Theme.Sans;
            _actionText.FontSize = 12;
            _actionText.FontWeight = FontWeights.SemiBold;
            _actionText.Foreground = Theme.Surface;
            _actionText.HorizontalAlignment = HorizontalAlignment.Center;
            _actionText.VerticalAlignment = VerticalAlignment.Center;
            _action.Child = _actionText;

            _action.MouseEnter += delegate { if (!_busy) _action.Opacity = 0.86; };
            _action.MouseLeave += delegate { _action.Opacity = 1; };
            _action.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!_busy) AnimateScale(0.98, 90);
            };
            _action.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScale(1.0, 160);
                OnToggle();
            };
            return _action;
        }

        private UIElement BuildSwitchRow()
        {
            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 22, 0, 0);
            row.Cursor = Cursors.Hand;

            _switch.Width = 38;
            _switch.Height = 22;
            _switch.CornerRadius = new CornerRadius(11);
            _switch.BorderBrush = Theme.Line;
            _switch.BorderThickness = new Thickness(1);
            _switch.Background = Theme.Ink;
            _switch.VerticalAlignment = VerticalAlignment.Center;

            _knob.Width = 16;
            _knob.Height = 16;
            _knob.Fill = Theme.Surface;
            _knob.HorizontalAlignment = HorizontalAlignment.Left;
            _knob.VerticalAlignment = VerticalAlignment.Center;
            _knob.Margin = new Thickness(2, 0, 0, 0);
            _knobShift.X = 16;
            _knob.RenderTransform = _knobShift;
            _switch.Child = _knob;
            row.Children.Add(_switch);

            var label = new TextBlock();
            label.Text = "Restart Steam automatically";
            label.FontFamily = Theme.Sans;
            label.FontSize = 12.5;
            label.Foreground = Theme.InkSoft;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(14, 0, 0, 0);
            row.Children.Add(label);

            row.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (_busy) return;
                _restart = !_restart;
                SetSwitch(_restart, true);
            };
            return row;
        }

        private UIElement BuildMeta()
        {
            var wrap = new StackPanel();

            var rule = new Border();
            rule.Height = 1;
            rule.Background = Theme.Line;
            rule.Margin = new Thickness(0, 0, 0, 0);
            wrap.Children.Add(rule);

            var pad = new Border();
            pad.Background = Theme.Paper;
            pad.CornerRadius = new CornerRadius(0, 0, 11, 11);
            pad.Padding = new Thickness(40, 18, 40, 20);

            _meta.FontFamily = Theme.Mono;
            _meta.FontSize = 10;
            _meta.Foreground = Theme.Muted;
            _meta.LineHeight = 17;
            _meta.TextWrapping = TextWrapping.Wrap;
            _meta.Text = "locating steam";
            pad.Child = _meta;

            wrap.Children.Add(pad);
            return wrap;
        }

        // -------------------------------------------------------- animations
        private void AnimateScale(double to, int ms)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _actionScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _actionScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private static void Spline(DependencyObject target, DependencyProperty prop,
                                   double from, double to, int ms, int delayMs)
        {
            var frames = new DoubleAnimationUsingKeyFrames();
            frames.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new SplineDoubleKeyFrame(
                to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Theme.Ease));

            var animatable = target as IAnimatable;
            if (animatable != null) animatable.BeginAnimation(prop, frames);
        }

        // Staggered entry: nothing mounts all at once.
        private void Reveal(FrameworkElement el, int index)
        {
            var shift = new TranslateTransform(0, 12);
            el.RenderTransform = shift;
            el.Opacity = 0;
            int delay = 90 + index * 80;
            Spline(el, OpacityProperty, 0, 1, 600, delay);
            Spline(shift, TranslateTransform.YProperty, 12, 0, 600, delay);
        }

        private void SetSwitch(bool on, bool animate)
        {
            _switch.Background = on ? Theme.Ink : Theme.Surface;
            _knob.Fill = on ? Theme.Surface : Theme.Muted;
            double to = on ? 16 : 0;
            if (!animate) { _knobShift.X = to; return; }

            var frames = new DoubleAnimationUsingKeyFrames();
            frames.KeyFrames.Add(new SplineDoubleKeyFrame(
                to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), Theme.Ease));
            _knobShift.BeginAnimation(TranslateTransform.XProperty, frames);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _action.Opacity = busy ? 0.4 : 1.0;
            _action.Cursor = busy ? Cursors.Wait : Cursors.Hand;

            if (busy)
            {
                // Drop back to neutral while in flight so the pastel never lies
                // about a state that is currently being rewritten.
                Tint(_cardFill, Theme.PaleNeutral, 260);
                Tint(_cardEdge, Theme.EdgeNeutral, 260);
                Tint(_accent, Theme.InkColor, 260);

                _hairline.Opacity = 0.9;
                var sweep = new DoubleAnimation(-110, ContentWidth, TimeSpan.FromMilliseconds(1150));
                sweep.RepeatBehavior = RepeatBehavior.Forever;
                sweep.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
                _hairShift.BeginAnimation(TranslateTransform.XProperty, sweep);
            }
            else
            {
                _hairShift.BeginAnimation(TranslateTransform.XProperty, null);
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                _hairline.BeginAnimation(OpacityProperty, fade);
            }
        }

        // A direct caption write has to invalidate any crossfade still in flight,
        // otherwise the pending callback lands afterwards and restores old text.
        private void SetCaption(string caption)
        {
            _textGen++;
            _caption.BeginAnimation(OpacityProperty, null);
            _caption.Opacity = 1;
            _caption.Text = caption;
        }

        // Crossfade rather than a hard text swap.
        private void SetStateText(string word, string caption)
        {
            int gen = ++_textGen;

            var outFade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            outFade.Completed += delegate
            {
                if (gen != _textGen) return;   // superseded while fading out
                _state.Text = word;
                _caption.Text = caption;
                var inFade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(300));
                inFade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _state.BeginAnimation(OpacityProperty, inFade);
                _caption.BeginAnimation(OpacityProperty, inFade);
            };
            _state.BeginAnimation(OpacityProperty, outFade);
            _caption.BeginAnimation(OpacityProperty, outFade);
        }

        // ------------------------------------------------------------- state
        private static void Tint(SolidColorBrush brush, System.Windows.Media.Color to, int ms)
        {
            var a = new ColorAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }

        private void Paint(bool blocked)
        {
            _blocked = blocked;

            Tint(_cardFill, blocked ? Theme.PaleRed  : Theme.PaleGreen, 420);
            Tint(_cardEdge, blocked ? Theme.EdgeRed  : Theme.EdgeGreen, 420);
            Tint(_accent,   blocked ? Theme.DeepRed  : Theme.DeepGreen, 420);

            if (blocked)
            {
                SetStateText("Offline", "Steam is cut off from the network. Games are unaffected.");
                _action.Background = Theme.Surface;
                _action.BorderBrush = Theme.Ink;
                _actionText.Foreground = Theme.Ink;
                _actionText.Text = Theme.Tracked("GO ONLINE");
            }
            else
            {
                SetStateText("Online", "Steam reaches the network normally.");
                _action.Background = Theme.Ink;
                _action.BorderBrush = Theme.Ink;
                _actionText.Foreground = Theme.Surface;
                _actionText.Text = Theme.Tracked("GO OFFLINE");
            }
        }

        private void Fail(string message)
        {
            Tint(_cardFill, Theme.PaleRed, 300);
            Tint(_cardEdge, Theme.EdgeRed, 300);
            Tint(_accent, Theme.DeepRed, 300);
            _state.Text = "Error";
            _caption.Text = message;
            _action.Opacity = 0.3;
            _action.IsHitTestVisible = false;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _body.Children.Count; i++)
                Reveal((FrameworkElement)_body.Children[i], i);

            SetSwitch(_restart, false);

            try
            {
                string root = await Task.Run(() => Steam.FindRoot());
                List<string> targets = await Task.Run(() => Steam.FindTargets(root));
                bool blocked = await Task.Run(() => Firewall.IsBlocked());

                _root = root;
                _targets = targets;
                _ready = true;

                _meta.Text = root.ToLowerInvariant() + "\n" +
                             targets.Count.ToString(CultureInfo.InvariantCulture) +
                             " client binaries · games left untouched";
                Paint(blocked);
            }
            catch (Exception ex)
            {
                _meta.Text = "no steam installation detected";
                Fail(ex.Message);
            }
        }

        // The rules can also be changed from the CLI scripts or Windows itself,
        // so re-read on focus instead of trusting the last value forever.
        private async void OnActivated(object sender, EventArgs e)
        {
            if (!_ready || _busy || _refreshing) return;
            _refreshing = true;
            try
            {
                bool blocked = await Task.Run(() => Firewall.IsBlocked());
                if (!_busy && blocked != _blocked) Paint(blocked);
            }
            catch { }
            finally { _refreshing = false; }
        }

        private async void OnToggle()
        {
            if (_busy || !_ready) return;
            SetBusy(true);
            string previous = _caption.Text;
            SetCaption(_blocked ? "Removing firewall rules" : "Writing firewall rules");

            try
            {
                bool wasRunning = _restart && Steam.IsRunning();
                bool goBlocked = !_blocked;
                string root = _root;
                List<string> targets = _targets;

                await Task.Run(delegate
                {
                    if (wasRunning) Steam.Stop();
                    if (goBlocked) Firewall.Block(targets); else Firewall.Unblock();
                    if (wasRunning) Steam.Start(root);
                });

                Paint(goBlocked);
            }
            catch (Exception ex)
            {
                SetCaption(previous);
                MessageBox.Show(this, ex.Message, "Steam Offline",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }

    internal static class Entry
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow());
        }
    }
}
