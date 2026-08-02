using System;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.Deer;

/// <summary>
/// 鹿预设与出生装配器。只复用通用 Body/ChunkConnection 原语；参数、腿链和控制器均属于
/// Deer 命名空间。稳定 ID 是宿主存档/CLI 契约，未知值快速失败。
/// </summary>
public static class DeerFactory
{
    public const string OriginalId = "deer/original";
    public const string CompactId = "deer/compact";
    public const string StriderId = "deer/strider";

    /// <summary>
    /// 当前 DLL + MMF Deer 基线。物理尺寸严格按 1px=0.025m 换算：头 22.5px/3 mass；
    /// 四节躯干使用 Deer 构造中的曲线结果；首连接 38px，后续连接为相邻最大半径的 0.8；
    /// dominance=.5 的 MMF 鹿角为 45px/0.5 mass；四腿均为 6 段、段半径 5px，构造时
    /// idealLength=300px，而活动运行期 maxLength=400px、完全休息为 133.33px。
    /// 腿段质量、3D 外撇、探测和控制增益在原作没有一一对应量，属于本项目确定性 3D 参数。
    /// </summary>
    public static DeerParams Original() => new()
    {
        StableId = OriginalId,
        HeadRadius = 0.5625f,
        HeadMass = 3f,
        TrunkSegments =
        [
            new DeerBodySegmentParams
            {
                Radius = 0.830563f,
                Mass = 7.502312f,
                ConnectionLengthToPrevious = 0.95f,
                SupportWeight = 1.55f,
                DriveWeight = 0.80f,
                PostureWeight = 1.10f,
            },
            new DeerBodySegmentParams
            {
                Radius = 0.745775f,
                Mass = 6.552679f,
                ConnectionLengthToPrevious = 0.6644504f,
                SupportWeight = 2.15f,
                DriveWeight = 0.60f,
                PostureWeight = 1.35f,
            },
            new DeerBodySegmentParams
            {
                Radius = 0.614812f,
                Mass = 5.085896f,
                ConnectionLengthToPrevious = 0.59662f,
                SupportWeight = 1.75f,
                DriveWeight = 0.40f,
                PostureWeight = 1.20f,
            },
            new DeerBodySegmentParams
            {
                Radius = 0.452582f,
                Mass = 3.268919f,
                ConnectionLengthToPrevious = 0.4918496f,
                SupportWeight = 0.55f,
                DriveWeight = 0.20f,
                PostureWeight = 0.85f,
            },
        ],
        AntlerRadius = 1.125f,
        AntlerMass = 0.5f,
        AntlerHeadOverlap = 0.25f,
        ConstraintIterations = 8,
        LegSlots =
        [
            OriginalLeg(anchor: 0, pair: 0, side: -1, forwardSplay: 0.68f, outwardSplay: 0.74f),
            OriginalLeg(anchor: 0, pair: 0, side: 1, forwardSplay: 0.58f, outwardSplay: 0.82f),
            OriginalLeg(anchor: 1, pair: 1, side: -1, forwardSplay: -0.55f, outwardSplay: 0.77f),
            OriginalLeg(anchor: 1, pair: 1, side: 1, forwardSplay: -0.71f, outwardSplay: 0.69f),
        ],
        IdealFootReachRatio = 0.90f,
        // 原作没有显式 BodyCenter 高度；6m/2.64m 是把 10m 活动腿长映射到 3D 外撇腿后的
        // 项目取舍。它们与原作 preferredHeight（只驱动腿长）刻意分开。
        PreferredBodyHeight = 6.0f,
        RestHeightRatio = 0.44f,
        RestLegReachRatio = 1f / 3f,
        RestLegReachExponent = 5f,
        RestDelayTicks = 160,
        GroundProbeDistance = 10.5f,
        ForwardHeightProbeDistance = 1.8f,
        MaxSupportImpulse = 0.055f,
        SupportResponseExponent = 0.10f,
        BaseDrive = 0.024f,
        MaxMoveSpeed = 0.095f,
        HesitationRisePerTick = 0.08f,
        HesitationFallPerTick = 0.12f,
        StallReleaseTicks = 32,
        AntiFoldStrength = 0.22f,
        AntiFoldSpanRatio = 0.58f,
        MaxLeanDegrees = 48f,
        MaxStandableSlopeDegrees = 52f,
        ArriveRadius = 0.45f,
    };

