using System;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>
/// DaddyLongLegs 的纯出生配置。它描述一个品种的确定性形态分布和运动常量；
/// 某一只个体的实际球数、半径、质量、触手长度与段数由工厂结合 stable seed 冻结。
/// 单位为米、米/tick 和固定 40 tick/s。
/// </summary>
public sealed class DaddyLongLegsParams
{
    public string StableId = "daddy-long-legs/custom";

    // —— 出生形态：原作的预算式随机装配改为 seed 域分离的无状态采样 ——
    public int MinimumBodyChunks = 4;
    public int MaximumBodyChunks = 7;
    public float BodyBudgetUnits = 12f;
    public float BodyRadiusBase = 0.0875f; // 3.5 px
    public float BodyRadiusPerBudgetUnit = 0.0875f; // 3.5 px
    public float BodyMassPerBudgetUnit = 1f;
    public int RestLayoutIterations = 5;
    public float RestOverlapRatio = 0.85f;
    public float RestContraction = 0.90f;

    public int MinimumTentacles = 5;
    public int MaximumTentacles = 12;
    public float TentacleBudgetBase = 75f; // 3000 px
    public float TentacleBudgetPerTentacle = 10f; // 400 px
    public float TentacleBudgetBlend = 0.50f;
    public int LengthRedistributionPassesPerTentacle = 5;
    public float MaximumRedistributionFraction = 0.30f;
    public float MinimumTentacleLength = 2.50f; // 100 px
    public float NominalSegmentLength = 1.00f; // 40 px
    public int MinimumSegmentsPerTentacle = 3;
    public int MaximumSegmentsPerTentacle = 18;
    public int MaximumTotalTentacleSegments = 168;
    public float SegmentRadius = 0.075f; // 3 px
    public float SegmentMass = 0.04f;
    public float SpawnExtension = 0.34f;

    // —— 身体与触手积分 ——
    public int BodyConstraintIterations = 7;
    public float UnsupportedAirFriction = 0.999f;
    public float SupportedAirFriction = 0.95f;
    public float UnsupportedSurfaceFriction = 0.40f;
    public float SupportedSurfaceFriction = 0.72f;
    public float TerrainSkin = 0.02f;
    public float SegmentDamping = 0.90f;
    public float SegmentVelocityCap = 0.28f;
    // 被动碰撞只清入面速度；主动抓附才额外耗散切向速度。两者再共同乘上方 .90 damping，
    // 分别对应原作约 .90 / .81 的 tick 末切向保留率。
    public float SegmentCollisionFriction = 1.00f;
    public float SegmentSurfaceFriction = 0.90f;
    public float SegmentPathServo = 0.032f;
    public float SegmentTargetServo = 0.055f;
    public float SegmentRootSpreadForce = 0.018f;
    public float SegmentSelfSeparation = 0.25f; // 原作 PushChunksApart 的 10 px
    public float LimpGravityScale = 1.35f;
    public int StepPeelTicks = 6;
    public int StepPeelMaximumTicks = 12;
    public float StepPeelVelocity = 0.040f;
    public float StepPeelStartFraction = 0.45f;
    public float StepPeelMaximumContactFraction = 0.20f;
    public float StepPeelRetractionServo = 0.018f;
    public int StepArrivalMinimumTicks = 2;
    public int TentacleConstraintIterations = 4;
    public int ResidualTerrainResolveIterations = 4;
    // 最终约束/自避可能把段重新推过薄墙。逐链环的障碍证据会即时截断远端抓附，
    // 连续若干 tick 后才执行确定性整后缀重建，避免三角形接缝的单帧命中造成整腿跳变。
    public int TerrainBacktrackReleaseTicks = 6;
    public float TerrainBacktrackRetractionSpeed = 0.080f;
    public int TerrainBacktrackCandidatePhases = 12;

