using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// What Claude Tools needs to know about Valheim (GameAdapter.cs there): the player and their camera, typing and menus, the cheat rule,
    /// messages on screen, prefabs, and Valheim's game code, systems and Thunderstore community.
    /// </summary>
    internal sealed partial class ValheimGame : GameAdapter
    {
        public override string Game => "Valheim";

        public override bool InWorld => global::Player.m_localPlayer != null;

        // (a world loading counts: a mod that registers pieces must not reload then either)
        public override bool WorldLoaded => ZNetScene.instance != null;

        public override bool Typing =>
            (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() || global::Menu.IsVisible();

        public override bool CheatsAllowed =>
            ZNet.instance == null || ZNet.instance.IsServer() || (global::Console.instance != null && global::Console.instance.IsCheatsEnabled());

        public override string CheatRefusal(string command) =>
            $"{command} changes the world, so it only works in single player, for the host, or with devcommands on (a server admin)";

        public override void Notify(string text, bool centre) =>
            global::Player.m_localPlayer?.Message(centre ? MessageHud.MessageType.Center : MessageHud.MessageType.TopLeft, text);

        /// <summary>Said in the game's chat (on this game only); nothing at the main menu, where there is no chat.</summary>
        public override void Announce(string text)
        {
            if (Chat.instance == null) return;
            Chat.instance.AddString("[Claude Tools]", text, Talker.Type.Normal);
            Traverse.Create(Chat.instance).Field("m_hideTimer").SetValue(0f);   // (shown now, and fades as usual)
            Chat.instance.m_chatWindow.gameObject.SetActive(true);
        }

        // Clawd (Clawd.cs) says what Claude is doing, and keeps out of Claude's pictures
        public override void OnCommand(string name, string owner, string[] a) => ValheimPlugin.ClawdOnCommand(name, owner, a);

        public override void OnRequestDone(IList<string> commands, string firstError) =>
            ValheimPlugin.ClawdOnDone(commands.Count(c => c != "clawd"), firstError);

        public override void OnScreenshotKey() => ValheimPlugin.ClawdScreenshot();

        public override bool HideForPictures(bool hide) => ValheimPlugin.Buddy != null && ValheimPlugin.Buddy.Hide(hide);

        public override Transform Player => global::Player.m_localPlayer != null ? global::Player.m_localPlayer.transform : null;

        public override string WorldName => ZNet.instance != null ? ZNet.instance.GetWorldName() : "";

        public override Camera Camera => GameCamera.instance != null ? GameCamera.instance.GetComponent<Camera>() : Camera.main;

        /// <summary>The point you are looking at (terrain, buildings, rocks), up to 60 m away.</summary>
        public override bool LookPoint(out Vector3 point)
        {
            point = Vector3.zero;
            if (GameCamera.instance == null) return false;
            Transform cam = GameCamera.instance.transform;
            if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 60f, LayerMask.GetMask("terrain", "Default", "static_solid", "Default_small", "piece"), QueryTriggerInteraction.Ignore)) return false;
            point = hit.point;
            return true;
        }

        public override GameObject FindPrefab(string name)
        {
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (p == null && ObjectDB.instance != null) p = ObjectDB.instance.GetItemPrefab(name);
            if (p == null && ObjectDB.instance != null)   // pieces only on a tool's build list (terrain tools and the like)
                foreach (GameObject item in ObjectDB.instance.m_items)
                {
                    PieceTable table = item != null ? item.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
                    GameObject found = table?.m_pieces.FirstOrDefault(x => x != null && x.name == name);
                    if (found != null) return found;
                }
            return p;
        }

        public override GameObject InstantiateStill(GameObject prefab, Vector3 at, Transform parent)
        {
            GameObject copy;
            ZNetView.m_forceDisableInit = true;   // a copy that is not a networked object (nothing is saved or shared)
            try { copy = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity, parent); }
            finally { ZNetView.m_forceDisableInit = false; }
            foreach (CircleProjector ring in copy.GetComponentsInChildren<CircleProjector>(true)) ring.gameObject.SetActive(false);
            return copy;
        }

        public override string[] GameAssemblies => new[] { "assembly_valheim.dll", "assembly_utils.dll", "assembly_guiutils.dll" };

        public override string GameVersion => global::Version.GetVersionString();

        public override string Thunderstore => "valheim";

        public override string BepInExPack => "denikson-BepInExPack_Valheim";

        public override string[][] Systems => ValheimSystems.Systems;
        public override IEnumerable<string> HighStakes => ValheimSystems.HighStakes;
        public override IEnumerable<string> DoesTheJob => ValheimSystems.DoesTheJob;
        public override IEnumerable<string> NotAJob => ValheimSystems.NotAJob;
    }
}
