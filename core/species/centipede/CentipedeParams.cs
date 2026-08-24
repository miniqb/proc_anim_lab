using System;

namespace ProcAnim.Core.Species.Centipede;

/// <summary>
/// 一节蜈蚣身体在出生时解析后的完整参数。控制器持有这些快照，不在运行时回读
/// <see cref="CentipedeParams"/> 或覆写表，因此修改体型需要重新装配。
/// </summary>
public sealed class CentipedeSegmentParams
{
    public float Radius = 0.14f;
    public float Mass = 0.3f;
    public float LinkLengthToNext = 0.23f;
    public float BendStiffness = 0.5f;
    public float DriveWeight = 1f;
    public float AdhesionWeight = 1f;
    public int LegPairs = 1;
    public float LegLength = 0.34f;
    public float FootRadius = 0.045f;
    public float LegHuntSpeed = 0.14f;
    public float LegQuickness = 0.65f;
    public int LegGripDelay = 3;
    public float LegStride = 0.3f;
    public float LegLateral = 0.22f;

    public CentipedeSegmentParams Copy() => new()
    {
        Radius = Radius,
        Mass = Mass,
        LinkLengthToNext = LinkLengthToNext,
        BendStiffness = BendStiffness,
        DriveWeight = DriveWeight,
        AdhesionWeight = AdhesionWeight,
        LegPairs = LegPairs,
        LegLength = LegLength,
        FootRadius = FootRadius,
        LegHuntSpeed = LegHuntSpeed,
        LegQuickness = LegQuickness,
        LegGripDelay = LegGripDelay,
        LegStride = LegStride,
        LegLateral = LegLateral,
    };

    internal void Validate(string path)
    {
        RequirePositive(Radius, $"{path}.{nameof(Radius)}");
        RequirePositive(Mass, $"{path}.{nameof(Mass)}");
        RequirePositive(LinkLengthToNext, $"{path}.{nameof(LinkLengthToNext)}");
        RequireUnit(BendStiffness, $"{path}.{nameof(BendStiffness)}");
        RequireNonNegative(DriveWeight, $"{path}.{nameof(DriveWeight)}");
        RequireNonNegative(AdhesionWeight, $"{path}.{nameof(AdhesionWeight)}");
        if (LegPairs < 0)
        {
            throw new ArgumentOutOfRangeException(path,
                $"{path}.{nameof(LegPairs)} must be at least zero.");
        }
        RequirePositive(LegLength, $"{path}.{nameof(LegLength)}");
        RequirePositive(FootRadius, $"{path}.{nameof(FootRadius)}");
        RequirePositive(LegHuntSpeed, $"{path}.{nameof(LegHuntSpeed)}");
        RequireUnitPositive(LegQuickness, $"{path}.{nameof(LegQuickness)}");
        if (LegGripDelay < 0)
        {
            throw new ArgumentOutOfRangeException(path,
                $"{path}.{nameof(LegGripDelay)} must be at least zero.");
        }
        RequirePositive(LegStride, $"{path}.{nameof(LegStride)}");
        RequireNonNegative(LegLateral, $"{path}.{nameof(LegLateral)}");
    }

    internal static void RequirePositive(float value, string path)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and greater than zero.");
        }
    }

    internal static void RequireNonNegative(float value, string path)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and non-negative.");
        }
    }

    internal static void RequireUnit(float value, string path)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and in [0, 1].");
        }
    }

    internal static void RequireUnitPositive(float value, string path)
    {
        if (!float.IsFinite(value) || value <= 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and in (0, 1].");
        }
    }
}

/// <summary>
/// 对指定节的稀疏出生覆写。未赋值字段沿用默认体型曲线；同一节出现多次时按数组顺序
/// 应用，后面的覆写胜出，便于预设先做区段覆写、调用侧再做最终微调。
/// </summary>
public sealed class CentipedeSegmentOverride
{
    public int SegmentIndex;
    public float? Radius;
    public float? Mass;
    public float? LinkLengthToNext;
    public float? BendStiffness;
    public float? DriveWeight;
    public float? AdhesionWeight;
    public int? LegPairs;
    public float? LegLength;
    public float? FootRadius;
    public float? LegHuntSpeed;
    public float? LegQuickness;
    public int? LegGripDelay;
    public float? LegStride;
    public float? LegLateral;

