using System.Collections.Generic;
using BepInEx.Configuration;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// What a player chose for their climber beyond the gloves: headgear and the colors of the jacket,
    /// headgear, trousers and pack. 0 always means the standard look (jacket and headgear in the
    /// player's lobby color).
    /// </summary>
    public struct Outfit
    {
        public int Headgear, Jacket, Hat, Pants, Pack;

        /// <summary>A lore look (<see cref="LoreLooks"/>); 0 is the standard climber.</summary>
        public int Look;

        /// <summary>In Roach Mode right now (not a choice: follows the binding), drawn as the giant roach.</summary>
        public bool Roach;

        public bool Same(Outfit o)
        {
            return Headgear == o.Headgear && Jacket == o.Jacket && Hat == o.Hat && Pants == o.Pants && Pack == o.Pack && Look == o.Look && Roach == o.Roach;
        }

        public override string ToString()
        {
            return Headgear + "," + Jacket + "," + Hat + "," + Pants + "," + Pack + "," + Look + "," + (Roach ? 1 : 0);
        }

        /// <summary>Reads what <see cref="ToString"/> wrote; anything unknown or out of range becomes standard.</summary>
        public static Outfit Parse(string text)
        {
            var o = new Outfit();
            if (string.IsNullOrEmpty(text))
            {
                return o;
            }
            string[] parts = text.Split(',');
            o.Headgear = Part(parts, 0, Outfits.HeadgearNames.Length);
            o.Jacket = Part(parts, 1, Outfits.Colors.Length);
            o.Hat = Part(parts, 2, Outfits.Colors.Length);
            o.Pants = Part(parts, 3, Outfits.Colors.Length);
            o.Pack = Part(parts, 4, Outfits.Colors.Length);
            o.Look = Part(parts, 5, LoreLooks.Count);
            o.Roach = Part(parts, 6, 2) == 1;
            return o;
        }

        private static int Part(string[] parts, int index, int count)
        {
            return parts.Length > index && int.TryParse(parts[index], out int v) && v >= 0 && v < count ? v : 0;
        }
    }

    /// <summary>
    /// The outfit choices, the local player's pick (saved in the config) and what each teammate wears
    /// (shared as Steam lobby member data, like the hand skins: no packets).
    /// </summary>
    public static class Outfits
    {
        private const string Key = "belay_outfit";

        public static readonly string[] HeadgearNames = { "Helmet", "Beanie", "Hood up", "Balaclava" };

        /// <summary>Names of the lore looks, for the menu.</summary>
        public static readonly string[] LookNames = System.Array.ConvertAll(LoreLooks.All, l => l.Name);

        public struct Swatch
        {
            public string Name;
            public Color Color;
        }

        /// <summary>Index 0 is the standard look; the rest are muted to sit in the game's grimy palette.</summary>
        public static readonly Swatch[] Colors =
        {
            new Swatch { Name = "Standard", Color = Color.clear },
            new Swatch { Name = "Rust", Color = new Color(0.56f, 0.26f, 0.15f) },
            new Swatch { Name = "Maroon", Color = new Color(0.4f, 0.13f, 0.15f) },
            new Swatch { Name = "Hazard orange", Color = new Color(0.78f, 0.4f, 0.1f) },
            new Swatch { Name = "Mustard", Color = new Color(0.66f, 0.52f, 0.18f) },
            new Swatch { Name = "Sand", Color = new Color(0.6f, 0.5f, 0.36f) },
            new Swatch { Name = "Bone", Color = new Color(0.72f, 0.67f, 0.62f) },
            new Swatch { Name = "Olive", Color = new Color(0.36f, 0.38f, 0.22f) },
            new Swatch { Name = "Forest", Color = new Color(0.16f, 0.32f, 0.2f) },
            new Swatch { Name = "Teal", Color = new Color(0.16f, 0.4f, 0.4f) },
            new Swatch { Name = "Slate", Color = new Color(0.3f, 0.36f, 0.42f) },
            new Swatch { Name = "Navy", Color = new Color(0.15f, 0.2f, 0.36f) },
            new Swatch { Name = "Plum", Color = new Color(0.36f, 0.2f, 0.4f) },
            new Swatch { Name = "Pink", Color = new Color(0.7f, 0.4f, 0.5f) },
            new Swatch { Name = "Charcoal", Color = new Color(0.16f, 0.15f, 0.16f) },
        };

        private static ConfigEntry<int> headgear, jacket, hat, pants, pack, look;
        private static string published;
        private static float nextPublish;
        private static readonly Dictionary<ulong, KeyValuePair<float, Outfit>> cache = new Dictionary<ulong, KeyValuePair<float, Outfit>>();

        private static bool Bind()
        {
            if (headgear != null)
            {
                return true;
            }
            if (Plugin.Instance == null)
            {
                return false;
            }
            ConfigFile config = Plugin.Instance.Config;
            headgear = config.Bind("Outfit", "Headgear", 0, "What your climber wears on their head (0 helmet, 1 beanie, 2 hood up, 3 balaclava).");
            jacket = config.Bind("Outfit", "JacketColor", 0, "Jacket color (0 = your lobby color).");
            hat = config.Bind("Outfit", "HeadgearColor", 0, "Helmet or beanie color (0 = your lobby color).");
            pants = config.Bind("Outfit", "TrousersColor", 0, "Trousers color (0 = standard).");
            pack = config.Bind("Outfit", "PackColor", 0, "Backpack color (0 = standard).");
            look = config.Bind("Outfit", "Look", 0, "Lore look (0 climber, 1 Delta Labs researcher, 2 Pipeworks maintenance, 3 pier worker, 4 Habitation scavenger, 5 Chimney expedition, 6 infested, 7 denizen).");
            return true;
        }

        /// <summary>The local player's outfit.</summary>
        public static Outfit Local
        {
            get
            {
                if (!Bind())
                {
                    return default;
                }
                Outfit o = Outfit.Parse(headgear.Value + "," + jacket.Value + "," + hat.Value + "," + pants.Value + "," + pack.Value + "," + look.Value);
                o.Roach = RoachMode.Local;
                return o;
            }
            set
            {
                if (!Bind())
                {
                    return;
                }
                headgear.Value = value.Headgear;
                jacket.Value = value.Jacket;
                hat.Value = value.Hat;
                pants.Value = value.Pants;
                pack.Value = value.Pack;
                look.Value = value.Look;
                nextPublish = 0f;
                cache.Clear();
            }
        }

        /// <summary>Shares the local outfit with the lobby when it changes. Cheap; call often.</summary>
        public static void PublishTick()
        {
            if (Time.unscaledTime < nextPublish)
            {
                return;
            }
            nextPublish = Time.unscaledTime + 2f;
            NetSession net = NetSession.Instance;
            if (net == null || !net.CurrentLobby.HasValue)
            {
                published = null;
                return;
            }
            string mine = Local.ToString();
            if (mine != published)
            {
                net.CurrentLobby.Value.SetMemberData(Key, mine);
                published = mine;
            }
        }

        /// <summary>
        /// What a teammate wears: their shared choice, the standard look when they haven't shared one
        /// (an older Belay), and the local player's own outfit for stand-ins that aren't lobby members
        /// (test bots), so the choice can be checked without a second player.
        /// </summary>
        public static Outfit Of(SteamId id)
        {
            if (cache.TryGetValue(id.Value, out KeyValuePair<float, Outfit> hit) && Time.unscaledTime < hit.Key)
            {
                return hit.Value;
            }
            Outfit outfit = LookUp(id);
            cache[id.Value] = new KeyValuePair<float, Outfit>(Time.unscaledTime + 1f, outfit);
            return outfit;
        }

        private static Outfit LookUp(SteamId id)
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.CurrentLobby.HasValue && id.Value != 0)
            {
                try
                {
                    Steamworks.Data.Lobby lobby = net.CurrentLobby.Value;
                    foreach (Friend member in lobby.Members)
                    {
                        if (member.Id == id)
                        {
                            return Outfit.Parse(lobby.GetMemberData(member, Key));
                        }
                    }
                }
                catch (System.Exception)
                {
                    return default;
                }
            }
            return Local;
        }
    }
}
