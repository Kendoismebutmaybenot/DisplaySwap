// Check for updates (Settings): asks GitHub for the latest release and, when it is newer, puts the
// DisplaySwap.exe from its zip in place of this one and starts it. Downloads only from this project's own
// GitHub releases, over HTTPS, and checks the zip against the SHA-256 that GitHub lists for it.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace DisplaySwap
{
    [DataContract]
    public class GhRelease
    {
        [DataMember(Name = "tag_name")] public string Tag;
        [DataMember(Name = "html_url")] public string Url;
        [DataMember(Name = "assets")] public List<GhAsset> Assets;
    }

    [DataContract]
    public class GhAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "browser_download_url")] public string Download;
        [DataMember(Name = "digest")] public string Digest;   // "sha256:<hex>"
    }

    public class UpdateInfo
    {
        public Version Version;
        public string Tag, PageUrl, ZipUrl, Sha256;
    }

    public static class Updater
    {
        public const string Repo = "Kendoismebutmaybenot/DisplaySwap";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
        const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        static HttpWebRequest Request(string url)
        {
            // .NET Framework may still default to TLS 1.0, which GitHub refuses.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "DisplaySwap/" + Program.Version;
            r.Timeout = 20000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        /// <summary>The latest release on GitHub (whether or not it is newer than this version).</summary>
        public static UpdateInfo Check()
        {
            var r = Request("https://api.github.com/repos/" + Repo + "/releases/latest");
            r.Accept = "application/vnd.github+json";
            GhRelease rel;
            using (var resp = r.GetResponse())
            using (var st = resp.GetResponseStream())
                rel = (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(st);
            Version v;
            if (rel == null || !Version.TryParse((rel.Tag ?? "").TrimStart('v', 'V'), out v))
                throw new Exception("GitHub sent an answer DisplaySwap doesn't understand.");
            var u = new UpdateInfo { Version = v, Tag = rel.Tag, PageUrl = rel.Url ?? ReleasesPage };
            var zip = (rel.Assets ?? new List<GhAsset>()).FirstOrDefault(a => a.Name != null
                && a.Name.StartsWith("DisplaySwap-v", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip != null)
            {
                u.ZipUrl = zip.Download;
                if (zip.Digest != null && zip.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    u.Sha256 = zip.Digest.Substring(7).ToLowerInvariant();
            }
            return u;
        }

        public static bool IsNewer(UpdateInfo u) { return u != null && Normal(u.Version) > Normal(Version.Parse(Program.Version)); }
        static Version Normal(Version v) { return new Version(v.Major, v.Minor, Math.Max(v.Build, 0)); }

        /// <summary>False when the exe's folder can't be written to (e.g. Program Files): then it can't update itself.</summary>
        public static bool CanInstallHere
        {
            get { return string.Equals(Path.GetFullPath(Store.Dir).TrimEnd('\\'), Path.GetFullPath(ExeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        }
        static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }

        /// <summary>Download the release and put its DisplaySwap.exe in place of this one. The running exe is
        /// renamed to DisplaySwap.exe.old (Windows allows renaming a running exe, not overwriting it) and
        /// deleted by the new one when it starts. Returns the path to start. Nothing changes if any check fails.</summary>
        public static string Install(UpdateInfo u, Action<string> progress, string exe = null)
        {
            exe = exe ?? ExePath;
            if (u.ZipUrl == null || !u.ZipUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
                throw new Exception("The release on GitHub has no DisplaySwap download.");
            string zipPath = Path.Combine(Path.GetTempPath(), "DisplaySwap-update-" + Guid.NewGuid().ToString("N") + ".zip");
            string fresh = exe + ".new", old = exe + ".old";
            try
            {
                progress("Downloading " + u.Tag + "...");
                using (var resp = Request(u.ZipUrl).GetResponse())
                using (var st = resp.GetResponseStream())
                using (var f = File.Create(zipPath))
                    st.CopyTo(f);

                if (u.Sha256 != null)
                {
                    string got;
                    using (var sha = SHA256.Create())
                    using (var f = File.OpenRead(zipPath))
                        got = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (got != u.Sha256) throw new Exception("The download doesn't match the release on GitHub (checksum differs).");
                }

                using (var z = ZipFile.OpenRead(zipPath))
                {
                    var entry = z.Entries.FirstOrDefault(e => string.Equals(e.Name, "DisplaySwap.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) throw new Exception("The download has no DisplaySwap.exe in it.");
                    if (File.Exists(fresh)) File.Delete(fresh);
                    entry.ExtractToFile(fresh);
                }
                var got2 = Normal(AssemblyName.GetAssemblyName(fresh).Version);
                if (got2 != Normal(u.Version)) throw new Exception("The downloaded app says it is v" + got2 + ", not " + u.Tag + ".");

                progress("Installing " + u.Tag + "...");
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch { File.Move(old, exe); throw; }
                return exe;
            }
            catch (UnauthorizedAccessException)
            {
                throw new Exception("DisplaySwap can't write to its folder (" + Path.GetDirectoryName(exe) + ").");
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
                try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
            }
        }

        /// <summary>Delete the exe an update left behind (called when the new one starts).</summary>
        public static void CleanUp()
        {
            var old = ExePath + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { System.Threading.Thread.Sleep(300); }
            }
        }

        public static string Explain(Exception e)
        {
            var we = e as WebException;
            if (we == null) return Sentence(e.Message);
            var resp = we.Response as HttpWebResponse;
            if (resp != null && (int)resp.StatusCode == 403) return "GitHub is limiting requests right now, try again in a while.";
            if (resp != null && (int)resp.StatusCode == 404) return "no release found on GitHub.";
            return "couldn't reach GitHub (" + we.Message.TrimEnd('.') + ").";
        }
        static string Sentence(string s) { s = (s ?? "").Trim(); return s.EndsWith(".") ? s : s + "."; }
    }
}
