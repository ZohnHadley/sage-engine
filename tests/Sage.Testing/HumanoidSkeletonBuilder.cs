#nullable enable
using System.Numerics;
using Sage.Simulation;

namespace Sage.Testing;

// A small humanoid skeleton, built in code (issue #120), for IK and attachment tests that need legs, a
// spine and an arm: the 3-joint chain of SkinnedModelBuilder has none of them. Model space is the
// engine's: +Y up, forward -Z, right +X. Every rest rotation is identity and every scale one, so each
// joint's rest model position is the sum of the offsets down to it:
//
//   root (0, 0, 0)
//   └ pelvis (0, 1, 0)                         hips, one metre up
//     ├ spine (0, 1.1, 0) └ chest (0, 1.4, 0) └ neck (0, 1.6, 0) └ head (0, 1.7, 0)
//     │                     └ upper_arm.R (0.2, 1.6, 0) └ forearm.R (0.5, 1.6, 0) └ hand.R (0.75, 1.6, 0)
//     ├ thigh.L (-0.15, 1, 0) └ shin.L (-0.15, 0.55, 0) └ foot.L (-0.15, 0.1, 0)
//     └ thigh.R ( 0.15, 1, 0) └ shin.R ( 0.15, 0.55, 0) └ foot.R ( 0.15, 0.1, 0)
//
// Legs are 0.45 + 0.45 m and the ankles rest FootHeight (0.1 m) above the root: stood on flat ground
// at the root's height, the soles touch it.
public static class HumanoidSkeletonBuilder
{
    public const string Root = "root", Pelvis = "pelvis", Spine = "spine", Chest = "chest", Neck = "neck", Head = "head";
    public const string UpperArmR = "upper_arm.R", ForearmR = "forearm.R", HandR = "hand.R";
    public const string ThighL = "thigh.L", ShinL = "shin.L", FootL = "foot.L";
    public const string ThighR = "thigh.R", ShinR = "shin.R", FootR = "foot.R";
    public const float Stance = 0.15f, ThighLength = 0.45f, ShinLength = 0.45f, FootHeight = 0.1f;

    public static Skeleton Build()
    {
        var joints = new (string Name, string? Parent, Vector3 Offset)[]
        {
            (Root, null, Vector3.Zero),
            (Pelvis, Root, new Vector3(0, 1f, 0)),
            (Spine, Pelvis, new Vector3(0, 0.1f, 0)),
            (Chest, Spine, new Vector3(0, 0.3f, 0)),
            (Neck, Chest, new Vector3(0, 0.2f, 0)),
            (Head, Neck, new Vector3(0, 0.1f, 0)),
            (UpperArmR, Chest, new Vector3(0.2f, 0.2f, 0)),
            (ForearmR, UpperArmR, new Vector3(0.3f, 0, 0)),
            (HandR, ForearmR, new Vector3(0.25f, 0, 0)),
            (ThighL, Pelvis, new Vector3(-Stance, 0, 0)),
            (ShinL, ThighL, new Vector3(0, -ThighLength, 0)),
            (FootL, ShinL, new Vector3(0, -ShinLength, 0)),
            (ThighR, Pelvis, new Vector3(Stance, 0, 0)),
            (ShinR, ThighR, new Vector3(0, -ThighLength, 0)),
            (FootR, ShinR, new Vector3(0, -ShinLength, 0)),
        };

        int count = joints.Length;
        var names = new string[count];
        var parents = new int[count];
        var rest = new Pose[count];
        var inverseBind = new Matrix4x4[count];
        var modelAt = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = joints[i].Name;
            parents[i] = joints[i].Parent == null ? -1 : System.Array.IndexOf(names, joints[i].Parent);
            rest[i] = new Pose { Position = joints[i].Offset, Rotation = Quaternion.Identity, Scale = Vector3.One };
            modelAt[i] = (parents[i] < 0 ? Vector3.Zero : modelAt[parents[i]]) + joints[i].Offset;
            inverseBind[i] = Matrix4x4.CreateTranslation(-modelAt[i]);
        }
        return new Skeleton(names, parents, rest, inverseBind);
    }
}
