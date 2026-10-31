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

                // The payload is "version,steamId,..." and is fully attacker controlled.
                ulong payloadId = 0;
                string[] parts = System.Text.Encoding.ASCII.GetString(request.Payload).Split(',');
                if (parts.Length >= 2) ulong.TryParse(parts[1], out payloadId);

                // The real ID comes from the Steam lobby join event, which cannot be spoofed.
                ulong realId = 0;
                bool payloadIsPending = payloadId != 0 && Security.PendingJoins.ContainsKey(payloadId);
                if (payloadIsPending)
                {
                    realId = payloadId;
                }
                else if (Security.PendingJoins.Count == 1)
                {
                    foreach (var kv in Security.PendingJoins) realId = kv.Key;
                }

                Plugin.Log.LogInfo($"AntiHacker: approval clientId={clientId} payloadId={payloadId} realId={realId} pending={Security.PendingJoins.Count}");

                // Banned (either the claimed or the real identity).
                if (Security.IsBanned(payloadId) || Security.IsBanned(realId))
                {
                    Plugin.Log.LogWarning($"Blocked banned player. payload={payloadId} real={realId}");
                    Security.PendingJoins.Remove(realId);
                    Reject(response, "You have been permanently kicked from this lobby.");
                    return false;
                }

                // The claimed ID is not who actually joined the lobby: spoofing.
                if (!payloadIsPending && Security.PendingJoins.Count > 0)
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
                if (realId != 0)
                {
                    Security.ClientSteamIds[clientId] = realId;
                    Security.PendingJoins.Remove(realId);
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
                ulong steamId = player.playerSteamId;

                // Prefer the real (lobby-verified) ID over whatever the client claimed.
                if (Security.ClientSteamIds.TryGetValue(player.actualClientId, out ulong realId)) steamId = realId;

                if (steamId > 0) Security.Ban(steamId, "Kicked by host");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error capturing kicked player: {ex}");
            }
        }
    }
}
