using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>The lie of the land: ground surveys around a spot (the survey command, and the player's Ctrl+F12 key).</summary>
    public partial class ValheimPlugin
    {
        private ConfigEntry<KeyboardShortcut> _surveyKey;
        private ConfigEntry<float> _surveyRadius;

        /// <summary>Its settings live in Claude Tools' own file (com.quad.claudetools.cfg, section Keys), where they always were.</summary>
        private void BindSurvey(ConfigFile config)
        {
            _surveyKey = config.Bind("Keys", "SurveyKey", new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl),
                "Write the ground heights, water and buildings around where you look into BepInEx/claude/_survey.json.");
            _surveyRadius = config.Bind("Keys", "SurveyRadius", 24f, new ConfigDescription("How far a survey reaches (metres).", new AcceptableValueRange<float>(4f, 64f)));
        }

        /// <summary>Ctrl+F12: a survey where the player looks.</summary>
        private void SurveyKey()
        {
            Player player = Player.m_localPlayer;
            if (player == null || _surveyKey == null || Adapter.Typing) return;
            KeyboardShortcut survey = _surveyKey.Value;
            bool surveyPressed = survey.MainKey != KeyCode.None && Keys.Down(survey.MainKey) && survey.Modifiers.All(Keys.Held);
            if (!surveyPressed) return;
            Vector3 at = LookPoint(out Vector3 hit) ? hit : player.transform.position;
            Survey(at, _surveyRadius.Value, 1f);
            player.Message(MessageHud.MessageType.Center, $"Surveyed {(int)_surveyRadius.Value} m around this spot for the assistant");
        }

        /// <summary>Ground heights on a grid around a point, the water level, the biome and the buildings there (BepInEx/claude/_survey.json).</summary>
        internal static string Survey(Vector3 centre, float radius, float step)
        {
            ZoneSystem zone = ZoneSystem.instance;
            float baseY = zone.GetGroundHeight(centre);
            int n = Mathf.Clamp(Mathf.RoundToInt(radius / step), 1, 128);
            var ground = new JArray();
            var solid = new JArray();
            float lowest = float.MaxValue, highest = float.MinValue;
            for (int iz = -n; iz <= n; iz++)
            {
                var gRow = new JArray();
                var sRow = new JArray();
                for (int ix = -n; ix <= n; ix++)
                {
                    var p = new Vector3(centre.x + ix * step, 0f, centre.z + iz * step);
                    float g = zone.GetGroundHeight(p) - baseY;
                    float s = zone.GetSolidHeight(p, out float sh, 1000) ? sh - baseY : g;
                    gRow.Add(Math.Round(g, 2));
                    sRow.Add(Math.Round(s, 2));
                    lowest = Mathf.Min(lowest, g); highest = Mathf.Max(highest, g);
                }
                ground.Add(gRow);
                solid.Add(sRow);
            }

            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(centre, radius, pieces);
            var existing = new JArray();
            foreach (Piece pc in pieces.Take(2000))
            {
                Vector3 local = pc.transform.position - new Vector3(centre.x, baseY, centre.z);
                existing.Add(new JObject { ["p"] = Utils.GetPrefabName(pc.gameObject), ["x"] = Math.Round(local.x, 2), ["y"] = Math.Round(local.y, 2), ["z"] = Math.Round(local.z, 2), ["ry"] = Math.Round(pc.transform.eulerAngles.y, 1) });
            }

            Player player = Player.m_localPlayer;
            var doc = new JObject
            {
                ["note"] = "Heights are metres above the ground at the centre. Rows run from -z (south) to +z, columns from -x (west) to +x, one every 'step' metres. 'solid' includes rocks, trees and buildings; 'ground' is the terrain only. Pieces are relative to the centre at its ground height.",
                ["time"] = DateTime.Now.ToString("s"),
                ["world"] = ZNet.instance != null ? ZNet.instance.GetWorldName() : "",
                ["centre"] = Vec(new Vector3(centre.x, baseY, centre.z)),
                ["playerYaw"] = player != null ? Math.Round(player.transform.eulerAngles.y, 1) : 0,
                ["biome"] = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(centre).ToString() : "",
                ["radius"] = radius, ["step"] = step,
                ["waterAboveCentre"] = Math.Round(zone.m_waterLevel - baseY, 2),
                ["lowest"] = Math.Round(lowest, 2), ["highest"] = Math.Round(highest, 2),
                ["ground"] = ground, ["solid"] = solid, ["pieces"] = existing,
            };
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "_survey.json");
            File.WriteAllText(path, doc.ToString(Newtonsoft.Json.Formatting.None));
            return path;
        }

        private void RegisterSurvey()
        {
            Builtin("survey", "survey [radius] [step] [place]: ground heights, water, biome and buildings around a place (default: where the player looks) into _survey.json", (a, output, error) =>
            {
                if (!Frame(a.Length > 3 ? a[3] : "look", out Vector3 at, out float _, out string err)) { error(err); return null; }
                output(new JObject { ["survey"] = Survey(at, Mathf.Clamp(F(a, 1, _surveyRadius.Value), 2f, 128f), Mathf.Max(0.25f, F(a, 2, 1f))) });
                return null;
            });
        }
    }
}
