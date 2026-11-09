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
            w.Write(flags);
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
