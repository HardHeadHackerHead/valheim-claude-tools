using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// Claude Tools: lets an AI assistant (Claude Code, or anything that can read and write files) see and help with your game through a
    /// "request mailbox", without any network port and without ever moving your character or pressing keys.
    ///
    ///   * The assistant writes a text file into BepInEx/claude/requests, one command per line. While you are in a world (and AllowRequests is
    ///     on), the game carries the commands out and writes the answer to a .done.json file beside it; pictures go to BepInEx/claude/shots.
    ///   * Built-in commands: pictures (your view, any camera spot, all round a place, straight down, a model on its own), the mods loaded, their
    ///     settings, the log and its errors, what makes the game stutter, the windows on screen, hot reload of the mods you are making, and the
    ///     mod-maker commands (the game's real code, the pre-release check, which mods patch what).
    ///   * This DLL uses no game's code. Each game has a small mod of its own beside it (ClaudeTools.Valheim, ClaudeTools.Mycopunk: a
    ///     GameAdapter, GameAdapter.cs) that says where the player is, what cheats are, and adds the game's own commands.
    ///   * Other mods add their own commands (BuildOrders adds blueprint commands) and named places to point cameras at; "help" lists them all.
    ///   * Keys: F12 saves a screenshot for the assistant.
    ///
    /// The guide for assistants is written to BepInEx/claude/CLAUDE.md (the game's adapter brings its own), so an assistant started in that
    /// folder knows all of this.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.quad.claudetools";
        public const string OldGuid = "com.dhack.claudetools"; // (its id until 2026-10: settings move over by themselves, see Shared/Migration.cs)
        public const string Name = "ClaudeTools";
        public const string Version = "1.3.0";

        public static Plugin Instance { get; private set; }
        public static BepInEx.Logging.ManualLogSource Log { get; private set; }

        private ConfigEntry<bool> _allowRequests, _allowConfigChanges;
        private ConfigEntry<KeyCode> _shotKey;

        public static string Folder => Path.Combine(Paths.BepInExRootPath, "claude");
        public static string RequestDir => Path.Combine(Folder, "requests");
        public static string ShotDir => Path.Combine(Folder, "shots");

        private void Awake()
        {
            DHack.Shared.Migration.FromOldGuid(this, OldGuid); // first: before any setting is read
            CoreLink.Install();   // (mods it hot-loads find each other under their new names)
            Instance = this;
            Log = Logger;
            _allowRequests = Config.Bind("Requests", "AllowRequests", false,
                "Carry out request files an AI assistant drops into BepInEx/claude/requests (pictures, surveys, what you carry and see, and commands other mods add, " +
                "such as placing blueprint ghosts). Only files on this computer can do this; nothing moves your character or presses keys. Turn it on while you work with an assistant.");
            _allowConfigChanges = Config.Bind("Requests", "AllowConfigChanges", false,
                "Also let requests change mods' settings (config set). Reading settings is always allowed.");
            _shotKey = Config.Bind("Keys", "ScreenshotKey", KeyCode.F12, "Save a screenshot (and where you stand and look) into BepInEx/claude/shots for the assistant.");
            RegisterBuiltIns();
            BindLibrary();
            BindDev();
            Logger.LogInfo($"{Name} {Version} loaded (requests {( _allowRequests.Value ? "on" : "off")}, folder {Folder})");
        }

        private void Start() => StartCoroutine(LoadScriptsAtStart());

        private void OnDestroy()
        {
            CoreLink.Uninstall();
            LagStop();
            _stopping = true;
            try { _requestWatcher?.Dispose(); } catch (Exception) { }
            _requestWatcher = null;
            ForgetCaches();
            if (Instance == this) Instance = null;
        }

        private float _nextScan;
        private bool _busy, _guideWritten;
        // A new request file is noticed at once: Windows tells this watcher (nothing runs while none come), and the folder is read a moment
        // later (the file has been written by then). The scan every second stays, in case a notice is missed.
        private FileSystemWatcher _requestWatcher;
        private volatile bool _requestArrived;
        private const float RequestSettle = 0.05f;

        private void Update()
        {
            // the guide: the game's adapter writes its own when it starts; on our own (none came within a few seconds), the general one
            if (!_guideWritten && Time.unscaledTime > 5f) { _guideWritten = true; WriteGuide(); }
            WatchScripts();

            if (Adapter.InWorld && !Adapter.Typing && Keys.Down(_shotKey.Value) && !Keys.Held(KeyCode.LeftControl) && !Keys.Held(KeyCode.RightControl))
            {
                Adapter.OnScreenshotKey();
                StartCoroutine(Screenshot(Path.Combine(ShotDir, Stamp() + ".png"), 0, p => Adapter.Notify("Screenshot saved for the assistant", false)));
            }

            if (_requestArrived) { _requestArrived = false; _nextScan = Mathf.Min(_nextScan, Time.unscaledTime + RequestSettle); }
            if (_allowRequests.Value && !_busy && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                if (_requestWatcher == null) WatchRequests();
                if (Directory.Exists(RequestDir))
                {
                    string next = Directory.GetFiles(RequestDir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (next != null) StartCoroutine(RunRequest(next));
                }
            }
        }

        private void WatchRequests()
        {
            try
            {
                Directory.CreateDirectory(RequestDir);
                _requestWatcher = new FileSystemWatcher(RequestDir, "*.txt") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                _requestWatcher.Created += (s, e) => _requestArrived = true;   // (another thread: Update reads the flag)
                _requestWatcher.Changed += (s, e) => _requestArrived = true;
                _requestWatcher.Renamed += (s, e) => _requestArrived = true;
                _requestWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Requests are read once a second (can't watch the folder: " + e.Message + ")");
                _requestWatcher = new FileSystemWatcher();   // (a placeholder: don't try again every second)
            }
        }

        public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        public static JArray Vec(Vector3 v) => new JArray(Math.Round(v.x, 2), Math.Round(v.y, 2), Math.Round(v.z, 2));

        /// <summary>The point you are looking at (as the game's adapter sees it: terrain, buildings, rocks), up to 60 m away.</summary>
        public static bool LookPoint(out Vector3 point) => Adapter.LookPoint(out point);

        /// <summary>
        /// Write CLAUDE.md (the assistants' guide) and the skills into BepInEx/claude, refreshed whenever they change: this DLL's, and the game
        /// adapter's (its guide, skills, templates and tools take the place of ours where they have the same name).
        /// </summary>
        internal void WriteGuide()
        {
            try
            {
                Directory.CreateDirectory(RequestDir);
                Directory.CreateDirectory(ShotDir);
                // the guide, the skills for mod makers (Claude Code finds .claude/skills in the folder it starts in), modkit (the same
                // commands on the command line, for when the game isn't running) and modelkit (making and checking 3D models offline)
                var files = new Dictionary<string, KeyValuePair<Assembly, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (Assembly asm in new[] { typeof(Plugin).Assembly, _adapter?.GetType().Assembly }.Where(x => x != null).Distinct())
                    foreach (string name in asm.GetManifestResourceNames())
                    {
                        string rel = name == "guide/CLAUDE.md" ? "CLAUDE.md"
                                   : name.StartsWith("skills/") ? ".claude/" + name.Replace('\\', '/')
                                   : name.StartsWith("modkit/") || name.StartsWith("modelkit/") || name.StartsWith("templates/") ? name.Replace('\\', '/') : null;
                        if (rel != null) files[rel] = new KeyValuePair<Assembly, string>(asm, name);
                    }
                foreach (var file in files)
                {
                    string rel = file.Key;
                    try
                    {
                        using (Stream s = file.Value.Key.GetManifestResourceStream(file.Value.Value))
                        {
                            if (s == null) continue;
                            var data = new MemoryStream();
                            s.CopyTo(data);
                            byte[] bytes = data.ToArray();
                            string path = Path.Combine(Folder, rel.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
                        }
                    }
                    catch (Exception e) { Logger.LogWarning($"Could not write {rel}: {e.Message}"); } // (modkit.exe may be running)
                }
            }
            catch (Exception e) { Logger.LogWarning("Could not write the guide: " + e.Message); }
        }
    }
}
