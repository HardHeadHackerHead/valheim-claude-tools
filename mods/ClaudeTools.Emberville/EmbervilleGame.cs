using System.Runtime.CompilerServices;
using BepInEx;

namespace ClaudeTools.Emberville
{
    /// <summary>
    /// Claude Tools for Emberville: so far only the game's name, with Claude Tools' defaults for any Unity game (the main camera, the look
    /// point under the screen's centre, no cheats, notices in the log). To make it fit the game, override from GameAdapter (in Claude Tools):
    /// InWorld (the player's character exists), Player (its transform), Camera, Typing (its chat and menus), CheatsAllowed (playing alone or
    /// hosting), Notify (a message on screen), FindPrefab, Thunderstore (its community, once there is one) and BepInExPack; and add the
    /// game's commands with Plugin.RegisterCommand in Attach, as ClaudeTools.Mycopunk does.
    /// </summary>
    internal sealed class EmbervilleGame : GameAdapter
    {
        public override string Game => "Emberville";
    }

    [BepInPlugin(Guid, Name, Plugin.Version)]
    [BepInDependency(Plugin.Guid)]
    public class EmbervillePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.quad.claudetools.emberville";
        public const string Name = "ClaudeTools.Emberville";

        private object _game;   // (an EmbervilleGame: typed object, so this class loads before Claude Tools does)

        private void Awake() => CoreLink.Install();   // first: a hot-loaded Claude Tools has a new name each time

        private void Update()
        {
            if (_game == null && CoreLink.Running(Plugin.Guid)) Attach();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Attach()
        {
            var game = new EmbervilleGame();
            _game = game;
            Plugin.UseAdapter(game, Name);
            Logger.LogInfo($"{Name} {Plugin.Version} loaded");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Detach()
        {
            Plugin.UnregisterAll(Name);
            Plugin.DropAdapter((GameAdapter)_game, Name);
        }

        private void OnDestroy()
        {
            if (_game != null) Detach();
            _game = null;
            CoreLink.Uninstall();
        }
    }
}
