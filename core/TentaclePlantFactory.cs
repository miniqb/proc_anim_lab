using System;
using Godot;

namespace ProcAnim.Core;

/// <summary>拟态草预设与装配器；所有品种差异均冻结在出生参数中。</summary>
public static class TentaclePlantFactory
{
    public static TentaclePlantParams Original() => new();

    public static TentaclePlantParams Short() => new()
    {
        Name = "tentacle-plant/short",
        Length = 5.0f,
        SegmentCount = 6,
        SegmentDamping = 0.94f,
        SegmentVelocityCap = 0.55f,
        InnerGoalAttraction = 0.0010f,
        GuideAttraction = 0.0010f,
        ShapeSeparationForce = 0.018f,
        ConstraintIterations = 2,
        WanderCenterDistance = 3.35f,
        WanderRadius = 2.45f,
        ChargeTicks = 110,
        LungeTicks = 10,
        GrabWindowTicks = 40,
        RetractTicks = 95,
        LungeImpulse = 0.43f,
    };

    public static TentaclePlantParams Hunter() => new()
    {
        Name = "tentacle-plant/hunter",
        Length = 9.0f,
        SegmentCount = 10,
        SegmentDamping = 0.97f,
        SegmentVelocityCap = 0.75f,
        TipGoalAttraction = 0.015f,
        InnerGoalAttraction = 0.0016f,
        GuideAttraction = 0.0016f,
        ShapeSeparationForce = 0.030f,
        ConstraintIterations = 4,
        WanderCenterDistance = 6.0f,
        WanderRadius = 4.4f,
        ChargeTicks = 70,
        LungeTicks = 12,
        GrabWindowTicks = 40,
        RetractTicks = 65,
        LungeImpulse = 0.56f,
        StrikeGrabRadius = 0.25f,
    };

    public static TentaclePlantParams[] AllPresets() =>
        new[] { Original(), Short(), Hunter() };

    /// <summary>按名取预设；未知名称快速失败，避免回归实际跑错品种却假绿。</summary>
    public static TentaclePlantParams ByName(string name)
    {
        foreach (TentaclePlantParams preset in AllPresets())
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }
        throw new ArgumentException($"Unknown tentacle plant preset '{name}'.", nameof(name));
    }

    public static TentaclePlantController CreateController(
        in TentaclePlantMount mount,
        TentaclePlantParams parameters,
        ulong stableSeed)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        TentaclePlantParams snapshot = parameters.Snapshot();
        TentaclePlantMount canonical = CanonicalizeMount(mount);
        Vector3 rootPos = canonical.Point + canonical.OutwardNormal * snapshot.RootSurfaceOffset;
        Vector3 handPos = rootPos + canonical.OutwardNormal *
            (snapshot.Length * snapshot.SpawnExtension);

        var root = new BodyChunk(rootPos, snapshot.RootRadius, snapshot.RootMass)
        {
            CollideWithTerrain = false,
        };
        var hand = new BodyChunk(handPos, snapshot.TipRadius, snapshot.HandMass)
        {
            // 末段由 TentacleChain 统一 sweep/MTD；避免 proxy 与 tip 重复解算。
            CollideWithTerrain = false,
        };
        var body = new Body
        {
            GravityScale = 0f,
            AirFriction = 0.99f,
            SurfaceFriction = 0.47f,
            ConstraintIterations = 1,
        };
        body.Chunks.Add(root);
        body.Chunks.Add(hand);
        root.RotationChunk = hand;
        hand.RotationChunk = root;

        var chain = new TentacleChain(root, snapshot, canonical.OutwardNormal);
        return new TentaclePlantController(
            canonical, snapshot, stableSeed, body, root, hand, chain);
    }

    internal static TentaclePlantMount CanonicalizeMount(in TentaclePlantMount mount)
    {
        if (!Finite(mount.Point) || !Finite(mount.OutwardNormal) || !Finite(mount.TangentHint))
        {
            throw new ArgumentException("Mount vectors must be finite.", nameof(mount));
        }
        if (mount.OutwardNormal.LengthSquared() <= 1e-10f)
        {
            throw new ArgumentException("Mount outward normal must be non-zero.", nameof(mount));
        }

        Vector3 outward = mount.OutwardNormal.Normalized();
        Vector3 tangent = mount.TangentHint - outward * mount.TangentHint.Dot(outward);
        if (tangent.LengthSquared() <= 1e-10f)
        {
            tangent = StablePerpendicular(outward);
        }
        tangent = tangent.Normalized();
        return new TentaclePlantMount(mount.Point, outward, tangent, mount.ColliderId);
    }

    internal static Vector3 StablePerpendicular(Vector3 normal)
    {
        Vector3 axis;
        float ax = Mathf.Abs(normal.X);
        float ay = Mathf.Abs(normal.Y);
        float az = Mathf.Abs(normal.Z);
        if (ax <= ay && ax <= az)
        {
            axis = Vector3.Right;
        }
        else if (ay <= az)
        {
            axis = Vector3.Up;
        }
        else
        {
            axis = Vector3.Back;
        }
        Vector3 tangent = axis - normal * axis.Dot(normal);
        return tangent.LengthSquared() <= 1e-10f ? Vector3.Right : tangent.Normalized();
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
