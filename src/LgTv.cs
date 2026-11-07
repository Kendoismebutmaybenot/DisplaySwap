// LG webOS protocol (SSAP over websocket): pair once (the TV shows an "allow" prompt), power state,
// turn off, turn the screen back on. Turning on from standby is Wake-on-LAN (LgDriver in TvDrivers.cs).
// The pairing manifest (lgtv-pairing.json, embedded) is the one from the MIT-licensed lgtv2 project.
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace DisplaySwap
{
    public static class LgTv
    {
        static TvSocket Open(string ip, int timeoutMs)
        {
            // Newer webOS only accepts wss on 3001 (self-signed certificate); older ones use ws on 3000.
            return new TvSocket(new[] { "wss://" + ip + ":3001", "ws://" + ip + ":3000" }, timeoutMs);
        }

        static string Manifest()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lgtv-pairing.json"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().Trim();
        }

        /// <summary>Register with the TV. With no key the TV shows an "allow" prompt; returns the client key.</summary>
        static string Register(TvSocket c, string key, int timeoutMs)
        {
            var payload = Manifest();
            if (!string.IsNullOrEmpty(key)) payload = "{\"client-key\":\"" + key + "\"," + payload.TrimStart().Substring(1);
            c.Send("{\"type\":\"register\",\"id\":\"register_0\",\"payload\":" + payload + "}");
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var msg = c.Receive((int)(until - DateTime.UtcNow).TotalMilliseconds);
                if (msg == null) break;
                var type = TvNet.Field(msg, "type");
                if (type == "registered") return TvNet.Field(msg, "client-key") ?? key;
                if (type == "error") throw new DisplayException("The TV refused: " + (TvNet.Field(msg, "error") ?? msg));
                // "response" with pairingType PROMPT: the TV is showing the prompt, keep waiting
            }
            throw new DisplayException(string.IsNullOrEmpty(key) ? "No answer to the pairing prompt on the TV" : "The TV did not accept the saved pairing");
        }

        static string Request(TvSocket c, string uri, int timeoutMs)
        {
            var id = "req_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            c.Send("{\"type\":\"request\",\"id\":\"" + id + "\",\"uri\":\"" + uri + "\"}");
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var msg = c.Receive((int)(until - DateTime.UtcNow).TotalMilliseconds);
                if (msg == null) break;
                if (TvNet.Field(msg, "id") != id) continue;
                if (TvNet.Field(msg, "type") == "error") throw new DisplayException("TV: " + (TvNet.Field(msg, "error") ?? msg));
                return msg;
            }
            throw new DisplayException("The TV did not answer " + uri);
        }

        /// <summary>Show the TV's pairing prompt and wait (up to a minute) for the user to accept. Returns the key.</summary>
        public static string Pair(string ip)
        {
            using (var c = Open(ip, 5000)) return Register(c, null, 60000);
        }

        /// <summary>"Active", "Screen Off", "Active Standby", "Suspend"... or null if the TV can't be reached.</summary>
        public static string PowerState(string ip, string key)
        {
            if (!TvNet.Reachable(ip, new[] { 3001, 3000 }, 800)) return null;
            try {
                using (var c = Open(ip, 3000))
                {
                    Register(c, key, 5000);
                    return TvNet.Field(Request(c, "ssap://com.webos.service.tvpower/power/getPowerState", 4000), "state") ?? "Active";
                }
            } catch { return null; }
        }

        public static void TurnOff(string ip, string key)
        {
            using (var c = Open(ip, 4000)) { Register(c, key, 5000); Request(c, "ssap://system/turnOff", 5000); }
        }

        public static void TurnOnScreen(string ip, string key)
        {
            using (var c = Open(ip, 4000)) { Register(c, key, 5000); Request(c, "ssap://com.webos.service.tvpower/power/turnOnScreen", 5000); }
        }
    }
}
