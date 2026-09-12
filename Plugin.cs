using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AntiHacker
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.invertigo.antihacker";
        public const string PLUGIN_NAME = "AntiHacker";
        public const string PLUGIN_VERSION = "1.0.0";

        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        private Harmony harmony;

        public ConfigEntry<bool> PersistKickList;
        public ConfigEntry<bool> AppearInVanilla;

        private void Awake()
        {
            Instance = this;
            Log = base.Logger;

            PersistKickList = Config.Bind("General", "PersistKickList", true, "If true, the kick list will persist across game sessions. If false, it will reset every time you close the game.");
            AppearInVanilla = Config.Bind("General", "AppearInVanilla", true, "If true, your server will appear in the vanilla server list instead of the modded one.");

            harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll();

            Log.LogInfo($"Plugin {PLUGIN_NAME} is loaded!");
            
            // Temporary dump for testing
            DumpFacepunchTransport();
        }

        private void DumpFacepunchTransport()
        {
            try 
            {
                var t = typeof(Netcode.Transports.Facepunch.FacepunchTransport);
                Log.LogInfo("Dumping FacepunchTransport fields:");
                foreach (var field in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)) 
                {
                    Log.LogInfo($"Field: {field.Name} - {field.FieldType.Name}");
                    if (field.Name == "connectedClients")
                    {
                        var args = field.FieldType.GetGenericArguments();
                        Log.LogInfo($"  Generic args: {args[0].Name}, {args[1].Name}");
                        
                        Log.LogInfo("Dumping Connection fields:");
                        foreach (var cfield in args[1].GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            Log.LogInfo($"  Conn Field: {cfield.Name} - {cfield.FieldType.Name}");
                        }
                        foreach (var cprop in args[1].GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            Log.LogInfo($"  Conn Prop: {cprop.Name} - {cprop.PropertyType.Name}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Dump failed: {ex}");
            }
        }
    }
}
