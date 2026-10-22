using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// Claude Tools for Valheim: what Claude Tools (ClaudeTools.dll, which uses no game's code) needs to know about Valheim, and Valheim's own
    /// commands: status, inventory, nearby, chests, pictures set up with shoot, surveys, building checks, the game's console, getting in and
    /// out of a world, testing commands, the ground's change records, server admin (Admin.cs) and Clawd, a little Claude who follows the
    /// player and says what Claude is doing (Clawd.cs). It carries the Valheim guide, skills, templates, modkit and modelkit,
    /// which Claude Tools writes to BepInEx/claude, and the patch map of the top Thunderstore mods.
    ///
    /// It starts once Claude Tools has (whatever order the loader started them in), and hands over a ValheimGame. If Claude Tools reloads
    /// itself, its dev loader reloads this mod too.
    /// </summary>
    [BepInPlugin(Guid, Name, Plugin.Version)]
    [BepInDependency(Plugin.Guid)]
    public partial class ValheimPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.quad.claudetools.valheim";
        public const string Name = "ClaudeTools.Valheim";

        internal static ValheimPlugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        private object _game;   // (a ValheimGame: typed object, so this class loads before Claude Tools does)
        private HarmonyLib.Harmony _harmony;

        private void Awake()
        {
            CoreLink.Install();   // first: a hot-loaded Claude Tools has a new name each time
            Instance = this;
            Log = Logger;
        }

        private void Update()
        {
            if (_game == null)
            {
                if (CoreLink.Running(Plugin.Guid)) Attach();
                return;
            }
            SurveyKey();
            ClawdUpdate();
        }

        /// <summary>Claude Tools is there: the game for it, and Valheim's commands.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Attach()
        {
            var game = new ValheimGame();
            _game = game;
            Plugin.UseAdapter(game, Name);
            // its only patches: "screen key" (FakeKeys, in Screen.cs) presses keys through the game's input, and Clawd hearing chat
            // (ClawdHearsChat, in Clawd.cs); undone in OnDestroy
            _harmony = new HarmonyLib.Harmony(Guid);
            _harmony.PatchAll(typeof(ValheimPlugin).Assembly);
            BindSurvey(Plugin.Instance.Config);
            BindAdmin(Plugin.Instance.Config);
            BindClawd(Plugin.Instance.Config);
            Plugin.RunsAtMenu("language", "play", "quit");
            Plugin.CheatOnly("give", "grow", "fixlevels", "chestrule", "clear", "terrain");
            RegisterBuiltIns();
            RegisterConsole();
            Logger.LogInfo($"{Name} {Plugin.Version} loaded");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Detach()
        {
            _harmony?.UnpatchSelf();
            ClawdGone();
            UnregisterConsole();
            Plugin.UnregisterAll(Name);
            Plugin.DropAdapter((GameAdapter)_game, Name);
        }

        private void OnDestroy()
        {
            if (_game != null) Detach();
            _game = null;
            CoreLink.Uninstall();
            if (Instance == this) Instance = null;
        }

        private static void Builtin(string name, string usage, Func<string[], Action<Newtonsoft.Json.Linq.JObject>, Action<string>, System.Collections.IEnumerator> run) =>
            Plugin.RegisterCommand(Name, name, usage, run);
    }
}
