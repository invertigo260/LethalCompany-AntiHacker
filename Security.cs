using System;
using System.Collections.Generic;
using System.IO;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

namespace AntiHacker
{
    /// <summary>
    /// Central state: ban list (with built-in blacklist), real Steam IDs verified via
    /// SteamNetworkingSockets.Internal.GetConnectionInfo (plus lobby join events),
    /// and the sender of the RPC that is currently executing.
    /// </summary>
    internal static class Security
    {
        /// <summary>Known hackers, always banned regardless of config.</summary>
        public static readonly ulong[] DefaultBlacklist =
        {
            76561199295933088UL, // spoofed host ID, ChangeLevelServerRpc(9999) + moved ship objects + FastBufferReader crash (2026-10-27 / 2026-11-03)
            76561198638639114UL, // "fleetway" - despawned all 92 ship scrap items via unheld Throw/PlaceObjectServerRpc (2026-10-30)
        };

        private static readonly HashSet<ulong> banned = new HashSet<ulong>();

        // Steam IDs that joined the Steam lobby but have not completed connection approval yet.
        public static readonly Dictionary<ulong, float> PendingJoins = new Dictionary<ulong, float>();

        // Netcode clientId -> real Steam ID (learned from the Steam socket / lobby, not from the spoofable payload).
        public static readonly Dictionary<ulong, ulong> ClientSteamIds = new Dictionary<ulong, ulong>();

        // Netcode clientId of the remote sender of the RPC currently executing (null = none / local).
        public static ulong? CurrentSender;

        private const float PendingLifetime = 120f;

        public static string KickListPath => Path.Combine(Application.persistentDataPath, "AntiHacker_KickList.txt");

        public static void Init()
        {
            banned.Clear();
            foreach (ulong id in DefaultBlacklist) banned.Add(id);

            foreach (string part in Plugin.Instance.ExtraBlacklist.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(part.Trim(), out ulong id)) banned.Add(id);
            }

            try
            {
                if (File.Exists(KickListPath))
                {
                    foreach (string line in File.ReadAllLines(KickListPath))
                    {
                        if (ulong.TryParse(line.Trim(), out ulong id)) banned.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed reading kick list: {ex}");
            }

            Plugin.Log.LogInfo($"AntiHacker: {banned.Count} Steam IDs on the ban list.");
        }

        public static bool IsBanned(ulong steamId)
        {
            return steamId != 0 && banned.Contains(steamId);
        }

        public static void Ban(ulong steamId, string reason)
        {
            if (steamId == 0) return;
            bool added = banned.Add(steamId);
            Plugin.Log.LogWarning($"AUTOBAN {steamId}: {reason}");
            if (!added || !Plugin.Instance.PersistKickList.Value) return;
            try
            {
                File.AppendAllText(KickListPath, steamId + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed writing kick list: {ex}");
            }
        }

        public static void PurgePending()
        {
            List<ulong> stale = null;
            foreach (var kv in PendingJoins)
            {
                if (Time.realtimeSinceStartup - kv.Value > PendingLifetime)
                {
                    if (stale == null) stale = new List<ulong>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null) foreach (ulong id in stale) PendingJoins.Remove(id);
        }

        /// <summary>
        /// Returns the cryptographically verified SteamID that Steam proved for a client's active socket connection
        /// (via SteamNetworkingSockets.Internal.GetConnectionInfo), or 0 off Steam / if unavailable.
        /// Note: FacepunchTransport.Client.steamId is always 0 because ConnectionInfo.Identity is private in Facepunch.
        /// </summary>
        public static ulong SteamIdOf(NetworkManager manager, ulong clientId)
        {
            try
            {
                if (manager == null || manager.ConnectionManager == null) return 0;
                ConnectionInfo info = default;
                return manager.NetworkConfig.NetworkTransport is FacepunchTransport steam &&
                    steam.connectedClients.TryGetValue(manager.ConnectionManager.ClientIdToTransportId(clientId), out FacepunchTransport.Client player) &&
                    SteamNetworkingSockets.Internal.GetConnectionInfo(player.connection, ref info) &&
                    info.identity.IsSteamId
                    ? info.identity.SteamId.Value
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static ulong SteamIdOf(ulong clientId)
        {
            return SteamIdOf(NetworkManager.Singleton, clientId);
        }

        /// <summary>
        /// Resolves the real Steam ID for a clientId using the authenticated Steam socket first,
        /// then cached approval mapping, then player scripts.
        /// </summary>
        public static ulong ResolveSteamId(ulong clientId)
        {
            ulong steamId = SteamIdOf(clientId);
            if (steamId != 0)
            {
                ClientSteamIds[clientId] = steamId;
                return steamId;
            }

            if (ClientSteamIds.TryGetValue(clientId, out steamId) && steamId != 0)
            {
                return steamId;
            }

            if (StartOfRound.Instance != null && StartOfRound.Instance.allPlayerScripts != null)
            {
                foreach (var p in StartOfRound.Instance.allPlayerScripts)
                {
                    if (p != null && p.actualClientId == clientId && p.playerSteamId != 0)
                    {
                        return p.playerSteamId;
                    }
                }
            }

            return 0;
        }

        /// <summary>Disconnect a client without adding them to the permanent ban list (e.g. packet rate flood).</summary>
        public static void Disconnect(NetworkManager manager, ulong clientId, string reason)
        {
            ulong steamId = ResolveSteamId(clientId);
            Plugin.Log.LogWarning($"AntiHacker: Disconnected {(steamId != 0 ? $"Steam user {steamId}" : $"client {clientId}")}: {reason}");
            try
            {
                if (manager != null && manager.IsServer)
                {
                    manager.DisconnectClient(clientId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to disconnect {clientId}: {ex}");
            }
        }

        /// <summary>Ban the sender's real Steam ID and disconnect them.</summary>
        public static void Punish(ulong clientId, string reason)
        {
            Plugin.Log.LogWarning($"HACK ATTEMPT from clientId {clientId}: {reason}");
            ulong steamId = ResolveSteamId(clientId);

            if (steamId != 0)
            {
                Ban(steamId, reason);
            }
            else
            {
                Plugin.Log.LogWarning($"No real Steam ID known for clientId {clientId}; disconnecting only.");
            }

            try
            {
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                {
                    NetworkManager.Singleton.DisconnectClient(clientId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to disconnect {clientId}: {ex}");
            }
        }
    }
}
