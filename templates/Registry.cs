// Registry.cs (Claude Tools template): keep your own list of things instead of searching the scene, and do a big job a slice per
// frame instead of all in one. Copy into your mod and change the namespace.
//
// Why: Object.FindObjectsByType / FindObjectsOfType walk every loaded object of that kind, about 10 ms a call in a big base, and
// ZDOMan.GetAllZDOsWithPrefab walks every saved object in the world. Done on a timer, it is a hitch every few seconds that players feel;
// several mods on the same timer land in the same frame. Real cases (lag, October 2026): a scene search every 2 s cost 20-25 ms,
// a ship search over the world once a second cost 40 ms, a 566-piece plan re-read from its file every 5 s cost 130-300 ms, and a
// colony scan that filtered every piece for every question it asked cost 45-65 ms every 8 s. Measure yours with Claude Tools: lag 20.
//
//   Live<T>   your own list of a component: the objects add themselves when they wake and leave when destroyed.
//   Sliced    a big job (a scan, a search over the world) run a few milliseconds per frame, resting between whole passes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace YourMod
{
    /// <summary>
    /// Every live T, without searching the scene. Your own components add themselves:
    ///
    ///   private void Awake() => Live&lt;Beacon&gt;.Add(this);
    ///   private void OnDestroy() => Live&lt;Beacon&gt;.Remove(this);
    ///
    /// For the game's components, which you can't edit, a postfix on their Awake adds them (and a check for Unity's null when reading
    /// takes away destroyed ones, so no OnDestroy patch is needed):
    ///
    ///   [HarmonyPatch(typeof(Container), "Awake")] static class SeeChests { static void Postfix(Container __instance) => Live&lt;Container&gt;.Add(__instance); }
    ///
    /// After a hot reload, or when your mod loads in a world already running, the ones already there never woke for you: call Seed() once
    /// (one scene search at load is fine; one every few seconds is not). Clear() in your plugin's OnDestroy.
    /// </summary>
    internal static class Live<T> where T : Component
    {
        private static readonly List<T> Items = new List<T>();
        private static readonly HashSet<T> Known = new HashSet<T>();

        public static void Add(T item) { if (item != null && Known.Add(item)) Items.Add(item); }

        public static void Remove(T item) { if (item != null && Known.Remove(item)) Items.Remove(item); }

        /// <summary>Every live one (destroyed ones are dropped as they are met). Don't add or remove while going through it.</summary>
        public static List<T> All
        {
            get
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                    if (Items[i] == null) { Known.Remove(Items[i]); Items.RemoveAt(i); }   // (Unity's null: destroyed)
                return Items;
            }
        }

        /// <summary>The live ones within a radius of a spot (into a list you keep, so nothing new is made each call).</summary>
        public static void Near(Vector3 at, float radius, List<T> into)
        {
            into.Clear();
            float r2 = radius * radius;
            foreach (T item in All)
                if ((item.transform.position - at).sqrMagnitude <= r2) into.Add(item);
        }

        /// <summary>Add the ones already in the scene: once, when the mod loads (or reloads) in a world.</summary>
        public static void Seed()
        {
            foreach (T item in UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None)) Add(item);
        }

        public static void Clear() { Items.Clear(); Known.Clear(); }
    }

    /// <summary>
    /// A big job done a slice per frame. Write it as a coroutine that yields between pieces of work; call Tick() from your Update. Each
    /// frame it runs for at most budgetMs, and when a whole pass is done it rests restSeconds before the next one:
    ///
    ///   private readonly Sliced _shipScan;
    ///   _shipScan = new Sliced(FindShips, budgetMs: 1f, restSeconds: 2f);     // in Awake
    ///   _shipScan.Tick();                                                     // in Update
    ///
    ///   private IEnumerator FindShips()
    ///   {
    ///       var found = new List&lt;ZDO&gt;();
    ///       int index = 0;
    ///       // the game's iterative search looks at up to 400 sectors a call: one call per step
    ///       while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative("Karve", found, ref index)) yield return null;
    ///       _ships = found;   // use the result only once the whole pass is done
    ///   }
    ///
    /// Two things a sliced job must do:
    /// - Read what it needs once per pass and keep it for the pass: the pieces of the area, the chests and what is in them. Asking the
    ///   whole scene again for every question (each item, each piece) is what made a colony scan cost 45-65 ms.
    /// - Check again anything it kept from earlier frames before using it. The game reuses a removed thing's ZDO for a new object, so a ZDO
    ///   found a few frames ago may now be something else: test zdo.IsValid() and zdo.GetPrefab() (and Unity objects against null).
    /// </summary>
    internal sealed class Sliced
    {
        private readonly Func<IEnumerator> _job;
        private readonly float _budgetMs, _restSeconds;
        private IEnumerator _running;
        private float _nextStart;

        public Sliced(Func<IEnumerator> job, float budgetMs = 1f, float restSeconds = 5f)
        {
            _job = job;
            _budgetMs = Mathf.Max(0.1f, budgetMs);
            _restSeconds = Mathf.Max(0f, restSeconds);
        }

        /// <summary>A pass is under way.</summary>
        public bool Running => _running != null;

        /// <summary>Start a new pass on the next Tick (after something changed that it should see now).</summary>
        public void Now() => _nextStart = 0f;

        /// <summary>Stop the pass under way (leaving a world, unloading).</summary>
        public void Stop() { _running = null; _nextStart = 0f; }

        public void Tick()
        {
            if (_running == null)
            {
                if (Time.unscaledTime < _nextStart) return;
                _running = _job();
            }
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed.TotalMilliseconds < _budgetMs)
                {
                    if (_running.MoveNext()) continue;
                    _running = null;   // the pass is done: rest
                    _nextStart = Time.unscaledTime + _restSeconds;
                    return;
                }
            }
            catch (Exception e)
            {
                _running = null;
                _nextStart = Time.unscaledTime + _restSeconds;
                UnityEngine.Debug.LogWarning($"[{typeof(Sliced).Namespace}] a sliced job failed: {e.Message}");
            }
        }
    }
}
