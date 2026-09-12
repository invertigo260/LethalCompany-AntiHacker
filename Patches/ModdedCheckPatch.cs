using HarmonyLib;

namespace AntiHacker.Patches
{
    [HarmonyPatch(typeof(ModdedCheck))]
    internal class ModdedCheckPatch
    {
        [HarmonyPatch(nameof(ModdedCheck.GetModdedState))]
        [HarmonyPostfix]
        private static void PostfixGetModdedState(ref ModdedState __result)
        {
            if (Plugin.Instance.AppearInVanilla.Value)
            {
                __result = ModdedState.Vanilla;
            }
        }
    }
}
