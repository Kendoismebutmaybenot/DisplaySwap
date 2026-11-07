// DisplaySwap core: Windows display topology (QueryDisplayConfig/SetDisplayConfig),
// HDR on/off (DisplayConfigSetDeviceInfo) and HDR colour profile association (mscms).
// C# 5 (built with the .NET Framework csc).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DisplaySwap
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint Low; public int High;
        public long Value { get { return ((long)High << 32) | Low; } }
        public static LUID From(long v) { LUID l; l.Low = (uint)(v & 0xFFFFFFFF); l.High = (int)(v >> 32); return l; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathSource { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathTarget {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo { public PathSource source; public PathTarget target; public uint flags; }

    // DISPLAYCONFIG_MODE_INFO (64 bytes). Union fields overlaid at offset 16.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ModeInfo {
        [FieldOffset(0)] public uint infoType;   // 1 source, 2 target, 3 desktop image
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        // source mode
        [FieldOffset(16)] public uint srcWidth;
        [FieldOffset(20)] public uint srcHeight;
        [FieldOffset(24)] public uint srcPixelFormat;
        [FieldOffset(28)] public int srcX;
        [FieldOffset(32)] public int srcY;
        // target mode (video signal info)
        [FieldOffset(16)] public ulong pixelRate;
        [FieldOffset(24)] public uint hSyncNum;
        [FieldOffset(28)] public uint hSyncDen;
        [FieldOffset(32)] public uint vSyncNum;
        [FieldOffset(36)] public uint vSyncDen;
        [FieldOffset(40)] public uint activeCx;
        [FieldOffset(44)] public uint activeCy;
        [FieldOffset(48)] public uint totalCx;
        [FieldOffset(52)] public uint totalCy;
        [FieldOffset(56)] public uint videoStandard;
        [FieldOffset(60)] public uint tgtScanLineOrdering;
    }

    public class DisplayException : Exception { public DisplayException(string m) : base(m) { } }

    /// <summary>One display as seen right now.</summary>
    public class LiveDisplay
    {
        public string Name;          // monitor friendly name from the EDID
        public string DevicePath;    // stable monitor id (\\?\DISPLAY#<model>#...)
        public string AdapterPath;
        public LUID Adapter;
        public uint TargetId;
        public uint SourceId;
        public bool Active;
        public bool Available;
        public string GdiName;       // \\.\DISPLAY1
        public int Width, Height, X, Y;
        public double Hz;
        public bool HdrSupported, HdrOn;
        public string HdrProfile;    // current default advanced-colour ICC (file name)
        public bool Primary { get { return Active && X == 0 && Y == 0; } }
    }

    public static class Native
    {
        public const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2;
        public const uint SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20,
            SDC_VALIDATE = 0x40, SDC_APPLY = 0x80, SDC_NO_OPTIMIZATION = 0x100, SDC_SAVE_TO_DATABASE = 0x200,
            SDC_ALLOW_CHANGES = 0x400, SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
        public const uint PATH_ACTIVE = 0x1;
        public const uint MODE_IDX_INVALID = 0xFFFFFFFF;

        [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[] paths, uint numModes, [In] ModeInfo[] modes, uint flags);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
        [DllImport("user32.dll")] public static extern int DisplayConfigSetDeviceInfo(IntPtr packet);

        public const uint CPT_ICC = 0, CPST_EXTENDED_DISPLAY_COLOR_MODE = 8;
    }

    public static class Displays
    {
        // ---------- raw query ----------
        public static void Query(uint flags, out PathInfo[] paths, out ModeInfo[] modes)
        {
            for (int attempt = 0; ; attempt++)
            {
                uint np, nm;
                Check(Native.GetDisplayConfigBufferSizes(flags, out np, out nm), "GetDisplayConfigBufferSizes");
                paths = new PathInfo[np]; modes = new ModeInfo[nm];
                int r = Native.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (r == 122 && attempt < 5) continue; // ERROR_INSUFFICIENT_BUFFER: topology changed
                Check(r, "QueryDisplayConfig");
                Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
                return;
            }
        }

        static void Check(int r, string what)
        {
            if (r != 0) throw new DisplayException(what + " failed: " + new Win32Exception(r).Message + " (" + r + ")");
        }

        // ---------- DisplayConfigGetDeviceInfo helpers (byte packets) ----------
        static IntPtr Packet(int type, int size, LUID adapter, uint id)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, type); Marshal.WriteInt32(p, 4, size);
            Marshal.WriteInt32(p, 8, (int)adapter.Low); Marshal.WriteInt32(p, 12, adapter.High);
            Marshal.WriteInt32(p, 16, (int)id);
            return p;
        }

        public static void TargetName(LUID adapter, uint targetId, out string friendly, out string path)
        {
            IntPtr p = Packet(2, 420, adapter, targetId);
            try {
                Check(Native.DisplayConfigGetDeviceInfo(p), "GetTargetName");
                friendly = Marshal.PtrToStringUni(p + 36);
                path = Marshal.PtrToStringUni(p + 164);
                if (string.IsNullOrEmpty(friendly)) {
                    int tech = Marshal.ReadInt32(p, 24);
                    friendly = (tech == unchecked((int)0x80000000)) ? "Built-in display" : "Display " + targetId;
                }
            } finally { Marshal.FreeHGlobal(p); }
        }

        public static string AdapterName(LUID adapter)
        {
            IntPtr p = Packet(4, 276, adapter, 0);
            try { Check(Native.DisplayConfigGetDeviceInfo(p), "GetAdapterName"); return Marshal.PtrToStringUni(p + 20); }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static string SourceGdiName(LUID adapter, uint sourceId)
        {
            IntPtr p = Packet(1, 84, adapter, sourceId);
            try { return Native.DisplayConfigGetDeviceInfo(p) == 0 ? Marshal.PtrToStringUni(p + 20) : ""; }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Always try the 24H2+ calls first and fall back when Windows rejects them. (Don't gate on
        // Environment.OSVersion: without a manifest .NET Framework is told it runs on Windows 8, and the old
        // type-9 "advancedColorSupported" bit is also set for wide-colour-only SDR monitors.)
        static bool UseHdr2 { get { return true; } }

        /// <summary>supported = display can do HDR; on = HDR currently enabled.</summary>
        public static void GetHdr(LUID adapter, uint targetId, out bool supported, out bool on)
        {
            supported = false; on = false;
            if (UseHdr2)
            {   // GET_ADVANCED_COLOR_INFO_2 (24H2+): separates HDR from ACM/WCG
                IntPtr p = Packet(15, 36, adapter, targetId);
                try {
                    if (Native.DisplayConfigGetDeviceInfo(p) == 0) {
                        uint v = (uint)Marshal.ReadInt32(p, 20);
                        supported = (v & 0x10) != 0;                // highDynamicRangeSupported
                        on = (Marshal.ReadInt32(p, 32) == 2);        // activeColorMode == HDR
                        return;
                    }
                } finally { Marshal.FreeHGlobal(p); }
            }
            IntPtr q = Packet(9, 32, adapter, targetId);
            try {
                Check(Native.DisplayConfigGetDeviceInfo(q), "GetAdvancedColorInfo");
                uint v = (uint)Marshal.ReadInt32(q, 20);
                supported = (v & 0x1) != 0; on = (v & 0x2) != 0;
            } finally { Marshal.FreeHGlobal(q); }
        }

        public static void SetHdr(LUID adapter, uint targetId, bool enable)
        {
            int r = -1;
            if (UseHdr2)
            {   // SET_HDR_STATE (24H2+)
                IntPtr p = Packet(16, 24, adapter, targetId);
                try { Marshal.WriteInt32(p, 20, enable ? 1 : 0); r = Native.DisplayConfigSetDeviceInfo(p); }
                finally { Marshal.FreeHGlobal(p); }
            }
            if (r != 0)
            {
                IntPtr q = Packet(10, 24, adapter, targetId);
                try { Marshal.WriteInt32(q, 20, enable ? 1 : 0); Check(Native.DisplayConfigSetDeviceInfo(q), "SetHdrState"); }
                finally { Marshal.FreeHGlobal(q); }
            }
        }

        // ---------- colour profiles ----------
        // Profiles are read/written per MONITOR device key (MONITOR\<model>\{4d36e96e-...}\<nnnn>) with the WCS
        // functions. The newer ColorProfile*(adapter, sourceId) functions resolve the source to a monitor
        // themselves and, right after a layout change where two monitors have used the same source, they
        // can write to the wrong monitor (e.g. the TV's profile lands on the desk monitor and vice versa).
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint prop, out uint type, StringBuilder buf, ref uint len, uint flags);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsGetDefaultColorProfileSize(uint scope, string dev, uint type, uint sub, uint id, out uint size);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsGetDefaultColorProfile(uint scope, string dev, uint type, uint sub, uint id, uint size, StringBuilder name);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsSetDefaultColorProfile(uint scope, string dev, uint type, uint sub, uint id, string name);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsGetUsePerUserProfiles(string dev, uint deviceClass, out bool perUser);
        const uint CLASS_MONITOR = 0x6D6E7472; // 'mntr'

        static readonly Dictionary<string, string> keyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>\\?\DISPLAY#<model>#<instance>#{guid}  ->  MONITOR\<model>\{4d36e96e-e325-11ce-bfc1-08002be10318}\<nnnn></summary>
        public static string MonitorKey(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            string k;
            lock (keyCache) if (keyCache.TryGetValue(devicePath, out k)) return k;
            var id = devicePath.StartsWith(@"\\?\") ? devicePath.Substring(4) : devicePath;
            int g = id.LastIndexOf("#{", StringComparison.Ordinal);
            if (g > 0) id = id.Substring(0, g);
            var parts = id.Split('#');
            if (parts.Length < 3) return null;
            uint inst;
            if (CM_Locate_DevNodeW(out inst, string.Join("\\", parts), 0) != 0) return null;
            var buf = new StringBuilder(256); uint len = 512, type;
            if (CM_Get_DevNode_Registry_PropertyW(inst, 0x0A /*CM_DRP_DRIVER*/, out type, buf, ref len, 0) != 0) return null;
            k = "MONITOR\\" + parts[1] + "\\" + buf.ToString();
            lock (keyCache) keyCache[devicePath] = k;
            return k;
        }

        static uint Scope(string key)
        {
            bool perUser;
            return WcsGetUsePerUserProfiles(key, CLASS_MONITOR, out perUser) && !perUser ? 0u : 1u; // 1 = current user
        }

        /// <summary>Current default HDR (advanced colour) profile of a monitor, works for inactive monitors too.</summary>
        public static string GetHdrProfile(string devicePath)
        {
            var key = MonitorKey(devicePath);
            if (key == null) return null;
            uint scope = Scope(key), size;
            if (!WcsGetDefaultColorProfileSize(scope, key, Native.CPT_ICC, Native.CPST_EXTENDED_DISPLAY_COLOR_MODE, 0, out size)) return null;
            var sb = new StringBuilder((int)(size / 2) + 2);
            if (!WcsGetDefaultColorProfile(scope, key, Native.CPT_ICC, Native.CPST_EXTENDED_DISPLAY_COLOR_MODE, 0, (uint)sb.Capacity * 2, sb)) return null;
            return sb.ToString();
        }

        /// <summary>Make an HDR profile the default for exactly this monitor (adds it to the monitor's list if needed).</summary>
        public static void SetHdrProfile(string devicePath, string profile)
        {
            var key = MonitorKey(devicePath);
            if (key == null) throw new DisplayException("Could not find the monitor's device entry for " + devicePath);
            if (!WcsSetDefaultColorProfile(Scope(key), key, Native.CPT_ICC, Native.CPST_EXTENDED_DISPLAY_COLOR_MODE, 0, profile))
                throw new DisplayException("Setting colour profile '" + profile + "' failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        /// <summary>HDR (advanced colour) ICC files installed in the colour store.</summary>
        public static List<string> InstalledProfiles()
        {
            if (Demo.On) return Demo.ColourProfiles();
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color");
            var list = new List<string>();
            try {
                foreach (var f in System.IO.Directory.GetFiles(dir))
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".icc" || ext == ".icm") list.Add(System.IO.Path.GetFileName(f));
                }
            } catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ---------- live view ----------
        public static List<LiveDisplay> Live()
        {
            if (Demo.On) return Demo.Displays();
            PathInfo[] paths; ModeInfo[] modes;
            Query(Native.QDC_ALL_PATHS, out paths, out modes);
            var byTarget = new Dictionary<string, LiveDisplay>();
            var order = new List<LiveDisplay>();
            foreach (var p in paths)
            {
                bool active = (p.flags & Native.PATH_ACTIVE) != 0;
                if (!active && p.target.targetAvailable == 0) continue;
                string key = p.target.adapterId.Value + ":" + p.target.id;
                LiveDisplay d;
                if (byTarget.TryGetValue(key, out d)) {
                    if (!active || d.Active) continue; // keep the active path for this target
                } else {
                    d = new LiveDisplay();
                    TargetName(p.target.adapterId, p.target.id, out d.Name, out d.DevicePath);
                    d.Adapter = p.target.adapterId; d.TargetId = p.target.id;
                    try { d.AdapterPath = AdapterName(d.Adapter); } catch { d.AdapterPath = ""; }
                    byTarget[key] = d; order.Add(d);
                }
                d.Available = p.target.targetAvailable != 0;
                d.Active = active;
                d.SourceId = p.source.id;
                if (active)
                {
                    d.GdiName = SourceGdiName(p.source.adapterId, p.source.id);
                    if (p.source.modeInfoIdx < modes.Length) {
                        var m = modes[p.source.modeInfoIdx];
                        d.Width = (int)m.srcWidth; d.Height = (int)m.srcHeight; d.X = m.srcX; d.Y = m.srcY;
                    }
                    if (p.target.refreshDen != 0) d.Hz = (double)p.target.refreshNum / p.target.refreshDen;
                    if (p.target.modeInfoIdx < modes.Length && modes[p.target.modeInfoIdx].vSyncDen != 0)
                        d.Hz = (double)modes[p.target.modeInfoIdx].vSyncNum / modes[p.target.modeInfoIdx].vSyncDen;
                }
            }
            foreach (var d in order)
            {
                try { GetHdr(d.Adapter, d.TargetId, out d.HdrSupported, out d.HdrOn); } catch { }
                try { d.HdrProfile = GetHdrProfile(d.DevicePath); } catch { }
            }
            return order;
        }

        // ---------- snapshot / restore ----------
        public static Layout Capture()
        {
            PathInfo[] paths; ModeInfo[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            var lay = new Layout();
            lay.Paths = ToBase64(paths); lay.Modes = ToBase64(modes);
            var adapters = new Dictionary<long, string>();
            foreach (var p in paths) Remember(adapters, p.source.adapterId);
            foreach (var m in modes) Remember(adapters, m.adapterId);
            lay.Adapters = adapters.Select(kv => new AdapterRef { Luid = kv.Key, Path = kv.Value }).ToList();
            lay.Targets = new List<TargetRef>();
            foreach (var p in paths)
            {
                string fn, dp; TargetName(p.target.adapterId, p.target.id, out fn, out dp);
                lay.Targets.Add(new TargetRef { Luid = p.target.adapterId.Value, TargetId = p.target.id, DevicePath = dp, Name = fn });
            }
            return lay;
        }

        static void Remember(Dictionary<long, string> map, LUID l)
        {
            if (map.ContainsKey(l.Value)) return;
            try { map[l.Value] = AdapterName(l); } catch { map[l.Value] = ""; }
        }

        /// <summary>Switch the desktop to a saved layout. Returns a human summary.</summary>
        public static string ApplyLayout(Layout lay, Action<string> log)
        {
            PathInfo[] paths; ModeInfo[] modes;
            PrepareLayout(lay, out paths, out modes);
            uint full = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_SAVE_TO_DATABASE | Native.SDC_ALLOW_CHANGES;
            int r = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, full);
            if (r == 0) { log("Layout applied (exact modes)."); return "exact"; }
            log("Exact layout rejected (" + new Win32Exception(r).Message + "), retrying with Windows' remembered modes...");

            // Fallback: just the topology; Windows picks the modes it remembers for it.
            var topo = (PathInfo[])paths.Clone();
            for (int i = 0; i < topo.Length; i++) { topo[i].source.modeInfoIdx = Native.MODE_IDX_INVALID; topo[i].target.modeInfoIdx = Native.MODE_IDX_INVALID; }
            r = Native.SetDisplayConfig((uint)topo.Length, topo, 0, null,
                Native.SDC_APPLY | Native.SDC_TOPOLOGY_SUPPLIED | Native.SDC_ALLOW_PATH_ORDER_CHANGES);
            Check(r, "SetDisplayConfig");
            log("Layout applied (topology only).");
            return "topology";
        }

        /// <summary>Ask Windows whether a saved layout would be accepted, without changing anything.</summary>
        public static int ValidateLayout(Layout lay)
        {
            PathInfo[] paths; ModeInfo[] modes;
            PrepareLayout(lay, out paths, out modes);
            return Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                Native.SDC_VALIDATE | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES);
        }

        /// <summary>Load a saved layout and fix it up for the current adapters/monitors.</summary>
        static void PrepareLayout(Layout lay, out PathInfo[] paths, out ModeInfo[] modes)
        {
            paths = FromBase64<PathInfo>(lay.Paths);
            modes = FromBase64<ModeInfo>(lay.Modes);

            // Current adapters and targets (adapter LUIDs change across reboots / driver updates).
            PathInfo[] allPaths; ModeInfo[] allModes;
            Query(Native.QDC_ALL_PATHS, out allPaths, out allModes);
            var currentAdapters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var currentTargets = new Dictionary<string, PathTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in allPaths)
            {
                try { currentAdapters[AdapterName(p.target.adapterId)] = p.target.adapterId.Value; } catch { }
                string key = p.target.adapterId.Value + ":" + p.target.id;
                if (currentTargets.ContainsKey(key)) continue;
                currentTargets[key] = p.target;
            }
            var luidMap = new Dictionary<long, long>();
            foreach (var a in lay.Adapters ?? new List<AdapterRef>())
            {
                long now;
                if (currentAdapters.TryGetValue(a.Path ?? "", out now)) luidMap[a.Luid] = now;
                else if (currentAdapters.Count == 1) luidMap[a.Luid] = currentAdapters.Values.First();
            }
            Func<LUID, LUID> remap = l => { long n; return luidMap.TryGetValue(l.Value, out n) ? LUID.From(n) : l; };

            // Make sure every saved monitor is plugged in (find by device path, which is stable).
            var missing = new List<string>();
            for (int i = 0; i < paths.Length; i++)
            {
                var t = lay.Targets != null && i < lay.Targets.Count ? lay.Targets[i] : null;
                paths[i].source.adapterId = remap(paths[i].source.adapterId);
                paths[i].target.adapterId = remap(paths[i].target.adapterId);
                if (t != null && !string.IsNullOrEmpty(t.DevicePath))
                {
                    bool found = false;
                    foreach (var p in allPaths)
                    {
                        string fn, dp;
                        try { TargetName(p.target.adapterId, p.target.id, out fn, out dp); } catch { continue; }
                        if (string.Equals(dp, t.DevicePath, StringComparison.OrdinalIgnoreCase))
                        {
                            paths[i].target.adapterId = p.target.adapterId;
                            paths[i].target.id = p.target.id;
                            if (p.target.targetAvailable != 0) found = true;
                            break;
                        }
                    }
                    if (!found) missing.Add(t.Name);
                }
                paths[i].flags |= Native.PATH_ACTIVE;
            }
            if (missing.Count > 0) throw new DisplayException("Not connected: " + string.Join(", ", missing));
            for (int i = 0; i < modes.Length; i++)
            {
                modes[i].adapterId = remap(modes[i].adapterId);
                // target modes carry the target id; keep them in step with remapped paths
            }
            for (int i = 0; i < paths.Length; i++)
            {
                if (paths[i].target.modeInfoIdx < modes.Length) {
                    modes[paths[i].target.modeInfoIdx].id = paths[i].target.id;
                    modes[paths[i].target.modeInfoIdx].adapterId = paths[i].target.adapterId;
                }
            }

        }

        /// <summary>Wait until Windows sees a monitor as connected (e.g. a TV that was just switched on).</summary>
        public static bool WaitForAvailable(string devicePath, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                try {
                    if (Live().Any(d => string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase) && (d.Available || d.Active))) return true;
                } catch { }
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(400);
            }
        }

        /// <summary>Wait until exactly the given monitors are active.</summary>
        public static bool WaitForActive(ICollection<string> devicePaths, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                try {
                    var act = Live().Where(d => d.Active).Select(d => d.DevicePath.ToLowerInvariant()).ToList();
                    if (act.Count == devicePaths.Count && devicePaths.All(p => act.Contains(p.ToLowerInvariant()))) return true;
                } catch { }
                Thread.Sleep(250);
            }
            return false;
        }

        // ---------- (de)serialising raw structs ----------
        public static string ToBase64<T>(T[] arr) where T : struct
        {
            int sz = Marshal.SizeOf(typeof(T));
            var bytes = new byte[sz * arr.Length];
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try {
                for (int i = 0; i < arr.Length; i++) { Marshal.StructureToPtr(arr[i], buf, false); Marshal.Copy(buf, bytes, i * sz, sz); }
            } finally { Marshal.FreeHGlobal(buf); }
            return Convert.ToBase64String(bytes);
        }

        public static T[] FromBase64<T>(string s) where T : struct
        {
            var bytes = Convert.FromBase64String(s ?? "");
            int sz = Marshal.SizeOf(typeof(T));
            var arr = new T[bytes.Length / sz];
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try {
                for (int i = 0; i < arr.Length; i++) { Marshal.Copy(bytes, i * sz, buf, sz); arr[i] = (T)Marshal.PtrToStructure(buf, typeof(T)); }
            } finally { Marshal.FreeHGlobal(buf); }
            return arr;
        }
    }

    // ---------- persisted model ----------
    public class AdapterRef { public long Luid; public string Path; }
    public class TargetRef { public long Luid; public uint TargetId; public string DevicePath; public string Name; }

    public class Layout
    {
        public string Paths;   // base64 DISPLAYCONFIG_PATH_INFO[]
        public string Modes;   // base64 DISPLAYCONFIG_MODE_INFO[]
        public List<AdapterRef> Adapters;
        public List<TargetRef> Targets;
    }

    public class DisplaySetting
    {
        public string DevicePath;
        public string Name;
        public int Width, Height, X, Y;
        public double Hz;
        public bool HdrSupported;
        public string Hdr;        // "on", "off", "keep"
        public string Profile;    // ICC file name, or null/"" = leave alone
    }

    public class Profile
    {
        public string Name;
        public string Hotkey;     // "", "Ctrl+Alt+1" ...
        public string AudioId;    // default playback device to set; empty = don't change
        public string AudioName;  // matched by name too, since HDMI audio endpoint ids change
        public Layout Layout;
        public List<DisplaySetting> Displays;
    }
}
