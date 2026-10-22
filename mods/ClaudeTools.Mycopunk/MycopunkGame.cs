using Pigeon.Movement;
using Unity.Netcode;
using UnityEngine;

namespace ClaudeTools.Mycopunk
{
    /// <summary>
    /// What Claude Tools needs to know about Mycopunk (GameAdapter.cs there). Mycopunk reads only Unity's new Input System, which Claude
    /// Tools' Keys handles by itself; it is a Netcode for GameObjects co-op game, so the cheat rule is "this game hosts" (or plays alone).
    /// </summary>
    internal sealed partial class MycopunkGame : GameAdapter
    {
        public override string Game => "Mycopunk";

        // (PlayerLook is the local player's camera rig: there from spawning in a level, the hub included, until leaving it)
        public override bool InWorld => PlayerLook.Instance != null;

        // (its text chat reads keys itself, without a text box: PlayerLook says when it's open)
        public override bool Typing =>
            base.Typing || Menu.IsCreatedAndOpenAllowInMission() || (PlayerLook.Instance != null && PlayerLook.Instance.IsTextChatOpen);

        public override bool CheatsAllowed
        {
            get
            {
                NetworkManager net = NetworkManager.Singleton;
                return net == null || !net.IsListening || net.IsServer;
            }
        }

        public override string CheatRefusal(string command) =>
            $"{command} changes the game, so it only works for the host (or playing alone)";

        public override Transform Player => Pigeon.Movement.Player.LocalPlayer != null ? Pigeon.Movement.Player.LocalPlayer.transform : null;

        public override string WorldName
        {
            get
            {
                string location = null;
                try { location = LevelData.Instance != null ? LevelData.Instance.GetLocationName() : null; }
                catch (System.Exception) { }   // (between missions it reads a mission that isn't there)
                return !string.IsNullOrEmpty(location) ? location : UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
        }

        public override Camera Camera => PlayerLook.LocalCamera != null ? PlayerLook.LocalCamera : Camera.main;

        public override string GameVersion => Global.Version;

        public override string Thunderstore => "mycopunk";

        public override string BepInExPack => "BepInEx-BepInExPack_Mycopunk";
    }
}