    // —— 整链贴附与落点搜索 ——
    public float IdealReachRatio = 0.70f;
    public float SearchReachMinimumRatio = 0.70f;
    public float SearchReachMaximumRatio = 1.20f;
    public float PreferenceMoveBlend = 0.50f;
    public float StuckMoveBlend = 0.95f;
    public float SearchJitterMaximum = 0.92f;
    public int SearchFailureExpandTicks = 180;
    public int SearchRefreshTicks = 4;
    public int SearchRayCount = 12;
    public float SearchConeMinimumDegrees = 62f;
    public float SearchConeMaximumDegrees = 168f;
    public float LandingSurfaceOffset = 0.02f;
    public float GrabArrivalRadius = 0.50f; // 20 px
    public float ContactProbeExtra = 0.035f;
    public float ContactTargetRange = 5.0f; // 200 px
    public float GuideContactSlackShare = 0.55f;
    public float GuideMaximumContactRatio = 0.24f;
    public float GuideTargetLengthRatio = 0.985f;
    public float GuideSlackArchShare = 0.60f;
    public float GuideBendMaximumRatio = 1.25f;
    // 余长鼓弧不查地形，墙边可能持续鼓进 collider：段被伺服压向墙内目标，guide
    // obstruction 每 6 tick 如实释放一个本来有效的落点，随后重搜又选回同一点，
    // 形成静止墙边的无限“清点-重搜”循环（2026-08-05 wall-idle 实测）。对策是把
    // 拱弧幅度做成逐触手自适应：观察到 obstruction 当 tick 立即按此步长衰减，
    // 4 tick 内压平（先于 6 tick 释放窗），无障碍时缓慢恢复；真实阻塞（目标在墙
    // 另一侧）仍按原释放路径清点重搜。
    public float GuideObstructionArchDecayPerTick = 0.25f;
    public float GuideArchRecoveryPerTick = 0.01f;
    public bool EnableGuideArchAdaptation = true;
    public float GuideTangentHandleRatio = 0.32f;
    public float GuideSurfaceApproachNormalWeight = 0.45f;
    public float SurfaceAdvanceNormalWeight = 1.00f;
    // 同面重搜保留锚点到抓面的法向跨度，并把面内前移封顶为腿长的一小部分。
    // 该局部构造依附抓面法线，不使用世界 Up；其余射线仍负责棱角/换面。
    public float SurfaceSpanReachRatio = 0.985f;
    public float SurfaceSpanAdvanceMaximumRatio = 0.25f;
    public int LandingCommitTicks = 20;
    // 原作 Climb 对 !atGrabDest 触手每 tick 调 UpdateClimbGrabPos——在途落点持续跟随
    // 身体的 idealGrabPos 前移。3D 版保留候选评分/同面跨度结构，只把移动 episode 内
    // 未到达腿的重瞄提交年龄降到搜索节拍，避免落点冻结导致“到达即落后”。
    public int MovingReplantRetargetTicks = 4;
    public int LandingValidationTicks = 6;
    // 静止落点的短复验射线在墙角/三角形接缝可能瞬时命中相邻面；连续失败才失效。
    public int IdleLandingValidationFailures = 3;
    public int IdleProximityProbeTicks = 8;
    public float IdleProximityProbeLength = 2.5f;

