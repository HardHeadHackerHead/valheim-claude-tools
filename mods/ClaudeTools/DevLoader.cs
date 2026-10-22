using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// The dev loader: mods being made load from BepInEx/scripts and reload one at a time, without restarting the game, and only when that's
    /// safe. Our own code, in place of ScriptEngine (which reloads every mod at once, ignores dependencies and incompatibilities, and reloads
    /// mods that register pieces in the middle of a world).
    ///
    ///   * At start, every mod in BepInEx/scripts is loaded, in dependency order, unless ScriptEngine is installed (then it loads them, and
    ///     `reload` still reloads one mod at a time on ScriptEngine's object, so its F6 keeps working).
    ///   * `reload <mod>`: unload that mod and load its DLL again. modcheck's reload level decides when: "world" mods reload anywhere, "menu" mods
    ///     (pieces, items, creatures, inventory size) only at the main menu, "restart" mods (Jotunn, blaxxun-boop's managers) never.
    ///   * AutoReload: a mod rebuilt into scripts reloads by itself, when its level allows it now.
    ///
    /// How a load works (as ScriptEngine does it): the DLL is read with Mono.Cecil, its assembly renamed (a new name each time, so .NET loads
    /// the new code next to the old), loaded from memory, and each BepInPlugin in it added as a component; its PluginInfo goes into
    /// Chainloader.PluginInfos, so other mods find it as usual. The old copy's code stays in memory until the game quits: that's why a mod
    /// must clean up in OnDestroy.
    /// </summary>
    public partial class Plugin
    {
        private const string ScriptEngineGuid = "com.bepis.bepinex.scriptengine";

        private ConfigEntry<bool> _loadScripts, _autoReload;
        private static GameObject _devHost;                        // the mods this loader started, one component each
        private readonly Dictionary<string, DateTime> _built = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); // dll -> build time loaded
        private readonly Dictionary<string, DateTime> _settling = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private float _nextWatch;
        private bool _reloading;

        internal static string ScriptsDir => Path.Combine(Paths.BepInExRootPath, "scripts");

        private void BindDev()
        {
            _loadScripts = Config.Bind("Dev", "LoadScripts", true,
                "Load the mods in BepInEx/scripts when the game starts (mods you are making), so they can be reloaded one at a time with " +
                "`claude reload <mod>`. Ignored when ScriptEngine is installed: it loads them instead.");
            _autoReload = Config.Bind("Dev", "AutoReload", false,
                "Reload a mod in BepInEx/scripts as soon as it is rebuilt, when that is safe now (mods that add pieces or items: only at the main menu).");
            RegisterDevCommands();
        }

        private static bool ScriptEngineInstalled(out BaseUnityPlugin engine)
        {
            engine = Chainloader.PluginInfos.TryGetValue(ScriptEngineGuid, out PluginInfo info) ? info.Instance : null;
            return engine != null;
        }

        /// <summary>The object our script mods live on: ScriptEngine's when it's installed (its F6 then reloads them too), else our own.</summary>
        private static GameObject Host()
        {
            if (ScriptEngineInstalled(out BaseUnityPlugin engine))
            {
                var manager = AccessTools.Field(engine.GetType(), "scriptManager")?.GetValue(engine) as GameObject;
                if (manager != null) return manager;
            }
            if (_devHost == null)
            {
                _devHost = new GameObject("ClaudeTools_DevMods");
                DontDestroyOnLoad(_devHost);
                _devHost.hideFlags = HideFlags.HideAndDontSave;
            }
            return _devHost;
        }

        /// <summary>At start (every plugin's Awake has run): load BepInEx/scripts, unless ScriptEngine does.</summary>
        private IEnumerator LoadScriptsAtStart()
        {
            yield return null;
            if (!Directory.Exists(ScriptsDir)) yield break;
            foreach (string dll in Directory.GetFiles(ScriptsDir, "*.dll")) _built[dll] = BuildTime(dll);
            if (!_loadScripts.Value || ScriptEngineInstalled(out _)) yield break;
            if (Path.GetDirectoryName(Info.Location) is string mine && Path.GetFullPath(mine).TrimEnd('\\', '/').Equals(Path.GetFullPath(ScriptsDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                yield break; // (this copy itself came from scripts: something else loads them)

            var plugins = new List<Prepared>();
            foreach (string dll in Directory.GetFiles(ScriptsDir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { plugins.AddRange(Prepare(dll, out _)); }
                catch (Exception e) { Logger.LogError($"Dev loader: could not load {Path.GetFileName(dll)}: {e.Message}"); }
            }
            int started = 0;
            foreach (Prepared p in InDependencyOrder(plugins))
                if (StartPlugin(p, Host(), out string why)) started++;
                else Logger.LogWarning($"Dev loader: {p.Guid} not loaded: {why}");
            Logger.LogInfo($"Dev loader: loaded {started} mod(s) from BepInEx/scripts");
        }

        // ---- loading ----

        private class Prepared
        {
            public string Guid, Name, Version, Dll;
            public Type Type;
            public PluginInfo Info;
            public List<BepInDependency> Needs;
            public List<BepInIncompatibility> Refuses;
        }

        /// <summary>Read a DLL and load its code under a fresh name. Nothing starts yet: the plugins come back to be started.</summary>
        private static List<Prepared> Prepare(string dll, out bool withSymbols)
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (string d in new[] { ScriptsDir, Paths.ManagedPath, Paths.BepInExAssemblyDirectory, Paths.PluginPath }.Where(Directory.Exists))
                resolver.AddSearchDirectory(d);
            string pdb = Path.ChangeExtension(dll, ".pdb");
            withSymbols = false;
            var found = new List<Prepared>();
            using (AssemblyDefinition def = Read(dll, resolver, File.Exists(pdb)))
            {
                bool symbols = def.MainModule.HasSymbols;
                def.Name.Name = $"{def.Name.Name}-{DateTime.Now.Ticks}";
                // a mod that uses another hot-loaded mod's code (a game's adapter and Claude Tools) gets that mod's live copy, not the first
                // one .NET loaded under the plain name
                foreach (AssemblyNameReference used in def.MainModule.AssemblyReferences)
                    if (LiveName(used.Name) is string live) used.Name = live;
                Assembly asm = null;
                if (symbols)
                {
                    // with its symbols, stack traces name the file and line (if this Mono takes them; otherwise without)
                    try
                    {
                        using (var raw = new MemoryStream())
                        using (var sym = new MemoryStream())
                        {
                            def.Write(raw, new WriterParameters { WriteSymbols = true, SymbolStream = sym, SymbolWriterProvider = new Mono.Cecil.Cil.PortablePdbWriterProvider() });
                            asm = Assembly.Load(raw.ToArray(), sym.ToArray());
                            withSymbols = true;
                        }
                    }
                    catch (Exception) { asm = null; }
                }
                if (asm == null)
                    using (var raw = new MemoryStream())
                    {
                        def.Write(raw);
                        asm = Assembly.Load(raw.ToArray());
                    }

                foreach (Type type in TypesOf(asm))
                {
                    if (!typeof(BaseUnityPlugin).IsAssignableFrom(type) || type.IsAbstract) continue;
                    BepInPlugin meta = MetadataHelper.GetMetadata(type);
                    if (meta == null) continue;
                    TypeDefinition td = def.MainModule.GetTypes().FirstOrDefault(t => t.FullName == type.FullName.Replace('+', '/'));
                    if (td == null) continue;
                    found.Add(new Prepared
                    {
                        Guid = meta.GUID, Name = meta.Name, Version = meta.Version?.ToString(), Dll = dll, Type = type, Info = Chainloader.ToPluginInfo(td),
                        Needs = MetadataHelper.GetDependencies(type).ToList(),
                        Refuses = MetadataHelper.GetAttributes<BepInIncompatibility>(type).ToList(),
                    });
                }
            }
            return found;
        }

        private static AssemblyDefinition Read(string dll, IAssemblyResolver resolver, bool symbols)
        {
            if (symbols)
                try { return AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(dll)), new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = true, SymbolStream = new MemoryStream(File.ReadAllBytes(Path.ChangeExtension(dll, ".pdb"))) }); }
                catch (Exception) { } // a pdb that doesn't match the DLL (or an old format): load without it
            return AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(dll)), new ReaderParameters { AssemblyResolver = resolver });
        }

        /// <summary>
        /// The name a running mod's code was loaded under, when a hot loader renamed it ("ClaudeTools-638...") and something references it
        /// by its plain name; null when it runs under its own name or isn't running.
        /// </summary>
        private static string LiveName(string plain)
        {
            foreach (PluginInfo info in Chainloader.PluginInfos.Values)
            {
                if (info.Instance == null) continue;
                string name = info.Instance.GetType().Assembly.GetName().Name;
                if (name != plain && BaseName(name) == plain) return name;
            }
            return null;
        }

        /// <summary>A hot-loaded copy's name without what the loader added ("ClaudeTools-638..." is ClaudeTools).</summary>
        internal static string BaseName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"-\d{8,}$", "");

        /// <summary>The mods running whose code uses Claude Tools' own (a game's adapter): they go when it reloads, and come back with it.</summary>
        private static List<PluginInfo> UsersOfClaudeTools()
        {
            string mine = BaseName(typeof(Plugin).Assembly.GetName().Name);
            return Chainloader.PluginInfos.Values.Where(i => i.Instance != null && i.Metadata.GUID != Guid
                && i.Instance.GetType().Assembly.GetReferencedAssemblies().Any(r => BaseName(r.Name) == mine)).ToList();
        }

        private static IEnumerable<Type> TypesOf(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                foreach (Exception le in e.LoaderExceptions.Take(5)) Log?.LogError($"Dev loader: {asm.GetName().Name}: {le.Message}");
                return e.Types.Where(t => t != null);
            }
        }

        /// <summary>
        /// Start one prepared plugin, as BepInEx does: refuse it next to a mod it declares incompatible or without one it needs (ScriptEngine
        /// ignores both), then add it to Chainloader.PluginInfos and as a component.
        /// </summary>
        private static bool StartPlugin(Prepared p, GameObject host, out string why)
        {
            why = null;
            if (Chainloader.PluginInfos.TryGetValue(p.Guid, out PluginInfo there))
            { why = $"{p.Guid} is already loaded ({Path.GetFileName(there.Location)})"; return false; }
            BepInIncompatibility clash = p.Refuses.FirstOrDefault(r => Chainloader.PluginInfos.ContainsKey(r.IncompatibilityGUID));
            if (clash != null) { why = $"it says it is incompatible with {clash.IncompatibilityGUID}, which is loaded"; return false; }
            foreach (BepInDependency d in p.Needs.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
            {
                if (!Chainloader.PluginInfos.TryGetValue(d.DependencyGUID, out PluginInfo dep)) { why = $"it needs {d.DependencyGUID}, which isn't loaded"; return false; }
                if (d.MinimumVersion != null && dep.Metadata.Version < d.MinimumVersion) { why = $"it needs {d.DependencyGUID} {d.MinimumVersion} or later ({dep.Metadata.Version} is loaded)"; return false; }
            }
            try
            {
                Chainloader.PluginInfos[p.Guid] = p.Info;
                Component c = host.AddComponent(p.Type);
                var t = Traverse.Create(p.Info);
                t.Property<BaseUnityPlugin>("Instance").Value = (BaseUnityPlugin)c;
                t.Property<string>("Location").Value = p.Dll;
                return true;
            }
            catch (Exception e)
            {
                Chainloader.PluginInfos.Remove(p.Guid);
                why = "it failed to start: " + (e.InnerException ?? e).Message;
                return false;
            }
        }

        /// <summary>Plugins whose hard dependencies come first (those in plugins/ are already loaded).</summary>
        private static List<Prepared> InDependencyOrder(List<Prepared> all)
        {
            var byGuid = all.GroupBy(p => p.Guid).ToDictionary(g => g.Key, g => g.First());
            var done = new HashSet<string>();
            var order = new List<Prepared>();
            void Visit(Prepared p, int depth)
            {
                if (done.Contains(p.Guid) || depth > 50) return;
                done.Add(p.Guid);
                foreach (BepInDependency d in p.Needs) // soft dependencies too: load after them when they're here
                    if (byGuid.TryGetValue(d.DependencyGUID, out Prepared dep)) Visit(dep, depth + 1);
                order.Add(p);
            }
            foreach (Prepared p in all) Visit(p, 0);
            return order;
        }

        // ---- reloading ----

        private static DateTime BuildTime(string dll)
        {
            DateTime t = File.GetLastWriteTimeUtc(dll);
            string pdb = Path.ChangeExtension(dll, ".pdb");
            if (File.Exists(pdb)) { DateTime p = File.GetLastWriteTimeUtc(pdb); if (p > t) t = p; }
            return t;
        }

        /// <summary>The reload level modcheck gives the DLL (world, menu, restart), and why.</summary>
        internal static JObject ReloadLevel(string dll)
        {
            DateTime built = BuildTime(dll);
            if (LevelCache.TryGetValue(dll, out var c) && c.Key == built) return c.Value;
            JObject check = Check.Run(dll, SearchDirs().Concat(new[] { ScriptsDir }).ToArray(), new Check.Others());
            JObject level = check["reload"] as JObject ?? new JObject { ["level"] = "world" };
            LevelCache[dll] = new KeyValuePair<DateTime, JObject>(built, level);
            return level;
        }

        private static readonly Dictionary<string, KeyValuePair<DateTime, JObject>> LevelCache = new Dictionary<string, KeyValuePair<DateTime, JObject>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a mod of this level may be reloaded now, and if not, why.</summary>
        private static string NotNow(string level)
        {
            bool inWorld = Adapter.WorldLoaded;
            if (level == "restart") return "it can only be loaded by restarting the game";
            if (level == "menu" && inWorld) return "it registers things the world saves (or changes inventory sizes): reload it at the main menu, or restart";
            return null;
        }

        /// <summary>The DLL in scripts holding this mod (by name, GUID or file name), and the loaded copy if any.</summary>
        private static string FindScript(string mod, out PluginInfo loaded)
        {
            loaded = Chainloader.PluginInfos.Values.FirstOrDefault(i => i.Metadata.GUID.Equals(mod, StringComparison.OrdinalIgnoreCase)
                                                                       || i.Metadata.Name.Equals(mod, StringComparison.OrdinalIgnoreCase)
                                                                       || Path.GetFileNameWithoutExtension(i.Location ?? "").Equals(mod, StringComparison.OrdinalIgnoreCase));
            if (loaded != null && File.Exists(loaded.Location) && InScripts(loaded.Location)) return loaded.Location;
            if (loaded != null && !InScripts(loaded.Location ?? "")) return null; // a mod from plugins/: BepInEx loaded it, only a restart reloads it
            string file = Path.Combine(ScriptsDir, mod.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? mod : mod + ".dll");
            return File.Exists(file) ? file : null;
        }

        private static bool InScripts(string path) =>
            path.Length > 0 && Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\', '/').Equals(Path.GetFullPath(ScriptsDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Unload one mod (if loaded) and load its DLL again, a frame later (Destroy happens at the end of the frame).</summary>
        private IEnumerator Reload(string dll, PluginInfo loaded, bool force, Action<JObject> output, Action<string> error)
        {
            if (_reloading) { error("a reload is already running"); yield break; }
            _reloading = true;
            try
            {
                if (loaded != null && loaded.Metadata.GUID == Guid)
                {
                    // Claude Tools itself: a helper from this copy hands over once this request's answer is written (see Handover). The
                    // mods using its code (the game's adapter) go and come back with it, so they must be reloadable too
                    GameObject from = Host();
                    List<PluginInfo> users = UsersOfClaudeTools();
                    PluginInfo stuck = users.FirstOrDefault(u => (u.Instance.gameObject != from && u.Instance.gameObject != _devHost) || !File.Exists(u.Location) || !InScripts(u.Location));
                    if (stuck != null)
                    {
                        error($"{stuck.Metadata.Name} uses Claude Tools' code and was loaded from {Path.GetDirectoryName(stuck.Location ?? "")}, so it can't come back with " +
                              "a new Claude Tools: restart the game to load the new build (or keep both in BepInEx/scripts)");
                        yield break;
                    }
                    output(new JObject
                    {
                        ["reloading"] = $"{Name} {Version}", ["dll"] = dll, ["with"] = new JArray(users.Select(u => u.Metadata.Name)),
                        ["note"] = "the new copy starts in a moment: send help or devmods next (other mods add their commands back within 5 seconds)",
                    });
                    var helper = new GameObject("ClaudeTools_Handover");
                    DontDestroyOnLoad(helper);
                    helper.AddComponent<Handover>().Begin(dll, loaded, from, GetType(), users);
                    yield break;
                }
                JObject level = ReloadLevel(dll);
                string notNow = NotNow((string)level["level"]);
                if (notNow != null && !force)
                {
                    error($"not reloading {Path.GetFileNameWithoutExtension(dll)}: {notNow} (reload level {level["level"]}: {string.Join("; ", level["why"]?.Select(x => (string)x) ?? new string[0])})");
                    yield break;
                }
                GameObject host = Host();
                if (loaded != null)
                {
                    if (loaded.Instance != null && loaded.Instance.gameObject != host && loaded.Instance.gameObject != _devHost)
                    { error($"{loaded.Metadata.Name} wasn't loaded from scripts by a reloader: restart the game to load the new build"); yield break; }
                    Chainloader.PluginInfos.Remove(loaded.Metadata.GUID);
                    if (loaded.Instance != null) Destroy(loaded.Instance);
                }
                yield return null; // the old copy's OnDestroy runs now

                List<Prepared> plugins;
                bool symbols;
                try { plugins = Prepare(dll, out symbols); }
                catch (Exception e) { error($"could not load {Path.GetFileName(dll)}: {e.Message}"); Announce($"Couldn't reload {Path.GetFileNameWithoutExtension(dll)}: {e.Message}"); yield break; }
                var started = new JArray();
                foreach (Prepared p in InDependencyOrder(plugins))
                    if (StartPlugin(p, host, out string why)) started.Add($"{p.Name} {p.Version}");
                    else { error($"{p.Guid}: {why}"); Announce($"Couldn't start {p.Name} again: {why}"); }
                _built[dll] = BuildTime(dll);
                if (started.Count > 0) Announce("Reloaded " + string.Join(", ", started.Select(x => (string)x)) + (notNow != null ? " (forced: " + notNow + ")" : ""));
                Logger.LogInfo($"Dev loader: reloaded {Path.GetFileName(dll)} ({string.Join(", ", started.Select(x => (string)x))})");
                output(new JObject
                {
                    ["reloaded"] = started, ["dll"] = dll, ["level"] = level["level"], ["symbols"] = symbols,
                    ["note"] = notNow != null ? "forced: " + notNow : "run errors next",
                });
            }
            finally { _reloading = false; }
        }

        /// <summary>
        /// Reloading Claude Tools itself: it can't unload the code that carries out the request, so a helper from the old copy (its code stays
        /// loaded) waits until the request's answer is written, takes the old copy down (its patches, commands and watchers go in OnDestroy),
        /// and a frame later starts the new build on the same object. The mods already loaded stay as they are (BepInEx's list keeps them, and
        /// they add their commands to the new copy themselves), except those using its code (the game's adapter): bound to the old copy, they
        /// go first and are loaded again after the new one, bound to it. If the new build doesn't start, the old copies are put back.
        /// </summary>
        private sealed class Handover : MonoBehaviour
        {
            private string _dll;
            private PluginInfo _old;
            private GameObject _host;
            private Type _oldType;
            private List<KeyValuePair<PluginInfo, Type>> _users;
            private GameAdapter _game;   // (the game's adapter as it was: the new one isn't there yet when the reload is said)
            private int _frames;

            public void Begin(string dll, PluginInfo old, GameObject host, Type oldType, List<PluginInfo> users)
            {
                _dll = dll; _old = old; _host = host; _oldType = oldType; _game = Adapter;
                _users = users.Select(u => new KeyValuePair<PluginInfo, Type>(u, u.Instance.GetType())).ToList();
            }

            private void Update()
            {
                if (++_frames < 3) return;   // (the request's answer is written first)
                enabled = false;
                StartCoroutine(Run());
            }

            private void Say(string text)
            {
                try { _game.Announce(text); }
                catch (Exception e) { Debug.LogWarning("[Claude Tools] couldn't say it in the game: " + e.Message); }
            }

            private void PutBack(PluginInfo info, Type type)
            {
                try
                {
                    Chainloader.PluginInfos[info.Metadata.GUID] = info;
                    Component back = _host.AddComponent(type);
                    Traverse.Create(info).Property<BaseUnityPlugin>("Instance").Value = (BaseUnityPlugin)back;
                }
                catch (Exception e) { Debug.LogError($"[Claude Tools] reload: the old {info.Metadata.Name} couldn't come back either (restart the game): " + e.Message); }
            }

            private IEnumerator Run()
            {
                string guid = _old.Metadata.GUID;
                foreach (var user in _users)
                {
                    Chainloader.PluginInfos.Remove(user.Key.Metadata.GUID);
                    if (user.Key.Instance != null) Destroy(user.Key.Instance);
                }
                Chainloader.PluginInfos.Remove(guid);
                if (_old.Instance != null) Destroy(_old.Instance);
                yield return null;   // the old copies' OnDestroy runs now
                bool started = false;
                string version = "";
                try
                {
                    foreach (Prepared p in InDependencyOrder(Prepare(_dll, out _)))
                        if (StartPlugin(p, _host, out string why)) { started = true; version = p.Version.ToString(); }
                        else Debug.LogWarning($"[Claude Tools] reload: {p.Guid} not started: {why}");
                }
                catch (Exception e) { Debug.LogError("[Claude Tools] reload: could not load the new build: " + e.Message); }
                if (!started)
                {
                    PutBack(_old, _oldType);
                    foreach (var user in _users) PutBack(user.Key, user.Value);
                    Debug.LogWarning("[Claude Tools] reload: the new build didn't start, so the old copy is back");
                    Say("Claude Tools' new build didn't start, so the old copy is back (see the log)");
                }
                else
                {
                    Debug.Log("[Claude Tools] reloaded itself");
                    Say($"Reloaded Claude Tools {version}");
                    // the mods that use its code, loaded again (each DLL once), now bound to the new copy (Prepare points them at it)
                    foreach (string dll in _users.Select(u => u.Key.Location).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (Prepared p in InDependencyOrder(Prepare(dll, out _)))
                                if (!StartPlugin(p, _host, out string why)) Debug.LogWarning($"[Claude Tools] reload: {p.Guid} not started again: {why}");
                        }
                        catch (Exception e) { Debug.LogError($"[Claude Tools] reload: could not load {Path.GetFileName(dll)} again (restart the game): {e.Message}"); }
                    }
                }
                Destroy(gameObject);
            }
        }

        /// <summary>AutoReload: a DLL in scripts rebuilt (and settled for a second and a half) reloads when its level allows it now.</summary>
        private void WatchScripts()
        {
            if (_autoReload == null || !_autoReload.Value || _reloading || Time.unscaledTime < _nextWatch || !Directory.Exists(ScriptsDir)) return;
            _nextWatch = Time.unscaledTime + 1f;
            foreach (string dll in Directory.GetFiles(ScriptsDir, "*.dll"))
            {
                DateTime now = BuildTime(dll);
                if (_built.TryGetValue(dll, out DateTime had) && had == now) { _settling.Remove(dll); continue; }
                if (!_settling.TryGetValue(dll, out DateTime seen) || seen != now) { _settling[dll] = now; _settleSince[dll] = Time.unscaledTime; continue; }
                if (Time.unscaledTime - _settleSince[dll] < 1.5f) continue; // still being copied
                _settling.Remove(dll);
                _built[dll] = now;
                string mod = Path.GetFileNameWithoutExtension(dll);
                FindScript(mod, out PluginInfo loaded);
                if (loaded?.Metadata.GUID == Guid) continue;
                string notNow;
                try { notNow = NotNow((string)ReloadLevel(dll)["level"]); }
                catch (Exception e) { Logger.LogWarning($"Dev loader: {mod} rebuilt, but it can't be read: {e.Message}"); continue; }
                if (notNow != null)
                {
                    Logger.LogWarning($"Dev loader: {mod} rebuilt, not reloaded: {notNow}");
                    Announce($"{mod} rebuilt: reload it at the main menu or restart ({notNow})");
                    continue;
                }
                StartCoroutine(Reload(dll, loaded, false, o => { }, e => Logger.LogWarning($"Dev loader: {e}")));   // (Reload says it in chat)
                return; // one at a time
            }
        }

        /// <summary>
        /// A reload, said where the player sees it (Valheim's adapter: the game's chat, on this game only), so the player knows when Claude or
        /// a rebuild reloaded a mod. Always in the log as well, where the callers write it.
        /// </summary>
        private static void Announce(string text)
        {
            try { Adapter.Announce(text); }
            catch (Exception e) { Debug.LogWarning("[Claude Tools] couldn't say it in the game: " + e.Message); }
        }

        private readonly Dictionary<string, float> _settleSince = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private void RegisterDevCommands()
        {
            Builtin("reload", "reload <mod> [force]: unload one mod from BepInEx/scripts and load its new build, when that's safe now (its reload level: " +
                "world = anywhere, menu = only at the main menu, restart = never); force ignores the level", (a, output, error) =>
            {
                string mod = a.Length > 1 ? a[1] : "";
                bool force = a.Length > 2 && a[2].Equals("force", StringComparison.OrdinalIgnoreCase);
                if (mod.Length == 0) { error("say which mod: reload YourMod (its name, GUID or DLL name)"); return null; }
                string dll = FindScript(mod, out PluginInfo loaded);
                if (dll == null)
                {
                    error(loaded != null ? $"{loaded.Metadata.Name} is in {Path.GetDirectoryName(loaded.Location)}: BepInEx loads that folder only at start, so restart the game"
                                         : $"no {mod} in BepInEx/scripts");
                    return null;
                }
                return Instance.Reload(dll, loaded, force, output, error);
            });
            Builtin("devmods", "devmods: the mods in BepInEx/scripts: loaded or not, their reload level (world, menu, restart) and why, and whether a newer build is waiting", (a, output, error) =>
            {
                if (!Directory.Exists(ScriptsDir)) { output(new JObject { ["scripts"] = "no BepInEx/scripts folder" }); return null; }
                bool engine = ScriptEngineInstalled(out _);
                var list = new JArray();
                foreach (string dll in Directory.GetFiles(ScriptsDir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string mod = Path.GetFileNameWithoutExtension(dll);
                    FindScript(mod, out PluginInfo loaded);
                    JObject level;
                    try { level = ReloadLevel(dll); } catch (Exception e) { level = new JObject { ["level"] = "unreadable", ["why"] = new JArray(e.Message) }; }
                    list.Add(new JObject
                    {
                        ["mod"] = mod, ["loaded"] = loaded != null ? $"{loaded.Metadata.Name} {loaded.Metadata.Version}" : null,
                        ["newerBuild"] = Instance._built.TryGetValue(dll, out DateTime had) && BuildTime(dll) > had,
                        ["level"] = level["level"], ["why"] = level["why"], ["reloadNow"] = NotNow((string)level["level"]) ?? "yes",
                    });
                }
                output(new JObject
                {
                    ["loader"] = engine ? "ScriptEngine loads scripts at start (F6 reloads all of them); reload <mod> reloads one" : "Claude Tools' dev loader",
                    ["inWorld"] = Adapter.WorldLoaded, ["mods"] = list,
                });
                return null;
            });
        }
    }
}
