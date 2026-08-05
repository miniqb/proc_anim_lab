using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>Daddy 触手的一节独立粒子；它不是 BodyChunk，也不进入共享 Body 求解器。</summary>
public sealed class DaddyTentacleSegmentState
{
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;
    public readonly float Mass;
    public bool TerrainContact;
    public Vector3 ContactNormal;
    public bool ActiveGrip;
    public Vector3 GripNormal;
    public ulong GripColliderId;

    internal bool PreviousActiveGrip;
    internal Vector3 PreviousGripNormal;
    internal Vector3 PreviousContactNormal;

    internal DaddyTentacleSegmentState(Vector3 pos, float radius, float mass)
    {
        Pos = pos;
        LastPos = pos;
        Radius = radius;
        Mass = mass;
    }

    public Vector3 LerpPos(float interpolation) => LastPos.Lerp(Pos, interpolation);
}

public enum DaddyTentacleReplantPhase : byte
{
    Planted,
    Peeling,
    Reaching,
}

/// <summary>
/// DaddyLongLegs 专属的多段贴面触手。整条链逐段做 sweep/probe/MTD，支撑取真实贴面段比例；
/// 不继承或引用任何既有物种的 Limb、Leg、Wing 或 TentacleChain。
/// </summary>
public sealed class DaddyTentacle
{
    public int Index { get; }
    public BodyChunk Anchor { get; }
    public float Length { get; }
    public float LinkLength { get; }
    public Vector3 LocalPreference { get; }
    public IReadOnlyList<DaddyTentacleSegmentState> Segments => _segments;
    public DaddyTentacleTask Task { get; private set; } = DaddyTentacleTask.Locomotion;
    public bool NeededForLocomotion { get; private set; }
    public DaddyTentacleRole Role => StunTicks > 0
        ? DaddyTentacleRole.Stunned
        : Task == DaddyTentacleTask.ExternalReach
            ? DaddyTentacleRole.ExternalReach
            : NeededForLocomotion
                ? DaddyTentacleRole.Locomotion
                : DaddyTentacleRole.Idle;
    public bool HasLandingTarget { get; private set; }
    public Vector3 LandingPoint { get; private set; }
    public Vector3 LandingNormal { get; private set; }
    public ulong LandingColliderId { get; private set; }
    public Vector3 IdealLandingPoint { get; private set; }
    public bool AtGrabDestination { get; private set; }
    public float GripFraction { get; private set; }
    public float SupportContribution { get; private set; }
    public Vector3 SupportNormal { get; private set; } = Vector3.Up;
    public float ReleaseScore { get; private set; }
    public int SearchFailureTicks { get; private set; }
    public int StunTicks { get; private set; }
    public int StepSerial { get; private set; }
    public int LandingSerial { get; private set; }
    public int SearchSerial { get; private set; }
    public int ReachInvalidationSerial { get; private set; }
    public int ValidationInvalidationSerial { get; private set; }
    public int ResidualRecoverySerial { get; private set; }
    public int ResidualInvalidationSerial { get; private set; }
    public float TerrainDistanceHint { get; private set; } = float.PositiveInfinity;
    public DaddyLongLegsTargetSnapshot? ExternalTarget => _externalTarget;
    public bool CanAcceptExternalTarget => Task == DaddyTentacleTask.Locomotion
        && !NeededForLocomotion
        && !StartReplantActive
        && StunTicks <= 0
        && BacktrackFrom < 0
        && !TerrainRecoveryActive
        && _pendingReleaseTargetId is null
        && _externalTarget is null;
    public DaddyLongLegsTargetEffect TargetEffect { get; private set; }
    public float MaximumConstraintError { get; private set; }
    public float MinimumSelfSeparation { get; private set; } = float.PositiveInfinity;
    public DaddyTentacleReplantPhase ReplantPhase { get; private set; } =
        DaddyTentacleReplantPhase.Reaching;
    public int ReplantPhaseTicks { get; private set; }
    /// <summary>
    /// 一次显式换步（BeginStep/BeginStartReplant）已释放、尚未重新 Planted+到达。
    /// 计数门以“到达数不跌破配额”自节流，多条腿可同时在途；该标记只用于
    /// 起步独占、stuck 强制换步的单腿约束与诊断，不是全局串行槽。
    /// </summary>
    public bool StepInFlight { get; private set; }
    public bool StartReplantActive { get; private set; }
    public Vector3 StartReplantDirection { get; private set; }
    public int ActiveGripCount { get; private set; }
    public float GuideLength { get; private set; }
    public float GuideContactLength { get; private set; }
    /// <summary>首个被地形隔断的 Anchor/segment 链边；-1 表示整链当前连通。</summary>
    public int BacktrackFrom { get; private set; } = -1;
    /// <summary>最终约束或自避位移被 post-solve sweep 拦下的累计次数。</summary>
    public int PostConstraintSweepSerial { get; private set; }
    /// <summary>连续隔断达到释放门、执行 distal suffix 恢复的累计次数。</summary>
    public int TerrainBacktrackSerial { get; private set; }
    /// <summary>整条触手曾连续存在任一真实阻断链边的最长 tick 数。</summary>
    public int MaximumBarrierTicks { get; private set; }
    public bool TerrainRecoveryActive => _terrainRecoveryFrom >= 0;
    public int TerrainRecoveryPhase => _terrainRecoveryPhase;
    public int MaximumGuideObstructionTicks { get; private set; }
    public int GuideObstructionReleaseSerial { get; private set; }
    /// <summary>当前自适应鼓弧幅度比例；obstruction 衰减、无障碍缓慢恢复。</summary>
    public float GuideArchScale => _guideArchScale;

    private readonly DaddyTentacleSegmentState[] _segments;
    private readonly Vector3[] _releaseOrigins;
    private readonly Vector3[] _releaseNormals;
    private readonly Vector3[] _guideTargets;
    private readonly bool[] _guideTargetValid;
    private readonly bool[] _barrierObserved;
    private readonly int[] _barrierTicks;
    private readonly Vector3[] _barrierPoints;
    private readonly Vector3[] _barrierNormals;
    private readonly Vector3[] _terrainRecoveryCandidates;
    private readonly Vector3[] _contactNormalA;
    private readonly Vector3[] _contactNormalB;
    private readonly Vector3[] _contactNormalC;
    private readonly byte[] _contactNormalCounts;
    private readonly Vector3[] _guideSamples = new Vector3[GuideSampleCount + 1];
    private readonly float[] _guideCumulative = new float[GuideSampleCount + 1];
    private readonly DaddyLongLegsParams _parameters;
    private readonly ulong _stableSeed;
    private DaddyLongLegsTargetSnapshot? _externalTarget;
    private ulong? _pendingReleaseTargetId;
    private int _landingValidationAge;
    private int _idleLandingValidationFailures;
    private int _idleProbeSerial;
    private int _landingAge;
    private int _releaseStart;
    private int _peelReleasedFrom;
    private Vector3 _peelNormal;
    private Vector3 _replantSurfacePoint;
    private Vector3 _replantSurfaceNormal;
    private bool _hasReplantSurface;
    private bool _forceLandingSearch;
    private bool _idleLandingRetargetCommitted;
    private bool _needsTerrainExpansion = true;
    private bool _guideObstructionObserved;
    private int _guideObstructionTicks;
    // 逐触手自适应鼓弧幅度：obstruction 当 tick 快速衰减、无障碍时缓慢恢复。
    // 拱弧鼓进墙体造成的 obstruction 因此在 6 tick 释放窗之前自愈，不再把
    // 有效落点每 6 tick 清点重搜；真实阻塞仍走原释放路径。
    private float _guideArchScale = 1f;
    private int _topologyBarrierTicks;
    private int _terrainRecoveryFrom = -1;
    private int _terrainRecoveryPhase;
    private Vector3 _terrainRecoveryPoint;
    private Vector3 _terrainRecoveryNormal;
    private Vector3 _terrainRecoveryTangentReference;
    private Vector3 _lastWorldPreference;
    private Vector3 _lastFrameAxisA;
    private Vector3 _lastFrameAxisB;
    private float _guideBridgeLength;
    private Vector3 _guideSurfaceStart;
    private Vector3 _guideSurfaceTangent;
    private const int GuideSampleCount = 24;
    private bool MaintainsTerrainTask => StunTicks <= 0
        && Task == DaddyTentacleTask.Locomotion
        && (_parameters.EnableIndependentLocomotionDuty || NeededForLocomotion);

    internal DaddyTentacle(
        int index,
        BodyChunk anchor,
        DaddyTentacleSpec spec,
        DaddyLongLegsParams parameters,
        ulong stableSeed)
    {
        Index = index;
        Anchor = anchor;
        Length = spec.Length;
        LocalPreference = spec.LocalPreference;
        LinkLength = spec.Length / spec.SegmentCount;
        _parameters = parameters;
        _stableSeed = stableSeed;
        _lastWorldPreference = NormalizeOr(spec.LocalPreference, Vector3.Right);
        _lastFrameAxisA = Orthogonal(_lastWorldPreference);
        _lastFrameAxisB = NormalizeOr(
            _lastWorldPreference.Cross(_lastFrameAxisA),
            Orthogonal(_lastWorldPreference));
        _segments = new DaddyTentacleSegmentState[spec.SegmentCount];
        _releaseOrigins = new Vector3[spec.SegmentCount];
        _releaseNormals = new Vector3[spec.SegmentCount];
        _guideTargets = new Vector3[spec.SegmentCount];
        _guideTargetValid = new bool[spec.SegmentCount];
        _barrierObserved = new bool[spec.SegmentCount];
        _barrierTicks = new int[spec.SegmentCount];
        _barrierPoints = new Vector3[spec.SegmentCount];
        _barrierNormals = new Vector3[spec.SegmentCount];
        _terrainRecoveryCandidates = new Vector3[spec.SegmentCount];
        _contactNormalA = new Vector3[spec.SegmentCount];
        _contactNormalB = new Vector3[spec.SegmentCount];
        _contactNormalC = new Vector3[spec.SegmentCount];
        _contactNormalCounts = new byte[spec.SegmentCount];
        Vector3 initialDirection = NormalizeOr(spec.LocalPreference, Vector3.Right);
        for (int i = 0; i < _segments.Length; i++)
        {
            float distance = LinkLength * parameters.SpawnExtension * (i + 1);
            _segments[i] = new DaddyTentacleSegmentState(
                anchor.Pos + initialDirection * distance,
                parameters.SegmentRadius,
                parameters.SegmentMass);
        }
    }

