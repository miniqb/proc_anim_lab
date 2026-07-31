using System;

namespace ProcAnim.Core;

/// <summary>
/// 拟态草的纯出生配置。单位与内核一致：米、米/tick、固定 40 tick/s。
/// 工厂会校验并冻结一份快照；运行时不按预设名分支。
/// </summary>
public sealed class TentaclePlantParams
{
    public string Name = "tentacle-plant/original";

    // —— 原作装配：300px / 8 chunks，1px = 0.025m ——
    public float Length = 7.5f;
    public int SegmentCount = 8;
    public float RootRadius = 0.20f;
    public float TipRadius = 0.025f;
    public float HandVisualRadius = 0.20f;
    public float StrikeGrabRadius = 0.225f;
    public float RootMass = 0.20f;
    public float HandMass = 0.20f;
    public float TipMass = 0.05f;
    public float RootSurfaceOffset = 0.05f;
    public float SpawnExtension = 0.35f;

    // —— 多段绳链（≙ TentacleProps + TentaclePlant 的逐段力）——
    public float SegmentDamping = 0.96f;
    public float SegmentVelocityCap = 0.65f;
    public float TipGoalAttraction = 0.0125f;
    public float InnerGoalAttraction = 0.00125f;
    public float GuideAttraction = 0.00125f;
    public float BacktrackSpeed = 0.055f;
    public float OutwardRootForce = 0.0075f;
    public float ShapeSeparationForce = 0.025f;
    public float SelfAvoidanceStrength = 0.012f;
    public float SelfAvoidancePadding = 0.015f;
    public int ConstraintIterations = 3;
    // 0.20 让最粗的首段在完全回收时仍能留在安装面外；链是 PullOnly，
    // 后续段仍可盘卷到根边，不会被这个最大节长强行撑开。
    public float RetractedLengthFraction = 0.20f;

    // —— 前方无目的游走 ——
    public float WanderCenterDistance = 5.0f;
    public float WanderRadius = 3.75f;
    public float WanderStep = 0.125f;
    public float WanderJitter = 0.075f;
    public float WanderProbeDistance = 0.50f;
    public float WanderClearanceDistance = 1.0f;
    public int WanderResetMeanTicks = 170;
    public float WanderGoalCatchupDistance = 1.25f;

    // —— 3D 引导与避障：固定顺序、最多两个折点 ——
    public int GuideRefreshTicks = 3;
    public float GuideTargetMoveThreshold = 0.50f;
    public float GuideClearanceRadius = 0.21f;
    public float GuideSurfaceOffset = 0.12f;
    public float GuideDetourDistance = 0.65f;
    public float GuideForwardProgressEpsilon = 0.04f;
    public int RoutingQueryBudget = 48;
    public int BlockedSuffixTicks = 20;
    public int RebuildTicks = 15;

    // —— attack 标量时序（原作无额外 MissRecovery / cooldown）——
    public int ChargeTicks = 90;
    public int LungeTicks = 10;
    public int GrabWindowTicks = 40;
    public int RetractTicks = 80;
    public int ConsumeForceTicks = 30;
    public float WindupStart = 0.50f;
    public float WindupPull = 0.020f;
    public float LungeImpulse = 0.50f;
    public float PredictionTicksPerMeter = 1.20f;
    public float LowRelativeGrabSpeed = 0.025f;
    public float ConsumeDistance = 0.50f;
    public float CarryHandMass = 0.10f;
    public float CarryVelocityGain = 0.20f;
    public float CarryRootPull = 0.05f;

