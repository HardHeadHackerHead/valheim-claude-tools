using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ClaudeTools
{
    /// <summary>
    /// Shaping a test site: "clear" takes things away around a spot (trees, rocks, plants, built pieces, items, creatures, or all of it), and
    /// "terrain" levels the ground there, or puts it back as the world made it. The ground is changed the way the game's own levelling does it
    /// (the hoe's, and Arena's and BuildOrders' ground): per point of land, saved with the world and sent to everyone, never more than the
    /// game allows ground to move (8 m from where the world made it). Only what is loaded (about 60 m around the player) can be changed.
    /// Both only where cheats are allowed.
    /// </summary>
    public partial class Plugin
    {
        private const float GroundLimit = 8f;   // the most the game lets ground move from where the world made it

        private static readonly AccessTools.FieldRef<TerrainComp, bool> TcInit = AccessTools.FieldRefAccess<TerrainComp, bool>("m_initialized");
        private static readonly AccessTools.FieldRef<TerrainComp, int> TcWidth = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> TcModH = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> TcLevel = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> TcSmooth = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> TcModP = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> TcView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly System.Reflection.MethodInfo TcSave = AccessTools.Method(typeof(TerrainComp), "Save");
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> SceneInstances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        private static readonly string[] ClearKinds = { "trees", "rocks", "plants", "pieces", "items", "creatures", "other" };

        private void RegisterTerrain()
        {
            RegisterFields();
            Builtin("clear", "clear <radius> [trees] [rocks] [plants] [pieces] [items] [creatures] [other] [all] [at <x> <z>]: take away what is within that many metres " +
                "of the player (or the spot): trees (and stumps and logs), rocks (and ore), plants (bushes, berries, flowers), built pieces, items on the ground, creatures " +
                "(not players), other (chests in the ground, runestones, locations...), all of these; none named: trees, rocks and plants. Only what is loaded " +
                "(about 60 m around the player); nothing it takes comes back. Only where cheats are allowed: ask the player first", (a, output, error) =>
            {
                if (!CheatsAllowed()) { error(CheatRefusal("clear")); return null; }
                if (!ParseSite(a, out float radius, out Vector3 center, out List<string> words, error)) return null;
                var kinds = new HashSet<string>(words.Where(w => w != "all"), StringComparer.OrdinalIgnoreCase);
                if (words.Contains("all")) kinds.UnionWith(ClearKinds);
                if (kinds.Count == 0) kinds.UnionWith(new[] { "trees", "rocks", "plants" });
                string bad = kinds.FirstOrDefault(k => !ClearKinds.Contains(k));
                if (bad != null) { error($"clear: '{bad}' isn't one of: {string.Join(", ", ClearKinds)}, all"); return null; }

                var counts = new Dictionary<string, int>();
                foreach (ZNetView view in SceneInstances(ZNetScene.instance).Values.ToList())
                {
                    if (view == null || !view.IsValid()) continue;
                    GameObject go = view.gameObject;
                    Vector3 p = go.transform.position;
                    if (new Vector2(p.x - center.x, p.z - center.z).magnitude > radius) continue;
                    string kind = KindOf(go);
                    if (kind == null || !kinds.Contains(kind)) continue;
                    try
                    {
                        if (!view.IsOwner()) view.ClaimOwnership();
                        ZNetScene.instance.Destroy(go);
                        counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
                    }
                    catch (Exception e) { error($"{go.name}: {e.Message}"); }
                }
                output(new JObject
                {
                    ["center"] = Vec(center),
                    ["radius"] = radius,
                    ["removed"] = JObject.FromObject(counts),
                    ["total"] = counts.Values.Sum()
                });
                return null;
            });

            Builtin("terrain", "terrain level <radius> [height] [blend=5] [at <x> <z>] | terrain reset <radius> [at <x> <z>]: level the ground within that many metres of the " +
                "player (or the spot) to a height (default: the ground under the player), blending into the land around over `blend` metres; or put it back as the world " +
                "made it (heights and paint). The game lets ground move at most 8 m from where the world made it: the answer says how many points couldn't reach. Only " +
                "loaded land (about 60 m around the player). Only where cheats are allowed: ask the player first", (a, output, error) =>
            {
                if (!CheatsAllowed()) { error(CheatRefusal("terrain")); return null; }
                string mode = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                if (mode != "level" && mode != "reset") { error("terrain level <radius> [height] [blend=5] [at <x> <z>] | terrain reset <radius> [at <x> <z>]"); return null; }
                if (!ParseSite(a.Skip(1).ToArray(), out float radius, out Vector3 center, out List<string> words, error)) return null;
                float blend = 5f;
                float? height = null;
                foreach (string w in words)
                {
                    if (w.StartsWith("blend=") && TryF(w.Substring(6), out float b)) blend = Mathf.Clamp(b, 0f, 30f);
                    else if (TryF(w, out float h)) height = h;
                    else { error($"terrain: '{w}' isn't a height or blend=<metres>"); return null; }
                }
                if (mode == "reset") blend = 0f;
                float target = height ?? GroundAt(center);
                output(Shape(center, radius, blend, mode == "reset" ? (float?)null : target));
                return null;
            });
        }

        private void RegisterFields()
        {
            Builtin("fields", "fields <prefab> <Component> [field...]: the values on a prefab's component (all its public fields, or the ones named), objects by " +
                "name and lists one level deep: a piece's cost (fields piece_x Piece m_resources m_enabled m_usage m_category), an item's data (fields Coins ItemDrop " +
                "m_itemData), a station's conversions. Its snap points too (fields <prefab> snappoints)", (a, output, error) =>
            {
                if (a.Length < 3) { error("fields <prefab> <Component> [field...] (or fields <prefab> snappoints)"); return null; }
                GameObject go = ZNetScene.instance?.GetPrefab(a[1]) ?? ObjectDB.instance?.GetItemPrefab(a[1]);
                if (go == null) { error($"no prefab called {a[1]} (prefabs <text> finds names)"); return null; }
                if (a[2].Equals("snappoints", StringComparison.OrdinalIgnoreCase))
                {
                    var points = new JArray();
                    foreach (Transform t in go.transform) if (t.CompareTag("snappoint")) points.Add(new JObject { ["name"] = t.name, ["at"] = Vec(t.localPosition) });
                    output(new JObject { ["prefab"] = go.name, ["snappoints"] = points });
                    return null;
                }
                Component c = go.GetComponentsInChildren<Component>(true).FirstOrDefault(x => x != null && x.GetType().Name.Equals(a[2], StringComparison.OrdinalIgnoreCase));
                if (c == null) { error($"{go.name} has no {a[2]} (inspect {go.name} lists its components)"); return null; }
                var want = new HashSet<string>(a.Skip(3), StringComparer.OrdinalIgnoreCase);
                var o = new JObject { ["prefab"] = go.name, ["component"] = c.GetType().Name };
                foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (want.Count == 0 || want.Contains(f.Name)) o[f.Name] = FieldValue(f.GetValue(c), 2);
                output(o);
                return null;
            });
        }

        /// <summary>A field's value as JSON: numbers, words, enums by name, game objects by name, lists and plain objects a few levels deep.</summary>
        private static JToken FieldValue(object v, int depth)
        {
            if (v == null) return JValue.CreateNull();
            if (v is UnityEngine.Object uo) return uo == null ? JValue.CreateNull() : (JToken)uo.name;
            if (v is string || v.GetType().IsPrimitive) return JToken.FromObject(v);
            if (v is Enum) return v.ToString();
            if (v is Vector3 v3) return Vec(v3);
            if (v is System.Collections.IEnumerable list)
            {
                var arr = new JArray();
                int n = 0;
                foreach (object item in list) { if (++n > 40) { arr.Add("..."); break; } arr.Add(depth > 0 ? FieldValue(item, depth - 1) : (JToken)(item?.ToString() ?? "null")); }
                return arr;
            }
            if (depth <= 0) return v.ToString();
            var o = new JObject();
            foreach (var f in v.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) o[f.Name] = FieldValue(f.GetValue(v), depth - 1);
            return o;
        }

        /// <summary>What a world object is, for clear (null: never taken, a player).</summary>
        private static string KindOf(GameObject go)
        {
            if (go.GetComponent<Player>() != null) return null;
            if (go.GetComponent<Piece>() != null || go.GetComponent<WearNTear>() != null) return "pieces";
            if (go.GetComponent<Character>() != null) return "creatures";
            if (go.GetComponent<ItemDrop>() != null) return "items";
            if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null) return "trees";
            if (go.GetComponent<MineRock>() != null || go.GetComponent<MineRock5>() != null) return "rocks";
            string name = go.name.ToLowerInvariant();
            if (go.GetComponent<Destructible>() != null)
            {
                if (name.Contains("stub") || name.Contains("stump") || name.Contains("log")) return "trees";
                if (name.Contains("rock") || name.Contains("stone") || name.Contains("boulder") || name.Contains("deposit")) return "rocks";
                return "plants";
            }
            if (go.GetComponent<Pickable>() != null) return "plants";
            return "other";
        }

        /// <summary>Reads "&lt;radius&gt; ... [at x z]" (after the command word): the radius, the centre (the player, or the spot) and the other words.</summary>
        private static bool ParseSite(string[] a, out float radius, out Vector3 center, out List<string> words, Action<string> error)
        {
            radius = 0f;
            center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            words = new List<string>();
            if (a.Length < 2 || !TryF(a[1], out radius) || radius <= 0f) { error($"{a[0]}: give a radius in metres first, e.g. {a[0]} 40"); return false; }
            radius = Mathf.Min(radius, 120f);
            for (int i = 2; i < a.Length; i++)
            {
                if (a[i].Equals("at", StringComparison.OrdinalIgnoreCase) && i + 2 < a.Length && TryF(a[i + 1], out float x) && TryF(a[i + 2], out float z))
                {
                    center = new Vector3(x, center.y, z);
                    i += 2;
                }
                else words.Add(a[i].ToLowerInvariant());
            }
            return true;
        }

        private static bool TryF(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        private static float GroundAt(Vector3 p) =>
            ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out float h) ? h : p.y;

        /// <summary>Levels (target set) or resets (null) every loaded point of land within radius + blend of the centre.</summary>
        private static JObject Shape(Vector3 center, float radius, float blend, float? target)
        {
            var maps = new List<Heightmap>();
            for (float x = -radius - blend; x <= radius + blend; x += 16f)
                for (float z = -radius - blend; z <= radius + blend; z += 16f)
                    Heightmap.FindHeightmap(center + new Vector3(x, 0f, z), 12f, maps);
            List<Heightmap> distinct = maps.Where(m => m != null).Distinct().ToList();
            int changed = 0, limited = 0, owned = 0;
            foreach (Heightmap hm in distinct)
            {
                TerrainComp tc = hm.GetAndCreateTerrainCompiler();
                if (tc == null || !TcInit(tc)) continue;
                ZNetView view = TcView(tc);
                if (view == null || !view.IsValid()) continue;
                if (!view.IsOwner()) view.ClaimOwnership();
                owned++;
                int width = TcWidth(tc), pitch = width + 1;
                float scale = hm.m_scale;
                Vector3 origin = tc.transform.position;
                bool[] modH = TcModH(tc), modP = TcModP(tc);
                float[] level = TcLevel(tc), smooth = TcSmooth(tc);
                bool any = false;
                for (int i = 0; i < pitch; i++)
                    for (int j = 0; j < pitch; j++)
                    {
                        float wx = origin.x + (j - width / 2) * scale, wz = origin.z + (i - width / 2) * scale;
                        float dist = new Vector2(wx - center.x, wz - center.z).magnitude;
                        if (dist > radius + blend) continue;
                        int k = i * pitch + j;
                        if (target == null)
                        {
                            if (!modH[k] && level[k] == 0f && smooth[k] == 0f && (modP == null || !modP[k])) continue;
                            level[k] = 0f; smooth[k] = 0f; modH[k] = false;
                            if (modP != null) modP[k] = false;
                            any = true; changed++;
                            continue;
                        }
                        float t = dist <= radius ? 1f : Mathf.SmoothStep(1f, 0f, (dist - radius) / Mathf.Max(blend, 0.01f));
                        float now = hm.GetHeight(j, i) + origin.y;            // the ground there now (with any change already made)
                        float natural = now - level[k] - smooth[k];           // the ground as the world made it
                        float goal = Mathf.Lerp(natural, target.Value, t);
                        float delta = Mathf.Clamp(goal - natural, -GroundLimit, GroundLimit);
                        if (Mathf.Abs(goal - natural) > GroundLimit + 0.01f && t >= 1f) limited++;
                        if (Mathf.Abs(level[k] + smooth[k] - delta) < 0.005f) continue;
                        level[k] = delta; smooth[k] = 0f; modH[k] = true;
                        any = true; changed++;
                    }
                if (any)
                {
                    TcSave?.Invoke(tc, new object[] { false });
                    hm.Poke(0, false);
                    ClutterSystem.instance?.ResetGrass(hm.transform.position, hm.m_width * hm.m_scale / 2f);
                }
            }
            var result = new JObject
            {
                ["center"] = Vec(center),
                ["radius"] = radius,
                ["pointsChanged"] = changed,
                ["landPieces"] = owned,
                ["landPiecesFound"] = distinct.Count
            };
            if (target != null) { result["height"] = Math.Round(target.Value, 2); result["blend"] = blend; result["couldNotReach"] = limited; }
            if (owned < distinct.Count) result["note"] = "some land wasn't loaded: walk nearer and run it again";
            return result;
        }
    }
}