    internal void Tick(
        in TickContext ctx,
        Vector3 bodyCenter,
        Vector3 worldPreference,
        Vector3 moveDirection,
        float stuckAmount,
        bool movementEpisodeActive,
        Vector3 frameAxisA,
        Vector3 frameAxisB)
    {
        TargetEffect = default;
        _lastWorldPreference = NormalizeOr(worldPreference, _lastWorldPreference);
        _lastFrameAxisA = NormalizeOr(frameAxisA, _lastFrameAxisA);
        _lastFrameAxisB = NormalizeOr(frameAxisB, _lastFrameAxisB);
        if (_pendingReleaseTargetId is { } released)
        {
            TargetEffect = new DaddyLongLegsTargetEffect(
                Index, released, false, false, true, Vector3.Zero, Vector3.Zero);
            _pendingReleaseTargetId = null;
        }

        bool stunnedThisTick = StunTicks > 0;
        bool maintainsTerrainTask = MaintainsTerrainTask;
        if (movementEpisodeActive)
            _idleLandingRetargetCommitted = false;
        if (_needsTerrainExpansion && !stunnedThisTick
            && Task == DaddyTentacleTask.Locomotion)
        {
            ExpandInitialChain(ctx, worldPreference);
        }
        if (_parameters.EnableIdleLandingStability
            && !movementEpisodeActive
            && HasLandingTarget)
        {
            _idleLandingRetargetCommitted = true;
        }
        if (!stunnedThisTick && maintainsTerrainTask)
        {
            // 一次显式换步的搜索在完整扩张窗内持续失败：该步宣告失败，腿退化为普通
            // 搜索腿。不能让它永久占用在途计数——stuck 豁免与饥饿阀都要求零在途，
            // 参与池的在途项也会用一条永远无法到达的腿抬高到达配额；起步腿同理经
            // 控制器的 !StepInFlight 中断判定重臂 pending。
            if (StepInFlight && !HasLandingTarget
                && SearchFailureTicks >= _parameters.SearchFailureExpandTicks)
            {
                StepInFlight = false;
            }
            UpdateIdealLanding(worldPreference, moveDirection, stuckAmount);
            bool hadLanding = HasLandingTarget;
            ValidateLanding(ctx, movementEpisodeActive);
            if (hadLanding && !HasLandingTarget)
            {
                ReplantPhase = DaddyTentacleReplantPhase.Reaching;
                ReplantPhaseTicks = 0;
                _forceLandingSearch = true;
            }
            bool maySearch = ReplantPhase != DaddyTentacleReplantPhase.Peeling
                && BacktrackFrom < 0;
            // ≙ 原作 Climb：!atGrabDest 的触手每 tick 重新提交 preliminaryGrabDest，
            // 在途落点持续跟随身体的 idealGrabPos。移动 episode 内因此用搜索节拍级的
            // 短提交年龄；停驶与 idle 保持原有 20 tick 提交避免落点churn。
            int landingCommitTicks = movementEpisodeActive
                && _parameters.EnableMovingReplantRetarget
                    ? _parameters.MovingReplantRetargetTicks
                    : _parameters.LandingCommitTicks;
            bool landingMayRetarget = HasLandingTarget
                && !AtGrabDestination
                // episode 内保留原有步态恢复；停驶后则把已验证点当 incumbent，
                // AtGrabDestination 的单 tick adhesion/guide 抖动不能再触发隐式换点。
                && (!_parameters.EnableIdleLandingStability
                    // 有真实 movement episode 时完整保留原步态恢复；稳定门只接管
                    // episode 真正结束后的有效 incumbent。
                    || movementEpisodeActive
                    || (ReplantPhase == DaddyTentacleReplantPhase.Reaching
                        && !_idleLandingRetargetCommitted))
                && _landingAge >= landingCommitTicks;
            if (maySearch && (!HasLandingTarget || landingMayRetarget)
                && (_forceLandingSearch
                    || (ctx.TickIndex + Index) % _parameters.SearchRefreshTicks == 0))
            {
                SearchForLanding(ctx, worldPreference, moveDirection, stuckAmount,
                    movementEpisodeActive, frameAxisA, frameAxisB);
                _forceLandingSearch = false;
            }
            if (!NeededForLocomotion)
                ProbeIdleTerrain(ctx, worldPreference);
        }
        else if (!stunnedThisTick && Task == DaddyTentacleTask.Locomotion)
        {
            // 消融路径：复现旧实现把 free duty 错当成“停止 locomotion task”的行为。
            if (HasLandingTarget)
                ValidateLanding(ctx, movementEpisodeActive);
            ProbeIdleTerrain(ctx, worldPreference);
        }

        Array.Clear(_barrierObserved);
        _guideObstructionObserved = false;
        IntegrateSegments(ctx, worldPreference);
        SolveChainConstraints();
        ResolveTerrain(ctx, worldPreference);
        SolveChainConstraints();
        // 原作在 Tentacle 基类完成链约束/地形更新之后才执行 PushChunksApart。
        // 放在最终阶段才能阻止后续 pull-only 松弛把同链段重新叠到一起；接触段的
        // 修正会投影到表面切平面，避免为了自避又把它推回 collider 内。
        SeparateSegments();
        // 首轮运动 sweep 可能被适配器/接缝漏报，之后的段长松弛与自避也可能把段中心
        // 送到薄墙另一侧；从 tick-start LastPos 补一次最终 sweep，不能只看 tick 末 MTD。
        ResolvePostConstraintTerrain(ctx);
        // 自避可能移动未贴面的段；最后仍以地形可行为硬约束。接触段的自避位移
        // 已严格限制在切平面内，因此这一步通常只处理自由段，不会重新压扁贴面段。
        ResolveResidualTerrain(ctx);
        // 逐条真实短链边检查 tick-end 拓扑连通；绝不拿 Anchor→Landing
        // 的直线可见性代替，因为长触手合法绕过棱角时二者本来就可能
        // 互相不可见。必须放在残余球体解算之后：该解算可能回滚单段，若先
        // 审计，核心会漏掉沙盒最终画出来的跨墙链边。
        AuditTerrainBacktrack(ctx);
        UpdateSupport();
        AdvanceReplantPhase();
        UpdateExternalEffect(bodyCenter);

        if (HasLandingTarget)
            _landingAge++;
        else
            _landingAge = 0;

        if (stunnedThisTick)
            StunTicks--;
    }

    internal void SetLocomotion()
    {
        if (StunTicks > 0 || Task == DaddyTentacleTask.ExternalReach
            || TerrainRecoveryActive)
            return;
        NeededForLocomotion = true;
        TerrainDistanceHint = float.PositiveInfinity;
    }

    internal void SetIdle()
    {
        if (StunTicks > 0 || Task == DaddyTentacleTask.ExternalReach)
            return;
        NeededForLocomotion = false;
    }

    internal void BeginStep() => BeginStepCore(false, Vector3.Zero);

    internal void BeginStartReplant(Vector3 moveDirection)
    {
        BeginStepCore(true, NormalizeOr(moveDirection, LocalPreference));
    }

    /// <summary>
    /// movement episode 已真正结束：提交当前有效落点并保留 Reaching phase/整链支撑；
    /// 起步专用的旧移动方向锁降级成普通落点收尾。验证失效后仍可强制重搜。
    /// </summary>
    internal void CommitIdleLanding()
    {
        _idleLandingRetargetCommitted = true;
        StartReplantActive = false;
        StartReplantDirection = Vector3.Zero;
        StepInFlight = false;
    }