    internal void ApplyTo(CentipedeSegmentParams target)
    {
        if (Radius is { } radius) target.Radius = radius;
        if (Mass is { } mass) target.Mass = mass;
        if (LinkLengthToNext is { } linkLength) target.LinkLengthToNext = linkLength;
        if (BendStiffness is { } bendStiffness) target.BendStiffness = bendStiffness;
        if (DriveWeight is { } driveWeight) target.DriveWeight = driveWeight;
        if (AdhesionWeight is { } adhesionWeight) target.AdhesionWeight = adhesionWeight;
        if (LegPairs is { } legPairs) target.LegPairs = legPairs;
        if (LegLength is { } legLength) target.LegLength = legLength;
        if (FootRadius is { } footRadius) target.FootRadius = footRadius;
        if (LegHuntSpeed is { } legHuntSpeed) target.LegHuntSpeed = legHuntSpeed;
        if (LegQuickness is { } legQuickness) target.LegQuickness = legQuickness;
        if (LegGripDelay is { } legGripDelay) target.LegGripDelay = legGripDelay;
        if (LegStride is { } legStride) target.LegStride = legStride;
        if (LegLateral is { } legLateral) target.LegLateral = legLateral;
    }
}

/// <summary>
/// 蜈蚣的纯出生配置：全局表面运动/行波参数 + 默认逐节参数 + 稀疏逐节覆写。
/// <see cref="ResolveSegments"/> 返回深拷贝快照，支持任意大于等于 2 的节数。
/// </summary>
public sealed class CentipedeParams
{
    public string StableId = "centipede/custom";
    public int SegmentCount = 5;
    public float EndRadius = 0.11f;
    public float MiddleRadius = 0.15f;
    public float ProfileExponent = 0.8f;
    public CentipedeSegmentParams BaseSegment = new();
    public CentipedeSegmentOverride?[] Overrides = Array.Empty<CentipedeSegmentOverride?>();

    public float BaseSpeed = 0.055f;
    public float MaxMoveSpeed = 0.08f;
    public int ConstraintIterations = 3;
    public float MoveIntentDeadzone = 0.05f;
    /// <summary>身体半径之外的额外贴面皮肤间隙（米），不是球心离表面的总高度。</summary>
    public float SurfaceClearance = 0.015f;
    public float SurfaceProbeDistance = 0.65f;
    public float SurfaceServo = 0.2f;
    public float SurfaceDamping = 0.55f;
    /// <summary>停驶时每 tick 消去的贴面切向速度比例；0 退回旧的无阻尼贴面弹簧。</summary>
    public float StanceDamping = 0.55f;
    public float SupportBlend = 0.25f;
    public float TrailSampleSpacing = 0.1f;
    public int CornerProbeSteps = 9;
    public float GaitFrequency = 0.16f;
    public float GaitWavelength = 2.2f;
    public float StanceFraction = 0.68f;
    public float SelfAvoidanceStrength = 0.12f;
    public float SelfAvoidanceCellSize = 0.36f;
    public float ArriveRadius = 0.25f;