    // —— 连续支撑、职责预算与全团推进 ——
    public float GripFractionExponent = 0.50f;
    public float ArrivedSupportWeight = 0.75f;
    public float SupportResponseExponent = 0.21f;
    public float SupportBlend = 0.32f;
    public float GravityCancellationGain = 1.20f;
    public float IdleGravityCancellationMaximum = 1.00f;
    // 共享 Body 在约束/碰撞前做 AirFriction；3D 墙面完整图投影会在其后重新写入共同速度。
    // 停驶后再衰减一次仅有的质心平移，不改内部相对速度，也不制造力偶。
    public float IdleBodyVelocityRetention = 0.80f;
    public float UnconditionalSupportDecayPerTick = 0.025f;
    public int MinimumLocomotionTentacles = 2;
    public int ReservedIdleTentacles = 1;
    public float ReleaseSupportThreshold = 0.85f;
    public int DutyChangeCooldownTicks = 5;
    public float DirectionalSupportDotFloor = -0.10f;
    public float StuckDirectionalSupportDotFloor = -1.00f;
    public float DirectionalSupportDotCeiling = 0.85f;
    public float DirectionalSupportExponent = 0.80f;
    public float DirectionalCouplingExponent = 0.80f;
    public float StuckDirectionalCouplingExponent = 0.10f;
    public float BaseDrive = 0.020f;
    public float MaxMoveSpeed = 0.115f;
    // 原作的 unconditionalSupport 会在静止时刷新，并在恢复移动后用约 40 tick 衰减。
    // 默认路径不再依赖这个可由点按反复续杯的推进托底；字段只保留给显式对照实验。
    public int MoveStartAssistTicks = 40;
    public float MoveStartDriveFloor = 0.36f;
    // 松键只短暂保留触手规划方向；身体推进、普通换步和 stuck 恢复仍以真实输入为门。
    public int MoveEpisodeGraceTicks = 24;
    public float MoveEpisodeDirectionResetDot = 0.25f;
    public float StartReplantDirectionalSupportThreshold = 0.40f;
    public float StartReplantReleaseDotMaximum = 0.25f;
    public float StartReplantMoveBlend = 0.88f;
    public float MoveTargetArriveRadius = 0.45f;
    public int MinimumArrivedTentaclesForStep = 2;
    // ≙ 原作 Act 的换腿节流：`legsGrabbing > N/2 && moving` 满足时每 tick 重定向一条
    // ReleaseScore 最差的到达腿——没有串行槽。3D 版同样以“到达数不跌破配额”作为唯一
    // 并发上限（释放即清到达，计数门自节流），另保留逐次释放的短冷却与逐候选支撑余量门。
    // 配额分母是参与池（Locomotion 任务且落点可得），永久够不到地形的触手
    // （SearchFailureTicks 达到扩张上限仍无落点）不占配额，避免高站姿下计数门被
    // 不可能满足的名额卡死。
    public int StepReleaseCooldownTicks = 3;
    // 普通连续换腿不得让预测抗重力跌破 1g；低余量形态会保留抓稳腿，并让已经失落的
    // 触手重新搜索，而不是为了凑步数强行 Peeling。Godot seed 93 已钉住这个区别。
    public float StepReleaseMinimumGravityCancellation = 1.00f;
    // 起步腿只允许一次有界的短暂支撑赤字；它仍按“整条腿立即失去贡献”的最坏值
    // 估算，且不进入 DriveScale。普通连续换步继续使用上面的 1.00 门。
    public float StartReplantMinimumGravityCancellation = 0.90f;
    // 1.00 硬门在稀疏形态（如 5 触手、部分永久够不到地）下会永久拒绝一切换步——原作
    // 没有余量门，靠计数门保护。折衷：连续 StepReserveStarvationTicks 个“计数门已过、
    // 候选仅因余量被拒”的合格 tick 后，允许以起步腿同款 0.90 预算串行放行一条
    // （在途普通步为零时才放）。每次阀释放后重新累计完整饥饿窗：只有 1.00 门恒拒绝
    // 的真死锁形态才持续用阀，尚有普通换步能力的低余量形态（如 Godot seed 93）不会
    // 被阀在普通步之间频繁抽腿压低身高。短暂的余量不足仍保留抓稳腿。
    public int StepReserveStarvationTicks = 90;

    // —— 卡住退化与确定性抖动 ——
    public int StuckHistoryTicks = 80;
    public int StuckCompareTicks = 40;
    public float StuckDistance = 0.32f;
    public int StuckRiseTicks = 100;
    public int StuckFallPerTick = 2;
    public float StuckDetourMoveWeight = 0.75f;
    public int StuckDetourMinimumTicks = 80;
    public int StuckDetourMaximumTicks = 400;
    public int StuckClearanceProbeIntervalTicks = 4;
    public int StuckClearanceRequiredMisses = 3;
    public float StuckClearanceMinimumLength = 1.50f;
    public float StuckClearanceEnvelopeMargin = 0.50f;
    public float StuckBodyJitter = 0.010f;
    public float StuckJitterSpeedCapMultiplier = 1.50f;

    // —— 外部够取/拉扯纯值接缝 ——
    public float ExternalReachRadius = 0.20f;
    public float ExternalPullGain = 0.12f;
    public float ExternalPullVelocityCap = 0.10f;
    public float ExternalPositionCorrectionGain = 0.35f;