    private void BeginStepCore(bool startReplant, Vector3 startReplantDirection)
    {
        if (StunTicks > 0
            || Task != DaddyTentacleTask.Locomotion
            || TerrainRecoveryActive
            || (!_parameters.EnableIndependentLocomotionDuty && !NeededForLocomotion))
            return;
        _idleLandingRetargetCommitted = false;
        StepInFlight = true;
        StartReplantActive = startReplant;
        StartReplantDirection = startReplant
            ? NormalizeOr(startReplantDirection, LocalPreference)
            : Vector3.Zero;
        _peelNormal = ResolvePeelNormal();
        CaptureReplantSurface();
        _releaseStart = Math.Clamp(
            (int)MathF.Floor(_segments.Length * _parameters.StepPeelStartFraction),
            0,
            _segments.Length - 1);
        _peelReleasedFrom = _segments.Length;
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            _releaseOrigins[i] = segment.Pos;
            _releaseNormals[i] = segment.ActiveGrip
                && segment.GripNormal.LengthSquared() > 1e-10f
                    ? segment.GripNormal.Normalized()
                    : _peelNormal;
        }
        ClearLanding(incrementStep: true);
        ClearSupport();
        ReplantPhase = _parameters.EnableStepPeel
            && _peelNormal.LengthSquared() > 1e-10f
                ? DaddyTentacleReplantPhase.Peeling
                : DaddyTentacleReplantPhase.Reaching;
        ReplantPhaseTicks = 0;
        _forceLandingSearch = ReplantPhase == DaddyTentacleReplantPhase.Reaching;
        if (ReplantPhase == DaddyTentacleReplantPhase.Peeling)
            UpdatePeelReleaseBoundary();
        SearchFailureTicks = Math.Max(SearchFailureTicks, 1);
    }

    private void CaptureReplantSurface()
    {
        _hasReplantSurface = false;
        _replantSurfacePoint = Vector3.Zero;
        _replantSurfaceNormal = Vector3.Zero;
        if (!_parameters.EnableSurfaceSpanReplant
            || !HasLandingTarget
            || LandingNormal.LengthSquared() <= 1e-10f)
        {
            return;
        }
        _replantSurfacePoint = LandingPoint;
        _replantSurfaceNormal = LandingNormal.Normalized();
        _hasReplantSurface = true;
    }

    internal bool TryAssignExternalTarget(in DaddyLongLegsTargetSnapshot target)
    {
        target.Validate();
        // 单条效果只有一个 TargetId；先让上一个目标的 Released 独占一 tick，避免同 tick
        // clear→reassign 后新目标状态覆盖旧目标释放事件。
        if (_pendingReleaseTargetId is not null)
            return false;
        if (StunTicks > 0)
            return false;
        if (Task == DaddyTentacleTask.ExternalReach)
        {
            if (_externalTarget is not { } existing || existing.StableId != target.StableId)
                return false;
            _externalTarget = target;
            return true;
        }
        if (!CanAcceptExternalTarget)
            return false;
        ClearLanding(incrementStep: false);
        ClearSupport();
        ClearTerrainContactMemory();
        ResetReplantState();
        ResetTerrainBacktrackState();
        _externalTarget = target;
        NeededForLocomotion = false;
        Task = DaddyTentacleTask.ExternalReach;
        return true;
    }

    internal void ClearExternalTarget()
    {
        // 取消接口必须幂等：宿主重复取消或误传一个正在运动的触手编号时，不能把它
        // 已聚合出的贴面支撑清成 0，同时还保留 Locomotion/落点状态。
        if (_externalTarget is not { } target)
            return;
        _pendingReleaseTargetId = target.StableId;
        _externalTarget = null;
        if (Task == DaddyTentacleTask.ExternalReach)
            Task = DaddyTentacleTask.Locomotion;
        NeededForLocomotion = false;
        ResetTerrainBacktrackState();
        ClearSupport();
    }

    internal void Stun(int ticks)
    {
        if (ticks <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Stun duration must be positive.");
        ClearExternalTarget();
        ClearLanding(incrementStep: false);
        ClearTerrainContactMemory();
        ResetReplantState();
        ResetTerrainBacktrackState();
        StunTicks = Math.Max(StunTicks, ticks);
        Task = DaddyTentacleTask.Locomotion;
        NeededForLocomotion = false;
        ClearSupport();
    }

    internal void Shift(Vector3 delta)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            segment.Pos += delta;
            segment.LastPos += delta;
            _releaseOrigins[i] += delta;
            if (_guideTargetValid[i])
                _guideTargets[i] += delta;
            if (_barrierTicks[i] > 0)
                _barrierPoints[i] += delta;
        }
        if (_terrainRecoveryFrom >= 0)
            _terrainRecoveryPoint += delta;
        if (_hasReplantSurface)
            _replantSurfacePoint += delta;
        LandingPoint += delta;
        IdealLandingPoint += delta;
        if (_externalTarget is { } target)
            _externalTarget = target.Shifted(delta);
    }

    internal void ResetForTeleport(Vector3 worldPreference)
    {
        ClearExternalTarget();
        ClearLanding(incrementStep: false);
        ResetReplantState();
        Task = DaddyTentacleTask.Locomotion;
        NeededForLocomotion = false;
        StunTicks = 0;
        SearchFailureTicks = 0;
        TerrainDistanceHint = float.PositiveInfinity;
        ResetTerrainBacktrackState();
        Vector3 direction = NormalizeOr(worldPreference, Vector3.Right);
        _lastWorldPreference = direction;
        for (int i = 0; i < _segments.Length; i++)
        {
            _segments[i].Pos = Anchor.Pos
                + direction * (LinkLength * _parameters.SpawnExtension * (i + 1));
            _segments[i].LastPos = _segments[i].Pos;
            _segments[i].Vel = Vector3.Zero;
            _segments[i].TerrainContact = false;
            _segments[i].ContactNormal = Vector3.Zero;
            _segments[i].ActiveGrip = false;
            _segments[i].GripNormal = Vector3.Zero;
            _segments[i].GripColliderId = 0UL;
            _guideTargets[i] = _segments[i].Pos;
            _guideTargetValid[i] = false;
            ResetContactManifold(i);
        }
        _needsTerrainExpansion = true;
        ClearSupport();
    }

    internal void Launch(Vector3 velocityPerTick)
    {
        foreach (DaddyTentacleSegmentState segment in _segments)
        {
            segment.Vel += velocityPerTick;
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
        ClearLanding(incrementStep: false);
        ResetReplantState();
        ResetTerrainBacktrackState();
        _needsTerrainExpansion = false;
        ClearSupport();
        if (Task == DaddyTentacleTask.Locomotion)
            SearchFailureTicks = Math.Max(1, SearchFailureTicks);
    }

    internal void FoldDeterministicState(DeterminismHasher hasher)
    {
        foreach (DaddyTentacleSegmentState segment in _segments)
        {
            hasher.Fold(segment.Pos);
            hasher.Fold(segment.LastPos);
            hasher.Fold(segment.Vel);
            hasher.Fold(segment.TerrainContact);
            hasher.Fold(segment.ContactNormal);
            hasher.Fold(segment.ActiveGrip);
            hasher.Fold(segment.GripNormal);
            hasher.FoldOpaqueId(segment.GripColliderId);
        }
        hasher.Fold((int)Task);
        hasher.Fold(NeededForLocomotion);
        hasher.Fold(HasLandingTarget);
        hasher.Fold(LandingPoint);
        hasher.Fold(LandingNormal);
        hasher.FoldOpaqueId(LandingColliderId);
        hasher.Fold(IdealLandingPoint);
        hasher.Fold(AtGrabDestination);
        hasher.Fold(GripFraction);
        hasher.Fold(SupportContribution);
        hasher.Fold(SupportNormal);
        hasher.Fold(ReleaseScore);
        hasher.Fold(SearchFailureTicks);
        hasher.Fold(StunTicks);
        hasher.Fold(StepSerial);
        hasher.Fold(LandingSerial);
        hasher.Fold(SearchSerial);
        hasher.Fold(ReachInvalidationSerial);
        hasher.Fold(ValidationInvalidationSerial);
        hasher.Fold(ResidualRecoverySerial);
        hasher.Fold(ResidualInvalidationSerial);
        hasher.Fold(BacktrackFrom);
        hasher.Fold(PostConstraintSweepSerial);
        hasher.Fold(TerrainBacktrackSerial);
        hasher.Fold(MaximumBarrierTicks);
        hasher.Fold(TerrainRecoveryActive);
        hasher.Fold(_terrainRecoveryFrom);
        hasher.Fold(_terrainRecoveryPhase);
        hasher.Fold(_terrainRecoveryPoint);
        hasher.Fold(_terrainRecoveryNormal);
        hasher.Fold(_terrainRecoveryTangentReference);
        hasher.Fold(_lastWorldPreference);
        hasher.Fold(_lastFrameAxisA);
        hasher.Fold(_lastFrameAxisB);
        hasher.Fold(_topologyBarrierTicks);
        hasher.Fold(_guideObstructionObserved);
        hasher.Fold(_guideObstructionTicks);
        hasher.Fold(_guideArchScale);
        hasher.Fold(MaximumGuideObstructionTicks);
        hasher.Fold(GuideObstructionReleaseSerial);
        for (int i = 0; i < _barrierTicks.Length; i++)
        {
            hasher.Fold(_barrierTicks[i]);
            hasher.Fold(_barrierPoints[i]);
            hasher.Fold(_barrierNormals[i]);
        }
        hasher.Fold(TerrainDistanceHint);
        hasher.Fold(_landingValidationAge);
        hasher.Fold(_idleLandingValidationFailures);
        hasher.Fold(_idleProbeSerial);
        hasher.Fold(_landingAge);
        hasher.Fold((int)ReplantPhase);
        hasher.Fold(ReplantPhaseTicks);
        hasher.Fold(StepInFlight);
        hasher.Fold(StartReplantActive);
        hasher.Fold(StartReplantDirection);
        hasher.Fold(_releaseStart);
        hasher.Fold(_peelReleasedFrom);
        hasher.Fold(_peelNormal);
        hasher.Fold(_replantSurfacePoint);
        hasher.Fold(_replantSurfaceNormal);
        hasher.Fold(_hasReplantSurface);
        hasher.Fold(_forceLandingSearch);
        hasher.Fold(_idleLandingRetargetCommitted);
        hasher.Fold(_needsTerrainExpansion);
        for (int i = 0; i < _releaseOrigins.Length; i++)
        {
            hasher.Fold(_releaseOrigins[i]);
            hasher.Fold(_releaseNormals[i]);
        }
        hasher.Fold(_pendingReleaseTargetId is not null);
        if (_pendingReleaseTargetId is { } pendingReleaseTargetId)
            hasher.Fold(pendingReleaseTargetId);
        hasher.Fold(_externalTarget is not null);
        if (_externalTarget is { } target)
        {
            hasher.Fold(target.StableId);
            hasher.Fold(target.Position);
            hasher.Fold(target.VelocityPerTick);
            hasher.Fold(target.Radius);
            hasher.Fold(target.Mass);
            hasher.Fold(target.PullTowardBody);
        }
        hasher.Fold(TargetEffect.TentacleIndex);
        hasher.Fold(TargetEffect.TargetId);
        hasher.Fold(TargetEffect.Reached);
        hasher.Fold(TargetEffect.Held);
        hasher.Fold(TargetEffect.Released);
        hasher.Fold(TargetEffect.PositionCorrection);
        hasher.Fold(TargetEffect.VelocityDelta);
    }

    private void UpdateIdealLanding(
        Vector3 worldPreference,
        Vector3 moveDirection,
        float stuckAmount)
    {
        float blend = Mathf.Lerp(
            _parameters.PreferenceMoveBlend,
            _parameters.StuckMoveBlend,
            stuckAmount);
        if (StartReplantActive)
            blend = Math.Max(blend, _parameters.StartReplantMoveBlend);
        Vector3 move = StartReplantActive
            ? StartReplantDirection
            : moveDirection.LengthSquared() > 1e-10f
                ? moveDirection.Normalized()
                : worldPreference;
        Vector3 direction = SphericalBlend(worldPreference, move, blend);
        IdealLandingPoint = Anchor.Pos + direction * (Length * _parameters.IdealReachRatio);
        if (!HasLandingTarget)
        {
            ReleaseScore = Length;
            return;
        }
        float nearest = float.PositiveInfinity;
        for (int i = _segments.Length / 2; i < _segments.Length; i++)
            nearest = Math.Min(nearest, _segments[i].Pos.DistanceTo(IdealLandingPoint));
        ReleaseScore = nearest;
    }

    private void SearchForLanding(
        in TickContext ctx,
        Vector3 worldPreference,
        Vector3 moveDirection,
        float stuckAmount,
        bool movementEpisodeActive,
        Vector3 frameAxisA,
        Vector3 frameAxisB)
    {
        float failure = _parameters.EnableSearchExpansion
            ? Mathf.Clamp(
                (float)SearchFailureTicks / _parameters.SearchFailureExpandTicks, 0f, 1f)
            : 0f;
        float searchRecovery = _parameters.EnableSearchExpansion
            ? Math.Max(failure, stuckAmount)
            : 0f;
        Vector3 move = StartReplantActive
            ? StartReplantDirection
            : moveDirection.LengthSquared() > 1e-10f
                ? moveDirection.Normalized()
                : worldPreference;
        float moveBlend = Mathf.Lerp(
            _parameters.PreferenceMoveBlend,
            _parameters.StuckMoveBlend,
            stuckAmount);
        if (StartReplantActive)
            moveBlend = Math.Max(moveBlend, _parameters.StartReplantMoveBlend);
        Vector3 baseDirection = SphericalBlend(worldPreference, move, moveBlend);
        Vector3 deterministicJitter = DaddyLongLegsFactory.SampleUnitVector(
            _stableSeed,
            0x5100UL + (ulong)Index,
            SearchSerial);
        float jitter = searchRecovery * _parameters.SearchJitterMaximum;

        float reachRatio = Mathf.Lerp(
            _parameters.SearchReachMinimumRatio,
            _parameters.SearchReachMaximumRatio,
            searchRecovery);
        float reach = Length * reachRatio;
        Vector3 tangentA = baseDirection.Cross(frameAxisA);
        if (tangentA.LengthSquared() <= 1e-10f)
            tangentA = baseDirection.Cross(frameAxisB);
        tangentA = NormalizeOr(tangentA, Orthogonal(baseDirection));
        Vector3 tangentB = NormalizeOr(baseDirection.Cross(tangentA), Orthogonal(baseDirection));
        float coneDegrees = Mathf.Lerp(
            _parameters.SearchConeMinimumDegrees,
            _parameters.SearchConeMaximumDegrees,
            searchRecovery);
        float phase = DaddyLongLegsFactory.Sample01(
            _stableSeed, 0x5200UL + (ulong)Index, SearchSerial) * Mathf.Tau;
        float goldenAngle = Mathf.Pi * (3f - Mathf.Sqrt(5f));

        bool found = false;
        float bestScore = float.PositiveInfinity;
        TerrainHit best = default;
        bool hasSurfaceSpanCandidate = TryBuildSurfaceSpanCandidate(
            move,
            out Vector3 surfaceSpanDirection,
            out float surfaceSpanReach,
            out Vector3 surfaceSpanTarget,
            out Vector3 surfaceSpanNormal);
        Vector3 surfaceAdvance = Vector3.Zero;
        if (ReplantPhase == DaddyTentacleReplantPhase.Reaching
            && _peelNormal.LengthSquared() > 1e-10f
            && move.LengthSquared() > 1e-10f)
        {
            Vector3 normal = _peelNormal.Normalized();
            Vector3 tangentMove = move - normal * move.Dot(normal);
            if (tangentMove.LengthSquared() > 1e-10f)
            {
                surfaceAdvance = NormalizeOr(
                    tangentMove.Normalized()
                        - normal * _parameters.SurfaceAdvanceNormalWeight,
                    baseDirection);
            }
        }
        for (int ray = 0; ray < _parameters.SearchRayCount; ray++)
        {
            Vector3 direction;
            if (ray == 0)
            {
                direction = hasSurfaceSpanCandidate
                    ? surfaceSpanDirection
                    : surfaceAdvance.LengthSquared() > 1e-10f
                        ? surfaceAdvance
                        : baseDirection;
            }
            else
            {
                float radial = Mathf.Sqrt((float)ray / (_parameters.SearchRayCount - 1));
                float angle = Mathf.DegToRad(coneDegrees * radial);
                float azimuth = phase + goldenAngle * ray;
                Vector3 around = tangentA * Mathf.Cos(azimuth) + tangentB * Mathf.Sin(azimuth);
                direction = NormalizeOr(
                    baseDirection * Mathf.Cos(angle) + around * Mathf.Sin(angle),
                    baseDirection);
                // ray 0 永远保留职责分配器给出的 coherent 基向；只有扩散候选吃
                // seed 决定的扰动，避免满 stuck 时全部射线一起随机偏离 detour。
                if (jitter > 0f)
                    direction = NormalizeOr(direction.Lerp(deterministicJitter, jitter), direction);
            }
            float candidateReach = ray == 0 && hasSurfaceSpanCandidate
                ? surfaceSpanReach
                : ray == 0 && surfaceAdvance.LengthSquared() > 1e-10f
                    ? Math.Max(reach, Length * 0.90f)
                    : reach;
            if (!ctx.Terrain.Raycast(
                    Anchor.Pos,
                    Anchor.Pos + direction * candidateReach,
                    out TerrainHit hit)
                || hit.Normal.LengthSquared() <= 1e-10f)
            {
                continue;
            }
            bool sameSurfaceSpanHit = ray == 0 && hasSurfaceSpanCandidate
                && hit.Normal.Normalized().Dot(surfaceSpanNormal) >= 0.45f;
            if (ray == 0 && hasSurfaceSpanCandidate && !sameSurfaceSpanHit)
                continue;
            float distance = Anchor.Pos.DistanceTo(hit.Point);
            if (distance < Anchor.Radius * 0.5f || distance > Length * 1.22f)
                continue;
            Vector3 scoringTarget = sameSurfaceSpanHit
                ? surfaceSpanTarget
                : IdealLandingPoint;
            float score = hit.Point.DistanceSquaredTo(scoringTarget)
                + distance * distance * 0.015f;
            if (score < bestScore)
            {
                found = true;
                bestScore = score;
                best = hit;
            }
        }
        SearchSerial++;

        if (found)
        {
            bool changed = !HasLandingTarget
                || LandingPoint.DistanceSquaredTo(best.Point) > 1e-6f
                || LandingNormal.Dot(best.Normal) < 0.999f;
            LandingPoint = best.Point;
            LandingNormal = best.Normal.Normalized();
            LandingColliderId = best.ColliderId;
            HasLandingTarget = true;
            _landingValidationAge = 0;
            _idleLandingValidationFailures = 0;
            _landingAge = 0;
            SearchFailureTicks = 0;
            if (_parameters.EnableIdleLandingStability && !movementEpisodeActive)
                _idleLandingRetargetCommitted = true;
            if (changed)
                LandingSerial++;
        }
        else
        {
            SearchFailureTicks = Math.Min(
                SearchFailureTicks + _parameters.SearchRefreshTicks,
                _parameters.SearchFailureExpandTicks * 2);
        }
    }

    private bool TryBuildSurfaceSpanCandidate(
        Vector3 move,
        out Vector3 direction,
        out float candidateReach,
        out Vector3 target,
        out Vector3 surfaceNormal)
    {
        direction = Vector3.Zero;
        candidateReach = 0f;
        target = Vector3.Zero;
        surfaceNormal = Vector3.Zero;
        if (!_parameters.EnableSurfaceSpanReplant)
            return false;

        Vector3 surfacePoint;
        if (HasLandingTarget && LandingNormal.LengthSquared() > 1e-10f)
        {
            surfacePoint = LandingPoint;
            surfaceNormal = LandingNormal.Normalized();
        }
        else if (_hasReplantSurface
            && _replantSurfaceNormal.LengthSquared() > 1e-10f)
        {
            surfacePoint = _replantSurfacePoint;
            surfaceNormal = _replantSurfaceNormal.Normalized();
        }
        else
        {
            return false;
        }

        float normalSpan = (Anchor.Pos - surfacePoint).Dot(surfaceNormal);
        float availableReach = Length * _parameters.SurfaceSpanReachRatio;
        if (normalSpan <= Anchor.Radius * 0.25f || normalSpan >= availableReach)
            return false;
        float tangentCapacity = MathF.Sqrt(Math.Max(
            0f,
            availableReach * availableReach - normalSpan * normalSpan));
        if (tangentCapacity <= _parameters.LandingSurfaceOffset)
            return false;

        Vector3 anchorProjection = Anchor.Pos - surfaceNormal * normalSpan;
        Vector3 tangentIdeal = IdealLandingPoint - anchorProjection;
        tangentIdeal -= surfaceNormal * tangentIdeal.Dot(surfaceNormal);
        Vector3 tangentMove = move - surfaceNormal * move.Dot(surfaceNormal);
        Vector3 tangent = tangentIdeal.LengthSquared() > 1e-10f
            ? tangentIdeal.Normalized()
            : tangentMove.LengthSquared() > 1e-10f
                ? tangentMove.Normalized()
                : Vector3.Zero;
        if (tangent.LengthSquared() <= 1e-10f)
            return false;
        float requestedAdvance = tangentIdeal.LengthSquared() > 1e-10f
            ? tangentIdeal.Length()
            : Length * _parameters.SurfaceSpanAdvanceMaximumRatio;
        float advance = Math.Min(
            Math.Min(requestedAdvance,
                Length * _parameters.SurfaceSpanAdvanceMaximumRatio),
            tangentCapacity * 0.92f);
        if (advance <= _parameters.LandingSurfaceOffset)
            return false;

        target = anchorProjection + tangent * advance;
        Vector3 rayEnd = target - surfaceNormal
            * (_parameters.LandingSurfaceOffset + _parameters.ContactProbeExtra);
        Vector3 delta = rayEnd - Anchor.Pos;
        if (delta.LengthSquared() <= 1e-10f)
            return false;
        candidateReach = delta.Length();
        direction = delta / candidateReach;
        return true;
    }

    private void ValidateLanding(in TickContext ctx, bool movementEpisodeActive)
    {
        if (!HasLandingTarget)
            return;
        // 搜索只接受 1.22×Length 内的点；身体走远后同一门也必须让旧点失效，
        // 否则 free-duty 触手会无限保留一个链条已不可能到达的世界锚。
        float maximumReach = Length * 1.22f;
        if (Anchor.Pos.DistanceSquaredTo(LandingPoint) > maximumReach * maximumReach)
        {
            ReachInvalidationSerial++;
            ClearLanding(incrementStep: false);
            return;
        }
        _landingValidationAge++;
        if (_landingValidationAge < _parameters.LandingValidationTicks)
            return;
        _landingValidationAge = 0;
        float distance = _parameters.SegmentRadius + _parameters.ContactProbeExtra + 0.04f;
        Vector3 from = LandingPoint + LandingNormal * distance;
        Vector3 to = LandingPoint - LandingNormal * 0.04f;
        bool invalid = !ctx.Terrain.Raycast(from, to, out TerrainHit hit)
            || hit.Normal.LengthSquared() <= 1e-10f
            || hit.Normal.Normalized().Dot(LandingNormal) < 0.45f;
        if (!invalid)
        {
            _idleLandingValidationFailures = 0;
            return;
        }
        if (_parameters.EnableIdleLandingStability && !movementEpisodeActive)
        {
            _idleLandingValidationFailures++;
            if (_idleLandingValidationFailures
                < _parameters.IdleLandingValidationFailures)
            {
                return;
            }
        }
        _idleLandingValidationFailures = 0;
        ValidationInvalidationSerial++;
        ClearLanding(incrementStep: false);
    }

    private void ProbeIdleTerrain(in TickContext ctx, Vector3 worldPreference)
    {
        if ((ctx.TickIndex + Index) % _parameters.IdleProximityProbeTicks != 0)
            return;
        Vector3 gravity = ctx.GravityPerTick.LengthSquared() > 1e-10f
            ? ctx.GravityPerTick.Normalized()
            : Vector3.Down;
        Vector3 direction = (_idleProbeSerial++ & 3) switch
        {
            0 => worldPreference,
            1 => -worldPreference,
            2 => gravity,
            _ => -gravity,
        };
        Vector3 tip = _segments[^1].Pos;
        if (ctx.Terrain.Raycast(
                tip,
                tip + direction * _parameters.IdleProximityProbeLength,
                out TerrainHit hit)
            && hit.Normal.LengthSquared() > 1e-10f)
        {
            TerrainDistanceHint = tip.DistanceTo(hit.Point);
        }
        else
        {
            TerrainDistanceHint = float.PositiveInfinity;
        }
    }

    private void IntegrateSegments(in TickContext ctx, Vector3 worldPreference)
    {
        bool stunned = StunTicks > 0;
        bool maintainsTerrainTask = MaintainsTerrainTask;
        bool soft = stunned || (!maintainsTerrainTask
            && Task == DaddyTentacleTask.Locomotion
            && !HasLandingTarget);
        Vector3 externalPoint = _externalTarget?.Position ?? Vector3.Zero;
        bool hasGuide = Task == DaddyTentacleTask.Locomotion && HasLandingTarget;
        if (ReplantPhase == DaddyTentacleReplantPhase.Peeling)
            UpdatePeelReleaseBoundary();
        if (hasGuide)
            BuildLandingGuide(worldPreference);
        else
            ResetGuideDiagnostics();

        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            _guideTargetValid[i] = false;
            ResetContactManifold(i);
            segment.PreviousContactNormal = segment.ContactNormal;
            segment.PreviousActiveGrip = segment.ActiveGrip;
            segment.PreviousGripNormal = segment.GripNormal;
            segment.LastPos = segment.Pos;
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
            segment.Vel *= _parameters.SegmentDamping;
            float u = (float)(i + 1) / _segments.Length;
            if (!stunned && BacktrackFrom >= 0 && i >= BacktrackFrom)
            {
                Vector3 predecessor = i == 0 ? Anchor.Pos : _segments[i - 1].Pos;
                ApplyCappedServo(
                    segment,
                    predecessor,
                    _parameters.TerrainBacktrackRetractionSpeed);
            }
            else if (soft)
            {
                if (!stunned || _parameters.EnableStunLimp)
                    segment.Vel += ctx.GravityPerTick * _parameters.LimpGravityScale;
            }
            else if (Task == DaddyTentacleTask.ExternalReach && _externalTarget is not null)
            {
                Vector3 desired = Anchor.Pos.Lerp(externalPoint, u);
                _guideTargets[i] = desired;
                _guideTargetValid[i] = true;
                ApplyServo(segment, desired, _parameters.SegmentTargetServo);
            }
            else if (ReplantPhase == DaddyTentacleReplantPhase.Peeling)
            {
                if (i >= _peelReleasedFrom)
                {
                    float distal = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(_parameters.StepPeelStartFraction, 1f, u));
                    float progress = Mathf.Clamp(
                        (float)(ReplantPhaseTicks + 1) / _parameters.StepPeelTicks,
                        0f,
                        1f);
                    Vector3 normal = NormalizeOr(_releaseNormals[i], _peelNormal);
                    float clearance = segment.Radius + _parameters.ContactProbeExtra + 0.08f;
                    Vector3 peelTarget = _releaseOrigins[i]
                        + normal * (clearance * Mathf.SmoothStep(0f, 1f, progress));
                    ApplyCappedServo(
                        segment,
                        peelTarget,
                        _parameters.StepPeelVelocity * Math.Max(0.2f, distal));
                    Vector3 retractTarget = Anchor.Pos.Lerp(_releaseOrigins[i], u * 0.82f);
                    ApplyServo(segment, retractTarget,
                        _parameters.StepPeelRetractionServo * distal);
                }
            }
            else if (hasGuide)
            {
                Vector3 desired = EvaluateLandingGuide(u);
                _guideTargets[i] = desired;
                _guideTargetValid[i] = true;
                ApplyServo(segment, desired, _parameters.SegmentPathServo);
                if (!_parameters.EnableSlackGuide && i == _segments.Length - 1)
                {
                    Vector3 landingCenter = LandingPoint + LandingNormal
                        * (_parameters.SegmentRadius + _parameters.LandingSurfaceOffset);
                    ApplyServo(segment, landingCenter, _parameters.SegmentTargetServo);
                }
            }
            else if (Task == DaddyTentacleTask.Locomotion)
            {
                Vector3 toIdeal = IdealLandingPoint - Anchor.Pos;
                Vector3 reachable = Anchor.Pos + toIdeal.LimitLength(Length * 0.92f);
                ApplyServo(
                    segment,
                    Anchor.Pos.Lerp(reachable, u),
                    _parameters.SegmentPathServo * 0.55f);
            }
            else
            {
                segment.Vel += ctx.GravityPerTick * 0.55f;
            }

            if (maintainsTerrainTask && u < 0.20f
                && (BacktrackFrom < 0 || i < BacktrackFrom))
            {
                segment.Vel += worldPreference
                    * (Mathf.InverseLerp(0.20f, 0f, u) * _parameters.SegmentRootSpreadForce);
            }
            segment.Vel = segment.Vel.LimitLength(_parameters.SegmentVelocityCap);
            segment.Pos += segment.Vel;
        }
        SeparateSegments();
    }

    private void SolveChainConstraints()
    {
        MaximumConstraintError = 0f;
        for (int iteration = 0; iteration < _parameters.TentacleConstraintIterations; iteration++)
        {
            Vector3 previous = Anchor.Pos;
            for (int i = 0; i < _segments.Length; i++)
            {
                DaddyTentacleSegmentState current = _segments[i];
                // 原子候选尚未找到时，真实阻断边必须是无张力边；否则已清掉抓附的
                // 墙后后缀仍会通过 pull-only 约束拖住身体。后缀内部继续保持链长。
                if (BacktrackFrom >= 0 && i == BacktrackFrom)
                {
                    previous = current.Pos;
                    continue;
                }
                Vector3 delta = current.Pos - previous;
                float distance = delta.Length();
                MaximumConstraintError = Math.Max(
                    MaximumConstraintError, Math.Max(0f, distance - LinkLength));
                if (distance > LinkLength)
                {
                    Vector3 direction = distance > 1e-6f
                        ? delta / distance
                        : DaddyLongLegsFactory.SampleUnitVector(
                            _stableSeed, 0x5300UL + (ulong)Index, i);
                    Vector3 correction = direction * (distance - LinkLength);
                    if (i == 0)
                    {
                        current.Pos -= correction;
                        current.Vel -= correction;
                    }
                    else
                    {
                        DaddyTentacleSegmentState prior = _segments[i - 1];
                        Vector3 half = correction * 0.5f;
                        prior.Pos += half;
                        prior.Vel += half;
                        current.Pos -= half;
                        current.Vel -= half;
                    }
                }
                previous = current.Pos;
            }
        }
    }

    private void SeparateSegments()
    {
        // 两轮固定序投影仍是有界 O(n²)，但能消除单轮 Gauss-Seidel 中后续 pair
        // 把先前 pair 再压近的明显残差。段数上限由形态参数钳死为 32。
        for (int iteration = 0; iteration < 2; iteration++)
        {
            for (int i = 0; i < _segments.Length; i++)
            {
                for (int j = i + 1; j < _segments.Length; j++)
                {
                    if (BacktrackFrom >= 0
                        && (i < BacktrackFrom) != (j < BacktrackFrom))
                    {
                        // 恢复中的墙后后缀不能再借自避把位移反馈给近端链。
                        continue;
                    }
                    DaddyTentacleSegmentState a = _segments[i];
                    DaddyTentacleSegmentState b = _segments[j];
                    Vector3 delta = b.Pos - a.Pos;
                    float distance = delta.Length();
                    float minimum = Math.Max(
                        a.Radius + b.Radius,
                        _parameters.SegmentSelfSeparation);
                    if (distance >= minimum)
                        continue;
                    Vector3 direction = distance > 1e-6f
                        ? delta / distance
                        : DaddyLongLegsFactory.SampleUnitVector(
                            _stableSeed, 0x5400UL + (ulong)Index, i * _segments.Length + j);
                    Vector3 correction = direction * ((minimum - distance) * 0.5f);
                    Vector3 correctionA = ConstrainToContactCone(i, -correction);
                    Vector3 correctionB = ConstrainToContactCone(j, correction);
                    // 若一侧因接触面投影失去法向自由度，把缺少的位移交给另一侧；
                    // 两侧都受限时仍保留各自切向分量，不凭空制造入面位移。
                    float applied = (correctionB - correctionA).Dot(direction);
                    float missing = Math.Max(0f, minimum - distance - applied);
                    if (missing > 1e-6f)
                    {
                        Vector3 extraA = ConstrainToContactCone(i, -direction * missing);
                        Vector3 extraB = ConstrainToContactCone(j, direction * missing);
                        float freedomA = -extraA.Dot(direction);
                        float freedomB = extraB.Dot(direction);
                        if (freedomA > freedomB && freedomA > 1e-6f)
                            correctionA += extraA * Math.Min(1f, missing / freedomA);
                        else if (freedomB > 1e-6f)
                            correctionB += extraB * Math.Min(1f, missing / freedomB);
                    }
                    a.Pos += correctionA;
                    a.Vel += correctionA;
                    b.Pos += correctionB;
                    b.Vel += correctionB;
                }
            }
        }
        UpdateMinimumSelfSeparation();
    }

    private void UpdateMinimumSelfSeparation()
    {
        MinimumSelfSeparation = float.PositiveInfinity;
        for (int i = 0; i < _segments.Length; i++)
        {
            for (int j = i + 1; j < _segments.Length; j++)
            {
                MinimumSelfSeparation = Math.Min(
                    MinimumSelfSeparation,
                    _segments[i].Pos.DistanceTo(_segments[j].Pos));
            }
        }
    }

    private Vector3 ConstrainToContactCone(int segmentIndex, Vector3 correction)
    {
        // 单一平均法线在 90° 内角会留下“沿平均切平面钻入其中一面”的自由度。
        // 固定三法线、固定八轮 half-space 投影覆盖非正交 floor/wall/corner，且无分配。
        // 单遍后一个法线可能重新引入对前一个面的入面分量；仍不收敛时宁可不做自避。
        for (int pass = 0; pass < 8; pass++)
        {
            bool adjusted = false;
            for (int normalIndex = 0;
                 normalIndex < _contactNormalCounts[segmentIndex];
                 normalIndex++)
            {
                Vector3 normal = ContactNormalAt(segmentIndex, normalIndex);
                float into = correction.Dot(normal);
                if (into < -1e-6f)
                {
                    correction -= normal * into;
                    adjusted = true;
                }
            }
            if (!adjusted)
                break;
        }
        for (int normalIndex = 0;
             normalIndex < _contactNormalCounts[segmentIndex];
             normalIndex++)
        {
            if (correction.Dot(ContactNormalAt(segmentIndex, normalIndex)) < -1e-5f)
                return Vector3.Zero;
        }
        return correction;
    }

    private void ResolveTerrain(in TickContext ctx, Vector3 worldPreference)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            Vector3 motion = segment.Pos - segment.LastPos;
            float motionLength = motion.Length();
            if (motionLength > 1e-6f)
            {
                Vector3 direction = motion / motionLength;
                if (ctx.Terrain.Raycast(
                        segment.LastPos,
                        segment.Pos + direction * segment.Radius,
                        out TerrainHit sweep))
                {
                    ResolveCollision(i, segment, sweep, observeGuideBarrier: true);
                }
            }

            if (TryGetGripProbeNormal(i, segment, out Vector3 probeNormal))
            {
                float probe = segment.Radius + _parameters.ContactProbeExtra;
                if (ctx.Terrain.Raycast(
                        segment.Pos,
                        segment.Pos - probeNormal * probe,
                        out TerrainHit adhesionHit))
                {
                    MaintainSurfaceGrip(i, segment, adhesionHit);
                }
            }

            if (ctx.Terrain.SpherePenetration(
                    segment.Pos,
                    segment.Radius,
                    out Vector3 pushDirection,
                    out float depth))
            {
                Vector3 normal = pushDirection.LengthSquared() > 1e-10f
                    ? pushDirection.Normalized()
                    : Vector3.Zero;
                if (normal.LengthSquared() <= 1e-10f)
                    continue;
                Vector3 surfacePoint = segment.Pos
                    + normal * (depth - segment.Radius);
                ObserveGuideBarrier(i, surfacePoint, normal);
                segment.Pos += normal * depth;
                SphereTerrain.RespondVelocity(
                    normal, _parameters.SegmentCollisionFriction, ref segment.Vel);
                RegisterContactNormal(i, normal);
            }
            if (!segment.TerrainContact)
                segment.ContactNormal = Vector3.Zero;
        }
    }

    private void ResolvePostConstraintTerrain(in TickContext ctx)
    {
        if (!_parameters.EnableTerrainBacktrack)
            return;
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            Vector3 motion = segment.Pos - segment.LastPos;
            float distance = motion.Length();
            if (distance <= 1e-6f)
                continue;
            Vector3 direction = motion / distance;
            if (!ctx.Terrain.Raycast(
                    segment.LastPos,
                    segment.Pos + direction * segment.Radius,
                    out TerrainHit hit)
                || hit.Normal.LengthSquared() <= 1e-10f
                || motion.Dot(hit.Normal) >= 0f)
            {
                continue;
            }
            if (ResolveCollision(i, segment, hit, observeGuideBarrier: false))
                PostConstraintSweepSerial++;
        }
    }

    private void AuditTerrainBacktrack(in TickContext ctx)
    {
        if (!_parameters.EnableTerrainBacktrack)
        {
            ResetTerrainBacktrackState();
            return;
        }

        Vector3 predecessor = Anchor.Pos;
        float predecessorRadius = Anchor.TerrainRadius;
        int firstBlocked = -1;
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            bool linkBlocked = IsInteriorTerrainBarrier(
                ctx, predecessor, predecessorRadius, segment.Pos, segment.Radius,
                out TerrainHit barrierHit);
            if (linkBlocked)
            {
                _barrierObserved[i] = true;
                _barrierPoints[i] = barrierHit.Point;
                _barrierNormals[i] = OrientBarrierNormal(
                    barrierHit.Normal, predecessor, segment.Pos, barrierHit.Point,
                    _lastWorldPreference);
                if (firstBlocked < 0)
                    firstBlocked = i;
            }
            _barrierTicks[i] = _barrierObserved[i]
                ? _barrierTicks[i] < int.MaxValue
                    ? _barrierTicks[i] + 1
                    : int.MaxValue
                : 0;

            predecessor = segment.Pos;
            predecessorRadius = segment.Radius;
        }

        // 阻断点可能沿链逐段迁移，不能只看每条边自己的短 run；整条触手只要
        // 每 tick 都仍有真实阻断，就属于同一个 episode。
        _topologyBarrierTicks = firstBlocked >= 0
            ? _topologyBarrierTicks < int.MaxValue
                ? _topologyBarrierTicks + 1
                : int.MaxValue
            : 0;
        MaximumBarrierTicks = Math.Max(MaximumBarrierTicks, _topologyBarrierTicks);

        if (_terrainRecoveryFrom >= 0 && firstBlocked >= 0
            && firstBlocked < _terrainRecoveryFrom)
        {
            // 身体/近端移动可能让阻断向根部迁移；扩大断开的后缀并从新的安全侧重启相位。
            BeginTerrainRecovery(firstBlocked);
        }
        else if (_terrainRecoveryFrom < 0
            && firstBlocked >= 0
            && _topologyBarrierTicks >= _parameters.TerrainBacktrackReleaseTicks)
        {
            BeginTerrainRecovery(firstBlocked);
        }

        if (_terrainRecoveryFrom >= 0)
        {
            // 即使原阻断射线已经自然消失，也不能直接提交当前后缀：卸力期间它可能
            // 与近端互相穿插。只有完整球壳/链边/自避证书的原子候选才能重新接回。
            if (TryBuildTerrainRecoveryCandidate(ctx))
            {
                FinishTerrainRecovery();
                firstBlocked = -1;
            }
            else
            {
                _terrainRecoveryPhase = (_terrainRecoveryPhase + 1)
                    % _parameters.TerrainBacktrackCandidatePhases;
            }
        }

        BacktrackFrom = _terrainRecoveryFrom >= 0
            ? _terrainRecoveryFrom
            : firstBlocked;
        if (BacktrackFrom >= 0)
            ClearDistalGrip(BacktrackFrom);

        ProcessGuideObstruction();
    }

    private bool IsInteriorTerrainBarrier(
        in TickContext ctx,
        Vector3 from,
        float fromRadius,
        Vector3 to,
        float toRadius,
        out TerrainHit interiorHit)
    {
        interiorHit = default;
        Vector3 delta = to - from;
        float length = delta.Length();
        // 只有真实球壳体积已经由球查询背书；TerrainSkin 是响应容差，不是实体体积。
        // 若把 skin 也剪掉，厚度 <=2*skin 的薄墙会整段落在“无需查询”区而永久漏检。
        float fromClearance = fromRadius;
        float toClearance = toRadius;
        if (length <= fromClearance + toClearance)
            return false;
        Vector3 direction = delta / length;
        // 直接剪掉两端已被球壳可行性背书的包络，否则一根从贴面
        // 前驱出发的射线可能只返回端点命中，把后续真正穿过实体的部分遮住。
        // 剪裁后的任何命中都位于链边内部；HitFromInside 的零法线在这里也是
        // 拓扑阻断证据，不需要像碰撞响应那样丢弃。
        Vector3 auditFrom = from + direction * fromClearance;
        Vector3 auditTo = to - direction * toClearance;
        return ctx.Terrain.Raycast(auditFrom, auditTo, out interiorHit);
    }

    private void BeginTerrainRecovery(int firstBlocked)
    {
        _terrainRecoveryFrom = firstBlocked;
        _terrainRecoveryPhase = 0;
        _terrainRecoveryPoint = _barrierPoints[firstBlocked];
        Vector3 predecessor = firstBlocked == 0
            ? Anchor.Pos
            : _segments[firstBlocked - 1].Pos;
        _terrainRecoveryNormal = NormalizeOr(
            _barrierNormals[firstBlocked],
            NormalizeOr(predecessor - _segments[firstBlocked].Pos, _lastWorldPreference));
        Vector3 frameReference = Math.Abs(_terrainRecoveryNormal.Dot(_lastFrameAxisA))
            <= Math.Abs(_terrainRecoveryNormal.Dot(_lastFrameAxisB))
                ? _lastFrameAxisA
                : _lastFrameAxisB;
        _terrainRecoveryTangentReference = NormalizeOr(
            frameReference
                - _terrainRecoveryNormal * frameReference.Dot(_terrainRecoveryNormal),
            NormalizeOr(
                _lastWorldPreference
                    - _terrainRecoveryNormal
                        * _lastWorldPreference.Dot(_terrainRecoveryNormal),
                _lastFrameAxisA));
        BacktrackFrom = firstBlocked;
        NeededForLocomotion = false;
        ReleaseTerrainTaskForBacktrack();
        ClearDistalGrip(firstBlocked);
    }

    private bool TryBuildTerrainRecoveryCandidate(in TickContext ctx)
    {
        int firstBlocked = _terrainRecoveryFrom;
        if (firstBlocked < 0)
            return false;

        Vector3 predecessor = firstBlocked == 0
            ? Anchor.Pos
            : _segments[firstBlocked - 1].Pos;
        float predecessorRadius = firstBlocked == 0
            ? Anchor.TerrainRadius
            : _segments[firstBlocked - 1].Radius;
        // 物理半径裁剪的链边证书以前驱球壳也已可行为前提；共享 Body/segment
        // 解算在退化 MTD 路径不提供可复用的严格 miss 证书，因此恢复处显式复验一次。
        if (ctx.Terrain.SpherePenetration(
                predecessor, predecessorRadius, out _, out _))
        {
            return false;
        }
        Vector3 direction = TerrainRecoveryDirection(predecessor);
        float minimumSpacing = Math.Max(
            _parameters.SegmentSelfSeparation,
            _parameters.SegmentRadius * 2f);
        float spacing = minimumSpacing
            + Math.Max(1e-5f,
                Math.Min(0.05f, (LinkLength - minimumSpacing) * 0.25f));

        for (int i = firstBlocked; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            Vector3 candidate = predecessor + direction * spacing;

            // 原子候选先做纯代数自避；不能为了逃墙制造恢复后才会爆开的重叠段。
            for (int j = 0; j < firstBlocked; j++)
            {
                float minimum = Math.Max(
                    segment.Radius + _segments[j].Radius,
                    _parameters.SegmentSelfSeparation) + 1e-5f;
                if (candidate.DistanceSquaredTo(_segments[j].Pos)
                    < minimum * minimum)
                {
                    return false;
                }
            }
            for (int j = firstBlocked; j < i; j++)
            {
                float minimum = Math.Max(
                    segment.Radius + _segments[j].Radius,
                    _parameters.SegmentSelfSeparation) + 1e-5f;
                if (candidate.DistanceSquaredTo(_terrainRecoveryCandidates[j])
                    < minimum * minimum)
                {
                    return false;
                }
            }

            // 一个 phase 每段最多 Sphere(2 primitive)+link ray(1)；整条后缀全部
            // 通过后才提交，任何末端失败都不会留下半截新链。
            if (ctx.Terrain.SpherePenetration(
                    candidate, segment.Radius, out _, out _)
                || IsInteriorTerrainBarrier(
                    ctx, predecessor, predecessorRadius,
                    candidate, segment.Radius, out _))
            {
                return false;
            }
            _terrainRecoveryCandidates[i] = candidate;
            predecessor = candidate;
            predecessorRadius = segment.Radius;
        }
        return true;
    }

    private Vector3 TerrainRecoveryDirection(Vector3 predecessor)
    {
        Vector3 safe = NormalizeOr(
            _terrainRecoveryNormal,
            NormalizeOr(predecessor - _terrainRecoveryPoint, _terrainRecoveryTangentReference));
        if ((predecessor - _terrainRecoveryPoint).Dot(safe) < 0f)
            safe = -safe;
        if (_terrainRecoveryPhase == 0
            || _parameters.TerrainBacktrackCandidatePhases == 1)
        {
            return safe;
        }

        int alternatives = Math.Max(1, _parameters.TerrainBacktrackCandidatePhases - 1);
        float u = (_terrainRecoveryPhase - 0.5f) / alternatives;
        float cosine = Mathf.Lerp(0.88f, 0.08f, Mathf.Clamp(u, 0f, 1f));
        float sine = Mathf.Sqrt(Math.Max(0f, 1f - cosine * cosine));
        Vector3 tangentA = _terrainRecoveryTangentReference
            - safe * _terrainRecoveryTangentReference.Dot(safe);
        tangentA = NormalizeOr(tangentA, _lastFrameAxisA);
        Vector3 tangentB = NormalizeOr(safe.Cross(tangentA), _lastFrameAxisB);
        float phaseOffset = DaddyLongLegsFactory.Sample01(
            _stableSeed, 0x5600UL + (ulong)Index, 0) * Mathf.Tau;
        float azimuth = phaseOffset
            + _terrainRecoveryPhase * (Mathf.Pi * (3f - Mathf.Sqrt(5f)));
        Vector3 around = tangentA * Mathf.Cos(azimuth)
            + tangentB * Mathf.Sin(azimuth);
        return NormalizeOr(safe * cosine + around * sine, safe);
    }

    private void FinishTerrainRecovery()
    {
        int firstBlocked = _terrainRecoveryFrom;
        if (firstBlocked < 0)
            return;
        Vector3 recoveryVelocity = firstBlocked == 0
            ? Anchor.Vel
            : _segments[firstBlocked - 1].Vel;
        for (int i = firstBlocked; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            segment.Pos = _terrainRecoveryCandidates[i];
            segment.LastPos = segment.Pos;
            segment.Vel = recoveryVelocity;
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
            ResetContactManifold(i);
            _barrierObserved[i] = false;
            _barrierTicks[i] = 0;
            _barrierPoints[i] = Vector3.Zero;
            _barrierNormals[i] = Vector3.Zero;
        }
        TerrainBacktrackSerial++;
        _terrainRecoveryFrom = -1;
        _terrainRecoveryPhase = 0;
        _terrainRecoveryPoint = Vector3.Zero;
        _terrainRecoveryNormal = Vector3.Zero;
        _terrainRecoveryTangentReference = Vector3.Zero;
        _topologyBarrierTicks = 0;
        BacktrackFrom = -1;
        UpdateMinimumSelfSeparation();
    }

    private void ReleaseTerrainTaskForBacktrack()
    {
        if (_externalTarget is { } target)
        {
            // 不能调用 ClearExternalTarget：它会清掉正在进行的几何恢复相位。
            _pendingReleaseTargetId = target.StableId;
            _externalTarget = null;
            Task = DaddyTentacleTask.Locomotion;
            NeededForLocomotion = false;
            TargetEffect = default;
            ClearSupport();
        }
        if (Task == DaddyTentacleTask.Locomotion)
        {
            ClearLanding(incrementStep: false);
            ReplantPhase = DaddyTentacleReplantPhase.Reaching;
            ReplantPhaseTicks = 0;
            StepInFlight = false;
            StartReplantActive = false;
            StartReplantDirection = Vector3.Zero;
            _forceLandingSearch = true;
            _idleLandingRetargetCommitted = false;
        }
    }

    private void ProcessGuideObstruction()
    {
        if (_parameters.EnableGuideArchAdaptation)
        {
            _guideArchScale = _guideObstructionObserved
                ? Math.Max(0f,
                    _guideArchScale - _parameters.GuideObstructionArchDecayPerTick)
                : Math.Min(1f, _guideArchScale + _parameters.GuideArchRecoveryPerTick);
        }
        _guideObstructionTicks = _guideObstructionObserved
            ? _guideObstructionTicks < int.MaxValue
                ? _guideObstructionTicks + 1
                : int.MaxValue
            : 0;
        MaximumGuideObstructionTicks = Math.Max(
            MaximumGuideObstructionTicks, _guideObstructionTicks);
        if (_guideObstructionTicks < _parameters.TerrainBacktrackReleaseTicks)
            return;

        bool hadTask = HasLandingTarget || _externalTarget is not null;
        if (hadTask)
        {
            ReleaseTerrainTaskForBacktrack();
            GuideObstructionReleaseSerial++;
        }
        _guideObstructionTicks = 0;
        _guideObstructionObserved = false;
    }

    private static Vector3 OrientBarrierNormal(
        Vector3 normal,
        Vector3 predecessor,
        Vector3 blockedSegment,
        Vector3 hitPoint,
        Vector3 fallback)
    {
        if (normal.LengthSquared() <= 1e-10f)
            return NormalizeOr(predecessor - blockedSegment, fallback);
        Vector3 oriented = normal.Normalized();
        if ((predecessor - hitPoint).Dot(oriented) < 0f)
            oriented = -oriented;
        return oriented;
    }

    private void ClearDistalGrip(int firstBlocked)
    {
        for (int i = firstBlocked; i < _segments.Length; i++)
        {
            _segments[i].ActiveGrip = false;
            _segments[i].GripNormal = Vector3.Zero;
            _segments[i].GripColliderId = 0UL;
        }
    }

    private void ObserveGuideBarrier(int segmentIndex, Vector3 point, Vector3 normal)
    {
        if (!_parameters.EnableTerrainBacktrack
            || !_guideTargetValid[segmentIndex]
            || normal.LengthSquared() <= 1e-10f)
        {
            return;
        }
        Vector3 n = normal.Normalized();
        Vector3 predecessor = segmentIndex == 0
            ? Anchor.Pos
            : _segments[segmentIndex - 1].Pos;
        float safeSide = (predecessor - point).Dot(n);
        float targetSide = (_guideTargets[segmentIndex] - point).Dot(n);
        if (safeSide >= -_parameters.TerrainSkin
            && targetSide < -_parameters.TerrainSkin)
        {
            // guide obstruction 只说明目标在碰撞面的另一侧，不等于当前短链边穿墙。
            // 它只让旧任务失效重搜，绝不能触发几何后缀重建。
            _guideObstructionObserved = true;
        }
    }

    private void ResetContactManifold(int segmentIndex)
    {
        _contactNormalCounts[segmentIndex] = 0;
        _contactNormalA[segmentIndex] = Vector3.Zero;
        _contactNormalB[segmentIndex] = Vector3.Zero;
        _contactNormalC[segmentIndex] = Vector3.Zero;
    }

    private void RegisterContactNormal(int segmentIndex, Vector3 value)
    {
        if (!value.IsFinite() || value.LengthSquared() <= 1e-10f)
            return;
        Vector3 normal = value.Normalized();
        int count = _contactNormalCounts[segmentIndex];
        for (int i = 0; i < count; i++)
        {
            if (ContactNormalAt(segmentIndex, i).Dot(normal) > 0.965f)
            {
                UpdatePublicContactNormal(segmentIndex);
                return;
            }
        }
        if (count < 3)
        {
            if (count == 0)
                _contactNormalA[segmentIndex] = normal;
            else if (count == 1)
                _contactNormalB[segmentIndex] = normal;
            else
                _contactNormalC[segmentIndex] = normal;
            _contactNormalCounts[segmentIndex]++;
        }
        UpdatePublicContactNormal(segmentIndex);
    }

    private Vector3 ContactNormalAt(int segmentIndex, int normalIndex) => normalIndex switch
    {
        0 => _contactNormalA[segmentIndex],
        1 => _contactNormalB[segmentIndex],
        _ => _contactNormalC[segmentIndex],
    };

    private void UpdatePublicContactNormal(int segmentIndex)
    {
        DaddyTentacleSegmentState segment = _segments[segmentIndex];
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < _contactNormalCounts[segmentIndex]; i++)
            sum += ContactNormalAt(segmentIndex, i);
        segment.TerrainContact = _contactNormalCounts[segmentIndex] > 0;
        segment.ContactNormal = !segment.TerrainContact
            ? Vector3.Zero
            : sum.LengthSquared() > 1e-10f
                ? sum.Normalized()
                : _contactNormalA[segmentIndex];
    }

    private static Vector3 MergeNormals(Vector3 current, Vector3 added)
    {
        Vector3 sum = current.LengthSquared() > 1e-10f
            ? current.Normalized() + added.Normalized()
            : added;
        return sum.LengthSquared() > 1e-10f ? sum.Normalized() : added.Normalized();
    }

    /// <summary>
    /// 末次 pull-only 段长松弛可能把刚被推出墙面的段重新拉入 collider。这里做有界
    /// 球壳 MTD，不再重跑 sweep/adhesion；因此 tick 结束态以地形可行为最终优先级，
    /// 剩余的细小段长误差留给下一 tick 的固定序继续松弛。共面 collider 接缝若让 MTD
    /// 在相反方向间二周期，或 K=4 的最后一次查询仍命中，先复验并恢复该段上一 tick
    /// 已背书的 LastPos；只有 LastPos 也不可行，才确定性收回 Anchor 并放弃旧落点重新搜索。
    /// </summary>
    private void ResolveResidualTerrain(in TickContext ctx)
    {
        if (!_parameters.EnableResidualTerrainResolve)
            return;
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            Vector3 previousPush = Vector3.Zero;
            for (int iteration = 0;
                 iteration < _parameters.ResidualTerrainResolveIterations;
                 iteration++)
            {
                if (!ctx.Terrain.SpherePenetration(
                        segment.Pos,
                        segment.Radius,
                        out Vector3 pushDirection,
                        out float depth))
                {
                    break;
                }
                float directionLengthSquared = pushDirection.LengthSquared();
                if (!float.IsFinite(depth) || depth <= 0f
                    || !float.IsFinite(pushDirection.X)
                    || !float.IsFinite(pushDirection.Y)
                    || !float.IsFinite(pushDirection.Z)
                    || directionLengthSquared <= 1e-12f)
                {
                    break;
                }
                pushDirection /= MathF.Sqrt(directionLengthSquared);
                bool twoCycle = previousPush.LengthSquared() > 1e-10f
                    && previousPush.Dot(pushDirection) < -0.98f;
                bool exhausted = _parameters.ResidualTerrainResolveIterations >= 4
                    && iteration == _parameters.ResidualTerrainResolveIterations - 1;
                if (twoCycle || exhausted)
                {
                    // Jolt 的相邻三角形/Box 接缝可能让同一段在两个 MTD 法线间来回推。
                    // LastPos 是本 tick 积分前、上一轮残余解算已经背书的世界位置；若它
                    // 仍可行，只回滚这一段即可，不能因为一次接缝歧义就清掉整条腿的落点。
                    Vector3 previousPosition = segment.LastPos;
                    if (!ctx.Terrain.SpherePenetration(
                            previousPosition,
                            segment.Radius,
                            out _,
                            out _))
                    {
                        bool retainedGrip = segment.ActiveGrip
                            && segment.GripNormal.LengthSquared() > 1e-10f;
                        Vector3 retainedNormal = retainedGrip
                            ? segment.GripNormal.Normalized()
                            : segment.PreviousContactNormal.LengthSquared() > 1e-10f
                                ? segment.PreviousContactNormal.Normalized()
                                : Vector3.Zero;
                        ulong retainedCollider = segment.GripColliderId;
                        Vector3 correction = previousPosition - segment.Pos;
                        segment.Pos = previousPosition;
                        segment.Vel += correction;
                        segment.TerrainContact = retainedNormal.LengthSquared() > 1e-10f;
                        segment.ContactNormal = retainedNormal;
                        segment.ActiveGrip = retainedGrip;
                        segment.GripNormal = retainedGrip ? retainedNormal : Vector3.Zero;
                        segment.GripColliderId = retainedGrip ? retainedCollider : 0UL;
                        ResetContactManifold(i);
                        if (retainedNormal.LengthSquared() > 1e-10f)
                            RegisterContactNormal(i, retainedNormal);
                        ResidualRecoverySerial++;
                        break;
                    }

                    // 上一帧也已不可行才使用最后兜底：收回锚点并让整条腿重新寻点。
                    segment.Pos = Anchor.Pos;
                    segment.LastPos = Anchor.Pos;
                    segment.Vel = Anchor.Vel;
                    segment.TerrainContact = false;
                    segment.ContactNormal = Vector3.Zero;
                    segment.ActiveGrip = false;
                    segment.GripNormal = Vector3.Zero;
                    segment.GripColliderId = 0UL;
                    ResetContactManifold(i);
                    ResidualInvalidationSerial++;
                    ClearLanding(incrementStep: false);
                    ReplantPhase = DaddyTentacleReplantPhase.Reaching;
                    _forceLandingSearch = true;
                    break;
                }
                segment.Pos += pushDirection * depth;
                SphereTerrain.RespondVelocity(
                    pushDirection, _parameters.SegmentCollisionFriction, ref segment.Vel);
                RegisterContactNormal(i, pushDirection);
                previousPush = pushDirection;
            }
        }
    }

    private bool ResolveCollision(
        int segmentIndex,
        DaddyTentacleSegmentState segment,
        in TerrainHit hit,
        bool observeGuideBarrier)
    {
        Vector3 pos = segment.Pos;
        Vector3 vel = segment.Vel;
        if (SphereTerrain.Resolve(
                hit,
                segment.Radius,
                _parameters.SegmentCollisionFriction,
                ref pos,
                ref vel,
                out Vector3 normal))
        {
            segment.Pos = pos;
            segment.Vel = vel;
            RegisterContactNormal(segmentIndex, normal);
            if (observeGuideBarrier)
                ObserveGuideBarrier(segmentIndex, hit.Point, normal);
            return true;
        }
        return false;
    }

    private bool TryGetGripProbeNormal(
        int segmentIndex,
        DaddyTentacleSegmentState segment,
        out Vector3 probeNormal)
    {
        probeNormal = Vector3.Zero;
        if (BacktrackFrom >= 0 && segmentIndex >= BacktrackFrom)
            return false;
        if (!MaintainsTerrainTask || !_parameters.EnableSegmentAdhesion)
            return false;

        if (!_parameters.EnableGripDiscrimination)
        {
            if (HasLandingTarget
                && segment.Pos.DistanceSquaredTo(LandingPoint)
                    <= _parameters.ContactTargetRange * _parameters.ContactTargetRange)
            {
                probeNormal = LandingNormal;
            }
            else
            {
                probeNormal = segment.PreviousContactNormal;
            }
            return probeNormal.LengthSquared() > 1e-10f;
        }

        if (ReplantPhase == DaddyTentacleReplantPhase.Peeling)
        {
            if (segmentIndex >= _peelReleasedFrom || !segment.PreviousActiveGrip)
                return false;
            probeNormal = segment.PreviousGripNormal;
            return probeNormal.LengthSquared() > 1e-10f;
        }

        if (!HasLandingTarget
            || segment.Pos.DistanceSquaredTo(LandingPoint)
                >= _parameters.ContactTargetRange * _parameters.ContactTargetRange)
        {
            return false;
        }

        probeNormal = segment.PreviousActiveGrip
            && segment.PreviousGripNormal.LengthSquared() > 1e-10f
                ? segment.PreviousGripNormal
                : segment.PreviousContactNormal.LengthSquared() > 1e-10f
                    ? segment.PreviousContactNormal
                    : LandingNormal;
        return probeNormal.LengthSquared() > 1e-10f;
    }

    private void MaintainSurfaceGrip(
        int segmentIndex,
        DaddyTentacleSegmentState segment,
        in TerrainHit hit)
    {
        if (!_parameters.EnableSegmentAdhesion || hit.Normal.LengthSquared() <= 1e-10f)
            return;
        Vector3 normal = hit.Normal.Normalized();
        float separation = (segment.Pos - hit.Point).Dot(normal);
        float maximum = segment.Radius + _parameters.ContactProbeExtra + 0.002f;
        if (separation < -segment.Radius || separation > maximum)
            return;
        float desired = segment.Radius + _parameters.LandingSurfaceOffset;
        segment.Vel += normal * ((desired - separation) * 0.10f);
        SphereTerrain.RespondVelocity(
            normal, _parameters.SegmentSurfaceFriction, ref segment.Vel);
        RegisterContactNormal(segmentIndex, normal);
        segment.ActiveGrip = true;
        segment.GripNormal = MergeNormals(segment.GripNormal, normal);
        segment.GripColliderId = hit.ColliderId;
    }

    private void UpdateSupport()
    {
        if (Task == DaddyTentacleTask.ExternalReach || StunTicks > 0)
        {
            ClearSupport();
            return;
        }
        int grips = 0;
        Vector3 normalSum = Vector3.Zero;
        bool arrived = false;
        Vector3 landingCenter = LandingPoint
            + LandingNormal * (_parameters.SegmentRadius + _parameters.LandingSurfaceOffset);
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            bool distalBlocked = BacktrackFrom >= 0 && i >= BacktrackFrom;
            bool active = !distalBlocked && (_parameters.EnableGripDiscrimination
                ? segment.ActiveGrip
                : segment.TerrainContact);
            Vector3 activeNormal = _parameters.EnableGripDiscrimination
                ? segment.GripNormal
                : segment.ContactNormal;
            if (HasLandingTarget
                && ReplantPhase != DaddyTentacleReplantPhase.Peeling
                && _landingAge >= _parameters.StepArrivalMinimumTicks
                && active
                && segment.Pos.DistanceTo(landingCenter) <= _parameters.GrabArrivalRadius)
            {
                // 当前 DLL 逐段检查 tChunks[j]，不是只检查尖端；3D 版保留这条语义。
                arrived = true;
            }
            if (active)
            {
                grips++;
                normalSum += activeNormal;
            }
        }
        ActiveGripCount = grips;
        GripFraction = (float)grips / _segments.Length;
        AtGrabDestination = arrived;
        float support = Mathf.Pow(GripFraction, _parameters.GripFractionExponent);
        if (AtGrabDestination)
            support = Mathf.Lerp(support, 1f, _parameters.ArrivedSupportWeight);
        SupportContribution = _parameters.EnableSupport ? support : 0f;
        SupportNormal = normalSum.LengthSquared() > 1e-10f
            ? normalSum.Normalized()
            : LandingNormal.LengthSquared() > 1e-10f ? LandingNormal : Vector3.Up;
    }

    private void UpdateExternalEffect(Vector3 bodyCenter)
    {
        // tick-end 短链边一旦确认被地形隔断，本 tick 就不能再通过墙体报告 Held 或拉力；
        // Released 仍走滞回门和 pending 单 tick事件，避免单帧接缝误报直接取消宿主任務。
        if (BacktrackFrom >= 0 || TerrainRecoveryActive
            || Task != DaddyTentacleTask.ExternalReach
            || _externalTarget is not { } target)
            return;
        DaddyTentacleSegmentState tip = _segments[^1];
        float reach = target.Radius + _parameters.ExternalReachRadius + tip.Radius;
        bool reached = tip.Pos.DistanceSquaredTo(target.Position) <= reach * reach;
        Vector3 correction = Vector3.Zero;
        Vector3 velocityDelta = Vector3.Zero;
        if (reached && target.PullTowardBody && _parameters.EnableExternalPull)
        {
            Vector3 delta = bodyCenter - target.Position;
            float distance = delta.Length();
            if (distance > 1e-6f)
            {
                Vector3 direction = delta / distance;
                float massScale = 1f / Math.Max(0.25f, target.Mass);
                velocityDelta = direction
                    * Math.Min(_parameters.ExternalPullVelocityCap,
                        distance * _parameters.ExternalPullGain * massScale);
                float excess = Math.Max(0f, distance - Length * 0.45f);
                correction = direction
                    * (excess * _parameters.ExternalPositionCorrectionGain * massScale);
            }
        }
        TargetEffect = new DaddyLongLegsTargetEffect(
            Index,
            target.StableId,
            reached,
            reached,
            false,
            correction,
            velocityDelta);
    }

    private void ExpandInitialChain(in TickContext ctx, Vector3 worldPreference)
    {
        _needsTerrainExpansion = false;
        Vector3 direction = NormalizeOr(worldPreference, LocalPreference);
        float extension = Length;
        if (ctx.Terrain.Raycast(
                Anchor.Pos,
                Anchor.Pos + direction * Length,
                out TerrainHit hit)
            && hit.Normal.LengthSquared() > 1e-10f)
        {
            extension = Math.Max(
                LinkLength * 0.25f,
                Anchor.Pos.DistanceTo(hit.Point)
                    - _parameters.SegmentRadius
                    - _parameters.LandingSurfaceOffset);
            extension = Math.Min(extension, Length);
            LandingPoint = hit.Point;
            LandingNormal = hit.Normal.Normalized();
            LandingColliderId = hit.ColliderId;
            HasLandingTarget = true;
            LandingSerial++;
            _landingAge = 0;
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            segment.Pos = Anchor.Pos
                + direction * (extension * (i + 1) / _segments.Length);
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
        ReplantPhase = DaddyTentacleReplantPhase.Reaching;
        ReplantPhaseTicks = 0;
    }

    private void AdvanceReplantPhase()
    {
        switch (ReplantPhase)
        {
            case DaddyTentacleReplantPhase.Peeling:
                ReplantPhaseTicks++;
                bool releaseComplete = ReplantPhaseTicks >= _parameters.StepPeelTicks;
                bool physicallyClear = DistalTerrainContactFraction()
                    <= _parameters.StepPeelMaximumContactFraction;
                if ((releaseComplete && physicallyClear)
                    || ReplantPhaseTicks >= _parameters.StepPeelMaximumTicks)
                {
                    ReplantPhase = DaddyTentacleReplantPhase.Reaching;
                    ReplantPhaseTicks = 0;
                    _forceLandingSearch = true;
                }
                break;
            case DaddyTentacleReplantPhase.Reaching:
                ReplantPhaseTicks++;
                if (HasLandingTarget && AtGrabDestination)
                {
                    ReplantPhase = DaddyTentacleReplantPhase.Planted;
                    ReplantPhaseTicks = 0;
                    _peelNormal = LandingNormal;
                    StartReplantActive = false;
                    StartReplantDirection = Vector3.Zero;
                    StepInFlight = false;
                }
                break;
            default:
                ReplantPhaseTicks = 0;
                break;
        }
    }

    private Vector3 ResolvePeelNormal()
    {
        Vector3 sum = Vector3.Zero;
        foreach (DaddyTentacleSegmentState segment in _segments)
        {
            if (segment.ActiveGrip && segment.GripNormal.LengthSquared() > 1e-10f)
                sum += segment.GripNormal;
        }
        if (sum.LengthSquared() > 1e-10f)
            return sum.Normalized();
        if (LandingNormal.LengthSquared() > 1e-10f)
            return LandingNormal.Normalized();
        return SupportNormal.LengthSquared() > 1e-10f
            ? SupportNormal.Normalized()
            : Vector3.Zero;
    }

    private void ResetReplantState()
    {
        ReplantPhase = DaddyTentacleReplantPhase.Reaching;
        ReplantPhaseTicks = 0;
        StepInFlight = false;
        _landingAge = 0;
        _releaseStart = 0;
        _peelReleasedFrom = 0;
        _peelNormal = Vector3.Zero;
        _replantSurfacePoint = Vector3.Zero;
        _replantSurfaceNormal = Vector3.Zero;
        _hasReplantSurface = false;
        _forceLandingSearch = true;
        _idleLandingRetargetCommitted = false;
        StartReplantActive = false;
        StartReplantDirection = Vector3.Zero;
        _guideArchScale = 1f;
        Array.Clear(_releaseOrigins);
        Array.Clear(_releaseNormals);
        ResetGuideDiagnostics();
    }

    private void ResetTerrainBacktrackState()
    {
        BacktrackFrom = -1;
        Array.Clear(_barrierObserved);
        Array.Clear(_barrierTicks);
        Array.Clear(_barrierPoints);
        Array.Clear(_barrierNormals);
        Array.Clear(_guideTargetValid);
        _guideObstructionObserved = false;
        _guideObstructionTicks = 0;
        _topologyBarrierTicks = 0;
        _terrainRecoveryFrom = -1;
        _terrainRecoveryPhase = 0;
        _terrainRecoveryPoint = Vector3.Zero;
        _terrainRecoveryNormal = Vector3.Zero;
        _terrainRecoveryTangentReference = Vector3.Zero;
    }

    private void UpdatePeelReleaseBoundary()
    {
        if (ReplantPhase != DaddyTentacleReplantPhase.Peeling)
            return;
        float progress = Mathf.Clamp(
            (float)(ReplantPhaseTicks + 1) / _parameters.StepPeelTicks,
            0f,
            1f);
        float boundary = Mathf.Lerp(
            1f,
            _parameters.StepPeelStartFraction,
            Mathf.SmoothStep(0f, 1f, progress));
        int releasedFrom = Math.Clamp(
            (int)MathF.Floor(_segments.Length * boundary),
            _releaseStart,
            _segments.Length - 1);
        _peelReleasedFrom = Math.Min(_peelReleasedFrom, releasedFrom);
        for (int i = _peelReleasedFrom; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
    }

    private float DistalTerrainContactFraction()
    {
        int count = _segments.Length - _releaseStart;
        if (count <= 0)
            return 0f;
        int contacts = 0;
        for (int i = _releaseStart; i < _segments.Length; i++)
            if (_segments[i].TerrainContact)
                contacts++;
        return (float)contacts / count;
    }

    private void BuildLandingGuide(Vector3 worldPreference)
    {
        Vector3 normal = NormalizeOr(LandingNormal, Vector3.Up);
        Vector3 landingCenter = LandingPoint
            + normal * (_parameters.SegmentRadius + _parameters.LandingSurfaceOffset);
        if (!_parameters.EnableSlackGuide)
        {
            GuideLength = Length;
            GuideContactLength = Length * 0.45f;
            return;
        }

        Vector3 direct = landingCenter - Anchor.Pos;
        float directDistance = direct.Length();
        Vector3 directDirection = NormalizeOr(direct, worldPreference);
        Vector3 planeDirection = direct - normal * direct.Dot(normal);
        if (planeDirection.LengthSquared() <= 1e-10f)
        {
            planeDirection = worldPreference
                - normal * worldPreference.Dot(normal);
        }
        _guideSurfaceTangent = NormalizeOr(
            planeDirection,
            Orthogonal(normal));

        float capacity = Length * _parameters.GuideTargetLengthRatio;
        float slack = Math.Max(0f, Length - directDistance);
        float contact = Math.Min(
            Math.Min(_parameters.ContactTargetRange,
                Length * _parameters.GuideMaximumContactRatio),
            slack * _parameters.GuideContactSlackShare);
        // 段目标按 GuideLength 等弧长分布，所以“贴面材料占比”的分母是最终引导
        // 曲线，而不是出生总长。由三角不等式 GuideLength >= directDistance；先以
        // directDistance×上限钳制即可保证后续 contact/GuideLength 不越界，同时
        // 不必做依赖浮点收敛条件的循环求根。
        contact = Math.Min(
            contact,
            directDistance * _parameters.GuideMaximumContactRatio);
        if (directDistance >= capacity)
        {
            contact = 0f;
            landingCenter = Anchor.Pos + directDirection * capacity;
        }
        else
        {
            float low = 0f;
            float high = contact;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                float candidate = (low + high) * 0.5f;
                Vector3 start = landingCenter - _guideSurfaceTangent * candidate;
                float path = Anchor.Pos.DistanceTo(start) + candidate;
                if (path <= capacity)
                    low = candidate;
                else
                    high = candidate;
            }
            contact = low;
        }

        _guideSurfaceStart = landingCenter - _guideSurfaceTangent * contact;
        Vector3 bridge = _guideSurfaceStart - Anchor.Pos;
        float bridgeDistance = bridge.Length();
        Vector3 bridgeDirection = NormalizeOr(bridge, directDirection);
        Vector3 lateralPreference = worldPreference
            - bridgeDirection * worldPreference.Dot(bridgeDirection);
        Vector3 startDirection = NormalizeOr(
            bridgeDirection + lateralPreference * 0.35f,
            bridgeDirection);
        Vector3 endDirection = contact > 1e-4f
            ? NormalizeOr(
                _guideSurfaceTangent
                    - normal * _parameters.GuideSurfaceApproachNormalWeight,
                _guideSurfaceTangent)
            : bridgeDirection;
        float handle = Math.Max(
            bridgeDistance * _parameters.GuideTangentHandleRatio,
            Length * 0.04f);
        float scale = 1f;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            BuildHermiteGuide(
                Anchor.Pos,
                _guideSurfaceStart,
                startDirection * (handle * scale),
                endDirection * (handle * scale),
                Vector3.Zero,
                0f);
            if (_guideBridgeLength + contact <= capacity + 1e-4f)
                break;
            scale *= 0.5f;
        }
        if (_guideBridgeLength + contact > capacity + 1e-4f)
        {
            for (int i = 0; i <= GuideSampleCount; i++)
                _guideSamples[i] = Anchor.Pos.Lerp(
                    _guideSurfaceStart,
                    (float)i / GuideSampleCount);
            ComputeGuideArcLengths();
        }

        // 贴面长度只消耗一小部分余长；其余材料不是继续铺成直线“地毯”，而是沿
        // 表面外法线鼓成确定性弧。桥段以浅角度入面，避免水平切线在地面附近拖出
        // 额外接触段；bump 在 t=0/1 的一阶导为 0，不改变桥内两端切线。
        // 不能把所有余长都强制塞进空中拱弧：那虽然能消掉地面的“腿毯”，却会让
        // 身体在低位就耗尽整条触手的材料长度，从而失去站高的几何动机。这里只用
        // 固定比例的可用余长塑形，其余长度交给支撑/推进自然拉开。
        float maximumBridgeLength = Math.Max(_guideBridgeLength, capacity - contact);
        float targetBridgeLength = _guideBridgeLength
            + (maximumBridgeLength - _guideBridgeLength)
                * (_parameters.GuideSlackArchShare * _guideArchScale);
        Vector3 bendDirection = normal - bridgeDirection * normal.Dot(bridgeDirection);
        if (bendDirection.LengthSquared() <= 1e-10f)
        {
            bendDirection = worldPreference
                - bridgeDirection * worldPreference.Dot(bridgeDirection);
        }
        bendDirection = NormalizeOr(bendDirection, Orthogonal(bridgeDirection));
        float lowBend = 0f;
        float highBend = Length * _parameters.GuideBendMaximumRatio * _guideArchScale;
        BuildHermiteGuide(
            Anchor.Pos,
            _guideSurfaceStart,
            startDirection * (handle * scale),
            endDirection * (handle * scale),
            bendDirection,
            highBend);
        if (_guideBridgeLength >= targetBridgeLength)
        {
            for (int iteration = 0; iteration < 12; iteration++)
            {
                float candidate = (lowBend + highBend) * 0.5f;
                BuildHermiteGuide(
                    Anchor.Pos,
                    _guideSurfaceStart,
                    startDirection * (handle * scale),
                    endDirection * (handle * scale),
                    bendDirection,
                    candidate);
                if (_guideBridgeLength < targetBridgeLength)
                    lowBend = candidate;
                else
                    highBend = candidate;
            }
            BuildHermiteGuide(
                Anchor.Pos,
                _guideSurfaceStart,
                startDirection * (handle * scale),
                endDirection * (handle * scale),
                bendDirection,
                lowBend);
        }
        GuideContactLength = contact;
        GuideLength = _guideBridgeLength + contact;
    }

    private void BuildHermiteGuide(
        Vector3 from,
        Vector3 to,
        Vector3 fromTangent,
        Vector3 toTangent,
        Vector3 bendDirection,
        float bendAmplitude)
    {
        for (int i = 0; i <= GuideSampleCount; i++)
        {
            float t = (float)i / GuideSampleCount;
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            float oneMinusT = 1f - t;
            // 三阶端点消隐：位置、一阶切线和二阶曲率都不会在锚点/贴面入口
            // 突然跳变，避免旧 L-guide 的直角感换成另一种尖肩。
            float bump = 64f * t3
                * oneMinusT * oneMinusT * oneMinusT;
            _guideSamples[i] = from * h00
                + fromTangent * h10
                + to * h01
                + toTangent * h11
                + bendDirection * (bendAmplitude * bump);
        }
        ComputeGuideArcLengths();
    }

    private void ComputeGuideArcLengths()
    {
        _guideCumulative[0] = 0f;
        for (int i = 1; i <= GuideSampleCount; i++)
        {
            _guideCumulative[i] = _guideCumulative[i - 1]
                + _guideSamples[i - 1].DistanceTo(_guideSamples[i]);
        }
        _guideBridgeLength = _guideCumulative[GuideSampleCount];
    }

    private Vector3 EvaluateLandingGuide(float u)
    {
        if (!_parameters.EnableSlackGuide)
        {
            Vector3 landingCenter = LandingPoint + LandingNormal
                * (_parameters.SegmentRadius + _parameters.LandingSurfaceOffset);
            float signed = (Anchor.Pos - LandingPoint).Dot(LandingNormal);
            Vector3 planeAnchor = Anchor.Pos - LandingNormal
                * (signed - _parameters.SegmentRadius - _parameters.LandingSurfaceOffset);
            float bridgeFraction = Mathf.Clamp(
                Math.Abs(signed) / Math.Max(Length, 1e-5f), 0.08f, 0.55f);
            return u <= bridgeFraction
                ? Anchor.Pos.Lerp(planeAnchor, u / bridgeFraction)
                : planeAnchor.Lerp(
                    landingCenter,
                    (u - bridgeFraction) / (1f - bridgeFraction));
        }

        float distance = GuideLength * Mathf.Clamp(u, 0f, 1f);
        if (distance >= _guideBridgeLength && GuideContactLength > 1e-6f)
        {
            return _guideSurfaceStart + _guideSurfaceTangent
                * Math.Min(GuideContactLength, distance - _guideBridgeLength);
        }
        if (_guideBridgeLength <= 1e-6f)
            return _guideSurfaceStart;
        int upper = 1;
        while (upper <= GuideSampleCount && _guideCumulative[upper] < distance)
            upper++;
        upper = Math.Min(upper, GuideSampleCount);
        int lower = Math.Max(0, upper - 1);
        float span = _guideCumulative[upper] - _guideCumulative[lower];
        float amount = span > 1e-6f
            ? (distance - _guideCumulative[lower]) / span
            : 0f;
        return _guideSamples[lower].Lerp(_guideSamples[upper], amount);
    }

    private void ResetGuideDiagnostics()
    {
        GuideLength = 0f;
        GuideContactLength = 0f;
        _guideBridgeLength = 0f;
        _guideSurfaceStart = Anchor.Pos;
        _guideSurfaceTangent = Vector3.Zero;
    }

    private void ClearLanding(bool incrementStep)
    {
        if (incrementStep)
            StepSerial++;
        HasLandingTarget = false;
        LandingPoint = Anchor.Pos;
        LandingNormal = Vector3.Zero;
        LandingColliderId = 0UL;
        AtGrabDestination = false;
        _landingValidationAge = 0;
        _idleLandingValidationFailures = 0;
        _landingAge = 0;
    }

    private void ClearSupport()
    {
        ActiveGripCount = 0;
        GripFraction = 0f;
        SupportContribution = 0f;
        AtGrabDestination = false;
        SupportNormal = Vector3.Up;
    }

    private void ClearTerrainContactMemory()
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            DaddyTentacleSegmentState segment = _segments[i];
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
            ResetContactManifold(i);
        }
    }

    private static void ApplyServo(
        DaddyTentacleSegmentState segment,
        Vector3 target,
        float strength)
    {
        Vector3 delta = target - segment.Pos;
        float distance = delta.Length();
        if (distance > 1e-6f)
            segment.Vel += delta / distance * Math.Min(strength, distance * strength);
    }

    private static void ApplyCappedServo(
        DaddyTentacleSegmentState segment,
        Vector3 target,
        float speed)
    {
        Vector3 delta = target - segment.Pos;
        float distance = delta.Length();
        if (distance > 1e-6f && speed > 0f)
            segment.Vel += delta / distance * Math.Min(speed, distance);
    }

    private static Vector3 SphericalBlend(Vector3 from, Vector3 to, float amount)
    {
        Vector3 a = NormalizeOr(from, Vector3.Right);
        Vector3 b = NormalizeOr(to, a);
        float dot = Mathf.Clamp(a.Dot(b), -1f, 1f);
        if (dot < -0.999f)
        {
            Vector3 orthogonal = Orthogonal(a);
            float angle = Mathf.Pi * amount;
            return NormalizeOr(a * Mathf.Cos(angle) + orthogonal * Mathf.Sin(angle), a);
        }
        return NormalizeOr(a.Lerp(b, amount), a);
    }

    private static Vector3 Orthogonal(Vector3 direction)
    {
        Vector3 axis = Math.Abs(direction.X) < 0.7f ? Vector3.Right : Vector3.Up;
        return NormalizeOr(direction.Cross(axis), Vector3.Back);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f
            ? value.Normalized()
            : fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;
}