    /// <summary>校验完整出生配置；失败时快速抛出带字段路径的参数异常。</summary>
    public CentipedeParams Validate()
    {
        if (string.IsNullOrWhiteSpace(StableId))
        {
            throw new ArgumentException($"{nameof(StableId)} must not be empty.", nameof(StableId));
        }
        if (SegmentCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentCount), SegmentCount,
                "A centipede requires at least two segments.");
        }
        CentipedeSegmentParams.RequirePositive(EndRadius, nameof(EndRadius));
        CentipedeSegmentParams.RequirePositive(MiddleRadius, nameof(MiddleRadius));
        CentipedeSegmentParams.RequirePositive(ProfileExponent, nameof(ProfileExponent));
        if (BaseSegment is null)
        {
            throw new ArgumentNullException(nameof(BaseSegment));
        }
        if (Overrides is null)
        {
            throw new ArgumentNullException(nameof(Overrides));
        }
        BaseSegment.Validate(nameof(BaseSegment));

        CentipedeSegmentParams.RequireNonNegative(BaseSpeed, nameof(BaseSpeed));
        CentipedeSegmentParams.RequirePositive(MaxMoveSpeed, nameof(MaxMoveSpeed));
        if (ConstraintIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConstraintIterations), ConstraintIterations,
                $"{nameof(ConstraintIterations)} must be at least one.");
        }
        CentipedeSegmentParams.RequireNonNegative(MoveIntentDeadzone, nameof(MoveIntentDeadzone));
        CentipedeSegmentParams.RequirePositive(SurfaceClearance, nameof(SurfaceClearance));
        CentipedeSegmentParams.RequirePositive(SurfaceProbeDistance, nameof(SurfaceProbeDistance));
        CentipedeSegmentParams.RequireNonNegative(SurfaceServo, nameof(SurfaceServo));
        CentipedeSegmentParams.RequireUnit(SurfaceDamping, nameof(SurfaceDamping));
        CentipedeSegmentParams.RequireUnit(StanceDamping, nameof(StanceDamping));
        CentipedeSegmentParams.RequireUnitPositive(SupportBlend, nameof(SupportBlend));
        CentipedeSegmentParams.RequirePositive(TrailSampleSpacing, nameof(TrailSampleSpacing));
        if (CornerProbeSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CornerProbeSteps), CornerProbeSteps,
                $"{nameof(CornerProbeSteps)} must be at least one.");
        }
        CentipedeSegmentParams.RequireNonNegative(GaitFrequency, nameof(GaitFrequency));
        CentipedeSegmentParams.RequirePositive(GaitWavelength, nameof(GaitWavelength));
        CentipedeSegmentParams.RequireUnitPositive(StanceFraction, nameof(StanceFraction));
        CentipedeSegmentParams.RequireNonNegative(SelfAvoidanceStrength, nameof(SelfAvoidanceStrength));
        CentipedeSegmentParams.RequirePositive(SelfAvoidanceCellSize, nameof(SelfAvoidanceCellSize));
        CentipedeSegmentParams.RequirePositive(ArriveRadius, nameof(ArriveRadius));

        for (int i = 0; i < Overrides.Length; i++)
        {
            CentipedeSegmentOverride? segmentOverride = Overrides[i];
            if (segmentOverride is null)
            {
                continue;
            }
            if ((uint)segmentOverride.SegmentIndex >= (uint)SegmentCount)
            {
                throw new ArgumentOutOfRangeException($"{nameof(Overrides)}[{i}].{nameof(CentipedeSegmentOverride.SegmentIndex)}",
                    segmentOverride.SegmentIndex, "Override segment index is outside the configured body.");
            }
        }

        // 在最终快照上校验覆写值，既避免重复规则，也能捕捉覆写组合后的非法结果。
        CentipedeSegmentParams[] resolved = ResolveSegmentsUnchecked();
        for (int i = 0; i < resolved.Length; i++)
        {
            resolved[i].Validate($"{nameof(Overrides)} resolved segment {i}");
        }
        return this;
    }

    /// <summary>
    /// 解析端点到中段的对称幂曲线并应用逐节覆写。每次调用都返回互不共享的逐节对象。
    /// 偶数节身体的两个中央节共享 MiddleRadius，避免长体出现肉眼可见的中心凹槽。
    /// </summary>
    public CentipedeSegmentParams[] ResolveSegments()
    {
        Validate();
        return ResolveSegmentsUnchecked();
    }

    private CentipedeSegmentParams[] ResolveSegmentsUnchecked()
    {
        var result = new CentipedeSegmentParams[SegmentCount];
        int centerDistance = (SegmentCount - 1) / 2;
        for (int i = 0; i < result.Length; i++)
        {
            CentipedeSegmentParams segment = BaseSegment.Copy();
            int distanceFromEnd = Math.Min(i, SegmentCount - 1 - i);
            float centerFactor = centerDistance == 0
                ? 0f
                : Math.Clamp(distanceFromEnd / (float)centerDistance, 0f, 1f);
            float shaped = MathF.Pow(centerFactor, ProfileExponent);
            segment.Radius = EndRadius + (MiddleRadius - EndRadius) * shaped;
            result[i] = segment;
        }

        foreach (CentipedeSegmentOverride? segmentOverride in Overrides)
        {
            if (segmentOverride is not null)
            {
                segmentOverride.ApplyTo(result[segmentOverride.SegmentIndex]);
            }
        }
        return result;
    }
}
