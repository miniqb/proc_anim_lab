using System;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.TentaclePlant;

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

    /// <summary>
    /// 3.2m 净高房间的天花板伏击者：短链 + 伪装参数调满（伪装就位后 10 tick 出手）。
    /// 常态手感对齐 short；WanderCenterDistance 压到 1.8 避免游走目标探进对面地板。
    /// </summary>
    public static TentaclePlantParams Lurker() => new()
    {
        Name = "tentacle-plant/lurker",
        Length = 3.2f,
        // 5 节而非 6：短链的逐节拉伸预算按 link 绝对长度走（1.25×0.64m），
        // 扑击落幕时整链余速拖根节的残差才收得回 STRIKE-GEOMETRY 的 1.25 门限
        //（峰值出现在 Striking→Recovering 的根节）；速度类参数同步缩配。
        SegmentCount = 5,
        SegmentDamping = 0.92f,
        SegmentVelocityCap = 0.42f,
        TipGoalAttraction = 0.009f,
        InnerGoalAttraction = 0.0008f,
        GuideAttraction = 0.0008f,
        OutwardRootForce = 0.005f,
        ShapeSeparationForce = 0.012f,
        ConstraintIterations = 5,
        // 3.2 × 0.32 / 5 ≈ 0.205 ≥ RootRadius − RootSurfaceOffset (0.15)，过 Validate。
        RetractedLengthFraction = 0.32f,
        WanderCenterDistance = 1.8f,
        WanderRadius = 1.2f,
        ChargeTicks = 100,
        LungeTicks = 8,
        GrabWindowTicks = 40,
        RetractTicks = 70,
        LungeImpulse = 0.26f,
        DisguiseExtensionFraction = 0.10f,
        DisguiseEngagePerTick = 0.0125f,
        DisguiseReleasePerTick = 0.25f,
        DisguiseChargeThreshold = 0.75f,
        DisguiseChargeMultiplier = 10,
        // 探头张紧与伪装对齐：锁定后 ceil(100/10) = 10 tick 出手。拉伸不在任何
        // 预设开启（StrikeStretchFactor 保持 1），由宿主/CLI 按场景 opt-in。
        ProbeChargeMultiplier = 10,
    };

    public static TentaclePlantParams[] AllPresets() =>
        new[] { Original(), Short(), Hunter(), Lurker() };

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
        Vector3 handPos = canonical.Point + canonical.OutwardNormal * snapshot.RootRadius;

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