    /// <summary>
    /// 紧凑种：Original 的身体线性尺寸 ×0.72、身体质量 ×0.55、腿长 ×0.68。
    /// 保留 4 节粗躯干和 4×6 段拓扑，但腿更快、确认/冷却更短、目标高度更低，适合窄台阶；
    /// 这是一套确定性 3D 手感变体，不对应原作随机个体差异。
    /// </summary>
    public static DeerParams Compact()
    {
        DeerParams p = Original();
        p.StableId = CompactId;
        ScaleMorphology(p, bodyLinearScale: 0.72f, bodyMassScale: 0.55f,
            legLengthScale: 0.68f, legThicknessScale: 0.78f, legMassScale: 0.62f);
        p.MaxSupportImpulse = 0.060f;
        p.BaseDrive = 0.030f;
        p.MaxMoveSpeed = 0.115f;
        p.SupportBlend = 0.46f;
        p.SupportResponseExponent = 0.10f;
        p.HeightFollowGain = 0.12f;
        p.MaxLeanDegrees = 54f;
        p.ArriveRadius = 0.34f;
        foreach (DeerLegSlotParams leg in p.LegSlots)
        {
            leg.HuntSpeed = 0.44f;
            leg.Quickness = 0.72f;
            leg.ReleaseCooldownTicks = 10;
            leg.GripConfirmTicks = 2;
            leg.CandidateHysteresisRatio = 0.10f;
        }
        return p;
    }

    /// <summary>
    /// 高脚种：Original 身体线性尺寸 ×1.05、质量 ×1.10，腿长 ×1.25 并细分为 8 段。
    /// 更高的目标高度、较慢腿段和更长确认形成跨沟/越阶能力与审慎步态；躯干拓扑和支撑路线不变。
    /// </summary>
    public static DeerParams Strider()
    {
        DeerParams p = Original();
        p.StableId = StriderId;
        ScaleMorphology(p, bodyLinearScale: 1.05f, bodyMassScale: 1.10f,
            legLengthScale: 1.25f, legThicknessScale: 0.90f, legMassScale: 0.85f);
        p.PreferredBodyHeight *= 1.32f;
        p.RestHeightRatio = 0.44f;
        p.RestDelayTicks = 200;
        p.MaxSupportImpulse = 0.060f;
        p.BaseDrive = 0.021f;
        p.MaxMoveSpeed = 0.102f;
        p.SupportBlend = 0.34f;
        p.SupportResponseExponent = 0.10f;
        p.IdealFootReachRatio = 0.86f;
        p.ForwardHeightProbeDistance = 2.45f;
        p.MaxLeanDegrees = 42f;
        p.BalanceRecoveryStrength = 0.14f;
        p.ArriveRadius = 0.55f;
        foreach (DeerLegSlotParams leg in p.LegSlots)
        {
            leg.SegmentCount = 8;
            leg.HuntSpeed = 0.27f;
            leg.Quickness = 0.48f;
            leg.ReleaseCooldownTicks = 18;
            leg.GripConfirmTicks = 4;
            leg.CandidateHysteresisRatio = 0.15f;
            leg.ProbeSpreadRatio = 0.23f;
        }
        return p;
    }

    /// <summary>正式预设固定顺序；每次调用均返回不共享数组或对象的新参数。</summary>
    public static DeerParams[] AllPresets() => [Original(), Compact(), Strider()];

    public static bool TryByStableId(string? stableId, out DeerParams parameters)
    {
        parameters = stableId switch
        {
            OriginalId => Original(),
            CompactId => Compact(),
            StriderId => Strider(),
            _ => null!,
        };
        return parameters is not null;
    }

    /// <summary>按稳定 ID 新建预设；未知值是宿主配置错误，必须快速失败。</summary>
    public static DeerParams ByStableId(string stableId)
    {
        if (TryByStableId(stableId, out DeerParams parameters))
        {
            return parameters;
        }
        throw new ArgumentException($"Unknown deer stable ID '{stableId}'.", nameof(stableId));
    }

    public static DeerLocomotionController CreateController(Vector3 origin) =>
        CreateController(origin, Vector3.Forward, Original());

    public static DeerLocomotionController CreateController(Vector3 origin, Vector3 forward) =>
        CreateController(origin, forward, Original());

    public static DeerLocomotionController CreateController(Vector3 origin, DeerParams parameters) =>
        CreateController(origin, Vector3.Forward, parameters);

