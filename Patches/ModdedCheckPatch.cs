using HarmonyLib;
using BepInEx.Bootstrap;
using System.Linq;

namespace AntiHacker.Patches
{
    [HarmonyPatch(typeof(ModdedCheck))]
    internal class ModdedCheckPatch
    {
        [HarmonyPatch(nameof(ModdedCheck.GetModdedState))]
        [HarmonyPostfix]
        private static void PostfixGetModdedState(ref ModdedState __result)
        {
            if (Plugin.Instance.ForceVanillaLobby.Value)
            {
                // Explicit override: always show as Vanilla
                __result = ModdedState.Vanilla;
            }
            else
            {
                // Override is false, fallback to dynamic logic
                // Check if any mods OTHER than this one are loaded
                bool hasOtherMods = Chainloader.PluginInfos.Keys.Any(guid => guid != Plugin.PLUGIN_GUID);

                if (!hasOtherMods)
                {
                    // It's the only mod, so it's safe to appear as Vanilla
                    __result = ModdedState.Vanilla;
                }
                else
                {
                    // Other mods are present. Do nothing, letting the game show as Modded.
                    Plugin.Log.LogInfo("AntiHacker: Other mods detected. Leaving server as Modded.");
                }
            }
        }
    }
}