    // 预算单位按最坏 Jolt primitive 计：Raycast=1；SpherePenetration=2（零法线时适配器
    // 内部可能追加一根 ray）。计数适配器到达上限后保守返回 miss。
    public int MaximumTerrainQueriesPerTick = 3800;

    // 专项 smoke 的失效注入开关；只属于本物种，不改变共享层。
    public bool EnableSegmentAdhesion = true;
    public bool EnableResidualTerrainResolve = true;
    public bool EnableTerrainBacktrack = true;
    public bool EnableSupport = true;
    public bool EnableSupportOvercompensation = true;
    public bool EnableDutyAllocation = true;
    public bool EnableIndependentLocomotionDuty = true;
    public bool EnableDirectionalDrive = true;
    public bool EnableStepRelease = true;
    public bool EnableStuckRecovery = true;
    public bool EnableSearchExpansion = true;
    public bool EnableStuckBodyJitter = true;
    public bool EnableStunLimp = true;
    public bool EnableExternalPull = true;
    public bool EnableGripDiscrimination = true;
    public bool EnableStepPeel = true;
    public bool EnableSlackGuide = true;
    public bool EnableSurfaceSpanReplant = true;
    public bool EnableMoveStartAssist = false;
    public bool EnableStartReplant = true;
    public bool EnableStepSupportReserve = true;
    public bool EnableStartReplantTransientSupportBudget = true;
    // 计数门节流（≙ 原作 legsGrabbing > N/2）。关闭后普通换步不再受到达数约束，
    // 多腿可同时进入 Peeling——专项消融用，正常运行必须开启。
    public bool EnableStepReleaseThrottle = true;
    // 移动 episode 内未到达腿的快速重瞄（≙ 原作每 tick UpdateClimbGrabPos）。
    public bool EnableMovingReplantRetarget = true;
    // 余量饥饿阀；关闭后稀疏形态回到 1.00 硬门永久拒绝换步的死锁。
    public bool EnableStepReserveStarvationValve = true;
    // 独立消融门：移动 episode 结束后提交已有落点、释放旧起步方向，并对复验做滞回。
    public bool EnableIdleLandingStability = true;
    public bool EnableIdleSupportNeutrality = true;

    public DaddyLongLegsParams Clone() => (DaddyLongLegsParams)MemberwiseClone();

