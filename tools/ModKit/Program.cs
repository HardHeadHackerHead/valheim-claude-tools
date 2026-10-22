using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClaudeTools
{
    /// <summary>
    /// Starts modkit: finds the game and its folder (Valheim unless it lives in another game's BepInEx or is told --game), and lets .NET find
    /// Mono.Cecil and Newtonsoft.Json in it (they come with the game and BepInEx) before any code that uses them runs.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var rest = new List<string>(args);
            string Take(string flag)
            {
                int at = rest.IndexOf(flag);
                if (at < 0 || at + 1 >= rest.Count) return null;
                string v = rest[at + 1];
                rest.RemoveRange(at, 2);
                return v;
            }
            string asked = Take("--game");
            string dir = Take("--gamedir") ?? Take("--valheim");
            GameProfile game = asked != null ? GameProfile.Find(asked) : GameProfile.Around(dir ?? AppContext.BaseDirectory) ?? GameProfile.All[0];
            if (game == null)
            {
                Console.Error.WriteLine($"modkit: no game called {asked} (modkit knows {string.Join(", ", GameProfile.All.Select(g => g.Id).ToArray())}).");
                return 2;
            }
            dir = dir ?? FindGame(game);
            if (dir == null)
            {
                Console.Error.WriteLine($"modkit: can't find the {game.Name} folder. Set {game.EnvVar}, or add --gamedir <folder>.");
                return 2;
            }
            // BepInEx: given, or the one this program lives in (BepInEx/claude/modkit: a mod manager's profile keeps it outside the game), or the game's
            string bepinex = Take("--bepinex") ?? Environment.GetEnvironmentVariable("BEPINEX_DIR");
            if (!IsBepInEx(bepinex)) bepinex = Ancestors(AppContext.BaseDirectory).FirstOrDefault(IsBepInEx) ?? Path.Combine(dir, "BepInEx");
            string[] dirs = { Path.Combine(bepinex, "core"), Path.Combine(dir, game.DataDir, "Managed") };
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in dirs)
                {
                    string path = Path.Combine(dir, file);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            return Start(rest.ToArray(), game, dir, bepinex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // (so Cecil and Newtonsoft load only after the resolver is in place)
        private static int Start(string[] args, GameProfile game, string dir, string bepinex) => Cli.Run(args, game, dir, bepinex);

        private static bool IsBepInEx(string d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "core", "BepInEx.dll"));

        private static IEnumerable<string> Ancestors(string d)
        {
            for (; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d.TrimEnd('/', '\\'))) yield return d;
        }

        /// <summary>The game's folder: its environment variable (VALHEIM_DIR), a folder above this program (it lives in BepInEx/claude/modkit), or Steam's usual places.</summary>
        private static string FindGame(GameProfile game)
        {
            bool IsGame(string d) => d != null && Directory.Exists(Path.Combine(d, game.DataDir));
            string env = Environment.GetEnvironmentVariable(game.EnvVar);
            if (IsGame(env)) return env;
            for (string d = AppContext.BaseDirectory; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d.TrimEnd('/', '\\')))
                if (IsGame(d)) return d;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] guesses =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\" + game.SteamFolder, @"D:\SteamLibrary\steamapps\common\" + game.SteamFolder, @"E:\SteamLibrary\steamapps\common\" + game.SteamFolder,
                Path.Combine(home, ".local/share/Steam/steamapps/common", game.SteamFolder), Path.Combine(home, ".steam/steam/steamapps/common", game.SteamFolder),
                Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common", game.SteamFolder),
                Path.Combine(home, "Library/Application Support/Steam/steamapps/common", game.SteamFolder),
            };
            return guesses.FirstOrDefault(IsGame);
        }
    }
}
