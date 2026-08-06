using System;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.Spider;

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
    /// 精瘦蜘蛛：按反编译的原作 <c>Spider</c>（群居小蜘蛛）比例换算，而不是沿用
    /// <see cref="SmallSpider"/> / <see cref="LargeSpider"/> 那两只 BigSpider 拓扑的自创比例。
    ///
    /// 取原作 <c>iVars.size = 0.6</c> 一档（1px = 0.025m）：
    /// 身体 <c>rad = Lerp(2,9,0.6) = 6.2px = 0.155m</c> 的**单球**、
    /// <c>limbLength = Lerp(10,40,0.6) = 28px = 0.70m</c>、
    /// 逐对总长系数 <c>{0.85, 1, 0.95, 0.9}</c>、上段占比 <c>{0.5, 0.6, 0.5, 0.65}</c>、
    /// 扇角 <c>Lerp(30°,140°,i/3)</c> 且第四对再 +20°（= 体轴 30/67/103/160°）。
    /// 结果是可达半径约为身体后节半径的 5.8 倍（原作小蜘蛛 4.5、BigSpider 6.1，
    /// 既有两只只有 2.5），即用户要的“身子小、腿细长”剪影。
    ///
    /// 三处有意偏离原作，各有理由：
    /// ① 原作是 1 chunk / 0 connection，本控制器要求有序链至少两节；这里改用
    ///    「头胸 0.085 + 腹 0.115、连接 0.10」的两球，总跨度 0.30m 与原作单球直径
    ///    0.31m 等价，顺带给出真实蜘蛛的两体节剪影。八条腿仍全部锚在第 0 节（≙ 原作
    ///    全挂 mainBodyChunk）。
    /// ② 原作腿是纯图形件（<c>Limb</c> 在 GraphicsModule，<c>ConnectToPoint</c> 从不写回
    ///    身体 chunk），移动是 <c>mainBodyChunk.vel += dir × 1.4</c>。本项目的腿真驱动身体，
    ///    因此 HuntSpeed 必须随腿长同比例上调，否则脚追不上身体（≙ M4「heavy 近瘫」教训）。
    /// ③ 逐对**总长**逐位取自原作，但上/下段的分割比压回 ±10%（原作 <c>{.5,.6,.5,.65}</c>）。
    ///    原作那组比例是 2D 贴图的 scaleY，其 <c>Limb.ConnectToPoint</c> 只钳最大距离、
    ///    没有下限；我们的两段 IK 有 <c>MinimumReach = |上段−下段|</c> 的内侧死区。照抄
    ///    会让第 4 对的死区达 0.19m（腿长 30%），地形 MTD 往内推、可达约束往外推，
    ///    <c>EnsureReachableAfterTerrain</c> 的四次交替投影不收敛，足端被兜底瞬移
    ///    0.9m（实测 course 路线膝跳变 105% > 88% 门）。保留「股节 ≥ 胫节」的原作朝向。
    ///
    /// 2026-08-06 场景门修复轮后 7 条场景路线 + gait 全绿（长腿轻身的三个结构性问题
    /// 与对应修复见 <see cref="SpiderBreedParams.KneeStepBudgetRatio"/>、
    /// <c>SpiderLeg.LimitNearRootWhip</c>、沙盒 <c>TurnArenaMinX</c>/<c>--gait-throttle</c>
    /// 与下方 touchdownLeadRatios 注释；CLAUDE.md 同轮段落有完整链路）。复现：七条场景
    /// 路线与既有两只共用命令（<c>--breed=spider-lean --route=course|turn|narrow-wall|
    /// wall-l|wall-ceiling</c>）；gait 为
    /// <c>--route=gait --spawn=11,0.55,8 --gait-throttle=0.6 --determinism=300</c>
    /// （快巡航品种在 24m 地板上给不出全油门 200 tick 观察窗，降油门是品种缩放变体）。
    /// 本预设仍不进回归矩阵；矩阵化前先重跑上述八条确认全绿。
    /// </summary>
    public static SpiderBreedParams LeanSpider()
    {
        var p = new SpiderBreedParams
        {
            Name = "spider-lean",
            BaseSpeed = 0.062f,
            MaxMoveSpeed = 0.09f,
            NoGripSpeed = 0.1f,
            MinGroundedLegs = 2,
            RegainFootingTicks = 3,
            LoseGripTicks = 6,
            // 身体轻小、腿长，姿态帧一快就带着 bend pole 和支撑法线甩：法线低通放慢、
            // 相位拉长、常驻抓地腿加到 4，急转时 poleDot 与 supportUp 才留得住。
            SupportBlend = 0.20f,
            GaitPhaseTicks = 9,
            MinPlantedLegs = 4,
            TrailingGain = 0.18f,
            LookAhead = 0.5f,
            RideHeight = 0.26f,
            StallReleaseTicks = 16,
            ConstraintIterations = 4,
            // 长腿 + 轻小身体的结构性问题：足端贴近腿根（d 低至 0.10~0.33，远小于
            // MaxReach 0.63~0.67）时，身体被约束甩动一次（实测 rootStep 0.24~0.26）
            // 就让腿轴单 tick 旋转 90°~130°，膝点作为 ~0.55L 的刚性杠杆沿圆瞬移
            // 0.9~1.04 倍腿长（course 实测）。余弦钳制管不住轴旋转（事发 tick 的
            // cos 全程在 0.61~0.75，[0.2,0.98] 带内），只能钳膝点位移本身。
            // 0.72 < turn 路线的 0.82 与通用 0.88 门，且高于全部路线的有机峰值
            // （wall-l 0.698）。既有两只保持 0（关闭），膝解算逐位不变。
            KneeStepBudgetRatio = 0.72f,
        };
        // 头胸与腹：两球跨度 0.085+0.10+0.115 = 0.30m ≈ 原作单球直径 0.31m。
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.085f,
            Mass = 0.12f,
        });
        p.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.115f,
            Mass = 0.24f,
            ConnectionLength = 0.10f,
            ConnectionWeight = 0.42f,
            ConnectionElasticity = 0.24f,
        });

        // 逐对总长 = limbLength × {0.85,1,0.95,0.9} = {0.596,0.700,0.666,0.630}，逐位照搬；
        // 分割比取 {.545,.55,.545,.555}（见上文 ③），死区落在腿长的 9%~12%。
        float[] upperLengths = { 0.325f, 0.385f, 0.363f, 0.350f };
        float[] lowerLengths = { 0.271f, 0.315f, 0.303f, 0.280f };
        // 本表的扇角从身体横轴量起（正值朝前）。原作的 30/67/103/160°（→ 60/23.3/-13.3/-70）
        // 同样是贴图扇角：最前/最后一对几乎沿体轴，横向分量被 MaxReach 钳制后挤没，实测
        // 急转后每条腿只剩名义站距的 67%（门 87.5%）。这里保留比既有两只更宽的展开，
        // 但收回已验证可行带。
        float[] fanAngles = { 56f, 20f, -20f, -56f };
        float[] longitudinalOffsets = { 0.05f, 0.02f, -0.01f, -0.04f };
        // AEP lead 不沿用 large 的前大后小递减（0.55..0.35）：冻结世界 AEP 在摆动+抓握的
        // ~7 tick 里被身体前进吃掉约「7×速度÷MaxReach」的相对超前量，lean 巡航快、
        // 腿长归一化后衰减 ≈0.6~0.9，后两对若沿用 0.40/0.33 会让落点恒在腿根之后，
        // gait 电池实测后腿平均复位只剩 0.04×腿长、微步 92%（落地即再抬的极限环）。
        // 抬到 0.45/0.48 后 gait（0.6 油门）rear 复位 0.288、微步 0，7 条场景路线余量
        // 同步改善（course 膝跳 0.72→0.67）。
        float[] touchdownLeadRatios = { 0.55f, 0.48f, 0.45f, 0.48f };
        for (int pair = 0; pair < 4; pair++)
        {
            p.LegPairs.Add(new LegPairSpec
            {
                AnchorSegmentIndex = 0,
                RootOffset = new Vector3(longitudinalOffsets[pair], 0f, 0f),
                RootLateralOffset = 0.045f,
                FanAngleDegrees = fanAngles[pair],
                PhaseGroup = pair & 1,
                OpposeSidePhase = true,
                UpperLength = upperLengths[pair],
                LowerLength = lowerLengths[pair],
                FootRadius = 0.022f,
                HuntSpeed = 0.24f,
                Quickness = 0.75f,
                GripDelay = 2,
                StepLength = 0.58f,
                TrailReleaseRatio = -1f,
                TouchdownLeadRatio = touchdownLeadRatios[pair],
                UseExplicitTouchdownLead = true,
                ForwardStepBias = 0f,
                LiftFeet = 0.26f,
                FeetDown = 0.38f,
                PairLateral = 0.46f,
                ProbeReach = 0.09f,
            });
        }
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
    public static SpiderBreedParams[] AllBreeds() =>
        new[] { SmallSpider(), LargeSpider(), LeanSpider() };

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
                    KneeStepBudgetRatio = p.KneeStepBudgetRatio,
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
