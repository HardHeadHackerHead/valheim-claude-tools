using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// "tcdoctor": the ground's change records (each zone's _TerrainCompiler: hoe, pickaxe and cultivator work, levelling by mods), and fixing
    /// the two ways they go wrong. A record made before its ground had loaded never attaches to it ("Terrain compiler could not find hmap" and a
    /// NullReferenceException in TerrainComp.Load in the log): the work there looks undone, and the next change makes a second record at the same
    /// spot. A player joining can't delete a second record it doesn't own and loops on the loading screen (a friend, 2026-10-12). report finds
    /// both; reattach drops this game's copy of an unattached record so the game makes it again with its ground (the saved record stays, and the
    /// game's own duplicate check then keeps the one with the most work); remove takes a record away by its id, on the host.
    /// </summary>
    public partial class ValheimPlugin
    {
        private const string TcDoctorUsage = "tcdoctor [report [at <x> <z>] | reattach | remove <userid:id>... | points <x0> <x1> <z0> <z1> | tilled [radius=60] | " +
            "untill <x0> <x1> <z0> <z1>]: the ground's change records (_TerrainCompiler, one per zone: hoe, pickaxe, cultivator and mods' levelling). report: " +
            "spots with more than one record and records loaded here but not attached to their ground (the cause of a friend looping on the loading screen; " +
            "the log says \"Terrain compiler could not find hmap\"), each with its id, owner, operations and changed points. reattach: drop this game's copy " +
            "of each unattached record so the game makes it again with its ground (nothing saved changes). remove: take records away by id (the host, " +
            "where cheats are allowed; keep the one with the most operations; back up the world first). points: every changed ground point in a box. " +
            "tilled: cultivated ground near the player in 4 m cells, and how much has a plant. untill: take cultivating off the points in a box (where " +
            "cheats are allowed)";

        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> TcHmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> TcPaint = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<List<TerrainComp>> TcAll = AccessTools.StaticFieldRefAccess<List<TerrainComp>>(AccessTools.Field(typeof(TerrainComp), "s_instances"));

        private void RegisterTcDoctor() => Builtin("tcdoctor", TcDoctorUsage, (a, output, error) => TcDoctor(a, output, error));

        private IEnumerator TcDoctor(string[] a, Action<JObject> output, Action<string> error)
        {
            if (ZDOMan.instance == null || ZNet.instance == null || ZNetScene.instance == null) { error("tcdoctor: not in a world"); yield break; }
            string mode = a.Length > 1 ? a[1].ToLowerInvariant() : "report";
            switch (mode)
            {
                case "report": TcReport(a, output); break;
                case "reattach": TcReattach(output); break;
                case "remove": TcRemove(a, output, error); break;
                case "points": TcPoints(a, false, output, error); break;
                case "untill": TcPoints(a, true, output, error); break;
                case "tilled": TcTilled(a, output); break;
                default: error(TcDoctorUsage); break;
            }
        }

        // ---- report ----

        private void TcReport(string[] a, Action<JObject> output)
        {
            var records = new List<ZDO>();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative("_TerrainCompiler", records, ref index)) { }   // (a command, once: all of them)
            List<TerrainComp> attached = TcAll();
            var doubles = new JArray();
            foreach (var spot in records.GroupBy(z => Vector2Int.RoundToInt(new Vector2(z.GetPosition().x, z.GetPosition().z))).Where(g => g.Count() > 1))
                doubles.Add(new JObject
                {
                    ["spot"] = new JArray(spot.Key.x, spot.Key.y),
                    ["records"] = new JArray(spot.OrderByDescending(TcOps).Select(TcRecordJson)),
                    ["note"] = "the game keeps the one with the most operations; a joining player can't remove one it doesn't own",
                });
            var loose = new JArray();
            foreach (TerrainComp tc in UnityEngine.Object.FindObjectsByType<TerrainComp>(FindObjectsSortMode.None))
            {
                ZDO z = tc != null ? tc.GetComponent<ZNetView>()?.GetZDO() : null;
                if (z != null && !attached.Contains(tc)) loose.Add(TcRecordJson(z));
            }
            var o = new JObject
            {
                ["records"] = records.Count,
                ["attachedHere"] = attached.Count,
                ["host"] = ZNet.instance.IsServer(),
                ["players"] = new JArray(ZNet.instance.GetPeers().Select(p => $"{p.m_playerName} at {p.m_refPos.x:0},{p.m_refPos.z:0}")),
                ["sameSpot"] = doubles,
                ["notAttached"] = loose,
            };
            int at = Array.FindIndex(a, w => w.Equals("at", StringComparison.OrdinalIgnoreCase));
            if (at > 0 && a.Length > at + 2 && TryF(a[at + 1], out float x) && TryF(a[at + 2], out float zz))
            {
                var p = new Vector3(x, 0f, zz);
                o["at"] = new JObject
                {
                    ["groundLoaded"] = Heightmap.FindHeightmap(p) != null,
                    ["recordFound"] = TerrainComp.FindTerrainCompiler(p)?.GetComponent<ZNetView>()?.GetZDO()?.m_uid.ToString(),
                };
            }
            if (loose.Count > 0) o["fix"] = "tcdoctor reattach (this game's copies only; nothing saved changes), then tcdoctor report again";
            else if (doubles.Count > 0) o["fix"] = "on the host, once the second record's ground is loaded: back up the world, then tcdoctor remove <id> for the record with fewer operations";
            output(o);
        }

        private static int TcOps(ZDO z)
        {
            try
            {
                byte[] b = z.GetByteArray(ZDOVars.s_TCData);
                if (b == null) return 0;
                var pk = new ZPackage(Utils.Decompress(b));
                pk.ReadInt();
                return pk.ReadInt();
            }
            catch (Exception) { return 0; }
        }

        private static JObject TcRecordJson(ZDO z)
        {
            var o = new JObject
            {
                ["id"] = $"{z.m_uid.UserID}:{z.m_uid.ID}", ["at"] = Vec(z.GetPosition()), ["owner"] = TcOwner(z.GetOwner()), ["revision"] = z.DataRevision,
            };
            byte[] b = z.GetByteArray(ZDOVars.s_TCData);
            if (b == null) o["data"] = "none";
            else
                try
                {
                    var pk = new ZPackage(Utils.Decompress(b));
                    pk.ReadInt();
                    o["operations"] = pk.ReadInt();
                    pk.ReadVector3(); pk.ReadSingle();
                    int n = pk.ReadInt(), heights = 0;
                    float most = 0f;
                    for (int i = 0; i < n; i++)
                        if (pk.ReadBool()) { heights++; float level = pk.ReadSingle(), smooth = pk.ReadSingle(); most = Mathf.Max(most, Mathf.Abs(level + smooth)); }
                    int pn = pk.ReadInt(), painted = 0;
                    for (int i = 0; i < pn; i++) if (pk.ReadBool()) { painted++; pk.ReadSingle(); pk.ReadSingle(); pk.ReadSingle(); pk.ReadSingle(); }
                    o["heightPoints"] = heights; o["mostMoved"] = Math.Round(most, 2); o["paintedPoints"] = painted; o["bytes"] = b.Length;
                }
                catch (Exception e) { o["data"] = "unreadable: " + e.Message; }
            if (SceneInstances(ZNetScene.instance).TryGetValue(z, out ZNetView v) && v != null && v.GetComponent<TerrainComp>() is TerrainComp tc)
                o["here"] = TcAll().Contains(tc) ? "loaded, attached" : TcHmap(tc) == null ? "loaded, NOT attached (no ground when it was made)" : "loaded, not registered";
            else o["here"] = "not loaded";
            return o;
        }

        private static string TcOwner(long uid)
        {
            if (uid == 0L) return "nobody";
            if (uid == ZDOMan.GetSessionID()) return "this game";
            ZNetPeer p = ZNet.instance.GetPeers().FirstOrDefault(x => x.m_uid == uid);
            return p != null ? p.m_playerName : $"gone ({uid})";
        }

        // ---- reattach: drop this game's copy only; the game makes it again next frame, now with its ground ----

        private void TcReattach(Action<JObject> output)
        {
            List<TerrainComp> attached = TcAll();
            var remade = new JArray();
            var waiting = new JArray();
            foreach (TerrainComp tc in UnityEngine.Object.FindObjectsByType<TerrainComp>(FindObjectsSortMode.None))
            {
                if (tc == null || attached.Contains(tc)) continue;
                ZNetView v = tc.GetComponent<ZNetView>();
                ZDO z = v != null ? v.GetZDO() : null;
                if (z == null) continue;
                if (Heightmap.FindHeightmap(z.GetPosition()) == null) { waiting.Add($"{z.m_uid.UserID}:{z.m_uid.ID}"); continue; }   // (its ground still isn't here)
                SceneInstances(ZNetScene.instance).Remove(z);
                v.ResetZDO();
                UnityEngine.Object.Destroy(tc.gameObject);
                remade.Add($"{z.m_uid.UserID}:{z.m_uid.ID} at {z.GetPosition().x:0},{z.GetPosition().z:0}");
                Logger.LogWarning($"tcdoctor: re-making terrain record {z.m_uid} at {z.GetPosition()} so it attaches to its ground");
            }
            output(new JObject
            {
                ["remade"] = remade, ["groundNotLoadedYet"] = waiting,
                ["note"] = remade.Count > 0 ? "made again within a frame or two: run tcdoctor report to check they attached" : "no unattached records with their ground loaded",
            });
        }

        // ---- remove: the host takes records away by id ----

        private void TcRemove(string[] a, Action<JObject> output, Action<string> error)
        {
            if (!ZNet.instance.IsServer()) { error("tcdoctor remove: only on the host (the world's owner); others can't delete records they don't own"); return; }
            if (!CheatsAllowed()) { error(CheatRefusal("tcdoctor remove")); return; }
            var removed = new JArray();
            foreach (string s in a.Skip(2))
            {
                string[] p = s.Split(':');
                if (p.Length != 2 || !long.TryParse(p[0], out long user) || !uint.TryParse(p[1], out uint id)) { error($"'{s}' isn't an id (userid:id, as report gives it)"); continue; }
                ZDO z = ZDOMan.instance.GetZDO(new ZDOID(user, id));
                if (z == null || z.GetPrefab() != "_TerrainCompiler".GetStableHashCode()) { error($"{s}: no terrain record with that id"); continue; }
                z.SetOwner(ZDOMan.GetSessionID());
                if (SceneInstances(ZNetScene.instance).TryGetValue(z, out ZNetView v) && v != null) ZNetScene.instance.Destroy(v.gameObject);
                else ZDOMan.instance.DestroyZDO(z);
                removed.Add(s);
                Logger.LogWarning("tcdoctor: removed terrain record " + s);
            }
            if (removed.Count == 0 && a.Length < 3) error("tcdoctor remove <userid:id>... (ids from tcdoctor report)");
            output(new JObject { ["removed"] = removed });
        }

        // ---- points / untill: the ground points in a box, and taking cultivating off them ----

        private void TcPoints(string[] a, bool untill, Action<JObject> output, Action<string> error)
        {
            if (a.Length < 6 || !TryF(a[2], out float x0) || !TryF(a[3], out float x1) || !TryF(a[4], out float z0) || !TryF(a[5], out float z1))
            { error($"tcdoctor {(untill ? "untill" : "points")} <x0> <x1> <z0> <z1>"); return; }
            if (untill && !CheatsAllowed()) { error(CheatRefusal("tcdoctor untill")); return; }
            if (Mathf.Abs(x1 - x0) > 64f || Mathf.Abs(z1 - z0) > 64f) { error("a box at most 64 m on a side"); return; }
            var records = new JArray();
            int undone = 0;
            foreach (TerrainComp tc in TcAll())
            {
                Heightmap hm = TcHmap(tc);
                if (hm == null) continue;
                Vector3 o = tc.transform.position;
                int width = TcWidth(tc), pitch = width + 1;
                float scale = hm.m_scale;
                bool[] modH = TcModH(tc), modP = TcModP(tc);
                float[] level = TcLevel(tc), smooth = TcSmooth(tc);
                Color[] paint = TcPaint(tc);
                ZNetView view = TcView(tc);
                var points = new JArray();
                int changed = 0;
                for (int i = 0; i < pitch; i++)
                {
                    float wz = o.z + (i - width / 2) * scale;
                    if (wz < Mathf.Min(z0, z1) || wz > Mathf.Max(z0, z1)) continue;
                    for (int j = 0; j < pitch; j++)
                    {
                        float wx = o.x + (j - width / 2) * scale;
                        if (wx < Mathf.Min(x0, x1) || wx > Mathf.Max(x0, x1)) continue;
                        int k = i * pitch + j;
                        if (!modH[k] && !modP[k]) continue;
                        Color c = paint[k];
                        points.Add($"{wx:0},{wz:0} height {(modH[k] ? $"level {level[k]:0.00} smooth {smooth[k]:0.00}" : "-")} paint {(modP[k] ? $"{c.r:0.0}/{c.g:0.0}/{c.b:0.0}/{c.a:0.0}" : "-")}");
                        if (!untill || !modP[k] || c.g < 0.3f) continue;   // (green: cultivated)
                        // its paint from the nearest point west of it in the row that isn't cultivated; the cultivator's smoothing taken off
                        int from = -1;
                        for (int jj = j - 1; jj >= 0; jj--) { int kk = i * pitch + jj; if (!modP[kk] || paint[kk].g < 0.3f) { from = kk; break; } }
                        if (from < 0) continue;
                        if (!view.IsOwner()) view.ClaimOwnership();
                        modP[k] = modP[from];
                        paint[k] = modP[from] ? paint[from] : new Color(0f, 0f, 0f, c.a);
                        if (modH[k] && smooth[k] != 0f) { smooth[k] = 0f; if (level[k] == 0f) modH[k] = false; }
                        changed++;
                    }
                }
                if (changed > 0)
                {
                    TcSave.Invoke(tc, new object[] { false });
                    hm.Poke(0, false);
                    ClutterSystem.instance?.ResetGrass(hm.transform.position, hm.m_width * hm.m_scale / 2f);
                    undone += changed;
                    Logger.LogWarning($"tcdoctor: took cultivating off {changed} points of {view.GetZDO().m_uid}");
                }
                if (points.Count > 0) records.Add(new JObject { ["record"] = $"{view.GetZDO().m_uid.UserID}:{view.GetZDO().m_uid.ID}", ["at"] = Vec(o), ["points"] = points });
            }
            var result = new JObject { ["records"] = records };
            if (untill) result["untilled"] = undone;
            output(result);
        }

        // ---- tilled: cultivated ground near the player in 4 m cells ----

        private void TcTilled(string[] a, Action<JObject> output)
        {
            float radius = Mathf.Clamp(F(a, 2, 60f), 8f, 150f);
            Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            var plants = new List<Vector3>();
            foreach (Plant p in UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None)) plants.Add(p.transform.position);
            foreach (Pickable p in UnityEngine.Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
                if (p.GetComponent<Piece>() != null || p.name.StartsWith("Pickable_")) plants.Add(p.transform.position);
            var cells = new SortedDictionary<string, int[]>();
            foreach (TerrainComp tc in TcAll())
            {
                Heightmap hm = TcHmap(tc);
                if (hm == null) continue;
                Vector3 o = tc.transform.position;
                int width = TcWidth(tc), pitch = width + 1;
                bool[] modP = TcModP(tc);
                Color[] paint = TcPaint(tc);
                for (int k = 0; k < modP.Length; k++)
                {
                    if (!modP[k] || paint[k].g < 0.3f) continue;
                    float wx = o.x + (k % pitch - width / 2) * hm.m_scale, wz = o.z + (k / pitch - width / 2) * hm.m_scale;
                    if (new Vector2(wx - me.x, wz - me.z).magnitude > radius) continue;
                    string key = $"{Mathf.FloorToInt(wx / 4f) * 4},{Mathf.FloorToInt(wz / 4f) * 4}";
                    if (!cells.TryGetValue(key, out int[] c)) cells[key] = c = new int[2];
                    c[0]++;
                    if (plants.Any(p => Mathf.Abs(p.x - wx) < 1.5f && Mathf.Abs(p.z - wz) < 1.5f)) c[1]++;
                }
            }
            output(new JObject
            {
                ["center"] = Vec(me), ["radius"] = radius,
                ["cells"] = new JArray(cells.Select(kv => $"{kv.Key}: {kv.Value[0]} cultivated, {kv.Value[1]} with a plant")),
                ["note"] = "4 m cells by their south-west corner (x,z); untill <x0> <x1> <z0> <z1> takes cultivating off a box",
            });
        }
    }
}
