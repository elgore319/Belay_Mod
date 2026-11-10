using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Belay.Players.Avatar;
using UnityEngine;

/// Poses the climber in a few stances with the same proportions and IK idea the mod uses, and dumps the
/// triangles (with their material colors) for render.py. Only managed Vector3 math is used.
internal static class Program
{
    private struct Frame
    {
        public Vector3 Origin, Right, Up, Forward;
        public float ScaleZ;

        public Vector3 Apply(Vector3 p) => Origin + Right * p.x + Up * p.y + Forward * (p.z * ScaleZ);

        public static Frame Look(Vector3 origin, Vector3 forward, Vector3 up, float scaleZ = 1f)
        {
            Vector3 f = forward.normalized;
            Vector3 r = Vector3.Cross(up, f).normalized;
            if (r.sqrMagnitude < 1e-6f)
            {
                r = Vector3.right;
            }
            Vector3 u = Vector3.Cross(f, r);
            return new Frame { Origin = origin, Right = r, Up = u, Forward = f, ScaleZ = scaleZ };
        }

        public Vector3 Dir(Vector3 d) => Right * d.x + Up * d.y + Forward * d.z;
    }

    private static readonly List<string> Figures = new List<string>();
    private static StringBuilder tris;
    private static Color[] colors;

    private static void Main(string[] args)
    {
        colors = AvatarDesign.Colors(new Color(1f, 0.45f, 0.2f));
        var sb = new StringBuilder("{\"colors\":[");
        for (int i = 0; i < colors.Length; i++)
        {
            sb.Append(i > 0 ? "," : "").Append(F(colors[i].r)).Append(',').Append(F(colors[i].g)).Append(',').Append(F(colors[i].b));
        }
        sb.Append("],\"figures\":{");

        Figure("standing", standing: true, leftGrip: false, rightGrip: false,
            new Vector3(-0.22f, 1.25f, 0.45f), new Vector3(0.22f, 1.25f, 0.45f));
        Figure("hanging", standing: false, leftGrip: true, rightGrip: false,
            new Vector3(-0.2f, 1.95f, 0.32f), new Vector3(0.38f, 1.55f, 0.4f));

        sb.Append(string.Join(",", Figures)).Append("}}");
        File.WriteAllText(args.Length > 0 ? args[0] : "preview.json", sb.ToString());
        Console.WriteLine("wrote preview.json");
    }

