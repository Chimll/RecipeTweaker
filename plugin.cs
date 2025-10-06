using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using VampireCommandFramework;

namespace RecipeTweaker
{
    [BepInPlugin(Id, Name, Version)]
    [BepInProcess("VRisingServer.exe")]
    [BepInDependency("gg.deca.VampireCommandFramework", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string Id      = "com.yourname.RecipeTweaker";
        public const string Name    = "RecipeTweaker";
        public const string Version = "0.4.0";

        internal static Plugin Instance = null!;
        internal static Harmony Harmony  = null!;
        internal static ManualLogSource Logger = null!;

        // Config
        internal static readonly HashSet<int> BlockedRecipeGuids = new(); 
        private static string ConfigDir  => Path.Combine("BepInEx", "config", "RecipeTweaker");
        private static string ConfigFile => Path.Combine(ConfigDir, "blocked_recipes.txt");

        // Default prefabs (weapon coatings) can be changed to whatever you like
        private static readonly int[] DefaultSeeds = new[]
        {
            // Recipe_WeaponCoating_Blood, Chaos, Frost, Illusion, Storm, Unholy
            -1487423952, -338717708, -789668816, 405829513, -2034775483, 216972181
        };

        public override void Load()
        {
            Instance = this;
            Logger = base.Log;

            EnsureConfigExists();
            LoadBlockedListFromDisk();

            Harmony = new Harmony(Id);
            Harmony.PatchAll(typeof(StartCraftingBlockPatch));

            
            try { CommandRegistry.RegisterAll(); }
            catch (Exception) { /* VCF not installed; commands just won't be available */ }

            Logger.LogInfo($"[{Name}] {Version} loaded — {BlockedRecipeGuids.Count} recipe(s) currently blocked.");
            Logger.LogInfo($"[{Name}] Config: {Path.GetFullPath(ConfigFile)}");
        }

        public override bool Unload()
        {
            try { Harmony?.UnpatchSelf(); } catch (Exception e) { Logger.LogError(e); }
            return true;
        }

        private static void EnsureConfigExists()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                if (!File.Exists(ConfigFile))
                {
                    using var sw = new StreamWriter(ConfigFile, false);
                    sw.WriteLine("# RecipeTweaker blocked recipe PrefabGUIDs (one per line)");
                    sw.WriteLine("# Lines starting with # or // are ignored (if you'd like to remove them temporarily)");
                    sw.WriteLine("# Please refer to the wiki for the recipe prefabs that can be added https://wiki.vrisingmods.com/prefabs/Recipe");
                    foreach (var id in DefaultSeeds)
                        sw.WriteLine(id);
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[{Name}] Failed to ensure config exists: {e}");
            }
        }

        internal static void LoadBlockedListFromDisk()
        {
            try
            {
                var set = new HashSet<int>();
                if (File.Exists(ConfigFile))
                {
                    foreach (var raw in File.ReadAllLines(ConfigFile))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("#") || line.StartsWith("//")) continue;
                        if (int.TryParse(line, out var id))
                            set.Add(id);
                        else
                            Logger.LogWarning($"[{Name}] Skipping invalid GUID line: {line}");
                    }
                }

                BlockedRecipeGuids.Clear();
                foreach (var id in set) BlockedRecipeGuids.Add(id);

                Logger.LogInfo($"[{Name}] Loaded {BlockedRecipeGuids.Count} blocked recipe(s) from disk.");
            }
            catch (Exception e)
            {
                Logger.LogError($"[{Name}] Failed to load recipe list: {e}");
            }
        }

        internal static void SaveBlockedListToDisk()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var sorted = BlockedRecipeGuids.OrderBy(x => x).ToArray();

                using var sw = new StreamWriter(ConfigFile, false);
                sw.WriteLine("# RecipeTweaker blocked recipe PrefabGUIDs (one per line)");
                sw.WriteLine("# lines starting with # or // are ignored");
                foreach (var id in sorted)
                    sw.WriteLine(id);

                Logger.LogInfo($"[{Name}] Saved {sorted.Length} blocked recipe(s) to disk.");
            }
            catch (Exception e)
            {
                Logger.LogError($"[{Name}] Failed to save recipe list: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(StartCraftingSystem), nameof(StartCraftingSystem.OnUpdate))]
    public static class StartCraftingBlockPatch
    {
        private static DateTime _lastLog = DateTime.MinValue;
        private static readonly TimeSpan LogCooldown = TimeSpan.FromSeconds(2);

        [HarmonyPrefix]
        public static void Prefix(StartCraftingSystem __instance)
        {
            var em = __instance.EntityManager;

            // Prefer the system's predefined query (_StartCraftItemEventQuery); else build a fallback
            EntityQuery startEvtQuery;
            try
            {
                var fi = typeof(StartCraftingSystem).GetField("_StartCraftItemEventQuery",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (fi != null)
                {
                    var val = fi.GetValue(__instance);
                    if (val is EntityQuery qFromField)
                    {
                        startEvtQuery = qFromField;
                    }
                    else
                    {
                        startEvtQuery = em.CreateEntityQuery(new EntityQueryDesc
                        {
                            All = new[]
                            {
                                ComponentType.ReadOnly<FromCharacter>(),
                                ComponentType.ReadOnly<StartCraftItemEvent>()
                            },
                            Options = EntityQueryOptions.IncludeDisabled
                        });
                    }
                }
                else
                {
                    startEvtQuery = em.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<FromCharacter>(),
                            ComponentType.ReadOnly<StartCraftItemEvent>()
                        },
                        Options = EntityQueryOptions.IncludeDisabled
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[{Plugin.Name}] Could not access StartCrafting query: {e}");
                return;
            }

            NativeArray<Entity> evts = default;
            try
            {
                evts = startEvtQuery.ToEntityArray(Allocator.Temp);
                if (evts.Length == 0) return;

                for (int i = 0; i < evts.Length; i++)
                {
                    var e = evts[i];
                    if (!em.HasComponent<StartCraftItemEvent>(e)) continue;

                    var ev = em.GetComponentData<StartCraftItemEvent>(e);

                    if (!TryGetRecipeGuid(ev, out var recipeGuid))
                        continue;

                    if (!Plugin.BlockedRecipeGuids.Contains(recipeGuid.GuidHash))
                        continue;

                    // Cancel the craft by destroying the event before vanilla handles it
                    em.DestroyEntity(e);

                    // Rate-limited log
                    var now = DateTime.UtcNow;
                    if (now - _lastLog > LogCooldown)
                    {
                        _lastLog = now;
                        try
                        {
                            var from = em.GetComponentData<FromCharacter>(e);
                            Plugin.Logger.LogInfo($"[{Plugin.Name}] Blocked craft {recipeGuid.GuidHash} from {from.User}");
                        }
                        catch
                        {
                            Plugin.Logger.LogInfo($"[{Plugin.Name}] Blocked craft {recipeGuid.GuidHash}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[{Plugin.Name}] Error scanning StartCraftItemEvent: {ex}");
            }
            finally
            {
                if (evts.IsCreated) evts.Dispose();
            }
        }

        // Reflect the recipe PrefabGUID from the event to be version-agnostic
        private static bool TryGetRecipeGuid(StartCraftItemEvent ev, out PrefabGUID guid)
        {
            string[] candidates = { "RecipePrefab", "Recipe", "RecipeGuid", "RecipePrefabGuid", "m_Recipe", "_Recipe" };
            var t = typeof(StartCraftItemEvent);
            object boxed = ev;

            foreach (var name in candidates)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(PrefabGUID))
                {
                    var val = f.GetValue(boxed);
                    if (val is PrefabGUID pg) { guid = pg; return true; }
                }
            }

            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType != typeof(PrefabGUID)) continue;
                var val = f.GetValue(boxed);
                if (val is PrefabGUID pg) { guid = pg; return true; }
            }

            guid = default;
            return false;
        }
    }

    // === VCF Commands ===
    [CommandGroup("RecipeTweaker")]
    public static class RecipeTweakerCommands
    {
        [Command("recipe add", adminOnly: true, description: "Add a recipe PrefabGUID to the recipe list and save")]
        public static void RecipeAdd(ICommandContext ctx, int prefabGuid)
        {
            if (Plugin.BlockedRecipeGuids.Add(prefabGuid))
            {
                Plugin.SaveBlockedListToDisk();
                ctx.Reply($"Added {prefabGuid} to recipe list ({Plugin.BlockedRecipeGuids.Count} total).");
            }
            else
            {
                ctx.Reply($"Already on recipe list: {prefabGuid}.");
            }
        }

        [Command("recipe remove", adminOnly: true, description: "Remove a recipe PrefabGUID from the recipe list and save")]
        public static void RecipeRemove(ICommandContext ctx, int prefabGuid)
        {
            if (Plugin.BlockedRecipeGuids.Remove(prefabGuid))
            {
                Plugin.SaveBlockedListToDisk();
                ctx.Reply($"Removed {prefabGuid} from recipe list ({Plugin.BlockedRecipeGuids.Count} remaining).");
            }
            else
            {
                ctx.Reply($"Not on Recipe klist: {prefabGuid}.");
            }
        }

        [Command("recipe list", adminOnly: true, description: "List currently blocked recipe PrefabGUIDs")]
        public static void RecipeList(ICommandContext ctx)
        {
            if (Plugin.BlockedRecipeGuids.Count == 0)
            {
                ctx.Reply("Recipe list is empty.");
                return;
            }

            // Print in chunks to avoid long lines
            var items = Plugin.BlockedRecipeGuids.OrderBy(x => x).ToArray();
            ctx.Reply($"Blocked recipes ({items.Length}):");
            const int perLine = 10;
            for (int i = 0; i < items.Length; i += perLine)
            {
                var slice = items.Skip(i).Take(perLine);
                ctx.Reply(string.Join(", ", slice));
            }
        }

        [Command("recipe reload", adminOnly: true, description: "Reload recipe list from disk")]
        public static void RecipeReload(ICommandContext ctx)
        {
            Plugin.LoadBlockedListFromDisk();
            ctx.Reply($"Reloaded recipe list from disk — {Plugin.BlockedRecipeGuids.Count} item(s).");
        }
    }
}
