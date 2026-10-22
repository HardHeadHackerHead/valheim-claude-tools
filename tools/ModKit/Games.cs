using System;
using System.IO;
using System.Linq;

namespace ClaudeTools
{
    /// <summary>
    /// The games modkit knows, as their Claude Tools adapters describe them in the game: where the code is, the game's Thunderstore community
    /// and BepInEx pack, and its systems (Valheim's only, so far). Valheim is the first, and the default.
    /// </summary>
    internal sealed class GameProfile
    {
        public string Id, Name, SteamFolder, DataDir, EnvVar, Community, BepInExPack;
        public string[] Assemblies = { "Assembly-CSharp.dll" };
        public string[][] Systems = new string[0][];
        public string[] HighStakes = new string[0], DoesTheJob = new string[0], NotAJob = new string[0];

        public static readonly GameProfile[] All =
        {
            new GameProfile
            {
                Id = "valheim", Name = "Valheim", SteamFolder = "Valheim", DataDir = "valheim_Data", EnvVar = "VALHEIM_DIR",
                Community = "valheim", BepInExPack = "denikson-BepInExPack_Valheim",
                Assemblies = new[] { "assembly_valheim.dll", "assembly_utils.dll", "assembly_guiutils.dll" },
                Systems = ValheimSystems.Systems, HighStakes = ValheimSystems.HighStakes, DoesTheJob = ValheimSystems.DoesTheJob, NotAJob = ValheimSystems.NotAJob,
            },
            new GameProfile
            {
                Id = "mycopunk", Name = "Mycopunk", SteamFolder = "Mycopunk", DataDir = "Mycopunk_Data", EnvVar = "MYCOPUNK_DIR",
                Community = "mycopunk", BepInExPack = "BepInEx-BepInExPack_Mycopunk",
            },
            new GameProfile
            {
                Id = "emberville", Name = "Emberville", SteamFolder = "Emberville Demo", DataDir = "Emberville_Data", EnvVar = "EMBERVILLE_DIR",
                // (no Thunderstore community known yet: no library)
            },
        };

        public static GameProfile Find(string id) => All.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || g.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The game whose folder holds this one (modkit lives in the game's BepInEx/claude/modkit), if any.</summary>
        public static GameProfile Around(string dir)
        {
            for (string d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d.TrimEnd('/', '\\')))
            {
                GameProfile here = All.FirstOrDefault(g => Directory.Exists(Path.Combine(d, g.DataDir)));
                if (here != null) return here;
            }
            return null;
        }
    }
}
