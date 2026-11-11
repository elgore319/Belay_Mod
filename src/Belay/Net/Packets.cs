using System.IO;
using UnityEngine;

namespace Belay.Net
{
    public enum PacketType : byte
    {
        Hello = 1,
        PlayerState = 2,
        PlayerLeft = 3,
        GooState = 10,
        DoorOpen = 20,
        DoorRequestClose = 21,
        DoorCountdown = 22,
        DoorClose = 23,
        ReviveRequest = 30,
        Revived = 31,
        RunOver = 32,
        ButtonPress = 40,
        LevelHash = 41,
        LevelMismatch = 42,
        HoldInteract = 43,
        LeverAngle = 44,
        HandTouch = 45,
        ItemPickup = 46,
        ItemDrop = 47,
        ShortcutVoteRequest = 48,
        ShortcutVote = 49,
        ShortcutVoteState = 50,
        ShortcutResult = 51,
        RopePull = 52,
        ZoneEntered = 53,
        Knockback = 54,
        PropState = 55,
        PropKill = 56,
    }

    public static class Channels
    {
        public const int Reliable = 0;
        public const int State = 1;
    }

    /// <summary>
    /// Snapshot of one player. Positions are "corrected" (world offset removed) so they mean the same
    /// thing on every client even though each client recenters its world independently.
    /// </summary>
    public struct PlayerState
    {
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public Vector3 LeftHand;
        public Vector3 RightHand;
        public bool LeftHanging;
        public bool RightHanging;
        public bool Dead;
        /// <summary>Inventory screen open: the avatar holds its backpack open in front of it.</summary>
        public bool InventoryOpen;
        /// <summary>Prefab names of the items in each hand ("" when empty), so teammates see them.</summary>
        public string LeftItem;
        public string RightItem;
        /// <summary>Teammate this player is gripping (0 = none). Their body is carried when <see cref="Anchored"/>.</summary>
        public ulong HeldTarget;
        /// <summary>Where on the held teammate's body the grip is, relative to that body's center.</summary>
        public Vector3 HeldOffset;
        /// <summary>Shared-space point in front of this player's face that a carried teammate is pulled to.</summary>
        public Vector3 CarryPoint;
        /// <summary>Standing or gripping the wall with the other hand: strong enough to lift whoever they hold.</summary>
        public bool Anchored;

        public void Write(BinaryWriter w)
        {
            w.WriteVector(Position);
            w.Write(Yaw);
            w.Write(Pitch);
            w.WriteVector(LeftHand);
            w.WriteVector(RightHand);
            byte flags = 0;
            if (LeftHanging) flags |= 1;
            if (RightHanging) flags |= 2;
            if (Dead) flags |= 4;
            if (InventoryOpen) flags |= 8;
            if (HeldTarget != 0) flags |= 16;
            if (Anchored) flags |= 32;
            w.Write(flags);
            w.Write(LeftItem ?? "");
            w.Write(RightItem ?? "");
            if (HeldTarget != 0)
            {
                w.Write(HeldTarget);
                w.WriteVector(HeldOffset);
                w.WriteVector(CarryPoint);
            }
        }

        public static PlayerState Read(BinaryReader r)
        {
            var s = new PlayerState
            {
                Position = r.ReadVector(),
                Yaw = r.ReadSingle(),
                Pitch = r.ReadSingle(),
                LeftHand = r.ReadVector(),
                RightHand = r.ReadVector(),
            };
            byte flags = r.ReadByte();
            s.LeftHanging = (flags & 1) != 0;
            s.RightHanging = (flags & 2) != 0;
            s.Dead = (flags & 4) != 0;
            s.InventoryOpen = (flags & 8) != 0;
            // Older versions stop after the flags.
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                s.LeftItem = r.ReadString();
                s.RightItem = r.ReadString();
            }
            s.Anchored = (flags & 32) != 0;
            if ((flags & 16) != 0)
            {
                s.HeldTarget = r.ReadUInt64();
                s.HeldOffset = r.ReadVector();
                s.CarryPoint = r.ReadVector();
            }
            return s;
        }
    }

    public static class BinaryExtensions
    {
        public static void WriteVector(this BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        public static Vector3 ReadVector(this BinaryReader r)
        {
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }
}
