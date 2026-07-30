using System;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 蜘蛛品种预设与通用装配器。与 <see cref="BodyFactory"/> 平行：只共享 Body/ChunkConnection
/// 原语，不创建蜥蜴控制器或 Limb。身体按配置列表装成一条有序刚性链，腿对显式挂载到指定节。
/// </summary>
public static class SpiderFactory
{
    private const float MaxWorldScalar = 1_000_000f;
    private const int MaxConfigTicks = 1_000_000;
    private const int MaxConstraintIterations = 256;

    /// <summary>
    /// 小型蜘蛛：两节身体、四对短腿全部挂在第 0 节，第 1 节无腿。
    /// 快速脚端、短抓握延迟与较短相位形成轻快的交替步态。
    /// </summary>
    public static SpiderBreedParams SmallSpider()
    {
        var p = new SpiderBreedParams
        {
            Name = "spider-small",
            BaseSpeed = 0.06f,
            MaxMoveSpeed = 0.09f,
            NoGripSpeed = 0.1f,
            MinGroundedLegs = 2,
            RegainFootingTicks = 3,
            LoseGripTicks = 6,
            SupportBlend = 0.24f,
            GaitPhaseTicks = 8,
            MinPlantedLegs = 3,
            TrailingGain = 0.2f,
            LookAhead = 0.44f,
            RideHeight = 0.18f,
            StallReleaseTicks = 16,
            ConstraintIterations = 4,
        };
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.16f,
            Mass = 0.36f,
        });
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.20f,
            Mass = 0.54f,
            ConnectionLength = 0.27f,
            ConnectionWeight = 0.45f,
            ConnectionElasticity = 0.22f,
        });

        AddFourLegPairs(p,
            longitudinalOffsets: new[] { 0.10f, 0.035f, -0.035f, -0.10f },
            fanAngles: new[] { 48f, 17f, -17f, -48f },
            upperLength: 0.24f,
            lowerLength: 0.29f,
            rootLateral: 0.065f,
            pairLateral: 0.40f,
            footRadius: 0.032f,
            huntSpeed: 0.18f,
            quickness: 0.72f,
            gripDelay: 2,
            stepLength: 0.58f,
            trailReleaseRatio: -1f,
            touchdownLeadRatios: new[] { 0.55f, 0.50f, 0.45f, 0.40f },
            forwardStepBias: 0f,
            liftFeet: 0.22f,
            feetDown: 0.42f,
            probeReach: 0.07f);
        return p;
    }

    /// <summary>
    /// 大型蜘蛛：两节大体型、四对长腿全部挂在第 0 节，第 1 节无腿。
    /// 更宽的站姿、较慢脚端和更长抓地确认令其换面较慢但支撑稳定。
    /// </summary>
    public static SpiderBreedParams LargeSpider()
    {
        var p = new SpiderBreedParams
        {
            Name = "spider-large",
            BaseSpeed = 0.048f,
            MaxMoveSpeed = 0.072f,
            NoGripSpeed = 0.055f,
            MinGroundedLegs = 3,
            RegainFootingTicks = 5,
            LoseGripTicks = 10,
            SupportBlend = 0.16f,
            GaitPhaseTicks = 12,
            MinPlantedLegs = 4,
            TrailingGain = 0.15f,
            LookAhead = 0.60f,
            RideHeight = 0.30f,
            StallReleaseTicks = 22,
            ConstraintIterations = 6,
        };
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.25f,
            Mass = 1.05f,
        });
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.34f,
            Mass = 1.75f,
            ConnectionLength = 0.40f,
            ConnectionWeight = 0.40f,
            ConnectionElasticity = 0.28f,
        });

        AddFourLegPairs(p,
            longitudinalOffsets: new[] { 0.16f, 0.055f, -0.055f, -0.16f },
            fanAngles: new[] { 52f, 19f, -19f, -52f },
            upperLength: 0.42f,
            lowerLength: 0.48f,
            rootLateral: 0.11f,
            pairLateral: 0.68f,
            footRadius: 0.052f,
            huntSpeed: 0.125f,
            quickness: 0.52f,
            gripDelay: 4,
            stepLength: 0.68f,
            trailReleaseRatio: 0.38f,
            touchdownLeadRatios: new[] { 0.55f, 0.48f, 0.40f, 0.35f },
            forwardStepBias: 0.35f,
            liftFeet: 0.28f,
            feetDown: 0.55f,
            probeReach: 0.11f);
        return p;
    }

    /// <summary>
    /// 只供核心测试的三节、多锚点拓扑：三对腿分别挂在第 0/1/2 节。
    /// 它证明装配和控制器没有把“两节身体、腿只在第一节”的正式预设写死。
    /// </summary>
    public static SpiderBreedParams SyntheticMultiAnchor()
    {
        var p = new SpiderBreedParams
        {
            Name = "spider-synthetic-multi-anchor",
            MinGroundedLegs = 2,
            MinPlantedLegs = 3,
            GaitPhaseTicks = 9,
            RideHeight = 0.19f,
            ConstraintIterations = 5,
        };
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.15f,
            Mass = 0.34f,
        });
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.18f,
            Mass = 0.46f,
            ConnectionLength = 0.25f,
            ConnectionWeight = 0.48f,
            ConnectionElasticity = 0.2f,
        });
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.21f,
            Mass = 0.62f,
            ConnectionLength = 0.29f,
            ConnectionWeight = 0.44f,
            ConnectionElasticity = 0.24f,
        });

        for (int pair = 0; pair < 3; pair++)
        {
            p.LegPairs.Add(new LegPairSpec
            {
                AnchorSegmentIndex = pair,
                RootOffset = new Vector3(pair == 0 ? 0.07f : pair == 2 ? -0.07f : 0f, 0f, 0f),
                RootLateralOffset = 0.065f,
                FanAngleDegrees = pair == 0 ? 30f : pair == 2 ? -30f : 0f,
                PhaseGroup = pair & 1,
                OpposeSidePhase = true,
                UpperLength = 0.25f,
                LowerLength = 0.30f,
                FootRadius = 0.032f,
                HuntSpeed = 0.16f,
                Quickness = 0.65f,
                GripDelay = 3,
                StepLength = 0.62f,
                TouchdownLeadRatio = 0.18f,
                UseExplicitTouchdownLead = true,
                LiftFeet = 0.22f,
                FeetDown = 0.46f,
                PairLateral = 0.42f,
                ProbeReach = 0.08f,
            });
        }
        return p;
    }

    /// <summary>沙盒正式实例表；测试专用的 SyntheticMultiAnchor 不列入玩家切换序列。</summary>
    public static SpiderBreedParams[] AllBreeds() => new[] { SmallSpider(), LargeSpider() };

    /// <summary>按稳定名称取正式预设；未知名称快速失败，避免命令行拼写错误静默换品种。</summary>
    public static SpiderBreedParams ByName(string name)
    {
        foreach (SpiderBreedParams p in AllBreeds())
        {
            if (p.Name == name)
            {
                return p;
            }
        }
        throw new ArgumentException($"Unknown spider breed '{name}'.", nameof(name));
    }

    public static SpiderLocomotionController CreateSpiderController(Vector3 origin) =>
        CreateSpiderController(origin, SmallSpider());

    /// <summary>
    /// 将品种展开为独立运行态。origin 表示出生支撑面附近的参考点；第 0 节球心会在其上方
    /// 留出「最大身体半径 + 最大脚半径 + 5cm」净空。身体从第 0 节开始沿世界 +X
    /// 依次向后出生，因此初始前向是 -X；后续朝向完全由输入与支撑反馈连续更新。
    /// </summary>
    public static SpiderLocomotionController CreateSpiderController(Vector3 origin, SpiderBreedParams p)
    {
        Validate(p);
        if (!IsFinite(origin) || MaxAbs(origin) > MaxWorldScalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin), "Spider origin must be finite and within the supported world range.");
        }

        float maxRadius = 0f;
        foreach (BodySegmentSpec spec in p.BodySegments)
        {
            maxRadius = Mathf.Max(maxRadius, spec.Radius);
        }
        float maxFootRadius = 0f;
        foreach (LegPairSpec spec in p.LegPairs)
        {
            maxFootRadius = Mathf.Max(maxFootRadius, spec.FootRadius);
        }
        float clearance = maxRadius + maxFootRadius + 0.05f;

        var body = new Body
        {
            ConstraintIterations = Mathf.Max(1, p.ConstraintIterations),
        };
        var chunks = new BodyChunk[p.BodySegments.Count];
        Vector3 position = origin + Vector3.Up * clearance;
        for (int i = 0; i < chunks.Length; i++)
        {
            BodySegmentSpec spec = p.BodySegments[i];
            if (i > 0)
            {
                position += Vector3.Right * spec.ConnectionLength;
            }
            chunks[i] = new BodyChunk(position, spec.Radius, spec.Mass);
            body.Chunks.Add(chunks[i]);
        }
        for (int i = 1; i < chunks.Length; i++)
        {
            BodySegmentSpec spec = p.BodySegments[i];
            body.Connections.Add(new ChunkConnection(
                chunks[i - 1], chunks[i], spec.ConnectionLength, spec.ConnectionWeight)
            {
                ConstraintMode = ChunkConnection.Mode.Rigid,
                Elasticity = spec.ConnectionElasticity,
            });
        }

        // ChunkConnection 构造的互绑会被后建连接覆盖。装配完显式重申有序身体朝向：
        // 首节参照链尾（全身前向），中间节参照下一节（局部链轴），链尾参照首节（后向）。
        chunks[0].RotationChunk = chunks[^1];
        for (int i = 1; i < chunks.Length - 1; i++)
        {
            chunks[i].RotationChunk = chunks[i + 1];
        }
        chunks[^1].RotationChunk = chunks[0];

        var controller = new SpiderLocomotionController(body, chunks)
        {
            BaseSpeed = p.BaseSpeed,
            MaxMoveSpeed = p.MaxMoveSpeed,
            NoGripSpeed = p.NoGripSpeed,
            MinGroundedLegs = p.MinGroundedLegs,
            RegainFootingTicks = p.RegainFootingTicks,
            LoseGripTicks = p.LoseGripTicks,
            SupportBlend = p.SupportBlend,
            GaitPhaseTicks = p.GaitPhaseTicks,
            MinPlantedLegs = p.MinPlantedLegs,
            TrailingGain = p.TrailingGain,
            LookAhead = p.LookAhead,
            RideHeight = p.RideHeight,
            StallReleaseTicks = p.StallReleaseTicks,
        };

        Vector3 forward = Vector3.Left;
        Vector3 support = Vector3.Up;
        Vector3 right = forward.Cross(support).Normalized();
        for (int pairIndex = 0; pairIndex < p.LegPairs.Count; pairIndex++)
        {
            LegPairSpec spec = p.LegPairs[pairIndex];
            var pair = new SpiderLeg[2];
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                int side = sideIndex == 0 ? -1 : 1;
                BodyChunk anchor = chunks[spec.AnchorSegmentIndex];
                Vector3 root = anchor.Pos
                    + forward * spec.RootOffset.X
                    + support * spec.RootOffset.Y
                    + right * (spec.RootOffset.Z + side * spec.RootLateralOffset);
                float fanAngle = Mathf.DegToRad(spec.FanAngleDegrees);
                Vector3 fanDirection = right * (side * Mathf.Cos(fanAngle))
                    + forward * Mathf.Sin(fanAngle);
                Vector3 initialDirection = fanDirection.Normalized() * 0.68f
                    - support * 0.35f;
                SpiderLeg.CalculateReachBounds(
                    spec.UpperLength,
                    spec.LowerLength,
                    SpiderLeg.DefaultMaxReachFactor,
                    out float minimumReach,
                    out float maximumReach,
                    out _);
                float initialDistance = Mathf.Clamp(
                    initialDirection.Length() * (spec.UpperLength + spec.LowerLength),
                    minimumReach,
                    maximumReach);
                Vector3 initialFoot = root
                    + initialDirection.Normalized() * initialDistance;

                pair[sideIndex] = new SpiderLeg(
                    anchor, initialFoot, spec.FootRadius, side, pairIndex)
                {
                    PhaseGroup = spec.PhaseGroup
                        ^ (spec.OpposeSidePhase && side > 0 ? 1 : 0),
                    RootOffset = spec.RootOffset,
                    RootLateralOffset = spec.RootLateralOffset,
                    FanAngle = fanAngle,
                    UpperLength = spec.UpperLength,
                    LowerLength = spec.LowerLength,
                    HuntSpeed = spec.HuntSpeed,
                    Quickness = spec.Quickness,
                    GripDelay = spec.GripDelay,
                    AcquisitionTimeoutTicks = spec.AcquisitionTimeoutTicks,
                    StepLength = spec.StepLength,
                    TrailReleaseRatio = spec.TrailReleaseRatio,
                    TouchdownLeadRatio = spec.TouchdownLeadRatio,
                    UseExplicitTouchdownLead = spec.UseExplicitTouchdownLead,
                    ForwardStepBias = spec.ForwardStepBias,
                    LiftFeet = spec.LiftFeet,
                    FeetDown = spec.FeetDown,
                    PairLateral = spec.PairLateral,
                    ProbeReach = spec.ProbeReach,
                };
                pair[sideIndex].InitializePose(forward, support);
                controller.Legs.Add(pair[sideIndex]);
            }
            pair[0].Pair = pair[1];
            pair[1].Pair = pair[0];
        }

        return controller;
    }

    private static void AddFourLegPairs(
        SpiderBreedParams p,
        float[] longitudinalOffsets,
        float[] fanAngles,
        float upperLength,
        float lowerLength,
        float rootLateral,
        float pairLateral,
        float footRadius,
        float huntSpeed,
        float quickness,
        int gripDelay,
        float stepLength,
        float trailReleaseRatio,
        float[] touchdownLeadRatios,
        float forwardStepBias,
        float liftFeet,
        float feetDown,
        float probeReach)
    {
        if (longitudinalOffsets.Length != 4
            || fanAngles.Length != 4
            || touchdownLeadRatios.Length != 4)
        {
            throw new ArgumentException("Four-leg-pair presets require four values per pair.");
        }
        for (int pair = 0; pair < 4; pair++)
        {
            p.LegPairs.Add(new LegPairSpec
            {
                AnchorSegmentIndex = 0,
                RootOffset = new Vector3(longitudinalOffsets[pair], 0f, 0f),
                RootLateralOffset = rootLateral,
                FanAngleDegrees = fanAngles[pair],
                PhaseGroup = pair & 1,
                OpposeSidePhase = true,
                UpperLength = upperLength,
                LowerLength = lowerLength,
                FootRadius = footRadius,
                HuntSpeed = huntSpeed,
                Quickness = quickness,
                GripDelay = gripDelay,
                StepLength = stepLength,
                TrailReleaseRatio = trailReleaseRatio,
                TouchdownLeadRatio = touchdownLeadRatios[pair],
                UseExplicitTouchdownLead = true,
                ForwardStepBias = forwardStepBias,
                LiftFeet = liftFeet,
                FeetDown = feetDown,
                PairLateral = pairLateral,
                ProbeReach = probeReach,
            });
        }
    }

    private static void Validate(SpiderBreedParams p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.BodySegments.Count < 2)
        {
            throw new ArgumentException("SpiderBreedParams requires at least two ordered body segments.", nameof(p));
        }
        if (p.LegPairs.Count == 0)
        {
            throw new ArgumentException("SpiderBreedParams requires at least one leg pair.", nameof(p));
        }
        int legCount = p.LegPairs.Count * 2;
        if (string.IsNullOrWhiteSpace(p.Name)
            || !float.IsFinite(p.BaseSpeed) || p.BaseSpeed <= 0f
            || !float.IsFinite(p.MaxMoveSpeed) || p.MaxMoveSpeed <= 0f
            || !InUnitRange(p.NoGripSpeed)
            || p.MinGroundedLegs < 1 || p.MinGroundedLegs > legCount
            // FootingCounter 的稳定上限是 100；超出后会形成永远无法关重力的伪合法配置。
            || p.RegainFootingTicks is < 1 or > 100
            || p.LoseGripTicks is < 0 or > MaxConfigTicks
            || !float.IsFinite(p.SupportBlend) || p.SupportBlend <= 0f || p.SupportBlend > 1f
            || p.GaitPhaseTicks is < 1 or > MaxConfigTicks
            || p.MinPlantedLegs < 0 || p.MinPlantedLegs > legCount
            || !FinitePositiveWithin(p.BaseSpeed, MaxWorldScalar)
            || !FinitePositiveWithin(p.MaxMoveSpeed, MaxWorldScalar)
            || !FiniteWithin(p.TrailingGain, 0f, MaxWorldScalar)
            || !FinitePositiveWithin(p.LookAhead, MaxWorldScalar)
            || !FiniteWithin(p.RideHeight, 0f, MaxWorldScalar)
            || p.StallReleaseTicks is < 1 or > MaxConfigTicks
            || p.ConstraintIterations is < 1 or > MaxConstraintIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(p),
                "Spider locomotion parameters contain an invalid range or tick count.");
        }
        for (int i = 0; i < p.BodySegments.Count; i++)
        {
            BodySegmentSpec segment = p.BodySegments[i]
                ?? throw new ArgumentException($"Body segment {i} is null.", nameof(p));
            if (!FinitePositiveWithin(segment.Radius, MaxWorldScalar)
                || !FinitePositiveWithin(segment.Mass, MaxWorldScalar))
            {
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Body segment {i} must have finite positive radius and mass.");
            }
            if (i > 0
                && (!FinitePositiveWithin(segment.ConnectionLength, MaxWorldScalar)
                    || !InUnitRange(segment.ConnectionWeight)
                    || !InUnitRange(segment.ConnectionElasticity)))
            {
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Body segment {i} has an invalid connection length, weight, or elasticity.");
            }
        }
        for (int i = 0; i < p.LegPairs.Count; i++)
        {
            LegPairSpec leg = p.LegPairs[i]
                ?? throw new ArgumentException($"Leg pair {i} is null.", nameof(p));
            if (leg.AnchorSegmentIndex < 0 || leg.AnchorSegmentIndex >= p.BodySegments.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Leg pair {i} references body segment {leg.AnchorSegmentIndex}.");
            }
            SpiderLeg.CalculateReachBounds(
                leg.UpperLength,
                leg.LowerLength,
                SpiderLeg.DefaultMaxReachFactor,
                out float minimumReach,
                out float maximumReach,
                out float theoreticalMaximumReach);
            if (!FiniteWithin(leg.UpperLength, 1e-4f, MaxWorldScalar)
                || !FiniteWithin(leg.LowerLength, 1e-4f, MaxWorldScalar)
                || !FinitePositiveWithin(leg.FootRadius, MaxWorldScalar)
                || !FiniteWithin(leg.HuntSpeed, 0.001f, MaxWorldScalar)
                || !float.IsFinite(leg.Quickness) || leg.Quickness <= 0f
                || !FiniteWithin(leg.RootLateralOffset, 0f, MaxWorldScalar)
                || !IsFinite(leg.RootOffset) || MaxAbs(leg.RootOffset) > MaxWorldScalar
                || !float.IsFinite(leg.FanAngleDegrees)
                || leg.PhaseGroup is < 0 or > 1
                || !FiniteWithin(leg.PairLateral, 0f, MaxWorldScalar)
                || !FiniteWithin(leg.ProbeReach, 0f, MaxWorldScalar)
                || !float.IsFinite(minimumReach)
                || !float.IsFinite(maximumReach)
                || !float.IsFinite(theoreticalMaximumReach)
                || !(minimumReach < maximumReach
                    && maximumReach < theoreticalMaximumReach))
            {
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Leg pair {i} contains a non-finite or non-positive physical parameter.");
            }
            if (!InUnitRange(leg.Quickness) || !InUnitRange(leg.StepLength)
                || !InUnitRange(leg.LiftFeet) || !InUnitRange(leg.FeetDown)
                || (leg.TrailReleaseRatio != -1f
                    && !InUnitRange(leg.TrailReleaseRatio))
                || !float.IsFinite(leg.TouchdownLeadRatio)
                || (leg.UseExplicitTouchdownLead
                    && (leg.TouchdownLeadRatio < -1f
                        || leg.TouchdownLeadRatio > 1f))
                || !InUnitRange(leg.ForwardStepBias)
                || leg.GripDelay is < 1 or > MaxConfigTicks
                || leg.AcquisitionTimeoutTicks is < 1 or > MaxConfigTicks)
            {
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Leg pair {i} contains an invalid normalized or tick parameter.");
            }
        }

        float maxRadius = 0f;
        float maxFootRadius = 0f;
        float chainLength = 0f;
        for (int i = 0; i < p.BodySegments.Count; i++)
        {
            BodySegmentSpec segment = p.BodySegments[i];
            maxRadius = Mathf.Max(maxRadius, segment.Radius);
            if (i > 0)
            {
                chainLength += segment.ConnectionLength;
            }
            if (!float.IsFinite(chainLength) || chainLength > MaxWorldScalar)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(p), "Spider body chain exceeds the supported world range.");
            }
        }
        foreach (LegPairSpec leg in p.LegPairs)
        {
            maxFootRadius = Mathf.Max(maxFootRadius, leg.FootRadius);
        }
        float clearance = maxRadius + maxFootRadius + 0.05f;
        if (!float.IsFinite(clearance) || clearance > MaxWorldScalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(p), "Spider spawn clearance exceeds the supported world range.");
        }
    }

    private static bool InUnitRange(float value) =>
        float.IsFinite(value) && value >= 0f && value <= 1f;

    private static bool FinitePositiveWithin(float value, float maximum) =>
        float.IsFinite(value) && value > 0f && value <= maximum;

    private static bool FiniteWithin(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static float MaxAbs(Vector3 value) =>
        Mathf.Max(Mathf.Abs(value.X), Mathf.Max(Mathf.Abs(value.Y), Mathf.Abs(value.Z)));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