    /// <summary>
    /// origin 是水平支撑面的参考点。头高于并领先首节躯干，其余粗躯干沿 -forward 铺开；
    /// 鹿角置于头上方并略向前。身体固定为 Head+4 Trunk+Antler
    /// （自定义表可改变躯干节数）；腿脚按各自 3D 外撇落在 origin 所在平面上。
    /// </summary>
    public static DeerLocomotionController CreateController(
        Vector3 origin,
        Vector3 forward,
        DeerParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeerParams snapshot = parameters.Clone().Validate();
        ValidateOrigin(origin);
        Vector3 bodyForward = CanonicalForward(forward);
        Vector3 up = Vector3.Up;
        Vector3 right = bodyForward.Cross(up).Normalized();

        var body = new Body
        {
            GravityScale = 1f,
            AirFriction = 0.999f,
            SurfaceFriction = 0.4f,
            ConstraintIterations = snapshot.ConstraintIterations,
            Skin = snapshot.TerrainClearance,
        };

        Vector3 bodyOrigin = origin + up * snapshot.PreferredBodyHeight;
        var head = new BodyChunk(bodyOrigin, snapshot.HeadRadius, snapshot.HeadMass);
        body.Chunks.Add(head);

        var trunk = new BodyChunk[snapshot.TrunkSegments.Length];
        Vector3 cursor = head.Pos;
        Vector3 headAxis = PostureAxis(
            bodyForward, up, snapshot.HeadPostureForwardWeight, snapshot.HeadPostureUpWeight);
        for (int i = 0; i < trunk.Length; i++)
        {
            DeerBodySegmentParams spec = snapshot.TrunkSegments[i];
            cursor -= (i == 0 ? headAxis : bodyForward) * spec.ConnectionLengthToPrevious;
            trunk[i] = new BodyChunk(cursor, spec.Radius, spec.Mass);
            body.Chunks.Add(trunk[i]);
        }

        float antlerLink = snapshot.HeadRadius + snapshot.AntlerRadius - snapshot.AntlerHeadOverlap;
        Vector3 antlerAxis = PostureAxis(
            bodyForward, up, snapshot.AntlerPostureForwardWeight, snapshot.AntlerPostureUpWeight);
        var antler = new BodyChunk(
            head.Pos + antlerAxis * antlerLink,
            snapshot.AntlerRadius,
            snapshot.AntlerMass);
        body.Chunks.Add(antler);

        // PreferredBodyHeight 的契约是质量中心高度，而不是头球高度。头和鹿角改为上置后，
        // 出生时先整体校正，避免首秒依赖腿支撑把低出生姿态抬到目标高度。
        Vector3 weightedCenter = Vector3.Zero;
        float totalMass = 0f;
        foreach (BodyChunk chunk in body.Chunks)
        {
            weightedCenter += chunk.Pos * chunk.Mass;
            totalMass += chunk.Mass;
        }
        float heightCorrection = snapshot.PreferredBodyHeight
            - (totalMass > 1e-8f ? weightedCenter.Dot(up) / totalMass : bodyOrigin.Dot(up));
        Vector3 spawnCorrection = up * heightCorrection;
        foreach (BodyChunk chunk in body.Chunks)
        {
            chunk.Pos += spawnCorrection;
            chunk.LastPos += spawnCorrection;
        }

        AddRigid(body, head, trunk[0], snapshot.TrunkSegments[0].ConnectionLengthToPrevious);
        for (int i = 1; i < trunk.Length; i++)
        {
            AddRigid(body, trunk[i - 1], trunk[i], snapshot.TrunkSegments[i].ConnectionLengthToPrevious);
        }
        // RW 的鹿角连接 weightSymmetry=0：误差全部落在又大又轻的鹿角块上，头不被它拖动。
        body.Connections.Add(new ChunkConnection(head, antler, antlerLink, 0f)
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
        });

        // ChunkConnection 的构造会随建链顺序覆盖参照；全部连接完成后重申 Deer 朝向契约。
        head.RotationChunk = antler;
        antler.RotationChunk = head;
        for (int i = 0; i + 1 < trunk.Length; i++)
        {
            trunk[i].RotationChunk = trunk[i + 1];
        }
        trunk[^1].RotationChunk = trunk[^2];

