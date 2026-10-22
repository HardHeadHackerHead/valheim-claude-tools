using System;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;

namespace ClaudeTools
{
    /// <summary>
    /// How a game's adapter (ClaudeTools.Valheim and the like) finds Claude Tools, whose DLL it references. Hot loaders (Claude Tools' dev
    /// loader, ScriptEngine) load a mod under a new name each time ("ClaudeTools-638..."), so .NET can't find "ClaudeTools" by name: this
    /// answers with the newest copy loaded (or the exact one asked for, when the dev loader pointed an adapter at it). Shared source
    /// (mods/Shared/CoreLink.cs), compiled into each adapter and into Claude Tools itself. An adapter installs it before any code that uses
    /// Claude Tools runs, and waits until Running says Claude Tools has started (the loader may start the adapter first).
    /// </summary>
    internal static class CoreLink
    {
        private static readonly Regex HotName = new Regex(@"^(.+)-(\d{8,})$");

        internal static void Install() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        internal static void Uninstall() => AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

        /// <summary>That plugin (Claude Tools' GUID) has started, by BepInEx's own list (so nothing of Claude Tools' code is touched yet).</summary>
        internal static bool Running(string guid) =>
            Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info) && info.Instance != null;

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string want = new AssemblyName(e.Name).Name;
            Assembly best = null;
            long newest = -1;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = a.GetName().Name;
                if (name == want) return a;
                Match m = HotName.Match(name);
                if (m.Success && m.Groups[1].Value == want && long.TryParse(m.Groups[2].Value, out long ticks) && ticks > newest) { best = a; newest = ticks; }
            }
            return best;
        }
    }
}
