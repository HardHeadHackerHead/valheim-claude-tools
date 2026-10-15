using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YourMod
{
    /// <summary>
    /// Frees what an older copy of the mod made, after a hot reload. .NET never unloads the old copy's code (a few MB, until the game
    /// quits), but that is the small part: the prefabs, materials, meshes, textures, icons and sounds it made stay too, and nothing ever
    /// frees them. PieceReload and ItemReload keep the old prefabs alive on unload on purpose (no gap for an area loading meanwhile), and
    /// the pieces standing in the world still draw with the old copy's materials and meshes (destroyed, they'd show magenta or nothing),
    /// so the old copy can't destroy them itself. Every reload left a full set: 35 MB a reload for a mod with 30 icons and 190 materials
    /// (Jarldom: 59 copies, 2 GB after a night of reloads), and 19 copies of every prefab of a building kit.
    ///
    /// So the old copy hands over what it made (through the AppDomain: the copies share no statics), and the new one, once its own exist,
    /// puts them in the old ones' place wherever they're still used (renderers, meshes, colliders, sounds, piece and item icons, UI images)
    /// and destroys the rest. One still used with nothing of the new copy's like it is kept and offered again at the next reload; so is an
    /// old item prefab that a loaded item still points at (its m_dropPrefab: destroyed, the item would save with no name and be lost).
    ///
    ///   GameObject prefab = Leftovers.Own(Object.Instantiate(from, holder));   // every prefab you make (and your holder object)
    ///   var bark = Leftovers.Own(new Material(wood) { name = "YourMod bark" });  // every material, mesh, texture, sprite, sound you make
    ///   piece.m_icon = Leftovers.Own(Icon.Make(prefab));                        // a sprite's texture is owned with it
    ///   plugin Awake, after registering (ReplacePrefab + Reattach, Replace + Repoint): Leftovers.TakeOver();
    ///   plugin OnDestroy (with PieceReload/ItemReload: instead of destroying them yourself): Leftovers.HandOver();
    ///
    /// Two of a copy's own are taken as alike by type and name (and a material's shader, colour and texture): name what you make, and
    /// give a material copied from the game's a name of your own. Made later than Awake (a lazy icon, a look built when first placed)?
    /// Call TakeOver once those exist, and a few seconds after anything that re-skins standing pieces in its Start. Items your mod holds
    /// in memory (a list of its own, outside any bag): pass them, TakeOver(items), or their old prefab is kept.
    /// TakeOver searches the loaded scene once (a few hundred ms in a big base), so only after a reload: on a fresh start it has nothing
    /// to do and returns at once.
    /// A template from Claude Tools (BepInEx/claude/templates): copy it into your mod and change the namespace (and Key).
    /// </summary>
    internal static class Leftovers
    {
        private const string Key = "YourMod.Leftovers";   // where one copy hands over to the next: name it after your mod
        private static readonly List<Object> Mine = new List<Object>();      // what this copy made
        private static readonly List<Object> Pending = new List<Object>();   // an older copy's, not freed yet
        private static bool _taken;
        private static readonly Type ClipType = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule"), SourceType = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");

        /// <summary>Something this copy made (a prefab, a material, mesh, texture, sprite or sound): freed by the next copy. Returns it.</summary>
        public static T Own<T>(T made) where T : Object
        {
            if (made == null) return made;
            Mine.Add(made);
            if (made is Sprite s && s.texture != null) Mine.Add(s.texture);
            return made;
        }

        /// <summary>The old copy, unloading: hand over what it made, and what it was still keeping of an older copy's.</summary>
        public static void HandOver()
        {
            var all = AppDomain.CurrentDomain.GetData(Key) as List<Object> ?? new List<Object>();
            all.AddRange(Mine.Concat(Pending).Where(o => o != null).Distinct());
            Mine.Clear(); Pending.Clear();
            AppDomain.CurrentDomain.SetData(Key, all);
        }

        /// <summary>
        /// The new copy, its own made and registered: put them in the old ones' place where those are still used, and destroy the rest.
        /// Once per copy (later calls do nothing). Returns how many were freed.
        /// </summary>
        public static int TakeOver(params ItemDrop.ItemData[] heldItems)
        {
            if (_taken) return 0;
            _taken = true;
            if (AppDomain.CurrentDomain.GetData(Key) is List<Object> handed) Pending.AddRange(handed.Where(o => o != null));
            AppDomain.CurrentDomain.SetData(Key, null);
            Pending.RemoveAll(o => o == null);
            if (Pending.Count == 0) return 0;

            var old = new HashSet<Object>(Pending);
            var like = new Dictionary<string, Object>();
            foreach (Object o in Mine) { string k = KeyOf(o); if (k != null && !old.Contains(o) && !like.ContainsKey(k)) like[k] = o; }
            var used = new HashSet<Object>();
            T Swap<T>(T o) where T : Object
            {
                if (o == null || !old.Contains(o)) return o;
                if (KeyOf(o) is string k && like.TryGetValue(k, out Object n) && n is T t) return t;
                used.Add(o);
                return o;
            }
            bool InOld(Component c) { for (Transform t = c.transform; t != null; t = t.parent) if (old.Contains(t.gameObject)) return true; return false; }

            bool looks = Pending.Any(o => o is Material || o is Mesh), sounds = ClipType != null && Pending.Any(o => ClipType.IsInstanceOfType(o)), icons = Pending.Any(o => o is Sprite), prefabs = Pending.Any(o => o is GameObject);
            if (looks)
            {
                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (InOld(r)) continue;   // (an old prefab's own: it goes too)
                    Material[] mats = r.sharedMaterials; bool changed = false;
                    for (int i = 0; i < mats.Length; i++) { Material m = Swap(mats[i]); if (m != mats[i]) { mats[i] = m; changed = true; } }
                    if (changed) r.sharedMaterials = mats;
                    if (r is SkinnedMeshRenderer s) { Mesh m = Swap(s.sharedMesh); if (m != s.sharedMesh) s.sharedMesh = m; }
                }
                foreach (MeshFilter f in Resources.FindObjectsOfTypeAll<MeshFilter>()) { if (InOld(f)) continue; Mesh m = Swap(f.sharedMesh); if (m != f.sharedMesh) f.sharedMesh = m; }
                foreach (MeshCollider c in Resources.FindObjectsOfTypeAll<MeshCollider>()) { if (InOld(c)) continue; Mesh m = Swap(c.sharedMesh); if (m != c.sharedMesh) c.sharedMesh = m; }
            }
            if (sounds)
            {
                // (by reflection: the project template doesn't reference UnityEngine.AudioModule, where the sound types live)
                var clip = SourceType?.GetProperty("clip");
                if (clip != null) foreach (Object a in Resources.FindObjectsOfTypeAll(SourceType)) { if (InOld((Component)a)) continue; var c = (Object)clip.GetValue(a, null); var n = Swap(c); if (n != c) clip.SetValue(a, n, null); }
                var clips = typeof(ZSFX).GetField("m_audioClips");
                foreach (ZSFX z in Resources.FindObjectsOfTypeAll<ZSFX>()) { if (InOld(z) || !(clips?.GetValue(z) is Object[] list)) continue; for (int i = 0; i < list.Length; i++) list[i] = Swap(list[i]); }
            }
            if (icons)
            {
                foreach (Piece p in Resources.FindObjectsOfTypeAll<Piece>()) if (!InOld(p)) p.m_icon = Swap(p.m_icon);
                foreach (UnityEngine.UI.Image i in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>()) if (!InOld(i)) i.sprite = Swap(i.sprite);
            }
            if (icons || prefabs)
            {
                // Items: a dropped one has its own data; one in a bag or chest shares its prefab's. An old prefab still pointed at stays.
                var seen = new HashSet<ItemDrop.ItemData.SharedData>();
                void Item(ItemDrop.ItemData item)
                {
                    if (item == null) return;
                    if (item.m_dropPrefab != null && old.Contains(item.m_dropPrefab)) used.Add(item.m_dropPrefab);
                    Sprite[] list = item.m_shared?.m_icons;
                    if (list != null && seen.Add(item.m_shared)) for (int i = 0; i < list.Length; i++) list[i] = Swap(list[i]);
                }
                foreach (ItemDrop d in Resources.FindObjectsOfTypeAll<ItemDrop>()) if (!InOld(d)) Item(d.m_itemData);
                foreach (Humanoid h in Object.FindObjectsByType<Humanoid>(FindObjectsSortMode.None)) if (h.GetInventory() != null) foreach (var item in h.GetInventory().GetAllItems()) Item(item);
                foreach (Container c in Object.FindObjectsByType<Container>(FindObjectsSortMode.None)) if (c.GetInventory() != null) foreach (var item in c.GetInventory().GetAllItems()) Item(item);
                if (heldItems != null) foreach (var item in heldItems) Item(item);
            }
            // (a kept one keeps what it's made of: an icon its picture, a prefab its holder and everything under it)
            foreach (Object o in used.ToList())
            {
                if (o is Sprite s && s.texture != null && old.Contains(s.texture)) used.Add(s.texture);
                if (o is GameObject g) for (Transform t = g.transform.parent; t != null; t = t.parent) if (old.Contains(t.gameObject)) used.Add(t.gameObject);
            }
            int freed = 0;
            foreach (Object o in Pending)
            {
                if (used.Contains(o)) continue;
                if (o is GameObject g && used.OfType<GameObject>().Any(u => u.transform.IsChildOf(g.transform))) { used.Add(o); continue; }   // (a holder with a kept prefab in it)
                Object.Destroy(o); freed++;
            }
            Pending.RemoveAll(o => !used.Contains(o));
            Debug.Log($"[{Key}] an older copy's: {freed} freed, {Pending.Count} still in use and kept");
            return freed;
        }

        // Two copies' alike: the same kind, made the same way, under the same name. (A texture and a prefab have no like: a texture
        // goes with its sprite, a prefab was replaced by name already.)
        private static string KeyOf(Object o)
        {
            switch (o)
            {
                case Material m:
                    string color = m.HasProperty("_Color") ? m.color.ToString() : "", glow = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor").ToString() : "";
                    return $"M|{m.name}|{m.shader?.name}|{color}|{glow}|{m.mainTexture?.name}|{m.mainTextureScale}";
                case Mesh me: return $"Me|{me.name}|{me.vertexCount}|{me.subMeshCount}";
                case Sprite s: return $"S|{s.name}";
                default: return ClipType != null && ClipType.IsInstanceOfType(o) ? $"A|{o.name}" : null;
            }
        }
    }
}
