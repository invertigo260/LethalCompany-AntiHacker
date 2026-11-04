using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using UnityEngine;

namespace AntiHacker.Patches
{
    /// <summary>
    /// Low-level NGO packet batch validator and flood limiter (adapted from LobbyGuard).
    /// Hooks NetworkConnectionManager.DataEventHandler BEFORE NetworkMessageManager.HandleIncomingData
    /// so malformed headers (such as uint32 MessageSize >= 0x80000000 overflowing to negative int32
    /// in FastBufferReader / UnsafeUtility.MemCpy) and unknown transportId packets are dropped before
    /// Unity Netcode can execute or crash on them.
    /// </summary>
    internal static class BatchGuard
    {
        private const ushort Magic = 0x1160;
        private const int HeaderSize = 16;

        // Netcode sends a client's packets once per channel per frame, so this leaves room for a very high frame rate.
        private const int MaxPacketsPerSecond = 2000;

        // A packet can hold thousands of tiny messages, and NGO logs an error for each one it can't use.
        private const int MaxMessagesPerSecond = 20000;

        private static readonly Dictionary<ulong, (float Start, int Packets, int Messages)> rates =
            new Dictionary<ulong, (float Start, int Packets, int Messages)>();

        private static Hook? hook;

        private delegate void DataEvent(NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime);

        public static void Hook()
        {
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                hook = new Hook(
                    typeof(NetworkConnectionManager).GetMethod(nameof(NetworkConnectionManager.DataEventHandler), any),
                    typeof(BatchGuard).GetMethod(nameof(OnData), any));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook NetworkConnectionManager.DataEventHandler: {ex}");
            }
        }

        private static void OnData(DataEvent orig, NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime)
        {
            if (!self.LocalClient.IsServer)
            {
                orig(self, transportId, ref payload, receiveTime);
                return;
            }

            // NGO takes data from a transport id it doesn't know as the host's own (ServerClientId = 0).
            // A kicked or unapproved client's packets arrive that way.
            if (!self.TransportIdToClientIdMap.TryGetValue(transportId, out ulong clientId))
            {
                return;
            }

            uint? connectRequest = self.NetworkManager.NetworkConfig.ConnectionApproval
                ? self.MessageManager.GetMessageType(typeof(ConnectionRequestMessage))
                : (uint?)null;

            string? why = Check(payload, Hash, connectRequest);
            bool isFlood = false;
            if (why == null && Flooding(transportId, BitConverter.ToUInt16(payload.Array!, payload.Offset + 2)))
            {
                why = "too many packets or messages";
                isFlood = true;
            }

            if (why == null)
            {
                orig(self, transportId, ref payload, receiveTime);
                return;
            }

            rates.Remove(transportId);
            if (isFlood)
            {
                Security.Disconnect(self.NetworkManager, clientId, why);
            }
            else
            {
                Security.Punish(clientId, $"Malformed Netcode packet ({why})");
            }
        }

        public static unsafe string? Check(ArraySegment<byte> data, Func<ArraySegment<byte>, ulong> hash, uint? connectRequest = null)
        {
            if (data.Array == null || data.Count < HeaderSize)
            {
                return "packet too short";
            }
            fixed (byte* start = data.Array)
            {
                byte* p = start + data.Offset;
                if (*(ushort*)p != Magic || *(int*)(p + 4) != data.Count)
                {
                    return "not a Netcode packet";
                }
                if (hash(new ArraySegment<byte>(data.Array, data.Offset + HeaderSize, data.Count - HeaderSize)) != *(ulong*)(p + 8))
                {
                    return "packet hash doesn't match";
                }
                int pos = HeaderSize;
                for (int i = 0; i < *(ushort*)(p + 2); i++)
                {
                    if (!Packed(p, data.Count, ref pos, out uint type) || !Packed(p, data.Count, ref pos, out uint size))
                    {
                        return "bad message header";
                    }
                    if (size > (uint)(data.Count - pos))
                    {
                        return "message bigger than its packet";
                    }
                    if (type == connectRequest && !ConnectionData(p, pos, pos + (int)size))
                    {
                        return "bad connection request";
                    }
                    pos += (int)size;
                }
            }
            return null;
        }

        private static unsafe bool ConnectionData(byte* p, int pos, int end)
        {
            if (!Packed(p, end, ref pos, out uint zigzag))
            {
                return false;
            }
            int versions = (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
            for (int i = 0; i < versions; i++)
            {
                pos += 4;
                if (pos > end || !Packed(p, end, ref pos, out _))
                {
                    return false;
                }
            }
            if (end - pos < 12)
            {
                return false;
            }
            int length = *(int*)(p + pos + 8);
            return length >= 0 && length <= end - pos - 12;
        }

        private static unsafe bool Packed(byte* p, int length, ref int pos, out uint value)
        {
            value = 0;
            if (pos >= length)
            {
                return false;
            }
            int n = p[pos] & 7;
            if (n == 0 || n > 5 || n > length - pos)
            {
                return false;
            }
            if (n == 5)
            {
                value = p[pos + 1] | (uint)p[pos + 2] << 8 | (uint)p[pos + 3] << 16 | (uint)p[pos + 4] << 24;
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    value |= (uint)p[pos + i] << (8 * i);
                }
                value >>= 3;
            }
            pos += n;
            return true;
        }

        private static unsafe ulong Hash(ArraySegment<byte> body)
        {
            fixed (byte* p = body.Array)
            {
                return XXHash.Hash64(p + body.Offset, body.Count);
            }
        }

        private static bool Flooding(ulong transportId, int messages)
        {
            float now = Time.realtimeSinceStartup;
            (float start, int packets, int total) = rates.TryGetValue(transportId, out (float Start, int Packets, int Messages) rate) &&
                now - rate.Start < 1 ? rate : (now, 0, 0);
            rates[transportId] = (start, ++packets, total += messages);
            return packets > MaxPacketsPerSecond || total > MaxMessagesPerSecond;
        }
    }
}