        var controller = new DeerLocomotionController(
            body, head, trunk, antler, snapshot, bodyForward);
        for (int i = 0; i < snapshot.LegSlots.Length; i++)
        {
            DeerLegSlotParams slot = snapshot.LegSlots[i];
            BodyChunk anchor = trunk[slot.AnchorTrunkIndex];
            Vector3 initialFoot = InitialFoot(
                origin, anchor, slot, snapshot.IdealFootReachRatio,
                snapshot.FootTargetDownWeight, snapshot.FootTargetSplayWeight,
                bodyForward, right, up);
            controller.AddLeg(new DeerLeg(
                i, anchor, slot, initialFoot, bodyForward, up));
        }
        controller.LinkLegPairs();
        return controller;
    }

    private static DeerLegSlotParams OriginalLeg(
        int anchor,
        int pair,
        int side,
        float forwardSplay,
        float outwardSplay) => new()
    {
        AnchorTrunkIndex = anchor,
        PairIndex = pair,
        Side = side,
        SegmentCount = 6,
        SegmentRadius = 0.125f,
        // RW TentacleChunk 没有独立质量；这是 3D 粒子链为约束权重补出的确定性值。
        SegmentMass = 0.10f,
        MaxLength = 10f,
        InitialLength = 7.5f,
        ForwardSplay = forwardSplay,
        OutwardSplay = outwardSplay,
        FootRadius = 0.125f,
        HuntSpeed = 0.32f,
        Quickness = 0.56f,
        ReleaseCooldownTicks = 15,
        GripConfirmTicks = 3,
        CandidateHysteresisRatio = 0.12f,
        ProbeDistance = 1.35f,
        ProbeSpreadRatio = 0.20f,
        BodyDragStartRatio = 0.90f,
        ReachReleaseRatio = 1f,
    };

    private static void ScaleMorphology(
        DeerParams p,
        float bodyLinearScale,
        float bodyMassScale,
        float legLengthScale,
        float legThicknessScale,
        float legMassScale)
    {
        p.HeadRadius *= bodyLinearScale;
        p.HeadMass *= bodyMassScale;
        foreach (DeerBodySegmentParams segment in p.TrunkSegments)
        {
            segment.Radius *= bodyLinearScale;
            segment.Mass *= bodyMassScale;
            segment.ConnectionLengthToPrevious *= bodyLinearScale;
        }
        p.AntlerRadius *= bodyLinearScale;
        p.AntlerMass *= bodyMassScale;
        p.AntlerHeadOverlap *= bodyLinearScale;
        p.PreferredBodyHeight *= bodyLinearScale;
        p.ForwardHeightProbeDistance *= bodyLinearScale;
        p.TerrainClearance *= bodyLinearScale;
        p.ArriveRadius *= bodyLinearScale;

        foreach (DeerLegSlotParams leg in p.LegSlots)
        {
            leg.SegmentRadius *= legThicknessScale;
            leg.SegmentMass *= legMassScale;
            leg.MaxLength *= legLengthScale;
            leg.InitialLength *= legLengthScale;
            leg.FootRadius *= legThicknessScale;
            leg.ProbeDistance *= legLengthScale;
        }
        p.GroundProbeDistance *= legLengthScale;
    }

    private static void AddRigid(Body body, BodyChunk a, BodyChunk b, float restLength)
    {
        body.Connections.Add(new ChunkConnection(a, b, restLength, MassWeightedCorrectionForA(a, b))
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
        });
    }

    private static Vector3 InitialFoot(
        Vector3 surfaceOrigin,
        BodyChunk anchor,
        DeerLegSlotParams slot,
        float idealReachRatio,
        float downWeight,
        float splayWeight,
        Vector3 forward,
        Vector3 right,
        Vector3 up)
    {
        Vector3 splay = forward * slot.ForwardSplay
            + right * (slot.Side * slot.OutwardSplay);
        if (splay.LengthSquared() <= 1e-10f)
        {
            splay = right * slot.Side;
        }
        Vector3 desiredDirection = -up * downWeight + splay * splayWeight;
        if (desiredDirection.LengthSquared() <= 1e-10f)
        {
            desiredDirection = -up;
        }
        desiredDirection = desiredDirection.Normalized();

        float surfaceHeight = surfaceOrigin.Dot(up) + slot.FootRadius;
        float verticalDrop = Mathf.Max(0f, anchor.Pos.Dot(up) - surfaceHeight);
        float downDot = Mathf.Max(0.35f, -desiredDirection.Dot(up));
        float desiredReach = Mathf.Min(
            slot.MaxLength * idealReachRatio,
            verticalDrop / downDot);
        return anchor.Pos + desiredDirection * desiredReach;
    }

    private static Vector3 PostureAxis(
        Vector3 forward,
        Vector3 up,
        float forwardWeight,
        float upWeight)
    {
        Vector3 axis = forward * forwardWeight + up * upWeight;
        return axis.LengthSquared() > 1e-10f ? axis.Normalized() : up;
    }

    private static float MassWeightedCorrectionForA(BodyChunk a, BodyChunk b) =>
        b.Mass / (a.Mass + b.Mass);

    private static void ValidateOrigin(Vector3 origin)
    {
        if (!Finite(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), "Deer origin must be finite.");
        }
    }

    private static Vector3 CanonicalForward(Vector3 forward)
    {
        if (!Finite(forward))
        {
            throw new ArgumentOutOfRangeException(nameof(forward), "Deer forward must be finite.");
        }
        Vector3 tangent = forward - Vector3.Up * forward.Dot(Vector3.Up);
        if (tangent.LengthSquared() <= 1e-10f)
        {
            throw new ArgumentException("Deer forward must have a non-zero ground-tangent component.", nameof(forward));
        }
        return tangent.Normalized();
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
