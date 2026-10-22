using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// "lag": what makes the game stutter. For a few seconds it times every mod's Update/LateUpdate/FixedUpdate/OnGUI, their coroutines
    /// and every Harmony patch method they have (and, with game=on, the game's own Update methods), then reports the slow frames: how long,
    /// what ran in them, whether memory cleanup (GC) happened, how many log lines were written; and how often they come. It also lists mods
    /// loaded more than once by hot reload whose old copy still has objects running. Everything it adds is taken off again at the end.
    /// </summary>
    public partial class Plugin
    {
        private const string LagUsage = "lag [seconds=20] [spike=50] [game=on|off] [top=20]: time every mod's Update methods, coroutines and " +
            "patches (and the game's own Updates) for that long, then report the slow frames (over spike ms): what ran in them, memory " +
            "cleanups, log lines, how often they come; plus mods whose old hot-reload copy still runs. Patching takes a few seconds first. " +
            "mod=<Name>[,Name] also times every method of those mods (to see which part of an Update is slow: times nest, so an Update includes " +
            "what it calls; patching a big mod takes minutes and the timers make busy methods look slower than they are); methods=Type.Method[,...] " +
            "times those game methods too (ZNetScene.CreateObjects)";

        private void RegisterLagCommand() => Builtin("lag", LagUsage, (a, output, error) => LagCommand(a, output, error));

        private static Harmony _lagHarmony;

        /// <summary>Called on unload: a reload in the middle of a lag run must not leave the timers on.</summary>
        private static void LagStop()
        {
            LagProbe.On = false;
            try { _lagHarmony?.UnpatchSelf(); } catch (Exception) { /* nothing more to do */ }
            _lagHarmony = null;
        }

        private IEnumerator LagCommand(string[] a, Action<JObject> output, Action<string> error)
        {
            if (_lagHarmony != null) { error("a lag run is going already"); yield break; }
            var o = Options(a, 1);
            float seconds = Mathf.Clamp(a.Length > 1 && !a[1].Contains("=") ? F(a, 1, 20f) : Opt(o, "seconds", 20f), 3f, 300f);
            float spikeMs = Mathf.Clamp(Opt(o, "spike", 50f), 10f, 2000f);
            bool game = !(o.TryGetValue("game", out string g) && g.Equals("off", StringComparison.OrdinalIgnoreCase));
            int top = Mathf.Clamp((int)Opt(o, "top", 20f), 5, 100);

            // ---- what to time ----
            Assembly own = typeof(Plugin).Assembly;
            Dictionary<Assembly, string> mods = ModAssemblies(own);
            var targets = new List<KeyValuePair<MethodBase, string[]>>();   // method -> { mod, label }
            var seen = new HashSet<MethodBase>();
            void Add(MethodBase m, string mod, string label)
            {
                if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType == null || m.DeclaringType.ContainsGenericParameters) return;
                if (m.Name.Contains("Lag") || m.DeclaringType.FullName.Contains("Lag")) return;   // (not this command itself)
                try { if (m.GetMethodBody() == null) return; } catch (Exception) { return; }
                if (seen.Add(m)) targets.Add(new KeyValuePair<MethodBase, string[]>(m, new[] { mod, label }));
            }
            string[] loops = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
            void AddTypes(Assembly asm, string mod)
            {
                foreach (Type t in SafeTypes(asm))
                {
                    if (t.ContainsGenericParameters) continue;
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    if (typeof(MonoBehaviour).IsAssignableFrom(t))
                        foreach (string n in loops) Add(t.GetMethod(n, all, null, Type.EmptyTypes, null), mod, $"{t.Name}.{n}");
                    else if (t.Name.StartsWith("<") && typeof(IEnumerator).IsAssignableFrom(t))   // a coroutine (iterator) step
                        Add(t.GetMethod("MoveNext", all, null, Type.EmptyTypes, null), mod, $"{t.DeclaringType?.Name}.{IteratorName(t.Name)} (coroutine)");
                }
            }
            foreach (var m in mods) AddTypes(m.Key, m.Value);
            foreach (MethodBase patched in Harmony.GetAllPatchedMethods().ToList())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(patched);
                if (info == null) continue;
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    if (p.PatchMethod?.DeclaringType != null && mods.TryGetValue(p.PatchMethod.DeclaringType.Assembly, out string mod))
                        Add(p.PatchMethod, mod, $"{p.PatchMethod.DeclaringType.Name}.{p.PatchMethod.Name} (patch on {patched.DeclaringType?.Name}.{patched.Name})");
            }
            Assembly gameCode = GameAssembly();
            if (game && gameCode != null) AddTypes(gameCode, "game");
            // a closer look: every method of the mods named (nested: an Update's time includes what it calls), and game methods by name
            var deep = new HashSet<string>((o.TryGetValue("mod", out string dm) ? dm : "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods.Where(x => deep.Contains(x.Value)))
                foreach (Type t in SafeTypes(m.Key))
                {
                    if (t.ContainsGenericParameters) continue;
                    foreach (MethodInfo mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!mi.Name.StartsWith("<") || t.Name.StartsWith("<")) Add(mi, m.Value, $"{(t.Name.StartsWith("<") ? (t.DeclaringType?.Name + "." + IteratorName(t.Name)) : t.Name)}.{mi.Name}");
                }
            if (o.TryGetValue("methods", out string named))
                foreach (string n in named.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int dot = n.LastIndexOf('.');
                    Type t = dot > 0 ? AccessTools.TypeByName(n.Substring(0, dot)) : null;
                    if (t == null) { error($"methods: no type {n}"); continue; }
                    var found = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(x => x.Name == n.Substring(dot + 1)).ToList();
                    if (found.Count == 0) error($"methods: no method {n}");
                    foreach (MethodInfo mi in found) Add(mi, "game", $"{t.Name}.{mi.Name}");
                }

            // ---- put the timers on, a few each frame (patching is slow: all at once would freeze the game) ----
            _lagHarmony = new Harmony(Guid + ".lag");
            LagProbe.Reset();
            var pre = new HarmonyMethod(typeof(LagProbe).GetMethod(nameof(LagProbe.Pre))) { priority = Priority.First };
            var post = new HarmonyMethod(typeof(LagProbe).GetMethod(nameof(LagProbe.Post))) { priority = Priority.Last };
            int failed = 0;
            var watch = Stopwatch.StartNew();
            float patchStart = Time.realtimeSinceStartup;
            var on = new List<MethodBase>();
            foreach (var t in targets)
            {
                if (_lagHarmony == null) { error("stopped (Claude Tools unloaded)"); yield break; }
                try { _lagHarmony.Patch(t.Key, prefix: pre, postfix: post); LagProbe.Name(t.Key, t.Value[0], t.Value[1]); on.Add(t.Key); }
                catch (Exception) { failed++; }
                if (watch.ElapsedMilliseconds > 8) { yield return null; watch.Restart(); }
            }
            float patchSeconds = Time.realtimeSinceStartup - patchStart;

            // ---- watch ----
            var logCounts = new Dictionary<string, int>();
            int logLines = 0;
            void OnLog(string text, string stack, LogType type)
            {
                Interlocked.Increment(ref logLines);
                string key = text == null ? "" : (text.Length > 140 ? text.Substring(0, 140) : text);
                int nl = key.IndexOf('\n'); if (nl >= 0) key = key.Substring(0, nl);
                lock (logCounts) logCounts[key] = logCounts.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            yield return new WaitForSecondsRealtime(1f);   // (let the frames settle after patching)
            Application.logMessageReceivedThreaded += OnLog;
            LagProbe.Start();
            var frameMs = new List<float>();
            var spikes = new List<JObject>();
            var spikeTimes = new List<float>();
            var pending = new List<KeyValuePair<int, JObject>>();   // finished two frames later, when their timings are in
            var inSpikes = new Dictionary<LagProbe.Stat, float[]>();   // stat -> { spikes it was in, ms in them }
            int gcStart = GC.CollectionCount(0), gcLast = gcStart, gcInSpikes = 0, logLast = 0;
            float startAt = Time.realtimeSinceStartup;
            yield return null;
            while (Time.realtimeSinceStartup - startAt < seconds && _lagHarmony != null)
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                int frame = Time.frameCount;
                int gc = GC.CollectionCount(0), logs = logLines;
                frameMs.Add(ms);
                if (ms >= spikeMs)
                {
                    float at = Time.realtimeSinceStartup - startAt;
                    spikeTimes.Add(at);
                    if (gc > gcLast) gcInSpikes++;
                    var s = new JObject
                    {
                        ["at"] = Math.Round(at, 2), ["ms"] = Math.Round(ms, 1), ["gc"] = gc - gcLast, ["logLines"] = logs - logLast,
                        ["heapMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0),
                    };
                    pending.Add(new KeyValuePair<int, JObject>(frame, s));
                    spikes.Add(s);
                }
                gcLast = gc; logLast = logs;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (frame - pending[i].Key < 2) continue;
                    FillSpike(pending[i].Value, pending[i].Key, inSpikes);
                    pending.RemoveAt(i);
                }
                yield return null;
            }
            LagProbe.On = false;
            Application.logMessageReceivedThreaded -= OnLog;
            foreach (var p in pending) FillSpike(p.Value, p.Key, inSpikes);
            float ran = Time.realtimeSinceStartup - startAt;
            int gcAll = GC.CollectionCount(0) - gcStart;

            // ---- take the timers off again, a few each frame ----
            watch.Restart();
            foreach (MethodBase m in on)
            {
                if (_lagHarmony == null) break;
                try { _lagHarmony.Unpatch(m, HarmonyPatchType.All, _lagHarmony.Id); } catch (Exception) { /* goes with UnpatchSelf below */ }
                if (watch.ElapsedMilliseconds > 8) { yield return null; watch.Restart(); }
            }
            LagStop();

            // ---- the report ----
            List<float> sorted = frameMs.OrderBy(x => x).ToList();
            float Pct(float p) => sorted.Count == 0 ? 0f : sorted[Mathf.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
            var gaps = new List<float>();
            for (int i = 1; i < spikeTimes.Count; i++) gaps.Add(spikeTimes[i] - spikeTimes[i - 1]);
            gaps.Sort();
            List<LagProbe.Stat> stats = LagProbe.All();
            JObject Row(LagProbe.Stat s) => new JObject
            {
                ["method"] = s.Label, ["mod"] = s.Mod, ["calls"] = s.Calls, ["msPerSecond"] = Math.Round(s.Ms / ran, 2),
                ["worstFrameMs"] = Math.Round(s.MaxFrameMs, 2),
            };
            var byMod = stats.GroupBy(s => s.Mod).Select(x => new { mod = x.Key, ms = x.Sum(s => s.Ms) })
                .OrderByDescending(x => x.ms).Take(top).Select(x => new JObject { ["mod"] = x.mod, ["msPerSecond"] = Math.Round(x.ms / ran, 2) });
            KeyValuePair<string, int>[] topLogs;
            lock (logCounts) topLogs = logCounts.OrderByDescending(x => x.Value).Take(8).ToArray();

            var result = new JObject
            {
                ["seconds"] = Math.Round(ran, 1), ["frames"] = frameMs.Count, ["fps"] = Math.Round(frameMs.Count / ran, 1),
                ["frameMs"] = new JObject { ["median"] = Math.Round(Pct(0.5f), 1), ["p95"] = Math.Round(Pct(0.95f), 1), ["p99"] = Math.Round(Pct(0.99f), 1), ["max"] = Math.Round(Pct(1f), 1) },
                ["spikeMs"] = spikeMs, ["spikes"] = spikes.Count,
                ["spikeEverySeconds"] = gaps.Count > 0 ? Math.Round(gaps[gaps.Count / 2], 2) : (double?)null,
                ["gc"] = new JObject { ["collections"] = gcAll, ["inSpikes"] = gcInSpikes, ["heapMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0) },
                ["timed"] = new JObject { ["methods"] = on.Count, ["couldNotTime"] = failed, ["patchSeconds"] = Math.Round(patchSeconds, 1), ["game"] = game },
                ["inSpikes"] = new JArray(inSpikes.OrderByDescending(x => x.Value[0]).ThenByDescending(x => x.Value[1]).Take(top)
                    .Select(x => new JObject { ["method"] = x.Key.Label, ["mod"] = x.Key.Mod, ["spikes"] = (int)x.Value[0], ["avgMs"] = Math.Round(x.Value[1] / x.Value[0], 2) })),
                ["slowestSpikes"] = new JArray(spikes.OrderByDescending(s => (double)s["ms"]).Take(10)),
                ["byMod"] = new JArray(byMod),
                ["worstFrame"] = new JArray(stats.OrderByDescending(s => s.MaxFrameMs).Take(top).Select(Row)),
                ["mostTime"] = new JArray(stats.OrderByDescending(s => s.Ms).Take(top).Select(Row)),
                ["logLinesPerSecond"] = Math.Round(logLines / ran, 1),
                ["commonLogLines"] = new JArray(topLogs.Select(x => new JObject { ["count"] = x.Value, ["line"] = x.Key })),
                ["oldCopiesRunning"] = OldCopies(own),
                ["note"] = "frame ms are measured at the start of the next frame; a spike's methods are from that frame and the one before. Times nest: " +
                    "a game Update includes the mod patches on what it calls. If a spike's timed methods add up to far less than its ms, the time went " +
                    "elsewhere: memory cleanup (gc > 0), log writing (logLines), loading, physics, rendering, or the network.",
            };
            output(result);
        }

        /// <summary>A spike's frames (it and the one before): the methods that took the most, and the time per mod.</summary>
        private static void FillSpike(JObject s, int frame, Dictionary<LagProbe.Stat, float[]> inSpikes)
        {
            var times = new Dictionary<LagProbe.Stat, float>();
            foreach (int f in new[] { frame - 1, frame })
                if (LagProbe.Frames.TryGetValue(f, out var list))
                    foreach (var x in list) times[x.Key] = (times.TryGetValue(x.Key, out float had) ? had : 0f) + x.Value;
            s["timedMs"] = Math.Round(times.Where(x => x.Key.Mod != "game").Sum(x => x.Value), 1);
            s["gameMs"] = Math.Round(times.Where(x => x.Key.Mod == "game").Sum(x => x.Value), 1);
            s["mods"] = new JObject(times.GroupBy(x => x.Key.Mod).Select(x => new { x.Key, ms = x.Sum(y => y.Value) }).Where(x => x.ms >= 0.5f)
                .OrderByDescending(x => x.ms).Take(6).Select(x => new JProperty(x.Key, Math.Round(x.ms, 1))));
            s["top"] = new JArray(times.OrderByDescending(x => x.Value).Take(8).Where(x => x.Value >= 0.2f)
                .Select(x => new JObject { ["method"] = x.Key.Label, ["mod"] = x.Key.Mod, ["ms"] = Math.Round(x.Value, 2) }));
            foreach (var x in times.Where(x => x.Value >= 1f))
            {
                if (!inSpikes.TryGetValue(x.Key, out float[] c)) inSpikes[x.Key] = c = new float[2];
                c[0]++; c[1] += x.Value;
            }
        }

        /// <summary>The mods' assemblies (from BepInEx/scripts and plugins, old hot-reload copies too), each with its name ("Name (old copy)").</summary>
        private static Dictionary<Assembly, string> ModAssemblies(Assembly own)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in new[] { ScriptsDir, Paths.PluginPath })
                if (Directory.Exists(dir))
                    foreach (string dll in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)) names.Add(Path.GetFileNameWithoutExtension(dll));
            var current = new HashSet<Assembly>(Chainloader.PluginInfos.Values.Where(p => p.Instance != null).Select(p => p.Instance.GetType().Assembly));
            var found = new Dictionary<Assembly, string>();
            // (hot-loaded copies are renamed "Name-<ticks>" so each reload can load: match on the name before that)
            string BaseName(Assembly x) => System.Text.RegularExpressions.Regex.Replace(x.GetName().Name, @"-\d{8,}$", "");
            foreach (var group in AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic && x != own && names.Contains(BaseName(x)))
                         .GroupBy(BaseName, StringComparer.OrdinalIgnoreCase))
            {
                List<Assembly> copies = group.OrderBy(x => x.GetName().Name, StringComparer.Ordinal).ToList();
                Assembly live = copies.FirstOrDefault(current.Contains) ?? copies.Last();
                foreach (Assembly asm in copies) found[asm] = asm == live ? group.Key : group.Key + " (old copy)";
            }
            return found;
        }

        /// <summary>Mods loaded more than once (hot reload) whose old copies still have objects in the game: the old code may still run.</summary>
        private static JArray OldCopies(Assembly own)
        {
            Dictionary<Assembly, string> mods = ModAssemblies(own);
            var alive = new Dictionary<string, int>();
            foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (b != null && mods.TryGetValue(b.GetType().Assembly, out string name) && name.EndsWith(" (old copy)"))
                    alive[name] = (alive.TryGetValue(name, out int n) ? n : 0) + 1;
            return new JArray(mods.Values.Where(v => v.EndsWith(" (old copy)")).GroupBy(v => v)
                .Select(x => new JObject { ["mod"] = x.Key.Replace(" (old copy)", ""), ["oldCopies"] = x.Count(), ["oldObjectsAlive"] = alive.TryGetValue(x.Key, out int n) ? n : 0 })
                .Where(x => (int)x["oldObjectsAlive"] > 0 || (int)x["oldCopies"] > 3));
        }

        /// <summary>The game's main DLL, loaded (the first of its adapter's GameAssemblies: assembly_valheim, Assembly-CSharp).</summary>
        private static Assembly GameAssembly()
        {
            string main = Path.GetFileNameWithoutExtension(Adapter.GameAssemblies.FirstOrDefault() ?? "");
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => !x.IsDynamic && x.GetName().Name == main);
        }

        public static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch (Exception) { return Enumerable.Empty<Type>(); }
        }

        private static string IteratorName(string generated)   // "<Warmup>d__12" -> "Warmup"
        {
            int end = generated.IndexOf('>');
            return end > 1 ? generated.Substring(1, end - 1) : generated;
        }
    }

    /// <summary>The timers the lag command puts on each method: time per call, added up per frame (main thread only).</summary>
    public static class LagProbe
    {
        public sealed class Stat
        {
            public string Mod, Label;
            public long Calls;
            public double Ms, MaxFrameMs;
            internal long FrameTicks;
            internal int Frame = -1;
        }

        internal static volatile bool On;
        private static int _mainThread;
        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static readonly List<Stat> Touched = new List<Stat>();
        private static int _frame = -1;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        /// <summary>The last few finished frames: what ran in each and for how long (ms).</summary>
        internal static readonly Dictionary<int, List<KeyValuePair<Stat, float>>> Frames = new Dictionary<int, List<KeyValuePair<Stat, float>>>();

        internal static void Reset() { On = false; Stats.Clear(); Touched.Clear(); Frames.Clear(); _frame = -1; }

        internal static void Name(MethodBase m, string mod, string label) => Stats[m] = new Stat { Mod = mod, Label = label };

        internal static void Start() { _mainThread = Thread.CurrentThread.ManagedThreadId; _frame = -1; On = true; }

        internal static List<Stat> All() => Stats.Values.Where(s => s.Calls > 0).ToList();

        public static void Pre(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        public static void Post(long __state, MethodBase __originalMethod)
        {
            if (__state == 0L || !On || Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (__originalMethod == null || !Stats.TryGetValue(__originalMethod, out Stat s)) return;
            int frame = Time.frameCount;
            if (frame != _frame) Roll(frame);
            if (s.Frame != frame) { s.Frame = frame; s.FrameTicks = 0; Touched.Add(s); }
            s.FrameTicks += ticks;
            s.Calls++;
            s.Ms += ticks * MsPerTick;
        }

        private static void Roll(int frame)
        {
            if (_frame >= 0)
            {
                var list = new List<KeyValuePair<Stat, float>>(Touched.Count);
                foreach (Stat s in Touched)
                {
                    float ms = (float)(s.FrameTicks * MsPerTick);
                    if (ms > s.MaxFrameMs) s.MaxFrameMs = ms;
                    list.Add(new KeyValuePair<Stat, float>(s, ms));
                }
                Frames[_frame] = list;
                Frames.Remove(_frame - 8);
            }
            Touched.Clear();
            _frame = frame;
        }
    }
}
