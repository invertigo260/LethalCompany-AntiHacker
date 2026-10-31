using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AntiHacker
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.invertigo.antihacker";
        public const string PLUGIN_NAME = "AntiHacker";
        public const string PLUGIN_VERSION = "2.0.2";

        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        private Harmony harmony;

        // Anti-Hacker Configs
        public ConfigEntry<bool> PersistKickList;
        public ConfigEntry<bool> ForceVanillaLobby;
        public ConfigEntry<string> ExtraBlacklist;
        public ConfigEntry<bool> AllowClientStartEnd;
        public ConfigEntry<Key> RecoveryKey;
        public ConfigEntry<bool> RotationTipEveryTime; public ConfigEntry<bool> EnableMoneyCommand;
        public ConfigEntry<bool> LockShipScrapUntilCompany;
        public ConfigEntry<int> MaxShipGrabsPerSecond;

        // MoreButtons Configs
        public ConfigEntry<bool> EnableTeleport;
        public ConfigEntry<Key> TeleportKey;
        public ConfigEntry<Key> LightSwitchKey;
        public ConfigEntry<Key> MoveTerminalKey;
        public ConfigEntry<Key> UnstoreKey;
        private static Vector3 defaultTerminalPos;
        private static Vector3 defaultTerminalRot;
        private static bool defaultTerminalSet = false;
        private static bool wasInBuildMode = false;
        private static PlaceableShipObject cachedTerminalPlaceable;

        private void Awake()
        {
            Instance = this;
            Log = base.Logger;

            // Anti-Hacker Config Bindings
            PersistKickList = Config.Bind("AntiHacker", "PersistKickList", true, "If true, the kick list will persist across game sessions. If false, it will reset every time you close the game.");
            ForceVanillaLobby = Config.Bind("AntiHacker", "ForceVanillaLobby", false, "If true, explicitly forces your server to appear in the vanilla server list, bypassing the automatic check for other mods.");
            ExtraBlacklist = Config.Bind("AntiHacker", "ExtraBlacklist", "", "Extra Steam IDs (comma separated) to ban. A built-in blacklist of known hackers is always active.");
            AllowClientStartEnd = Config.Bind("AntiHacker", "AllowClientStartEnd", true, "If false, only the host may pull the start/end lever (client attempts are ignored, not banned).");
            RecoveryKey = Config.Bind("AntiHacker", "RecoveryKey", Key.F9, "Host only: press to clear a stuck ship travel lock (left behind by a failed ChangeLevel exploit).");
            EnableMoneyCommand = Config.Bind("AntiHacker", "EnableMoneyCommand", false, "If true, the host can use the 'money' terminal command to set or adjust group credits. Disabled by default; only intended for repairing credits tampered with by hackers.");
            LockShipScrapUntilCompany = Config.Bind("AntiHacker", "LockShipScrapUntilCompany", true, "If true, remote clients cannot pick up items with a scrap value (scrapValue > 0) once they are inside the ship, unless the ship is at the Company Building (71 Gordion) to sell.");
            MaxShipGrabsPerSecond = Config.Bind("AntiHacker", "MaxShipGrabsPerSecond", 3, "Maximum number of ship objects (isInShipRoom) a remote client can grab per second before grabs are blocked (and extreme spam is auto-banned).");
            RotationTipEveryTime = Config.Bind("Hotkeys", "RotationTipEveryTime", false, "If true, the rotation hint popup shows every time you start moving an object. If false, it shows only once.");

            // MoreButtons Config Bindings
            EnableTeleport = Config.Bind("Hotkeys", "EnableTeleport", false, "Whether the F5 contextual teleport function is enabled. Disabled by default; only intended for resolving issues caused by hackers.");
            TeleportKey = Config.Bind("Hotkeys", "TeleportKey", Key.F5, "Hotkey to teleport. Contextual based on if you are in the ship.");
            LightSwitchKey = Config.Bind("Hotkeys", "LightSwitchKey", Key.F7, "Hotkey to toggle the ship lights.");
            MoveTerminalKey = Config.Bind("Hotkeys", "MoveTerminalKey", Key.F8, "Hotkey to store/unstore the terminal.");
            UnstoreKey = Config.Bind("Hotkeys", "UnstoreKey", Key.F6, "Hotkey to unstore the first item in storage and enter build mode.");

            Security.Init();

            harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll();
            PatchRpcSenderTracking();

            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave;

            Log.LogInfo($"Plugin {PLUGIN_NAME} v{PLUGIN_VERSION} is loaded!");
        }

        private void OnDestroy()
        {
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave;
        }

        private static void OnLobbyMemberJoined(Lobby lobby, Friend friend)
        {
            ulong id = friend.Id.Value;
            Security.PendingJoins[id] = Time.realtimeSinceStartup;
            Log.LogInfo($"AntiHacker: lobby join {id}" + (Security.IsBanned(id) ? " (BANNED)" : ""));
        }

        private static void OnLobbyMemberLeave(Lobby lobby, Friend friend)
        {
            Security.PendingJoins.Remove(friend.Id.Value);
        }

        // ---- Tracking which client sent the RPC currently being executed ----

        private void PatchRpcSenderTracking()
        {
            try
            {
                Type helpers = AccessTools.TypeByName("Unity.Netcode.RpcMessageHelpers");
                MethodInfo handle = AccessTools.Method(helpers, "Handle");
                harmony.Patch(handle,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(HandlePrefix)),
                    finalizer: new HarmonyMethod(typeof(Plugin), nameof(HandleFinalizer)));
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to patch RpcMessageHelpers.Handle: {ex}");
            }
        }

        private static void HandlePrefix(ref __RpcParams rpcParams)
        {
            try
            {
                Security.CurrentSender = rpcParams.Server.Receive.SenderClientId;
            }
            catch
            {
                Security.CurrentSender = null;
            }
        }

        private static Exception HandleFinalizer(Exception __exception)
        {
            Security.CurrentSender = null;
            return __exception;
        }

        // ---- Recovery hotkey (host only) ----

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || StartOfRound.Instance == null) return;
            if (kb[RecoveryKey.Value].wasPressedThisFrame)
            {
                RepairShip();
            }
        }

        public static string RepairShip()
        {
            var sor = StartOfRound.Instance;
            if (sor == null || !sor.IsHost) return "Only the host can repair the ship.";

            if (!sor.travellingToNewLevel)
            {
                Log.LogInfo("AntiHacker: ship is not stuck travelling, nothing to repair.");
                return "Ship is not stuck; nothing to repair.";
            }

            Log.LogWarning("AntiHacker: clearing stuck travellingToNewLevel flag");
            sor.travellingToNewLevel = false;

            var lever = UnityEngine.Object.FindObjectOfType<StartMatchLever>();
            if (lever != null && lever.triggerScript != null) lever.triggerScript.interactable = true;

            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (terminal != null) terminal.useCreditsCooldown = false;
            return "Ship repaired: travel lock cleared, lever re-enabled.";
        }

        // ---- MoreButtons Integration ----


        private static void ShowRotationTip()
        {
            if (HUDManager.Instance == null) return;
            // Same bottom-right popup used by the remote hack. Saved once unless RotationTipEveryTime is on.
            bool once = !Instance.RotationTipEveryTime.Value;
            HUDManager.Instance.DisplayTip(
                "MOVING OBJECTS",
                "Arrow keys: rotate 90 degrees\nEnd: reset rotation (fixes flipped objects)",
                false, once, "LC_AntiHackerRotateTip");
        }
        public static void DoUpdate()
        {
            if (Keyboard.current == null)
                return;

            if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null)
                return;

            var localPlayer = GameNetworkManager.Instance.localPlayerController;
            if (localPlayer.isTypingChat || localPlayer.inTerminalMenu || localPlayer.isPlayerDead)
                return;

            if (cachedTerminalPlaceable == null && StartOfRound.Instance != null && StartOfRound.Instance.unlockablesList != null)
            {
                int terminalUnlockableID = -1;
                for (int i = 0; i < StartOfRound.Instance.unlockablesList.unlockables.Count; i++)
                {
                    if (StartOfRound.Instance.unlockablesList.unlockables[i].unlockableName == "Terminal")
                    {
                        terminalUnlockableID = i;
                        break;
                    }
                }

                if (terminalUnlockableID != -1)
                {
                    foreach (var p in Resources.FindObjectsOfTypeAll<PlaceableShipObject>())
                    {
                        if (p.unlockableID == terminalUnlockableID)
                        {
                            cachedTerminalPlaceable = p;
                            break;
                        }
                    }
                }
                
                if (cachedTerminalPlaceable != null && !defaultTerminalSet)
                {
                    defaultTerminalPos = cachedTerminalPlaceable.parentObject.transform.position;
                    defaultTerminalRot = cachedTerminalPlaceable.parentObject.transform.eulerAngles;
                    defaultTerminalSet = true;
                    Log.LogInfo($"Terminal default position recorded: {defaultTerminalPos} | Rotation: {defaultTerminalRot}");
                }
            }

            // F5 Teleport (Disabled by default)
            try 
            {
                if (Instance.EnableTeleport.Value && Keyboard.current[Instance.TeleportKey.Value].wasPressedThisFrame)
                {
                    ShipTeleporter[] teleporters = UnityEngine.Object.FindObjectsOfType<ShipTeleporter>();
                    Log.LogInfo($"F5 triggered! Found {teleporters.Length} teleporters.");
                    
                    if (localPlayer.isInHangarShipRoom)
                    {
                        if (RoundManager.Instance.insideAINodes != null && RoundManager.Instance.insideAINodes.Length > 0)
                        {
                            Vector3 targetPos = RoundManager.Instance.insideAINodes[UnityEngine.Random.Range(0, RoundManager.Instance.insideAINodes.Length)].transform.position;
                            
                            localPlayer.isInElevator = false;
                            localPlayer.isInHangarShipRoom = false;
                            localPlayer.isInsideFactory = true;
                            localPlayer.averageVelocity = 0.0f;
                            localPlayer.velocityLastFrame = Vector3.zero;
                            localPlayer.TeleportPlayer(targetPos);
                            
                            // Fix Audio and Weather effects
                            var audioReverbs = UnityEngine.Object.FindObjectOfType<AudioReverbPresets>();
                            if (audioReverbs != null && audioReverbs.audioPresets.Length > 2)
                            {
                                audioReverbs.audioPresets[2].ChangeAudioReverbForPlayer(localPlayer);
                            }

                            // Fix Lightning Strikes
                            for (int i = 0; i < localPlayer.ItemSlots.Length; i++)
                            {
                                if (localPlayer.ItemSlots[i] != null)
                                {
                                    localPlayer.ItemSlots[i].isInFactory = true;
                                    localPlayer.ItemSlots[i].isInShipRoom = false;
                                }
                            }
                            if (localPlayer.ItemOnlySlot != null)
                            {
                                localPlayer.ItemOnlySlot.isInFactory = true;
                                localPlayer.ItemOnlySlot.isInShipRoom = false;
                            }
                            
                            Log.LogInfo("Standalone Inverse Teleport triggered and synced!");
                        }
                        else
                        {
                            Log.LogWarning("No AI nodes available yet to teleport to!");
                        }
                    }
                    else
                    {
                        var audioReverbs = UnityEngine.Object.FindObjectOfType<AudioReverbPresets>();
                        if (audioReverbs != null && audioReverbs.audioPresets.Length > 3)
                        {
                            audioReverbs.audioPresets[3].ChangeAudioReverbForPlayer(localPlayer);
                        }

                        localPlayer.isInElevator = true;
                        localPlayer.isInHangarShipRoom = true;
                        localPlayer.isInsideFactory = false;
                        localPlayer.averageVelocity = 0.0f;
                        localPlayer.velocityLastFrame = Vector3.zero;

                        Vector3 shipTeleportPos = StartOfRound.Instance.playerSpawnPositions[0].position;
                        localPlayer.TeleportPlayer(shipTeleportPos, true, 160f);

                        for (int i = 0; i < localPlayer.ItemSlots.Length; i++)
                        {
                            if (localPlayer.ItemSlots[i] != null)
                            {
                                localPlayer.ItemSlots[i].isInFactory = false;
                                localPlayer.ItemSlots[i].isInShipRoom = true;
                            }
                        }
                        if (localPlayer.ItemOnlySlot != null)
                        {
                            localPlayer.ItemOnlySlot.isInFactory = false;
                            localPlayer.ItemOnlySlot.isInShipRoom = true;
                        }

                        Log.LogInfo("Standalone Regular Teleport triggered and synced!");
                    }
                }
            } 
            catch (Exception ex) 
            {
                Log.LogError($"Error during F5 check: {ex}");
            }

            // Light switch
            if (Keyboard.current[Instance.LightSwitchKey.Value].wasPressedThisFrame)
            {
                ShipLights shipLights = UnityEngine.Object.FindObjectOfType<ShipLights>();
                if (shipLights != null)
                {
                    shipLights.ToggleShipLights();
                    Log.LogInfo("Ship lights toggled!");
                }
            }

            // Move Terminal
            if (Keyboard.current[Instance.MoveTerminalKey.Value].wasPressedThisFrame)
            {
                Log.LogInfo("F8 (MoveTerminalKey) was pressed!");

                if (cachedTerminalPlaceable == null)
                {
                    Log.LogError("Could not find PlaceableShipObject for Terminal!");
                }
                else
                {
                    Log.LogInfo($"Found terminalPlaceable with unlockableID: {cachedTerminalPlaceable.unlockableID}");
                    int unlockableID = cachedTerminalPlaceable.unlockableID;
                    UnlockableItem unlockable = StartOfRound.Instance.unlockablesList.unlockables[unlockableID];
                    unlockable.canBeStored = true;

                    bool isStored = unlockable.inStorage || cachedTerminalPlaceable.parentObject.transform.position.y < -100f;

                    if (!isStored)
                    {
                        Log.LogInfo("Terminal is NOT in storage. Storing it via teleportation...");
                        if (ShipBuildModeManager.Instance.InBuildMode)
                        {
                            ShipBuildModeManager.Instance.CancelBuildMode();
                        }
                        
                        ShipBuildModeManager.Instance.PlaceShipObjectServerRpc(
                            new Vector3(0, -500f, 0),
                            Vector3.zero,
                            cachedTerminalPlaceable.parentObject.GetComponent<NetworkObject>(), 
                            (int)localPlayer.playerClientId
                        );
                        unlockable.inStorage = true;
                        cachedTerminalPlaceable.parentObject.disableObject = true;
                    }
                    else
                    {
                        Log.LogInfo("Terminal is in storage. Unstoring it and entering placement mode...");
                        StartOfRound.Instance.ReturnUnlockableFromStorageServerRpc(unlockableID);
                        unlockable.inStorage = false;
                        cachedTerminalPlaceable.parentObject.disableObject = false;

                        ShipBuildModeManager.Instance.CancelBuildMode();
                        
                        if (!float.IsNaN(defaultTerminalRot.x) && !float.IsNaN(defaultTerminalRot.y) && !float.IsNaN(defaultTerminalRot.z) &&
                            !float.IsInfinity(defaultTerminalRot.x) && !float.IsInfinity(defaultTerminalRot.y) && !float.IsInfinity(defaultTerminalRot.z))
                        {
                            cachedTerminalPlaceable.parentObject.transform.eulerAngles = defaultTerminalRot;
                            if (cachedTerminalPlaceable.mainMesh != null)
                            {
                                cachedTerminalPlaceable.mainMesh.transform.eulerAngles = defaultTerminalRot;
                            }
                        }
                        else 
                        {
                            Log.LogWarning($"Skipped rotation reset because defaultTerminalRot was invalid: {defaultTerminalRot}");
                        }
                        
                        typeof(ShipBuildModeManager).GetField("placingObject", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(ShipBuildModeManager.Instance, cachedTerminalPlaceable);
                        
                        var colliders = cachedTerminalPlaceable.parentObject.GetComponentsInChildren<Collider>();
                        typeof(ShipBuildModeManager).GetField("collidersInPlacingObject", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(ShipBuildModeManager.Instance, colliders);
                        
                        for (int index = 0; index < colliders.Length; ++index)
                        {
                            if (!colliders[index].CompareTag("DoNotSet") && !colliders[index].CompareTag("InteractTrigger"))
                                colliders[index].enabled = false;
                        }
                        
                        ShipBuildModeManager.Instance.InBuildMode = true;
                        typeof(ShipBuildModeManager).GetMethod("CreateGhostObjectAndHighlight", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ShipBuildModeManager.Instance, null);
                    }
                }
            }

            // Unstore Item
            if (Keyboard.current[Instance.UnstoreKey.Value].wasPressedThisFrame)
            {
                Log.LogInfo("UnstoreKey was pressed!");
                int foundUnlockableID = -1;
                for (int i = 0; i < StartOfRound.Instance.unlockablesList.unlockables.Count; i++)
                {
                    var unlock = StartOfRound.Instance.unlockablesList.unlockables[i];
                    if (unlock.inStorage)
                    {
                        foundUnlockableID = i;
                        break;
                    }
                }

                if (foundUnlockableID != -1)
                {
                    Log.LogInfo($"Found stored item with ID {foundUnlockableID}. Unstoring and entering build mode...");
                    StartOfRound.Instance.ReturnUnlockableFromStorageServerRpc(foundUnlockableID);
                    var unlockable = StartOfRound.Instance.unlockablesList.unlockables[foundUnlockableID];
                    unlockable.inStorage = false;

                    GameNetworkManager.Instance.StartCoroutine(WaitAndEnterBuildMode(foundUnlockableID));
                }
                else
                {
                    Log.LogInfo("No items found in storage to unstore.");
                }
            }

            // Rotate ghost object in build mode
            if (ShipBuildModeManager.Instance != null && ShipBuildModeManager.Instance.InBuildMode && ShipBuildModeManager.Instance.ghostObject != null)
            {
                if (Keyboard.current[Key.UpArrow].wasPressedThisFrame)
                {
                    ShipBuildModeManager.Instance.ghostObject.eulerAngles += new Vector3(90f, 0f, 0f);
                    Log.LogInfo("Rotated ghostObject +90 on X axis");
                }
                if (Keyboard.current[Key.DownArrow].wasPressedThisFrame)
                {
                    ShipBuildModeManager.Instance.ghostObject.eulerAngles += new Vector3(-90f, 0f, 0f);
                    Log.LogInfo("Rotated ghostObject -90 on X axis");
                }
                if (Keyboard.current[Key.RightArrow].wasPressedThisFrame)
                {
                    ShipBuildModeManager.Instance.ghostObject.eulerAngles += new Vector3(0f, 0f, 90f);
                    Log.LogInfo("Rotated ghostObject +90 on Z axis");
                }
                if (Keyboard.current[Key.LeftArrow].wasPressedThisFrame)
                {
                    ShipBuildModeManager.Instance.ghostObject.eulerAngles += new Vector3(0f, 0f, -90f);
                    Log.LogInfo("Rotated ghostObject -90 on Z axis");
                }
                
                if (Keyboard.current[Key.End].wasPressedThisFrame)
                {
                    PlaceableShipObject placingObj = (PlaceableShipObject)typeof(ShipBuildModeManager).GetField("placingObject", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ShipBuildModeManager.Instance);
                    if (placingObj != null)
                    {
                        UnlockableItem unlockable = StartOfRound.Instance.unlockablesList.unlockables[placingObj.unlockableID];
                        Vector3 defaultRot = Vector3.zero;
                        if (!unlockable.spawnPrefab)
                        {
                            defaultRot = new Vector3(270f, 0f, 0f);
                        }
                        else if (unlockable.prefabObject != null)
                        {
                            PlaceableShipObject pso = unlockable.prefabObject.GetComponentInChildren<PlaceableShipObject>();
                            if (pso != null && pso.mainMesh != null)
                            {
                                defaultRot = pso.mainMesh.transform.localEulerAngles;
                            }
                        }
                        ShipBuildModeManager.Instance.ghostObject.eulerAngles = defaultRot;
                        Log.LogInfo("Reset ghostObject rotation to default via End key");
                    }
                }
            }
        }

        private static IEnumerator WaitAndEnterBuildMode(int unlockableID)
        {
            PlaceableShipObject foundPlaceable = null;
            float timeout = 2.0f;
            while (timeout > 0f)
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<PlaceableShipObject>())
                {
                    if (p != null && p.parentObject != null && p.unlockableID == unlockableID)
                    {
                        var netObj = p.parentObject.GetComponent<NetworkObject>();
                        if (netObj != null && netObj.IsSpawned)
                        {
                            foundPlaceable = p;
                            break;
                        }
                    }
                }
                
                if (foundPlaceable != null)
                    break;
                    
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (foundPlaceable != null)
            {
                foundPlaceable.parentObject.disableObject = false;

                if (ShipBuildModeManager.Instance.InBuildMode)
                {
                    ShipBuildModeManager.Instance.CancelBuildMode();
                }

                typeof(ShipBuildModeManager).GetField("placingObject", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(ShipBuildModeManager.Instance, foundPlaceable);
                var colliders = foundPlaceable.parentObject.GetComponentsInChildren<Collider>();
                typeof(ShipBuildModeManager).GetField("collidersInPlacingObject", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(ShipBuildModeManager.Instance, colliders);
                
                for (int index = 0; index < colliders.Length; ++index)
                {
                    if (!colliders[index].CompareTag("DoNotSet") && !colliders[index].CompareTag("InteractTrigger"))
                        colliders[index].enabled = false;
                }
                
                ShipBuildModeManager.Instance.InBuildMode = true;
                typeof(ShipBuildModeManager).GetMethod("CreateGhostObjectAndHighlight", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ShipBuildModeManager.Instance, null);
            }
            else
            {
                Log.LogError($"Could not find PlaceableShipObject for un-stored item ID {unlockableID} after waiting!");
            }
        }
    }

    [HarmonyPatch(typeof(GameNetcodeStuff.PlayerControllerB))]
    internal static class PlayerControllerBPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void UpdatePostfix(GameNetcodeStuff.PlayerControllerB __instance)
        {
            if (__instance == GameNetworkManager.Instance.localPlayerController)
            {
                Plugin.DoUpdate();
            }
        }
    }
}
