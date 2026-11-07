// TV power control for several brands, behind one small interface.
//
// NETWORK USE (deliberately limited):
//  * TvDiscovery.FindAll() is the ONLY code that scans the network (SSDP multicast). It runs only when the
//    user clicks "Find & pair TV" in Settings, never in the background or at startup.
//  * During a switch, and only if the user turned on the TV power options, DisplaySwap talks to the ONE
//    paired TV at its saved address, plus a Wake-on-LAN broadcast when it has to switch the TV on.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DisplaySwap
{
    public class TvInfo
    {
        public string Brand;   // "lg", "samsung", "sony", "roku"
        public string Ip, Mac, Name;
        public string Label { get { return TvPower.For(Brand).Label + " " + Name; } }
    }

    public interface ITvDriver
    {
        string Brand { get; }
        string Label { get; }
        /// <summary>Pair with a found TV. Returns the token/key to store ("" if none is needed).
        /// askText(title, body) asks the user for text (null = cancelled).</summary>
        string Pair(TvInfo tv, Func<string, string, string> askText);
        /// <summary>True if the saved pairing for this TV still works (then "Pair again" needs no prompt).</summary>
        bool PairingWorks(Settings s);
        /// <summary>"on", "screen-off" or "off" (off also means unreachable).</summary>
        string PowerState(Settings s);
        void TurnOn(Settings s, string state);
        void TurnOff(Settings s);
    }

    // ---------------------------------------------------------------- shared helpers
    public static class TvNet
    {
        [DllImport("iphlpapi.dll")] static extern int SendARP(uint dest, uint src, byte[] mac, ref int len);

        public static string Mac(string ip)
        {
            try {
                var mac = new byte[6]; int len = 6;
                if (SendARP(BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0), 0, mac, ref len) != 0) return null;
                return BitConverter.ToString(mac, 0, len);
            } catch { return null; }
        }

        /// <summary>Wake-on-LAN magic packet, sent to the broadcast address and the TV's subnet.</summary>
        public static void Wake(string mac, string ip)
        {
            var m = (mac ?? "").Replace("-", "").Replace(":", "");
            if (m.Length != 12) throw new DisplayException("No MAC address saved for the TV");
            var bytes = new byte[102];
            for (int i = 0; i < 6; i++) bytes[i] = 0xFF;
            for (int r = 0; r < 16; r++) for (int i = 0; i < 6; i++) bytes[6 + r * 6 + i] = Convert.ToByte(m.Substring(i * 2, 2), 16);
            using (var u = new UdpClient { EnableBroadcast = true })
            {
                var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, 9), new IPEndPoint(IPAddress.Broadcast, 7) };
                try { var p = IPAddress.Parse(ip).GetAddressBytes(); p[3] = 255; targets.Add(new IPEndPoint(new IPAddress(p), 9)); } catch { }
                for (int k = 0; k < 3; k++) foreach (var t in targets) u.Send(bytes, bytes.Length, t);
            }
        }

        public static bool Reachable(string ip, int[] ports, int timeoutMs)
        {
            foreach (var port in ports)
            {
                try {
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect(ip, port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(timeoutMs) && c.Connected) { c.EndConnect(ar); return true; }
                    }
                } catch { }
            }
            return false;
        }

        /// <summary>Small HTTP helper. Returns the body, or throws.</summary>
        public static string Http(string method, string url, string body, Dictionary<string, string> headers, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method; req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json; charset=UTF-8";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            else if (method == "POST") req.ContentLength = 0;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream())) return r.ReadToEnd();
        }

        public static string Field(string json, string name)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(name) + "\"\\s*:\\s*(\"((?:[^\"\\\\]|\\\\.)*)\"|true|false|-?\\d+)");
            return !m.Success ? null : m.Groups[2].Success && m.Groups[1].Value.StartsWith("\"") ? m.Groups[2].Value : m.Groups[1].Value;
        }

        public static string Tag(string xml, string tag)
        {
            var m = Regex.Match(xml ?? "", "<" + Regex.Escape(tag) + ">([^<]*)</" + Regex.Escape(tag) + ">");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        }

        public static Exception Inner(Exception e) { while (e != null && e.InnerException != null) e = e.InnerException; return e ?? new Exception("unknown error"); }
    }

    /// <summary>Websocket to a TV, trying several URLs (TVs use self-signed certificates on their wss ports).</summary>
    public class TvSocket : IDisposable
    {
        ClientWebSocket ws;

        public TvSocket(string[] urls, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var errors = new List<string>();
            foreach (var url in urls)
            {
                var w = new ClientWebSocket();
                try {
                    var host = new Uri(url).Host;
                    // accept the TV's self-signed certificate, for this TV only
                    RemoteCertificateValidationCallback ok = (s, c, ch, e) => {
                        var r = s as HttpWebRequest; return e == SslPolicyErrors.None || (r != null && r.RequestUri.Host == host);
                    };
                    ServicePointManager.ServerCertificateValidationCallback += ok;
                    try { if (!w.ConnectAsync(new Uri(url), CancellationToken.None).Wait(timeoutMs)) throw new TimeoutException("connect timed out"); }
                    finally { ServicePointManager.ServerCertificateValidationCallback -= ok; }
                    ws = w; return;
                } catch (Exception e) { errors.Add(url.Split('/')[0] + " " + TvNet.Inner(e).Message); w.Dispose(); }
            }
            throw new DisplayException("Can't reach the TV (" + string.Join("; ", errors) + ")");
        }

        public void Send(string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(5000);
        }

        /// <summary>Next text message, or null on timeout/close.</summary>
        public string Receive(int timeoutMs)
        {
            var buf = new byte[16384]; var sb = new StringBuilder();
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                try { if (!t.Wait(left)) return null; } catch { return null; }
                if (t.Result.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buf, 0, t.Result.Count));
                if (t.Result.EndOfMessage) return sb.ToString();
            }
        }

        public void Dispose()
        {
            try { if (ws != null && ws.State == WebSocketState.Open) ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch { }
            if (ws != null) ws.Dispose();
        }
    }

    // ---------------------------------------------------------------- discovery (button only)
    public static class TvDiscovery
    {
        static readonly string[] SearchTargets = {
            "urn:lge-com:service:webos-second-screen:1",          // LG webOS
            "urn:samsung.com:device:RemoteControlReceiver:1",     // Samsung Tizen
            "urn:schemas-sony-com:service:ScalarWebAPI:1",        // Sony Bravia
            "roku:ecp",                                           // Roku TVs
        };

        /// <summary>Scan the local network for supported TVs (~3 s). Only called from "Find & pair TV".</summary>
        public static List<TvInfo> FindAll()
        {
            var replies = new Dictionary<string, string[]>();   // ip -> [brand, location]
            using (var u = new UdpClient())
            {
                u.Client.ReceiveTimeout = 700;
                var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                Action send = () => {
                    foreach (var st in SearchTargets)
                    {
                        var msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
                        u.Send(msg, msg.Length, group);
                    }
                };
                send();
                var until = DateTime.UtcNow.AddSeconds(3);
                bool resent = false;
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        var reply = Encoding.ASCII.GetString(u.Receive(ref ep));
                        var ip = ep.Address.ToString();
                        var brand = BrandOf(reply);
                        if (brand == null || replies.ContainsKey(ip)) continue;
                        var loc = Regex.Match(reply, @"(?im)^location:\s*(\S+)");
                        replies[ip] = new[] { brand, loc.Success ? loc.Groups[1].Value : null };
                    }
                    catch (SocketException) { if (!resent) { resent = true; send(); } }   // one retry for missed packets
                }
            }
            var found = new List<TvInfo>();
            foreach (var kv in replies)
            {
                var tv = new TvInfo { Brand = kv.Value[0], Ip = kv.Key, Mac = TvNet.Mac(kv.Key) };
                try { tv.Name = NameOf(tv, kv.Value[1]); } catch { }
                if (string.IsNullOrEmpty(tv.Name)) tv.Name = "TV at " + tv.Ip;
                found.Add(tv);
            }
            return found;
        }

        static string BrandOf(string reply)
        {
            var st = (Regex.Match(reply, @"(?im)^st:\s*(.+)$").Groups[1].Value + " " + Regex.Match(reply, @"(?im)^server:\s*(.+)$").Groups[1].Value).ToLowerInvariant();
            if (st.Contains("lge-com") || st.Contains("webos")) return "lg";
            if (st.Contains("samsung") || st.Contains("tizen")) return "samsung";
            if (st.Contains("sony")) return "sony";
            if (st.Contains("roku")) return "roku";
            return null;
        }

        static string NameOf(TvInfo tv, string location)
        {
            if (tv.Brand == "samsung")
            {
                var j = TvNet.Http("GET", "http://" + tv.Ip + ":8001/api/v2/", null, null, 2000);
                var mac = TvNet.Field(j, "wifiMac");
                if (!string.IsNullOrEmpty(mac)) tv.Mac = mac.Replace(":", "-").ToUpperInvariant();
                return TvNet.Field(j, "modelName") ?? TvNet.Field(j, "name");
            }
            if (tv.Brand == "roku")
            {
                var x = TvNet.Http("GET", "http://" + tv.Ip + ":8060/query/device-info", null, null, 2000);
                return TvNet.Tag(x, "user-device-name") ?? TvNet.Tag(x, "friendly-model-name") ?? TvNet.Tag(x, "model-name");
            }
            if (location == null) return null;
            var d = TvNet.Http("GET", location, null, null, 2000);
            var n = TvNet.Tag(d, "friendlyName");
            return n == null ? null : n.Replace("[LG] ", "").Replace("webOS TV ", "");
        }
    }

    // ---------------------------------------------------------------- facade used by the switcher
    public static class TvPower
    {
        static readonly ITvDriver[] drivers = { new LgDriver(), new SamsungDriver(), new SonyDriver(), new RokuDriver(), new CustomDriver() };

        public static ITvDriver For(string brand)
        {
            var b = string.IsNullOrEmpty(brand) ? "lg" : brand;   // profiles paired before brands existed are LG
            return drivers.FirstOrDefault(d => d.Brand == b) ?? drivers[0];
        }

        /// <summary>Make sure the paired TV is on; waits until it says so (custom commands can't report, so they don't wait).</summary>
        public static void EnsureOn(Settings s, Action<string> log, int timeoutMs)
        {
            var d = For(s.TvBrand);
            var state = d.PowerState(s);
            if (state == "on") return;
            log("Turning the TV on" + (state == "screen-off" ? " (screen was off)" : ""));
            d.TurnOn(s, state);
            if (d is CustomDriver) return;
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int n = 0;
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(1000);
                if (d.PowerState(s) == "on") { log("TV is on"); return; }
                if (++n % 4 == 0) d.TurnOn(s, "off");   // repeat in case a wake packet was missed
            }
            throw new DisplayException("The TV did not turn on" + (d.Brand == "lg" || d.Brand == "samsung" ? " (check that the TV allows turning on via Wi-Fi/LAN)" : ""));
        }

        public static void TurnOff(Settings s)
        {
            var d = For(s.TvBrand);
            if (d is CustomDriver) { d.TurnOff(s); return; }
            if (d.PowerState(s) == "off") return;   // already off; Samsung's power key would toggle it back on
            d.TurnOff(s);
        }
    }

    // ---------------------------------------------------------------- LG webOS
    public class LgDriver : ITvDriver
    {
        public string Brand { get { return "lg"; } }
        public string Label { get { return "LG"; } }
        public string Pair(TvInfo tv, Func<string, string, string> askText) { return LgTv.Pair(tv.Ip); }
        public bool PairingWorks(Settings s) { return LgTv.PowerState(s.TvIp, s.TvKey) != null; }
        public string PowerState(Settings s)
        {
            var st = LgTv.PowerState(s.TvIp, s.TvKey);
            return st == "Active" ? "on" : st == "Screen Off" ? "screen-off" : "off";
        }
        public void TurnOn(Settings s, string state)
        {
            if (state == "screen-off") { try { LgTv.TurnOnScreen(s.TvIp, s.TvKey); return; } catch { } }
            TvNet.Wake(s.TvMac, s.TvIp);
        }
        public void TurnOff(Settings s) { LgTv.TurnOff(s.TvIp, s.TvKey); }
    }

    // ---------------------------------------------------------------- Samsung Tizen (2016+)
    public class SamsungDriver : ITvDriver
    {
        const string AppName = "RGlzcGxheVN3YXA=";   // base64 "DisplaySwap", shown in the TV's allow prompt
        public string Brand { get { return "samsung"; } }
        public string Label { get { return "Samsung"; } }
        public bool PairingWorks(Settings s) { return false; }

        static TvSocket Open(string ip, string token, int timeoutMs)
        {
            var q = "/api/v2/channels/samsung.remote.control?name=" + AppName + (string.IsNullOrEmpty(token) ? "" : "&token=" + token);
            return new TvSocket(new[] { "wss://" + ip + ":8002" + q, "ws://" + ip + ":8001" + q }, timeoutMs);
        }

        public string Pair(TvInfo tv, Func<string, string, string> askText)
        {
            using (var c = Open(tv.Ip, null, 5000))
            {
                var until = DateTime.UtcNow.AddSeconds(60);   // the TV shows an "allow" prompt
                while (DateTime.UtcNow < until)
                {
                    var msg = c.Receive((int)(until - DateTime.UtcNow).TotalMilliseconds);
                    if (msg == null) break;
                    var ev = TvNet.Field(msg, "event");
                    if (ev == "ms.channel.connect") return TvNet.Field(msg, "token") ?? "";
                    if (ev == "ms.channel.unauthorized") throw new DisplayException("The TV refused the connection");
                }
            }
            throw new DisplayException("No answer to the prompt on the TV");
        }

        public string PowerState(Settings s)
        {
            try {
                var j = TvNet.Http("GET", "http://" + s.TvIp + ":8001/api/v2/", null, null, 1500);
                var p = TvNet.Field(j, "PowerState");
                return p == null || p == "on" ? "on" : "off";   // older models don't report it; reachable = on
            } catch { return "off"; }
        }

        public void TurnOn(Settings s, string state) { TvNet.Wake(s.TvMac, s.TvIp); }

        public void TurnOff(Settings s)
        {
            using (var c = Open(s.TvIp, s.TvKey, 4000))
            {
                c.Receive(3000);   // ms.channel.connect
                c.Send("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"Click\",\"DataOfCmd\":\"KEY_POWER\",\"Option\":\"false\",\"TypeOfRemote\":\"SendRemoteKey\"}}");
                Thread.Sleep(300);
            }
        }
    }

    // ---------------------------------------------------------------- Sony Bravia (pre-shared key)
    public class SonyDriver : ITvDriver
    {
        public string Brand { get { return "sony"; } }
        public string Label { get { return "Sony"; } }
        public bool PairingWorks(Settings s) { try { return TvNet.Field(Call(s.TvIp, s.TvKey, "getPowerStatus", ""), "status") != null; } catch { return false; } }

        static string Call(string ip, string psk, string method, string paramsJson)
        {
            return TvNet.Http("POST", "http://" + ip + "/sony/system",
                "{\"method\":\"" + method + "\",\"id\":1,\"params\":[" + paramsJson + "],\"version\":\"1.0\"}",
                new Dictionary<string, string> { { "X-Auth-PSK", psk ?? "" } }, 3000);
        }

        public string Pair(TvInfo tv, Func<string, string, string> askText)
        {
            var psk = askText("Sony pre-shared key",
                "On the TV, open Settings → Network → Home network → IP control. Set Authentication to \"Normal and Pre-Shared Key\", " +
                "choose a pre-shared key, turn on Remote start, then type the same key here.");
            if (string.IsNullOrEmpty(psk)) throw new DisplayException("Pairing cancelled");
            var r = Call(tv.Ip, psk, "getPowerStatus", "");
            if (TvNet.Field(r, "status") == null) throw new DisplayException("The TV did not accept the key: " + r);
            return psk;
        }

        public string PowerState(Settings s)
        {
            try { return TvNet.Field(Call(s.TvIp, s.TvKey, "getPowerStatus", ""), "status") == "active" ? "on" : "off"; }
            catch { return "off"; }
        }

        public void TurnOn(Settings s, string state)
        {
            try { Call(s.TvIp, s.TvKey, "setPowerStatus", "{\"status\":true}"); } catch { }
            try { TvNet.Wake(s.TvMac, s.TvIp); } catch { }
        }

        public void TurnOff(Settings s) { Call(s.TvIp, s.TvKey, "setPowerStatus", "{\"status\":false}"); }
    }

    // ---------------------------------------------------------------- Roku TV (ECP, no pairing)
    public class RokuDriver : ITvDriver
    {
        public string Brand { get { return "roku"; } }
        public string Label { get { return "Roku"; } }
        public bool PairingWorks(Settings s) { return true; }

        public string Pair(TvInfo tv, Func<string, string, string> askText)
        {
            var x = TvNet.Http("GET", "http://" + tv.Ip + ":8060/query/device-info", null, null, 3000);
            if (TvNet.Tag(x, "is-tv") == "false") throw new DisplayException("That Roku is a streaming stick/box, not a TV, so it can't power a screen");
            return "";
        }

        public string PowerState(Settings s)
        {
            try {
                var mode = TvNet.Tag(TvNet.Http("GET", "http://" + s.TvIp + ":8060/query/device-info", null, null, 1500), "power-mode");
                return mode == "PowerOn" ? "on" : mode == "DisplayOff" ? "screen-off" : "off";
            } catch { return "off"; }
        }

        public void TurnOn(Settings s, string state)
        {
            try { TvNet.Http("POST", "http://" + s.TvIp + ":8060/keypress/PowerOn", null, null, 3000); }
            catch { if (!string.IsNullOrEmpty(s.TvMac)) TvNet.Wake(s.TvMac, s.TvIp); }
        }

        public void TurnOff(Settings s) { TvNet.Http("POST", "http://" + s.TvIp + ":8060/keypress/PowerOff", null, null, 3000); }
    }

    // ---------------------------------------------------------------- custom commands (any TV)
    public class CustomDriver : ITvDriver
    {
        public string Brand { get { return "custom"; } }
        public string Label { get { return "Custom"; } }
        public string Pair(TvInfo tv, Func<string, string, string> askText) { return ""; }
        public bool PairingWorks(Settings s) { return true; }
        public string PowerState(Settings s) { return "unknown"; }
        public void TurnOn(Settings s, string state) { Run(s.TvOnCommand); }
        public void TurnOff(Settings s) { Run(s.TvOffCommand); }

        /// <summary>"https://..." (GET), "POST https://...", or a command line run hidden through cmd.exe.</summary>
        public static void Run(string command)
        {
            var c = (command ?? "").Trim();
            if (c.Length == 0) return;
            var m = Regex.Match(c, @"^(?:(GET|POST)\s+)?(https?://\S+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                TvNet.Http(m.Groups[1].Success ? m.Groups[1].Value.ToUpperInvariant() : "GET", m.Groups[2].Value, null, null, 10000);
                return;
            }
            var psi = new ProcessStartInfo("cmd.exe", "/c " + c) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(psi))
                if (!p.WaitForExit(15000)) throw new DisplayException("The TV command is still running after 15 s");
                else if (p.ExitCode != 0) throw new DisplayException("The TV command failed (exit code " + p.ExitCode + ")");
        }
    }
}
