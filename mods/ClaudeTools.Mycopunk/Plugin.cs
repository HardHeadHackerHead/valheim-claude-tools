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

        private void Awake() => CoreLink.Install();   // first: a hot-loaded Claude Tools has a new name each time

        private void Update()
        {
            if (_game == null && CoreLink.Running(Plugin.Guid)) Attach();
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
