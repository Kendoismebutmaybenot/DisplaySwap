// Playback devices: list them (MMDevice API) and set the Windows default (IPolicyConfig, the
// undocumented-but-stable interface SoundSwitch/EarTrumpet use, Windows 7+).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace DisplaySwap
{
    public class AudioDevice
    {
        public string Id;      // {0.0.0.00000000}.{guid}
        public string Name;    // e.g. "Living Room TV (NVIDIA High Definition Audio)"
        public bool Active;    // plugged in and usable right now
        public bool Present;   // exists now (active or unplugged); false = only exists while its screen is on
        public bool IsDefault;
    }

    public static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropKey key);
            [PreserveSig] int GetValue(ref PropKey key, out PropVariant value);
        }

        // Only SetDefaultEndpoint is called; the others keep the vtable order.
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        const int eRender = 0, ACTIVE = 1, NOTPRESENT = 4, UNPLUGGED = 8;
        static readonly Guid FriendlyNameFmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        static string NameOf(IMMDevice d)
        {
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) return null;
            try {
                var key = new PropKey { fmtid = FriendlyNameFmt, pid = 14 };
                PropVariant v;
                if (ps.GetValue(ref key, out v) != 0) return null;
                try { return v.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(v.p) : null; }
                finally { PropVariantClear(ref v); }
            } finally { Marshal.ReleaseComObject(ps); }
        }

        /// <summary>Playback devices that are plugged in, plus unplugged ones (e.g. TV HDMI audio while the TV is off).</summary>
        public static List<AudioDevice> PlaybackDevices() { return PlaybackDevices(false); }

        /// <param name="includeAbsent">also list screens' HDMI/DP audio that only exists while the screen is on
        /// (one entry per name; Windows keeps several stale copies).</param>
        public static List<AudioDevice> PlaybackDevices(bool includeAbsent)
        {
            if (Demo.On) return Demo.Audio();
            var list = new List<AudioDevice>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            try {
                string defId = null;
                IMMDevice def;
                if (en.GetDefaultAudioEndpoint(eRender, 0, out def) == 0 && def != null) { def.GetId(out defId); Marshal.ReleaseComObject(def); }
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(eRender, ACTIVE | UNPLUGGED | (includeAbsent ? NOTPRESENT : 0), out col) != 0) return list;
                uint n; col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d; if (col.Item(i, out d) != 0) continue;
                    try {
                        var a = new AudioDevice(); int state;
                        d.GetId(out a.Id); d.GetState(out state);
                        a.Name = NameOf(d) ?? a.Id; a.Active = state == ACTIVE; a.Present = state != NOTPRESENT; a.IsDefault = a.Id == defId;
                        list.Add(a);
                    } finally { Marshal.ReleaseComObject(d); }
                }
                Marshal.ReleaseComObject(col);
            } finally { Marshal.ReleaseComObject(en); }
            if (includeAbsent)
            {   // keep present devices, plus one absent entry per name that has no present twin
                var present = new HashSet<string>(list.Where(d => d.Present).Select(d => BaseName(d.Name)), StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                list = list.Where(d => d.Present || (!present.Contains(BaseName(d.Name)) && seen.Add(BaseName(d.Name)))).ToList();
            }
            return list.OrderBy(d => d.Active ? 0 : 1).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static AudioDevice Default() { return PlaybackDevices().FirstOrDefault(d => d.IsDefault); }

        // Windows prefixes a number when it re-creates an endpoint ("2- Living Room TV (...)").
        public static string BaseName(string n) { return Regex.Replace(n ?? "", @"^\d+\s*-\s*", "").Trim(); }

        /// <summary>Find the saved device now: by id, or by name (the TV's HDMI endpoint id can change).</summary>
        public static AudioDevice Find(List<AudioDevice> devs, string id, string name)
        {
            var hit = devs.FirstOrDefault(d => d.Active && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            hit = devs.FirstOrDefault(d => d.Active && string.Equals(BaseName(d.Name), BaseName(name), StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            return devs.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? devs.FirstOrDefault(d => string.Equals(BaseName(d.Name), BaseName(name), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Make a device the default for everything (games/apps, media and calls).</summary>
        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            try {
                for (int role = 0; role < 3; role++)
                {
                    int r = pc.SetDefaultEndpoint(id, role);
                    if (r != 0) throw new DisplayException("Setting the sound output failed (0x" + r.ToString("X8") + ")");
                }
            } finally { Marshal.ReleaseComObject(pc); }
        }

        /// <summary>Wait for a device to be plugged in (HDMI audio appears a moment after the screen comes on).</summary>
        public static AudioDevice WaitFor(string id, string name, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                AudioDevice d = null;
                try { d = Find(PlaybackDevices(), id, name); } catch { }
                if (d != null && d.Active) return d;
                if (DateTime.UtcNow >= until) return d;
                Thread.Sleep(300);
            }
        }
    }
}
