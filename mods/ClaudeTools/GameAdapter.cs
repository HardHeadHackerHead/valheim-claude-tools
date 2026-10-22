using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClaudeTools
{
    /// <summary>
    /// What Claude Tools needs to know about the game it runs in. Claude Tools itself uses no game's code: each game has a small mod of its own
    /// (ClaudeTools.Valheim, ClaudeTools.Mycopunk) that makes one of these, hands it over with Plugin.UseAdapter when it starts, and adds the
    /// game's own commands with Plugin.RegisterCommand. Without one, Claude Tools still runs: every answer here is a safe default for any Unity
    /// game (the main camera, nothing to cheat with, notices in the log only).
    /// </summary>
    public abstract class GameAdapter
    {
        /// <summary>The game's name, as players know it ("Valheim").</summary>
        public abstract string Game { get; }

        /// <summary>The player is in a world (most commands need that); false at the main menu and on loading screens.</summary>
        public virtual bool InWorld => true;

        /// <summary>A world is loaded or loading: what a mod that registers things the world saves must not be reloaded during.</summary>
        public virtual bool WorldLoaded => InWorld;

        /// <summary>The player is typing or has a menu open, so keys are not for Claude Tools (F12 and the like).</summary>
        public virtual bool Typing
        {
            get
            {
                GameObject sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (sel == null) return false;
                TMP_InputField tmp = sel.GetComponent<TMP_InputField>();
                InputField legacy = sel.GetComponent<InputField>();
                return (tmp != null && tmp.isFocused) || (legacy != null && legacy.isFocused);
            }
        }

        /// <summary>Whether the game allows cheats here (single player, the host): commands that change the world ask this.</summary>
        public virtual bool CheatsAllowed => false;

        /// <summary>What a cheat command answers where cheats aren't allowed.</summary>
        public virtual string CheatRefusal(string command) => $"{command} changes the game, so it only works where {Game} allows cheats";

        /// <summary>A message for the player (centre: in the middle of the screen, else a small one in a corner). By default only the log has it.</summary>
        public virtual void Notify(string text, bool centre) => Plugin.Log?.LogInfo(text);

        /// <summary>A reload or another thing Claude Tools did on its own, said where the player sees it (Valheim: the game's chat).</summary>
        public virtual void Announce(string text) => Notify(text, false);

        /// <summary>A command starts (from a request file, or the game's console): Valheim's Clawd says what Claude is doing.</summary>
        public virtual void OnCommand(string name, string owner, string[] a) { }

        /// <summary>A request is done: the commands it ran, and its first error if any.</summary>
        public virtual void OnRequestDone(IList<string> commands, string firstError) { }

        /// <summary>The player's F12 screenshot is being taken.</summary>
        public virtual void OnScreenshotKey() { }

        /// <summary>Hide (or show again) what the game's adapter adds to the world before a separate camera's picture; true if anything was hidden.</summary>
        public virtual bool HideForPictures(bool hide) => false;

        /// <summary>The player's character: where they stand and which way they face (null when there is none).</summary>
        public virtual Transform Player => null;

        /// <summary>The world, level or save the player is in, for pictures and surveys.</summary>
        public virtual string WorldName => "";

        /// <summary>The camera showing the player's view.</summary>
        public virtual Camera Camera => Camera.main;

        /// <summary>The point the player looks at (what the crosshair is on), up to 60 m away; not the player's own body.</summary>
        public virtual bool LookPoint(out Vector3 point)
        {
            point = Vector3.zero;
            Camera cam = Camera;
            if (cam == null) return false;
            Transform me = Player;
            foreach (RaycastHit hit in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 60f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (me != null && hit.collider.transform.IsChildOf(me)) continue;
                point = hit.point;
                return true;
            }
            return false;
        }

        /// <summary>A prefab by its name (for render and inspect). By default: a loaded object of that name that is not in a scene.</summary>
        public virtual GameObject FindPrefab(string name) =>
            Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g != null && g.name == name && g.transform.parent == null && !g.scene.IsValid());

        /// <summary>
        /// A copy of a prefab to photograph on its own (render): nothing of it may act in the game (no networking, saving, sounds or scripts).
        /// By default it is made under an inactive holder, its scripts removed, then shown.
        /// </summary>
        public virtual GameObject InstantiateStill(GameObject prefab, Vector3 at, Transform parent)
        {
            var holder = new GameObject("ClaudeStillHolder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject copy = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity, holder.transform);
            foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true).Reverse())
                if (script != null) UnityEngine.Object.DestroyImmediate(script);
            copy.transform.SetParent(parent, true);
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            return copy;
        }

        /// <summary>The game's own DLLs in its Managed folder, main one first: what `game` reads and `lag` times.</summary>
        public virtual string[] GameAssemblies => new[] { "Assembly-CSharp.dll" };

        /// <summary>The game's version (the library and the game snapshots keep it).</summary>
        public virtual string GameVersion => Application.version;

        /// <summary>The game's community on Thunderstore ("valheim": thunderstore.io/c/valheim); null: no mod library.</summary>
        public virtual string Thunderstore => null;

        /// <summary>The game's BepInEx package on Thunderstore (Namespace-Name, never a mod in the library's top list).</summary>
        public virtual string BepInExPack => null;

        /// <summary>More to say beside a screenshot (its .json): the game's own facts about the moment.</summary>
        public virtual void ShotInfo(JObject info) { }

        /// <summary>
        /// The game's systems: groups of methods that do one job ({ name, "Type.Method", "Type.*", ... }), so mods patching different methods of
        /// one job show as a possible clash. HighStakes are those where that can cost players items, DoesTheJob methods whose skipping prefixes
        /// do the job themselves, NotAJob systems whose overlaps are only input handling.
        /// </summary>
        public virtual string[][] Systems => new string[0][];
        public virtual IEnumerable<string> HighStakes => Enumerable.Empty<string>();
        public virtual IEnumerable<string> DoesTheJob => Enumerable.Empty<string>();
        public virtual IEnumerable<string> NotAJob => Enumerable.Empty<string>();

        // ---- the screen command (UiCommand.cs) ----

        /// <summary>
        /// A control of the game's own that screen lists: an item cell by the item in it, or a button of the game's own kind. Click: how to
        /// press it, when the event system's pointer events don't (null: they do).
        /// </summary>
        public class ScreenItem
        {
            public GameObject Go;
            public string Kind, Label;
            public bool On = true;
            public Action Click;
        }

        /// <summary>The game's own clickable things on a canvas that aren't plain buttons (an inventory's item cells, Mycopunk's buttons).</summary>
        public virtual IEnumerable<ScreenItem> ScreenItems(Canvas canvas) => Enumerable.Empty<ScreenItem>();

        /// <summary>A control screen should leave out (the game's item cells, listed once by ScreenItems).</summary>
        public virtual bool HideFromScreen(Selectable control) => false;

        /// <summary>
        /// "screen use", "screen pause", "screen key" and any other screen word the game knows: run it, or null when this game can't (they act as
        /// the player, through the game's own input).
        /// </summary>
        public virtual IEnumerator Screen(string sub, string[] a, string rest, Action<JObject> output, Action<string> error) => null;
    }

    /// <summary>No game's adapter: Claude Tools on its own.</summary>
    internal sealed class AnyGame : GameAdapter
    {
        public override string Game => Application.productName;
    }
}
