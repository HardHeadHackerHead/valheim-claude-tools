using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// Valheim's building checks: a piece's colliders as the support check sees them, the support built pieces get now, and comfort. (render
    /// and inspect, for any game, are Claude Tools' own.)
    /// </summary>
    public partial class ValheimPlugin
    {
        private void RegisterBuilding()
        {
            Builtin("colliders", "colliders <prefab> [prefab...]: each piece's solid colliders in its own frame (as the game's support check sees them: box centre, rotation, size; other shapes as bounds) and its support settings (centre of mass, material, whether it holds others up)", (a, output, error) =>
            {
                if (a.Length < 2) { error("colliders <prefab> [prefab...]"); return null; }
                var all = new JObject();
                foreach (string name in a.Skip(1))
                {
                    GameObject prefab = FindPrefab(name);
                    if (prefab == null) { all[name] = "no such prefab"; continue; }
                    Transform root = prefab.transform;
                    var list = new JArray();
                    foreach (Collider col in prefab.GetComponentsInChildren<Collider>(true))
                    {
                        if (col.isTrigger || col.attachedRigidbody != null) continue;
                        Transform t = col.transform;
                        Matrix4x4 toRoot = root.worldToLocalMatrix * t.localToWorldMatrix;
                        Quaternion rot = Quaternion.Inverse(root.rotation) * t.rotation;
                        Vector3 scale = new Vector3(toRoot.GetColumn(0).magnitude, toRoot.GetColumn(1).magnitude, toRoot.GetColumn(2).magnitude);
                        var c = new JObject { ["path"] = t.name, ["layer"] = LayerMask.LayerToName(t.gameObject.layer) };
                        if (col is BoxCollider bc)
                        {
                            c["type"] = "box";
                            c["centre"] = Vec(toRoot.MultiplyPoint3x4(bc.center));
                            c["rot"] = new JArray(rot.x, rot.y, rot.z, rot.w);
                            c["size"] = Vec(Vector3.Scale(scale, bc.size));
                        }
                        else
                        {
                            Bounds lb = col is MeshCollider mc && mc.sharedMesh != null ? mc.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.zero);
                            if (col is CapsuleCollider cc) lb = new Bounds(cc.center, cc.direction == 0 ? new Vector3(cc.height, cc.radius * 2, cc.radius * 2) : cc.direction == 1 ? new Vector3(cc.radius * 2, cc.height, cc.radius * 2) : new Vector3(cc.radius * 2, cc.radius * 2, cc.height));
                            if (col is SphereCollider sc) lb = new Bounds(sc.center, Vector3.one * sc.radius * 2);
                            c["type"] = col is MeshCollider m2 ? (m2.convex ? "mesh-convex" : "mesh") : col.GetType().Name;
                            c["centre"] = Vec(toRoot.MultiplyPoint3x4(lb.center));      // its own box, turned with its part (the game takes the world bounds of this)
                            c["rot"] = new JArray(rot.x, rot.y, rot.z, rot.w);
                            c["size"] = Vec(Vector3.Scale(scale, lb.size));
                        }
                        list.Add(c);
                    }
                    WearNTear w = prefab.GetComponent<WearNTear>();
                    var info = new JObject { ["colliders"] = list };
                    if (w != null)
                    {
                        info["com"] = Vec(w.m_comOffset);
                        info["material"] = w.m_materialType.ToString();
                        info["supports"] = w.m_supports;
                        info["noSupportWear"] = w.m_noSupportWear;
                    }
                    all[prefab.name] = info;
                }
                output(new JObject { ["colliders"] = all });
                return null;
            });

            Builtin("support", "support [radius=20] [place=look] [filter]: built pieces near a place with the support the game gives them now (and their material's minimum and maximum), weakest first", (a, output, error) =>
            {
                float radius = a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr) ? rr : 20f;
                if (!Frame(a.Length > 2 ? a[2] : "look", out Vector3 at, out float _, out string err)) { error(err); return null; }
                string filter = a.Length > 3 ? a[3] : null;
                var getSupport = HarmonyLib.AccessTools.Method(typeof(WearNTear), "GetSupport");
                var getProps = HarmonyLib.AccessTools.Method(typeof(WearNTear), "GetMaterialProperties");
                var rows = new List<KeyValuePair<float, JObject>>();
                foreach (WearNTear w in UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None))
                {
                    if (w == null || (w.transform.position - at).sqrMagnitude > radius * radius) continue;
                    string name = w.gameObject.name.Replace("(Clone)", "").Trim();
                    if (filter != null && !name.Contains(filter)) continue;
                    float s = (float)getSupport.Invoke(w, null);
                    var args = new object[] { 0f, 0f, 0f, 0f };
                    getProps.Invoke(w, args);
                    float max = (float)args[0], min = (float)args[1];
                    rows.Add(new KeyValuePair<float, JObject>(max > 0 ? (s - min) / max : 0f, new JObject { ["piece"] = name, ["support"] = Math.Round(s, 1), ["min"] = min, ["max"] = max,
                        ["position"] = Vec(w.transform.position), ["health"] = Math.Round(w.GetHealthPercentage() * 100f) }));
                }
                output(new JObject { ["pieces"] = new JArray(rows.OrderBy(r => r.Key).Take(60).Select(r => (JToken)r.Value)), ["count"] = rows.Count });
                return null;
            });

            Builtin("comfort", "comfort [prefab...]: with prefabs, the comfort level they would give together (under a roof, all within 10 m), each piece's comfort and comfort group, and which count; without, the player's comfort now and the pieces giving it", (a, output, error) =>
            {
                var pieces = new List<Piece>();
                bool real = a.Length < 2;
                if (real)
                {
                    Player p = Player.m_localPlayer;
                    Piece.GetAllComfortPiecesInRadius(p.transform.position, 10f, pieces);
                }
                else
                    foreach (string name in a.Skip(1))
                    {
                        Piece pc = FindPrefab(name)?.GetComponent<Piece>();
                        if (pc == null) { error($"no piece called '{name}'"); return null; }
                        pieces.Add(pc);
                    }
                // the game's rule (SE_Rested.CalculateComfortLevel): 1, +1 under a roof, + the best of each group; pieces of no group count
                // once per name
                var sorted = pieces.OrderBy(pc => (int)pc.m_comfortGroup).ThenByDescending(pc => pc.m_comfort).ThenByDescending(pc => pc.m_name, StringComparer.Ordinal).ToList();
                int level = 2;
                var rows = new JArray();
                for (int i = 0; i < sorted.Count; i++)
                {
                    Piece pc = sorted[i], prev = i > 0 ? sorted[i - 1] : null;
                    bool counts = prev == null || !((pc.m_comfortGroup != 0 && pc.m_comfortGroup == prev.m_comfortGroup) || pc.m_name == prev.m_name);
                    if (counts) level += pc.m_comfort;
                    rows.Add($"{Utils.GetPrefabName(pc.gameObject)} ({Localization.instance.Localize(pc.m_name)}): {pc.m_comfort}, group {pc.m_comfortGroup}{(counts ? "" : "  (not counted: its group or kind is already counted)")}");
                }
                var result = new JObject { ["level"] = level, ["pieces"] = rows };
                if (real)
                {
                    Player p = Player.m_localPlayer;
                    result["inShelter"] = p.InShelter();
                    result["gameSays"] = SE_Rested.CalculateComfortLevel(p);
                }
                output(result);
                return null;
            });
        }
    }
}
