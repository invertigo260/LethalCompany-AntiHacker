using System;
using HarmonyLib;
using Unity.Netcode;

namespace AntiHacker.Patches
{
    [HarmonyPatch(typeof(GameNetworkManager))]
    internal class NetworkPatch
    {
        private static void Reject(NetworkManager.ConnectionApprovalResponse response, string reason)
        {
            response.Reason = reason;
            response.CreatePlayerObject = false;
            response.Approved = false;
            response.Pending = false;
        }

        [HarmonyPatch("ConnectionApproval")]
        [HarmonyPrefix]
        static bool ConnectionApprovalPrefix(GameNetworkManager __instance, NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            try
            {
                if (!NetworkManager.Singleton.IsHost) return true;

                ulong clientId = request.ClientNetworkId;
                if (clientId == NetworkManager.Singleton.LocalClientId) return true;

                // Steam is disabled (LAN): nothing to verify.
                if (__instance.disableSteam) return true;

                Security.PurgePending();

                // 1. The payload is "version,steamId,..." and is fully attacker-controlled.
                ulong payloadId = 0;
                if (request.Payload != null && request.Payload.Length > 0)
                {
                    string[] parts = System.Text.Encoding.ASCII.GetString(request.Payload).Split(',');
                    if (parts.Length >= 2) ulong.TryParse(parts[1], out payloadId);
                }

                // 2. Get the cryptographically verified Steam ID directly from the Facepunch/SteamNetworkingSockets connection.
                ulong socketId = Security.SteamIdOf(NetworkManager.Singleton, clientId);

                // 3. Fallback to the Steam lobby join event if needed.
                ulong realId = socketId;
                bool payloadIsPending = payloadId != 0 && Security.PendingJoins.ContainsKey(payloadId);
                if (realId == 0)
                {
                    if (payloadIsPending)
                    {
                        realId = payloadId;
                    }
                    else if (Security.PendingJoins.Count == 1)
                    {
                        foreach (var kv in Security.PendingJoins) realId = kv.Key;
                    }
                }

                Plugin.Log.LogInfo($"AntiHacker: approval clientId={clientId} payloadId={payloadId} socketId={socketId} realId={realId} pending={Security.PendingJoins.Count}");

                bool kickedEarlier = StartOfRound.Instance != null &&
                    ((realId != 0 && StartOfRound.Instance.KickedClientIds.Contains(realId)) ||
                     (payloadId != 0 && StartOfRound.Instance.KickedClientIds.Contains(payloadId)));

                // Banned (either the claimed or the real identity, or in StartOfRound.KickedClientIds).
                if (Security.IsBanned(payloadId) || Security.IsBanned(realId) || kickedEarlier)
                {
                    Plugin.Log.LogWarning($"Blocked banned/kicked player. payload={payloadId} real={realId}");
                    Security.PendingJoins.Remove(realId);
                    Reject(response, "You have been permanently kicked from this lobby.");
                    return false;
                }

                // Direct socket proof: if SteamNetworkingSockets proved who is on this connection (socketId != 0)
                // and their payload claims a different Steam ID, they are 100% spoofing.
                if (socketId != 0 && payloadId != 0 && payloadId != socketId)
                {
                    Plugin.Log.LogWarning($"Hacker detected! Socket Steam ID {socketId} claimed spoofed payload Steam ID {payloadId}.");
                    Security.Ban(socketId, $"Spoofed Steam ID {payloadId}");
                    Security.PendingJoins.Remove(socketId);
                    Reject(response, "Spoofed Steam ID detected. Connection rejected.");
                    return false;
                }

                // Fallback lobby check: the claimed ID is not who actually joined the lobby.
                if (socketId == 0 && !payloadIsPending && Security.PendingJoins.Count > 0)
                {
                    Plugin.Log.LogWarning($"Hacker detected! Claimed Steam ID {payloadId} did not join the lobby (real: {realId}).");
                    if (realId != 0) Security.Ban(realId, $"Spoofed Steam ID {payloadId}");
                    Security.PendingJoins.Remove(realId);
                    Reject(response, "Spoofed Steam ID detected. Connection rejected.");
                    return false;
                }

                // Payload ID already belongs to a connected client (duplicate/impersonation).
                if (payloadId != 0 && Security.ClientSteamIds.ContainsValue(payloadId))
                {
                    foreach (var kv in Security.ClientSteamIds)
                    {
                        if (kv.Value == payloadId && NetworkManager.Singleton.ConnectedClients.ContainsKey(kv.Key))
                        {
                            Plugin.Log.LogWarning($"Duplicate Steam ID {payloadId} already connected as client {kv.Key}.");
                            if (realId != 0 && realId != payloadId) Security.Ban(realId, "Impersonating connected player " + payloadId);
                            Reject(response, "Duplicate connection rejected.");
                            return false;
                        }
                    }
                }

                // Approved: remember who this clientId really is.
                ulong idToRecord = realId != 0 ? realId : payloadId;
                if (idToRecord != 0)
                {
                    Security.ClientSteamIds[clientId] = idToRecord;
                    Security.PendingJoins.Remove(idToRecord);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in ConnectionApprovalPrefix: {ex}");
            }

            return true;
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
                if (GameNetworkManager.Instance.disableSteam) return;

                var player = __instance.allPlayerScripts[playerObjToKick];
                ulong steamId = Security.ResolveSteamId(player.actualClientId);
                if (steamId == 0) steamId = player.playerSteamId;

                if (steamId > 0) Security.Ban(steamId, "Kicked by host");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error capturing kicked player: {ex}");
            }
        }
    }
}
