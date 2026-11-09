// DisplaySwap UI: WPF window (XAML loaded at runtime), tray icon, global hotkeys, CLI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("DisplaySwap")]
[assembly: AssemblyProduct("DisplaySwap")]
[assembly: AssemblyDescription("Switch between saved monitor/TV setups, with HDR, colour profiles and sound")]
[assembly: AssemblyVersion("1.3.4.0")]
[assembly: AssemblyFileVersion("1.3.4.0")]

namespace DisplaySwap
{
    public static class Program
    {
        public const string Version = "1.3.4";

        /// <summary>Remember where DisplaySwap.exe lives (HKCU\Software\DisplaySwap\ExePath) so the Playnite
        /// extension can find it wherever the user unzipped it.</summary>
        static void RegisterLocation()
        {
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\DisplaySwap"))
                    k.SetValue("ExePath", Assembly.GetExecutingAssembly().Location);
            } catch { }
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        const string MutexName = "DisplaySwap.SingleInstance.v1";
        const string ShowEventName = "DisplaySwap.ShowWindow.v1";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0) AttachConsole(-1);
            if (!Demo.On) RegisterLocation();
            string apply = null; bool tray = false, remember = false; int updatedFrom = 0;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].ToLowerInvariant();
                if ((a == "--apply" || a == "-a" || a == "/apply") && i + 1 < args.Length) apply = args[++i];
                else if (a == "--tray") tray = true;
                else if (a == "--after-update" && i + 1 < args.Length) int.TryParse(args[++i], out updatedFrom);
                else if (a == "--remember") remember = true;
                else if (a == "--restore") return RestoreFromCommandLine();
                else if (a == "--make-icon" && i + 1 < args.Length) { IconMaker.WriteIco(args[++i]); return 0; }
                else if (a == "--version") { Console.WriteLine("DisplaySwap " + Version); return 0; }
                else if (a == "--check-update")
                {
                    try { var u = Updater.Check(); Console.WriteLine(Updater.IsNewer(u) ? u.Tag + " is available: " + u.PageUrl : "Up to date (latest is " + u.Tag + ")."); return 0; }
                    catch (Exception e) { Console.Error.WriteLine("Couldn't check: " + Updater.Explain(e)); return 1; }
                }
                else if (a == "--list") { foreach (var p in Store.Load().Profiles) Console.WriteLine(p.Name); return 0; }
                else if (a == "--status") { foreach (var d in Displays.Live()) Console.WriteLine("{0} active={1} {2}x{3}@{4:F2} hdr={5} profile={6}", d.Name, d.Active, d.Width, d.Height, d.Hz, d.HdrOn, d.HdrProfile); return 0; }
            }

            if (apply != null) return ApplyFromCommandLine(apply, remember);

            // Started by an update: let the old version close first (it still holds the single-instance lock).
            if (updatedFrom > 0)
            {
                try { Process.GetProcessById(updatedFrom).WaitForExit(15000); } catch { }
                Updater.CleanUp();
            }

            bool first;
            var mutex = new Mutex(true, Demo.On ? MutexName + ".demo" : MutexName, out first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                return 0;
            }
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var ui = new MainUi(Store.Load(), tray && updatedFrom == 0);
            if (updatedFrom > 0) ui.Dispatcher.BeginInvoke(new Action(ui.ShowUpdated));
            var t = new Thread(() => {
                while (true) { showEvent.WaitOne(); ui.Dispatcher.BeginInvoke(new Action(ui.ShowFromTray)); }
            }) { IsBackground = true };
            t.Start();
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        /// <summary>--apply "Name" [--remember]. With --remember the current setup is saved first so
        /// --restore can bring it back (used by the Playnite extension around a game session).
        /// If a saved setup is already waiting (one game started from another), the older one is kept.</summary>
        static int ApplyFromCommandLine(string name, bool remember)
        {
            var store = Store.Load();
            var p = store.Find(name);
            if (p == null) { Log.Write("CLI: no profile named '" + name + "'"); Console.Error.WriteLine("No profile named '" + name + "'"); return 2; }
            if (remember)
            {
                var existing = Store.LoadSession();
                if (existing != null && !Session.IsStranded(existing)) Log.Write("CLI: keeping the setup saved earlier for --restore");
                else
                {
                    try { Store.SaveSnapshot(Switcher.CaptureCurrent("setup before switching")); Log.Write("CLI: saved current setup for --restore"); }
                    catch (Exception e) { Log.Write("CLI: could not save current setup: " + e.Message); }
                }
            }
            try { Switcher.Apply(p, Console.WriteLine, store.Settings); return 0; }
            catch (Exception e) { Log.Write("CLI: " + e.Message); Console.Error.WriteLine(e.Message); return 1; }
        }

        /// <summary>--restore: go back to the setup saved by --apply ... --remember.</summary>
        static int RestoreFromCommandLine()
        {
            var snap = Store.LoadSnapshot();
            if (snap == null) { Log.Write("CLI: --restore, nothing saved"); Console.WriteLine("Nothing to restore."); return 0; }
            try
            {
                Switcher.Apply(snap, Console.WriteLine, Store.Load().Settings);
                Store.DeleteSnapshot();
                return 0;
            }
            catch (Exception e) { Log.Write("CLI: restore failed: " + e.Message); Console.Error.WriteLine(e.Message); return 1; }
        }
    }

    public class MainUi
    {
        readonly Store store;
        readonly Window win;
        public Dispatcher Dispatcher { get { return win.Dispatcher; } }

        // named elements
        StackPanel profileList, displayRows; Canvas preview; TextBox nameBox; TextBlock detailSub, currentText, statusText;
        Ellipse statusDot; Button applyBtn, newBtn, updateBtn, deleteBtn, shortcutBtn; ComboBox hotkeyBox;
        FrameworkElement detail, emptyState; System.Windows.Controls.Primitives.Popup settingsPopup;
        CheckBox optStartup, optTray, optConfirm, optRefresh, optTvOn, optTvOff;
        TextBlock tvStatus, tvPairText; Button tvPairBtn, tvCustomBtn; bool pairing;
        DispatcherTimer strandedTimer; string strandedPromptedFor;
        Border mainPanel; WrapPanel bgSwatches;

        WF.NotifyIcon trayIcon;
        Profile selected;
        List<LiveDisplay> live = new List<LiveDisplay>();
        List<AudioDevice> audioDevs = new List<AudioDevice>();
        AudioDevice currentAudio;
        ComboBox soundBox; TextBlock soundSub;
        bool busy, reallyExit, suppressEvents;
        DispatcherTimer displayChangeTimer;
        HwndSource hwnd;
        readonly Dictionary<int, Profile> hotkeyIds = new Dictionary<int, Profile>();

        static readonly Brush SubBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8)));
        static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)));
        static readonly Brush HdrBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)));
        static readonly Brush GoodBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)));
        static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
        static Brush LineBrush { get { return Theme.Brush("2C313B"); } }
        static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0)));
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        public MainUi(Store store, bool startInTray)
        {
            this.store = store;
            Theme.Apply(CurrentBackground());   // before the window loads, so it starts in the saved colour
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
            using (var r = new StreamReader(s))
                win = (Window)XamlReader.Parse(Theme.Prepare(r.ReadToEnd()));
            win.Icon = IconMaker.WpfIcon();
            Bind();
            BuildTray();

            var helper = new WindowInteropHelper(win);
            helper.EnsureHandle();
            hwnd = HwndSource.FromHwnd(helper.Handle);
            hwnd.AddHook(WndProc);
            DarkTitleBar(helper.Handle);
            ApplyBackground();   // title bar colour needs the window handle

            displayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            displayChangeTimer.Tick += (s, e) => { displayChangeTimer.Stop(); if (!busy) RefreshLive(); };

            // A game session whose launcher closed/crashed before switching back (checked shortly after start, then every 20 s).
            strandedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            strandedTimer.Tick += (s, e) => { strandedTimer.Interval = TimeSpan.FromSeconds(20); CheckStranded(); };
            strandedTimer.Start();

            win.Closing += (s, e) => {
                if (!reallyExit && store.Settings.CloseToTray) { e.Cancel = true; win.Hide(); }
            };
            win.Closed += (s, e) => ExitApp();
            win.Activated += (s, e) => { if (!busy) RefreshLive(); };

            RegisterHotkeys();
            RefreshLive();
            var first = store.Profiles.FirstOrDefault(p => Demo.Select != null && p.Name == Demo.Select) ?? store.Profiles.FirstOrDefault();
            Select(first);
            if (!startInTray) win.Show();
            if (Demo.Tray)   // screenshot mode: open the tray menu by itself, near the top left of the screen
                win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                    var wa = WF.Screen.PrimaryScreen.WorkingArea;
                    trayIcon.ContextMenuStrip.Show(wa.Left + 60, wa.Top + 60);
                }));
        }

        T F<T>(string name) where T : class { return (T)win.FindName(name); }

        void Bind()
        {
            profileList = F<StackPanel>("ProfileList"); displayRows = F<StackPanel>("DisplayRows");
            preview = F<Canvas>("Preview"); nameBox = F<TextBox>("NameBox"); detailSub = F<TextBlock>("DetailSub");
            currentText = F<TextBlock>("CurrentText"); statusText = F<TextBlock>("StatusText"); statusDot = F<Ellipse>("StatusDot");
            applyBtn = F<Button>("ApplyBtn"); newBtn = F<Button>("NewBtn"); updateBtn = F<Button>("UpdateBtn");
            deleteBtn = F<Button>("DeleteBtn"); shortcutBtn = F<Button>("ShortcutBtn"); hotkeyBox = F<ComboBox>("HotkeyBox");
            detail = F<FrameworkElement>("Detail"); emptyState = F<FrameworkElement>("EmptyState");
            settingsPopup = F<System.Windows.Controls.Primitives.Popup>("SettingsPopup");
            optStartup = F<CheckBox>("OptStartup"); optTray = F<CheckBox>("OptTray"); optConfirm = F<CheckBox>("OptConfirm"); optRefresh = F<CheckBox>("OptRefresh");
            optTvOn = F<CheckBox>("OptTvOn"); optTvOff = F<CheckBox>("OptTvOff"); tvStatus = F<TextBlock>("TvStatus"); tvPairText = F<TextBlock>("TvPairText");
            tvPairBtn = F<Button>("TvPairBtn");
            F<TextBlock>("VersionText").Text = "v" + Program.Version;
            tvPairBtn.Click += (s, e) => PairTv();
            tvCustomBtn = F<Button>("TvCustomBtn");
            tvCustomBtn.Click += (s, e) => SetupCustomTv();
            optTvOn.Click += (s, e) => { if (!suppressEvents) { store.Settings.TvTurnOn = optTvOn.IsChecked == true; Save(); } };
            optTvOff.Click += (s, e) => { if (!suppressEvents) { store.Settings.TvTurnOff = optTvOff.IsChecked == true; Save(); } };

            newBtn.Click += (s, e) => NewProfile();
            applyBtn.Click += (s, e) => { if (selected != null) ApplyProfile(selected); };
            updateBtn.Click += (s, e) => OverwriteSelected();
            deleteBtn.Click += (s, e) => DeleteSelected();
            shortcutBtn.Click += (s, e) => MakeShortcut();
            F<Button>("RefreshBtn").Click += (s, e) => { RefreshLive(); SetStatus("Displays re-read.", null); };
            F<Button>("SettingsBtn").Click += (s, e) => {
                suppressEvents = true;
                optStartup.IsChecked = Startup.Enabled; optTray.IsChecked = store.Settings.CloseToTray; optConfirm.IsChecked = store.Settings.ConfirmAfterSwitch; optRefresh.IsChecked = store.Settings.RefreshHdr;
                UpdateTvUi();
                suppressEvents = false;
                settingsPopup.IsOpen = true;
            };
            F<Button>("LogBtn").Click += (s, e) => { if (File.Exists(Store.LogPath)) Process.Start("notepad.exe", "\"" + Store.LogPath + "\""); };
            F<Button>("OpenFolderBtn").Click += (s, e) => { Directory.CreateDirectory(Store.Dir); Process.Start("explorer.exe", "\"" + Store.Dir + "\""); };
            updateCheckBtn = F<Button>("UpdateCheckBtn"); updateCheckText = F<TextBlock>("UpdateCheckText"); updateStatus = F<TextBlock>("UpdateStatus");
            updateCheckBtn.Click += (s, e) => CheckOrInstallUpdate();
            optStartup.Click += (s, e) => { if (!suppressEvents) Startup.Enabled = optStartup.IsChecked == true; };
            optTray.Click += (s, e) => { if (!suppressEvents) { store.Settings.CloseToTray = optTray.IsChecked == true; Save(); } };
            optRefresh.Click += (s, e) => { if (!suppressEvents) { store.Settings.RefreshHdr = optRefresh.IsChecked == true; Save(); } };
            optConfirm.Click += (s, e) => { if (!suppressEvents) { store.Settings.ConfirmAfterSwitch = optConfirm.IsChecked == true; Save(); } };
            mainPanel = F<Border>("MainPanel"); bgSwatches = F<WrapPanel>("BgSwatches");
            FillBgSwatches(); ApplyBackground();

            nameBox.KeyDown += (s, e) => {
                if (e.Key == Key.Enter) { CommitName(); Keyboard.ClearFocus(); win.Focus(); }
                else if (e.Key == Key.Escape) { nameBox.Text = selected != null ? selected.Name : ""; Keyboard.ClearFocus(); }
            };
            nameBox.LostKeyboardFocus += (s, e) => CommitName();

            FillHotkeyBox("");
            // WPF scrolls the selected hotkey to the top of the list, hiding None and "Set your own..." above it.
            // Open at the top instead whenever the selected one is still visible from there.
            // The list stays invisible until it has been scrolled, so the jump never shows.
            hotkeyBox.DropDownOpened += (s, e) => {
                var popup = hotkeyBox.Template.FindName("PART_Popup", hotkeyBox) as System.Windows.Controls.Primitives.Popup;
                var list = popup == null ? null : popup.Child as UIElement;
                if (list != null) list.Opacity = 0;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                    try
                    {
                        var item = hotkeyBox.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, hotkeyBox.SelectedIndex)) as FrameworkElement;
                        DependencyObject up = item;
                        while (up != null && !(up is ScrollViewer)) up = VisualTreeHelper.GetParent(up);
                        var sv = up as ScrollViewer;
                        if (item == null || sv == null) return;
                        // the list scrolls by item (viewport counted in items), or by pixels if item scrolling is off
                        double fromTop = sv.CanContentScroll ? hotkeyBox.SelectedIndex + 1 : item.ActualHeight * (hotkeyBox.SelectedIndex + 1);
                        if (fromTop <= sv.ViewportHeight) { sv.ScrollToTop(); sv.UpdateLayout(); }
                    }
                    finally { if (list != null) list.Opacity = 1; }
                }));
            };
            soundBox = F<ComboBox>("SoundBox"); soundSub = F<TextBlock>("SoundSub");
            soundBox.SelectionChanged += (s, e) => {
                if (suppressEvents || selected == null) return;
                var it = soundBox.SelectedItem as ComboBoxItem;
                var dev = it == null ? null : it.Tag as AudioDevice;
                if (soundBox.SelectedIndex <= 0) { selected.AudioId = null; selected.AudioName = null; }
                else if (dev != null) { selected.AudioId = dev.Id; selected.AudioName = dev.Name; }
                Save(); RenderList(); UpdateSoundSub();
            };
            hotkeyBox.SelectionChanged += (s, e) => {
                if (suppressEvents || selected == null) return;
                if ((string)hotkeyBox.SelectedItem == SetOwnHotkey)
                {
                    // Open the dialog once the dropdown has closed. Our own hotkeys are off meanwhile, so pressing
                    // one of them in the dialog doesn't switch screens.
                    var prof = selected;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => {
                        UnregisterHotkeys();
                        var own = HotkeyDialog.Ask(prof.Name, win);
                        if (own != null) SetHotkey(prof, own);
                        else RegisterHotkeys();
                        if (selected == prof) { suppressEvents = true; FillHotkeyBox(prof.Hotkey); suppressEvents = false; }
                    }));
                    return;
                }
                var picked = hotkeyBox.SelectedItem as string;
                SetHotkey(selected, picked == null || picked == "None" ? "" : picked);
            };
        }

        // ---------------- background colour ----------------
        static readonly string DefaultBackground = "#000000";
        static readonly string[][] BackgroundPresets = {
            new[] { "#000000", "Black (default)" }, new[] { "#111318", "Slate" }, new[] { "#1A1A1A", "Charcoal" }, new[] { "#0D1424", "Navy" },
            new[] { "#160F22", "Purple" }, new[] { "#0C1A14", "Green" }, new[] { "#1E0E10", "Red" },
        };

        static bool TryParseColour(string hex, out Color c)
        {
            c = Colors.Black;
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            uint v;
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v)) return false;
            c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        Color CurrentBackground()
        {
            Color c;
            return TryParseColour(store.Settings.Background, out c) ? c : (Color)ColorConverter.ConvertFromString(DefaultBackground);
        }

        /// <summary>Window and title bar get the colour; the right-hand panel a slightly lighter shade of it.</summary>
        void ApplyBackground()
        {
            ShowBackground(CurrentBackground());
            FillBgSwatches();
        }

        /// <summary>Paints the window in a colour without saving it (also used for the live preview).</summary>
        void ShowBackground(Color c)
        {
            Theme.Apply(c);   // every surface in the app and its dialogs uses these shades
            if (profileList != null) { RenderList(); DrawPreviewAgain(); }   // the layout tiles are drawn with fixed brushes
            if (hwnd != null) { int caption = c.R | (c.G << 8) | (c.B << 16); DwmSetWindowAttribute(hwnd.Handle, 35, ref caption, 4); }
        }

        void SetBackground(string hex)
        {
            store.Settings.Background = hex.Equals(DefaultBackground, StringComparison.OrdinalIgnoreCase) ? null : hex.ToUpperInvariant();
            Save(); ApplyBackground();
        }

        void FillBgSwatches()
        {
            if (bgSwatches == null) return;
            bgSwatches.Children.Clear();
            var cur = CurrentBackground();
            bool presetPicked = false;
            foreach (var pr in BackgroundPresets)
            {
                Color c; TryParseColour(pr[0], out c);
                bool on = c == cur; presetPicked |= on;
                var hex = pr[0];
                bgSwatches.Children.Add(Swatch(new SolidColorBrush(SwatchColour(c)), pr[1], on, () => SetBackground(hex)));
            }
            // custom colour: shows the colour when it's not a preset, otherwise a "+"
            var custom = Swatch(presetPicked ? (Brush)Brushes.Transparent : new SolidColorBrush(SwatchColour(cur)),
                presetPicked ? "Your own colour" : "Your own colour (" + store.Settings.Background + ")", !presetPicked, PickCustomBackground);
            if (presetPicked) custom.Child = new TextBlock { Text = "+", Foreground = SubBrush, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            bgSwatches.Children.Add(custom);
        }

        /// <summary>The backgrounds are all very dark (the text is light), so on screen they'd look alike: the swatch
        /// shows the same colour 4x brighter. Black stays black.</summary>
        static Color SwatchColour(Color c)
        {
            double h, s, v; ColourDialog.ToHsv(c, out h, out s, out v);
            return ColourDialog.FromHsv(h, s, Math.Min(1, v * 4));
        }

        Border Swatch(Brush fill, string tip, bool selectedNow, Action pick)
        {
            var b = new Border {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = fill, Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(2), BorderBrush = AccentBrush, Cursor = Cursors.Hand, ToolTip = tip,
            };
            if (!selectedNow) b.SetResourceReference(Border.BorderBrushProperty, "S4A505C");
            b.MouseLeftButtonUp += (s, e) => pick();
            return b;
        }

        void PickCustomBackground()
        {
            settingsPopup.IsOpen = false;
            var picked = ColourDialog.Ask(CurrentBackground(), win, ShowBackground);   // the window previews it while you pick
            if (picked == null) { ApplyBackground(); return; }                          // cancelled: back to the saved colour
            var c = picked.Value;
            SetBackground(string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
            SetStatus("Background colour changed.", true);
        }

        static readonly string SetOwnHotkey = "Set your own…";

        static IEnumerable<string> HotkeyPresets()
        {
            for (int i = 1; i <= 9; i++) yield return "Ctrl+Alt+" + i;
            for (int i = 1; i <= 12; i++) yield return "Ctrl+Alt+F" + i;
        }

        /// <summary>None, "Set your own...", the profile's own hotkey if it isn't a preset, then the presets.</summary>
        void FillHotkeyBox(string current)
        {
            hotkeyBox.Items.Clear();
            hotkeyBox.Items.Add("None");
            hotkeyBox.Items.Add(SetOwnHotkey);
            var presets = HotkeyPresets().ToList();
            if (!string.IsNullOrEmpty(current) && !presets.Contains(current)) hotkeyBox.Items.Add(current);
            foreach (var hk in presets) hotkeyBox.Items.Add(hk);
            hotkeyBox.SelectedItem = string.IsNullOrEmpty(current) ? "None" : current;
            if (hotkeyBox.SelectedIndex < 0) hotkeyBox.SelectedItem = "None";
        }

        void SetHotkey(Profile p, string hk)
        {
            if (hk != "") foreach (var o in store.Profiles) if (o != p && o.Hotkey == hk) o.Hotkey = "";
            p.Hotkey = hk;
            Save(); RegisterHotkeys(); RenderList();
        }

        // ---------------- state ----------------
        void Save()
        {
            try { store.Save(); } catch (Exception e) { SetStatus("Could not save profiles: " + e.Message, false); }
            RebuildTrayMenu();
        }

        void RefreshLive()
        {
            try { live = Displays.Live(); }
            catch (Exception e) { SetStatus(e.Message, false); return; }
            ReadAudio();
            SyncHdrSupport();
            var act = live.Where(d => d.Active).OrderBy(d => d.X).ToList();
            currentText.Text = act.Count == 0 ? "No active screens" :
                "Now: " + string.Join("   ·   ", act.Select(d =>
                    d.Name + " " + d.Width + "×" + d.Height + " " + Math.Round(d.Hz) + " Hz" +
                    (d.HdrSupported ? (d.HdrOn ? ", HDR on" : ", HDR off") : "") +
                    (d.HdrOn && !string.IsNullOrEmpty(d.HdrProfile) ? " (" + System.IO.Path.GetFileNameWithoutExtension(d.HdrProfile) + ")" : "")))
                + (currentAudio != null ? "   ·   Sound: " + ShortAudio(currentAudio.Name) : "");
            RenderList();
            RenderDetail();
            UpdateTrayTooltip();
        }

        void ReadAudio()
        {
            try {
                // Absent devices: only keep screens' own HDMI/DP audio (e.g. the TV while it's off), not old junk.
                var screens = live.Select(d => d.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                audioDevs = Audio.PlaybackDevices(true)
                    .Where(d => d.Present || screens.Any(n => Audio.BaseName(d.Name).StartsWith(n + " (", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                currentAudio = audioDevs.FirstOrDefault(d => d.IsDefault);
            }
            catch { audioDevs = new List<AudioDevice>(); currentAudio = null; }
        }

        /// <summary>"Living Room TV (NVIDIA High Definition Audio)" -> "Living Room TV"; other names stay whole.</summary>
        static string ShortAudio(string n)
        {
            n = Audio.BaseName(n);
            int i = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (i > 0 && n.IndexOf("High Definition Audio", i, StringComparison.OrdinalIgnoreCase) > 0) return n.Substring(0, i);
            return n;
        }

        /// <summary>Correct saved "HDR supported" flags from what Windows reports for screens that are on now
        /// (a capture taken while a screen is still starting up can get it wrong).</summary>
        void SyncHdrSupport()
        {
            bool changed = false;
            foreach (var p in store.Profiles)
            {
                if (p.Displays == null) continue;
                foreach (var s in p.Displays)
                {
                    var d = live.FirstOrDefault(x => x.Active && Switcher.Same(x.DevicePath, s.DevicePath));
                    if (d == null) continue;
                    // Remember which refresh rates the screen offers at this profile's resolution (for the picker
                    // while the screen is off). SafeRates leaves out a list that may be another screen's.
                    if (d.Width == s.Width && d.Height == s.Height)
                    {
                        var rates = Switcher.SafeRates(d);
                        if (rates.Count > 0 && (s.Rates == null || !rates.SequenceEqual(s.Rates))) { s.Rates = rates; changed = true; }
                    }
                    if (d.HdrSupported == s.HdrSupported) continue;
                    s.HdrSupported = d.HdrSupported;
                    if (!d.HdrSupported) { s.Hdr = "keep"; s.Profile = null; }
                    else if (s.Hdr == "keep" || string.IsNullOrEmpty(s.Hdr)) s.Hdr = d.HdrOn ? "on" : "off";
                    changed = true;
                }
            }
            if (changed) Save();
        }

        Profile ActiveProfile()
        {
            return store.Profiles.FirstOrDefault(p => { try { return Switcher.FullyMatches(p, live, currentAudio); } catch { return false; } });
        }

        void Select(Profile p)
        {
            selected = p;
            RenderList();
            RenderDetail();
        }

        // ---------------- profile list ----------------
        void RenderList()
        {
            profileList.Children.Clear();
            var active = ActiveProfile();
            foreach (var p in store.Profiles)
            {
                var prof = p;
                bool isSel = p == selected;
                var card = new Border {
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 12, 12), Margin = new Thickness(0, 0, 0, 8),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = isSel ? AccentBrush : Brushes.Transparent, Cursor = Cursors.Hand,
                };
                card.SetResourceReference(Border.BackgroundProperty, isSel ? "S262B35" : "S1F232B");
                card.MouseEnter += (s, e) => { if (prof != selected) card.SetResourceReference(Border.BackgroundProperty, "S262B35"); };
                card.MouseLeave += (s, e) => { if (prof != selected) card.SetResourceReference(Border.BackgroundProperty, "S1F232B"); };
                card.MouseLeftButtonUp += (s, e) => { if (prof != selected) Select(prof); };

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var names = p.Displays == null ? "" : string.Join(" + ", p.Displays.Select(d => d.Name));
                left.Children.Add(new TextBlock { Text = names, Foreground = SubBrush, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                var chips = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
                if (p == active) chips.Children.Add(Chip("Active", GoodBrush));
                if (p.Displays != null && p.Displays.Any(d => d.Hdr == "on")) chips.Children.Add(Chip("HDR", HdrBrush));
                if (!string.IsNullOrEmpty(p.Hotkey)) chips.Children.Add(Chip(p.Hotkey, SubBrush));
                if (chips.Children.Count > 0) left.Children.Add(chips);
                g.Children.Add(left);

                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                var mini = new Canvas { Width = 64, Height = 38 };
                DrawLayout(mini, p, true);
                right.Children.Add(mini);
                var play = new Button { Style = (Style)win.FindResource("GhostBtn"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Switch to " + p.Name, IsEnabled = !busy,
                    Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13, Foreground = AccentBrush } };
                play.Click += (s, e) => { ApplyProfile(prof); e.Handled = true; };
                right.Children.Add(play);
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
                card.Child = g;
                profileList.Children.Add(card);
            }
        }

        static Border Chip(string text, Brush color)
        {
            var c = ((SolidColorBrush)color).Color;
            return new Border {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)),
                Child = new TextBlock { Text = text, Foreground = color, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
        }

        // ---------------- detail ----------------
        void RenderDetail()
        {
            bool has = selected != null;
            detail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            emptyState.Visibility = store.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;

            suppressEvents = true;
            if (!nameBox.IsKeyboardFocused) nameBox.Text = selected.Name;
            FillHotkeyBox(selected.Hotkey);
            suppressEvents = false;

            int n = selected.Displays == null ? 0 : selected.Displays.Count;
            bool isActive = selected == ActiveProfile();
            detailSub.Text = (n == 1 ? "1 screen" : n + " screens") + (isActive ? "  ·  active now" : "");
            applyBtn.IsEnabled = !busy; updateBtn.IsEnabled = !busy; newBtn.IsEnabled = !busy;

            preview.Children.Clear();
            preview.Width = double.NaN;
            preview.SizeChanged -= PreviewSized; preview.SizeChanged += PreviewSized;
            if (preview.ActualWidth > 0) DrawLayout(preview, selected, false);

            displayRows.Children.Clear();
            if (selected.Displays == null) return;
            var profiles = Displays.InstalledProfiles();
            foreach (var s in selected.Displays) displayRows.Children.Add(DisplayRow(s, profiles));
            FillSoundBox();
        }

        void FillSoundBox()
        {
            suppressEvents = true;
            soundBox.Items.Clear();
            soundBox.Items.Add(new ComboBoxItem { Content = "Don't change" });
            int sel = 0;
            AudioDevice saved = string.IsNullOrEmpty(selected.AudioName) ? null : Audio.Find(audioDevs, selected.AudioId, selected.AudioName);
            foreach (var d in audioDevs)
            {
                var label = ShortAudio(d.Name) + (d.Active ? "" : d.Present ? "  (unplugged)" : "  (when its screen is on)");
                soundBox.Items.Add(new ComboBoxItem { Content = label, Tag = d, ToolTip = d.Name });
                if (saved != null && d == saved) sel = soundBox.Items.Count - 1;
            }
            if (!string.IsNullOrEmpty(selected.AudioName) && saved == null)
            {
                soundBox.Items.Add(new ComboBoxItem { Content = ShortAudio(selected.AudioName) + "  (not found)",
                    Tag = new AudioDevice { Id = selected.AudioId, Name = selected.AudioName } });
                sel = soundBox.Items.Count - 1;
            }
            soundBox.SelectedIndex = sel;
            suppressEvents = false;
            UpdateSoundSub();
        }

        void UpdateSoundSub()
        {
            soundSub.Text = string.IsNullOrEmpty(selected.AudioName)
                ? "Leaves the Windows sound output as it is"
                : "Set as the Windows default (games, media and calls) when switching";
        }

        void PreviewSized(object sender, SizeChangedEventArgs e)
        {
            preview.Children.Clear();
            if (selected != null) DrawLayout(preview, selected, false);
        }

        FrameworkElement DisplayRow(DisplaySetting s, List<string> iccs)
        {
            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8) };
            row.SetResourceReference(Border.BackgroundProperty, "S1F232B");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Refresh rate, HDR and HDR profile sit to the right of the name, or below it when the window is narrow.
            var ctl = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };   // wraps when narrow
            Grid.SetColumn(ctl, 2); g.Children.Add(ctl);
            row.SizeChanged += (o, e) => {
                // Same width rule for every row (HDR and non-HDR alike) so they all line up: icon + ~200 px
                // name + refresh + HDR + profile pickers.
                bool narrow = row.ActualWidth < 800;
                Grid.SetRow(ctl, narrow ? 1 : 0); Grid.SetColumn(ctl, narrow ? 1 : 2); Grid.SetColumnSpan(ctl, narrow ? 2 : 1);
                ctl.Margin = new Thickness(narrow ? -12 : 0, narrow ? 10 : 0, 0, 0);
            };

            var icon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 22, Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            g.Children.Add(icon);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = s.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            var bits = new List<string> { s.Width + " × " + s.Height };
            if (s.X == 0 && s.Y == 0 && selected.Displays.Count > 1) bits.Add("main screen");
            var ld = live.FirstOrDefault(d => Switcher.Same(d.DevicePath, s.DevicePath));
            if (ld == null) bits.Add("not connected");
            info.Children.Add(new TextBlock { Text = string.Join("  ·  ", bits), Foreground = ld == null ? BadBrush : SubBrush, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(info, 1); g.Children.Add(info);

            ctl.Children.Add(RefreshPicker(s, ld));

            // Screens without HDR get no HDR controls at all.
            if (!s.HdrSupported)
            {
                var none = new TextBlock { Text = "No HDR", Foreground = SubBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 4, 7) };
                ctl.Children.Add(none);
                row.Child = g;
                return row;
            }

            // HDR on/off
            var hdrPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            hdrPanel.Children.Add(new TextBlock { Text = "HDR", Foreground = SubBrush, FontSize = 11, Margin = new Thickness(2, 0, 0, 4) });
            var hdr = new ComboBox { Width = 130 };
            if (s.HdrSupported)
            {
                hdr.Items.Add("On"); hdr.Items.Add("Off"); hdr.Items.Add("Don't change");
                hdr.SelectedIndex = s.Hdr == "on" ? 0 : s.Hdr == "off" ? 1 : 2;
                hdr.SelectionChanged += (o, e) => {
                    s.Hdr = hdr.SelectedIndex == 0 ? "on" : hdr.SelectedIndex == 1 ? "off" : "keep";
                    Save(); RenderList(); DrawPreviewAgain();
                };
            }
            else { hdr.Items.Add("Not supported"); hdr.SelectedIndex = 0; hdr.IsEnabled = false; }
            hdrPanel.Children.Add(hdr);
            ctl.Children.Add(hdrPanel);

            // HDR colour profile
            var profPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            profPanel.Children.Add(new TextBlock { Text = "HDR colour profile", Foreground = SubBrush, FontSize = 11, Margin = new Thickness(2, 0, 0, 4) });
            var prof = new ComboBox { Width = 250 };
            prof.Items.Add("Don't change");
            var items = new List<string>(iccs);
            if (!string.IsNullOrEmpty(s.Profile) && !items.Any(i => Switcher.Same(i, s.Profile))) items.Insert(0, s.Profile);
            // Named profiles first, then the dated Windows HDR Calibration ones, newest first.
            var named = items.Where(i => !i.StartsWith("HDR Calibrated Profile", StringComparison.OrdinalIgnoreCase) && !IsSystemProfile(i)).ToList();
            var dated = items.Where(i => i.StartsWith("HDR Calibrated Profile", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(i => FileTime(i)).ToList();
            foreach (var i in named.Concat(dated)) prof.Items.Add(new ComboBoxItem { Content = DisplayName(i), Tag = i });
            prof.SelectedIndex = 0;
            for (int i = 1; i < prof.Items.Count; i++)
                if (Switcher.Same((string)((ComboBoxItem)prof.Items[i]).Tag, s.Profile)) { prof.SelectedIndex = i; break; }
            prof.ToolTip = "Windows HDR Calibration profile to make default for this screen when switching";
            prof.SelectionChanged += (o, e) => {
                s.Profile = prof.SelectedIndex <= 0 ? "" : (string)((ComboBoxItem)prof.SelectedItem).Tag;
                Save(); RenderList();
            };
            profPanel.Children.Add(prof);
            ctl.Children.Add(profPanel);

            row.Child = g;
            return row;
        }

        /// <summary>Refresh-rate dropdown. Lists what the screen offers at the profile's resolution (read live
        /// when the screen is on, otherwise the list remembered from the last time it was on).</summary>
        FrameworkElement RefreshPicker(DisplaySetting s, LiveDisplay ld)
        {
            var panel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            panel.Children.Add(new TextBlock { Text = "Refresh rate", Foreground = SubBrush, FontSize = 11, Margin = new Thickness(2, 0, 0, 4) });
            var box = new ComboBox { Width = 96 };
            var rates = new List<int>();
            if (ld != null && ld.Active && ld.Width == s.Width && ld.Height == s.Height) rates = Switcher.SafeRates(ld);
            if (rates.Count == 0 && s.Rates != null) rates = new List<int>(s.Rates);
            int current = ShownRate(s, ld);
            if (current > 0 && !rates.Contains(current) && rates.Count > 0 && s.RefreshHz <= 0)
            {
                // Older profile: snap its exact rate to one Windows lists (it writes 59.94 as 59), but only to a
                // close one: a list without it may be out of date, and then the profile's own rate stays.
                int fl = (int)Math.Floor(s.Hz);
                int near = rates.Contains(fl) ? fl : rates.OrderBy(r => Math.Abs(r - s.Hz)).First();
                if (Math.Abs(near - s.Hz) <= 1) current = near;
            }
            if (current > 0 && !rates.Contains(current)) { rates.Add(current); rates.Sort(); }
            foreach (var r in rates) box.Items.Add(new ComboBoxItem { Content = r + " Hz", Tag = r });
            box.SelectedIndex = rates.IndexOf(current);
            if (rates.Count == 0) { box.Items.Add("Unknown"); box.SelectedIndex = 0; box.IsEnabled = false; }
            box.ToolTip = "Refresh rate this screen is set to when switching";
            box.SelectionChanged += (o, e) => {
                if (box.SelectedItem is ComboBoxItem) { s.RefreshHz = (int)((ComboBoxItem)box.SelectedItem).Tag; Save(); RenderList(); DrawPreviewAgain(); }
            };
            panel.Children.Add(box);
            return panel;
        }

        /// <summary>The refresh rate a profile asks for, in whole Hz as Windows lists them.</summary>
        static int ShownRate(DisplaySetting s, LiveDisplay ld)
        {
            if (s.RefreshHz > 0) return s.RefreshHz;
            // Older profiles only have the exact rate from the layout: use Windows' figure if the screen runs it now.
            if (ld != null && ld.Active && ld.Rate > 0 && Math.Abs(ld.Hz - s.Hz) < 0.6) return ld.Rate;
            return (int)Math.Round(s.Hz);
        }

        void DrawPreviewAgain() { preview.Children.Clear(); if (selected != null && preview.ActualWidth > 0) DrawLayout(preview, selected, false); }

        static bool IsTv(string name) { return name != null && name.IndexOf("TV", StringComparison.OrdinalIgnoreCase) >= 0; }
        static bool IsSystemProfile(string f)
        {
            var l = f.ToLowerInvariant();
            return l == "rswop.icm" || l == "srgb color space profile.icm";
        }
        static string DisplayName(string f)
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(f);
            if (n.StartsWith("HDR Calibrated Profile ", StringComparison.OrdinalIgnoreCase))
            {
                var d = FileTime(f);
                return "Calibration " + (d == DateTime.MinValue ? n.Substring(23) : d.ToString("d MMM yyyy, HH:mm"));
            }
            return n;
        }
        static DateTime FileTime(string f)
        {
            try { return File.GetLastWriteTime(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color", f)); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Draw the screens of a profile, scaled to fit the canvas.</summary>
        void DrawLayout(Canvas c, Profile p, bool mini)
        {
            if (p.Displays == null || p.Displays.Count == 0) return;
            double W = mini ? c.Width : c.ActualWidth, H = mini ? c.Height : c.ActualHeight;
            if (double.IsNaN(W) || W <= 0) return;
            double pad = mini ? 2 : 24;
            int minX = p.Displays.Min(d => d.X), minY = p.Displays.Min(d => d.Y);
            int maxX = p.Displays.Max(d => d.X + d.Width), maxY = p.Displays.Max(d => d.Y + d.Height);
            double scale = Math.Min((W - pad * 2) / Math.Max(1, maxX - minX), (H - pad * 2) / Math.Max(1, maxY - minY));
            double offX = (W - (maxX - minX) * scale) / 2, offY = (H - (maxY - minY) * scale) / 2;
            double gap = mini ? 1 : 4;
            foreach (var d in p.Displays)
            {
                bool hdr = d.Hdr == "on";
                bool main = d.X == 0 && d.Y == 0;
                var r = new Border {
                    Width = Math.Max(4, d.Width * scale - gap * 2), Height = Math.Max(4, d.Height * scale - gap * 2),
                    CornerRadius = new CornerRadius(mini ? 2.5 : 8),
                    Background = new LinearGradientBrush(Theme.Shade("2A303C"), Theme.Shade("222731"), 90),
                    BorderBrush = main ? AccentBrush : LineBrush, BorderThickness = new Thickness(mini ? 1 : 1.5),
                };
                if (!mini)
                {
                    var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6) };
                    sp.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    sp.Children.Add(new TextBlock { Text = d.Width + "×" + d.Height + " · " + ShownRate(d, live.FirstOrDefault(x => Switcher.Same(x.DevicePath, d.DevicePath))) + " Hz", Foreground = SubBrush, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6) });
                    var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    if (hdr) row.Children.Add(Chip("HDR", HdrBrush));
                    if (main && p.Displays.Count > 1) row.Children.Add(Chip("Main", AccentBrush));
                    sp.Children.Add(row);
                    r.Child = new Viewbox { Child = sp, StretchDirection = StretchDirection.DownOnly };
                }
                else if (hdr)
                {
                    r.Child = new Border { Height = 2, Background = HdrBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 2), CornerRadius = new CornerRadius(1) };
                }
                Canvas.SetLeft(r, offX + (d.X - minX) * scale + gap);
                Canvas.SetTop(r, offY + (d.Y - minY) * scale + gap);
                c.Children.Add(r);
            }
        }

        // ---------------- actions ----------------
        void NewProfile()
        {
            try
            {
                RefreshLive();
                var act = live.Where(d => d.Active).ToList();
                string name = act.Count == 1 ? (IsTv(act[0].Name) ? "TV only" : "Monitor only") : act.Count == 2 ? "Both screens" : act.Count + " screens";
                string baseName = name; int n = 2;
                while (store.Find(name) != null) name = baseName + " " + n++;
                var p = Switcher.CaptureCurrent(name);
                store.Profiles.Add(p);
                Save(); RegisterHotkeys();
                Select(p);
                SetStatus("Saved '" + name + "'. Click the name to rename it.", true);
                nameBox.Focus(); nameBox.SelectAll();
            }
            catch (Exception e) { SetStatus("Could not read the current setup: " + e.Message, false); }
        }

        void OverwriteSelected()
        {
            if (selected == null) return;
            if (MessageBox.Show(win, "Replace '" + selected.Name + "' with the screens, resolutions, HDR state, colour profiles and sound output Windows is using right now?",
                "DisplaySwap", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try { Switcher.RefreshFromCurrent(selected); Save(); RefreshLive(); SetStatus("'" + selected.Name + "' updated from the current setup.", true); }
            catch (Exception e) { SetStatus(e.Message, false); }
        }

        void DeleteSelected()
        {
            if (selected == null) return;
            if (MessageBox.Show(win, "Delete '" + selected.Name + "'?", "DisplaySwap", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            int idx = store.Profiles.IndexOf(selected);
            store.Profiles.Remove(selected);
            Save(); RegisterHotkeys();
            Select(store.Profiles.Count == 0 ? null : store.Profiles[Math.Min(idx, store.Profiles.Count - 1)]);
        }

        void CommitName()
        {
            if (selected == null || suppressEvents) return;
            var n = (nameBox.Text ?? "").Trim();
            if (n == selected.Name) return;
            if (n.Length == 0 || store.Profiles.Any(p => p != selected && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus(n.Length == 0 ? "A profile needs a name." : "There's already a profile called '" + n + "'.", false);
                nameBox.Text = selected.Name; return;
            }
            selected.Name = n;
            Save(); RenderList();
        }

        void MakeShortcut()
        {
            if (selected == null) return;
            try
            {
                var exe = Assembly.GetExecutingAssembly().Location;
                var safe = string.Concat(selected.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                var lnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Switch to " + safe + ".lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var t = sc.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { "--apply \"" + selected.Name + "\"" });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { System.IO.Path.GetDirectoryName(exe) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "DisplaySwap: switch to " + selected.Name });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                SetStatus("Desktop shortcut created: " + System.IO.Path.GetFileName(lnk) + " (works in Steam/Playnite too).", true);
            }
            catch (Exception e) { SetStatus("Could not create shortcut: " + e.Message, false); }
        }

        public void ApplyProfile(Profile p)
        {
            if (busy) return;
            if (Demo.On) { SetStatus("Switched to '" + p.Name + "'.", true); return; }   // screenshot mode: never switch
            busy = true;
            RenderList(); RenderDetail();
            SetStatus("Switching to '" + p.Name + "'...", null);
            Profile previous = null;
            try { previous = Switcher.CaptureCurrent("(previous setup)"); } catch { }
            bool confirm = store.Settings.ConfirmAfterSwitch && previous != null;
            var settings = store.Settings;
            var th = new Thread(() => {
                string err = null; bool layoutUpdated = false;
                try { layoutUpdated = Switcher.Apply(p, m => Dispatcher.BeginInvoke(new Action(() => SetStatus(m, null))), settings); }
                catch (Exception e) { err = e.Message; Log.Write("[" + p.Name + "] FAILED: " + e.Message); }
                Dispatcher.BeginInvoke(new Action(() => {
                    busy = false;
                    if (layoutUpdated && store.Profiles.Contains(p)) Save();   // new refresh rate folded into the layout
                    RefreshLive();
                    if (err != null) { SetStatus("Switch to '" + p.Name + "' had a problem: " + err, false); Balloon("Switch had a problem", err, true); return; }
                    SetStatus("Switched to '" + p.Name + "'.", true);
                    if (!win.IsVisible) Balloon("DisplaySwap", "Switched to " + p.Name, false);
                    if (confirm && !ConfirmDialog.Ask(store.Settings.ConfirmSeconds))
                    {
                        previous.Name = "previous setup";
                        ApplyProfile(previous);
                    }
                }));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void SetStatus(string text, bool? ok)
        {
            statusText.Text = text;
            statusDot.Fill = ok == null ? (busy ? HdrBrush : SubBrush) : ok.Value ? GoodBrush : BadBrush;
        }

        // ---------------- stranded game session ----------------
        void CheckStranded()
        {
            if (busy) return;
            SessionSnapshot s;
            try { s = Store.LoadSession(); } catch { return; }
            if (s == null || !Session.IsStranded(s)) return;
            if (s.SavedAt == strandedPromptedFor) return;
            strandedPromptedFor = s.SavedAt;
            try {
                if (Switcher.FullyMatches(s.Setup, Displays.Live(), currentAudio))
                { Store.DeleteSnapshot(); Log.Write("Stranded session: already back on the saved setup, cleared it"); return; }
            } catch { }
            var who = string.IsNullOrEmpty(s.OwnerName) ? "the launcher" : s.OwnerName.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase) ? "Playnite" : s.OwnerName;
            var names = s.Setup.Displays == null ? "" : string.Join(" + ", s.Setup.Displays.Select(d => d.Name));
            Log.Write("Stranded session found (" + who + " is gone), asking to switch back");
            bool back = AskDialog.Ask("Switch back to your previous setup?",
                "A game switched your screens, but " + who + " closed before it could switch them back.\n\nPrevious setup: " + names,
                "Switch back", "Stay like this");
            Store.DeleteSnapshot();
            if (back) { s.Setup.Name = "previous setup"; ApplyProfile(s.Setup); }
            else Log.Write("Stranded session: user chose to stay");
        }

        // ---------------- TV power ----------------
        void UpdateTvUi()
        {
            var st = store.Settings;
            var d = TvPower.For(st.TvBrand);
            tvStatus.Text = pairing ? "Pairing... follow the prompt on your TV." :
                !st.TvPaired ? "Not set up. Supports LG, Samsung, Sony and Roku TVs on your network, or your own on/off commands for any other TV." :
                d.Brand == "custom" ? "Using your own on/off commands" :
                "Paired with " + d.Label + " " + (st.TvName ?? "TV") + " (" + st.TvIp + ")";
            tvPairText.Text = st.TvPaired && d.Brand != "custom" ? "Pair again" : "Find & pair TV";
            tvPairBtn.IsEnabled = tvCustomBtn.IsEnabled = !pairing;
            optTvOn.IsEnabled = optTvOff.IsEnabled = st.TvPaired;
            suppressEvents = true;
            optTvOn.IsChecked = st.TvTurnOn; optTvOff.IsChecked = st.TvTurnOff;
            suppressEvents = false;
        }

        /// <summary>The only place that scans the network: runs when the user clicks "Find & pair TV".</summary>
        void PairTv()
        {
            if (pairing) return;
            pairing = true; settingsPopup.IsOpen = false; UpdateTvUi();
            SetStatus("Looking for TVs on your network...", null);
            Log.Write("TV: scanning the network for TVs (user clicked Find & pair TV)");
            var th = new Thread(() =>
            {
                string err = null, key = null; TvInfo pick = null;
                try
                {
                    var tvs = TvDiscovery.FindAll();
                    Log.Write("TV: found " + (tvs.Count == 0 ? "nothing" : string.Join(", ", tvs.Select(x => x.Label + " " + x.Ip))));
                    if (tvs.Count == 0) throw new DisplayException("No LG, Samsung, Sony or Roku TV found. Is the TV on and on the same network? For other TVs use Custom commands.");
                    // Always let the user confirm, even with one TV found: the one that answered may be another TV
                    // in the house, and pairing makes a prompt pop up on whichever TV is picked.
                    int idx = -1;
                    Dispatcher.Invoke(new Action(() => idx = ChooseDialog.Choose("Which TV is connected to this PC?",
                        (tvs.Count == 1 ? "This TV answered on your network." : tvs.Count + " TVs answered on your network.") +
                        " Pick the one plugged into this PC; a pairing prompt will appear on that TV. If yours isn't listed, make sure it's on and press Cancel.",
                        tvs.Select(x => x.Label + "   (" + x.Ip + ")").ToList())));
                    if (idx < 0) throw new DisplayException("Pairing cancelled");
                    pick = tvs[idx];
                    var p = pick;
                    Log.Write("TV: user picked " + pick.Label + " at " + pick.Ip);
                    var drv = TvPower.For(pick.Brand);
                    var cur = store.Settings;
                    // Same TV as already paired and the saved pairing still works: keep it, no prompt on the TV.
                    if (cur.TvPaired && cur.TvIp == pick.Ip && TvPower.For(cur.TvBrand).Brand == drv.Brand && drv.PairingWorks(cur))
                    {
                        key = cur.TvKey;
                        Log.Write("TV: saved pairing still works, kept it");
                    }
                    else
                    {
                    Dispatcher.BeginInvoke(new Action(() => SetStatus("Pairing with " + p.Label + " at " + p.Ip + ". Accept the prompt on the TV if one appears...", null)));
                    key = drv.Pair(pick, (title, body) => {
                        string[] r = null;
                        Dispatcher.Invoke(new Action(() => r = InputDialog.Ask(title, body, new[] { "Key" }, new[] { "" })));
                        return r == null ? null : r[0];
                    });
                    }
                }
                catch (Exception e) { err = e.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    pairing = false;
                    if (err == null)
                    {
                        var st = store.Settings;
                        st.TvBrand = pick.Brand; st.TvIp = pick.Ip; st.TvMac = pick.Mac ?? st.TvMac; st.TvName = pick.Name; st.TvKey = key ?? "";
                        if (!PickTvScreen(pick.Brand)) { UpdateTvUi(); return; }
                        Save();
                        Log.Write("TV: paired with " + pick.Label + " at " + pick.Ip + " mac " + pick.Mac);
                        SetStatus("Paired with " + pick.Label + ". Turn on the TV power options in Settings.", true);
                    }
                    else { Log.Write("TV pairing failed: " + err); SetStatus("TV pairing failed: " + err, false); }
                    UpdateTvUi();
                }));
            }) { IsBackground = true };
            th.Start();
        }

        /// <summary>Your own commands for TVs DisplaySwap can't talk to (Home Assistant, IR blaster, smart plug...).</summary>
        void SetupCustomTv()
        {
            settingsPopup.IsOpen = false;
            var st = store.Settings;
            bool wasCustom = st.TvBrand == "custom";
            var r = InputDialog.Ask("Custom TV commands",
                "For any TV: enter what turns it on and off. Use a web address (\"POST https://...\" for a POST, " +
                "e.g. a Home Assistant webhook) or a command line (runs hidden). Leave one empty to skip it.",
                new[] { "Turn on", "Turn off" }, new[] { wasCustom ? st.TvOnCommand ?? "" : "", wasCustom ? st.TvOffCommand ?? "" : "" });
            if (r == null) return;
            if (r[0].Trim().Length == 0 && r[1].Trim().Length == 0) { SetStatus("No commands entered, nothing changed.", null); return; }
            st.TvBrand = "custom"; st.TvOnCommand = r[0].Trim(); st.TvOffCommand = r[1].Trim();
            st.TvIp = null; st.TvKey = null; st.TvName = null;
            if (!PickTvScreen("custom")) { UpdateTvUi(); return; }
            Save(); UpdateTvUi();
            SetStatus("Custom TV commands saved. Turn on the TV power options in Settings.", true);
        }

        /// <summary>Work out which screen is the TV (by the brand's monitor ID), or ask.</summary>
        bool PickTvScreen(string brand)
        {
            var codes = brand == "lg" ? new[] { "GSM" } : brand == "samsung" ? new[] { "SAM", "SEC" } : brand == "sony" ? new[] { "SNY" } :
                        brand == "roku" ? new[] { "TCL", "HEC", "HIS", "SHP", "PHL", "ONN", "ELE", "INS" } : new string[0];
            var match = live.Where(d => d.DevicePath != null && codes.Any(c => d.DevicePath.IndexOf("#" + c, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (match.Count == 1) { store.Settings.TvDevicePath = match[0].DevicePath; return true; }
            var all = live.Where(d => d.DevicePath != null).ToList();
            int idx = ChooseDialog.Choose("Which screen is the TV?", "DisplaySwap turns the TV on and off around the profiles that use this screen.",
                all.Select(d => d.Name + (d.Active ? "" : "   (not on right now)")).ToList());
            if (idx < 0) { SetStatus("TV setup cancelled.", null); return false; }
            store.Settings.TvDevicePath = all[idx].DevicePath;
            return true;
        }

        // ---------------- tray ----------------
        void BuildTray()
        {
            trayIcon = new WF.NotifyIcon { Icon = IconMaker.TrayIcon(), Text = "DisplaySwap", Visible = true };
            trayIcon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
            RebuildTrayMenu();
        }

        void RebuildTrayMenu()
        {
            if (trayIcon == null) return;
            var m = trayIcon.ContextMenuStrip;
            if (m == null)
            {
                m = new WF.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkMenuRenderer(), Font = new SD.Font("Segoe UI", 9.5f) };
                m.ForeColor = SD.Color.FromArgb(0xE8, 0xEA, 0xF0);
                m.Opening += (s, e) => {
                    var bg = Theme.Shade("20242C"); m.BackColor = SD.Color.FromArgb(bg.R, bg.G, bg.B);   // follows the background colour
                    try { live = Displays.Live(); } catch { } ReadAudio(); PopulateTray(m);
                };
                trayIcon.ContextMenuStrip = m;
            }
            PopulateTray(m);
        }

        void PopulateTray(WF.ContextMenuStrip m)
        {
            m.Items.Clear();
            m.Items.Add(new WF.ToolStripLabel("Switch to") { ForeColor = SD.Color.FromArgb(0x94, 0x9B, 0xA8) });
            var active = ActiveProfile();
            foreach (var p in store.Profiles)
            {
                var prof = p;
                var it = new WF.ToolStripMenuItem(p.Name) { Checked = p == active, ShortcutKeyDisplayString = p.Hotkey ?? "" };
                it.ForeColor = m.ForeColor; it.Enabled = !busy;
                it.Click += (s, e) => ApplyProfile(prof);
                m.Items.Add(it);
            }
            if (store.Profiles.Count == 0) m.Items.Add(new WF.ToolStripLabel("(no profiles yet)") { ForeColor = SD.Color.Gray });
            m.Items.Add(new WF.ToolStripSeparator());
            var open = new WF.ToolStripMenuItem("Open DisplaySwap") { ForeColor = m.ForeColor };
            open.Click += (s, e) => ShowFromTray();
            m.Items.Add(open);
            var exit = new WF.ToolStripMenuItem("Exit") { ForeColor = m.ForeColor };
            exit.Click += (s, e) => { reallyExit = true; win.Close(); };
            m.Items.Add(exit);
        }

        void UpdateTrayTooltip()
        {
            if (trayIcon == null) return;
            var a = ActiveProfile();
            var t = "DisplaySwap" + (a != null ? " - " + a.Name : "");
            trayIcon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            RebuildTrayMenu();
        }

        void Balloon(string title, string text, bool error)
        {
            if (trayIcon == null) return;
            trayIcon.ShowBalloonTip(3000, title, text, error ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
        }

        public void ShowFromTray()
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate(); win.Topmost = true; win.Topmost = false;
            RefreshLive();
        }

        // ---------------- updates ----------------
        Button updateCheckBtn; TextBlock updateCheckText, updateStatus;
        UpdateInfo pendingUpdate; bool updating;

        void ShowUpdateStatus(string text, string link)
        {
            updateStatus.Inlines.Clear();
            updateStatus.Inlines.Add(new System.Windows.Documents.Run(text));
            if (link != null)
            {
                var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("What's new"));
                h.Foreground = (Brush)win.FindResource("Accent");
                h.Click += (s, e) => { try { Process.Start(link); } catch { } };
                updateStatus.Inlines.Add(new System.Windows.Documents.Run(" "));
                updateStatus.Inlines.Add(h);
            }
            updateStatus.Visibility = Visibility.Visible;
        }

        /// <summary>First click asks GitHub for the latest version; if it is newer the button turns into
        /// "Install vX", and that click downloads it, swaps the exe and restarts DisplaySwap.</summary>
        void CheckOrInstallUpdate()
        {
            if (updating) return;
            if (pendingUpdate != null) { InstallUpdate(pendingUpdate); return; }
            updating = true; updateCheckBtn.IsEnabled = false;
            ShowUpdateStatus("Checking GitHub...", null);
            ThreadPool.QueueUserWorkItem(_ => {
                UpdateInfo u = null; Exception err = null;
                try { u = Updater.Check(); } catch (Exception e) { err = e; }
                win.Dispatcher.BeginInvoke(new Action(() => {
                    updating = false; updateCheckBtn.IsEnabled = true;
                    if (err != null) { ShowUpdateStatus("Couldn't check for updates: " + Updater.Explain(err), null); return; }
                    if (!Updater.IsNewer(u)) { ShowUpdateStatus("You have the latest version (v" + Program.Version + ").", null); return; }
                    pendingUpdate = u;
                    updateCheckText.Text = "Install " + u.Tag;
                    ShowUpdateStatus(u.Tag + " is available. DisplaySwap restarts to finish; your profiles and settings are kept.", u.PageUrl);
                }));
            });
        }

        void InstallUpdate(UpdateInfo u)
        {
            if (busy) { ShowUpdateStatus("Wait for the switch to finish, then try again.", null); return; }
            if (!Updater.CanInstallHere)
            {
                ShowUpdateStatus("DisplaySwap's folder can't be written to, so it can't update itself. Download " + u.Tag + " from GitHub instead.", u.PageUrl);
                return;
            }
            updating = true; updateCheckBtn.IsEnabled = false;
            Action<string> progress = msg => win.Dispatcher.BeginInvoke(new Action(() => ShowUpdateStatus(msg, null)));
            ThreadPool.QueueUserWorkItem(_ => {
                string exe = null; Exception err = null;
                try { exe = Updater.Install(u, progress); } catch (Exception e) { err = e; }
                win.Dispatcher.BeginInvoke(new Action(() => {
                    if (err != null)
                    {
                        updating = false; updateCheckBtn.IsEnabled = true;
                        Log.Write("Update to " + u.Tag + " failed: " + err.Message);
                        ShowUpdateStatus("Update failed: " + Updater.Explain(err) + " Nothing was changed.", u.PageUrl);
                        return;
                    }
                    Log.Write("Updated to " + u.Tag + ", restarting");
                    try { Process.Start(exe, "--after-update " + Process.GetCurrentProcess().Id); }
                    catch (Exception e)
                    {
                        updating = false;
                        ShowUpdateStatus(u.Tag + " is installed but couldn't start (" + e.Message + "). Close DisplaySwap and open it again.", null);
                        return;
                    }
                    reallyExit = true;
                    win.Close();
                }));
            });
        }

        /// <summary>Shown by the new version after an update restarted it.</summary>
        public void ShowUpdated()
        {
            ShowFromTray();
            SetStatus("Updated to v" + Program.Version + ".", true);
        }

        void ExitApp()
        {
            UnregisterHotkeys();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
            Application.Current.Shutdown();
        }

        // ---------------- hotkeys / window messages ----------------
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void UnregisterHotkeys()
        {
            if (hwnd == null) return;
            foreach (var id in hotkeyIds.Keys) UnregisterHotKey(hwnd.Handle, id);
            hotkeyIds.Clear();
        }

        void RegisterHotkeys()
        {
            if (hwnd == null || Demo.On) return;
            UnregisterHotkeys();
            int id = 0x5100;
            var failed = new List<string>();
            foreach (var p in store.Profiles)
            {
                uint mods, vk;
                if (!ParseHotkey(p.Hotkey, out mods, out vk)) continue;
                if (RegisterHotKey(hwnd.Handle, id, mods | 0x4000 /*NOREPEAT*/, vk)) hotkeyIds[id++] = p;
                else failed.Add(p.Hotkey);
            }
            if (failed.Count > 0) SetStatus("Hotkey already used by another app: " + string.Join(", ", failed), false);
        }

        static bool ParseHotkey(string hk, out uint mods, out uint vk)
        {
            return HotkeyText.Parse(hk, out mods, out vk);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == 0x0312) // WM_HOTKEY
            {
                Profile p;
                if (hotkeyIds.TryGetValue(wp.ToInt32(), out p)) { ApplyProfile(p); handled = true; }
            }
            else if (msg == 0x007E) // WM_DISPLAYCHANGE
            {
                displayChangeTimer.Stop(); displayChangeTimer.Start();
            }
            return IntPtr.Zero;
        }

        static void DarkTitleBar(IntPtr h)
        {
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, 4);            // immersive dark mode
            int backdrop = 2; DwmSetWindowAttribute(h, 38, ref backdrop, 4); // Mica (Win11), ignored elsewhere
            int caption = 0x00181311; DwmSetWindowAttribute(h, 35, ref caption, 4); // caption colour #111318 (BGR)
        }
    }

    /// <summary>The background colour setting. Every grey surface of the dark design (window, panels, cards, inputs,
    /// borders, popups) is the default background plus a fixed step, so a new background shifts them all with it.
    /// Brushes live in the Application resources as "S" + the default hex (e.g. "S1F232B"), which the XAML and the
    /// dialogs reference as DynamicResource, so a change shows at once.</summary>
    public static class Theme
    {
        static readonly Color Base = Color.FromRgb(0x11, 0x13, 0x18);
        static readonly string[] Surfaces = { "111318", "14161B", "181B21", "1F232B", "20242C", "222731", "262B35", "2A303C",
                                              "2C313B", "2E3440", "343A46", "3A404C", "4A505C", "4A5260" };
        static Color bg = Base;

        static Color Hex(string h) { uint v = Convert.ToUInt32(h, 16); return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v); }
        static byte Step(byte b, int d) { return (byte)Math.Max(0, Math.Min(255, b + d)); }

        /// <summary>The default-design colour hex moved onto background "on".</summary>
        public static Color Shade(string hex, Color on)
        {
            var d = Hex(hex);
            return Color.FromRgb(Step(on.R, d.R - Base.R), Step(on.G, d.G - Base.G), Step(on.B, d.B - Base.B));
        }
        public static Color Shade(string hex) { return Shade(hex, bg); }

        public static Brush Brush(string hex)
        {
            var b = new SolidColorBrush(Shade(hex)); b.Freeze(); return b;
        }

        public static void Apply(Color background)
        {
            bg = background;
            if (Application.Current == null) return;
            foreach (var h in Surfaces) Application.Current.Resources["S" + h] = Brush(h);
        }

        /// <summary>Points the window XAML's surface colours at the theme brushes.</summary>
        public static string Prepare(string xaml)
        {
            xaml = xaml.Replace("{StaticResource Panel}", "{DynamicResource S181B21}").Replace("{StaticResource Card}", "{DynamicResource S1F232B}")
                       .Replace("{StaticResource CardHover}", "{DynamicResource S262B35}").Replace("{StaticResource Line}", "{DynamicResource S2C313B}");
            return System.Text.RegularExpressions.Regex.Replace(xaml, @"(\s[A-Za-z.:]+)=""#([0-9A-Fa-f]{6})""", m => {
                var attr = m.Groups[1].Value.Trim(); var hex = m.Groups[2].Value.ToUpperInvariant();
                if (attr == "Color" || Array.IndexOf(Surfaces, hex) < 0) return m.Value;   // only brush attributes of surface colours
                return m.Groups[1].Value + "=\"{DynamicResource S" + hex + "}\"";
            });
        }
    }

    /// <summary>Hotkey text such as "Ctrl+Shift+T" to and from RegisterHotKey modifiers and virtual-key codes.</summary>
    public static class HotkeyText
    {
        public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

        static readonly Dictionary<string, uint> Named = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) {
            { "Space", 0x20 }, { "PageUp", 0x21 }, { "PageDown", 0x22 }, { "End", 0x23 }, { "Home", 0x24 },
            { "Left", 0x25 }, { "Up", 0x26 }, { "Right", 0x27 }, { "Down", 0x28 }, { "Insert", 0x2D }, { "Delete", 0x2E },
            { "Pause", 0x13 },
        };

        /// <summary>Name of a key that can be used in a hotkey, or null.</summary>
        public static string KeyName(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();          // A-Z
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();          // 0-9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);          // F1-F24
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);            // numpad 0-9
            foreach (var kv in Named) if (kv.Value == vk) return kv.Key;
            return null;
        }

        public static string Format(uint mods, uint vk)
        {
            var name = KeyName(vk);
            if (name == null) return null;
            var parts = new List<string>();
            if ((mods & Ctrl) != 0) parts.Add("Ctrl");
            if ((mods & Alt) != 0) parts.Add("Alt");
            if ((mods & Shift) != 0) parts.Add("Shift");
            if ((mods & Win) != 0) parts.Add("Win");
            parts.Add(name);
            return string.Join("+", parts);
        }

        public static bool Parse(string hk, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(hk)) return false;
            foreach (var part in hk.Split('+'))
            {
                var t = part.Trim();
                uint named;
                if (t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= Ctrl;
                else if (t.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Alt;
                else if (t.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Shift;
                else if (t.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= Win;
                else if (t.Length == 1 && char.IsLetterOrDigit(t[0])) vk = char.ToUpperInvariant(t[0]);
                else if (Named.TryGetValue(t, out named)) vk = named;
                else if (t.StartsWith("Num", StringComparison.OrdinalIgnoreCase)) { int n; if (int.TryParse(t.Substring(3), out n) && n >= 0 && n <= 9) vk = (uint)(0x60 + n); }
                else if (t.StartsWith("F", StringComparison.OrdinalIgnoreCase)) { int n; if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) vk = (uint)(0x70 + n - 1); }
            }
            return vk != 0 && mods != 0;
        }
    }

    /// <summary>"Press the keys you want" dialog. Returns the hotkey text, or null if cancelled.</summary>
    public static class HotkeyDialog
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static string Ask(string profileName, Window owner)
        {
            string result = null, pending = null;
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Title = "DisplaySwap",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var sub = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8));
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Hotkey for '" + profileName + "'", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock {
                Text = "Press the keys you want, for example Ctrl+Shift+T. Use Ctrl, Alt or Win with it, so normal keys in games keep working. Esc cancels.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = sub, FontSize = 13, Margin = new Thickness(0, 8, 0, 16) });
            var keys = new TextBlock { Text = "Waiting for keys…", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = sub,
                HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new Border { Background = Theme.Brush("14161B"), CornerRadius = new CornerRadius(10),
                BorderBrush = Theme.Brush("343A46"), BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 14, 16, 14), Child = keys });
            var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 10, 0, 0), Foreground = sub };
            sp.Children.Add(note);
            var ok = new Button { Content = "Save", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsEnabled = false, Focusable = false };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(22, 8, 22, 8), Focusable = false };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel); sp.Children.Add(btns);
            if (owner != null)   // the main window's dark button styles
            {
                var accent = owner.TryFindResource("AccentBtn") as Style; var plain = owner.TryFindResource("Btn") as Style;
                if (accent != null) ok.Style = accent;
                if (plain != null) cancel.Style = plain;
            }
            w.Content = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp, MinWidth = 420 };

            var bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
            var good = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
            w.PreviewKeyDown += (s, e) => {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                bool alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool winKey = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
                bool anyMod = ctrl || alt || shift || winKey;
                if (key == Key.Escape && !anyMod) { w.Close(); return; }
                if (key == Key.Enter && !anyMod) { if (pending != null) { result = pending; w.Close(); } return; }
                uint mods = (ctrl ? HotkeyText.Ctrl : 0) | (alt ? HotkeyText.Alt : 0) | (shift ? HotkeyText.Shift : 0) | (winKey ? HotkeyText.Win : 0);
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
                {
                    if (pending == null) { keys.Text = HotkeyText.Format(mods, 0x41).Replace("+A", "+…"); keys.Foreground = sub; }
                    return;
                }
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                var text = HotkeyText.Format(mods, vk);
                pending = null; ok.IsEnabled = false;
                if (text == null) { keys.Text = "Waiting for keys…"; keys.Foreground = sub; note.Foreground = bad; note.Text = "That key can't be used. Try a letter, number or F key."; return; }
                keys.Text = text;
                if (!ctrl && !alt && !winKey) { keys.Foreground = bad; note.Foreground = bad; note.Text = "Add Ctrl, Alt or Win, so the key still works normally everywhere else."; return; }
                // Check that Windows lets us have it (Windows itself and other apps reserve some combos).
                var h = new WindowInteropHelper(w).Handle;
                if (!RegisterHotKey(h, 0x5FFF, mods | 0x4000, vk)) { keys.Foreground = bad; note.Foreground = bad; note.Text = text + " is already used by Windows or another app. Try a different one."; return; }
                UnregisterHotKey(h, 0x5FFF);
                keys.Foreground = Brushes.White; note.Foreground = good; note.Text = "Press Enter or Save to use it.";
                pending = text; ok.IsEnabled = true;
            };
            ok.Click += (s, e) => { result = pending; w.Close(); };
            cancel.Click += (s, e) => w.Close();
            w.Loaded += (s, e) => { w.Activate(); w.Focus(); };
            w.ShowDialog();
            return result;
        }
    }

    /// <summary>Colour picker: hue/saturation wheel, brightness bar and a hex box. Returns the colour, or null if
    /// cancelled. <paramref name="preview"/> gets every colour while picking. Colours too light for the app's light
    /// text can't be saved.</summary>
    public static class ColourDialog
    {
        const double Wheel = 220, MaxLight = 0.35;

        public static Color? Ask(Color start, Window owner, Action<Color> preview)
        {
            Color? result = null;
            double h, s, v; ToHsv(start, out h, out s, out v);
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Title = "DisplaySwap",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var sub = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8));
            var line = Theme.Brush("343A46");

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Background colour", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock { Text = "Pick a colour on the wheel, then set how bright it is. Keep it dark: the text is light.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Foreground = sub, FontSize = 13, Margin = new Thickness(0, 8, 0, 16) });

            // wheel (drawn once at full brightness, at 2x for sharp edges on high-DPI screens)
            var wheelHost = new Grid { Width = Wheel, Height = Wheel, Background = Brushes.Transparent, Cursor = Cursors.Cross, HorizontalAlignment = HorizontalAlignment.Center };
            wheelHost.Children.Add(new Image { Source = DrawWheel((int)(Wheel * 2)), Width = Wheel, Height = Wheel });
            var marks = new Canvas { IsHitTestVisible = false };
            var marker = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5 };
            marks.Children.Add(marker); wheelHost.Children.Add(marks);
            sp.Children.Add(wheelHost);

            // brightness bar
            sp.Children.Add(new TextBlock { Text = "Brightness", Foreground = sub, FontSize = 12, Margin = new Thickness(0, 16, 0, 6) });
            var barGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            var bar = new Grid { Height = 20, Width = Wheel + 80, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = barGrad, BorderBrush = line, BorderThickness = new Thickness(1) });
            var barMarks = new Canvas { IsHitTestVisible = false };
            var thumb = new Ellipse { Width = 20, Height = 20, Stroke = Brushes.White, StrokeThickness = 2.5 };
            barMarks.Children.Add(thumb); bar.Children.Add(barMarks);
            sp.Children.Add(bar);

            // preview + hex
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var swatch = new Border { Width = 44, Height = 32, CornerRadius = new CornerRadius(7), BorderBrush = line, BorderThickness = new Thickness(1) };
            row.Children.Add(swatch);
            var hex = new TextBox { Width = 110, Padding = new Thickness(8, 5, 8, 5), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 7,
                Background = Theme.Brush("14161B"), Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = line, BorderThickness = new Thickness(1) };
            Grid.SetColumn(hex, 1); row.Children.Add(hex);
            sp.Children.Add(row);
            var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) };
            sp.Children.Add(note);

            var ok = new Button { Content = "Save", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(22, 8, 22, 8), IsCancel = true };
            if (owner != null)
            {
                var accent = owner.TryFindResource("AccentBtn") as Style; var plain = owner.TryFindResource("Btn") as Style;
                if (accent != null) ok.Style = accent;
                if (plain != null) cancel.Style = plain;
            }
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel); sp.Children.Add(btns);
            var frame = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp };
            w.Content = frame;

            bool fromHex = false;
            Color current = start;
            Action update = () => {
                current = FromHsv(h, s, v);
                double r = s * Wheel / 2, a = h * Math.PI / 180;
                Canvas.SetLeft(marker, Wheel / 2 + r * Math.Cos(a) - 8); Canvas.SetTop(marker, Wheel / 2 - r * Math.Sin(a) - 8);
                marker.Fill = new SolidColorBrush(FromHsv(h, s, 1));
                barGrad.GradientStops.Clear();
                barGrad.GradientStops.Add(new GradientStop(Colors.Black, 0));
                barGrad.GradientStops.Add(new GradientStop(FromHsv(h, s, 1), 1));
                Canvas.SetLeft(thumb, v * (bar.Width - 20)); thumb.Fill = new SolidColorBrush(current);
                swatch.Background = new SolidColorBrush(current);
                if (!fromHex) hex.Text = string.Format("#{0:X2}{1:X2}{2:X2}", current.R, current.G, current.B);
                bool tooLight = Light(current) > MaxLight;
                note.Text = tooLight ? "Too light for the light text. Turn the brightness down." : "";
                ok.IsEnabled = !tooLight;
                if (!tooLight)
                {
                    // the dialog follows the colour too, a step lighter like the app's cards (default #111318 -> #1F232B)
                    frame.Background = new SolidColorBrush(Theme.Shade("1F232B", current));
                    if (preview != null) preview(current);
                }
            };

            Action<Point> pickWheel = p => {
                double dx = p.X - Wheel / 2, dy = Wheel / 2 - p.Y;
                h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                s = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (Wheel / 2));
                update();
            };
            wheelHost.MouseLeftButtonDown += (o, e) => { wheelHost.CaptureMouse(); pickWheel(e.GetPosition(wheelHost)); };
            wheelHost.MouseMove += (o, e) => { if (wheelHost.IsMouseCaptured) pickWheel(e.GetPosition(wheelHost)); };
            wheelHost.MouseLeftButtonUp += (o, e) => wheelHost.ReleaseMouseCapture();

            Action<Point> pickBar = p => { v = Math.Max(0, Math.Min(1, (p.X - 10) / (bar.Width - 20))); update(); };
            bar.MouseLeftButtonDown += (o, e) => { bar.CaptureMouse(); pickBar(e.GetPosition(bar)); };
            bar.MouseMove += (o, e) => { if (bar.IsMouseCaptured) pickBar(e.GetPosition(bar)); };
            bar.MouseLeftButtonUp += (o, e) => bar.ReleaseMouseCapture();

            hex.TextChanged += (o, e) => {
                if (!hex.IsKeyboardFocused) return;   // only while typing
                var t = hex.Text.Trim().TrimStart('#');
                uint val;
                if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out val)) return;
                ToHsv(Color.FromRgb((byte)(val >> 16), (byte)(val >> 8), (byte)val), out h, out s, out v);
                fromHex = true; update(); fromHex = false;
            };

            ok.Click += (o, e) => { result = current; w.Close(); };
            cancel.Click += (o, e) => w.Close();
            w.Loaded += (o, e) => { update(); w.Activate(); };
            w.ShowDialog();
            return result;
        }

        static double Light(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }

        static BitmapSource DrawWheel(int size)
        {
            var px = new byte[size * size * 4];
            double c = size / 2.0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - c, dy = c - (y + 0.5), d = Math.Sqrt(dx * dx + dy * dy) / c;
                    if (d > 1 + 1.0 / c) continue;
                    double alpha = Math.Max(0, Math.Min(1, (1 - d) * c + 0.5));   // soft edge
                    var col = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, Math.Min(1, d), 1);
                    int i = (y * size + x) * 4;   // premultiplied BGRA
                    px[i] = (byte)(col.B * alpha); px[i + 1] = (byte)(col.G * alpha); px[i + 2] = (byte)(col.R * alpha); px[i + 3] = (byte)(255 * alpha);
                }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
            bmp.Freeze();
            return bmp;
        }

        public static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        public static void ToHsv(Color col, out double h, out double s, out double v)
        {
            double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
    }

    /// <summary>Dark dialog with labelled text boxes. Returns the values, or null if cancelled.</summary>
    public static class InputDialog
    {
        public static string[] Ask(string title, string body, string[] labels, string[] values)
        {
            string[] result = null;
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true, Title = "DisplaySwap", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            var sub = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8));
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = sub, FontSize = 13, Margin = new Thickness(0, 8, 0, 14) });
            var boxes = new List<TextBox>();
            for (int i = 0; i < labels.Length; i++)
            {
                sp.Children.Add(new TextBlock { Text = labels[i], Foreground = sub, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
                var tb = new TextBox { Text = values[i] ?? "", Width = 460, Padding = new Thickness(8, 6, 8, 6), FontSize = 13,
                    Background = Theme.Brush("14161B"), Foreground = Brushes.White, CaretBrush = Brushes.White,
                    BorderBrush = Theme.Brush("343A46"), BorderThickness = new Thickness(1) };
                boxes.Add(tb); sp.Children.Add(tb);
            }
            var ok = new Button { Content = "Save", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(22, 8, 22, 8), IsCancel = true };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel); sp.Children.Add(btns);
            w.Content = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp };
            ok.Click += (s, e) => { result = boxes.Select(x => x.Text).ToArray(); w.Close(); };
            cancel.Click += (s, e) => w.Close();
            w.Loaded += (s, e) => { w.Activate(); if (boxes.Count > 0) boxes[0].Focus(); };
            w.ShowDialog();
            return result;
        }
    }

    /// <summary>Dark dialog with one button per choice. Returns the index, or -1 if cancelled.</summary>
    public static class ChooseDialog
    {
        public static int Choose(string title, string body, System.Collections.Generic.List<string> options)
        {
            int result = -1;
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true, Title = "DisplaySwap", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8)), FontSize = 13, Margin = new Thickness(0, 8, 0, 16) });
            for (int i = 0; i < options.Count; i++)
            {
                int k = i;
                var btn = new Button { Content = options[i], Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
                btn.Click += (s, e) => { result = k; w.Close(); };
                sp.Children.Add(btn);
            }
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(22, 8, 22, 8), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            cancel.Click += (s, e) => w.Close();
            sp.Children.Add(cancel);
            w.Content = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp, MinWidth = 400 };
            w.Loaded += (s, e) => w.Activate();
            w.ShowDialog();
            return result;
        }
    }

    /// <summary>Simple dark yes/no dialog.</summary>
    public static class AskDialog
    {
        public static bool Ask(string title, string body, string yes, string no)
        {
            bool result = false;
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true, Title = "DisplaySwap", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            var t = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            var b = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8)), FontSize = 13, Margin = new Thickness(0, 8, 0, 20) };
            var yesBtn = new Button { Content = yes, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var noBtn = new Button { Content = no, Padding = new Thickness(22, 8, 22, 8), IsCancel = true };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(yesBtn); btns.Children.Add(noBtn);
            var sp = new StackPanel(); sp.Children.Add(t); sp.Children.Add(b); sp.Children.Add(btns);
            w.Content = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp, MinWidth = 400 };
            yesBtn.Click += (s, e) => { result = true; w.Close(); };
            noBtn.Click += (s, e) => w.Close();
            w.Loaded += (s, e) => { w.Activate(); yesBtn.Focus(); };
            w.ShowDialog();
            return result;
        }
    }

    public static class ConfirmDialog
    {
        public static bool Ask(int seconds)
        {
            bool keep = false;
            var w = new Window {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true, Title = "DisplaySwap", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            var title = new TextBlock { Text = "Keep this display setup?", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            var sub = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA8)), FontSize = 13, Margin = new Thickness(0, 6, 0, 18) };
            var keepBtn = new Button { Content = "Keep", Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var revertBtn = new Button { Content = "Go back", Padding = new Thickness(22, 8, 22, 8), IsCancel = true };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(keepBtn); btns.Children.Add(revertBtn);
            var sp = new StackPanel(); sp.Children.Add(title); sp.Children.Add(sub); sp.Children.Add(btns);
            w.Content = new Border { Background = Theme.Brush("1F232B"), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 22),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), BorderThickness = new Thickness(1.5), Child = sp, MinWidth = 380 };
            int left = seconds;
            Action upd = () => sub.Text = "Going back to the previous setup in " + left + " s";
            upd();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) => { left--; upd(); if (left <= 0) { t.Stop(); w.Close(); } };
            keepBtn.Click += (s, e) => { keep = true; w.Close(); };
            revertBtn.Click += (s, e) => w.Close();
            w.Loaded += (s, e) => { t.Start(); w.Activate(); keepBtn.Focus(); };
            w.ShowDialog();
            t.Stop();
            return keep;
        }
    }

    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool Enabled
        {
            get {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("DisplaySwap") != null;
            }
            set {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("DisplaySwap", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue("DisplaySwap", false);
                }
            }
        }
    }

    class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.ForeColor; base.OnRenderItemText(e); }
        // Active profile: a blue dot in the check column, so every name lines up.
        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle; float d = 7f;
            e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SD.SolidBrush(SD.Color.FromArgb(0x5B, 0x8C, 0xFF)))
                e.Graphics.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }
        class DarkColors : WF.ProfessionalColorTable
        {
            // the same shades as the app's popups, so the menu follows the background colour setting
            static SD.Color T(string hex) { var c = Theme.Shade(hex); return SD.Color.FromArgb(c.R, c.G, c.B); }
            static SD.Color Bg { get { return T("20242C"); } }
            static SD.Color Hi { get { return T("2E3440"); } }
            static SD.Color Ln { get { return T("343A46"); } }
            public override SD.Color ToolStripDropDownBackground { get { return Bg; } }
            public override SD.Color MenuBorder { get { return Ln; } }
            public override SD.Color MenuItemBorder { get { return Hi; } }
            public override SD.Color MenuItemSelected { get { return Hi; } }
            public override SD.Color MenuItemSelectedGradientBegin { get { return Hi; } }
            public override SD.Color MenuItemSelectedGradientEnd { get { return Hi; } }
            public override SD.Color SeparatorDark { get { return Ln; } }
            public override SD.Color SeparatorLight { get { return Ln; } }
            public override SD.Color ImageMarginGradientBegin { get { return Bg; } }
            public override SD.Color ImageMarginGradientMiddle { get { return Bg; } }
            public override SD.Color ImageMarginGradientEnd { get { return Bg; } }
        }
    }

    /// <summary>Draws the app icon (blue rounded square, white screen glyph).</summary>
    public static class IconMaker
    {
        static SD.Bitmap Render(int size)
        {
            var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
                float r = size * 0.22f, s = size - 1;
                using (var path = new SD.Drawing2D.GraphicsPath())
                {
                    path.AddArc(0, 0, r * 2, r * 2, 180, 90); path.AddArc(s - r * 2, 0, r * 2, r * 2, 270, 90);
                    path.AddArc(s - r * 2, s - r * 2, r * 2, r * 2, 0, 90); path.AddArc(0, s - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    using (var br = new SD.Drawing2D.LinearGradientBrush(new SD.PointF(0, 0), new SD.PointF(0, size), SD.Color.FromArgb(0x6E, 0x9B, 0xFF), SD.Color.FromArgb(0x42, 0x6F, 0xF0)))
                        g.FillPath(br, path);
                }
                // screen glyph drawn with shapes so it doesn't depend on icon fonts
                float w = size * 0.62f, h = size * 0.40f, x = (size - w) / 2, y = size * 0.22f, t = Math.Max(1.2f, size * 0.07f);
                using (var pen = new SD.Pen(SD.Color.White, t) { LineJoin = SD.Drawing2D.LineJoin.Round })
                {
                    g.DrawRectangle(pen, x, y, w, h);
                    g.DrawLine(pen, size / 2f, y + h, size / 2f, y + h + size * 0.12f);
                    g.DrawLine(pen, size * 0.34f, y + h + size * 0.14f, size * 0.66f, y + h + size * 0.14f);
                }
                using (var hdr = new SD.SolidBrush(SD.Color.FromArgb(0xF5, 0xB9, 0x42)))
                    g.FillRectangle(hdr, x + t, y + h - t * 1.6f, w * 0.45f, t * 0.9f);
            }
            return bmp;
        }

        static byte[] Png(int size)
        {
            using (var b = Render(size)) using (var ms = new MemoryStream()) { b.Save(ms, SD.Imaging.ImageFormat.Png); return ms.ToArray(); }
        }

        /// <summary>Multi-size PNG-compressed .ico.</summary>
        public static byte[] IcoBytes()
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64, 256 };
            var pngs = sizes.Select(x => Png(x)).ToList();
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
                return ms.ToArray();
            }
        }

        public static void WriteIco(string path) { File.WriteAllBytes(path, IcoBytes()); }

        public static SD.Icon TrayIcon()
        {
            using (var ms = new MemoryStream(IcoBytes())) return new SD.Icon(ms, WF.SystemInformation.SmallIconSize);
        }

        public static ImageSource WpfIcon()
        {
            var ms = new MemoryStream(IcoBytes());
            return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
    }
}
