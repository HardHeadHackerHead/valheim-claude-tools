using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// The request mailbox and its commands. Other mods add commands and named places through the public static methods below, called by
    /// reflection (so they need no reference to this mod and work without it installed):
    ///
    ///   RegisterCommand(owner, name, usage, Func&lt;string[] args, Action&lt;JObject&gt; output, Action&lt;string&gt; error, IEnumerator&gt;)
    ///   RegisterFrame(owner, Func&lt;string name, float[] {x, y, z, yaw}&gt;)    a named place that view/orbit/top/survey can point at
    ///   UnregisterAll(owner)
    ///
    /// args[0] is the command's name; a command may return null (done at once) or an IEnumerator to take its time (yield as in a coroutine).
    /// Both mods may hot-reload: a mod registers again when it sees a new Claude Tools, and its own registrations replace its old ones.
    ///
    /// A game's adapter (ClaudeTools.Valheim and the like) references this DLL instead: UseAdapter(its GameAdapter), then its commands through
    /// RegisterCommand like any mod, which count as built in (another mod can't replace them).
    /// </summary>
    public partial class Plugin
    {
        public const int ApiVersion = 1;

        internal class Command
        {
            public string Owner, Name, Usage;
            public Func<string[], Action<JObject>, Action<string>, IEnumerator> Run;
        }

        private static readonly Dictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<string, float[]>> Frames = new Dictionary<string, Func<string, float[]>>();

        // Claude Tools and its game's adapter: their commands are built in
        private static readonly HashSet<string> BuiltinOwners = new HashSet<string> { Name };

        public static void RegisterCommand(string owner, string name, string usage, Func<string[], Action<JObject>, Action<string>, IEnumerator> run)
        {
            if (string.IsNullOrEmpty(name) || run == null) return;
            if (Commands.TryGetValue(name, out Command old) && BuiltinOwners.Contains(old.Owner) && !BuiltinOwners.Contains(owner))
            {
                Log?.LogWarning($"{owner} tried to replace the built-in command '{name}'; keeping the built-in one");
                return;
            }
            Commands[name] = new Command { Owner = owner, Name = name.ToLowerInvariant(), Usage = usage ?? name, Run = run };
        }

        public static void RegisterFrame(string owner, Func<string, float[]> resolve)
        {
            if (resolve != null) Frames[owner] = resolve;
        }

        public static void UnregisterAll(string owner)
        {
            foreach (string key in Commands.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList()) Commands.Remove(key);
            Frames.Remove(owner);
        }

        private static void Builtin(string name, string usage, Func<string[], Action<JObject>, Action<string>, IEnumerator> run) => RegisterCommand(Name, name, usage, run);

        // ---- the game ----

        private static GameAdapter _adapter;
        private static readonly GameAdapter NoAdapter = new AnyGame();

        /// <summary>The game Claude Tools runs in, as its adapter describes it (or the defaults for any Unity game).</summary>
        public static GameAdapter Adapter => _adapter ?? NoAdapter;

        /// <summary>
        /// A game's adapter starts: from now on Claude Tools asks it about the game, and <paramref name="owner"/>'s commands are built in. Its
        /// systems and code go to the mod-maker commands, its guide and skills to BepInEx/claude.
        /// </summary>
        public static void UseAdapter(GameAdapter adapter, string owner)
        {
            _adapter = adapter;
            if (!string.IsNullOrEmpty(owner)) BuiltinOwners.Add(owner);
            UseGameRules(Adapter);
            Log?.LogInfo($"{Name}: the game is {Adapter.Game} ({owner})");
            if (Instance != null) { Instance._guideWritten = true; Instance.WriteGuide(); }
        }

        /// <summary>The adapter is going (its mod unloads): back to the defaults.</summary>
        public static void DropAdapter(GameAdapter adapter, string owner)
        {
            if (_adapter != adapter) return;
            _adapter = null;
            BuiltinOwners.Remove(owner);
            UseGameRules(Adapter);
        }

        private static void UseGameRules(GameAdapter game)
        {
            Compat.UseSystems(game.Systems, game.HighStakes, game.DoesTheJob, game.NotAJob);
            GameIndex.Assemblies = game.GameAssemblies;
            ForgetCaches();   // (the game index and the shipped patch map are the game's)
        }

        // commands that need no world: they run from the main menu too (in the console, and from request files); an adapter adds its own
        private static readonly HashSet<string> NoWorld = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "help", "library", "patches", "who", "clashes", "systems", "mods", "config", "log", "errors", "modcheck", "game", "gameupdate", "waitfor", "reload", "devmods", "screen", "wait" };

        // commands that change the world or hand out items: only where the game itself allows cheats (single player, the host, or a server
        // admin who turned devcommands on), from the console and from request files alike
        private static readonly HashSet<string> Cheats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>These commands run at the main menu too.</summary>
        public static void RunsAtMenu(params string[] names) => NoWorld.UnionWith(names);

        /// <summary>These commands change the world or hand out items: only where the game allows cheats.</summary>
        public static void CheatOnly(params string[] names) => Cheats.UnionWith(names);

        public static bool CheatsAllowed() => Adapter.CheatsAllowed;

        public static string CheatRefusal(string name) => Adapter.CheatRefusal(name);

        // ---- running a command from elsewhere (the game's console: claude <command>) ----

        /// <summary>The command a word names (its name as registered), if there is one.</summary>
        public static bool FindCommand(string word, out string name)
        {
            name = Commands.TryGetValue(word ?? "", out Command cmd) ? cmd.Name : null;
            return name != null;
        }

        /// <summary>It needs the player in a world, and they aren't.</summary>
        public static bool NeedsWorld(string name) => !Adapter.InWorld && !NoWorld.Contains(name);

        /// <summary>It is a cheat, and cheats aren't allowed here.</summary>
        public static bool CheatBlocked(string name) => Cheats.Contains(name) && !CheatsAllowed();

        /// <summary>Start a command (a[0] is its name): null when done, or the steps still to run, as a request runs them.</summary>
        public static IEnumerator RunCommand(string[] a, Action<JObject> output, Action<string> error) => Commands[a[0]].Run(a, output, error);

        /// <summary>Who added a command (its owner as registered), if there is one.</summary>
        public static string CommandOwner(string name) => Commands.TryGetValue(name ?? "", out Command cmd) ? cmd.Owner : null;

        /// <summary>A command's answer as lines of text, for a console.</summary>
        public static IEnumerable<string> TextLines(JToken answer) => Compat.Lines(answer, "");

        /// <summary>
        /// A place to measure camera positions from: "world" (world coordinates), "here" (you, facing where you face), "look" (the spot you
        /// look at, facing where you face), or a name another mod knows (a placed blueprint, for BuildOrders; "last" for the latest one).
        /// </summary>
        public static bool Frame(string token, out Vector3 origin, out float yaw, out string error)
        {
            origin = Vector3.zero; yaw = 0f; error = null;
            Transform p = Adapter.Player;
            token = string.IsNullOrEmpty(token) ? "look" : token;
            if (token == "world") return true;
            if (token == "here" && p != null) { origin = p.position; yaw = p.eulerAngles.y; return true; }
            if (token == "look" && p != null) { origin = LookPoint(out Vector3 hit) ? hit : p.position; yaw = p.eulerAngles.y; return true; }
            foreach (var kv in Frames.ToList())
            {
                try
                {
                    float[] f = kv.Value(token);
                    if (f != null && f.Length >= 4) { origin = new Vector3(f[0], f[1], f[2]); yaw = f[3]; return true; }
                }
                catch (Exception) { }
            }
            error = $"no place called '{token}' (use world, here, look{(Frames.Count > 0 ? ", or a name " + string.Join("/", Frames.Keys.ToArray()) + " knows" : "")})";
            return false;
        }

        // ---- running a request ----

        private IEnumerator RunRequest(string file)
        {
            _busy = true;
            string taken = Path.ChangeExtension(file, ".taken");
            var result = new JObject { ["request"] = Path.GetFileName(file), ["time"] = DateTime.Now.ToString("s") };
            var outputs = new JArray();
            var errors = new JArray();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
                if (lines.Length == 0 && new FileInfo(file).Length == 0) { _busy = false; _nextScan = Time.unscaledTime + 0.1f; yield break; }   // (still being written)
                File.Delete(taken); File.Move(file, taken);
            }
            catch (Exception e) { _busy = false; Logger.LogWarning("Could not read request " + file + ": " + e.Message); yield break; }

            var ran = new List<string>();   // (for the game's adapter: Valheim's Clawd says what Claude did)
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (!Commands.TryGetValue(a[0], out Command cmd)) { errors.Add($"unknown command: {a[0]} (\"help\" lists them)"); continue; }
                if (NeedsWorld(cmd.Name)) { errors.Add($"{cmd.Name}: needs the player in a world ({line})"); continue; }
                if (CheatBlocked(cmd.Name)) { errors.Add(CheatRefusal(cmd.Name)); continue; }

                IEnumerator run = null;
                string name = cmd.Name;
                ran.Add(name);
                Adapter.OnCommand(name, cmd.Owner, a);
                try { run = cmd.Run(a, o => outputs.Add(o), e => errors.Add(name + ": " + e)); }
                catch (Exception e) { errors.Add(name + ": " + e.Message); }
                // a command that takes its time: step through it here, so an error inside it is reported, not lost
                while (run != null)
                {
                    object step;
                    try
                    {
                        if (!run.MoveNext()) break;
                        step = run.Current;
                    }
                    catch (Exception e) { errors.Add(name + ": " + e.Message); break; }
                    yield return step;
                }
            }

            result["outputs"] = outputs;
            result["errors"] = errors;
            result["ok"] = errors.Count == 0;
            try { File.WriteAllText(Path.ChangeExtension(file, ".done.json"), result.ToString()); }
            catch (Exception e) { Logger.LogWarning("Could not write the request result: " + e.Message); }
            Logger.LogInfo($"Request {Path.GetFileName(file)}: {outputs.Count} result(s), {errors.Count} error(s)");
            Adapter.OnRequestDone(ran, errors.Count > 0 ? (string)errors[0] : null);
            _busy = false;
            _nextScan = 0f;   // (the next request waiting runs straight away)
        }

        // ---- reading arguments ----

        public static float F(string[] a, int i, float fallback) =>
            a.Length > i && float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        public static int I(string[] a, int i, int fallback) => a.Length > i && int.TryParse(a[i], out int v) ? v : fallback;

        public static string Rest(string[] a, int from) => a.Length > from ? string.Join(" ", a.Skip(from).ToArray()) : "";
    }
}
