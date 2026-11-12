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
        public const string Version = "0.6.7";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<float> DefaultRopeLength;
        public static ConfigEntry<bool> ShowRope;
        public static ConfigEntry<bool> RopeOnByDefault;
        public static ConfigEntry<KeyCode> RopePullKey;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<KeyCode> SpectateNextKey;
        public static ConfigEntry<KeyCode> VoteYesKey;
        public static ConfigEntry<KeyCode> VoteNoKey;
        public static ConfigEntry<bool> AutoUpdate;
        public static ConfigEntry<string> UpdateRepo;
        public static ConfigEntry<KeyCode> BotPovKey;
        public static ConfigEntry<float> BotFollowDelay;
        public static ConfigEntry<KeyCode> TestClientAutopilotKey;
        public static ConfigEntry<bool> SolidTeammates;
        public static ConfigEntry<bool> SyncProps;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F7, "Opens the Belay multiplayer window.");
            DefaultRopeLength = Config.Bind("Rope", "DefaultRopeLength", 12f, "Rope length (meters) used when you host a lobby.");
            RopeOnByDefault = Config.Bind("Rope", "RopeOnByDefault", false, "Turn the rope on when you host a lobby (you can switch it in Rules).");
            RopePullKey = Config.Bind("Rope", "RopePullKey", KeyCode.V, "Hold while gripping a wall to haul up the teammates roped below you.");
            ShowRope = Config.Bind("Rope", "ShowRope", true, "Draw the rope between you and your teammates.");
            VoteYesKey = Config.Bind("Votes", "VoteYesKey", KeyCode.F1, "Vote yes when a teammate wants to take a shortcut.");
            VoteNoKey = Config.Bind("Votes", "VoteNoKey", KeyCode.F2, "Vote no when a teammate wants to take a shortcut.");
            SpectateNextKey = Config.Bind("Revive", "SpectateNextKey", KeyCode.Tab, "While dead, switch which teammate you watch.");
            SendRate = Config.Bind("Network", "SendRate", 20, "Player state updates sent per second.");
            AutoUpdate = Config.Bind("Updates", "AutoUpdate", true, "Download new Belay releases on launch. They apply the next time the game starts.");
            BotPovKey = Config.Bind("Test Bot", "PovWindowKey", KeyCode.F8, "Shows or hides the test bot's point-of-view window.");
            TestClientAutopilotKey = Config.Bind("Test Bot", "TestClientAutopilotKey", KeyCode.F9, "In the test client window: switch between following you automatically and playing it yourself.");
            BotFollowDelay = Config.Bind("Test Bot", "FollowDelay", 2f, "Seconds the test client window trails behind you when it follows you.");
            SolidTeammates = Config.Bind("Teammates", "SolidTeammates", true, "Teammates are solid: you bump into them, can stand on them, grip them to climb them, and lift them while you stand or hold the wall.");
            SyncProps = Config.Bind("World", "SyncProps", true, "Boxes and other loose physics objects move the same for everyone.");
            UpdateRepo = Config.Bind("Updates", "UpdateRepo", "elgore319/Belay_Mod", "GitHub repo (owner/name) whose releases Belay updates from.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Patches.LevelRandomPatches.Apply(harmony);

            // BepInEx's manager object survives scene loads, so our components live on it too.
            gameObject.AddComponent<NetSession>();
            gameObject.AddComponent<PlayerSync>();
            gameObject.AddComponent<PlayerContact>();
            gameObject.AddComponent<Tether>();
            gameObject.AddComponent<RunFlow>();
            gameObject.AddComponent<GooSync>();
            gameObject.AddComponent<DoorSync>();
            gameObject.AddComponent<ButtonSync>();
            gameObject.AddComponent<InteractionSync>();
            gameObject.AddComponent<ItemSync>();
            gameObject.AddComponent<PlacedSync>();
            gameObject.AddComponent<Dev.DevTools>();
            gameObject.AddComponent<PropSync>();
            gameObject.AddComponent<EnemySync>();
            gameObject.AddComponent<ShortcutSync>();
            gameObject.AddComponent<VoteTerminal>();
            gameObject.AddComponent<ZoneSync>();
            gameObject.AddComponent<LevelCheck>();
            gameObject.AddComponent<ReviveSystem>();
            gameObject.AddComponent<BelayMenu>();
            gameObject.AddComponent<Diagnostics.FocusLog>();
            if (Net.LoopbackLink.LaunchedAsClient)
            {
                // Second copy of the game for solo testing: never self-update, never write saves.
                Bot.TestClient.Install(gameObject, harmony);
            }
            else
            {
                gameObject.AddComponent<Updater>();
            }
            gameObject.AddComponent<Bot.TestBot>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
