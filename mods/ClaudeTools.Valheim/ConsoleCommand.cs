using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// "claude &lt;command&gt;" in the game's console (F5): any request command, typed by the player, with the answer printed in the console
    /// and saved in full to BepInEx/claude/console/&lt;command&gt;.json. For debugging by hand: claude clashes, claude who Player.ConsumeResources,
    /// claude library update. Works whether or not AllowRequests is on (the player is typing it); commands that change the world or hand
    /// out items only where the game allows cheats.
    /// </summary>
    public partial class ValheimPlugin
    {
        private const string ConsoleName = "claude";
        private const int ConsoleLines = 60;

        private void RegisterConsole()
        {
            new Terminal.ConsoleCommand(ConsoleName,
                "Claude Tools: claude help (all commands), claude clashes [mod], claude who <Type.Method>, claude patches [text], claude library [update]",
                (Terminal.ConsoleEvent)(args => FromConsole(args)));
        }

        private void UnregisterConsole()
        {
            try
            {
                var commands = AccessTools.Field(typeof(Terminal), "commands")?.GetValue(null) as IDictionary<string, Terminal.ConsoleCommand>;
                commands?.Remove(ConsoleName);
            }
            catch (Exception) { }
        }

        private void FromConsole(Terminal.ConsoleEventArgs args)
        {
            Terminal term = args.Context;
            string[] a = args.ArgsAll.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) a = new[] { "help" };
            if (!FindCommand(a[0], out string name)) { term.AddString($"claude: no command '{a[0]}' (claude help lists them)"); return; }
            if (NeedsWorld(name)) { term.AddString($"claude {name}: join a world first"); return; }
            if (CheatBlocked(name)) { term.AddString("claude " + CheatRefusal(name)); return; }

            var outputs = new JArray();
            var errors = new List<string>();
            IEnumerator run = null;
            ClawdOnCommand(name, CommandOwner(name), a);
            try { run = RunCommand(a, o => outputs.Add(o), e => errors.Add(e)); }
            catch (Exception e) { errors.Add(e.Message); }
            if (run == null) Print(term, name, outputs, errors);
            else
            {
                term.AddString($"claude {name}: working...");
                StartCoroutine(Finish(term, name, run, outputs, errors));
            }
        }

        private IEnumerator Finish(Terminal term, string name, IEnumerator run, JArray outputs, List<string> errors)
        {
            while (true)
            {
                object step;
                try
                {
                    if (!run.MoveNext()) break;
                    step = run.Current;
                }
                catch (Exception e) { errors.Add(e.Message); break; }
                yield return step;
            }
            Print(term, name, outputs, errors);
        }

        private static void Print(Terminal term, string name, JArray outputs, List<string> errors)
        {
            if (name != "clawd") ClawdOnDone(1, errors.Count > 0 ? name + ": " + errors[0] : null);
            var lines = new List<string>();
            foreach (JToken o in outputs) lines.AddRange(TextLines(o));
            lines.AddRange(errors.Select(e => "error: " + e));
            string saved = null;
            try
            {
                string dir = Path.Combine(Folder, "console");
                Directory.CreateDirectory(dir);
                saved = Path.Combine(dir, name + ".json");
                File.WriteAllText(saved, new JObject { ["outputs"] = outputs, ["errors"] = new JArray(errors) }.ToString());
            }
            catch (Exception) { saved = null; }
            foreach (string line in lines.Take(ConsoleLines)) term.AddString(line);
            if (lines.Count > ConsoleLines) term.AddString($"... {lines.Count - ConsoleLines} more lines" + (saved != null ? " in " + saved : ""));
            else if (lines.Count == 0) term.AddString($"claude {name}: done");
        }
    }
}
