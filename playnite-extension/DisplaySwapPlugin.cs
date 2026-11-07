// Playnite extension for DisplaySwap.
// Right-click a game -> "Monitor profile" -> pick a DisplaySwap profile. The choice is stored as a
// "DisplaySwap: <profile>" tag. When the game starts, DisplaySwap switches to that profile (remembering
// the current setup); when it stops, DisplaySwap switches back.
// Built with the .NET Framework csc against Playnite.SDK (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace DisplaySwapPlaynite
{
    public class DisplaySwapPlugin : GenericPlugin
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly Guid id = new Guid("73680185-fef5-47b8-bf1b-c9c5afc8f1e9");
        const string TagPrefix = "DisplaySwap: ";
        const string Section = "Monitor profile";
        // DisplaySwap records its location in the registry every time it runs, so it can live anywhere.
        static string ExePath
        {
            get
            {
                try {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\DisplaySwap"))
                    {
                        var p = k == null ? null : k.GetValue("ExePath") as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                } catch { }
                return null;
            }
        }

        readonly HashSet<Guid> switchedFor = new HashSet<Guid>();
        Task pendingRestore = Task.FromResult(0);
        List<string> profileCache; DateTime profileCacheTime;
        string CheckIcon { get { return Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "check.png"); } }

        public override Guid Id { get { return id; } }

        public DisplaySwapPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
        }

        // ---------------- DisplaySwap.exe ----------------
        static int Run(string args, int timeoutMs, out string output)
        {
            output = "";
            var exe = ExePath;
            if (exe == null) { output = "DisplaySwap not found. Start DisplaySwap.exe once so Playnite can find it."; return -1; }
            try
            {
                var psi = new ProcessStartInfo(exe, args) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "DisplaySwap did not finish in time"; return -1; }
                    output = (outTask.Result + "\n" + errTask.Result).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception e) { output = e.Message; return -1; }
        }

        List<string> Profiles()
        {
            if (profileCache != null && (DateTime.Now - profileCacheTime).TotalSeconds < 5) return profileCache;
            string o;
            var list = Run("--list", 5000, out o) == 0
                ? o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                : new List<string>();
            profileCache = list; profileCacheTime = DateTime.Now;
            return list;
        }

        static string ProfileOf(Game g)
        {
            if (g == null || g.Tags == null) return null;
            var t = g.Tags.FirstOrDefault(x => x.Name != null && x.Name.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase));
            return t == null ? null : t.Name.Substring(TagPrefix.Length).Trim();
        }

        void Notify(string key, string text, NotificationType type)
        {
            PlayniteApi.Notifications.Add(new NotificationMessage("DisplaySwap_" + key, "DisplaySwap: " + text, type));
        }

        // ---------------- right-click menu ----------------
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var items = new List<GameMenuItem>();
            if (ExePath == null)
            {
                items.Add(new GameMenuItem { MenuSection = Section, Description = "DisplaySwap not found: start DisplaySwap.exe once" });
                return items;
            }

            var games = args.Games;
            var chosen = games.Select(ProfileOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string common = chosen.Count == 1 ? chosen[0] : "\u0001mixed";

            var profiles = Profiles();
            foreach (var name in profiles)
            {
                var prof = name;
                items.Add(new GameMenuItem {
                    MenuSection = Section,
                    Description = prof,
                    Icon = string.Equals(common, prof, StringComparison.OrdinalIgnoreCase) ? CheckIcon : null,
                    Action = a => SetProfile(a.Games, prof),
                });
            }
            // A tag pointing at a profile that was renamed/deleted in DisplaySwap.
            foreach (var orphan in chosen.Where(c => c != null && !profiles.Any(p => string.Equals(p, c, StringComparison.OrdinalIgnoreCase))))
                items.Add(new GameMenuItem { MenuSection = Section, Description = orphan + "  (profile no longer exists)", Icon = CheckIcon });

            if (profiles.Count == 0)
                items.Add(new GameMenuItem { MenuSection = Section, Description = "(no profiles yet: save one in DisplaySwap)" });

            items.Add(new GameMenuItem {
                MenuSection = Section,
                Description = "Don't switch",
                Icon = common == null ? CheckIcon : null,
                Action = a => SetProfile(a.Games, null),
            });
            items.Add(new GameMenuItem { MenuSection = Section, Description = "-" });
            items.Add(new GameMenuItem {
                MenuSection = Section, Description = "Open DisplaySwap",
                Action = a => { try { Process.Start(ExePath); } catch (Exception e) { Notify("open", e.Message, NotificationType.Error); } },
            });
            return items;
        }

        void SetProfile(List<Game> games, string profile)
        {
            var db = PlayniteApi.Database;
            Tag tag = null;
            if (profile != null)
            {
                var name = TagPrefix + profile;
                tag = db.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? db.Tags.Add(name);
            }
            var ours = new HashSet<Guid>(db.Tags.Where(t => t.Name != null && t.Name.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id));
            using (db.Games.BufferedUpdate())
            {
                foreach (var g in games)
                {
                    if (g.TagIds == null) g.TagIds = new List<Guid>();
                    g.TagIds.RemoveAll(ours.Contains);
                    if (tag != null) g.TagIds.Add(tag.Id);
                    db.Games.Update(g);
                }
            }
            // Drop DisplaySwap tags no game uses any more.
            foreach (var t in db.Tags.Where(t => ours.Contains(t.Id) && (tag == null || t.Id != tag.Id)).ToList())
                if (!db.Games.Any(g => g.TagIds != null && g.TagIds.Contains(t.Id))) db.Tags.Remove(t);
        }

        // ---------------- game start / stop ----------------
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var prof = ProfileOf(args.Game);
            if (prof == null) return;

            string output = null; int rc = 0;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                try { pendingRestore.Wait(60000); } catch { }   // a previous game's switch-back must finish first
                rc = Run("--apply \"" + prof + "\" --remember", 60000, out output);
            }, new GlobalProgressOptions("Switching to " + prof + "...", false) { IsIndeterminate = true });

            if (rc == 2) { Notify("missing", "there is no profile called '" + prof + "'. Pick another one under Monitor profile.", NotificationType.Error); return; }
            switchedFor.Add(args.Game.Id);   // even on a partial failure the old setup was saved, so switch back later
            if (rc != 0) Notify("apply", "switching to '" + prof + "' had a problem: " + output, NotificationType.Error);
            logger.Info("DisplaySwap: " + args.Game.Name + " -> " + prof + " (exit " + rc + ")");
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args) { RestoreAfter(args.Game); }
        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args) { RestoreAfter(args.Game); }

        void RestoreAfter(Game g)
        {
            if (g == null || !switchedFor.Remove(g.Id)) return;
            pendingRestore = Task.Run(() =>
            {
                string o;
                int rc = Run("--restore", 60000, out o);
                if (rc != 0) Notify("restore", "switching back had a problem: " + o, NotificationType.Error);
                logger.Info("DisplaySwap: restored after " + g.Name + " (exit " + rc + ")");
            });
        }
    }
}
