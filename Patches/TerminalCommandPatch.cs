using System.Linq;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace AntiHacker.Patches
{
    /// <summary>
    /// Terminal commands:
    ///   money 500 / money +500 / money -200   (host only; sets or adjusts group credits for everyone)
    ///   fixship                                 (host only; clears the stuck travel lock left by a failed ChangeLevel)
    /// </summary>
    [HarmonyPatch(typeof(Terminal))]
    internal class TerminalCommandPatch
    {
        private static TerminalNode Reply(string text)
        {
            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.clearPreviousText = true;
            node.displayText = text + "\n\n";
            return node;
        }

        [HarmonyPatch("ParsePlayerSentence")]
        [HarmonyPrefix]
        static bool ParsePlayerSentencePrefix(Terminal __instance, ref TerminalNode __result)
        {
            string text = __instance.screenText.text.Substring(__instance.screenText.text.Length - __instance.textAdded);
            text = text.Replace("'", "").Replace(":", "").Replace(";", "").Replace(",", "").Replace(".", "").Trim();
            string lower = text.ToLowerInvariant();

            if (lower == "fixship")
            {
                __result = Reply(Plugin.RepairShip());
                return false;
            }

            if (Plugin.Instance.EnableMoneyCommand.Value && lower.StartsWith("money "))
            {
                __result = HandleMoney(__instance, lower.Substring(6).Trim());
                return false;
            }
            if (lower == "clean all")
            {
                __result = HandleCleanAll();
                return false;
            }

            if (lower.StartsWith("clean "))
            {
                __result = HandleClean(__instance, lower.Substring(6).Trim());
                return false;
            }

            return true;
        }

        private static TerminalNode HandleCleanAll()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
            {
                return Reply("Only the host can clean ship objects.");
            }

            var localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer == null) return Reply("Player not found.");

            int storedCount = 0;
            foreach (PlaceableShipObject shipObject in Object.FindObjectsOfType<PlaceableShipObject>())
            {
                if (shipObject == null || shipObject.parentObject == null) continue;
                
                int unlockableID = shipObject.unlockableID;
                if (unlockableID < 0 || unlockableID >= StartOfRound.Instance.unlockablesList.unlockables.Count)
                    continue;

                UnlockableItem unlockable = StartOfRound.Instance.unlockablesList.unlockables[unlockableID];
                if (unlockable == null) continue;
                
                // Ignore terminal
                if (unlockable.unlockableName == "Terminal")
                    continue;

                // Check if already in storage or cannot be stored
                if (unlockable.inStorage || !unlockable.canBeStored || shipObject.parentObject.transform.position.y < -100f)
                    continue;
                
                // Do not store items if they have scrap on them (vanilla check)
                bool hasScrap = shipObject.parentObject.GetComponentInChildren<GrabbableObject>() != null;
                if (hasScrap)
                    continue;

                if (ShipBuildModeManager.Instance.InBuildMode)
                {
                    ShipBuildModeManager.Instance.CancelBuildMode();
                }

                NetworkObject netObj = shipObject.parentObject.GetComponent<NetworkObject>();
                if (netObj == null) continue;
                NetworkObjectReference netRef = (NetworkObjectReference)netObj;
                ShipBuildModeManager.Instance.StoreObjectServerRpc(netRef, (int)localPlayer.playerClientId);

                storedCount++;
            }

            return Reply($"Stored {storedCount} objects.");
        }

        private static TerminalNode HandleClean(Terminal terminal, string searchName)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
            {
                return Reply("Only the host can clean ship objects.");
            }

            var localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer == null) return Reply("Player not found.");

            int storedCount = 0;
            string matchedName = "";

            foreach (PlaceableShipObject shipObject in Object.FindObjectsOfType<PlaceableShipObject>())
            {
                if (shipObject == null || shipObject.parentObject == null) continue;

                int unlockableID = shipObject.unlockableID;
                if (unlockableID < 0 || unlockableID >= StartOfRound.Instance.unlockablesList.unlockables.Count)
                    continue;

                UnlockableItem unlockable = StartOfRound.Instance.unlockablesList.unlockables[unlockableID];
                if (unlockable == null) continue;
                
                if (string.IsNullOrEmpty(unlockable.unlockableName)) continue;
                if (!unlockable.unlockableName.ToLower().Contains(searchName))
                    continue;

                if (unlockable.unlockableName == "Terminal")
                    continue;

                if (unlockable.inStorage || !unlockable.canBeStored || shipObject.parentObject.transform.position.y < -100f)
                    continue;
                
                bool hasScrap = shipObject.parentObject.GetComponentInChildren<GrabbableObject>() != null;
                if (hasScrap)
                    continue;

                if (ShipBuildModeManager.Instance.InBuildMode)
                {
                    ShipBuildModeManager.Instance.CancelBuildMode();
                }

                NetworkObject netObj = shipObject.parentObject.GetComponent<NetworkObject>();
                if (netObj == null) continue;
                NetworkObjectReference netRef = (NetworkObjectReference)netObj;
                
                ShipBuildModeManager.Instance.StoreObjectServerRpc(netRef, (int)localPlayer.playerClientId);

                storedCount++;
                matchedName = unlockable.unlockableName;
            }

            if (storedCount > 0)
            {
                return Reply($"Stored {storedCount} '{matchedName}'(s).");
            }
            return Reply($"Could not find a storable object matching '{searchName}' on the ship.");
        }

        private static TerminalNode HandleMoney(Terminal terminal, string arg)
        {
            if (!NetworkManager.Singleton.IsHost)
            {
                return Reply("Only the host can change credits.");
            }

            bool relative = arg.StartsWith("+") || arg.StartsWith("-");
            if (!int.TryParse(arg, out int amount))
            {
                return Reply("Usage: money <amount>  (or money +<amount> / money -<amount>)");
            }

            int newCredits = relative ? terminal.groupCredits + amount : amount;
            newCredits = Mathf.Max(0, newCredits);

            // Host-local call: executes on the host and broadcasts to all clients.
            terminal.SyncGroupCreditsServerRpc(newCredits, terminal.numberOfItemsInDropship);
            Plugin.Log.LogInfo($"AntiHacker: money command set credits to {newCredits}");
            return Reply($"Group credits set to {newCredits}.");
        }    }
}