    public DaddyLongLegsParams Validate()
    {
        if (string.IsNullOrWhiteSpace(StableId))
        {
            throw new ArgumentException($"{nameof(StableId)} must not be empty.", nameof(StableId));
        }
        IntegerRange(MinimumBodyChunks, 4, 16, nameof(MinimumBodyChunks));
        IntegerRange(MaximumBodyChunks, MinimumBodyChunks, 16, nameof(MaximumBodyChunks));
        Positive(BodyBudgetUnits, nameof(BodyBudgetUnits));
        Positive(BodyRadiusBase, nameof(BodyRadiusBase));
        Positive(BodyRadiusPerBudgetUnit, nameof(BodyRadiusPerBudgetUnit));
        Positive(BodyMassPerBudgetUnit, nameof(BodyMassPerBudgetUnit));
        IntegerRange(RestLayoutIterations, 1, 24, nameof(RestLayoutIterations));
        UnitPositive(RestOverlapRatio, nameof(RestOverlapRatio));
        UnitPositive(RestContraction, nameof(RestContraction));

        IntegerRange(MinimumTentacles, 3, 16, nameof(MinimumTentacles));
        IntegerRange(MaximumTentacles, MinimumTentacles, 16, nameof(MaximumTentacles));
        Positive(TentacleBudgetBase, nameof(TentacleBudgetBase));
        Positive(TentacleBudgetPerTentacle, nameof(TentacleBudgetPerTentacle));
        Unit(TentacleBudgetBlend, nameof(TentacleBudgetBlend));
        IntegerRange(LengthRedistributionPassesPerTentacle, 0, 32,
            nameof(LengthRedistributionPassesPerTentacle));
        Unit(MaximumRedistributionFraction, nameof(MaximumRedistributionFraction));
        Positive(MinimumTentacleLength, nameof(MinimumTentacleLength));
        Positive(NominalSegmentLength, nameof(NominalSegmentLength));
        IntegerRange(MinimumSegmentsPerTentacle, 2, 32, nameof(MinimumSegmentsPerTentacle));
        IntegerRange(MaximumSegmentsPerTentacle, MinimumSegmentsPerTentacle, 32,
            nameof(MaximumSegmentsPerTentacle));
        IntegerRange(MaximumTotalTentacleSegments,
            MaximumTentacles * MinimumSegmentsPerTentacle, 384,
            nameof(MaximumTotalTentacleSegments));
        Positive(SegmentRadius, nameof(SegmentRadius));
        if (SegmentRadius > BodyRadiusBase)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentRadius), SegmentRadius,
                $"{nameof(SegmentRadius)} must not exceed the smallest possible body radius " +
                $"({nameof(BodyRadiusBase)}={BodyRadiusBase}).");
        }
        Positive(SegmentMass, nameof(SegmentMass));
        UnitPositive(SpawnExtension, nameof(SpawnExtension));

        IntegerRange(BodyConstraintIterations, 1, 20, nameof(BodyConstraintIterations));
        UnitPositive(UnsupportedAirFriction, nameof(UnsupportedAirFriction));
        UnitPositive(SupportedAirFriction, nameof(SupportedAirFriction));
        UnitPositive(UnsupportedSurfaceFriction, nameof(UnsupportedSurfaceFriction));
        UnitPositive(SupportedSurfaceFriction, nameof(SupportedSurfaceFriction));
        NonNegative(TerrainSkin, nameof(TerrainSkin));
        UnitPositive(SegmentDamping, nameof(SegmentDamping));
        Positive(SegmentVelocityCap, nameof(SegmentVelocityCap));
        UnitPositive(SegmentCollisionFriction, nameof(SegmentCollisionFriction));
        UnitPositive(SegmentSurfaceFriction, nameof(SegmentSurfaceFriction));
        NonNegative(SegmentPathServo, nameof(SegmentPathServo));
        NonNegative(SegmentTargetServo, nameof(SegmentTargetServo));
        NonNegative(SegmentRootSpreadForce, nameof(SegmentRootSpreadForce));
        Positive(SegmentSelfSeparation, nameof(SegmentSelfSeparation));
        float maximumCountBudget = TentacleBudgetBase
            + (MaximumTentacles * TentacleBudgetPerTentacle - TentacleBudgetBase)
                * TentacleBudgetBlend;
        float minimumInitialTentacleLength = maximumCountBudget / MaximumTentacles;
        if (minimumInitialTentacleLength < MinimumTentacleLength)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumTentacleLength),
                MinimumTentacleLength,
                $"The maximum-count initial allocation ({minimumInitialTentacleLength}) " +
                $"must be at least {nameof(MinimumTentacleLength)}.");
        }
        float minimumGeneratedLink = Math.Min(
            NominalSegmentLength,
            MinimumTentacleLength / MinimumSegmentsPerTentacle);
        float minimumSegmentSpacing = Math.Max(
            SegmentSelfSeparation,
            SegmentRadius * 2f);
        if (minimumGeneratedLink <= minimumSegmentSpacing + 1e-5f)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentSelfSeparation),
                SegmentSelfSeparation,
                $"The shortest generated link ({minimumGeneratedLink}) must exceed " +
                $"the segment non-overlap spacing ({minimumSegmentSpacing}).");
        }
        NonNegative(LimpGravityScale, nameof(LimpGravityScale));
        IntegerRange(StepPeelTicks, 1, 60, nameof(StepPeelTicks));
        IntegerRange(StepPeelMaximumTicks, StepPeelTicks, 120,
            nameof(StepPeelMaximumTicks));
        Positive(StepPeelVelocity, nameof(StepPeelVelocity));
        Range(StepPeelStartFraction, 0.05f, 0.95f, nameof(StepPeelStartFraction));
        Unit(StepPeelMaximumContactFraction, nameof(StepPeelMaximumContactFraction));
        NonNegative(StepPeelRetractionServo, nameof(StepPeelRetractionServo));
        IntegerRange(StepArrivalMinimumTicks, 0, 30, nameof(StepArrivalMinimumTicks));
        IntegerRange(TentacleConstraintIterations, 1, 12, nameof(TentacleConstraintIterations));
        IntegerRange(ResidualTerrainResolveIterations, 1, 8,
            nameof(ResidualTerrainResolveIterations));
        IntegerRange(TerrainBacktrackReleaseTicks, 2, 30,
            nameof(TerrainBacktrackReleaseTicks));
        Positive(TerrainBacktrackRetractionSpeed,
            nameof(TerrainBacktrackRetractionSpeed));
        IntegerRange(TerrainBacktrackCandidatePhases, 1, 64,
            nameof(TerrainBacktrackCandidatePhases));

        UnitPositive(IdealReachRatio, nameof(IdealReachRatio));
        UnitPositive(SearchReachMinimumRatio, nameof(SearchReachMinimumRatio));
        Range(SearchReachMaximumRatio, SearchReachMinimumRatio, 2f,
            nameof(SearchReachMaximumRatio));
        Unit(PreferenceMoveBlend, nameof(PreferenceMoveBlend));
        Unit(StuckMoveBlend, nameof(StuckMoveBlend));
        Unit(SearchJitterMaximum, nameof(SearchJitterMaximum));
        IntegerRange(SearchFailureExpandTicks, 1, 2000, nameof(SearchFailureExpandTicks));
        IntegerRange(SearchRefreshTicks, 1, 120, nameof(SearchRefreshTicks));
        IntegerRange(SearchRayCount, 1, 32, nameof(SearchRayCount));
        Range(SearchConeMinimumDegrees, 0f, 180f, nameof(SearchConeMinimumDegrees));
        Range(SearchConeMaximumDegrees, SearchConeMinimumDegrees, 180f,
            nameof(SearchConeMaximumDegrees));
        NonNegative(LandingSurfaceOffset, nameof(LandingSurfaceOffset));
        Positive(GrabArrivalRadius, nameof(GrabArrivalRadius));
        NonNegative(ContactProbeExtra, nameof(ContactProbeExtra));
        Positive(ContactTargetRange, nameof(ContactTargetRange));
        Unit(GuideContactSlackShare, nameof(GuideContactSlackShare));
        Range(GuideMaximumContactRatio, 0.01f, 0.45f,
            nameof(GuideMaximumContactRatio));
        Range(GuideTargetLengthRatio, 0.75f, 1f, nameof(GuideTargetLengthRatio));
        Unit(GuideSlackArchShare, nameof(GuideSlackArchShare));
        Range(GuideBendMaximumRatio, 0.1f, 2f, nameof(GuideBendMaximumRatio));
        Range(GuideObstructionArchDecayPerTick, 0.05f, 1f,
            nameof(GuideObstructionArchDecayPerTick));
        Range(GuideArchRecoveryPerTick, 0.001f, 0.5f,
            nameof(GuideArchRecoveryPerTick));
        if (GuideObstructionArchDecayPerTick * (TerrainBacktrackReleaseTicks - 1) < 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GuideObstructionArchDecayPerTick),
                GuideObstructionArchDecayPerTick,
                "Arch decay must fully flatten the slack arch before the guide " +
                "obstruction release window elapses.");
        }
        Range(GuideTangentHandleRatio, 0.01f, 1f, nameof(GuideTangentHandleRatio));
        Range(GuideSurfaceApproachNormalWeight, 0f, 2f,
            nameof(GuideSurfaceApproachNormalWeight));
        Range(SurfaceAdvanceNormalWeight, 0.01f, 2f,
            nameof(SurfaceAdvanceNormalWeight));
        Range(SurfaceSpanReachRatio, 0.50f, 1.20f, nameof(SurfaceSpanReachRatio));
        Range(SurfaceSpanAdvanceMaximumRatio, 0.01f, 0.50f,
            nameof(SurfaceSpanAdvanceMaximumRatio));
        IntegerRange(LandingCommitTicks, 1, 120, nameof(LandingCommitTicks));
        IntegerRange(MovingReplantRetargetTicks, 1, LandingCommitTicks,
            nameof(MovingReplantRetargetTicks));
        IntegerRange(LandingValidationTicks, 1, 120, nameof(LandingValidationTicks));
        IntegerRange(IdleLandingValidationFailures, 1, 10,
            nameof(IdleLandingValidationFailures));
        IntegerRange(IdleProximityProbeTicks, 1, 120, nameof(IdleProximityProbeTicks));
        Positive(IdleProximityProbeLength, nameof(IdleProximityProbeLength));

        Range(GripFractionExponent, 0.01f, 4f, nameof(GripFractionExponent));
        Unit(ArrivedSupportWeight, nameof(ArrivedSupportWeight));
        Range(SupportResponseExponent, 0.01f, 4f, nameof(SupportResponseExponent));
        UnitPositive(SupportBlend, nameof(SupportBlend));
        Range(GravityCancellationGain, 0f, 2f, nameof(GravityCancellationGain));
        Unit(IdleGravityCancellationMaximum, nameof(IdleGravityCancellationMaximum));
        UnitPositive(IdleBodyVelocityRetention, nameof(IdleBodyVelocityRetention));
        Range(UnconditionalSupportDecayPerTick, 0.001f, 1f,
            nameof(UnconditionalSupportDecayPerTick));
        IntegerRange(MinimumLocomotionTentacles, 1, MinimumTentacles,
            nameof(MinimumLocomotionTentacles));
        IntegerRange(ReservedIdleTentacles, 1, MinimumTentacles - 1,
            nameof(ReservedIdleTentacles));
        if (MinimumLocomotionTentacles + ReservedIdleTentacles > MinimumTentacles)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumLocomotionTentacles),
                "Minimum locomotion duty plus reserved idle tentacles exceeds the smallest morphology.");
        }
        Unit(ReleaseSupportThreshold, nameof(ReleaseSupportThreshold));
        IntegerRange(DutyChangeCooldownTicks, 0, 120, nameof(DutyChangeCooldownTicks));
        Range(DirectionalSupportDotFloor, -1f, 1f, nameof(DirectionalSupportDotFloor));
        Range(StuckDirectionalSupportDotFloor, -1f, DirectionalSupportDotFloor,
            nameof(StuckDirectionalSupportDotFloor));
        Range(DirectionalSupportDotCeiling, DirectionalSupportDotFloor + 0.001f, 1f,
            nameof(DirectionalSupportDotCeiling));
        Range(DirectionalSupportExponent, 0.01f, 4f, nameof(DirectionalSupportExponent));
        Range(DirectionalCouplingExponent, 0.01f, 4f, nameof(DirectionalCouplingExponent));
        Range(StuckDirectionalCouplingExponent, 0.01f, DirectionalCouplingExponent,
            nameof(StuckDirectionalCouplingExponent));
        NonNegative(BaseDrive, nameof(BaseDrive));
        Positive(MaxMoveSpeed, nameof(MaxMoveSpeed));
        IntegerRange(MoveStartAssistTicks, 1, 120, nameof(MoveStartAssistTicks));
        Unit(MoveStartDriveFloor, nameof(MoveStartDriveFloor));
        IntegerRange(MoveEpisodeGraceTicks, 0, 120, nameof(MoveEpisodeGraceTicks));
        Range(MoveEpisodeDirectionResetDot, -1f, 1f,
            nameof(MoveEpisodeDirectionResetDot));
        Unit(StartReplantDirectionalSupportThreshold,
            nameof(StartReplantDirectionalSupportThreshold));
        Range(StartReplantReleaseDotMaximum, -1f, 1f,
            nameof(StartReplantReleaseDotMaximum));
        Unit(StartReplantMoveBlend, nameof(StartReplantMoveBlend));
        Positive(MoveTargetArriveRadius, nameof(MoveTargetArriveRadius));
        IntegerRange(MinimumArrivedTentaclesForStep, 1, MinimumTentacles,
            nameof(MinimumArrivedTentaclesForStep));
        IntegerRange(StepReleaseCooldownTicks, 1, 120, nameof(StepReleaseCooldownTicks));
        Range(StepReleaseMinimumGravityCancellation, 0f, 2f,
            nameof(StepReleaseMinimumGravityCancellation));
        Range(StartReplantMinimumGravityCancellation, 0f, 2f,
            nameof(StartReplantMinimumGravityCancellation));
        IntegerRange(StepReserveStarvationTicks, 1, 2000,
            nameof(StepReserveStarvationTicks));

        IntegerRange(StuckHistoryTicks, 8, 512, nameof(StuckHistoryTicks));
        IntegerRange(StuckCompareTicks, 1, StuckHistoryTicks - 1, nameof(StuckCompareTicks));
        Positive(StuckDistance, nameof(StuckDistance));
        IntegerRange(StuckRiseTicks, 1, 1000, nameof(StuckRiseTicks));
        IntegerRange(StuckFallPerTick, 1, 100, nameof(StuckFallPerTick));
        Range(StuckDetourMoveWeight, 0.05f, 2f, nameof(StuckDetourMoveWeight));
        IntegerRange(StuckDetourMinimumTicks, 1, 2000, nameof(StuckDetourMinimumTicks));
        IntegerRange(StuckDetourMaximumTicks, StuckDetourMinimumTicks, 4000,
            nameof(StuckDetourMaximumTicks));
        IntegerRange(StuckClearanceProbeIntervalTicks, 1, 120,
            nameof(StuckClearanceProbeIntervalTicks));
        IntegerRange(StuckClearanceRequiredMisses, 1, 30,
            nameof(StuckClearanceRequiredMisses));
        Positive(StuckClearanceMinimumLength, nameof(StuckClearanceMinimumLength));
        NonNegative(StuckClearanceEnvelopeMargin, nameof(StuckClearanceEnvelopeMargin));
        NonNegative(StuckBodyJitter, nameof(StuckBodyJitter));
        Range(StuckJitterSpeedCapMultiplier, 1f, 4f,
            nameof(StuckJitterSpeedCapMultiplier));

        NonNegative(ExternalReachRadius, nameof(ExternalReachRadius));
        NonNegative(ExternalPullGain, nameof(ExternalPullGain));
        NonNegative(ExternalPullVelocityCap, nameof(ExternalPullVelocityCap));
        NonNegative(ExternalPositionCorrectionGain, nameof(ExternalPositionCorrectionGain));

        int maximumConnections = MaximumBodyChunks * (MaximumBodyChunks - 1) / 2;
        int conservativeQueries = MaximumBodyChunks * (5 + 2 * ResidualTerrainResolveIterations)
            + maximumConnections * 14
            // 每段既有 sweep+adhesion+MTD+residual（含 LastPos 复验），再加最终
            // temporal sweep、相邻链边审计，以及恢复 tick 的一个原子候选
            // （SpherePenetration 最坏 2 + link ray 1）。所有触手可同时恢复，故按 S 全计。
            + MaximumTotalTentacleSegments * (11 + 2 * ResidualTerrainResolveIterations)
            // 同 tick 可依次发生初始展开、落点复验、R 条搜索和 idle proximity probe。
            // recovering tentacle 还需复验一次 predecessor 球壳（最坏 2 primitive）。
            + MaximumTentacles * (SearchRayCount + 5)
            + 1; // 卡住 detour 的身体内侧体缘 clearance probe。
        IntegerRange(MaximumTerrainQueriesPerTick, conservativeQueries, 4096,
            nameof(MaximumTerrainQueriesPerTick));
        return this;
    }

    internal DaddyLongLegsParams Snapshot() => Clone().Validate();

    private static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be finite and positive.");
    }

    private static void NonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be finite and non-negative.");
    }

    private static void Unit(float value, string name) => Range(value, 0f, 1f, name);

    private static void UnitPositive(float value, string name) => Range(value, float.Epsilon, 1f, name);

    private static void Range(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be finite and in [{minimum}, {maximum}].");
    }

    private static void IntegerRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be in [{minimum}, {maximum}].");
    }
}