    /// <summary>完整校验；无效出生配置快速失败，不静默夹值。</summary>
    public TentaclePlantParams Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException($"{nameof(Name)} must not be empty.", nameof(Name));
        }
        Positive(Length, nameof(Length));
        if (SegmentCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentCount), SegmentCount,
                "A tentacle plant requires at least three segments.");
        }
        Positive(RootRadius, nameof(RootRadius));
        Positive(TipRadius, nameof(TipRadius));
        if (TipRadius > RootRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(TipRadius), TipRadius,
                $"{nameof(TipRadius)} must not exceed {nameof(RootRadius)}.");
        }
        Positive(HandVisualRadius, nameof(HandVisualRadius));
        Positive(StrikeGrabRadius, nameof(StrikeGrabRadius));
        Positive(RootMass, nameof(RootMass));
        Positive(HandMass, nameof(HandMass));
        Positive(TipMass, nameof(TipMass));
        NonNegative(RootSurfaceOffset, nameof(RootSurfaceOffset));
        if (RootSurfaceOffset >= RootRadius)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RootSurfaceOffset),
                RootSurfaceOffset,
                $"{nameof(RootSurfaceOffset)} must keep the root center buried within {nameof(RootRadius)}.");
        }
        UnitPositive(SpawnExtension, nameof(SpawnExtension));
        float minimumSurfaceLink = Math.Max(0f, RootRadius - RootSurfaceOffset);
        float spawnLinkLength = Length * SpawnExtension / SegmentCount;
        if (spawnLinkLength < minimumSurfaceLink)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SpawnExtension),
                SpawnExtension,
                "Spawn first-link length must keep the root segment outside the mount surface.");
        }

        UnitPositive(SegmentDamping, nameof(SegmentDamping));
        Positive(SegmentVelocityCap, nameof(SegmentVelocityCap));
        NonNegative(TipGoalAttraction, nameof(TipGoalAttraction));
        NonNegative(InnerGoalAttraction, nameof(InnerGoalAttraction));
        NonNegative(GuideAttraction, nameof(GuideAttraction));
        NonNegative(BacktrackSpeed, nameof(BacktrackSpeed));
        NonNegative(OutwardRootForce, nameof(OutwardRootForce));
        NonNegative(ShapeSeparationForce, nameof(ShapeSeparationForce));
        NonNegative(SelfAvoidanceStrength, nameof(SelfAvoidanceStrength));
        NonNegative(SelfAvoidancePadding, nameof(SelfAvoidancePadding));
        if (ConstraintIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConstraintIterations), ConstraintIterations,
                $"{nameof(ConstraintIterations)} must be at least one.");
        }
        UnitPositive(RetractedLengthFraction, nameof(RetractedLengthFraction));
        float retractedLinkLength = Length * RetractedLengthFraction / SegmentCount;
        if (retractedLinkLength < minimumSurfaceLink)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetractedLengthFraction),
                RetractedLengthFraction,
                "Fully retracted first-link length must keep the root segment outside the mount surface.");
        }

        Positive(WanderCenterDistance, nameof(WanderCenterDistance));
        Positive(WanderRadius, nameof(WanderRadius));
        if (WanderRadius >= WanderCenterDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(WanderRadius), WanderRadius,
                $"{nameof(WanderRadius)} must stay smaller than {nameof(WanderCenterDistance)}.");
        }
        Positive(WanderStep, nameof(WanderStep));
        NonNegative(WanderJitter, nameof(WanderJitter));
        Positive(WanderProbeDistance, nameof(WanderProbeDistance));
        Positive(WanderClearanceDistance, nameof(WanderClearanceDistance));
        Positive(WanderResetMeanTicks, nameof(WanderResetMeanTicks));
        Positive(WanderGoalCatchupDistance, nameof(WanderGoalCatchupDistance));

        Positive(GuideRefreshTicks, nameof(GuideRefreshTicks));
        Positive(GuideTargetMoveThreshold, nameof(GuideTargetMoveThreshold));
        Positive(GuideClearanceRadius, nameof(GuideClearanceRadius));
        if (GuideClearanceRadius < RootRadius)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GuideClearanceRadius),
                GuideClearanceRadius,
                $"{nameof(GuideClearanceRadius)} must cover the thickest chain segment.");
        }
        Positive(GuideSurfaceOffset, nameof(GuideSurfaceOffset));
        Positive(GuideDetourDistance, nameof(GuideDetourDistance));
        NonNegative(GuideForwardProgressEpsilon, nameof(GuideForwardProgressEpsilon));
        Positive(RoutingQueryBudget, nameof(RoutingQueryBudget));
        Positive(BlockedSuffixTicks, nameof(BlockedSuffixTicks));
        Positive(RebuildTicks, nameof(RebuildTicks));

        Positive(ChargeTicks, nameof(ChargeTicks));
        Positive(LungeTicks, nameof(LungeTicks));
        Positive(GrabWindowTicks, nameof(GrabWindowTicks));
        Positive(RetractTicks, nameof(RetractTicks));
        Positive(ConsumeForceTicks, nameof(ConsumeForceTicks));
        UnitPositive(WindupStart, nameof(WindupStart));
        NonNegative(WindupPull, nameof(WindupPull));
        Positive(LungeImpulse, nameof(LungeImpulse));
        NonNegative(PredictionTicksPerMeter, nameof(PredictionTicksPerMeter));
        NonNegative(LowRelativeGrabSpeed, nameof(LowRelativeGrabSpeed));
        Positive(ConsumeDistance, nameof(ConsumeDistance));
        Positive(CarryHandMass, nameof(CarryHandMass));
        NonNegative(CarryVelocityGain, nameof(CarryVelocityGain));
        NonNegative(CarryRootPull, nameof(CarryRootPull));
        return this;
    }

    internal TentaclePlantParams Snapshot()
    {
        var copy = (TentaclePlantParams)MemberwiseClone();
        copy.Validate();
        return copy;
    }

    private static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be finite and greater than zero.");
        }
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be greater than zero.");
        }
    }

    private static void NonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be finite and non-negative.");
        }
    }

    private static void UnitPositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be finite and in (0, 1].");
        }
    }
}
