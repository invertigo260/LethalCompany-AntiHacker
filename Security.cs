using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

namespace AntiHacker
{
    /// <summary>
    /// Central state: ban list (with built-in blacklist), real Steam IDs learned from
    /// lobby join events, and the sender of the RPC that is currently executing.
    /// </summary>
    internal static class Security
    {
        /// <summary>Known hackers, always banned regardless of config.</summary>
        public static readonly ulong[] DefaultBlacklist =
        {
            76561199295933088UL, // spoofed host ID, ChangeLevelServerRpc(9999) + moved ship objects (2026-10-27)
            76561198638639114UL, // "fleetway" - despawned all 92 ship scrap items via unheld Throw/PlaceObjectServerRpc (2026-10-30)
        };

        private static readonly HashSet<ulong> banned = new HashSet<ulong>();

        // Steam IDs that joined the Steam lobby but have not completed connection approval yet.
        public static readonly Dictionary<ulong, float> PendingJoins = new Dictionary<ulong, float>();

        // Netcode clientId -> real Steam ID (learned from the lobby, not from the spoofable payload).
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

        /// <summary>Ban the sender's real Steam ID and disconnect them.</summary>
        public static void Punish(ulong clientId, string reason)
        {
            Plugin.Log.LogWarning($"HACK ATTEMPT from clientId {clientId}: {reason}");
            if (ClientSteamIds.TryGetValue(clientId, out ulong steamId))
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
