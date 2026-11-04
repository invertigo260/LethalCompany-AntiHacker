using System;
using System.Reflection;
using Dissonance;
using Dissonance.Audio.Codecs;
using Dissonance.Integrations.Unity_NFGO;
using Dissonance.Networking;
using Dissonance.Networking.Server;
using MonoMod.RuntimeDetour;
using Unity.Netcode;

namespace AntiHacker.Patches
{
    /// <summary>
    /// Voice chat sizes its decoder buffers from the codec settings each player sends, and the host passes them on to
    /// everyone (adapted from LobbyGuard). The game only ever sends Opus at 48 kHz with one of Opus's frame sizes, or
    /// Dissonance's fixed Identity settings, so the host drops anything else (or anything cut short) and punishes the sender.
    /// </summary>
    internal static class VoiceGuard
    {
        private static readonly int[] opusFrameSizes = { 120, 240, 480, 960, 1920, 2880 };

        private delegate void Orig(BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader);

        private static Hook? handshake;
        private static Hook? state;

        public static void Hook()
        {
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                Type clients = typeof(BroadcastingClientCollection<NfgoConn>);
                handshake = new Hook(
                    clients.GetMethod(nameof(BroadcastingClientCollection<NfgoConn>.ProcessHandshakeRequest), any),
                    typeof(VoiceGuard).GetMethod(nameof(Handshake), any));
                state = new Hook(
                    clients.GetMethod(nameof(BroadcastingClientCollection<NfgoConn>.ProcessClientState), any),
                    typeof(VoiceGuard).GetMethod(nameof(State), any));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook Dissonance voice handlers: {ex}");
            }
        }

        public static bool Allowed(byte codec, uint frameSize, int sampleRate) => codec switch
        {
            (byte)Codec.Opus => sampleRate == 48000 && Array.IndexOf(opusFrameSizes, (int)frameSize) >= 0,
            (byte)Codec.Identity => sampleRate == 44100 && frameSize == 441,
            _ => false,
        };

        private static void Handshake(Orig orig, BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader)
        {
            PacketReader copy = reader;
            if (Check(source, () =>
            {
                copy.ReadHandshakeRequest(out _, out CodecSettings settings);
                return settings;
            }))
            {
                orig(self, source, ref reader);
            }
        }

        private static void State(Orig orig, BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader)
        {
            PacketReader copy = reader;
            if (Check(source, () => copy.ReadClientStateHeader().CodecSettings))
            {
                orig(self, source, ref reader);
            }
        }

        private static bool Check(NfgoConn source, Func<CodecSettings> read)
        {
            string why;
            try
            {
                CodecSettings settings = read();
                if (Allowed((byte)settings.Codec, settings.FrameSize, settings.SampleRate))
                {
                    return true;
                }
                why = "bad voice codec settings";
            }
            catch (Exception)
            {
                why = "broken voice packet";
            }

            NetworkManager? manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer && source.ClientId != manager.LocalClientId)
            {
                Security.Punish(source.ClientId, why);
            }
            else
            {
                Plugin.Log.LogWarning($"Dropped a voice packet from this game: {why}");
            }
            return false;
        }
    }
}

