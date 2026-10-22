using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Mycopunk
{
    /// <summary>
    /// Claude Tools for Mycopunk: what Claude Tools (ClaudeTools.dll, which uses no game's code) needs to know about Mycopunk (MycopunkGame),
    /// and Mycopunk's own commands (status, so far). Everything else is Claude Tools' own: pictures, the windows on screen, the log and its
    /// errors, lag, hot reload, the game's code and the mod library (Thunderstore's mycopunk community).
    ///
    /// It starts once Claude Tools has (whatever order the loader started them in). If Claude Tools reloads itself, its dev loader reloads
    /// this mod too.
    /// </summary>
    [BepInPlugin(Guid, Name, Plugin.Version)]
    [BepInDependency(Plugin.Guid)]
    public class MycopunkPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.quad.claudetools.mycopunk";
        public const string Name = "ClaudeTools.Mycopunk";

        private object _game;   // (a MycopunkGame: typed object, so this class loads before Claude Tools does)
        private bool _gaveUp;

        private void Awake() => CoreLink.Install();   // first: a hot-loaded Claude Tools has a new name each time

        private void Update()
        {
            if (_game == null) TryAttach();
        }

        /// <summary>
        /// Claude Tools is running: hand it the game. A ClaudeTools.dll too old for this mod (the one before the split, with Valheim inside)
        /// fails here: said once in the log, not every frame.
        /// </summary>
        private void TryAttach()
        {
            if (_gaveUp || !CoreLink.Running(Plugin.Guid)) return;
            try { Attach(); }
            catch (Exception e)
            {
                _gaveUp = true;
                Logger.LogError($"Claude Tools is running, but not a copy this mod can work with ({e.GetBaseException().Message}): " +
                                "install the ClaudeTools.dll that came with this one, in the same folder");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Attach()
        {
            var game = new MycopunkGame();
            _game = game;
            Plugin.UseAdapter(game, Name);
            RegisterCommand(Name, "status", "status: the level, where the player is, health, whether this game hosts, who is in it", (a, output, error) =>
            {
                Pigeon.Movement.Player p = Pigeon.Movement.Player.LocalPlayer;
                if (p == null) { error("no player yet (still loading?)"); return null; }
                Camera cam = game.Camera;
                output(new JObject
                {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["level"] = game.WorldName, ["hub"] = LevelData.IsHub,
                    ["player"] = p.Name,
                    ["position"] = Vec(p.transform.position), ["yaw"] = Math.Round(p.transform.eulerAngles.y, 1),
                    ["camera"] = cam != null ? Vec(cam.transform.position) : null,
                    ["health"] = Math.Round(p.Health, 1), ["maxHealth"] = Math.Round(p.MaxHealth, 1),
                    ["host"] = game.CheatsAllowed,
                    ["online"] = new JArray(GameManager.players.Where(x => x != null).Select(x => new JObject
                        { ["name"] = x.Name, ["distance"] = Math.Round(Vector3.Distance(x.transform.position, p.transform.position), 1) })),
                });
                return null;
            });
            // the windows on screen, by the name Mycopunk players use: Claude Tools' screen command (Mycopunk's buttons and keys: Screen.cs)
            RegisterCommand(Name, "ui", "ui [list [text] | texts [text] | click <words or name> [n] | key <key> [frames] | type <text> | submit | slider <n> <value>]: " +
                "the same as screen: every button on screen (the game's own too) with its text; click one by its text or its object's name (the main " +
                "menu's Play: ui click play); press a key through the game's input (ui key F8). Clicking, typing and keys only for the host or alone", (a, output, error) =>
                RunCommand(new[] { "screen" }.Concat(a.Length > 1 ? a.Skip(1) : new[] { "list" }).ToArray(), output, error));
            // pictures, the windows and the log work at the main menu too (getting into the hub starts there)
            RunsAtMenu("ui", "shot");
            Logger.LogInfo($"{Name} {Plugin.Version} loaded");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Detach()
        {
            UnregisterAll(Name);
            DropAdapter((GameAdapter)_game, Name);
        }

        private void OnDestroy()
        {
            if (_game != null) Detach();
            _game = null;
            CoreLink.Uninstall();
        }
    }
}
