using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace AntiHacker.Patches
{
    /// <summary>
    /// Host-side validation of ServerRpcs. Only applies to RPCs executed on behalf of a remote
    /// client (Security.CurrentSender set by the RpcMessageHelpers.Handle hook); the host's own calls pass.
    /// </summary>
    internal static class RpcGuard
    {
        private static readonly Dictionary<ulong, List<float>> placeTimes = new Dictionary<ulong, List<float>>();
        private static readonly Dictionary<ulong, List<float>> shipGrabTimes = new Dictionary<ulong, List<float>>();

        /// <summary>True when this call was sent by a remote client.</summary>
        public static bool IsRemote(out ulong sender)
        {
            sender = 0;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return false;
            if (Security.CurrentSender == null) return false;
            sender = Security.CurrentSender.Value;
            return sender != NetworkManager.ServerClientId;
        }

        public static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z));
        }

        /// <summary>Returns true when sender exceeded maxPerSecond placement/store calls.</summary>
        public static bool RateLimited(ulong sender, int maxPerSecond)
        {
            if (!placeTimes.TryGetValue(sender, out var list))
            {
                list = new List<float>();
                placeTimes[sender] = list;
            }
            float now = Time.realtimeSinceStartup;
            list.RemoveAll(t => now - t > 1f);
            list.Add(now);
            return list.Count > maxPerSecond;
        }

        /// <summary>
        /// Tracks how many ship objects (isInShipRoom) a remote client grabs within 1 second.
        /// Returns true when the client exceeds maxPerSecond.
        /// </summary>
        public static bool ShipGrabRateLimited(ulong sender, int maxPerSecond, out int countInWindow)
        {
            if (!shipGrabTimes.TryGetValue(sender, out var list))
            {
                list = new List<float>();
                shipGrabTimes[sender] = list;
            }
            float now = Time.realtimeSinceStartup;
            list.RemoveAll(t => now - t > 1f);
            list.Add(now);
            countInWindow = list.Count;
            return countInWindow > maxPerSecond;
        }

        public static bool InsideShip(Vector3 pos)
        {
            var sor = StartOfRound.Instance;
            if (sor == null || sor.shipBounds == null) return true; // cannot judge

            Bounds b = sor.shipBounds.bounds;
            b.Expand(40f); // +20 units each side
            if (b.Contains(pos)) return true;
            if (sor.elevatorTransform != null && b.Contains(sor.elevatorTransform.TransformPoint(pos))) return true;
            return false;
        }

        /// <summary>Checks whether a world position is inside the ship's inner cabin (shipInnerRoomBounds).</summary>
        public static bool InsideShipInnerRoom(Vector3 worldPos)
        {
            var sor = StartOfRound.Instance;
            if (sor == null || sor.shipInnerRoomBounds == null) return false;

            Bounds inner = sor.shipInnerRoomBounds.bounds;
            inner.Expand(1f);
            return inner.Contains(worldPos);
        }

        /// <summary>True when the ship is currently at the Company Building (71 Gordion) to sell scrap.</summary>
        public static bool IsCompanyMoon()
        {
            var sor = StartOfRound.Instance;
            if (sor == null || sor.currentLevel == null) return false;
            var lvl = sor.currentLevel;
            return lvl.levelID == 3
                || (!string.IsNullOrEmpty(lvl.sceneName) && lvl.sceneName.IndexOf("Company", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrEmpty(lvl.PlanetName) && lvl.PlanetName.IndexOf("Gordion", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Cleanly rejects a GrabObjectServerRpc by replacing grabbedObject with default(NetworkObjectReference)
        /// and letting GrabObjectServerRpc execute its vanilla invalidation branch (GrabObjectClientRpc(false, ...)),
        /// so the client's grab animation is cancelled cleanly instead of locking their hands.
        /// </summary>
        public static bool RejectGrab(ref NetworkObjectReference grabbedObject)
        {
            grabbedObject = default;
            return true;
        }

        private const int MaxCreditIncreaseBeforeBan = 1000;

        /// <summary>
        /// Clients report the credit total after their purchase. That number must never be higher than
        /// the host's own balance. Small mismatches (a purchase racing a sync) are dropped silently;
        /// negative or wildly inflated values are treated as hacking.
        /// </summary>
        public static bool CreditsValid(ulong sender, int newCredits, string rpcName)
        {
            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (terminal == null) return true;

            int current = terminal.groupCredits;
            if (newCredits < 0 || newCredits > current + MaxCreditIncreaseBeforeBan)
            {
                Security.Punish(sender, $"{rpcName} with invalid credits {newCredits} (host has {current})");
                return false;
            }
            if (newCredits > current)
            {
                Plugin.Log.LogWarning($"AntiHacker: dropped {rpcName} from client {sender}: credits {newCredits} > host {current}");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB))]
    internal class PlayerItemRpcGuard
    {
        private const float MaxGrabDistance = 15f;

        /// <summary>
        /// Certain items have a scrapValue > 0 in vanilla but should remain grabbable inside the ship:
        /// Clipboard, Sticky Note, Dead Player Bodies, Keys, Whoopie Cushions, Easter Eggs, and Homemade Flashbangs.
        /// </summary>
        private static bool IsExemptFromShipScrapLock(GrabbableObject grabbable)
        {
            if (grabbable == null) return true;
            if (grabbable is RagdollGrabbableObject ||
                grabbable is ClipboardItem ||
                grabbable is KeyItem ||
                grabbable is WhoopieCushionItem ||
                grabbable is StunGrenadeItem)
            {
                return true;
            }

            string name = grabbable.name;
            if (!string.IsNullOrEmpty(name) && MatchesExemptItemName(name))
            {
                return true;
            }

            string itemName = grabbable.itemProperties != null ? grabbable.itemProperties.itemName : null;
            if (!string.IsNullOrEmpty(itemName) && MatchesExemptItemName(itemName))
            {
                return true;
            }

            return false;
        }

        private static bool MatchesExemptItemName(string text)
        {
            return text.IndexOf("Clipboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("StickyNote", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Whoopie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Easter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Egg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Flashbang", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [HarmonyPatch("GrabObjectServerRpc")]
        [HarmonyPrefix]
        static bool GrabObject(PlayerControllerB __instance, ref NetworkObjectReference grabbedObject)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;

            if (!grabbedObject.TryGet(out NetworkObject netObj) || netObj == null)
            {
                return true;
            }

            GrabbableObject grabbable = netObj.GetComponentInChildren<GrabbableObject>();
            if (grabbable == null)
            {
                return true;
            }

            // 1. Distance check so a remote client cannot vacuum items from across the map.
            float dist = Vector3.Distance(__instance.transform.position, grabbable.transform.position);
            if (dist > MaxGrabDistance)
            {
                Plugin.Log.LogWarning($"AntiHacker: blocked client {sender} from grabbing '{grabbable.name}' from {dist:F1}m away");
                return RpcGuard.RejectGrab(ref grabbedObject);
            }

            // 2. Only check isInShipRoom (not isInElevator, since elevators/catwalks are loaded and unloaded while ferrying loot).
            if (grabbable.isInShipRoom && !RpcGuard.IsCompanyMoon())
            {
                // Check scrap lock first (without counting blocked attempts toward the rate limiter).
                if (Plugin.Instance.LockShipScrapUntilCompany.Value && grabbable.scrapValue > 0 && !IsExemptFromShipScrapLock(grabbable))
                {
                    return RpcGuard.RejectGrab(ref grabbedObject);
                }

                // Rate-limit grabbing allowed ship objects (e.g. 0-value tools) without auto-banning players who mash E.
                int maxRate = Math.Max(1, Plugin.Instance.MaxShipGrabsPerSecond.Value);
                if (RpcGuard.ShipGrabRateLimited(sender, maxRate, out int grabCount))
                {
                    Plugin.Log.LogWarning($"AntiHacker: rate-limited client {sender} grabbing ship object '{grabbable.name}' ({grabCount}/{maxRate} per sec)");
                    return RpcGuard.RejectGrab(ref grabbedObject);
                }
            }

            return true;
        }

        [HarmonyPatch("ThrowObjectServerRpc")]
        [HarmonyPrefix]
        static bool ThrowObject(
            PlayerControllerB __instance,
            NetworkObjectReference grabbedObject,
            ref bool droppedInElevator,
            ref bool droppedInShipRoom,
            ref Vector3 targetFloorPosition)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;

            if (!grabbedObject.TryGet(out NetworkObject netObj) || netObj == null)
            {
                return false;
            }

            GrabbableObject grabbable = netObj.GetComponentInChildren<GrabbableObject>();
            if (grabbable == null)
            {
                return false;
            }

            // 1. Must actually be holding the object on the server.
            //    Hackers (like fleetway) call ThrowObjectServerRpc on every unheld ship item with
            //    droppedInShipRoom=false to strip isInShipRoom and despawn all scrap on takeoff.
            if (__instance.currentlyHeldObjectServer != grabbable && grabbable.playerHeldBy != __instance)
            {
                if (grabbable.isInShipRoom && !droppedInShipRoom)
                {
                    Security.Punish(sender, $"ThrowObjectServerRpc on unheld ship item '{grabbable.name}' with droppedInShipRoom={droppedInShipRoom}");
                    return false;
                }
                Plugin.Log.LogWarning($"AntiHacker: dropped ThrowObjectServerRpc from client {sender} for unheld item '{grabbable.name}'");
                return false;
            }

            // 2. Validate targetFloorPosition is finite (Note: vanilla GetItemFloorPosition raycasts up to 80m down,
            //    so legitimate held drops over pits/stairs or while the ship/doors move can exceed 20m-80m).
            if (!RpcGuard.IsFinite(targetFloorPosition))
            {
                Security.Punish(sender, $"ThrowObjectServerRpc with non-finite position {targetFloorPosition}");
                return false;
            }

            var sor = StartOfRound.Instance;
            Transform parentTransform = droppedInElevator ? sor?.elevatorTransform : sor?.propsContainer;
            Vector3 worldTarget = parentTransform != null ? parentTransform.TransformPoint(targetFloorPosition) : targetFloorPosition;

            // 3. If the item is dropped inside the ship's inner cabin, prevent spoofing droppedInShipRoom=false.
            if ((__instance.isInHangarShipRoom || grabbable.isInShipRoom) && RpcGuard.InsideShipInnerRoom(worldTarget) && (!droppedInShipRoom || !droppedInElevator))
            {
                Plugin.Log.LogWarning($"AntiHacker: client {sender} dropped '{grabbable.name}' inside ship room with droppedInShipRoom={droppedInShipRoom}; forcing ship flags true");
                if (!droppedInElevator && sor?.elevatorTransform != null)
                {
                    targetFloorPosition = sor.elevatorTransform.InverseTransformPoint(worldTarget);
                }
                droppedInElevator = true;
                droppedInShipRoom = true;
            }

            return true;
        }

        [HarmonyPatch("PlaceObjectServerRpc")]
        [HarmonyPrefix]
        static bool PlaceObject(
            PlayerControllerB __instance,
            NetworkObjectReference grabbedObject,
            NetworkObjectReference parentObject,
            Vector3 placePositionOffset)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;

            if (!grabbedObject.TryGet(out NetworkObject netObj) || netObj == null)
            {
                return false;
            }

            GrabbableObject grabbable = netObj.GetComponentInChildren<GrabbableObject>();
            if (grabbable == null)
            {
                return false;
            }

            // 1. Must actually be holding the object on the server.
            if (__instance.currentlyHeldObjectServer != grabbable && grabbable.playerHeldBy != __instance)
            {
                if (grabbable.isInShipRoom)
                {
                    Security.Punish(sender, $"PlaceObjectServerRpc on unheld ship item '{grabbable.name}'");
                    return false;
                }
                Plugin.Log.LogWarning($"AntiHacker: dropped PlaceObjectServerRpc from client {sender} for unheld item '{grabbable.name}'");
                return false;
            }

            // 2. Validate parentObject and offset.
            if (!RpcGuard.IsFinite(placePositionOffset) || !parentObject.TryGet(out NetworkObject parentNetObj) || parentNetObj == null)
            {
                Security.Punish(sender, $"PlaceObjectServerRpc with invalid parentObject or offset {placePositionOffset}");
                return false;
            }

            // Block parenting scrap onto moon enemy nests (exact exploit vector used on NetworkObject #107).
            if (parentNetObj.GetComponent<EnemyAINestSpawnObject>() != null || parentNetObj.GetComponentInChildren<EnemyAINestSpawnObject>() != null)
            {
                Security.Punish(sender, $"PlaceObjectServerRpc parenting '{grabbable.name}' onto EnemyAINestSpawnObject");
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal class StartOfRoundRpcGuard
    {
        [HarmonyPatch("ChangeLevelServerRpc")]
        [HarmonyPrefix]
        static bool ChangeLevel(StartOfRound __instance, int levelID, int newGroupCreditsAmount)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            if (levelID < 0 || levelID >= __instance.levels.Length)
            {
                Security.Punish(sender, $"ChangeLevelServerRpc with invalid level {levelID}");
                return false;
            }
            return RpcGuard.CreditsValid(sender, newGroupCreditsAmount, "ChangeLevelServerRpc");
        }

        [HarmonyPatch("BuyShipUnlockableServerRpc")]
        [HarmonyPrefix]
        static bool BuyUnlockable(StartOfRound __instance, int unlockableID, int newGroupCreditsAmount)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            if (unlockableID < 0 || unlockableID >= __instance.unlockablesList.unlockables.Count)
            {
                Security.Punish(sender, $"BuyShipUnlockableServerRpc with invalid id {unlockableID}");
                return false;
            }
            return RpcGuard.CreditsValid(sender, newGroupCreditsAmount, "BuyShipUnlockableServerRpc");
        }

        [HarmonyPatch("ReturnUnlockableFromStorageServerRpc")]
        [HarmonyPrefix]
        static bool ReturnUnlockable(StartOfRound __instance, int unlockableID)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            if (unlockableID < 0 || unlockableID >= __instance.unlockablesList.unlockables.Count)
            {
                Security.Punish(sender, $"ReturnUnlockableFromStorageServerRpc with invalid id {unlockableID}");
                return false;
            }
            return true;
        }

        [HarmonyPatch("StartGameServerRpc")]
        [HarmonyPrefix]
        static bool StartGame()
        {
            return AllowLever();
        }

        [HarmonyPatch("EndGameServerRpc")]
        [HarmonyPrefix]
        static bool EndGame()
        {
            return AllowLever();
        }

        [HarmonyPatch("ManuallyEjectPlayersServerRpc")]
        [HarmonyPrefix]
        static bool Eject()
        {
            return AllowLever();
        }

        private static bool AllowLever()
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            return Plugin.Instance.AllowClientStartEnd.Value;
        }

        // Debug RPCs must never be called by a legitimate client.
        [HarmonyPatch("Debug_EnableTestRoomServerRpc")]
        [HarmonyPrefix]
        static bool DebugTestRoom() { return DebugBlock("Debug_EnableTestRoomServerRpc"); }

        [HarmonyPatch("Debug_ToggleAllowDeathServerRpc")]
        [HarmonyPrefix]
        static bool DebugDeath() { return DebugBlock("Debug_ToggleAllowDeathServerRpc"); }

        [HarmonyPatch("Debug_ReviveAllPlayersServerRpc")]
        [HarmonyPrefix]
        static bool DebugRevive() { return DebugBlock("Debug_ReviveAllPlayersServerRpc"); }

        private static bool DebugBlock(string name)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            Security.Punish(sender, $"{name} called by a client");
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipBuildModeManager))]
    internal class ShipBuildRpcGuard
    {
        private static bool IsLightSwitch(NetworkObject netObj, UnlockableItem unlockable)
        {
            if (netObj != null)
            {
                if (netObj.GetComponentInChildren<ShipLights>() != null) return true;
                if (!string.IsNullOrEmpty(netObj.name) && netObj.name.IndexOf("LightSwitch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if (unlockable != null && !string.IsNullOrEmpty(unlockable.unlockableName))
            {
                if (unlockable.unlockableName.IndexOf("Light switch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        [HarmonyPatch("PlaceShipObjectServerRpc")]
        [HarmonyPrefix]
        static bool PlaceShipObject(Vector3 newPosition, Vector3 newRotation, NetworkObjectReference objectRef)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;

            // Remote clients must never place ship objects at y <= -100f (the F8 terminal storage trick is host-only)
            // or outside the ship bounds.
            if (!RpcGuard.IsFinite(newPosition) || !RpcGuard.IsFinite(newRotation) || newPosition.y <= -100f || !RpcGuard.InsideShip(newPosition))
            {
                Security.Punish(sender, $"PlaceShipObjectServerRpc to invalid position {newPosition}");
                return false;
            }

            if (objectRef.TryGet(out NetworkObject netObj) && netObj != null)
            {
                UnlockableItem unlockable = null;
                PlaceableShipObject shipObj = netObj.GetComponentInChildren<PlaceableShipObject>();
                if (shipObj != null && StartOfRound.Instance != null &&
                    shipObj.unlockableID >= 0 && shipObj.unlockableID < StartOfRound.Instance.unlockablesList.unlockables.Count)
                {
                    unlockable = StartOfRound.Instance.unlockablesList.unlockables[shipObj.unlockableID];
                }

                if (IsLightSwitch(netObj, unlockable))
                {
                    Plugin.Log.LogWarning($"AntiHacker: blocked client {sender} from moving the Light Switch");
                    return false;
                }

                if (unlockable != null && unlockable.unlockableName == "Terminal")
                {
                    Plugin.Log.LogWarning($"AntiHacker: blocked client {sender} from moving the Terminal");
                    return false;
                }
            }

            if (RpcGuard.RateLimited(sender, 10))
            {
                Plugin.Log.LogWarning($"AntiHacker: dropping placement spam from client {sender}");
                return false;
            }
            return true;
        }

        [HarmonyPatch("StoreObjectServerRpc")]
        [HarmonyPrefix]
        static bool StoreObject(NetworkObjectReference objectRef)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;

            if (objectRef.TryGet(out NetworkObject netObj) && netObj != null)
            {
                UnlockableItem unlockable = null;
                PlaceableShipObject shipObj = netObj.GetComponentInChildren<PlaceableShipObject>();
                if (shipObj != null && StartOfRound.Instance != null &&
                    shipObj.unlockableID >= 0 && shipObj.unlockableID < StartOfRound.Instance.unlockablesList.unlockables.Count)
                {
                    unlockable = StartOfRound.Instance.unlockablesList.unlockables[shipObj.unlockableID];
                }

                if (IsLightSwitch(netObj, unlockable))
                {
                    Plugin.Log.LogWarning($"AntiHacker: blocked client {sender} from storing the Light Switch");
                    return false;
                }

                if (unlockable != null)
                {
                    if (!unlockable.canBeStored ||
                        unlockable.unlockableName == "Terminal" ||
                        unlockable.unlockableName == "Teleporter" ||
                        unlockable.unlockableName == "Inverse Teleporter")
                    {
                        Plugin.Log.LogWarning($"AntiHacker: blocked client {sender} from storing protected ship object '{unlockable.unlockableName}'");
                        return false;
                    }
                }
            }

            if (RpcGuard.RateLimited(sender, 5))
            {
                Plugin.Log.LogWarning($"AntiHacker: dropping store spam from client {sender}");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Terminal))]
    internal class TerminalRpcGuard
    {
        [HarmonyPatch("SyncGroupCreditsServerRpc")]
        [HarmonyPrefix]
        static bool SyncGroupCredits(int newGroupCredits)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            return RpcGuard.CreditsValid(sender, newGroupCredits, "SyncGroupCreditsServerRpc");
        }

        [HarmonyPatch("BuyItemsServerRpc")]
        [HarmonyPrefix]
        static bool BuyItems(int newGroupCredits)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            return RpcGuard.CreditsValid(sender, newGroupCredits, "BuyItemsServerRpc");
        }

        [HarmonyPatch("BuyVehicleServerRpc")]
        [HarmonyPrefix]
        static bool BuyVehicle(int newGroupCredits)
        {
            if (!RpcGuard.IsRemote(out ulong sender)) return true;
            return RpcGuard.CreditsValid(sender, newGroupCredits, "BuyVehicleServerRpc");
        }
    }
}
