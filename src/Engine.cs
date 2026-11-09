// Profile storage and the switching sequence (layout -> HDR on/off -> HDR colour profile).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.Serialization.Json;

namespace DisplaySwap
{
    public class Settings
    {
        public bool CloseToTray = true;
        public bool ConfirmAfterSwitch = false;
        public bool RefreshHdr = false;   // turn HDR off/on after a switch so Windows reloads the calibration
        public int ConfirmSeconds = 15;
        public string Background;        // window background "#RRGGBB"; empty = the default (black)

        // LG TV power (optional). Filled in by "Find & pair TV".
        public bool TvTurnOn = false;    // wake the TV before switching to a profile that uses it
        public bool TvTurnOff = false;   // turn it off after switching away from it
        public string TvBrand;           // "lg", "samsung", "sony", "roku", "custom" (empty = lg, from before brands)
        public string TvIp, TvMac, TvKey, TvName, TvDevicePath;
        public string TvOnCommand, TvOffCommand;   // custom: URL ("POST https://..." allowed) or command line
        public bool TvPaired
        {
            get
            {
                var b = string.IsNullOrEmpty(TvBrand) ? "lg" : TvBrand;
                if (b == "custom") return !string.IsNullOrEmpty(TvOnCommand) || !string.IsNullOrEmpty(TvOffCommand);
                if (b == "roku") return !string.IsNullOrEmpty(TvIp);
                return !string.IsNullOrEmpty(TvIp) && !string.IsNullOrEmpty(TvKey);
            }
        }
    }

    /// <summary>Setup saved before a game session, plus who started the session (to spot sessions that
    /// never switched back because Playnite closed or crashed).</summary>
    public class SessionSnapshot
    {
        public Profile Setup;
        public int OwnerPid;
        public string OwnerName;
        public long OwnerStartTicks;
        public string SavedAt;
    }

    public class Store
    {
        public List<Profile> Profiles = new List<Profile>();
        public Settings Settings = new Settings();

        // Portable: profiles.json and the log live next to DisplaySwap.exe. Falls back to %APPDATA%\DisplaySwap
        // only if the exe's folder can't be written to.
        static string dir;
        public static string Dir
        {
            get
            {
                if (dir != null) return dir;
                var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                try {
                    var probe = Path.Combine(exeDir, ".write-test");
                    File.WriteAllText(probe, ""); File.Delete(probe);
                    dir = exeDir;
                } catch {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplaySwap");
                }
                return dir;
            }
        }
        public static string FilePath { get { return Path.Combine(Dir, "profiles.json"); } }
        public static string LogPath { get { return Path.Combine(Dir, "DisplaySwap.log"); } }
        static string SnapshotPath { get { return Path.Combine(Dir, "previous-setup.json"); } }

        // Setup saved by "--apply X --remember", brought back by "--restore".
        public static bool HasSnapshot { get { return File.Exists(SnapshotPath); } }
        public static void SaveSnapshot(Profile p)
        {
            var snap = new SessionSnapshot { Setup = p, SavedAt = DateTime.Now.ToString("o") };
            try {
                var parent = Session.ParentProcess();
                if (parent != null) { snap.OwnerPid = parent.Id; snap.OwnerName = parent.ProcessName; snap.OwnerStartTicks = parent.StartTime.Ticks; }
            } catch { }
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SnapshotPath, ToJson(snap), Encoding.UTF8);
        }
        public static void DeleteSnapshot() { try { File.Delete(SnapshotPath); } catch { } }
        public static Profile LoadSnapshot() { var s = LoadSession(); return s == null ? null : s.Setup; }
        public static SessionSnapshot LoadSession()
        {
            try { return HasSnapshot ? FromJson<SessionSnapshot>(File.ReadAllText(SnapshotPath, Encoding.UTF8)) : null; }
            catch (Exception e) { Log.Write("Could not read previous-setup.json: " + e.Message); return null; }
        }

