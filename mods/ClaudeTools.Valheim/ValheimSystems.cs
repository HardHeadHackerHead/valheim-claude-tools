namespace ClaudeTools
{
    /// <summary>
    /// Valheim's game systems: groups of methods that do one job, so mods patching different methods of one job show up as possible clashes
    /// (who, clashes, modcheck). The Valheim adapter hands them to Claude Tools (GameAdapter.Systems); modkit, without the game, takes them
    /// from here too (tools/ModKit compiles this file).
    /// </summary>
    internal static class ValheimSystems
    {
        public static readonly string[][] Systems =
        {
            new[] { "crafting payment", "Inventory.CountItems", "Inventory.HaveItem", "Inventory.RemoveItem", "Player.HaveRequirementItems", "Player.HaveRequirements",
                    "Player.ConsumeResources", "Player.GetFirstRequiredItem", "InventoryGui.DoCrafting" },
            new[] { "recipe list", "InventoryGui.UpdateRecipeList", "InventoryGui.UpdateCraftingPanel", "InventoryGui.AddRecipeToList", "Player.GetAvailableRecipes",
                    "Player.UpdateKnownRecipesList", "Player.UpdateAvailablePiecesList" },
            new[] { "inventory size", "Player.SetInventorySize", "InventoryGui.SetInventorySize", "Player.OnSpawned", "Inventory..ctor" },
            new[] { "inventory slots", "Inventory.FindEmptySlot", "Inventory.HaveEmptySlot", "Inventory.GetEmptySlots", "Inventory.CanAddItem", "Inventory.AddItem",
                    "Inventory.MoveItemToThis", "Inventory.MoveAll", "InventoryGrid.DropItem" },
            new[] { "saving items and characters", "Inventory.Save", "Inventory.Load", "ItemDrop.SaveToZDO", "ItemDrop.LoadFromZDO", "Player.Save", "Player.Load",
                    "PlayerProfile.SavePlayerData", "PlayerProfile.LoadPlayerData", "PlayerProfile.SavePlayerToDisk", "PlayerProfile.LoadPlayerFromDisk" },
            new[] { "death and tombstones", "Player.OnDeath", "Player.CreateTombStone", "TombStone.*", "Inventory.MoveInventoryToGrave", "Humanoid.UnequipAllItems" },
            new[] { "equipment", "Humanoid.EquipItem", "Humanoid.UnequipItem", "Humanoid.SetupEquipment", "Humanoid.IsItemEquiped", "Player.QueueEquipAction",
                    "Player.QueueUnequipAction", "VisEquipment.*" },
            new[] { "portals and teleporting", "TeleportWorld.*", "Game.ConnectPortals", "ZDOMan.GetPortalList", "Player.TeleportTo", "Teleport.*", "Player.IsTeleportable",
                    "Inventory.IsTeleportable" },
            new[] { "containers", "Container.*", "InventoryGui.Show", "InventoryGui.Hide" },
            new[] { "picking up", "Player.AutoPickup", "Humanoid.Pickup", "ItemDrop.Pickup", "ItemDrop.Interact", "Pickable.*" },
            new[] { "smelting, cooking and fermenting", "Smelter.*", "CookingStation.*", "Fermenter.*", "Beehive.*" },
            new[] { "building", "Player.PlacePiece", "Player.TryPlacePiece", "Player.UpdatePlacement", "Player.UpdatePlacementGhost", "Player.SetupPlacementGhost",
                    "Player.RemovePiece", "Player.CheckCanRemovePiece", "Piece.*", "WearNTear.*" },
            new[] { "carry weight", "Player.GetMaxCarryWeight", "Inventory.GetTotalWeight", "Inventory.UpdateTotalWeight" },
            new[] { "food, health and stamina", "Player.UseStamina", "Player.HaveStamina", "Player.UpdateFood", "Player.EatFood", "Player.CanEat", "Player.GetTotalFoodValue",
                    "Player.SetMaxHealth", "Player.SetMaxStamina", "SEMan.*" },
            new[] { "skills", "Skills.*" },
            new[] { "combat and damage", "Character.Damage", "Character.RPC_Damage", "Character.ApplyDamage", "Humanoid.BlockAttack", "Attack.*", "HitData.*" },
            new[] { "keys and input", "ZInput.*", "PlayerController.TakeInput" },
            new[] { "inventory screen", "InventoryGui.*", "InventoryGrid.*" },
            new[] { "map", "Minimap.*" },
            new[] { "creatures' AI", "BaseAI.*", "MonsterAI.*", "AnimalAI.*", "Tameable.*" },
            new[] { "spawning", "SpawnSystem.*", "CreatureSpawner.*", "SpawnArea.*" },
            new[] { "world generation", "ZoneSystem.*", "WorldGenerator.*", "Heightmap.*", "TerrainComp.*" },
            new[] { "network", "ZNet.*", "ZDOMan.*", "ZRoutedRpc.*", "ZNetView.*" },
            // everyone adds items and pieces here; stacking postfixes is normal
            new[] { "registering items and pieces", "ObjectDB.Awake", "ObjectDB.CopyOtherDB", "ZNetScene.Awake", "FejdStartup.Awake", "FejdStartup.SetupObjectDB" },
        };

        /// <summary>Systems where two mods doing the same job can cost players items: their overlaps rank first among the ones to check.</summary>
        public static readonly string[] HighStakes =
            { "crafting payment", "inventory size", "inventory slots", "saving items and characters", "death and tombstones", "containers",
              "smelting, cooking and fermenting" };

        /// <summary>
        /// Methods where a prefix that skips the game's method usually does the job itself (pays, deposits, hands over). Two such prefixes both
        /// run (HarmonyX runs every prefix), so the job is done twice unless the later one reads __runOriginal.
        /// </summary>
        public static readonly string[] DoesTheJob =
        {
            "Inventory.RemoveItem", "Inventory.RemoveOneItem", "Player.ConsumeResources", "Smelter.Spawn", "Fermenter.DelayedTap",
            "Fermenter.Tap", "CookingStation.SpawnItem", "Beehive.Extract", "Container.RPC_TakeAllRespons", "Inventory.MoveAll",
        };

        // systems whose overlaps are nearly always window-only input handling (a mod's window blocking the player's keys): not a job done twice
        public static readonly string[] NotAJob = { "keys and input", "inventory screen", "registering items and pieces" };
    }
}
