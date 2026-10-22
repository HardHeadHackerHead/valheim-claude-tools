using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// The admin commands: the player hosts (or is a server admin) and asks Claude to act on the server: who is on it, bring or send players,
    /// heal, effects, messages, the world clock, saving, kicking and banning (docs/admin-design.md). They act through calls the game itself
    /// accepts on every character and carries out in that player's own game (RPC_TeleportTo, RPC_Heal, RPC_AddStatusEffect, Message), so the
    /// other player needs no mod, and through the server's own tools. Off until the player turns AllowAdmin on. Every action goes into
    /// BepInEx/claude/admin.log, and a player it touches sees who did it.
    /// </summary>
    public partial class ValheimPlugin
    {
        private ConfigEntry<bool> _allowAdmin;
        private static readonly Dictionary<ZDOID, Vector3> TeleportedFrom = new Dictionary<ZDOID, Vector3>();   // for "tp <who> back"

        private const string Targets = "<who> is a character name (or the start of one), me, or all";

        private void RegisterAdmin()
        {
            Builtin("players", "players: everyone on the server (the server's own list, not only those near you): name, position, distance from you, " +
                "health and whether dead (from their character), and which one is you", (a, output, error) => Players(output, error));
            Builtin("bring", $"bring <who>: teleport players to stand beside you ({Targets}; all: everyone else). They see who did it. Needs AllowAdmin",
                (a, output, error) => Bring(a, output, error));
            Builtin("goto", "goto <who>: teleport yourself beside a player, wherever they are. Needs AllowAdmin", (a, output, error) => GoTo(a, output, error));
            Builtin("tp", $"tp <who> to <who> | tp <who> to <x> <z> | tp <who> back: move players beside another player, to a spot on the ground (in an area " +
                "nobody has loaded, on the map's own ground height: dug or raised ground there isn't known), or back where the last tp took them from. " +
                $"{Targets}. Needs AllowAdmin", (a, output, error) => Tp(a, output, error));
            Builtin("heal", $"heal <who> [hp]: heal players (all their health if no hp given; not the dead). {Targets}. Needs AllowAdmin", (a, output, error) => Heal(a, output, error));
            Builtin("effect", $"effect <who> <effect>: give players a status effect for its usual time (Rested, Wet, Burning, a mead's effect, a boss power: " +
                $"`effect me list` lists them). {Targets}. Needs AllowAdmin", (a, output, error) => Effect(a, output, error));
            Builtin("tell", $"tell <who> <text>: a message in the middle of those players' screens. {Targets}. Needs AllowAdmin", (a, output, error) => Tell(a, output, error));
            Builtin("announce", "announce <text>: a message in the middle of everyone's screen. Needs AllowAdmin", (a, output, error) => Announce(a, output, error));
            Builtin("worldtime", "worldtime | worldtime <hh:mm> | worldtime skip <hours>: the world's clock, for everyone (only forward, as sleeping does: " +
                "08:00 when it is 20:00 is tomorrow morning). daytime only changes this screen. The host only; needs AllowAdmin", (a, output, error) => WorldTime(a, output, error));
            Builtin("save", "save: save the world and the characters now, as the game's console save does. The host only; needs AllowAdmin", (a, output, error) => Save(output, error));
            Builtin("kick", "kick <who>: disconnect a player (they can join again). The host only; needs AllowAdmin", (a, output, error) => KickBan("kick", a, output, error));
            Builtin("ban", "ban <who>: disconnect a player and keep them out (unban lets them back). Only when the player asked for exactly that. The host only; needs AllowAdmin",
                (a, output, error) => KickBan("ban", a, output, error));
            Builtin("unban", "unban <name>: let a banned player join again. The host only; needs AllowAdmin", (a, output, error) => KickBan("unban", a, output, error));
        }

        /// <summary>Its setting lives in Claude Tools' own file (com.quad.claudetools.cfg, section Requests).</summary>
        private void BindAdmin(ConfigFile config)
        {
            _allowAdmin = config.Bind("Requests", "AllowAdmin", false,
                "Let requests act as the server's admin for you, when you host (or are on the server's admin list): bring and send players, heal them, " +
                "give them effects, message them, set the world's clock, save, kick and ban. The players it touches see who did it, and every action " +
                "is written to BepInEx/claude/admin.log.");
        }

        // ---- who is on the server ----

        internal sealed class Online
        {
            public string Name;
            public ZDOID Id;
            public ZDO Zdo;
            public bool Me;
            public Vector3? Position;
        }

        /// <summary>Everyone on the server, from the server's own list (Player.GetAllPlayers only has the characters loaded near this game).</summary>
        internal static List<Online> OnlinePlayers()
        {
            var list = new List<Online>();
            if (ZNet.instance == null) return list;
            Player me = Player.m_localPlayer;
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                ZDO zdo = info.m_characterID.IsNone() || ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(info.m_characterID);
                list.Add(new Online
                {
                    Name = info.m_name, Id = info.m_characterID, Zdo = zdo,
                    Me = me != null && info.m_characterID == me.GetZDOID(),
                    Position = zdo != null ? zdo.GetPosition() : info.m_publicPosition ? info.m_position : (Vector3?)null,
                });
            }
            if (me != null && !list.Any(o => o.Me))   // (single player: the list can be empty)
                list.Insert(0, new Online { Name = me.GetPlayerName(), Id = me.GetZDOID(), Zdo = me.GetComponent<ZNetView>()?.GetZDO(), Me = true, Position = me.transform.position });
            return list;
        }

        /// <summary>The players a name means: me, all (everyone, or everyone else), an exact name, or the only name starting with it.</summary>
        private static List<Online> Find(string who, bool allButMe, Action<string> error)
        {
            List<Online> online = OnlinePlayers();
            if (string.IsNullOrEmpty(who)) { error("say who: " + Targets); return null; }
            if (who.Equals("me", StringComparison.OrdinalIgnoreCase)) return online.Where(o => o.Me).ToList();
            if (who.Equals("all", StringComparison.OrdinalIgnoreCase)) return online.Where(o => !allButMe || !o.Me).ToList();
            List<Online> exact = online.Where(o => string.Equals(o.Name, who, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            List<Online> start = online.Where(o => o.Name != null && o.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase)).ToList();
            if (start.Count == 1) return start;
            error(start.Count == 0 ? $"nobody called {who} is on the server (on it: {string.Join(", ", online.Select(o => o.Name))})"
                                   : $"{who} could be {string.Join(" or ", start.Select(o => o.Name))}: say which");
            return null;
        }

        private IEnumerator Players(Action<JObject> output, Action<string> error)
        {
            if (ZNet.instance == null) { error("not in a world"); yield break; }
            Vector3? mine = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (Vector3?)null;
            var list = new JArray();
            foreach (Online o in OnlinePlayers())
            {
                var row = new JObject { ["name"] = o.Name, ["you"] = o.Me };
                if (o.Position is Vector3 p)
                {
                    row["position"] = Vec(p);
                    if (mine is Vector3 m) row["distance"] = Math.Round(Vector3.Distance(p, m), 1);
                }
                else row["position"] = "unknown (not shared)";
                if (o.Zdo != null)
                {
                    float hp = o.Zdo.GetFloat(ZDOVars.s_health, -1f);
                    row["health"] = hp < 0f ? (JToken)"full" : Math.Round(hp, 1);   // (the game drops the value at full health)
                    row["dead"] = o.Zdo.GetBool(ZDOVars.s_dead);
                }
                list.Add(row);
            }
            output(new JObject
            {
                ["host"] = ZNet.instance.IsServer(), ["youAreAdmin"] = ZNet.instance.LocalPlayerIsAdminOrHost(), ["adminCommands"] = _allowAdmin.Value ? "on" : "off (AllowAdmin)",
                ["players"] = list,
            });
        }

        // ---- who may ----

        private bool AdminRefused(string what, bool hostOnly, Action<string> error)
        {
            if (!_allowAdmin.Value) { error($"admin commands are off (AllowAdmin, section Requests, in com.quad.claudetools.cfg: the player turns it on)"); return true; }
            if (ZNet.instance == null || ZDOMan.instance == null || ZRoutedRpc.instance == null) { error($"not in a world"); return true; }
            if (ZNet.instance.IsServer()) return false;
            if (hostOnly) { error($"only the host can do that"); return true; }
            if (!ZNet.instance.LocalPlayerIsAdminOrHost()) { error($"only the host or a server admin can do that"); return true; }
            return false;
        }

        private static string AdminName => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "The host";

        /// <summary>Into admin.log (and BepInEx's log): every admin action, done or not.</summary>
        private void AdminLog(string line)
        {
            Logger.LogInfo("Admin: " + line);
            try { File.AppendAllText(Path.Combine(Folder, "admin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); }
            catch (Exception e) { Logger.LogWarning("Admin: couldn't write admin.log: " + e.Message); }
        }

        /// <summary>A short line at the top left of a player's screen: who did something to them.</summary>
        private static void Notify(Online o, string text)
        {
            if (o.Me) { Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, text); return; }
            if (o.Zdo != null) ZRoutedRpc.instance.InvokeRoutedRPC(o.Zdo.GetOwner(), o.Id, "Message", (int)MessageHud.MessageType.TopLeft, text, 0);
        }

        // ---- moving players ----

        /// <summary>
        /// Where to put someone beside a standing player: a step in front of them on the ground found there, or (a wall, a roof, a drop in the
        /// way) right where they stand. The game sets a teleported player at exactly this height, so it is always ground somebody stands on.
        /// </summary>
        private static Vector3 Beside(Vector3 at, Quaternion facing)
        {
            Vector3 forward = facing * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Vector3 spot = at + forward.normalized * 1.5f;
            if (ZoneSystem.instance.IsZoneLoaded(spot) && ZoneSystem.instance.GetSolidHeight(spot, out float h) && Mathf.Abs(h - at.y) < 1f)
                return new Vector3(spot.x, h + 0.05f, spot.z);
            return at + Vector3.up * 0.05f;
        }

        /// <summary>Start one player's teleport (in their own game). Far moves load the area first, with the loading screen.</summary>
        private static void Send(Online o, Vector3 to, Quaternion rot)
        {
            bool far = o.Position is Vector3 from && Vector3.Distance(from, to) > 64f;
            if (o.Me) { Player.m_localPlayer.TeleportTo(to, rot, far); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(o.Zdo.GetOwner(), o.Id, "RPC_TeleportTo", to, rot, far);
        }

        /// <summary>Teleport these players to spots, then check where they are a few seconds later (their own game may refuse: dead, or just teleported).</summary>
        private IEnumerator Move(List<(Online who, Vector3 to, Quaternion rot)> moves, string why, Action<JObject> output)
        {
            foreach (var m in moves)
            {
                if (m.who.Zdo == null && !m.who.Me) continue;
                if (m.who.Position is Vector3 was) TeleportedFrom[m.who.Id] = was;
                Send(m.who, m.to, m.rot);
                if (!m.who.Me) Notify(m.who, $"{AdminName} (admin) {why}");
            }
            float waited = 0f;
            while (waited < 12f)
            {
                yield return new WaitForSeconds(1f);
                waited += 1f;
                if (waited >= 3f && moves.All(m => Arrived(m.who, m.to))) break;
            }
            var done = new JArray();
            foreach (var m in moves)
            {
                bool there = Arrived(m.who, m.to);
                Vector3? now = PositionOf(m.who);
                done.Add(new JObject
                {
                    ["player"] = m.who.Name, ["to"] = Vec(m.to), ["arrived"] = there,
                    ["now"] = now is Vector3 n ? Vec(n) : null,
                    ["note"] = there ? null : m.who.Zdo == null && !m.who.Me ? "no character (still loading in?)" : "not there yet: dead, in a loading screen, or teleported in the last 2 seconds (try again)",
                });
                AdminLog($"teleport {m.who.Name} to {m.to.x:0},{m.to.y:0},{m.to.z:0} ({why}): {(there ? "arrived" : "not arrived")}");
            }
            output(new JObject { ["moved"] = done });
        }

        private static Vector3? PositionOf(Online o)
        {
            if (o.Me && Player.m_localPlayer != null) return Player.m_localPlayer.transform.position;
            return o.Zdo != null ? o.Zdo.GetPosition() : (Vector3?)null;
        }

        private static bool Arrived(Online o, Vector3 to) => PositionOf(o) is Vector3 p && Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(to.x, 0f, to.z)) < 4f && Mathf.Abs(p.y - to.y) < 4f;

        private IEnumerator Bring(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("bring", false, error)) yield break;
            Player me = Player.m_localPlayer;
            if (me == null) { error("no character of yours in the world"); yield break; }
            if (!me.IsOnGround()) { error("stand on the ground first (they land where you stand)"); yield break; }
            List<Online> who = Find(Rest(a, 1), true, error);
            if (who == null) yield break;
            who = who.Where(o => !o.Me).ToList();
            if (who.Count == 0) { error("nobody else to bring"); yield break; }
            var moves = new List<(Online, Vector3, Quaternion)>();
            for (int i = 0; i < who.Count; i++)
            {
                Quaternion round = me.transform.rotation * Quaternion.Euler(0f, i * 360f / Math.Max(1, who.Count), 0f);   // (several: in a ring, not on top of each other)
                moves.Add((who[i], Beside(me.transform.position, round), Quaternion.LookRotation(-(round * Vector3.forward))));
            }
            yield return Move(moves, "brought you to them", output);
        }

        private IEnumerator GoTo(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("goto", false, error)) yield break;
            List<Online> who = Find(Rest(a, 1), true, error);
            if (who == null) yield break;
            Online target = who.FirstOrDefault(o => !o.Me);
            Online self = OnlinePlayers().FirstOrDefault(o => o.Me);
            if (target == null || self == null) { error("say another player"); yield break; }
            if (!(target.Position is Vector3 at)) { error($"{target.Name}'s position isn't known"); yield break; }
            Quaternion facing = target.Zdo != null ? target.Zdo.GetRotation() : Quaternion.identity;
            Vector3 to = ZoneSystem.instance.IsZoneLoaded(at) ? Beside(at, facing) : at + Vector3.up * 0.05f;   // (far: on them, the ground they stand on)
            yield return Move(new List<(Online, Vector3, Quaternion)> { (self, to, Quaternion.LookRotation(at - to == Vector3.zero ? Vector3.forward : Vector3.ProjectOnPlane(at - to, Vector3.up).normalized)) }, "came to you", output);
        }

        private IEnumerator Tp(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("tp", false, error)) yield break;
            if (a.Length < 3) { error("tp <who> to <who> | tp <who> to <x> <z> | tp <who> back"); yield break; }
            List<Online> who = Find(a[1], false, error);
            if (who == null) yield break;
            var moves = new List<(Online, Vector3, Quaternion)>();
            if (a[2].Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Online o in who)
                    if (TeleportedFrom.TryGetValue(o.Id, out Vector3 was)) moves.Add((o, was + Vector3.up * 0.05f, Quaternion.identity));
                    else error($"{o.Name} hasn't been teleported by Claude Tools since the game started");
                if (moves.Count > 0) yield return Move(moves, "sent you back", output);
                yield break;
            }
            int at = a[2].Equals("to", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            if (a.Length > at + 1 && float.TryParse(a[at], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                && float.TryParse(a[at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            {
                var spot = new Vector3(x, 0f, z);
                string ground;
                if (ZoneSystem.instance.IsZoneLoaded(spot) && ZoneSystem.instance.GetSolidHeight(spot, out float h)) { spot.y = h + 0.05f; ground = "the ground there (loaded)"; }
                else if (WorldGenerator.instance != null) { spot.y = WorldGenerator.instance.GetHeight(x, z) + 0.3f; ground = "the map's own ground height (the area isn't loaded: dug or raised ground there isn't known)"; }
                else { error("no world"); yield break; }
                if (spot.y < ZoneSystem.instance.m_waterLevel - 0.5f) ground += "; it is under water there";
                for (int i = 0; i < who.Count; i++) moves.Add((who[i], spot + new Vector3(i * 1.2f, 0f, 0f), Quaternion.identity));
                output(new JObject { ["landing"] = ground });
                yield return Move(moves, $"teleported you to {x:0},{z:0}", output);
                yield break;
            }
            List<Online> dest = Find(Rest(a, at), false, error);
            if (dest == null) yield break;
            if (dest.Count != 1) { error("say one player to go to"); yield break; }
            Online target = dest[0];
            if (!(target.Position is Vector3 tpos)) { error($"{target.Name}'s position isn't known"); yield break; }
            Quaternion facing = target.Me && Player.m_localPlayer != null ? Player.m_localPlayer.transform.rotation : target.Zdo != null ? target.Zdo.GetRotation() : Quaternion.identity;
            int n = 0, count = who.Count(o => o.Id != target.Id);
            foreach (Online o in who.Where(o => o.Id != target.Id))
            {
                Quaternion round = facing * Quaternion.Euler(0f, n++ * 360f / Math.Max(1, count), 0f);
                Vector3 to = ZoneSystem.instance.IsZoneLoaded(tpos) ? Beside(tpos, round) : tpos + Vector3.up * 0.05f;
                moves.Add((o, to, Quaternion.LookRotation(-(round * Vector3.forward))));
            }
            if (moves.Count == 0) { error("they are the same player"); yield break; }
            yield return Move(moves, $"teleported you to {target.Name}", output);
        }

        // ---- care and messages ----

        private IEnumerator Heal(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("heal", false, error)) yield break;
            List<Online> who = Find(a.Length > 1 ? a[1] : "", false, error);
            if (who == null) yield break;
            float hp = a.Length > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? Mathf.Max(1f, v) : 10000f;
            var done = new JArray();
            foreach (Online o in who)
            {
                if (o.Zdo == null) { done.Add(new JObject { ["player"] = o.Name, ["healed"] = false, ["why"] = "no character" }); continue; }
                if (o.Zdo.GetBool(ZDOVars.s_dead)) { done.Add(new JObject { ["player"] = o.Name, ["healed"] = false, ["why"] = "dead" }); continue; }
                ZRoutedRpc.instance.InvokeRoutedRPC(o.Zdo.GetOwner(), o.Id, "RPC_Heal", hp, true);
                if (!o.Me) Notify(o, $"{AdminName} (admin) healed you");
                AdminLog($"heal {o.Name} by {(hp >= 10000f ? "all" : hp.ToString("0"))}");
                done.Add(new JObject { ["player"] = o.Name, ["healed"] = true });
            }
            yield return new WaitForSeconds(1f);
            foreach (JObject row in done.Cast<JObject>())
            {
                Online o = who.First(x => x.Name == (string)row["player"]);
                if (o.Zdo != null) { float now = o.Zdo.GetFloat(ZDOVars.s_health, -1f); row["health"] = now < 0f ? (JToken)"full" : Math.Round(now, 1); }
            }
            output(new JObject { ["heal"] = done });
        }

        private IEnumerator Effect(string[] a, Action<JObject> output, Action<string> error)
        {
            if (a.Length > 2 && a[2].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                output(new JObject { ["effects"] = new JArray(ObjectDB.instance.m_StatusEffects.Where(e => e != null).Select(e => e.name).OrderBy(n => n)) });
                yield break;
            }
            if (AdminRefused("effect", false, error)) yield break;
            if (a.Length < 3) { error("effect <who> <effect> (effect me list: the effects)"); yield break; }
            List<Online> who = Find(a[1], false, error);
            if (who == null) yield break;
            string want = Rest(a, 2);
            StatusEffect se = ObjectDB.instance.m_StatusEffects.FirstOrDefault(e => e != null && e.name.Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? ObjectDB.instance.m_StatusEffects.FirstOrDefault(e => e != null && Localization.instance.Localize(e.m_name).Equals(want, StringComparison.OrdinalIgnoreCase));
            if (se == null) { error($"no status effect called {want} (effect me list: the effects)"); yield break; }
            var done = new JArray();
            foreach (Online o in who)
            {
                if (o.Zdo == null) continue;
                ZRoutedRpc.instance.InvokeRoutedRPC(o.Zdo.GetOwner(), o.Id, "RPC_AddStatusEffect", se.NameHash(), true, 0, 0f, 0);
                if (!o.Me) Notify(o, $"{AdminName} (admin) gave you {Localization.instance.Localize(se.m_name)}");
                AdminLog($"effect {se.name} on {o.Name}");
                done.Add(o.Name);
            }
            output(new JObject { ["effect"] = se.name, ["given"] = done });
        }

        private IEnumerator Tell(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("tell", false, error)) yield break;
            List<Online> who = Find(a.Length > 1 ? a[1] : "", false, error);
            if (who == null) yield break;
            string text = Rest(a, 2);
            if (text.Length == 0) { error("tell <who> <text>"); yield break; }
            foreach (Online o in who)
            {
                if (o.Me) Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
                else if (o.Zdo != null) ZRoutedRpc.instance.InvokeRoutedRPC(o.Zdo.GetOwner(), o.Id, "Message", (int)MessageHud.MessageType.Center, text, 0);
            }
            AdminLog($"tell {string.Join(", ", who.Select(o => o.Name))}: {text}");
            output(new JObject { ["told"] = new JArray(who.Select(o => o.Name)), ["text"] = text });
        }

        private IEnumerator Announce(string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("announce", false, error)) yield break;
            string text = Rest(a, 1);
            if (text.Length == 0) { error("announce <text>"); yield break; }
            MessageHud.instance.MessageAll(MessageHud.MessageType.Center, text);
            AdminLog("announce: " + text);
            output(new JObject { ["announced"] = text, ["to"] = new JArray(OnlinePlayers().Select(o => o.Name)) });
        }

        // ---- the world ----

        /// <summary>The clock the game shows (rescaled: 06:00 to 18:00 take 70% of the day) back to the raw fraction of the day.</summary>
        private static double RawDayFraction(float shown)
        {
            if (shown >= 0.25f && shown <= 0.75f) return 0.15 + (shown - 0.25) / 0.5 * 0.7;
            if (shown < 0.25f) return shown / 0.25 * 0.15;
            return 0.85 + (shown - 0.75) / 0.25 * 0.15;
        }

        private IEnumerator WorldTime(string[] a, Action<JObject> output, Action<string> error)
        {
            EnvMan env = EnvMan.instance;
            if (env == null || ZNet.instance == null) { error("not in a world"); yield break; }
            double now = ZNet.instance.GetTimeSeconds(), len = env.m_dayLengthSec;
            JObject Show(double t) => new JObject { ["day"] = env.GetDay(t), ["time"] = Clock(Shown((float)(t % len / len))) };
            if (a.Length < 2) { output(Show(now)); yield break; }
            if (AdminRefused("worldtime", true, error)) yield break;
            double target;
            if (a[1].Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length < 3 || !double.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours) || hours <= 0 || hours > 240)
                { error("worldtime skip <hours> (more than 0, up to 240)"); yield break; }
                target = now + hours / 24.0 * len;
            }
            else
            {
                string[] hm = a[1].Split(':');
                if (hm.Length != 2 || !int.TryParse(hm[0], out int h) || !int.TryParse(hm[1], out int m) || h < 0 || h > 23 || m < 0 || m > 59)
                { error("worldtime <hh:mm>, e.g. worldtime 08:00"); yield break; }
                double dayStart = Math.Floor(now / len) * len;
                target = dayStart + RawDayFraction((h * 60 + m) / 1440f) * len;
                if (target <= now) target += len;   // (only forward: the clock never goes back)
            }
            JObject before = Show(now);
            ZNet.instance.SetNetTime(target);
            AdminLog($"worldtime from day {before["day"]} {before["time"]} to day {env.GetDay(target)} {Clock(Shown((float)(target % len / len)))}");
            output(new JObject { ["from"] = before, ["to"] = Show(target) });
        }

        /// <summary>The raw fraction of the day as the game shows it (EnvMan's rescale).</summary>
        private static float Shown(float raw)
        {
            if (raw >= 0.15f && raw <= 0.85f) return 0.25f + (raw - 0.15f) / 0.7f * 0.5f;
            if (raw < 0.5f) return raw / 0.15f * 0.25f;
            return 0.75f + (raw - 0.85f) / 0.15f * 0.25f;
        }

        private IEnumerator Save(Action<JObject> output, Action<string> error)
        {
            if (AdminRefused("save", true, error)) yield break;
            ZNet.instance.SaveWorldAndPlayerProfiles();
            AdminLog("save");
            output(new JObject { ["saved"] = true, ["world"] = ZNet.instance.GetWorldName() });
        }

        private IEnumerator KickBan(string what, string[] a, Action<JObject> output, Action<string> error)
        {
            if (AdminRefused(what, true, error)) yield break;
            string name = Rest(a, 1);
            if (name.Length == 0) { error($"{what} <who>"); yield break; }
            if (what != "unban")
            {
                List<Online> who = Find(name, true, error);
                if (who == null) yield break;
                if (who.Count != 1 || who[0].Me) { error($"say one other player"); yield break; }
                name = who[0].Name;
                if (what == "kick") ZNet.instance.Kick(name); else ZNet.instance.Ban(name);
            }
            else ZNet.instance.Unban(name);
            AdminLog($"{what} {name}");
            yield return new WaitForSeconds(1f);
            output(new JObject { [what] = name, ["online"] = new JArray(OnlinePlayers().Select(o => o.Name)) });
        }
    }
}