        // DataContractJsonSerializer, not JavaScriptSerializer: the latter starts the ASP.NET runtime on first
        // use, which loads a native ASP.NET DLL that can take seconds (made every CLI call, i.e. every Playnite
        // game switch, 5 s slower and timed out the extension's --list).
        static string ToJson<T>(T o)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream()) { ser.WriteObject(ms, o); return Pretty(Encoding.UTF8.GetString(ms.ToArray())); }
        }
        static T FromJson<T>(string s)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes((s ?? "").TrimStart('﻿')))) return (T)ser.ReadObject(ms);
        }

        public static Store Load()
        {
            try {
                if (File.Exists(FilePath)) {
                    var s = FromJson<Store>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) {
                        if (s.Profiles == null) s.Profiles = new List<Profile>();
                        if (s.Settings == null) s.Settings = new Settings();
                        // this serializer skips field initialisers, so fix values missing from older files
                        if (s.Settings.ConfirmSeconds <= 0) s.Settings.ConfirmSeconds = 15;
                        return s;
                    }
                }
            } catch (Exception e) { Log.Write("Could not read profiles.json: " + e.Message); }
            return new Store();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, ToJson(this), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak"); else File.Move(tmp, FilePath);
        }

        public Profile Find(string name)
        {
            return Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Minimal JSON indenter so the file is hand-editable.
        static string Pretty(string json)
        {
            var sb = new StringBuilder(); int ind = 0; bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (str)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);   // escaped char, incl. \" and \\
                    else if (c == '"') str = false;
                    continue;
                }
                if (c == '"') { str = true; sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c).Append('\n').Append(' ', ++ind * 2); }
                else if (c == '}' || c == ']') { sb.Append('\n').Append(' ', --ind * 2).Append(c); }
                else if (c == ',') { sb.Append(c).Append('\n').Append(' ', ind * 2); }
                else if (c == ':') sb.Append(": ");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public static class Log
    {
        static readonly object gate = new object();
        public static void Write(string msg)
        {
            lock (gate) {
                try {
                    Directory.CreateDirectory(Store.Dir);
                    var fi = new FileInfo(Store.LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(Store.LogPath);
                    File.AppendAllText(Store.LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
                } catch { }
            }
        }
    }

    public static class Session
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PBI { public IntPtr ExitStatus, Peb, Affinity, BasePriority, UniqueId, ParentId; }
        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PBI info, int size, out int ret);

        public static System.Diagnostics.Process ParentProcess()
        {
            var pbi = new PBI(); int ret;
            using (var me = System.Diagnostics.Process.GetCurrentProcess())
                if (NtQueryInformationProcess(me.Handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out ret) != 0) return null;
            try { return System.Diagnostics.Process.GetProcessById(pbi.ParentId.ToInt32()); } catch { return null; }
        }

        /// <summary>True when the program that started the session (Playnite) is gone, so nobody will switch back.</summary>
        public static bool IsStranded(SessionSnapshot s)
        {
            if (s == null || s.Setup == null) return false;
            if (s.OwnerPid == 0) return true;
            try {
                using (var p = System.Diagnostics.Process.GetProcessById(s.OwnerPid))
                    return p.HasExited || p.StartTime.Ticks != s.OwnerStartTicks;   // pid reused by another program
            } catch { return true; }
        }
    }

    public static class Switcher
    {
        /// <summary>Build a profile from whatever Windows is showing right now.</summary>
        public static Profile CaptureCurrent(string name)
        {
            var p = new Profile { Name = name, Hotkey = "" };
            RefreshFromCurrent(p);
            return p;
        }

        /// <summary>Replace the profile's layout and display settings with the current state.</summary>
        public static void RefreshFromCurrent(Profile p)
        {
            p.Layout = Displays.Capture();
            try { var def = Audio.Default(); if (def != null) { p.AudioId = def.Id; p.AudioName = def.Name; } } catch { }
            var old = p.Displays ?? new List<DisplaySetting>();
            p.Displays = new List<DisplaySetting>();
            foreach (var d in Displays.Live().Where(x => x.Active).OrderBy(x => x.X).ThenBy(x => x.Y))
            {
                var prev = old.FirstOrDefault(o => Same(o.DevicePath, d.DevicePath));
                p.Displays.Add(new DisplaySetting {
                    DevicePath = d.DevicePath, Name = d.Name,
                    Width = d.Width, Height = d.Height, X = d.X, Y = d.Y, Hz = Math.Round(d.Hz, 2),
                    RefreshHz = d.Rate, Rates = SafeRates(d),
                    HdrSupported = d.HdrSupported,
                    Hdr = d.HdrSupported ? (d.HdrOn ? "on" : "off") : "keep",
                    Profile = !d.HdrSupported ? null : (d.HdrProfile ?? (prev != null ? prev.Profile : null)),
                });
            }
        }

        /// <summary>Refresh rates an active screen offers at its current resolution. Windows lists them per
        /// graphics-card output, and screens can take turns on one output, so right after a switch the list can
        /// still be the previous screen's. It is only trusted when it has the rate this screen runs at now
        /// (which Windows reports per screen); otherwise the result is empty.</summary>
        public static List<int> SafeRates(LiveDisplay d)
        {
            List<int> rates;
            try { rates = Displays.RefreshRates(d.GdiName, d.Width, d.Height); } catch { return new List<int>(); }
            if (d.Hz > 0 && !rates.Any(r => Math.Abs(r - d.Hz) <= 1)) return new List<int>();
            return rates;
        }

        /// <summary>True when an active screen runs at the refresh rate the profile asks for (or the profile doesn't say).</summary>
        public static bool RateMatches(DisplaySetting s, LiveDisplay d)
        {
            return s.RefreshHz <= 0 || d.Rate <= 0 || d.Rate == s.RefreshHz;
        }

        public static bool SameAudio(AudioDevice d, Profile p)
        {
            return Same(d.Id, p.AudioId) || Same(Audio.BaseName(d.Name), Audio.BaseName(p.AudioName));
        }

        public static bool Same(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }

        /// <summary>True when the active displays, sizes, positions and refresh rates already match.</summary>
        public static bool LayoutMatches(Profile p, List<LiveDisplay> live)
        {
            var act = live.Where(d => d.Active).ToList();
            if (p.Displays == null || act.Count != p.Displays.Count) return false;
            foreach (var s in p.Displays)
            {
                var d = act.FirstOrDefault(x => Same(x.DevicePath, s.DevicePath));
                if (d == null || d.Width != s.Width || d.Height != s.Height || d.X != s.X || d.Y != s.Y) return false;
                // A picked refresh rate is set by its own step (no need to re-apply the whole layout for it).
                if (s.RefreshHz <= 0 && s.Hz > 0 && Math.Abs(d.Hz - s.Hz) > 0.6) return false;
            }
            return true;
        }

        /// <summary>True when layout, HDR state, HDR colour profiles and (when known) sound output all match.</summary>
        public static bool FullyMatches(Profile p, List<LiveDisplay> live, AudioDevice currentAudio)
        {
            if (!LayoutMatches(p, live)) return false;
            if (currentAudio != null && !string.IsNullOrEmpty(p.AudioName) && !SameAudio(currentAudio, p)) return false;
            foreach (var s in p.Displays)
            {
                var d = live.First(x => x.Active && Same(x.DevicePath, s.DevicePath));
                if (!RateMatches(s, d)) return false;
                if (d.HdrSupported && s.Hdr == "on" && !d.HdrOn) return false;
                if (d.HdrSupported && s.Hdr == "off" && d.HdrOn) return false;
                if (d.HdrSupported && !string.IsNullOrEmpty(s.Profile) && !Same(s.Profile, d.HdrProfile)) return false;
            }
            return true;
        }

        /// <summary>Switch to a profile. Throws DisplayException with a readable message on failure.
        /// Returns true when the profile's saved layout was updated (a new refresh rate got folded in), so
        /// the caller should save it.</summary>
        public static bool Apply(Profile p, Action<string> status, Settings settings)
        {
            if (Demo.On) { if (status != null) status("Screenshot mode: not switching."); return false; }
            bool layoutUpdated = false;
            if (settings == null) settings = new Settings();
            bool refreshHdr = settings.RefreshHdr;
            bool somethingChanged = false;
            Action<string> log = m => { Log.Write("[" + p.Name + "] " + m); if (status != null) status(m); };
            if (p.Layout == null || p.Displays == null || p.Displays.Count == 0) throw new DisplayException("Profile has no saved layout.");
            log("Switching to '" + p.Name + "'...");

            // LG TV power: is the TV part of this profile, and is it on screen right now?
            bool tvKnown = settings.TvPaired && !string.IsNullOrEmpty(settings.TvDevicePath);
            bool profileUsesTv = tvKnown && p.Displays.Any(d => Same(d.DevicePath, settings.TvDevicePath));
            var live = Displays.Live();
            bool tvWasActive = tvKnown && live.Any(d => d.Active && Same(d.DevicePath, settings.TvDevicePath));
            if (profileUsesTv && settings.TvTurnOn)
            {
                try {
                    TvPower.EnsureOn(settings, log, 25000, () => Displays.WaitForAvailable(settings.TvDevicePath, 0));
                    if (!Displays.WaitForAvailable(settings.TvDevicePath, 15000)) log("Warning: Windows doesn't see the TV yet.");
                    live = Displays.Live();
                } catch (Exception e) { log("TV power: " + e.Message); }
            }

            if (LayoutMatches(p, live)) log("Layout already correct.");
            else
            {
                Displays.ApplyLayout(p.Layout, log);
                somethingChanged = true;
                var want = p.Displays.Select(d => d.DevicePath).ToList();
                if (!Displays.WaitForActive(want, 10000)) log("Warning: Windows has not finished switching after 10 s.");
                Thread.Sleep(800); // let the driver/DWM settle before touching HDR
            }

            // Refresh rate picked in DisplaySwap (the saved layout may still hold the rate it was saved with).
            bool rateChanged = false;
            live = Displays.Live();
            foreach (var s in p.Displays)
            {
                var d = live.FirstOrDefault(x => x.Active && Same(x.DevicePath, s.DevicePath));
                if (d == null || RateMatches(s, d)) continue;
                log(s.Name + ": refresh rate " + d.Rate + " -> " + s.RefreshHz + " Hz");
                try { Displays.SetRefreshRate(d.GdiName, s.RefreshHz, false); rateChanged = true; }
                catch (Exception e) { log(s.Name + ": " + e.Message); }   // reported by the final check
            }
            if (rateChanged)
            {
                somethingChanged = true;
                Thread.Sleep(1000);
                // Fold the new rate into the saved layout so the next switch sets it in one go.
                live = Displays.Live();
                if (LayoutMatches(p, live) && p.Displays.All(s => RateMatches(s, live.First(x => x.Active && Same(x.DevicePath, s.DevicePath)))))
                {
                    try {
                        p.Layout = Displays.Capture();
                        foreach (var s in p.Displays) s.Hz = Math.Round(live.First(x => x.Active && Same(x.DevicePath, s.DevicePath)).Hz, 2);
                        layoutUpdated = true;
                    } catch { }
                }
            }

            // HDR on/off
            bool hdrChanged = false;
            live = Displays.Live();
            foreach (var s in p.Displays)
            {
                var d = live.FirstOrDefault(x => x.Active && Same(x.DevicePath, s.DevicePath));
                if (d == null) { log(s.Name + ": not active, skipped."); continue; }
                if (!d.HdrSupported || s.Hdr == "keep" || string.IsNullOrEmpty(s.Hdr)) continue;
                bool want = s.Hdr == "on";
                if (d.HdrOn == want) continue;
                log(s.Name + ": HDR " + (want ? "on" : "off"));
                Displays.SetHdr(d.Adapter, d.TargetId, want);
                hdrChanged = true;
            }
            if (hdrChanged) Thread.Sleep(1200);

            // HDR colour profile. Windows sometimes swaps the default after a topology/HDR change
            // a moment later, so set, wait, verify and set again if needed.
            for (int pass = 0; pass < 3; pass++)
            {
                bool allGood = true;
                live = Displays.Live();
                foreach (var s in p.Displays)
                {
                    if (string.IsNullOrEmpty(s.Profile)) continue;
                    var d = live.FirstOrDefault(x => x.Active && Same(x.DevicePath, s.DevicePath));
                    if (d == null || !d.HdrSupported) continue;
                    if (Same(d.HdrProfile, s.Profile)) continue;
                    allGood = false;
                    log(s.Name + ": HDR colour profile -> " + s.Profile + (pass > 0 ? " (again)" : ""));
                    Displays.SetHdrProfile(d, s.Profile);
                    somethingChanged = true;
                }
                if (allGood) break;
                Thread.Sleep(1000);
            }

            // Sound output. A screen's HDMI/DP audio appears a few seconds after the screen turns on.
            string audioProblem = null;
            if (!string.IsNullOrEmpty(p.AudioName))
            {
                var dev = Audio.WaitFor(p.AudioId, p.AudioName, 8000);
                if (dev == null || !dev.Active) audioProblem = "sound output '" + p.AudioName + "' not available";
                else if (!dev.IsDefault)
                {
                    log("Sound output -> " + dev.Name);
                    try { Audio.SetDefault(dev.Id); } catch (Exception ex) { audioProblem = ex.Message; }
                }
            }

            // Optional: HDR off -> on so Windows definitely reloads the calibration.
            // Only when this switch changed something; if all was already right there is nothing to reload.
            if (refreshHdr && somethingChanged)
            {
                live = Displays.Live();
                var hdrScreens = p.Displays
                    .Select(s => live.FirstOrDefault(x => x.Active && Same(x.DevicePath, s.DevicePath)))
                    .Where(d => d != null && d.HdrSupported && d.HdrOn).ToList();
                if (hdrScreens.Count > 0)
                {
                    log("Refreshing HDR on " + string.Join(", ", hdrScreens.Select(d => d.Name)) + "...");
                    foreach (var d in hdrScreens) Displays.SetHdr(d.Adapter, d.TargetId, false);
                    Thread.Sleep(1500);
                    foreach (var d in hdrScreens) Displays.SetHdr(d.Adapter, d.TargetId, true);
                    Thread.Sleep(1500);
                }
            }

            live = Displays.Live();
            var problems = new List<string>();
            foreach (var s in p.Displays)
            {
                var d = live.FirstOrDefault(x => x.Active && Same(x.DevicePath, s.DevicePath));
                if (d == null) { problems.Add(s.Name + " is not active"); continue; }
                if (!RateMatches(s, d)) problems.Add(s.Name + " runs at " + d.Rate + " Hz, not " + s.RefreshHz + " Hz");
                if (d.HdrSupported && s.Hdr == "on" && !d.HdrOn) problems.Add(s.Name + " HDR did not turn on");
                if (d.HdrSupported && s.Hdr == "off" && d.HdrOn) problems.Add(s.Name + " HDR did not turn off");
                if (d.HdrSupported && !string.IsNullOrEmpty(s.Profile) && !Same(d.HdrProfile, s.Profile)) problems.Add(s.Name + " profile is " + (d.HdrProfile ?? "none"));
            }
            if (audioProblem != null) problems.Add(audioProblem);

            // LG TV power: switched away from the TV, so turn it off.
            if (tvKnown && settings.TvTurnOff && tvWasActive && !profileUsesTv)
            {
                try { log("Turning the TV off"); TvPower.TurnOff(settings); }
                catch (Exception e) { log("TV power: " + e.Message); }
            }

            if (problems.Count > 0) throw new DisplayException(string.Join("; ", problems));
            log("Done: '" + p.Name + "' is active.");
            return layoutUpdated;
        }
    }
}
