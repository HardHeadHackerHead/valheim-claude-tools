using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// The commands every game has: help, waiting, the mods and their settings, the log. The game's own (where the player is, what they carry)
    /// come from its adapter (ClaudeTools.Valheim and the like).
    /// </summary>
    public partial class Plugin
    {
        private void RegisterBuiltIns()
        {
            RegisterPictureCommands();
            RegisterRenderCommands();
            RegisterLibraryCommands();
            RegisterGameCommands();
            RegisterUiCommands();
            RegisterLagCommand();   // lag (Lag.cs)

            Builtin("help", "help: every command, with the mod that adds it", (a, output, error) =>
            {
                output(new JObject
                {
                    ["commands"] = new JArray(Commands.Values.OrderBy(c => BuiltinOwners.Contains(c.Owner) ? 0 : 1).ThenBy(c => c.Owner).ThenBy(c => c.Name)
                        .Select(c => new JObject { ["command"] = c.Name, ["usage"] = c.Usage, ["from"] = c.Owner })),
                    ["places"] = new JArray(new[] { "world", "here", "look" }.Concat(Frames.Keys.Select(k => "(names " + k + " knows)"))),
                });
                return null;
            });

            Builtin("wait", "wait <seconds>: pause (let things happen before the next picture)", (a, output, error) => Wait(Mathf.Clamp(F(a, 1, 2f), 0f, 60f)));

            Builtin("mods", "mods: the mods running, with their versions", (a, output, error) =>
            {
                output(new JObject
                {
                    ["mods"] = new JArray(LoadedMods().Select(m =>
                    {
                        BepInPlugin meta = MetadataHelper.GetMetadata(m);
                        return new JObject { ["guid"] = meta?.GUID, ["name"] = meta?.Name, ["version"] = meta?.Version?.ToString() };
                    }).OrderBy(o => (string)o["name"]))
                });
                return null;
            });

            Builtin("config", "config <mod> [section] [key]: a mod's settings; config set <mod> <section> <key> <value> changes one (needs AllowConfigChanges)", (a, output, error) =>
            {
                bool set = a.Length > 1 && a[1].Equals("set", StringComparison.OrdinalIgnoreCase);
                int at = set ? 2 : 1;
                if (a.Length <= at) { error("which mod? (\"mods\" lists them)"); return null; }
                BaseUnityPlugin mod = FindMod(a[at]);
                if (mod == null) { error($"no mod called '{a[at]}' (\"mods\" lists them)"); return null; }
                ConfigFile cfg = mod.Config;
                if (set)
                {
                    if (!_allowConfigChanges.Value) { error("changing settings is off (AllowConfigChanges in com.quad.claudetools.cfg)"); return null; }
                    if (a.Length < at + 4) { error("config set <mod> <section> <key> <value>"); return null; }
                    var def = cfg.Keys.FirstOrDefault(k => k.Section.Equals(a[at + 1], StringComparison.OrdinalIgnoreCase) && k.Key.Equals(a[at + 2], StringComparison.OrdinalIgnoreCase));
                    if (def == null) { error($"{a[at]} has no setting {a[at + 1]}/{a[at + 2]}"); return null; }
                    ConfigEntryBase entry = cfg[def];
                    string before = entry.GetSerializedValue();
                    entry.SetSerializedValue(Rest(a, at + 3));
                    output(new JObject { ["mod"] = a[at], ["setting"] = def.Section + "/" + def.Key, ["was"] = before, ["now"] = entry.GetSerializedValue() });
                    return null;
                }
                string section = a.Length > at + 1 ? a[at + 1] : null, key = a.Length > at + 2 ? a[at + 2] : null;
                var list = new JArray();
                foreach (ConfigDefinition def in cfg.Keys.Where(k => (section == null || k.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) && (key == null || k.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
                {
                    ConfigEntryBase entry = cfg[def];
                    list.Add(new JObject { ["section"] = def.Section, ["key"] = def.Key, ["value"] = entry.GetSerializedValue(), ["default"] = TomlDefault(entry), ["about"] = entry.Description?.Description });
                }
                output(new JObject { ["mod"] = MetadataHelper.GetMetadata(mod)?.Name, ["settings"] = list });
                return null;
            });

            Builtin("log", "log [lines] [text]: the last lines of the BepInEx log (only those containing the text, if given)", (a, output, error) =>
            {
                string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(path)) { error("no log file"); return null; }
                int count = Mathf.Clamp(I(a, 1, 40), 1, 300);
                string filter = Rest(a, 2);
                var lines = new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0 || (filter.Length > 0 && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        lines.Add(line.Length > 400 ? line.Substring(0, 400) + "..." : line);
                        if (lines.Count > count) lines.RemoveAt(0);
                    }
                }
                output(new JObject { ["log"] = new JArray(lines) });
                return null;
            });

            Builtin("logtrace", "logtrace <warning|log|error> <on|off>: add the calling code (a stack trace) to the game's warnings (or plain log lines, or errors) " +
                "in the log, to find what keeps logging a warning; off again when done (traces make the log much longer)", (a, output, error) =>
                {
                    if (a.Length < 3) { error("logtrace <warning|log|error> <on|off>"); return null; }
                    LogType type = a[1].ToLowerInvariant() == "log" ? LogType.Log : a[1].ToLowerInvariant() == "error" ? LogType.Error : LogType.Warning;
                    bool on = a[2].Equals("on", StringComparison.OrdinalIgnoreCase);
                    Application.SetStackTraceLogType(type, on ? StackTraceLogType.ScriptOnly : (type == LogType.Error ? StackTraceLogType.ScriptOnly : StackTraceLogType.None));
                    output(new JObject { ["type"] = type.ToString(), ["traces"] = Application.GetStackTraceLogType(type).ToString() });
                    return null;
                });
        }

        private static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        /// <summary>The mods running, from BepInEx's own list (hot-reloaded ones too). Much cheaper than Resources.FindObjectsOfTypeAll.</summary>
        public static IEnumerable<BaseUnityPlugin> LoadedMods() => Chainloader.PluginInfos.Values.Select(i => i.Instance).Where(p => p != null);

        public static BaseUnityPlugin FindMod(string name) =>
            LoadedMods().FirstOrDefault(m =>
            {
                BepInPlugin meta = MetadataHelper.GetMetadata(m);
                return meta != null && (meta.GUID.Equals(name, StringComparison.OrdinalIgnoreCase) || meta.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            });

        private static string TomlDefault(ConfigEntryBase entry)
        {
            try { return TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType); }
            catch (Exception) { return entry.DefaultValue?.ToString(); }
        }
    }
}
