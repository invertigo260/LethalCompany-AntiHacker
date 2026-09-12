using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;
using System.IO;

namespace AntiHacker.Patches
{
    [HarmonyPatch(typeof(GameNetworkManager))]
    internal class NetworkPatch
    {
        private static string GetKickListPath()
        {
            return Path.Combine(Application.persistentDataPath, "AntiHacker_KickList.txt");
        }

        private static HashSet<ulong> LoadKickList()
        {
            HashSet<ulong> list = new HashSet<ulong>();
            if (Plugin.Instance.PersistKickList.Value)
            {
                string path = GetKickListPath();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    foreach (string line in lines)
                    {
                        if (ulong.TryParse(line, out ulong id))
                        {
                            list.Add(id);
                        }
                    }
                }
            }
            return list;
        }

        private static void SaveToKickList(ulong steamId)
        {
            if (Plugin.Instance.PersistKickList.Value)
            {
                string path = GetKickListPath();
                File.AppendAllText(path, steamId.ToString() + Environment.NewLine);
            }
        }

        [HarmonyPatch("ConnectionApproval")]
        [HarmonyPrefix]
        static bool ConnectionApprovalPrefix(GameNetworkManager __instance, NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            try
            {
                // We only do anti-hacker checks if we are the host.
                if (!NetworkManager.Singleton.IsHost) return true;

                ulong clientId = request.ClientNetworkId;
                
                // Exclude local host client
                if (clientId == NetworkManager.Singleton.LocalClientId) return true;

                // Try to get the real Steam ID from the Facepunch Transport
                ulong realSteamId = 0;
                if (__instance.GetComponent<Netcode.Transports.Facepunch.FacepunchTransport>() is Netcode.Transports.Facepunch.FacepunchTransport fpTransport)
                {
                    FieldInfo clientsField = typeof(Netcode.Transports.Facepunch.FacepunchTransport).GetField("connectedClients", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (clientsField != null)
                    {
                        object clientsDict = clientsField.GetValue(fpTransport);
                        if (clientsDict is IDictionary dict)
                        {
                            Plugin.Log.LogInfo($"AntiHacker: found dict. dict count = {dict.Count}");
                            if (dict.Contains(clientId))
                            {
                                object clientObj = dict[clientId];
                                if (clientObj != null)
                                {
                                    FieldInfo steamIdField = clientObj.GetType().GetField("steamId", BindingFlags.Public | BindingFlags.Instance);
                                    if (steamIdField != null)
                                    {
                                        object steamIdObj = steamIdField.GetValue(clientObj);
                                        Plugin.Log.LogInfo($"AntiHacker: steamIdObj type = {steamIdObj?.GetType().FullName}");
                                        
                                        // Since it's a struct, we can just ToString it or cast to ulong if possible, 
                                        // or use reflection to get the Value property/field!
                                        if (steamIdObj != null)
                                        {
                                            PropertyInfo valueProp = steamIdObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                            FieldInfo valueField = steamIdObj.GetType().GetField("Value", BindingFlags.Public | BindingFlags.Instance);
                                            
                                            if (valueProp != null) {
                                                realSteamId = (ulong)valueProp.GetValue(steamIdObj);
                                            } else if (valueField != null) {
                                                realSteamId = (ulong)valueField.GetValue(steamIdObj);
                                            } else {
                                                // Fallback to parsing ToString() since SteamId struct ToString() returns the number!
                                                ulong.TryParse(steamIdObj.ToString(), out realSteamId);
                                            }
                                        }
                                    }
                                    else Plugin.Log.LogInfo("AntiHacker: steamIdField is null");
                                }
                                else Plugin.Log.LogInfo("AntiHacker: clientObj is null");
                            }
                            else Plugin.Log.LogInfo($"AntiHacker: dict does NOT contain clientId {clientId}");
                        }
                        else Plugin.Log.LogInfo("AntiHacker: clientsDict is NOT IDictionary");
                    }
                    else Plugin.Log.LogInfo("AntiHacker: clientsField is null");
                }
                else Plugin.Log.LogInfo("AntiHacker: fpTransport is null");

                Plugin.Log.LogInfo($"Connection Approval for Client ID {clientId}. Real Steam ID from Transport: {realSteamId}");

                // Read our secure kick list
                HashSet<ulong> kickedIds = LoadKickList();
                
                // Add vanilla kicked IDs just in case they were kicked this session
                if (StartOfRound.Instance != null && StartOfRound.Instance.KickedClientIds != null)
                {
                    foreach (ulong id in StartOfRound.Instance.KickedClientIds)
                    {
                        kickedIds.Add(id);
                    }
                }

                if (realSteamId != 0 && kickedIds.Contains(realSteamId))
                {
                    Plugin.Log.LogWarning($"Blocked connection from banned Steam ID: {realSteamId}");
                    response.Reason = "You have been permanently kicked from this lobby.";
                    response.CreatePlayerObject = false;
                    response.Approved = false;
                    response.Pending = false;
                    return false; // Skip original method
                }
                
                // Check if they are trying to spoof their Steam ID in the payload!
                string payload = System.Text.Encoding.ASCII.GetString(request.Payload);
                string[] strArray = payload.Split(',');
                if (strArray.Length >= 2 && ulong.TryParse(strArray[1], out ulong payloadSteamId))
                {
                    if (realSteamId != 0 && payloadSteamId != realSteamId)
                    {
                        Plugin.Log.LogWarning($"Hacker detected! Spoofed Steam ID {payloadSteamId} doesn't match real Steam ID {realSteamId}!");
                        
                        // We could block them right here just for spoofing!
                        // But we definitely add their real ID to the ban list.
                        SaveToKickList(realSteamId);
                        
                        response.Reason = "Spoofed Steam ID detected. Connection rejected.";
                        response.CreatePlayerObject = false;
                        response.Approved = false;
                        response.Pending = false;
                        return false;
                    }
                }

            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in ConnectionApprovalCallbackPrefix: {ex}");
            }

            return true; // Let original method run
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal class StartOfRoundPatch
    {
        [HarmonyPatch("KickPlayer")]
        [HarmonyPostfix]
        static void KickPlayerPostfix(StartOfRound __instance, int playerObjToKick)
        {
            if (!__instance.IsServer) return;

            try
            {
                // Capture the Steam ID of the player being kicked
                if (!GameNetworkManager.Instance.disableSteam)
                {
                    ulong playerSteamId = __instance.allPlayerScripts[playerObjToKick].playerSteamId;
                    
                    if (playerSteamId > 0)
                    {
                        Plugin.Log.LogInfo($"Player kicked! Adding Real Steam ID to AntiHacker ban list: {playerSteamId}");
                        
                        // Check if it's already in the file to avoid duplicates
                        string path = Path.Combine(Application.persistentDataPath, "AntiHacker_KickList.txt");
                        bool alreadyBanned = false;
                        if (File.Exists(path))
                        {
                            string[] lines = File.ReadAllLines(path);
                            foreach (string line in lines)
                            {
                                if (ulong.TryParse(line, out ulong id) && id == playerSteamId)
                                {
                                    alreadyBanned = true;
                                    break;
                                }
                            }
                        }

                        if (!alreadyBanned && Plugin.Instance.PersistKickList.Value)
                        {
                            File.AppendAllText(path, playerSteamId.ToString() + Environment.NewLine);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error capturing kicked player: {ex}");
            }
        }
    }
}