    private static void Figure(string name, bool standing, bool leftGrip, bool rightGrip, Vector3 leftHand, Vector3 rightHand)
    {
        tris = new StringBuilder();
        Vector3 pelvis = new Vector3(0, AvatarDesign.PelvisHeight, 0);
        float lean = standing ? 4f : -6f;
        float rad = lean * Mathf.Deg2Rad;
        Frame torso = Frame.Look(pelvis, new Vector3(0, -Mathf.Sin(rad), Mathf.Cos(rad)), Vector3.up);
        Emit(AvatarDesign.BuildTorso, torso);

        Vector3 neck = torso.Apply(new Vector3(0, AvatarDesign.NeckHeight, 0.01f));
        float pitch = (standing ? 10f : -25f) * Mathf.Deg2Rad;
        Emit(AvatarDesign.BuildHead, Frame.Look(neck, new Vector3(0, -Mathf.Sin(pitch), Mathf.Cos(pitch)), Vector3.up));

        Arm(torso, -1, leftHand, leftGrip);
        Arm(torso, 1, rightHand, rightGrip);

        float legLength = AvatarDesign.Thigh + AvatarDesign.Shin;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 hip = pelvis + new Vector3(side * AvatarDesign.HipWidth, -0.07f, 0);
            Vector3 foot = standing
                ? new Vector3(hip.x, AvatarDesign.AnkleHeight, side < 0 ? 0.12f : -0.1f)
                : (side < 0 ? hip + Vector3.down * (legLength * 0.78f) + new Vector3(0.04f, 0, 0.22f)
                            : hip + Vector3.down * (legLength * 0.95f) + new Vector3(0.03f, 0, 0.05f));
            Vector3 ankle = Limb(hip, foot, hip + new Vector3(side * 0.1f, 0, 0.6f), AvatarDesign.Thigh, AvatarDesign.Shin,
                AvatarDesign.BuildThigh, AvatarDesign.BuildShin, false);
            float toe = standing ? 0f : 35f * Mathf.Deg2Rad;
            Emit(AvatarDesign.BuildBoot, Frame.Look(ankle, new Vector3(0, -Mathf.Sin(toe), Mathf.Cos(toe)), Vector3.up));
        }
        Figures.Add("\"" + name + "\":[" + tris.ToString().TrimEnd(',') + "]");
    }

    private static void Arm(Frame torso, int side, Vector3 hand, bool grip)
    {
        Vector3 shoulder = torso.Apply(new Vector3(side * AvatarDesign.ShoulderWidth, AvatarDesign.ShoulderHeight - 0.02f, 0));
        Vector3 dir = (hand - shoulder).normalized;
        Vector3 wrist = hand - dir * (grip ? 0.06f : 0.04f);
        Vector3 pole = shoulder + torso.Dir(new Vector3(side * 0.6f, -0.6f, -0.35f));
        Vector3 elbow = Vector3.zero;
        Vector3 end = Limb(shoulder, wrist, pole, AvatarDesign.UpperArm, AvatarDesign.Forearm,
            AvatarDesign.BuildUpperArm, AvatarDesign.BuildForearm, true, e => elbow = e);
        Vector3 forearm = (end - elbow).normalized;
        Vector3 up = torso.Up;
        Vector3 back = Vector3.Lerp(up, -torso.Forward, Mathf.Abs(Vector3.Dot(forearm, up)));
        bool left = side < 0;
        Emit(k => { if (grip) AvatarDesign.BuildFist(k, left); else AvatarDesign.BuildOpenHand(k, left); },
            Frame.Look(wrist, forearm, back));
    }

    private static Vector3 Limb(Vector3 start, Vector3 target, Vector3 pole, float a, float b,
        Action<MeshKit> upper, Action<MeshKit> lower, bool stretch, Action<Vector3> joint = null)
    {
        Vector3 to = target - start;
        float d = to.magnitude;
        Vector3 dir = to / d;
        float reach = (a + b) * 0.999f, scale = 1f;
        if (d > reach)
        {
            if (stretch) scale = Mathf.Min(d / reach, 3f);
            d = reach;
        }
        float cos = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
        float sin = Mathf.Sqrt(1 - cos * cos);
        Vector3 bend = Vector3.ProjectOnPlane(pole - start, dir).normalized;
        Vector3 mid = start + (dir * cos + bend * sin) * a * scale;
        Vector3 end = start + dir * d * scale;
        joint?.Invoke(mid);
        Emit(upper, Frame.Look(start, mid - start, bend, (mid - start).magnitude));
        Emit(lower, Frame.Look(mid, end - mid, bend, (end - mid).magnitude));
        return end;
    }

    private static void Emit(Action<MeshKit> build, Frame frame)
    {
        var kit = new MeshKit(Slot.Count);
        build(kit);
        for (int s = 0; s < kit.Submeshes.Length; s++)
        {
            List<int> list = kit.Submeshes[s];
            for (int i = 0; i < list.Count; i += 3)
            {
                tris.Append('[').Append(s);
                for (int j = 0; j < 3; j++)
                {
                    // Unity is left-handed (x right, y up, z forward); write as x, z, y for a right-handed plot.
                    Vector3 p = frame.Apply(kit.Vertices[list[i + j]]);
                    tris.Append(',').Append(F(p.x)).Append(',').Append(F(p.z)).Append(',').Append(F(p.y));
                }
                tris.Append("],");
            }
        }
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
