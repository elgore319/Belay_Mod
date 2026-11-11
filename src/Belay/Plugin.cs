using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Belay.Net;
using Belay.Players;
using Belay.Revive;
using Belay.Rope;
using Belay.Run;
using Belay.UI;
using Belay.World;
using HarmonyLib;
using UnityEngine;

namespace Belay
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "elgore319.belay";
        public const string Name = "Belay";
        public const string Version = "0.5.6";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<float> DefaultRopeLength;
        public static ConfigEntry<bool> ShowRope;
        public static ConfigEntry<bool> RopeOnByDefault;
        public static ConfigEntry<KeyCode> RopePullKey;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<KeyCode> ReviveKey;
        public static ConfigEntry<KeyCode> SpectateNextKey;
        public static ConfigEntry<KeyCode> VoteYesKey;
        public static ConfigEntry<KeyCode> VoteNoKey;
        public static ConfigEntry<bool> AutoUpdate;
        public static ConfigEntry<string> UpdateRepo;
        public static ConfigEntry<KeyCode> BotPovKey;
        public static ConfigEntry<float> BotFollowDelay;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F7, "Opens the Belay multiplayer window.");
            DefaultRopeLength = Config.Bind("Rope", "DefaultRopeLength", 12f, "Rope length (meters) used when you host a lobby.");
            RopeOnByDefault = Config.Bind("Rope", "RopeOnByDefault", false, "Turn the rope on when you host a lobby (you can switch it in Rules).");
            RopePullKey = Config.Bind("Rope", "RopePullKey", KeyCode.V, "Hold while gripping a wall to haul up the teammates roped below you.");
            ShowRope = Config.Bind("Rope", "ShowRope", true, "Draw the rope between you and your teammates.");
            ReviveKey = Config.Bind("Revive", "ReviveKey", KeyCode.G, "Press at a revive station to revive a dead teammate.");
            VoteYesKey = Config.Bind("Votes", "VoteYesKey", KeyCode.F1, "Vote yes when a teammate wants to take a shortcut.");
            VoteNoKey = Config.Bind("Votes", "VoteNoKey", KeyCode.F2, "Vote no when a teammate wants to take a shortcut.");
            SpectateNextKey = Config.Bind("Revive", "SpectateNextKey", KeyCode.Tab, "While dead, switch which teammate you watch.");
            SendRate = Config.Bind("Network", "SendRate", 20, "Player state updates sent per second.");
            AutoUpdate = Config.Bind("Updates", "AutoUpdate", true, "Download new Belay releases on launch. They apply the next time the game starts.");
            BotPovKey = Config.Bind("Test Bot", "PovWindowKey", KeyCode.F8, "Shows or hides the test bot's point-of-view window.");
            BotFollowDelay = Config.Bind("Test Bot", "FollowDelay", 2f, "Seconds a following test bot trails behind you (each extra bot adds this again).");
            UpdateRepo = Config.Bind("Updates", "UpdateRepo", "elgore319/Belay_Mod", "GitHub repo (owner/name) whose releases Belay updates from.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Patches.LevelRandomPatches.Apply(harmony);

            // BepInEx's manager object survives scene loads, so our components live on it too.
            gameObject.AddComponent<NetSession>();
            gameObject.AddComponent<PlayerSync>();
            gameObject.AddComponent<Tether>();
            gameObject.AddComponent<RunFlow>();
            gameObject.AddComponent<GooSync>();
            gameObject.AddComponent<DoorSync>();
            gameObject.AddComponent<ButtonSync>();
            gameObject.AddComponent<InteractionSync>();
            gameObject.AddComponent<ItemSync>();
            gameObject.AddComponent<ShortcutSync>();
            gameObject.AddComponent<ZoneSync>();
            gameObject.AddComponent<LevelCheck>();
            gameObject.AddComponent<ReviveSystem>();
            gameObject.AddComponent<BelayMenu>();
            gameObject.AddComponent<Updater>();
            gameObject.AddComponent<Bot.TestBot>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
