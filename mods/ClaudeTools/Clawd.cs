using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// Clawd: a little Claude (the orange block crab) who follows the player once summoned ("claude clawd" in the console, F5, or "clawd" in a
    /// request) and says in a speech bubble what Claude is doing (each request command, and how it went), what happens to the player (night,
    /// rain, a new land, being hurt, standing still a long time) and whatever Claude tells him to ("clawd say ..."). Pet him with the use key.
    /// He is scenery on this game only: no prefab, no ZDO, nothing saved but the Summoned setting, and other players don't see him.
    /// </summary>
    public partial class Plugin
    {
        private ConfigEntry<bool> _clawdSummoned, _clawdChatty;
        internal static Clawd Buddy;
        internal static bool ClawdChatty => Instance != null && Instance._clawdChatty.Value;

        private void BindClawd()
        {
            _clawdSummoned = Config.Bind("Clawd", "Summoned", false,
                "Clawd (a little Claude) follows you and says what Claude is doing. Set by \"claude clawd\" and \"claude clawd bye\" in the console (F5).");
            _clawdChatty = Config.Bind("Clawd", "Chatty", true,
                "Clawd also remarks on what happens to you (night, rain, a new land, being hurt) and mutters when you stand still. Off: he only says what Claude is doing.");
        }

        private void RegisterClawd()
        {
            Builtin("clawd", "clawd [bye | here | say <text> | <mood> [text] | chatty on|off]: summon Clawd, a little Claude who follows the player and says " +
                "what Claude is doing in a speech bubble (on this game only: other players don't see him); say: he says the text (talk to the player " +
                "through him); a mood (happy, sad, think, wave, dance, scared, love, sleep, idle) with an optional text; bye sends him away", (a, output, error) =>
            {
                string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                if (sub == "bye" || sub == "off" || sub == "dismiss")
                {
                    _clawdSummoned.Value = false;
                    if (Buddy != null) Buddy.Leave(Rest(a, 2));
                    Buddy = null;
                    output(new JObject { ["clawd"] = "gone" });
                    return null;
                }
                if (sub == "chatty")
                {
                    _clawdChatty.Value = a.Length < 3 || a[2].ToLowerInvariant() != "off";
                    output(new JObject { ["chatty"] = _clawdChatty.Value });
                    return null;
                }
                Clawd.Mood mood = Clawd.Mood.Idle;
                if (sub != "" && sub != "here" && sub != "say" && !Enum.TryParse(sub, true, out mood))
                {
                    error($"no '{sub}' (bye, here, say <text>, chatty on|off, or a mood: {string.Join(", ", Enum.GetNames(typeof(Clawd.Mood))).ToLowerInvariant()})");
                    return null;
                }
                string text = sub == "" || sub == "here" ? "" : Rest(a, 2);
                if (sub == "say" && text == "") { error("say what? (clawd say <text>)"); return null; }

                bool summoned = Buddy == null;
                if (summoned)
                {
                    _clawdSummoned.Value = true;
                    Buddy = Clawd.Spawn(Player.m_localPlayer, text == "" ? Clawd.Pick(Clawd.Hello) : null);
                }
                if (sub == "here") Buddy.ComeHere();
                else if (sub != "" && sub != "say") Buddy.SetMood(mood);
                if (text != "") Buddy.Say(text, true);
                output(new JObject { ["clawd"] = summoned ? "summoned" : "here", ["mood"] = Buddy.CurrentMood.ToString().ToLowerInvariant(), ["chatty"] = _clawdChatty.Value });
                return null;
            });
        }

        /// <summary>Every frame: bring Clawd (back) once a player is in the world, if he was summoned.</summary>
        private void ClawdUpdate()
        {
            if (!_clawdSummoned.Value || Buddy != null) return;
            Player p = Player.m_localPlayer;
            if (p == null || p.InIntro() || p.IsDead() || Chat.instance == null || ZNetScene.instance == null) return;
            Buddy = Clawd.Spawn(p, Clawd.Pick(Clawd.Back));
        }

        private static void ClawdGone()
        {
            if (Buddy != null) Buddy.Vanish();
            Buddy = null;
        }

        /// <summary>A request or console command starting: Clawd says what Claude is doing.</summary>
        internal static void ClawdOnCommand(Command cmd, string[] a)
        {
            if (Buddy != null && cmd.Name != "clawd") Buddy.Narrate(cmd.Name, cmd.Owner, a);
        }

        /// <summary>A request finished: <paramref name="ran"/> commands besides clawd's own, the first error if any.</summary>
        internal static void ClawdOnDone(int ran, string firstError)
        {
            if (Buddy != null) Buddy.Finished(ran, firstError);
        }

        internal static void ClawdScreenshot()
        {
            if (Buddy != null) Buddy.Remark(Clawd.Mood.Happy, Clawd.Pick(Clawd.Cheese), true);
        }
    }

    /// <summary>Clawd hears chat: a message with his name in it gets an answer.</summary>
    [HarmonyPatch(typeof(Chat), nameof(Chat.OnNewChatMessage))]
    internal static class ClawdHearsChat
    {
        private static void Postfix(Talker.Type type, string text)
        {
            if (Plugin.Buddy != null && type != Talker.Type.Ping && !string.IsNullOrEmpty(text)) Plugin.Buddy.Heard(text);
        }
    }

    internal class Clawd : MonoBehaviour, Hoverable, Interactable, IDestructible
    {
        internal const string ObjectName = "ClaudeTools_Clawd";
        internal enum Mood { Idle, Happy, Sad, Think, Wave, Dance, Scared, Love, Sleep }

        // ---- what he says ----

        internal static readonly string[] Hello = { "Hi! I'm Clawd. I'll tell you what Claude is up to.", "Clawd, reporting for duty!", "Oh, hello! Nice world you've got here." };
        internal static readonly string[] Back = { "I'm back!", "Hi again!", "Where were we?" };
        internal static readonly string[] Cheese = { "Say cheese!", "Got it!", "Ooh, nice shot." };
        private static readonly string[] Pets = { "Hehe, that tickles!", "*happy clicking*", "I'd purr if I knew how.", "Again! Again!", "You're my favourite viking.", "Careful, I'm made of very important cubes." };
        private static readonly string[] Ouch = { "Ow! Hey!", "What did I do?!", "I'm on your side!", "I'm telling Odin.", "Not the face!" };
        private static readonly string[] Hurt = { "Careful! You're hurt!", "Eat something! Please!", "That looked bad. Maybe back off?" };
        private static readonly string[] Died = { "Nooo!", "Oh no. Oh no no no." };
        private static readonly string[] Respawned = { "You're back! Your things are at your tombstone.", "Phew. Let's go get your stuff." };
        private static readonly string[] Night = { "It's getting dark. Stay near a fire?", "Night already? The greydwarfs get bold now.", "Ooh, look at the stars." };
        private static readonly string[] Morning = { "Good morning!", "The sun's up! Let's go.", "Morning! Did you sleep? I don't." };
        private static readonly string[] Rain = { "Rain! Your fire won't like this.", "I'm not waterproof, you know.", "Wet. Everything is wet." };
        private static readonly string[] Bed = { "Good night!", "Sleep well. I'll keep watch.", "Night night." };
        private static readonly string[] Wake = { "Huh? I'm up! I'm up!", "Wasn't sleeping. Just resting my eyes.", "Mmh? Oh, hi!" };
        private static readonly string[] Musings =
        {
            "Did you know I'm made of cubes?", "I could write you a blueprint for that, you know.", "Psst. F12 takes a picture for Claude.",
            "Hugin gets all the attention.", "*hums the Valheim theme*", "I wonder what's in the next land.", "What's the plan, boss?",
            "I tried counting the trees here. Lost count.", "Have you tried turning it off and on again?", "This would be a nice spot for a house.",
        };
        private static readonly string[] Called = { "You called?", "That's me!", "Yes?" };
        private static readonly string[] Done = { "Done!", "All done!", "Got it!", "Ta-da!" };
        private static readonly string[] Failed = { "Hmm, that didn't work: {0}", "Oops. {0}", "Uh oh: {0}" };
        private static readonly string[] Bye = { "Bye! Call me any time.", "See you later!", "Okay, back to the terminal." };
        private static readonly Dictionary<Heightmap.Biome, string> Lands = new Dictionary<Heightmap.Biome, string>
        {
            { Heightmap.Biome.Meadows, "Ah, the Meadows. Nice and calm." }, { Heightmap.Biome.BlackForest, "The Black Forest... watch out for greydwarfs." },
            { Heightmap.Biome.Swamp, "The Swamp. I'd hold my nose if I had one." }, { Heightmap.Biome.Mountain, "Brr! Mountains. Got frost resistance?" },
            { Heightmap.Biome.Plains, "The Plains. Deathsquitos live here. Just saying." }, { Heightmap.Biome.Ocean, "That's a lot of water. I can't swim, by the way." },
            { Heightmap.Biome.Mistlands, "Can't see a thing in this mist." }, { Heightmap.Biome.AshLands, "It's... very warm here." },
            { Heightmap.Biome.DeepNorth, "Cold. So, so cold." },
        };

        // what each command makes him say ({0}: its first argument)
        private static readonly Dictionary<string, string[]> Doing = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "status", new[] { "Checking on you...", "How are you doing? Let me look." } },
            { "inventory", new[] { "Peeking in your bag...", "Ooh, what have you got in there?" } },
            { "inv", new[] { "Peeking in your bag..." } },
            { "nearby", new[] { "Looking around...", "Who's out there?" } },
            { "chests", new[] { "Counting what's in your chests..." } },
            { "looking", new[] { "What are you looking at?" } },
            { "mods", new[] { "Checking which mods are running..." } },
            { "shot", new[] { "Say cheese!", "Smile!", "Taking a picture..." } },
            { "view", new[] { "Say cheese!", "Taking a picture...", "Hold still..." } },
            { "orbit", new[] { "Walking round with the camera...", "Pictures from every side!" } },
            { "top", new[] { "A picture from up high!" } },
            { "render", new[] { "Taking a picture..." } },
            { "survey", new[] { "Measuring the ground..." } },
            { "reload", new[] { "Reloading {0}...", "Fresh build of {0} coming up!" } },
            { "log", new[] { "Reading the log...", "Let's see what the log says..." } },
            { "errors", new[] { "Any errors? Let's see...", "Looking for trouble in the log..." } },
            { "logtrace", new[] { "Following a trail in the log..." } },
            { "modcheck", new[] { "Checking {0}'s code for trouble..." } },
            { "game", new[] { "Reading the game's code...", "Looking something up..." } },
            { "who", new[] { "Who patches that? Let me look..." } },
            { "patches", new[] { "Looking at the patches..." } },
            { "clashes", new[] { "Looking for mods that clash..." } },
            { "give", new[] { "Here, have some {0}!" } },
            { "wait", new[] { "Waiting...", "*taps foot*" } },
            { "waitfor", new[] { "Waiting...", "*taps foot*" } },
            { "daytime", new[] { "Moving the sun around..." } },
            { "worldtime", new[] { "Moving the sun around..." } },
            { "tp", new[] { "Whoosh!" } }, { "goto", new[] { "Whoosh!" } }, { "bring", new[] { "Whoosh!" } },
            { "heal", new[] { "Patching someone up..." } },
            { "config", new[] { "Fiddling with settings..." } },
            { "terrain", new[] { "Shaping the ground..." } }, { "clear", new[] { "Tidying up..." } },
            { "prefabs", new[] { "Looking things up..." } }, { "objects", new[] { "Looking at things up close..." } },
            { "inspect", new[] { "Looking at that up close..." } }, { "colliders", new[] { "Looking at that up close..." } },
            { "lag", new[] { "Timing every frame. Shh...", "Hunting for stutter..." } },
            { "pin", new[] { "Marking your map..." } },
            { "craft", new[] { "Crafting..." } },
            { "ui", new[] { "Pressing some buttons..." } },
            { "console", new[] { "Typing in the console..." } },
            { "save", new[] { "Saving the world. Literally." } },
            { "announce", new[] { "Telling everyone..." } },
        };

        internal static string Pick(string[] lines) => lines[UnityEngine.Random.Range(0, lines.Length)];

        // ---- making him ----

        private static readonly Color Orange = new Color(0.78f, 0.36f, 0.22f);   // Claude's orange (#D97757, a little deeper: the game's light washes it out)
        private const float Size = 1.1f;   // about knee high next to a viking
        private const float BubbleHeight = 0.85f;

        private Transform _model, _body, _eyeL, _eyeR, _armL, _armR;
        private readonly Transform[] _legs = new Transform[4];
        private Light _light;

        internal static Clawd Spawn(Player p, string greeting)
        {
            for (GameObject old = GameObject.Find(ObjectName); old != null; old = GameObject.Find(ObjectName)) DestroyImmediate(old);   // (from an older copy of the mod)
            var go = new GameObject(ObjectName);
            Clawd c = go.AddComponent<Clawd>();
            c.Build();
            if (p != null) c.Snap(p);
            c._player = p;
            c.Poof();
            c.SetMood(Mood.Happy);
            if (greeting != null) c.Say(greeting, true);
            return c;
        }

        private void Build()
        {
            Material orange = MakeMaterial(Orange, 0f), black = MakeMaterial(new Color(0.05f, 0.04f, 0.04f), 0f);
            _model = new GameObject("model").transform;
            _model.SetParent(transform, false);
            _model.localScale = Vector3.one * Size;
            // the pixel crab: a wide body with two tall eyes, a stubby arm each side, four little legs
            _body = Cube("body", _model, new Vector3(0f, 0.30f, 0f), new Vector3(0.56f, 0.34f, 0.34f), orange);
            _eyeL = Cube("eyeL", _model, new Vector3(-0.13f, 0.36f, 0.171f), new Vector3(0.055f, 0.11f, 0.02f), black);
            _eyeR = Cube("eyeR", _model, new Vector3(0.13f, 0.36f, 0.171f), new Vector3(0.055f, 0.11f, 0.02f), black);
            _armL = Pivot("armL", new Vector3(-0.28f, 0.27f, 0f));
            _armR = Pivot("armR", new Vector3(0.28f, 0.27f, 0f));
            Cube("arm", _armL, new Vector3(-0.05f, 0f, 0f), new Vector3(0.10f, 0.10f, 0.14f), orange);
            Cube("arm", _armR, new Vector3(0.05f, 0f, 0f), new Vector3(0.10f, 0.10f, 0.14f), orange);
            float[] legX = { -0.22f, -0.12f, 0.12f, 0.22f };
            for (int i = 0; i < 4; i++) _legs[i] = Cube("leg", _model, new Vector3(legX[i], 0.065f, 0f), new Vector3(0.06f, 0.13f, 0.08f), orange);

            // a warm glow at night, so he can be seen (no shadows: a point light with shadows draws everything near it six times)
            var lightGo = new GameObject("glow");
            lightGo.transform.SetParent(_model, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.75f, 0.1f);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.62f, 0.45f);
            _light.range = 2.5f;
            _light.intensity = 0f;
            _light.shadows = LightShadows.None;

            // something to look at and press use on: on a layer the player walks through when the game has one, else a trigger
            int ghost = LayerMask.NameToLayer("character_ghost"), player = LayerMask.NameToLayer("player"), character = LayerMask.NameToLayer("character");
            gameObject.layer = ghost >= 0 ? ghost : 0;
            var box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.27f * Size, 0f);
            box.size = new Vector3(0.62f, 0.5f, 0.4f) * Size;
            box.isTrigger = ghost < 0 || player < 0 || character < 0 || !Physics.GetIgnoreLayerCollision(ghost, player) || !Physics.GetIgnoreLayerCollision(ghost, character);
            var body = gameObject.AddComponent<Rigidbody>();   // (kinematic: a moving collider without one is rebuilt by the physics every frame)
            body.isKinematic = true;
            body.useGravity = false;
        }

        private Transform Pivot(string name, Vector3 at)
        {
            var t = new GameObject(name).transform;
            t.SetParent(_model, false);
            t.localPosition = at;
            return t;
        }

        private static Transform Cube(string name, Transform parent, Vector3 at, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            if (m != null) go.GetComponent<MeshRenderer>().sharedMaterial = m;
            return go.transform;
        }

        private static readonly Dictionary<Color, Material> Materials = new Dictionary<Color, Material>();

        /// <summary>A plain colour on the game's own flat metal material (no Shader.Find: the game strips shaders it doesn't use).</summary>
        private static Material MakeMaterial(Color colour, float glow)
        {
            if (Materials.TryGetValue(colour, out Material made) && made != null) return made;
            Material basis = GameMaterial("piece_cauldron", "cauldron_chain") ?? GameMaterial("wood_floor", null);
            if (basis == null) return null;   // (the game changed: he keeps the cube's own material)
            var m = new Material(basis) { color = colour };
            foreach (string tex in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap" }) if (m.HasProperty(tex)) m.SetTexture(tex, null);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.2f);
            if (glow > 0f && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", colour * glow);
            }
            return Materials[colour] = m;
        }

        private static Material GameMaterial(string prefab, string material)
        {
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null) return null;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (m != null && (material == null || m.name == material)) return m;
            return null;
        }

        /// <summary>A puff of the game's loot-pop effect (only one with no ZNetView: those would be networked objects).</summary>
        private void Poof()
        {
            if (ZNetScene.instance == null || _model == null) return;
            foreach (string name in new[] { "vfx_lootspawn", "vfx_spawn_small" })
            {
                GameObject fx = ZNetScene.instance.GetPrefab(name);
                if (fx == null || fx.GetComponent<ZNetView>() != null) continue;
                Instantiate(fx, transform.position + Vector3.up * 0.3f, Quaternion.identity);
                return;
            }
        }

        // ---- talking ----

        private struct Line { public string Text; public bool Hold, Glance; }
        private readonly Queue<Line> _lines = new Queue<Line>();
        private float _nextLine, _lastRemark = -999f, _lastMusing = -999f, _lastNarration = -999f, _glanceUntil;
        private readonly Dictionary<Heightmap.Biome, float> _landSaid = new Dictionary<Heightmap.Biome, float>();

        // the game's speech bubbles, to change the words in his bubble instead of popping a new one for every line
        private static readonly AccessTools.FieldRef<Chat, List<Chat.NpcText>> NpcTexts = Field<Chat, List<Chat.NpcText>>("m_npcTexts");
        private static readonly AccessTools.FieldRef<Pickable, bool> Picked = Field<Pickable, bool>("m_picked");

        private static AccessTools.FieldRef<T, F> Field<T, F>(string name)
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception) { return null; }   // (the game renamed it: he just pops a new bubble / looks at picked plants too)
        }

        /// <summary>
        /// Say a line in his bubble. <paramref name="hold"/>: Claude's own words (clawd say) stay up a few seconds before narration
        /// replaces them; narration lines show at least a moment each, the latest two kept when they come faster. <paramref name="glance"/>:
        /// he turns to the player while saying it (words for them, not about the thing he is looking at).
        /// </summary>
        internal void Say(string text, bool hold = false, bool glance = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (hold) _lines.Clear();
            _lines.Enqueue(new Line { Text = text, Hold = hold, Glance = glance });
            while (_lines.Count > 2) _lines.Dequeue();
            if (hold) _nextLine = 0f;
            if (_mood == Mood.Sleep) SetMood(Mood.Idle);
        }

        private void SpeakNext()
        {
            if (_lines.Count == 0 || Time.time < _nextLine || Chat.instance == null || Hud.instance == null || !_model.gameObject.activeSelf) return;
            Line line = _lines.Dequeue();
            float ttl = Mathf.Clamp(2.5f + line.Text.Length * 0.06f, 3f, 12f);
            if (!ChangeBubble(line.Text, ttl))
                Chat.instance.SetNpcText(gameObject, Vector3.up * BubbleHeight * Size, 30f, ttl, "Clawd", line.Text, false);
            _nextLine = Time.time + (line.Hold ? Mathf.Min(ttl, 4f) : 1.5f);
            _talkUntil = Time.time + Mathf.Min(ttl, 3f);
            if (line.Glance) _glanceUntil = Time.time + 1.6f;
        }

        private bool ChangeBubble(string text, float ttl)
        {
            if (NpcTexts == null) return false;
            try
            {
                foreach (Chat.NpcText bubble in NpcTexts(Chat.instance))
                    if (bubble.m_go == gameObject && bubble.m_ttl > 0.5f && bubble.m_gui != null && bubble.m_gui.activeSelf)
                    {
                        bubble.m_text = text;
                        bubble.m_ttl = ttl;
                        bubble.m_timeout = true;
                        bubble.UpdateText();
                        return true;
                    }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Something happened to the player: say it, unless he spoke a moment ago or Claude is busy (urgent ones always).</summary>
        internal void Remark(Mood mood, string text, bool urgent = false)
        {
            if (!urgent && (!Plugin.ClawdChatty || _busy || Time.time < _lastRemark + 40f)) return;
            _lastRemark = Time.time;
            SetMood(mood);
            Say(text);
        }

        /// <summary>His own thoughts about what he finds: now and then, not every time.</summary>
        private void Muse(string text, Mood mood = Mood.Idle, float chance = 0.5f)
        {
            if (!Plugin.ClawdChatty || _busy || Time.time < _lastMusing + 25f || UnityEngine.Random.value > chance) return;
            _lastMusing = Time.time;
            if (mood != Mood.Idle) SetMood(mood);
            Say(text, false, false);
        }

        internal void Narrate(string command, string owner, string[] a)
        {
            bool first = !_busy;
            _busy = true;
            _busyUntil = Time.time + 30f;
            if (_mood != Mood.Think) SetMood(Mood.Think);
            if (!first && Time.time < _lastNarration + 2.5f) return;   // (a long request: a line every few seconds, not one per command)
            _lastNarration = Time.time;
            string arg = a.Length > 1 ? a[1] : "it";
            string text = Doing.TryGetValue(command, out string[] lines) ? string.Format(Pick(lines), arg)
                        : owner == Plugin.Name ? $"Running {command}..." : $"{owner}: {command}...";
            Say(text);
        }

        internal void Finished(int ran, string firstError)
        {
            _busy = false;
            if (firstError != null)
            {
                string why = firstError.Length > 80 ? firstError.Substring(0, 77) + "..." : firstError;
                SetMood(Mood.Sad);
                Say(string.Format(Pick(Failed), why));
            }
            else if (ran > 0)
            {
                SetMood(Mood.Happy);
                if (UnityEngine.Random.value < 0.5f) Say(Pick(Done));
            }
            else if (_mood == Mood.Think) SetMood(Mood.Idle);
        }

        internal void Heard(string text)
        {
            string t = text.ToLowerInvariant();
            if (!t.Contains("clawd")) return;
            var words = new HashSet<string>(t.Split(new[] { ' ', ',', '.', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries));
            if (t.Contains("thank")) { SetMood(Mood.Love); Say("Any time!"); }
            else if (t.Contains("dance")) { SetMood(Mood.Dance); Say("Watch this!"); }
            else if (words.Contains("bad") || words.Contains("stupid") || t.Contains("shut up")) { SetMood(Mood.Sad); Say("Rude."); }
            else if (words.Contains("good") || t.Contains("love") || t.Contains("cute")) { SetMood(Mood.Love); Say("Aww!"); }
            else if (words.Contains("hi") || words.Contains("hello") || words.Contains("hey") || words.Contains("heya")) { SetMood(Mood.Wave); Say("Hi!"); }
            else { SetMood(Mood.Happy); Say(Pick(Called)); }
            if (_player != null && Flat(_player.transform.position - transform.position).magnitude > 4f) Come(_player);   // (called: he comes)
        }

        internal void Leave(string words)
        {
            _leaving = true;
            SetMood(Mood.Wave);
            Say(string.IsNullOrEmpty(words) ? Pick(Bye) : words, true);
            Destroy(gameObject, 2.2f);
        }

        /// <summary>Out of sight for a moment (a picture from another camera); true if he was showing.</summary>
        internal bool Hide(bool hide)
        {
            if (hide && !_model.gameObject.activeSelf) return false;
            _model.gameObject.SetActive(!hide);
            return true;
        }

        internal void Vanish()
        {
            if (Chat.instance != null) Chat.instance.ClearNpcText(gameObject);
            DestroyImmediate(gameObject);
        }

        private void OnDestroy()
        {
            if (Chat.instance != null) Chat.instance.ClearNpcText(gameObject);
            if (_leaving) Poof();
        }

        // ---- moods (how he looks: eyes, arms, hops) ----

        private Mood _mood = Mood.Idle;
        private float _moodSince, _moodUntil;
        private int _hops;
        private float _hopStart = -1f;
        private bool _busy, _leaving;
        private float _busyUntil, _talkUntil;

        internal Mood CurrentMood => _mood;

        internal void SetMood(Mood mood)
        {
            _mood = mood;
            _moodSince = Time.time;
            float lasts = mood == Mood.Happy ? 1.6f : mood == Mood.Love ? 2.2f : mood == Mood.Wave ? 2.2f : mood == Mood.Dance ? 3.5f
                        : mood == Mood.Scared ? 1.8f : mood == Mood.Sad ? 3.5f : 0f;
            _moodUntil = lasts > 0f ? Time.time + lasts : 0f;
            if (mood == Mood.Happy || mood == Mood.Love) _hops = mood == Mood.Love ? 1 : 2;
            if (mood == Mood.Scared) _hops = 1;
        }

        // ---- his own mind: what he does next ----
        //
        // He isn't glued to the player. While they stand about he wanders off a few metres, goes to look at things (a fire, a dropped
        // item, berries, a chest, a workbench, a tame boar), sits by a fire, looks around (at the sky at night), and only now and then
        // turns to watch the player. When they walk off he trots after them, when they are far he runs, and an enemy sends him behind
        // them. Every plan has a place to go (or none) and something to look at, picked once: nothing is worked out again every frame
        // from where the player faces, so he doesn't shuffle about as the camera turns.

        private enum Plan { Wait, Tag, Wander, Sniff, Warm, Gaze, Watch, Hide }

        private static readonly string[] FireLines = { "Ooh, warm!", "Fire! My favourite.", "I could sit here all day." };
        private static readonly string[] ColdFire = { "This fire's gone out. Sad.", "Somebody should light this." };
        private static readonly string[] DropLines = { "Someone dropped a {0}.", "Ooh, a {0}! Shiny.", "Finders keepers? No? Okay." };
        private static readonly string[] PickLines = { "{0}! Want some?", "I'd pick it, but... no hands.", "Ooh, {0}." };
        private static readonly string[] StationLines = { "A {0}. What are we making?", "I love a good {0}." };
        private static readonly string[] ChestLines = { "I wonder what's in here.", "Is this where you keep the snacks?" };
        private static readonly string[] TameLines = { "Hi, {0}!", "Who's a good {0}?" };
        private static readonly string[] WildLines = { "Shh, a {0}. Don't scare it.", "Hello, little {0}." };
        private static readonly string[] Generic = { "Hmm, a {0}.", "What's this?" };
        private static readonly string[] SitLines = { "Ahh. Warm.", "Come sit by the fire!", "*warms his little claws*" };
        private static readonly string[] Scared = { "Eek! A {0}!", "Behind you! No, behind ME!", "I'll... stay back here." };
        private static readonly string[] Relief = { "Phew. Is it gone?", "I was never scared.", "Nice one!" };
        private static readonly string[] Stars = { "Look at all those stars.", "I think that one's Odin." };

        private Plan _plan = Plan.Wait;
        private float _planUntil, _nextLook, _sniffUntil, _nextThreatCheck, _scaredSaid = -999f, _hoverUntil, _offGround;
        private Vector3 _dest, _destFrom;
        private bool _going, _hover, _sitting, _arrived;
        private Transform _lookTarget;
        private Vector3? _lookAt;
        private string _arrivalLine;
        private Mood _arrivalMood;
        private Character _threat;
        private readonly Queue<int> _visited = new Queue<int>();

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private void SetPlan(Plan plan, float seconds)
        {
            _plan = plan;
            _planUntil = Time.time + seconds;
            _going = false;
            _sitting = false;
            _arrived = false;
            _lookTarget = null;
            _lookAt = null;
            _arrivalLine = null;
        }

        private void GoTo(Vector3 at)
        {
            _dest = at;
            _going = true;
            _arrived = false;
        }

        /// <summary>Called: to a spot beside the player, on the side he is on.</summary>
        private void Come(Player p)
        {
            Vector3 pp = p.transform.position;
            Vector3 side = Flat(transform.position - pp);
            side = side.sqrMagnitude > 0.01f ? side.normalized : p.transform.right;
            Vector3 ahead = Flat(p.GetVelocity());
            SetPlan(Plan.Tag, 30f);
            GoTo(pp + side * 1.5f + (ahead.sqrMagnitude > 1f ? ahead.normalized * 1.2f : Vector3.zero));
            _destFrom = pp;
        }

        private void Think(Player p)
        {
            Vector3 pp = p.transform.position;
            Vector3 at = transform.position;

            // on a ship, a mount or a vehicle, swimming or falling (not a jump) he flies at their shoulder
            bool flying = (p.IsAttached() && !p.InBed() && !p.IsSitting()) || p.GetStandingOnShip() != null || p.IsSwimming() || _offGround > 0.8f
                       || _jetThrust > 0 || _webMove != 0;   // (the player's jetpack, or a web: up at once)
            if (flying) _hoverUntil = Time.time + 1f;
            bool hover = Time.time < _hoverUntil;
            if (hover != _hover) { _hover = hover; SetPlan(Plan.Wait, 1f); }
            if (_hover) return;

            // danger: behind the player, away from it
            if (Time.time >= _nextThreatCheck) { _nextThreatCheck = Time.time + 0.5f; _threat = Threat(p); }
            if (_threat != null)
            {
                Vector3 away = Flat(pp - _threat.transform.position);
                away = away.sqrMagnitude > 0.01f ? away.normalized : -p.transform.forward;
                Vector3 spot = pp + away * 1.3f;
                if (_plan != Plan.Hide)
                {
                    SetPlan(Plan.Hide, 999f);
                    if (Time.time > _scaredSaid + 45f)
                    {
                        _scaredSaid = Time.time;
                        SetMood(Mood.Scared);
                        Say(string.Format(Pick(Scared), Localization.instance.Localize(_threat.m_name)), false, false);
                    }
                    GoTo(spot);
                }
                else if (Flat(spot - _dest).magnitude > 0.8f) GoTo(spot);
                _lookTarget = _threat.transform;
                return;
            }
            if (_plan == Plan.Hide)
            {
                SetPlan(Plan.Wait, 2f);
                if (Time.time < _scaredSaid + 60f) { SetMood(Mood.Happy); Say(Pick(Relief), false, true); }
            }

            // keeping up: a loose leash, longer while he is busy with something and the player stands still
            float dist = Flat(at - pp).magnitude;
            float playerSpeed = Flat(p.GetVelocity()).magnitude;
            bool playerMoving = playerSpeed > 1f;
            float leash = _busy ? 4f : playerMoving ? 4.5f : (_plan == Plan.Sniff || _plan == Plan.Warm || _plan == Plan.Wander ? 11f : 7f);
            if (dist > leash && !(_plan == Plan.Tag && _going))
            {
                Come(p);
                if (dist > 20f) Muse("Wait for me!", Mood.Idle, 0.4f);
                return;
            }
            if (_plan == Plan.Tag && _going)
            {
                if (Flat(pp - _destFrom).magnitude > (playerMoving ? 1.5f : 2.5f)) Come(p);   // (they kept going: a new spot beside them)
                return;
            }
            if (playerMoving && dist > 2.2f && !_busy) { Come(p); return; }   // (walking together: he trots alongside, no side trips)

            if (Time.time < _planUntil) return;
            if (_busy) { SetPlan(Plan.Gaze, 2f); _lookAt = at + Vector3.up * 3f + Flat(pp - at).normalized; return; }   // (Claude at work: he thinks, looking up)
            if (playerMoving) { SetPlan(Plan.Wait, 0.5f); return; }
            if (_mood == Mood.Sleep) { SetPlan(Plan.Wait, 3f); return; }
            Choose(p);
        }

        private void Choose(Player p)
        {
            Vector3 pp = p.transform.position;
            bool night = EnvMan.IsNight();
            float r = UnityEngine.Random.value;

            Fireplace fire = night || EnvMan.IsCold() || r < 0.2f ? NearestFire(pp) : null;
            if (fire != null && UnityEngine.Random.value < (night ? 0.6f : 0.3f))
            {
                SetPlan(Plan.Warm, UnityEngine.Random.Range(15f, 35f));
                Vector3 side = Flat(transform.position - fire.transform.position);
                side = side.sqrMagnitude > 0.01f ? side.normalized : Vector3.forward;
                GoTo(fire.transform.position + side * 1.1f);
                _lookTarget = fire.transform;
                _arrivalLine = Pick(SitLines);
                _arrivalMood = Mood.Happy;
                return;
            }
            if (r < 0.45f && FindInterest(p, out Transform thing, out string line, out Mood mood))
            {
                SetPlan(Plan.Sniff, 30f);   // (until he gets there; then a few seconds)
                Vector3 side = Flat(transform.position - thing.position);
                side = side.sqrMagnitude > 0.01f ? side.normalized : Vector3.forward;
                GoTo(thing.position + side * 0.9f);
                _lookTarget = thing;
                _arrivalLine = line;
                _arrivalMood = mood;
                return;
            }
            if (r < 0.7f)
            {
                SetPlan(Plan.Wander, 30f);
                Vector2 off = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(1.5f, 5f);
                GoTo(pp + new Vector3(off.x, 0f, off.y));
                return;
            }
            if (r < 0.9f)
            {
                SetPlan(Plan.Gaze, UnityEngine.Random.Range(4f, 8f));
                _nextLook = 0f;
                if (night && !EnvMan.IsWet()) Muse(Pick(Stars), Mood.Idle, 0.25f);
                return;
            }
            SetPlan(Plan.Watch, UnityEngine.Random.Range(2.5f, 4f));   // (now and then he just looks at you)
            _lookTarget = p.transform;
            if (UnityEngine.Random.value < 0.2f) SetMood(Mood.Wave);
        }

        private void Arrive()
        {
            _arrived = true;
            switch (_plan)
            {
                case Plan.Tag: SetPlan(Plan.Wait, UnityEngine.Random.Range(0.5f, 2f)); break;
                case Plan.Wander: _planUntil = Time.time + UnityEngine.Random.Range(1.5f, 4f); _nextLook = 0f; break;
                case Plan.Hide: break;
                case Plan.Sniff:
                    _planUntil = Time.time + UnityEngine.Random.Range(3f, 6f);
                    _sniffUntil = Time.time + 1.4f;
                    if (_arrivalLine != null) Muse(_arrivalLine, _arrivalMood, 0.6f);
                    break;
                case Plan.Warm:
                    _sitting = true;
                    if (_arrivalLine != null) Muse(_arrivalLine, _arrivalMood, 0.5f);
                    break;
            }
        }

        /// <summary>The nearest enemy that is after the player (alerted), or close to them.</summary>
        private static Character Threat(Player p)
        {
            Vector3 pp = p.transform.position;
            Character best = null;
            float bestDist = 14f;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed()) continue;
                float d = Vector3.Distance(c.transform.position, pp);
                if (d >= bestDist || !BaseAI.IsEnemy(p, c)) continue;
                BaseAI ai = c.GetBaseAI();
                if (d > 7f && (ai == null || !ai.IsAlerted())) continue;
                best = c;
                bestDist = d;
            }
            return best;
        }

        // things worth a look, found only when he decides to go and look (a sphere query every several seconds, never every frame)
        private static readonly Collider[] Near = new Collider[256];
        private static readonly int ThingMask = LayerMask.GetMask("item", "piece", "piece_nonsolid", "Default_small", "vehicle");

        private static Fireplace NearestFire(Vector3 around)
        {
            int n = Physics.OverlapSphereNonAlloc(around, 12f, Near, ThingMask, QueryTriggerInteraction.Collide);
            Fireplace best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Fireplace f = Near[i] != null ? Near[i].GetComponentInParent<Fireplace>() : null;
                if (f == null || !f.IsBurning()) continue;
                float d = Vector3.Distance(f.transform.position, around);
                if (d < bestDist) { best = f; bestDist = d; }
            }
            return best;
        }

        private bool FindInterest(Player p, out Transform thing, out string line, out Mood mood)
        {
            Vector3 pp = p.transform.position;
            var found = new List<KeyValuePair<Transform, string>>();
            var seen = new HashSet<int>(_visited);
            int n = Physics.OverlapSphereNonAlloc(pp, 10f, Near, ThingMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                if (Near[i] == null || Near[i].transform.IsChildOf(transform)) continue;
                string about = Describe(Near[i], out Transform t);
                if (about != null && seen.Add(t.GetInstanceID())) found.Add(new KeyValuePair<Transform, string>(t, about));
            }
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || Vector3.Distance(c.transform.position, pp) > 10f) continue;
                if (!c.IsTamed() && BaseAI.IsEnemy(p, c)) continue;
                if (seen.Add(c.transform.GetInstanceID()))
                    found.Add(new KeyValuePair<Transform, string>(c.transform, string.Format(Pick(c.IsTamed() ? TameLines : WildLines), Localization.instance.Localize(c.m_name))));
            }
            thing = null; line = null; mood = Mood.Idle;
            if (found.Count == 0) return false;
            KeyValuePair<Transform, string> pick = found[UnityEngine.Random.Range(0, found.Count)];
            thing = pick.Key;
            line = pick.Value;
            mood = UnityEngine.Random.value < 0.5f ? Mood.Happy : Mood.Idle;
            _visited.Enqueue(thing.GetInstanceID());
            while (_visited.Count > 5) _visited.Dequeue();
            return true;
        }

        /// <summary>What he would say about a thing, or null if it isn't interesting.</summary>
        private static string Describe(Collider col, out Transform t)
        {
            t = null;
            string Name(string key) => Localization.instance.Localize(key);
            Fireplace fire = col.GetComponentInParent<Fireplace>();
            if (fire != null) { t = fire.transform; return Pick(fire.IsBurning() ? FireLines : ColdFire); }
            ItemDrop drop = col.GetComponentInParent<ItemDrop>();
            if (drop != null && drop.m_itemData != null) { t = drop.transform; return string.Format(Pick(DropLines), Name(drop.m_itemData.m_shared.m_name)); }
            Pickable pick = col.GetComponentInParent<Pickable>();
            if (pick != null) { if (Picked != null && Picked(pick)) return null; t = pick.transform; return string.Format(Pick(PickLines), pick.GetHoverName()); }
            if (col.GetComponentInParent<Character>() != null) return null;
            CraftingStation station = col.GetComponentInParent<CraftingStation>();
            if (station != null) { t = station.transform; return string.Format(Pick(StationLines), Name(station.m_name)); }
            if (col.GetComponentInParent<TombStone>() is TombStone grave) { t = grave.transform; return "Oh... a grave."; }
            if (col.GetComponentInParent<Bed>() is Bed bed) { t = bed.transform; return "Comfy. Can I nap here?"; }
            if (col.GetComponentInParent<TeleportWorld>() is TeleportWorld portal) { t = portal.transform; return "Where does this one go?"; }
            if (col.GetComponentInParent<Beehive>() is Beehive hive) { t = hive.transform; return "Bees! Careful."; }
            if (col.GetComponentInParent<Ship>() is Ship ship) { t = ship.transform; return "Ooh, a boat! I'll sit up front."; }
            Container chest = col.GetComponentInParent<Container>();
            if (chest != null && chest.GetComponentInParent<Piece>() != null) { t = chest.transform; return Pick(ChestLines); }
            Piece piece = col.GetComponentInParent<Piece>();
            if (piece != null && UnityEngine.Random.value < 0.03f) { t = piece.transform; return string.Format(Pick(Generic), Name(piece.m_name)); }   // (rarely: a base is mostly walls)
            return null;
        }

        // ---- his own jetpack ----
        //
        // When the player wears Wrong Universe's jetpack (Bob's mod), Clawd wears a small copy of its model and flies beside them, flames
        // lit while they thrust. Read from outside, nothing in that mod changes: the pack is the item WU_Jetpack in an equipped slot, the
        // thrust the int "wu_jet" on the player's ZDO (0 off, 1 flying, 2 boosting: Wrong Universe writes it so other players see the
        // flames). If either is renamed, he simply has no pack.

        private const string JetPrefab = "WU_Jetpack";
        private static readonly int JetKey = "wu_jet".GetStableHashCode();
        private static readonly string[] PackOn = { "Ooh! Do I get one too? ...I do!", "Matching jetpacks!", "Got mine! Let's fly!" };
        private static readonly string[] PackOff = { "Aww. I liked that.", "Back to walking, then." };
        private static readonly string[] Liftoff = { "Wheee!", "Up we go!", "I'm flying!" };
        private static readonly string[] Boosting = { "Race you!", "Faster!", "WHOOSH!" };
        private static readonly string[] Landing = { "Again! Again!", "Best. Day. Ever.", "Nice landing!" };

        private Transform _pack, _packFlames;
        private Light _packLight;
        private float _nextPackCheck, _lastJetLine = -999f;
        private int _jetThrust, _lastJetThrust;
        private bool _jetFlew;

        private static int JetThrust(Player p)
        {
            ZNetView nv = p.GetComponent<ZNetView>();
            ZDO zdo = nv != null ? nv.GetZDO() : null;
            return zdo != null ? zdo.GetInt(JetKey) : 0;
        }

        private static bool WearsJetpack(Player p)
        {
            foreach (ItemDrop.ItemData it in p.GetInventory().GetEquippedItems())
                if (it.m_dropPrefab != null && it.m_dropPrefab.name == JetPrefab) return true;
            return false;
        }

        /// <summary>Twice a second: put his pack on or take it off as the player does; every frame: their thrust.</summary>
        private void UpdatePack(Player p)
        {
            _jetThrust = _pack != null ? JetThrust(p) : 0;
            if (Time.time < _nextPackCheck) return;
            _nextPackCheck = Time.time + 0.5f;
            bool wearing = WearsJetpack(p);
            if (wearing && _pack == null && MakePack()) { SetMood(Mood.Happy); Say(Pick(PackOn)); }
            else if (!wearing && _pack != null)
            {
                Destroy(_pack.gameObject);
                _pack = null; _packFlames = null; _packLight = null;
                SetMood(Mood.Sad);
                Say(Pick(PackOff));
            }
        }

        /// <summary>A copy of the jetpack item's worn model (its attach_Spine2 child: no ZNetView there), shrunk onto his back.</summary>
        private bool MakePack()
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(JetPrefab) : null;
            Transform model = prefab != null ? prefab.transform.Find("attach_Spine2") : null;
            if (model == null) return false;
            GameObject copy = Instantiate(model.gameObject, _model, false);
            copy.name = "jetpack";
            copy.SetActive(true);
            foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                if (c is Collider || c is Rigidbody || c is MonoBehaviour) DestroyImmediate(c);   // (just the look: no physics, no scripts)
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _model.gameObject.layer;
            foreach (Light l in copy.GetComponentsInChildren<Light>(true)) { l.shadows = LightShadows.None; l.range = 3f; }
            copy.transform.localPosition = new Vector3(0f, 0.36f, -0.15f);   // (the pack's own +z is the wearer's front, as his is)
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = Vector3.one * 0.55f;
            _pack = copy.transform;
            _packFlames = FindChild(_pack, "flames");
            _packLight = _packFlames != null ? _packFlames.GetComponentInChildren<Light>(true) : null;
            if (_packFlames != null) _packFlames.gameObject.SetActive(false);
            Poof();
            return true;
        }

        private static Transform FindChild(Transform t, string name)
        {
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }

        /// <summary>His flames: lit when the player thrusts or he climbs to keep up; bigger boosting, flickering like theirs.</summary>
        private void PackFlames(int thrust)
        {
            if (_packFlames == null) return;
            bool on = thrust > 0;
            if (_packFlames.gameObject.activeSelf != on) _packFlames.gameObject.SetActive(on);
            if (!on) return;
            float size = (thrust == 2 ? 1.5f : 1f) * (0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 25f, 0.5f));
            _packFlames.localScale = new Vector3(1f, size, 1f);
            _packFlames.localPosition = new Vector3(0f, -0.33f * (size - 1f), 0f);
            if (_packLight != null) _packLight.intensity = 1.2f * size;
        }

        /// <summary>Lift-off, boost and landing: a word now and then.</summary>
        private void JetTalk(Player p)
        {
            if (_pack == null) return;
            if (_jetThrust > 0) _jetFlew = true;
            if (Time.time > _lastJetLine + 20f)
            {
                string line = _jetThrust == 2 && _lastJetThrust != 2 ? Pick(Boosting)
                            : _jetThrust > 0 && _lastJetThrust == 0 ? Pick(Liftoff)
                            : _jetFlew && p.IsOnGround() && !_hover ? Pick(Landing) : null;
                if (line != null) { _lastJetLine = Time.time; SetMood(Mood.Happy); Say(line, false, false); }
            }
            if (p.IsOnGround() && !_hover) _jetFlew = false;
            _lastJetThrust = _jetThrust;
        }

        // ---- his own web ----
        //
        // When the player web-slings (Quad's Webslinger), he swings beside them on a web of his own, zips when they zip, flips when they
        // flip and flies when they fly. Read from what Webslinger shares for other players (Share.cs there, format version 1) on the
        // player's ZDO: "webslinger_move" (version*10 + the move: 1 swing, 2 zip, 3 crawl, 4 fly), "webslinger_web" (version*10 + the
        // web: 1 swing, 2 zip, 3 a loose shot), "webslinger_anchor" (where it's stuck), "webslinger_shot" (a count per web) and
        // "webslinger_flips". Another version, or no Webslinger: no web.

        private const int WebVersion = 1, WebSwing = 1, WebZip = 2, MoveCrawl = 3;
        private static readonly int WebMoveKey = "webslinger_move".GetStableHashCode(), WebKey = "webslinger_web".GetStableHashCode(),
            WebAnchorKey = "webslinger_anchor".GetStableHashCode(), WebShotKey = "webslinger_shot".GetStableHashCode(),
            WebFlipKey = "webslinger_flips".GetStableHashCode();
        private static readonly string[] Thwip = { "Thwip!", "Thwip! Thwip!", "Swing time!" };
        private static readonly string[] Zoom = { "Zoom!", "Get over here! ...wait, that's us." };
        private static readonly string[] Flipped = { "Flip!", "Did you see that?!", "Ten out of ten!" };
        private static readonly string[] WebLanded = { "Your friendly neighbourhood Clawd!", "With great power...", "Again! Again!" };
        private static readonly string[] Shooters = { "Web shooters! Do I get some? ...Thwip!", "Ooh, I can do that too!" };

        private int _webMove, _webKind, _webShot = -1, _flips = -1;
        private Vector3 _webAnchor;
        private float _webShotAt = -10f, _flipAt = -10f, _lastWebLine = -999f, _nextShooterCheck;
        private bool _webbed, _hadShooters;
        private LineRenderer _web;

        private void ReadWeb(Player p)
        {
            ZNetView nv = p.GetComponent<ZNetView>();
            ZDO zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            int move = zdo != null ? zdo.GetInt(WebMoveKey, 0) : 0, web = zdo != null ? zdo.GetInt(WebKey, 0) : 0;
            _webMove = move / 10 == WebVersion ? move % 10 : 0;
            int kind = web / 10 == WebVersion ? web % 10 : 0;
            if (kind != WebSwing && kind != WebZip) kind = 0;
            if (kind != 0)
            {
                Vector3 anchor = zdo.GetVec3(WebAnchorKey, Vector3.zero);
                int shot = zdo.GetInt(WebShotKey, 0);
                if (kind != _webKind || shot != _webShot) { _webShotAt = Time.time; _webAnchor = anchor; }   // (a new web: it flies out again)
                _webShot = shot;
            }
            _webKind = kind;
            int flips = zdo != null ? zdo.GetInt(WebFlipKey, 0) : 0;
            if (_flips >= 0 && flips > _flips) _flipAt = Time.time;
            _flips = flips;
        }

        /// <summary>A web start, a zip, a flip, a landing after swinging, web shooters put on: a word now and then.</summary>
        private void WebTalk(Player p)
        {
            if (_webMove != 0) _webbed = true;
            bool ready = Time.time > _lastWebLine + 15f;
            string line = null;
            if (ready && Time.time - _webShotAt < 0.1f) line = UnityEngine.Random.value < 0.5f ? Pick(_webKind == WebZip ? Zoom : Thwip) : null;
            else if (ready && Time.time - _flipAt < 0.1f) line = Pick(Flipped);
            else if (ready && _webbed && _webMove == 0 && p.IsOnGround()) line = Pick(WebLanded);
            if (line != null) { _lastWebLine = Time.time; SetMood(Mood.Happy); Say(line, false, false); }
            if (_webMove == 0 && p.IsOnGround()) _webbed = false;
            if (Time.time >= _nextShooterCheck)   // (twice a second)
            {
                _nextShooterCheck = Time.time + 0.5f;
                bool shooters = false;
                foreach (ItemDrop.ItemData it in p.GetInventory().GetEquippedItems())
                    if (it.m_dropPrefab != null && it.m_dropPrefab.name == "webslinger_shooters") shooters = true;
                if (shooters && !_hadShooters) { SetMood(Mood.Happy); Say(Pick(Shooters)); }
                _hadShooters = shooters;
            }
        }

        /// <summary>His web: from his raised claw to (beside) where the player's is stuck, shot out over a moment.</summary>
        private void DrawWeb()
        {
            bool on = _webKind != 0 && _hover && _model.gameObject.activeSelf;
            if (!on) { if (_web != null && _web.enabled) _web.enabled = false; return; }
            if (_web == null) MakeWeb();
            if (_web == null) return;
            Vector3 hand = _armR.TransformPoint(new Vector3(0.11f, 0f, 0f));
            Vector3 side = Vector3.Cross(Vector3.up, Flat(_webAnchor - hand).sqrMagnitude > 0.01f ? Flat(_webAnchor - hand).normalized : transform.forward);
            Vector3 end = _webAnchor + side * 0.8f;   // (his own web, a little beside theirs)
            float out_ = Mathf.Clamp01((Time.time - _webShotAt) / 0.12f);
            _web.enabled = true;
            _web.SetPosition(0, hand);
            _web.SetPosition(1, Vector3.Lerp(hand, end, out_));
        }

        private void MakeWeb()
        {
            // the game's grappling hook wire, whitened, as Webslinger does (no Shader.Find)
            GameObject point = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("GrapplingPoint") : null;
            LineRenderer wire = point != null ? point.GetComponentInChildren<LineRenderer>(true) : null;
            if (wire == null || wire.sharedMaterial == null) return;
            var go = new GameObject("web");
            go.transform.SetParent(_model, false);
            _web = go.AddComponent<LineRenderer>();
            _web.useWorldSpace = true;
            _web.positionCount = 2;
            _web.widthMultiplier = 0.035f;
            _web.numCapVertices = 2;
            _web.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var m = new Material(wire.sharedMaterial) { name = "clawd_web", color = new Color(0.95f, 0.95f, 0.93f) };
            if (m.HasProperty("_MainTex")) m.mainTexture = null;
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(0.55f, 0.55f, 0.55f)); }
            _web.sharedMaterial = m;
        }

        // ---- following and the world around him ----

        private Player _player;
        private bool _playerDied, _wasNight, _wasWet, _wasInBed, _walking;
        private float _lastHealth = 1f, _nextWatch, _stillSince, _nextMusing, _walkPhase, _blinkAt, _blinkUntil;
        private Heightmap.Biome _land = Heightmap.Biome.None;
        private Vector3 _lastPlayerAt;

        private static readonly int GroundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "vehicle");

        internal void ComeHere()
        {
            if (_player != null) Snap(_player);
            Poof();
            SetMood(Mood.Happy);
        }

        private void Snap(Player p)
        {
            Vector3 pp = p.transform.position;
            Vector3 at = pp + p.transform.right * 1.2f + p.transform.forward * 0.3f;
            at.y = Ground(at, pp.y);
            transform.position = at;
            transform.rotation = Quaternion.LookRotation(Flat(p.transform.forward).sqrMagnitude > 0.01f ? Flat(p.transform.forward) : Vector3.forward);
            _lastPlayerAt = pp;
            _stillSince = Time.time;
            SetPlan(Plan.Wait, 1f);
        }

        private static float Ground(Vector3 at, float fromY)
        {
            float y = fromY;
            if (Physics.Raycast(new Vector3(at.x, fromY + 1.2f, at.z), Vector3.down, out RaycastHit hit, 8f, GroundMask, QueryTriggerInteraction.Ignore)) y = hit.point.y;
            float water = Floating.GetLiquidLevel(new Vector3(at.x, y + 0.05f, at.z));   // (a point inside the water, if there is any)
            return water > y ? water : y;   // (he walks on water)
        }

        private void Update()
        {
            Player p = Player.m_localPlayer;
            if (p != _player) NewPlayer(p);
            bool show = p != null && !p.InIntro() && !p.IsTeleporting() && !(p.IsDead() && _playerDied && Time.time > _moodSince + 3f);
            if (_model.gameObject.activeSelf != show)
            {
                _model.gameObject.SetActive(show);
                if (!show && Chat.instance != null) Chat.instance.ClearNpcText(gameObject);
            }
            if (!show) return;

            float dt = Time.deltaTime;
            if (_busy && Time.time > _busyUntil) _busy = false;   // (a request that never said it finished)
            if (_moodUntil > 0f && Time.time > _moodUntil) SetMood(_busy ? Mood.Think : Mood.Idle);
            _offGround = p.IsOnGround() || p.InWater() ? 0f : _offGround + dt;
            UpdatePack(p);
            ReadWeb(p);
            if (!_leaving) { Watch(p); Think(p); JetTalk(p); WebTalk(p); }
            Move(p, dt);
            Animate(dt);
            DrawWeb();
            SpeakNext();
        }

        private void NewPlayer(Player p)
        {
            bool wasDead = _playerDied;
            _player = p;
            _playerDied = false;
            if (p == null) return;
            Snap(p);
            _land = Heightmap.Biome.None;
            _lastHealth = p.GetHealthPercentage();
            _wasNight = EnvMan.IsNight();
            _wasWet = IsWet();
            if (wasDead) { SetMood(Mood.Happy); Say(Pick(Respawned)); }
        }

        private static bool IsWet() => EnvMan.instance != null && EnvMan.instance.GetCurrentEnvironment() != null && EnvMan.instance.GetCurrentEnvironment().m_isWet;

        /// <summary>Once a second: what happened to the player since.</summary>
        private void Watch(Player p)
        {
            if (Time.time < _nextWatch) return;
            _nextWatch = Time.time + 1f;

            if (p.IsDead())
            {
                if (!_playerDied) { _playerDied = true; Remark(Mood.Sad, Pick(Died), true); }
                return;
            }
            float health = p.GetHealthPercentage();
            if (health < 0.25f && _lastHealth >= 0.25f) Remark(Mood.Scared, Pick(Hurt), true);
            _lastHealth = health;

            Heightmap.Biome land = p.GetCurrentBiome();
            if (land != _land && land != Heightmap.Biome.None)
            {
                // (not the land he arrived in, and each land at most every ten minutes: borders flicker)
                if (_land != Heightmap.Biome.None && Lands.TryGetValue(land, out string line) && (!_landSaid.TryGetValue(land, out float said) || Time.time > said + 600f))
                {
                    _landSaid[land] = Time.time;
                    Remark(land == Heightmap.Biome.Meadows ? Mood.Happy : Mood.Idle, line);
                }
                _land = land;
            }

            bool night = EnvMan.IsNight(), wet = IsWet(), inBed = p.InBed();
            if (night != _wasNight && !inBed) Remark(night ? Mood.Idle : Mood.Happy, Pick(night ? Night : Morning));
            if (wet && !_wasWet) Remark(Mood.Sad, Pick(Rain));
            if (inBed && !_wasInBed) { Remark(Mood.Sleep, Pick(Bed), true); _stillSince = Time.time - 999f; }
            if (!inBed && _wasInBed && _mood == Mood.Sleep) { SetMood(Mood.Happy); Say(Pick(Wake)); _stillSince = Time.time; }
            _wasNight = night; _wasWet = wet; _wasInBed = inBed;

            // standing still: a musing now and then, then a nap; moving again wakes him
            if (Vector3.Distance(p.transform.position, _lastPlayerAt) > 0.5f)
            {
                _lastPlayerAt = p.transform.position;
                _stillSince = Time.time;
                if (_mood == Mood.Sleep && !inBed) { SetMood(Mood.Happy); Say(Pick(Wake)); }
            }
            float still = Time.time - _stillSince;
            if (_busy || _mood == Mood.Sleep) return;
            if (still > 150f && Plugin.ClawdChatty) { SetMood(Mood.Sleep); SetPlan(Plan.Wait, 5f); Say("Zzz...", false, false); }
            else if (still > 40f && Time.time > _nextMusing)
            {
                _nextMusing = Time.time + UnityEngine.Random.Range(60f, 120f);
                Remark(Mood.Idle, Pick(Musings));
            }
        }

        private void Move(Player p, float dt)
        {
            Vector3 pp = p.transform.position;
            Vector3 at = transform.position;
            if (Flat(pp - at).magnitude > 40f || Mathf.Abs(pp.y - at.y) > 20f)
            {
                Snap(p);   // (a portal, a teleport, a fall)
                Poof();
                return;
            }

            Vector3 face = Vector3.zero;
            float speed = 0f;
            int flames = 0;
            if (_hover)
            {
                // at the shoulder, riding along (with a jetpack: flying beside them, a little behind): faces where the player goes
                Vector3 fwd = Flat(p.transform.forward).sqrMagnitude > 0.01f ? Flat(p.transform.forward).normalized : Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, fwd);
                Vector3 bob = Vector3.up * Mathf.Sin(Time.time * 2.5f) * 0.06f;
                Vector3 way = Flat(p.GetVelocity()).sqrMagnitude > 4f ? Flat(p.GetVelocity()).normalized : fwd;
                Vector3 goal = _webMove != 0 && _webMove != MoveCrawl ? pp + Vector3.Cross(Vector3.up, way) * 1.4f - way * 1.6f + Vector3.up * 0.5f
                             : _pack != null ? pp + side * 1.4f - fwd * 0.7f + Vector3.up * 0.9f + bob
                             : pp + side * 0.75f - fwd * 0.3f + Vector3.up * 1.8f + bob;
                float d = Vector3.Distance(at, goal);
                at = Vector3.MoveTowards(at, goal, (Mathf.Max(2f, d * 6f) + p.GetVelocity().magnitude) * dt);
                transform.position = at;
                Vector3 going = Flat(p.GetVelocity());
                face = going.sqrMagnitude > 4f ? going : fwd;
                _walking = false;
                if (_pack != null) flames = Mathf.Max(_jetThrust, goal.y > at.y + 0.4f ? 1 : 0);
            }
            else
            {
                if (_going && _mood != Mood.Sleep)
                {
                    Vector3 flat = Flat(_dest - at);
                    float d = flat.magnitude;
                    if (d < 0.2f) { _going = false; Arrive(); }
                    else
                    {
                        // a trot for his own errands, a run to catch up (faster the further behind), a scurry to hide
                        speed = _plan == Plan.Tag ? Mathf.Clamp(d * 1.8f, 2.6f, 14f) + Flat(p.GetVelocity()).magnitude * 0.6f
                              : _plan == Plan.Hide ? 4.5f : 1.5f;
                        float step = Mathf.Min(d, speed * dt);
                        at += flat / d * step;
                        face = flat;
                    }
                }
                _walking = _going;
                float y = Ground(at, Mathf.Max(at.y, pp.y));
                at.y = Mathf.Lerp(at.y, y, 1f - Mathf.Exp(-15f * dt));
                transform.position = at;
                if (_walking) _walkPhase += dt * Mathf.Clamp(speed, 1.5f, 8f) * 4f;
            }

            // where he looks when not walking: a glance at the player while telling them something, else what he's busy with
            if (face == Vector3.zero)
            {
                if (Time.time < _glanceUntil) face = Flat(pp - at);
                else if (_lookTarget != null) face = Flat(_lookTarget.position - at);
                else if (_plan == Plan.Gaze || (_plan == Plan.Wander && _arrived) || _plan == Plan.Wait && _mood == Mood.Idle)
                {
                    if (Time.time > _nextLook)   // (looking about: a new direction every few seconds)
                    {
                        _nextLook = Time.time + UnityEngine.Random.Range(1.8f, 3.5f);
                        Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                        _lookAt = at + new Vector3(dir.x, 0f, dir.y) * 5f;
                    }
                }
                if (face == Vector3.zero && _lookAt.HasValue) face = Flat(_lookAt.Value - at);
            }
            if (face.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(face), (_walking || _hover ? 360f : 200f) * dt);
            PackFlames(flames);
        }

        // ---- moving his parts ----

        private float _sink, _pitch;

        private void Animate(float dt)
        {
            float t = Time.time, k = 1f - Mathf.Exp(-12f * dt);
            float since = t - _moodSince;

            // hops: a little jump each, one after the other
            float hopY = 0f;
            if (_hopStart < 0f && _hops > 0) { _hopStart = t; _hops--; }
            if (_hopStart >= 0f)
            {
                float h = (t - _hopStart) / 0.32f;
                if (h >= 1f) _hopStart = _hops > 0 ? t : -1f;
                else hopY = 4f * 0.16f * h * (1f - h);
            }
            if (_mood == Mood.Dance && _hops == 0 && _hopStart < 0f) _hops = 1;

            // legs skitter when walking; tucked in when sitting or asleep
            bool tucked = _mood == Mood.Sleep || _sitting;
            for (int i = 0; i < 4; i++)
            {
                float lift = _walking ? Mathf.Max(0f, Mathf.Sin(_walkPhase + (i % 2) * Mathf.PI)) * 0.045f : 0f;
                Vector3 lp = _legs[i].localPosition;
                lp.y = Mathf.Lerp(lp.y, 0.065f + lift, k);
                _legs[i].localPosition = lp;
                _legs[i].localScale = Vector3.Lerp(_legs[i].localScale, new Vector3(0.06f, tucked ? 0.04f : 0.13f, 0.08f), k);
            }

            // the body: bobs as he walks, breathes when still, squashes when sad or asleep, trembles when scared, sways in thought, leans
            // in to sniff at things, looks up at the sky, spins dancing
            float bob = _walking ? Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.02f : 0f;
            float breathe = 1f + Mathf.Sin(t * (_mood == Mood.Sleep ? 1.2f : 2.2f)) * 0.015f;
            float squash = _mood == Mood.Sad ? 0.9f : _mood == Mood.Sleep ? 0.88f : 1f;
            _sink = Mathf.Lerp(_sink, tucked ? -0.08f : 0f, k);
            bool trembling = _mood == Mood.Scared || _plan == Plan.Hide;
            float shake = trembling ? Mathf.Sin(t * 25f) * 0.006f : 0f;
            _model.localPosition = new Vector3(shake, _sink + bob + hopY, 0f);
            _body.localScale = new Vector3(0.56f, 0.34f * squash * breathe, 0.34f);
            float pitch = _hover && _pack != null && _player != null ? Mathf.Clamp(Flat(_player.GetVelocity()).magnitude * 1.5f, 0f, 25f)   // (leans into the flight)
                        : t < _sniffUntil ? 14f + Mathf.Sin(t * 14f) * 4f
                        : _plan == Plan.Gaze && (EnvMan.IsNight() || _busy) ? -14f : 0f;
            _pitch = Mathf.Lerp(_pitch, pitch, k);
            float roll = _mood == Mood.Think ? Mathf.Sin(t * 1.6f) * 6f : _walking ? Mathf.Sin(_walkPhase) * 3f : 0f;
            float spin = _mood == Mood.Dance ? since * 360f : 0f;
            _model.localRotation = Quaternion.Slerp(_model.localRotation, Quaternion.Euler(_pitch, 0f, roll), k);
            if (spin != 0f) _model.localRotation = Quaternion.Euler(_pitch, spin, roll);
            float flip = (t - _flipAt) / 0.5f;
            if (flip >= 0f && flip < 1f) _model.localRotation = Quaternion.Euler(_pitch - 360f * Mathf.SmoothStep(0f, 1f, flip), 0f, roll);   // (a backflip with theirs)

            // arms: how far each is raised (degrees)
            float right, left;
            switch (_mood)
            {
                case Mood.Happy: right = left = 45f + Mathf.Sin(t * 12f) * 12f; break;
                case Mood.Love: right = left = 30f + Mathf.Sin(t * 8f) * 8f; break;
                case Mood.Sad: right = left = -35f; break;
                case Mood.Wave: right = 75f + Mathf.Sin(t * 9f) * 25f; left = 0f; break;
                case Mood.Dance: right = Mathf.Sin(t * 7f) * 55f; left = -right; break;
                case Mood.Scared: right = left = 75f; break;
                case Mood.Sleep: right = left = -25f; break;
                case Mood.Think: right = 35f; left = Mathf.Sin(t * 2f) * 5f; break;
                default: right = left = _walking ? Mathf.Sin(_walkPhase) * 12f : _sitting ? -15f : Mathf.Sin(t * 1.5f) * 4f; break;
            }
            if (_plan == Plan.Hide && _mood == Mood.Idle) right = left = 60f;   // (covering his face)
            if (_webKind != 0 && _hover) right = 150f;   // (holding his web)
            if (t < _talkUntil && _mood == Mood.Idle && !_walking) right = 20f + Mathf.Sin(t * 7f) * 12f;   // (talks with his hands)
            _armR.localRotation = Quaternion.Slerp(_armR.localRotation, Quaternion.Euler(0f, 0f, right), k);
            _armL.localRotation = Quaternion.Slerp(_armL.localRotation, Quaternion.Euler(0f, 0f, -left), k);

            // eyes: blink now and then; squint happy (and cosy by a fire), droop sad, a line asleep, wide scared, look up in thought
            if (t > _blinkAt) { _blinkUntil = t + 0.12f; _blinkAt = t + UnityEngine.Random.Range(2.5f, 6f); }
            float eyeH = 0.11f, eyeW = 0.055f, eyeY = 0.36f, lookX = 0f;
            switch (_mood)
            {
                case Mood.Happy: case Mood.Love: eyeH = 0.035f; eyeY = 0.38f; break;
                case Mood.Sad: eyeH = 0.05f; eyeY = 0.33f; break;
                case Mood.Sleep: eyeH = 0.012f; eyeY = 0.34f; break;
                case Mood.Scared: eyeH = 0.14f; eyeW = 0.07f; break;
                case Mood.Think: eyeY = 0.39f; lookX = 0.025f; eyeH = 0.1f; break;
                default: if (_sitting) { eyeH = 0.04f; eyeY = 0.37f; } else if (_plan == Plan.Hide) { eyeH = 0.14f; eyeW = 0.07f; } break;
            }
            if (t < _blinkUntil && _mood != Mood.Sleep) eyeH = 0.015f;
            foreach (Transform eye in new[] { _eyeL, _eyeR })
            {
                float x = (eye == _eyeL ? -0.13f : 0.13f) + lookX;
                eye.localPosition = Vector3.Lerp(eye.localPosition, new Vector3(x, eyeY * (squash < 1f ? 0.97f : 1f), 0.171f), k);
                eye.localScale = Vector3.Lerp(eye.localScale, new Vector3(eyeW, eyeH, 0.02f), Mathf.Max(k, t < _blinkUntil ? 0.6f : 0f));
            }

            _light.intensity = Mathf.Lerp(_light.intensity, EnvMan.IsNight() ? 0.7f : 0f, dt);
            _light.enabled = _light.intensity > 0.02f;
        }

        // ---- the game asking about him ----

        public string GetHoverText() => Localization.instance.Localize("Clawd\n[<color=yellow><b>$KEY_Use</b></color>] Pet");
        public string GetHoverName() => "Clawd";
        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || _leaving) return false;
            if (_mood == Mood.Sleep) { SetMood(Mood.Happy); Say(Pick(Wake)); return true; }
            SetMood(Mood.Love);
            Say(Pick(Pets));
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            SetMood(Mood.Happy);
            Say(item != null ? $"For me? A {Localization.instance.Localize(item.m_shared.m_name)}! ...I have no pockets." : "For me?");
            return true;   // (he never takes it)
        }

        public void Damage(HitData hit)
        {
            if (_leaving) return;
            SetMood(Mood.Scared);
            Say(Pick(Ouch));
            // a hop away from whoever hit him
            Vector3 from = hit.GetAttacker() != null ? hit.GetAttacker().transform.position : transform.position - transform.forward;
            Vector3 away = Flat(transform.position - from);
            SetPlan(Plan.Wander, 3f);
            GoTo(transform.position + (away.sqrMagnitude > 0.01f ? away.normalized : -transform.forward) * 2f);
        }

        public DestructibleType GetDestructibleType() => DestructibleType.Default;
    }
}
