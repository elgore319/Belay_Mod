using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Belay.Net;
using Belay.Players;
using Belay.Rope;
using Belay.Run;
using Belay.UI;
using HarmonyLib;
using UnityEngine;

namespace Belay
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "elgore319.belay";
        public const string Name = "Belay";
        public const string Version = "0.2.0";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<float> DefaultRopeLength;
        public static ConfigEntry<bool> ShowRope;
        public static ConfigEntry<int> SendRate;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F7, "Opens the Belay multiplayer window.");
            DefaultRopeLength = Config.Bind("Rope", "DefaultRopeLength", 12f, "Rope length (meters) used when you host a lobby.");
            ShowRope = Config.Bind("Rope", "ShowRope", true, "Draw the rope between you and your teammates.");
            SendRate = Config.Bind("Network", "SendRate", 20, "Player state updates sent per second.");

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            // BepInEx's manager object survives scene loads, so our components live on it too.
            gameObject.AddComponent<NetSession>();
            gameObject.AddComponent<PlayerSync>();
            gameObject.AddComponent<Tether>();
            gameObject.AddComponent<RunFlow>();
            gameObject.AddComponent<LobbyWindow>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
