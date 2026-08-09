using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>
/// 无头尾、完整图身体 + 多条整链贴面触手的独立 3D 控制器。它只复用 Body/terrain/host
/// 原语；方向来自宿主移动意图和每条触手的材料偏好，不存在身体 forward 或转身状态机。
/// </summary>
public sealed class DaddyLongLegsLocomotionController
{
    public DaddyLongLegsParams Params { get; }
    public DaddyLongLegsMorphology Morphology { get; }
    public Body Body { get; }
    public IReadOnlyList<DaddyTentacle> Tentacles => _tentacles;
    public IReadOnlyList<DaddyLongLegsTargetEffect> TargetEffects => _targetEffects;

    public Vector3 MoveDir;
    public float RunSpeed = 1f;
    public Vector3? MoveTarget;
    public float MoveTargetArriveRadius;
    public bool AtMoveTarget { get; private set; }
    public bool HasMoveIntent => RunSpeed > 1e-5f
        && (MoveTarget is not null ? !AtMoveTarget : MoveDir.LengthSquared() > 1e-10f);
    public Vector3 LastMoveTarget { get; private set; }
    public MoveTargetKind LastMoveTargetKind { get; private set; }

    public Vector3 BodyCenter { get; private set; }
    public Vector3 MaterialAxisX { get; private set; } = Vector3.Right;
    public Vector3 MaterialAxisY { get; private set; } = Vector3.Up;
    public Vector3 MaterialAxisZ { get; private set; } = Vector3.Back;
    public Vector3 SupportNormal { get; private set; } = Vector3.Up;
    public float RawSupport { get; private set; }
    public float EffectiveSupport { get; private set; }
    public float UnconditionalSupport => _unconditionalSupport;
    public float ContinuousSupport => _continuousSupport;
    public float GravityCancellation { get; private set; }
    public float DirectionalSupport { get; private set; }
    public float DriveScale { get; private set; }
    public int LocomotionTentacleCount => CountLocomotionTentacles();
    public int ArrivedTentacleCount => CountArrivedTentacles();
    public int StuckCounter { get; private set; }
    public float StuckAmount { get; private set; }
    public bool StuckDetourActive { get; private set; }
    public Vector3 StuckDetourDirection { get; private set; }
    public int StuckEpisodeSerial { get; private set; }
    public int DutyAssignmentSerial { get; private set; }
    public int DutyReleaseSerial { get; private set; }
    public int StepReleaseSerial { get; private set; }
    /// <summary>
    /// 仅统计真正动用了 stuck 强制换步豁免（绕过到达数门或 1g 支撑余量门）的释放。
    /// 纯派生诊断量：只读取本 tick 已折叠的状态，不回写任何行为，因此有意不进
    /// FoldDeterministicState——既有哈希基线保持逐位不变。
    /// </summary>
    public int StuckForcedStepReleaseSerial { get; private set; }
    public int MovementEpisodeSerial { get; private set; }
    public int StartReplantSerial { get; private set; }
    /// <summary>仅因 1.00g 余量门连续饥饿后按 0.90 预算放行的换步累计次数。</summary>
    public int ReserveStarvationReleaseSerial { get; private set; }
    public bool MoveEpisodeActive => _moveEpisodeActive;
    public int MoveEpisodeGraceTicksRemaining => _moveEpisodeGraceTicks;
    public Vector3 MoveEpisodeDirection => _moveEpisodeDirection;
    public bool StartReplantPending => _startReplantPending;
    public int ActiveStartReplantTentacleIndex => _startReplantIndex;
    /// <summary>当前处于显式换步在途（已释放、尚未重新 Planted+到达）的触手数。</summary>
    public int InFlightStepCount { get; private set; }
    /// <summary>
    /// 换步配额的参与池：Locomotion 任务、未眩晕/未在地形恢复，且落点可得
    /// （有落点、在途，或搜索失败尚未到扩张上限）。永久够不到地形的触手不占配额。
    /// </summary>
    public int StepPoolCount { get; private set; }
    /// <summary>≙ 原作 legsGrabbing &gt; N/2 的换步配额，分母取参与池。</summary>
    public int RequiredArrivedTentaclesForStep { get; private set; }
    public int ReserveStarvationTicks => _reserveStarvationTicks;
    public int LastStartReplantTentacleIndex { get; private set; } = -1;
    public float LastStartReplantReleaseDot { get; private set; } = 1f;
    public float LastStepReleasePredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    public float MinimumStepReleasePredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    public float LastStartReplantPredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    public float MinimumStartReplantPredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    /// <summary>饥饿阀释放的预测抗重力单独跟踪；普通换步的 1.00 门指标不被它污染。</summary>
    public float LastReserveStarvationPredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    public float MinimumReserveStarvationPredictedGravityCancellation { get; private set; }
        = float.PositiveInfinity;
    public int TickQueryCount { get; private set; }
    public int PeakQueryCount { get; private set; }
    public bool QueryBudgetExceeded { get; private set; }

    private readonly DaddyTentacle[] _tentacles;
    private readonly DaddyLongLegsTargetEffect[] _targetEffects;
    private readonly DaddyLongLegsParams _parameters;
    private readonly CountingTerrainQuery _countingTerrain = new();
    private readonly Vector3[] _positionHistory;
    private int _historyHead;
    private int _historyCount;
    private int _dutyCooldown;
    private int _stepCooldown;
    private int _moveStartAssistTicks;
    private bool _hadMoveIntent;
    private Vector3 _lastMoveIntent;
    private bool _moveEpisodeActive;
    private int _moveEpisodeGraceTicks;
    private Vector3 _moveEpisodeDirection;
    private bool _startReplantPending;
    private int _startReplantIndex = -1;
    private int _reserveStarvationTicks;
    private float _unconditionalSupport = 1f;
    private float _continuousSupport = 1f;
    private bool _frameInitialized;
    private Vector3 _stuckDetourForward;
    private Vector3 _stuckDetourSurfaceNormal;
    private Vector3 _stuckPairDirection;
    private Vector3 _stuckPairSurfaceNormal;
    private int _stuckDetourTicks;
    private int _stuckClearanceMisses;
    private bool _stuckDetourArmed = true;

    internal DaddyLongLegsLocomotionController(
        DaddyLongLegsParams parameters,
        DaddyLongLegsMorphology morphology,
        Body body,
        DaddyTentacle[] tentacles)
    {
        _parameters = parameters.Snapshot();
        Params = parameters.Snapshot();
        Morphology = morphology;
        Body = body;
        _tentacles = tentacles;
        _targetEffects = new DaddyLongLegsTargetEffect[tentacles.Length];
        _positionHistory = new Vector3[_parameters.StuckHistoryTicks];
        MoveTargetArriveRadius = _parameters.MoveTargetArriveRadius;
        BodyCenter = ComputeBodyCenter();
        LastMoveTarget = BodyCenter;
        UpdateMaterialFrame();

        int minimum = Math.Min(_parameters.MinimumLocomotionTentacles, _tentacles.Length);
        for (int i = 0; i < minimum; i++)
        {
            int index = Math.Min(_tentacles.Length - 1,
                (int)((long)i * _tentacles.Length / minimum));
            _tentacles[index].SetLocomotion();
        }
    }

    /// <summary>
    /// 固定 tick 顺序：全重力 Body→形态材料 frame→触手→支撑聚合→同 tick 连续抗重力→
    /// 无力偶全团推进→职责/换步→发布下一次 Body.Tick 使用的阻尼。所有地形查询走带硬预算的同一接缝。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        ValidateInputs(ctx);
        _countingTerrain.Reset(ctx.Terrain, _parameters.MaximumTerrainQueriesPerTick);
        var countedContext = new TickContext(
            ctx.GravityPerTick,
            _countingTerrain,
            ctx.TickIndex);

        Body.Tick(countedContext);
        _unconditionalSupport = Math.Max(
            0f,
            _unconditionalSupport - _parameters.UnconditionalSupportDecayPerTick);
        ResolveResidualBodyTerrain(countedContext);
        BodyCenter = ComputeBodyCenter();
        UpdateMaterialFrame();
        Vector3 rawMove = ResolveMoveIntent();
        Vector3 planningMove = UpdateMovementEpisode(rawMove);
        UpdateMoveStartAssist(rawMove);
        UpdateStuckState(BodyCenter, planningMove);
        Vector3 locomotionMove = UpdateStuckDetour(countedContext, planningMove);
        float searchRecovery = StuckDetourActive ? 1f : _parameters.EnableStuckRecovery
            ? Mathf.Clamp((StuckCounter - _parameters.StuckRiseTicks * 0.5f)
                / (_parameters.StuckRiseTicks * 1.5f), 0f, 1f)
            : 0f;
        float jitterRecovery = _parameters.EnableStuckRecovery
            ? Mathf.Clamp((float)(StuckCounter - _parameters.StuckRiseTicks)
                / _parameters.StuckRiseTicks, 0f, 1f)
            : 0f;

        for (int i = 0; i < _tentacles.Length; i++)
        {
            DaddyTentacle tentacle = _tentacles[i];
            Vector3 preference = TransformPreference(tentacle.LocalPreference);
            tentacle.Tick(
                countedContext,
                BodyCenter,
                preference,
                locomotionMove,
                searchRecovery,
                _moveEpisodeActive,
                MaterialAxisX,
                MaterialAxisY);
            _targetEffects[i] = tentacle.TargetEffect;
        }

        AggregateSupport(locomotionMove);
        ApplyContinuousSupportToBody(ctx.GravityPerTick);
        ApplyWholeBodyDrive(locomotionMove);
        ApplyDeterministicStuckJitter(ctx.TickIndex, jitterRecovery, locomotionMove);
        UpdateDutyAllocator(ctx.TickIndex);
        UpdateStepRelease(locomotionMove);

        TickQueryCount = _countingTerrain.Count;
        PeakQueryCount = Math.Max(PeakQueryCount, TickQueryCount);
        QueryBudgetExceeded |= _countingTerrain.Exhausted;
    }

    /// <summary>
    /// Body 的通用固定序会在碰撞之后恢复 TerrainCoupled 连接；完全图在角落中可能因此把
    /// 某个球重新拉入 collider。Daddy 在物种边界内做最后的有界 MTD，地形可行性优先于
    /// 留到下一 tick 继续松弛的微小完整图误差，不改变共享 Body 的既有顺序或基线。
    /// </summary>
    private void ResolveResidualBodyTerrain(in TickContext ctx)
    {
        if (!_parameters.EnableResidualTerrainResolve)
            return;
        for (int i = 0; i < Body.Chunks.Count; i++)
        {
            BodyChunk chunk = Body.Chunks[i];
            Vector3 previousPush = Vector3.Zero;
            bool restoredPreviousPosition = false;
            for (int iteration = 0;
                 iteration < _parameters.ResidualTerrainResolveIterations;
                 iteration++)
            {
                if (!ctx.Terrain.SpherePenetration(
                        chunk.Pos,
                        chunk.TerrainRadius,
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
                if (twoCycle && !restoredPreviousPosition)
                {
                    Vector3 correction = chunk.LastPos - chunk.Pos;
                    if (correction.LengthSquared() > 1e-12f)
                    {
                        chunk.Pos = chunk.LastPos;
                        chunk.Vel += correction;
                        restoredPreviousPosition = true;
                        previousPush = Vector3.Zero;
                        continue;
                    }
                }
                chunk.Pos += pushDirection * depth;
                SphereTerrain.RespondVelocity(
                    pushDirection, Body.SurfaceFriction, ref chunk.Vel);
                chunk.TerrainContact = true;
                chunk.ContactNormal = pushDirection;
                previousPush = pushDirection;
            }
        }
    }

    /// <summary>找到当前真正空闲、未眩晕且未被运动预算征用的第一条触手；无则返回 -1。</summary>
    public int FindIdleTentacle()
    {
        for (int i = 0; i < _tentacles.Length; i++)
            if (_tentacles[i].CanAcceptExternalTarget)
                return i;
        return -1;
    }

    /// <summary>
    /// 只允许空闲触手接单；已在够同一 StableId 的触手可用新快照逐 tick 更新目标位置。
    /// ExternalReach 不计运动支撑，也不会被职责分配器抢走。
    /// </summary>
    public bool TryAssignExternalTarget(
        int tentacleIndex,
        in DaddyLongLegsTargetSnapshot target)
    {
        DaddyTentacle tentacle = TentacleAt(tentacleIndex);
        return tentacle.TryAssignExternalTarget(target);
    }

    public void ClearExternalTarget(int tentacleIndex)
    {
        TentacleAt(tentacleIndex).ClearExternalTarget();
    }

    /// <summary>按触手编号打断；该条立即清零支撑、放弃地形/外部任务并软化下垂。</summary>
    public void StunTentacle(int tentacleIndex, int ticks)
    {
        DaddyTentacle tentacle = TentacleAt(tentacleIndex);
        bool interruptedStep = tentacle.StepInFlight;
        bool interruptedStartReplant = tentacleIndex == _startReplantIndex;
        tentacle.Stun(ticks);
        if (!interruptedStep && !interruptedStartReplant)
            return;

        // Stun 是同步宿主输入，而 DaddyTentacle 会在控制器检查在途状态之前递减计时。
        // 只看下一次 UpdateStepRelease 里的当前 StunTicks 会吞掉 1-tick 中断。因此在
        // 输入边界立即提交中断事件（Stun 内部 ResetReplantState 已清 StepInFlight）；
        // 冷却至少保留一个完整 UpdateStepRelease 窗口，同一安全窗不再松第二条腿。
        if (interruptedStartReplant)
        {
            _startReplantIndex = -1;
            if (_moveEpisodeActive && _parameters.EnableStartReplant)
                _startReplantPending = true;
        }
        _stepCooldown = Math.Max(_stepCooldown, 2);
        InFlightStepCount = CountInFlightSteps();
    }

    /// <summary>该触手出生时的完整段数（Morphology 冻结值；断手期间实例段数小于它）。</summary>
    public int FullTentacleSegmentCount(int tentacleIndex)
    {
        _ = TentacleAt(tentacleIndex);
        return Morphology.TentacleAt(tentacleIndex).SegmentCount;
    }

    /// <summary>当前是否处于断手状态（实例段数少于出生 spec，无需额外记账）。</summary>
    public bool IsTentacleSevered(int tentacleIndex) =>
        TentacleAt(tentacleIndex).Segments.Count
            < Morphology.TentacleAt(tentacleIndex).SegmentCount;

    /// <summary>
    /// opt-in 断手：把触手截断成前 keepSegments 段的短链实例重建（LinkLength 不变、
    /// 近端段运动学状态原样保留），返回被移除的远端段状态——已与内核脱钩的孤儿对象，
    /// 供宿主自行模拟断落段。断手期间不可叠断，再调用即抛。注意：不会为旧实例的外部
    /// 目标补发 Released 事件，宿主自行清理其绑定状态。既有回归从不调用本方法，
    /// 因此全部既有确定性基线逐位不变。
    /// </summary>
    public DaddyTentacleSegmentState[] SeverTentacle(int tentacleIndex, int keepSegments)
    {
        DaddyTentacle old = TentacleAt(tentacleIndex);
        if (IsTentacleSevered(tentacleIndex))
            throw new InvalidOperationException(
                $"Tentacle {tentacleIndex} is already severed.");
        int fullCount = old.Segments.Count;
        if (keepSegments < 1 || keepSegments >= fullCount)
            throw new ArgumentOutOfRangeException(nameof(keepSegments), keepSegments,
                $"keepSegments must be in [1, {fullCount - 1}].");

        var removed = new DaddyTentacleSegmentState[fullCount - keepSegments];
        for (int i = 0; i < removed.Length; i++)
            removed[i] = old.Segments[keepSegments + i];
        RebuildTentacle(tentacleIndex, keepSegments, old,
            ReadOnlySpan<Vector3>.Empty, Vector3.Zero);
        return removed;
    }

    /// <summary>
    /// 接回断手：按出生 spec 重建完整实例；近端沿用当前短链状态，远端由宿主提供
    /// 断落段的世界位置（从断口到末梢顺序，数量必须恰好补齐与出生段数的差额）。
    /// </summary>
    public void RestoreTentacle(
        int tentacleIndex,
        ReadOnlySpan<Vector3> distalPositions,
        Vector3 distalVelocityPerTick)
    {
        DaddyTentacle old = TentacleAt(tentacleIndex);
        if (!IsTentacleSevered(tentacleIndex))
            throw new InvalidOperationException(
                $"Tentacle {tentacleIndex} is not severed.");
        int fullCount = Morphology.TentacleAt(tentacleIndex).SegmentCount;
        int missing = fullCount - old.Segments.Count;
        if (distalPositions.Length != missing)
            throw new ArgumentException(
                $"Expected exactly {missing} distal positions, got {distalPositions.Length}.",
                nameof(distalPositions));
        EnsureFinite(distalVelocityPerTick, nameof(distalVelocityPerTick));
        foreach (Vector3 position in distalPositions)
            EnsureFinite(position, nameof(distalPositions));
        RebuildTentacle(tentacleIndex, fullCount, old,
            distalPositions, distalVelocityPerTick);
    }

    /// <summary>
    /// 断/接手共用的实例重建：同 anchor、同 LocalPreference、同 seed，LinkLength 恒为
    /// 出生值；近端从旧实例播种、远端（若有）从宿主坐标播种。求解器零改动——任意段数
    /// 本来就是构造期自由度。控制器侧记账与 StunTentacle 同款：起步/在途腿立即提交
    /// 中断并保留一个完整 UpdateStepRelease 冷却窗。
    /// </summary>
    private void RebuildTentacle(
        int tentacleIndex,
        int segmentCount,
        DaddyTentacle old,
        ReadOnlySpan<Vector3> distalPositions,
        Vector3 distalVelocityPerTick)
    {
        DaddyTentacleSpec fullSpec = Morphology.TentacleAt(tentacleIndex);
        float linkLength = fullSpec.Length / fullSpec.SegmentCount;
        var spec = new DaddyTentacleSpec(
            fullSpec.AnchorBodyIndex,
            linkLength * segmentCount,
            segmentCount,
            fullSpec.LocalPreference);
        var rebuilt = new DaddyTentacle(
            tentacleIndex, old.Anchor, spec, _parameters, Morphology.StableSeed);
        int copied = Math.Min(segmentCount, old.Segments.Count);
        for (int i = 0; i < copied; i++)
        {
            DaddyTentacleSegmentState source = old.Segments[i];
            rebuilt.SeedSegmentForRebuild(i, source.Pos, source.LastPos, source.Vel);
        }
        for (int i = copied; i < segmentCount; i++)
        {
            Vector3 position = distalPositions[i - copied];
            rebuilt.SeedSegmentForRebuild(i, position, position, distalVelocityPerTick);
        }
        _tentacles[tentacleIndex] = rebuilt;
        _targetEffects[tentacleIndex] = default;

        if (tentacleIndex == _startReplantIndex)
        {
            _startReplantIndex = -1;
            if (_moveEpisodeActive && _parameters.EnableStartReplant)
                _startReplantPending = true;
        }
        _stepCooldown = Math.Max(_stepCooldown, 2);
        InFlightStepCount = CountInFlightSteps();
    }

    /// <summary>地形与生物一起 rebase：位置态全部平移，速度、职责、支撑与 seed 相位保留。</summary>
    public void Shift(Vector3 delta)
    {
        EnsureFinite(delta, nameof(delta));
        Body.Shift(delta);
        foreach (DaddyTentacle tentacle in _tentacles)
            tentacle.Shift(delta);
        if (MoveTarget is { } target)
            MoveTarget = target + delta;
        LastMoveTarget += delta;
        BodyCenter += delta;
        for (int i = 0; i < _historyCount; i++)
            _positionHistory[i] += delta;
    }

    /// <summary>
    /// 地形不随生物移动的瞬移：保留同一出生形态，但清除旧抓点、外部够取、MoveTarget、
    /// 支撑和卡住历史；触手在各自材料偏好方向附近重新展开。
    /// </summary>
    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        UpdateMaterialFrame();
        foreach (DaddyTentacle tentacle in _tentacles)
            tentacle.ResetForTeleport(TransformPreference(tentacle.LocalPreference));
        MoveTarget = null;
        AtMoveTarget = false;
        LastMoveTargetKind = MoveTargetKind.None;
        LastMoveTarget = ComputeBodyCenter();
        ResetSupportAndRecovery(restoreUnconditionalSupport: true);
        RestoreMinimumDuty();
    }

    /// <summary>
    /// 给身体球和全部触手段注入同一速度；立即释放地形落点并让重力全开，MoveTarget 与
    /// 外部目标快照保留，之后仍由同一职责/贴附循环自动恢复。
    /// </summary>
    public void Launch(Vector3 velocityPerTick)
    {
        EnsureFinite(velocityPerTick, nameof(velocityPerTick));
        foreach (BodyChunk chunk in Body.Chunks)
            chunk.Vel += velocityPerTick;
        foreach (DaddyTentacle tentacle in _tentacles)
            tentacle.Launch(velocityPerTick);
        AtMoveTarget = false;
        LastMoveTargetKind = MoveTargetKind.None;
        ResetSupportAndRecovery(restoreUnconditionalSupport: false);
    }

    /// <summary>折叠完整 Body、形态 frame、职责/恢复记忆与全部触手状态。</summary>
    public void FoldDeterministicState(DeterminismHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.FoldBody(Body);
        // FoldBody 是既有物种的公共 Pos/Vel 顺序，不能为 Daddy 改共享基线；本物种另折叠
        // 下一 tick sweep 会读取的 LastPos，保证“同哈希状态”具有同一后续碰撞轨迹。
        foreach (BodyChunk chunk in Body.Chunks)
            hasher.Fold(chunk.LastPos);
        hasher.Fold(Morphology.StableSeed);
        hasher.Fold(MoveDir);
        hasher.Fold(RunSpeed);
        hasher.Fold(MoveTarget is not null);
        if (MoveTarget is { } target)
            hasher.Fold(target);
        hasher.Fold(MoveTargetArriveRadius);
        hasher.Fold(AtMoveTarget);
        hasher.Fold(LastMoveTarget);
        hasher.Fold((int)LastMoveTargetKind);
        hasher.Fold(BodyCenter);
        hasher.Fold(MaterialAxisX);
        hasher.Fold(MaterialAxisY);
        hasher.Fold(MaterialAxisZ);
        hasher.Fold(SupportNormal);
        hasher.Fold(RawSupport);
        hasher.Fold(EffectiveSupport);
        hasher.Fold(_unconditionalSupport);
        hasher.Fold(_continuousSupport);
        hasher.Fold(_moveStartAssistTicks);
        hasher.Fold(_hadMoveIntent);
        hasher.Fold(_lastMoveIntent);
        hasher.Fold(_moveEpisodeActive);
        hasher.Fold(_moveEpisodeGraceTicks);
        hasher.Fold(_moveEpisodeDirection);
        hasher.Fold(_startReplantPending);
        hasher.Fold(_startReplantIndex);
        hasher.Fold(_reserveStarvationTicks);
        hasher.Fold(InFlightStepCount);
        hasher.Fold(StepPoolCount);
        hasher.Fold(RequiredArrivedTentaclesForStep);
        hasher.Fold(ReserveStarvationReleaseSerial);
        hasher.Fold(GravityCancellation);
        hasher.Fold(DirectionalSupport);
        hasher.Fold(DriveScale);
        hasher.Fold(LocomotionTentacleCount);
        hasher.Fold(ArrivedTentacleCount);
        hasher.Fold(StuckCounter);
        hasher.Fold(StuckAmount);
        hasher.Fold(StuckDetourActive);
        hasher.Fold(StuckDetourDirection);
        hasher.Fold(StuckEpisodeSerial);
        hasher.Fold(DutyAssignmentSerial);
        hasher.Fold(DutyReleaseSerial);
        hasher.Fold(StepReleaseSerial);
        hasher.Fold(MovementEpisodeSerial);
        hasher.Fold(StartReplantSerial);
        hasher.Fold(LastStartReplantTentacleIndex);
        hasher.Fold(LastStartReplantReleaseDot);
        hasher.Fold(LastStepReleasePredictedGravityCancellation);
        hasher.Fold(MinimumStepReleasePredictedGravityCancellation);
        hasher.Fold(LastStartReplantPredictedGravityCancellation);
        hasher.Fold(MinimumStartReplantPredictedGravityCancellation);
        hasher.Fold(LastReserveStarvationPredictedGravityCancellation);
        hasher.Fold(MinimumReserveStarvationPredictedGravityCancellation);
        hasher.Fold(TickQueryCount);
        hasher.Fold(PeakQueryCount);
        hasher.Fold(QueryBudgetExceeded);
        hasher.Fold(_historyHead);
        hasher.Fold(_historyCount);
        hasher.Fold(_dutyCooldown);
        hasher.Fold(_stepCooldown);
        hasher.Fold(_frameInitialized);
        hasher.Fold(_stuckDetourForward);
        hasher.Fold(_stuckDetourSurfaceNormal);
        hasher.Fold(_stuckPairDirection);
        hasher.Fold(_stuckPairSurfaceNormal);
        hasher.Fold(_stuckDetourTicks);
        hasher.Fold(_stuckClearanceMisses);
        hasher.Fold(_stuckDetourArmed);
        for (int i = 0; i < _positionHistory.Length; i++)
            hasher.Fold(_positionHistory[i]);
        foreach (DaddyTentacle tentacle in _tentacles)
            tentacle.FoldDeterministicState(hasher);
        hasher.Fold(Body.GravityScale);
        hasher.Fold(Body.AirFriction);
        hasher.Fold(Body.SurfaceFriction);
        foreach (BodyChunk chunk in Body.Chunks)
        {
            hasher.Fold(chunk.TerrainContact);
            hasher.Fold(chunk.ContactNormal);
            hasher.Fold(chunk.HadContactLastTick);
        }
        foreach (ChunkConnection connection in Body.Connections)
            hasher.Fold(connection.SnagTicks);
    }

    private Vector3 ResolveMoveIntent()
    {
        if (MoveTarget is { } target)
        {
            Vector3 delta = target - BodyCenter;
            float distance = delta.Length();
            AtMoveTarget = distance <= MoveTargetArriveRadius;
            if (RunSpeed <= 1e-5f || AtMoveTarget || distance <= 1e-6f)
            {
                LastMoveTarget = BodyCenter;
                LastMoveTargetKind = MoveTargetKind.None;
                return Vector3.Zero;
            }
            LastMoveTarget = target;
            LastMoveTargetKind = MoveTargetKind.External;
            return delta / distance;
        }
        AtMoveTarget = false;
        LastMoveTargetKind = MoveTargetKind.None;
        if (RunSpeed <= 1e-5f || MoveDir.LengthSquared() <= 1e-10f)
        {
            LastMoveTarget = BodyCenter;
            return Vector3.Zero;
        }
        Vector3 direction = MoveDir.Normalized();
        LastMoveTarget = BodyCenter + direction;
        LastMoveTargetKind = MoveTargetKind.Fallback;
        return direction;
    }

    private void AggregateSupport(Vector3 effectiveMove)
    {
        float supportSum = 0f;
        float directionalSum = 0f;
        Vector3 normalSum = Vector3.Zero;
        for (int i = 0; i < _tentacles.Length; i++)
        {
            DaddyTentacle tentacle = _tentacles[i];
            supportSum += tentacle.SupportContribution;
            normalSum += tentacle.SupportNormal * tentacle.SupportContribution;
            if (!tentacle.AtGrabDestination || !tentacle.HasLandingTarget)
                continue;
            if (effectiveMove.LengthSquared() <= 1e-10f)
                continue;
            Vector3 side = tentacle.LandingPoint - BodyCenter;
            if (side.LengthSquared() <= 1e-10f)
                continue;
            float dot = side.Normalized().Dot(effectiveMove);
            float directionRecovery = Mathf.Clamp(
                (float)StuckCounter / _parameters.StuckRiseTicks, 0f, 1f);
            float dotFloor = Mathf.Lerp(
                _parameters.DirectionalSupportDotFloor,
                _parameters.StuckDirectionalSupportDotFloor,
                directionRecovery);
            float mapped = Mathf.Clamp(Mathf.InverseLerp(
                dotFloor,
                _parameters.DirectionalSupportDotCeiling,
                dot), 0f, 1f);
            // 当前 DLL 的方向项统计“抓点位于移动侧的已到达触手占比”，单条触手的
            // 贴面比例只进入下面的总支撑耦合，不能在这里重复乘一次。
            directionalSum += Mathf.Pow(mapped, _parameters.DirectionalSupportExponent);
        }

        RawSupport = _parameters.EnableSupport
            ? Mathf.Clamp(supportSum / _tentacles.Length, 0f, 1f)
            : 0f;
        float response = RawSupport > 0f
            ? Mathf.Pow(RawSupport, _parameters.SupportResponseExponent)
            : 0f;
        // DLL 中 num10 先做 support^0.3，再与 PlaceInRoom 的 unconditionalSupport 取 max。
        // 本项目的 3D 整链贴面与显式 Peeling 会让可用接触占比更稀疏，因此参数表有意改为
        // 0.21（保持连续单调且 0/1 端点不变）；偏离理由与消融证据见控制器文档。
        // 这个瞬时值驱动抗重力；低通后的 EffectiveSupport 只作为稳定观测量与职责滞回。
        _continuousSupport = _parameters.EnableSupport
            ? Math.Max(response, _unconditionalSupport)
            : 0f;
        EffectiveSupport = Mathf.Lerp(
            EffectiveSupport, _continuousSupport, _parameters.SupportBlend);
        DirectionalSupport = Mathf.Clamp(directionalSum / _tentacles.Length, 0f, 1f);
        SupportNormal = normalSum.LengthSquared() > 1e-10f
            ? normalSum.Normalized()
            : SupportNormal;
        float coupled = DirectionalSupport * RawSupport;
        float couplingRecovery = Mathf.Clamp(
            (float)(StuckCounter - _parameters.StuckRiseTicks)
                / _parameters.StuckRiseTicks, 0f, 1f);
        float couplingExponent = Mathf.Lerp(
            _parameters.DirectionalCouplingExponent,
            _parameters.StuckDirectionalCouplingExponent,
            couplingRecovery);
        float coupledDrive = coupled > 0f
            ? Mathf.Pow(coupled, couplingExponent)
            : 0f;
        float moveStartAssist = 0f;
        if (_parameters.EnableMoveStartAssist && _moveStartAssistTicks > 0)
        {
            float supportGate = Mathf.Clamp(RawSupport / 0.35f, 0f, 1f);
            float time = (float)_moveStartAssistTicks / _parameters.MoveStartAssistTicks;
            moveStartAssist = _parameters.MoveStartDriveFloor
                * supportGate
                * Mathf.SmoothStep(0f, 1f, time);
        }
        DriveScale = _parameters.EnableSupport
            ? Math.Max(coupledDrive, moveStartAssist)
            : 0f;
    }

    private void UpdateMoveStartAssist(Vector3 rawMove)
    {
        if (!_parameters.EnableMoveStartAssist)
        {
            _moveStartAssistTicks = 0;
            _hadMoveIntent = false;
            _lastMoveIntent = Vector3.Zero;
            return;
        }
        bool hasIntent = RunSpeed > 1e-5f && rawMove.LengthSquared() > 1e-10f;
        Vector3 direction = hasIntent ? rawMove.Normalized() : Vector3.Zero;
        bool changedDirection = hasIntent && _hadMoveIntent
            && _lastMoveIntent.LengthSquared() > 1e-10f
            && direction.Dot(_lastMoveIntent) < 0.25f;
        if (hasIntent && (!_hadMoveIntent || changedDirection))
            _moveStartAssistTicks = _parameters.MoveStartAssistTicks;
        else if (_moveStartAssistTicks > 0)
            _moveStartAssistTicks--;
        _hadMoveIntent = hasIntent;
        if (hasIntent)
            _lastMoveIntent = direction;
    }

    private Vector3 UpdateMovementEpisode(Vector3 rawMove)
    {
        bool hasIntent = HasMoveIntent && rawMove.LengthSquared() > 1e-10f;
        if (hasIntent)
        {
            Vector3 direction = rawMove.Normalized();
            bool changedDirection = _moveEpisodeActive
                && _moveEpisodeDirection.LengthSquared() > 1e-10f
                && direction.Dot(_moveEpisodeDirection)
                    < _parameters.MoveEpisodeDirectionResetDot;
            if (!_moveEpisodeActive || changedDirection)
            {
                _moveEpisodeActive = true;
                _startReplantPending = _parameters.EnableStartReplant;
                MovementEpisodeSerial++;
            }
            _moveEpisodeDirection = direction;
            _moveEpisodeGraceTicks = _parameters.MoveEpisodeGraceTicks;
            return direction;
        }

        // 到达宿主直喂目标是硬停止，不把旧方向继续保留给触手规划。
        if (MoveTarget is not null && AtMoveTarget)
        {
            EndMovementEpisode();
            return Vector3.Zero;
        }
        if (_moveEpisodeActive && _moveEpisodeGraceTicks > 0)
        {
            _moveEpisodeGraceTicks--;
            return _moveEpisodeDirection;
        }
        EndMovementEpisode();
        return Vector3.Zero;
    }

    private void EndMovementEpisode()
    {
        bool endingActiveEpisode = _moveEpisodeActive;
        if (_parameters.EnableIdleLandingStability && endingActiveEpisode)
        {
            // 原作 tile 量化和 incumbent bonus 会让静止落点天然锁存。连续 3D 没有
            // 这层网格迟滞，因此 episode 真结束时一次性提交全部当前落点；验证失效
            // 仍可清点重搜。起步腿同时解除旧 MoveDir，但不清 Landing/支撑/phase。
            foreach (DaddyTentacle tentacle in _tentacles)
                tentacle.CommitIdleLanding();
            _startReplantIndex = -1;
        }
        _moveEpisodeActive = false;
        _moveEpisodeGraceTicks = 0;
        _moveEpisodeDirection = Vector3.Zero;
        _startReplantPending = false;
    }

    private void ApplyWholeBodyDrive(Vector3 effectiveMove)
    {
        if (!_parameters.EnableDirectionalDrive
            || effectiveMove.LengthSquared() <= 1e-10f
            || !HasMoveIntent
            || DriveScale <= 0f)
        {
            return;
        }
        float speed = _parameters.MaxMoveSpeed * Mathf.Clamp(RunSpeed, 0f, 1f);
        float maximumImpulse = _parameters.BaseDrive * DriveScale;
        // 原作给所有 body chunk 加同一个推进量。本物种没有前向轴，因此先以质量质心速度
        // 求一次共同增量，再逐球相同应用；完整图内部相对速度不变，也不会凭空制造力偶。
        float along = ComputeBodyVelocityCenter().Dot(effectiveMove);
        float impulse = Mathf.Clamp(speed - along, 0f, maximumImpulse);
        Vector3 delta = effectiveMove * impulse;
        foreach (BodyChunk chunk in Body.Chunks)
            chunk.Vel += delta;
    }

    private void ApplyContinuousSupportToBody(Vector3 gravityPerTick)
    {
        bool idleSupportNeutrality = _parameters.EnableIdleSupportNeutrality
            && MovementEpisodeSerial > 0
            && !_moveEpisodeActive;
        GravityCancellation = _parameters.EnableSupport
            ? _continuousSupport * _parameters.GravityCancellationGain
            : 0f;
        if (!_parameters.EnableSupportOvercompensation)
            GravityCancellation = Math.Min(GravityCancellation, 1f);
        // 当前 DLL 的移动档 num3=1.2，允许满支撑留下 .2g 净抬升；但 safari/direct
        // control 在 !moving 时会把 num3 改为最多 1，并刷新静止支撑。因此本项目同类
        // 宿主输入一旦没有真实移动意图，也只允许中性抵消，不能让球团自行爬出旧锚可及圈。
        // 出生后的无输入建立姿态仍保留本项目既有的高站姿；只有至少一次移动
        // episode 真正结束后才进入 direct-control idle 档。episode 的 grace 内（包括
        // 连续点按的松键帧）仍属于同一次运动，不应反复切换升力档。
        if (idleSupportNeutrality)
        {
            GravityCancellation = Math.Min(
                GravityCancellation,
                _parameters.IdleGravityCancellationMaximum);

            // RW 的支撑阻尼写在 Creature.Update 后；本项目共享 Body 的 AirFriction
            // 位于约束/碰撞前，Jolt 墙面与完整图恢复随后写入的共同速度不会被它衰减。
            // 这里只补偿执行序差：缩放质量质心速度，给每个球同一增量，保持内部形变/
            // 自旋逐位不动。它只在一次 movement episode 真结束后生效。
            Vector3 commonVelocity = ComputeBodyVelocityCenter();
            Vector3 commonVelocityDelta = commonVelocity
                * (_parameters.IdleBodyVelocityRetention - 1f);
            foreach (BodyChunk chunk in Body.Chunks)
                chunk.Vel += commonVelocityDelta;
        }

        // Body 已在本 tick 施加完整重力。原作随后在 Daddy.Act 追加
        // -gravity * support * 1.2；满支撑因此保留 0.2g 的净抬升，而不是把 scale 钳到零。
        Body.GravityScale = 1f;
        Vector3 supportImpulse = -gravityPerTick * GravityCancellation;
        foreach (BodyChunk chunk in Body.Chunks)
            chunk.Vel += supportImpulse;
        Body.AirFriction = Mathf.Lerp(
            _parameters.UnsupportedAirFriction,
            _parameters.SupportedAirFriction,
            _continuousSupport);
        Body.SurfaceFriction = Mathf.Lerp(
            _parameters.UnsupportedSurfaceFriction,
            _parameters.SupportedSurfaceFriction,
            _continuousSupport);
    }

    private void UpdateDutyAllocator(long tickIndex)
    {
        if (_dutyCooldown > 0)
            _dutyCooldown--;
        if (!_parameters.EnableDutyAllocation)
            return;

        if (LocomotionTentacleCount < _parameters.MinimumLocomotionTentacles)
        {
            if (AssignBestIdle(tickIndex))
                _dutyCooldown = _parameters.DutyChangeCooldownTicks;
            return;
        }
        if (_dutyCooldown > 0)
            return;

        float allocatedFraction = (float)LocomotionTentacleCount / _tentacles.Length;
        if (_continuousSupport < 1f - allocatedFraction)
        {
            if (AssignBestIdle(tickIndex))
                _dutyCooldown = _parameters.DutyChangeCooldownTicks;
            return;
        }
        if (_continuousSupport > _parameters.ReleaseSupportThreshold
            && LocomotionTentacleCount > _parameters.MinimumLocomotionTentacles)
        {
            int release = -1;
            float leastContribution = float.PositiveInfinity;
            for (int i = 0; i < _tentacles.Length; i++)
            {
                DaddyTentacle tentacle = _tentacles[i];
                if (!tentacle.NeededForLocomotion)
                    continue;
                if (tentacle.SupportContribution < leastContribution)
                {
                    leastContribution = tentacle.SupportContribution;
                    release = i;
                }
            }
            if (release >= 0)
            {
                _tentacles[release].SetIdle();
                DutyReleaseSerial++;
                _dutyCooldown = _parameters.DutyChangeCooldownTicks;
            }
        }
    }

    private bool AssignBestIdle(long tickIndex)
    {
        int idleCount = 0;
        for (int i = 0; i < _tentacles.Length; i++)
            if (_tentacles[i].CanAcceptExternalTarget)
                idleCount++;
        // 本项目宿主契约要求运动预算始终只征用一部分触手；即使支撑为零，
        // 也保留至少一条真正 Idle 的触手给外部够取，而不是照 DLL 把全部设为 needed。
        if (idleCount <= _parameters.ReservedIdleTentacles)
            return false;

        int selected = -1;
        float bestScore = float.PositiveInfinity;
        int tieStart = (int)((Morphology.StableSeed + unchecked((ulong)tickIndex))
            % (ulong)_tentacles.Length);
        for (int offset = 0; offset < _tentacles.Length; offset++)
        {
            int i = (tieStart + offset) % _tentacles.Length;
            DaddyTentacle tentacle = _tentacles[i];
            if (!tentacle.CanAcceptExternalTarget)
                continue;
            float score = float.IsFinite(tentacle.TerrainDistanceHint)
                ? tentacle.TerrainDistanceHint
                : 1_000_000f + offset;
            if (score < bestScore)
            {
                bestScore = score;
                selected = i;
            }
        }
        if (selected < 0)
            return false;
        _tentacles[selected].SetLocomotion();
        DutyAssignmentSerial++;
        return true;
    }

    private void UpdateStepRelease(Vector3 effectiveMove)
    {
        if (_stepCooldown > 0)
            _stepCooldown--;
        InFlightStepCount = CountInFlightSteps();
        StepPoolCount = CountStepPool();
        RequiredArrivedTentaclesForStep = Math.Max(
            _parameters.MinimumArrivedTentaclesForStep,
            StepPoolCount / 2 + 1);
        if (!_parameters.EnableStepRelease)
            return;

        // 起步腿保持独占在途：抓稳或被打断前不释放普通腿（既有起步契约不变）。
        // 普通换步不再共用串行槽——原作 Act 以 legsGrabbing > N/2 的计数门为唯一
        // 节流，每 tick 都可重定向一条最差到达腿；释放即清到达数，门自行收紧。
        if (_startReplantIndex >= 0)
        {
            DaddyTentacle startLeg = _tentacles[_startReplantIndex];
            bool completed = startLeg.ReplantPhase == DaddyTentacleReplantPhase.Planted
                && startLeg.AtGrabDestination;
            bool interrupted = startLeg.StunTicks > 0
                || startLeg.Task != DaddyTentacleTask.Locomotion
                || startLeg.TerrainRecoveryActive
                || !startLeg.StepInFlight;
            if (!completed && !interrupted)
                return;
            _startReplantIndex = -1;
            if (!completed && _moveEpisodeActive && _parameters.EnableStartReplant)
                _startReplantPending = true;
            // 完成或被打断的同一 tick 不再释放第二条腿。
            return;
        }
        if (_stepCooldown > 0)
            return;
        if (!HasMoveIntent)
        {
            // 点按间隙只冻结饥饿记忆；episode 真结束才复位。
            if (!_moveEpisodeActive)
                _reserveStarvationTicks = 0;
            return;
        }
        // stuck 强制换步保持单腿：只有零在途时才动用豁免，绕过到达数/余量门。
        bool forcedByStuck = _parameters.EnableStuckRecovery
            && StuckCounter > _parameters.StuckRiseTicks
            && InFlightStepCount == 0;
        if (_parameters.EnableStartReplant && _startReplantPending && !forcedByStuck)
        {
            if (DirectionalSupport >= _parameters.StartReplantDirectionalSupportThreshold)
            {
                _startReplantPending = false;
            }
            else
            {
                TryBeginStartReplant(effectiveMove);
                // 没有安全候选时保留 pending；不能退回普通换步再随便释放一条。
                return;
            }
        }
        if (effectiveMove.LengthSquared() <= 1e-10f && !forcedByStuck)
            return;
        bool arrivedShortfall =
            ArrivedTentacleCount < RequiredArrivedTentaclesForStep;
        if (_parameters.EnableStepReleaseThrottle && arrivedShortfall && !forcedByStuck)
        {
            // 计数门挡住时只冻结饥饿计时，不复位：稀疏形态的阀腿在途/重新到达期间
            // 到达数必然周期性跌破配额，此处复位会让饥饿窗永远凑不满阈值。
            // 复位只发生在释放（普通或阀）或运动 episode 真结束时。
            return;
        }
        int selected = -1;
        float selectedCancellationAfter = float.PositiveInfinity;
        float highestScore = float.NegativeInfinity;
        int reserveBlockedCandidates = 0;
        int valveSelected = -1;
        float valveCancellation = float.PositiveInfinity;
        float valveScore = float.NegativeInfinity;
        for (int i = 0; i < _tentacles.Length; i++)
        {
            DaddyTentacle tentacle = _tentacles[i];
            if (tentacle.Task != DaddyTentacleTask.Locomotion
                || (!_parameters.EnableIndependentLocomotionDuty
                    && !tentacle.NeededForLocomotion)
                || tentacle.StepInFlight
                || !tentacle.AtGrabDestination
                || !tentacle.HasLandingTarget)
            {
                continue;
            }
            float cancellationAfter = PredictGravityCancellationAfterRelease(tentacle);
            // 先过滤掉会击穿支撑余量的候选，再从可释放者中按原有姿态误差选“最该换”的腿。
            // 这样不会因为最高分恰好也是最强支撑腿，就把其它安全候选一起饿死。
            if (!forcedByStuck && _parameters.EnableStepSupportReserve
                && cancellationAfter < _parameters.StepReleaseMinimumGravityCancellation)
            {
                reserveBlockedCandidates++;
                if (cancellationAfter
                        >= _parameters.StartReplantMinimumGravityCancellation
                    && tentacle.ReleaseScore > valveScore)
                {
                    valveScore = tentacle.ReleaseScore;
                    valveSelected = i;
                    valveCancellation = cancellationAfter;
                }
                continue;
            }
            if (tentacle.ReleaseScore > highestScore)
            {
                highestScore = tentacle.ReleaseScore;
                selected = i;
                selectedCancellationAfter = cancellationAfter;
            }
        }
        if (selected >= 0)
        {
            LastStepReleasePredictedGravityCancellation = selectedCancellationAfter;
            MinimumStepReleasePredictedGravityCancellation = Math.Min(
                MinimumStepReleasePredictedGravityCancellation, selectedCancellationAfter);
            _tentacles[selected].BeginStep();
            InFlightStepCount = CountInFlightSteps();
            StepReleaseSerial++;
            _reserveStarvationTicks = 0;
            // 只有这次释放确实依赖 stuck 豁免（到达数不足，或被选腿低于 1g 余量门）
            // 才算强制换步；stuck 期间恰好满足普通条件的释放不计入。
            if (forcedByStuck
                && (arrivedShortfall
                    || (_parameters.EnableStepSupportReserve
                        && selectedCancellationAfter
                            < _parameters.StepReleaseMinimumGravityCancellation)))
            {
                StuckForcedStepReleaseSerial++;
            }
            _stepCooldown = _parameters.StepReleaseCooldownTicks;
            return;
        }
        if (reserveBlockedCandidates == 0)
        {
            // 本 tick 没有仅因余量被拒的候选（如全部在途）；冻结计时等待下一个合格 tick。
            return;
        }
        if (!_parameters.EnableStepSupportReserve
            || !_parameters.EnableStepReserveStarvationValve
            || forcedByStuck)
        {
            return;
        }
        // 余量饥饿阀：计数门已过、候选仅因 1.00 门被拒的合格 tick 连续累计到阈值后，
        // 以起步腿同款 0.90 预算串行放行一条。每次阀释放都重新累计完整饥饿窗——
        // 只有 1.00 门恒拒绝的真死锁形态才持续用阀（约 1 步/(阈值+换步) tick），
        // 尚有普通换步能力的低余量形态在两次普通步之间不会被阀频繁抽腿压低身高。
        _reserveStarvationTicks = Math.Min(
            _reserveStarvationTicks + 1, _parameters.StepReserveStarvationTicks);
        if (_reserveStarvationTicks < _parameters.StepReserveStarvationTicks
            || InFlightStepCount != 0
            || valveSelected < 0)
        {
            return;
        }
        LastReserveStarvationPredictedGravityCancellation = valveCancellation;
        MinimumReserveStarvationPredictedGravityCancellation = Math.Min(
            MinimumReserveStarvationPredictedGravityCancellation, valveCancellation);
        _tentacles[valveSelected].BeginStep();
        InFlightStepCount = CountInFlightSteps();
        StepReleaseSerial++;
        ReserveStarvationReleaseSerial++;
        _reserveStarvationTicks = 0;
        _stepCooldown = _parameters.StepReleaseCooldownTicks;
    }

    private bool TryBeginStartReplant(Vector3 effectiveMove)
    {
        if (effectiveMove.LengthSquared() <= 1e-10f)
            return false;
        Vector3 move = effectiveMove.Normalized();
        int selected = -1;
        float selectedDot = float.PositiveInfinity;
        float selectedRelease = float.NegativeInfinity;
        float selectedCancellation = float.PositiveInfinity;
        for (int i = 0; i < _tentacles.Length; i++)
        {
            DaddyTentacle tentacle = _tentacles[i];
            if (tentacle.Task != DaddyTentacleTask.Locomotion
                || (!_parameters.EnableIndependentLocomotionDuty
                    && !tentacle.NeededForLocomotion)
                || tentacle.StunTicks > 0
                || !tentacle.AtGrabDestination
                || !tentacle.HasLandingTarget)
            {
                continue;
            }
            Vector3 side = tentacle.LandingPoint - BodyCenter;
            if (side.LengthSquared() <= 1e-10f)
                continue;
            float sideDot = side.Normalized().Dot(move);
            if (sideDot > _parameters.StartReplantReleaseDotMaximum)
                continue;
            float cancellationAfter = PredictGravityCancellationAfterRelease(tentacle);
            float startMinimum = _parameters.EnableStartReplantTransientSupportBudget
                ? _parameters.StartReplantMinimumGravityCancellation
                : _parameters.StepReleaseMinimumGravityCancellation;
            if (_parameters.EnableStepSupportReserve
                && cancellationAfter < startMinimum)
            {
                continue;
            }
            float normalizedRelease = tentacle.ReleaseScore / Math.Max(tentacle.Length, 1e-5f);
            bool better = sideDot < selectedDot - 1e-5f
                || (Math.Abs(sideDot - selectedDot) <= 1e-5f
                    && (normalizedRelease > selectedRelease + 1e-5f
                        || (Math.Abs(normalizedRelease - selectedRelease) <= 1e-5f
                            && (selected < 0 || i < selected))));
            if (!better)
                continue;
            selected = i;
            selectedDot = sideDot;
            selectedRelease = normalizedRelease;
            selectedCancellation = cancellationAfter;
        }
        if (selected < 0)
            return false;

        LastStartReplantPredictedGravityCancellation = selectedCancellation;
        MinimumStartReplantPredictedGravityCancellation = Math.Min(
            MinimumStartReplantPredictedGravityCancellation, selectedCancellation);
        LastStartReplantTentacleIndex = selected;
        LastStartReplantReleaseDot = selectedDot;
        _tentacles[selected].BeginStartReplant(move);
        InFlightStepCount = CountInFlightSteps();
        _startReplantIndex = selected;
        _startReplantPending = false;
        StartReplantSerial++;
        StepReleaseSerial++;
        _stepCooldown = _parameters.StepReleaseCooldownTicks;
        return true;
    }

    private float PredictGravityCancellationAfterRelease(DaddyTentacle tentacle)
    {
        float rawAfter = Math.Max(0f,
            RawSupport - tentacle.SupportContribution / _tentacles.Length);
        float responseAfter = rawAfter > 0f
            ? Mathf.Pow(rawAfter, _parameters.SupportResponseExponent)
            : 0f;
        float continuousAfter = _parameters.EnableSupport
            ? Math.Max(responseAfter, _unconditionalSupport)
            : 0f;
        float cancellationAfter = continuousAfter * _parameters.GravityCancellationGain;
        return _parameters.EnableSupportOvercompensation
            ? cancellationAfter
            : Math.Min(cancellationAfter, 1f);
    }

    private void UpdateStuckState(Vector3 center, Vector3 effectiveMove)
    {
        _positionHistory[_historyHead] = center;
        _historyHead = (_historyHead + 1) % _positionHistory.Length;
        _historyCount = Math.Min(_historyCount + 1, _positionHistory.Length);
        bool stalled = false;
        if (effectiveMove.LengthSquared() > 1e-10f
            && RunSpeed > 1e-5f
            && _historyCount > _parameters.StuckCompareTicks)
        {
            int oldIndex = _historyHead - 1 - _parameters.StuckCompareTicks;
            while (oldIndex < 0)
                oldIndex += _positionHistory.Length;
            stalled = center.DistanceTo(_positionHistory[oldIndex]) < _parameters.StuckDistance;
        }
        if (!HasMoveIntent && _moveEpisodeActive)
        {
            // 点按间隙只冻结恢复记忆；不能把一次运动误拆成大量“重新起步”，也不能
            // 在没有真实输入时继续累积或消退 stuck。
            StuckAmount = Mathf.Clamp(
                (float)StuckCounter / _parameters.StuckRiseTicks, 0f, 1f);
            return;
        }
        StuckCounter = stalled
            ? Math.Min(_parameters.StuckRiseTicks * 2, StuckCounter + 1)
            : Math.Max(0, StuckCounter - _parameters.StuckFallPerTick);
        StuckAmount = Mathf.Clamp(
            (float)StuckCounter / _parameters.StuckRiseTicks, 0f, 1f);
        if (StuckCounter <= _parameters.StuckRiseTicks / 2)
            _stuckDetourArmed = true;
    }

    private Vector3 UpdateStuckDetour(in TickContext ctx, Vector3 rawMove)
    {
        if (!HasMoveIntent && _moveEpisodeActive)
        {
            return StuckDetourActive
                ? NormalizeOr(
                    rawMove + StuckDetourDirection * _parameters.StuckDetourMoveWeight,
                    rawMove)
                : rawMove;
        }
        if (!_parameters.EnableStuckRecovery
            || !HasMoveIntent
            || rawMove.LengthSquared() <= 1e-10f)
        {
            ClearActiveStuckDetour();
            return rawMove;
        }

        Vector3 move = rawMove.Normalized();
        if (!StuckDetourActive
            && _stuckDetourArmed
            && StuckCounter > _parameters.StuckRiseTicks)
        {
            BeginStuckDetour(move, retryAfterTimeout: false);
        }
        if (!StuckDetourActive)
            return move;

        // 宿主已经给出新的邻近路径点或明显改向时，旧障碍的锁存绕行侧立即失效；
        // 继续沿旧 side 只会把一次墙前脱困错误延长成新的 locomotion 模式。
        if (move.Dot(_stuckDetourForward) < 0.80f)
        {
            ClearActiveStuckDetour();
            return move;
        }

        _stuckDetourTicks++;
        if (_stuckDetourTicks % _parameters.StuckClearanceProbeIntervalTicks == 0)
        {
            float envelope = 0f;
            foreach (BodyChunk chunk in Body.Chunks)
            {
                envelope = Math.Max(envelope,
                    chunk.Pos.DistanceTo(BodyCenter) + chunk.Radius);
            }
            float skin = Math.Max(Body.Skin, 0.005f);
            Vector3 from = BodyCenter
                - StuckDetourDirection
                    * (envelope + _parameters.StuckClearanceEnvelopeMargin)
                + _stuckDetourSurfaceNormal * (2f * skin);
            float probeLength = Math.Max(
                _parameters.StuckClearanceMinimumLength,
                2f * envelope + _parameters.StuckClearanceEnvelopeMargin);
            bool blocked = ctx.Terrain.Raycast(
                from,
                from + _stuckDetourForward * probeLength,
                out _);
            _stuckClearanceMisses = blocked ? 0 : _stuckClearanceMisses + 1;
        }

        bool clearanceConfirmed = _stuckDetourTicks >= _parameters.StuckDetourMinimumTicks
            && _stuckClearanceMisses >= _parameters.StuckClearanceRequiredMisses;
        if (clearanceConfirmed)
        {
            ClearActiveStuckDetour();
            return move;
        }
        if (_stuckDetourTicks >= _parameters.StuckDetourMaximumTicks)
        {
            if (StuckCounter > _parameters.StuckRiseTicks)
            {
                // 大体型可能把一个侧向 episode 完全耗尽。保持 detour 连续，用递增 serial
                // 立即重启；成对 attempt 精确反向，避免同一随机侧重复撞完整超时窗。
                BeginStuckDetour(move, retryAfterTimeout: true);
                return NormalizeOr(
                    move + StuckDetourDirection * _parameters.StuckDetourMoveWeight,
                    move);
            }
            ClearActiveStuckDetour();
            return move;
        }

        return NormalizeOr(
            move + StuckDetourDirection * _parameters.StuckDetourMoveWeight,
            move);
    }

    private Vector3 SelectStuckDetourDirection(
        Vector3 move,
        int episodeSerial,
        out Vector3 surfaceNormal)
    {
        Vector3 normalizedMove = NormalizeOr(move, MaterialAxisX);
        int pairSerial = (episodeSerial + 1) / 2;
        Vector3 sampled = DaddyLongLegsFactory.SampleUnitVector(
            Morphology.StableSeed, 0x5A00UL, pairSerial);
        surfaceNormal = Vector3.Zero;
        Vector3 lateral = Vector3.Zero;

        float bestAlignment = 0.85f;
        foreach (DaddyTentacle tentacle in _tentacles)
        {
            if (tentacle.SupportContribution <= 0.01f
                || tentacle.SupportNormal.LengthSquared() <= 1e-10f)
            {
                continue;
            }
            Vector3 candidateNormal = tentacle.SupportNormal.Normalized();
            float alignment = Math.Abs(candidateNormal.Dot(normalizedMove));
            if (alignment < bestAlignment)
            {
                bestAlignment = alignment;
                surfaceNormal = candidateNormal;
            }
        }
        if (surfaceNormal.LengthSquared() > 1e-10f)
            lateral = surfaceNormal.Cross(normalizedMove);

        if (lateral.LengthSquared() <= 1e-10f)
        {
            Vector3[] axes = [MaterialAxisX, MaterialAxisY, MaterialAxisZ];
            float bestLength = -1f;
            foreach (Vector3 axis in axes)
            {
                Vector3 projected = axis - normalizedMove * axis.Dot(normalizedMove);
                float length = projected.LengthSquared();
                if (length > bestLength)
                {
                    bestLength = length;
                    lateral = projected;
                }
            }
        }
        if (lateral.LengthSquared() <= 1e-10f)
            lateral = sampled - normalizedMove * sampled.Dot(normalizedMove);
        lateral = NormalizeOr(lateral, sampled);
        if (lateral.Dot(sampled) < 0f)
            lateral = -lateral;
        if ((episodeSerial & 1) == 0)
            lateral = -lateral;
        return lateral;
    }

    private void BeginStuckDetour(Vector3 move, bool retryAfterTimeout)
    {
        _stuckDetourArmed = false;
        StuckEpisodeSerial++;
        _stuckDetourForward = move;
        bool useOppositePair = retryAfterTimeout
            && (StuckEpisodeSerial & 1) == 0
            && _stuckPairDirection.LengthSquared() > 1e-10f;
        if (useOppositePair)
        {
            StuckDetourDirection = -_stuckPairDirection;
            _stuckDetourSurfaceNormal = _stuckPairSurfaceNormal;
        }
        else
        {
            StuckDetourDirection = SelectStuckDetourDirection(move, StuckEpisodeSerial,
                out _stuckDetourSurfaceNormal);
            _stuckPairDirection = StuckDetourDirection;
            _stuckPairSurfaceNormal = _stuckDetourSurfaceNormal;
        }
        _stuckDetourTicks = 0;
        _stuckClearanceMisses = 0;
        StuckDetourActive = true;
    }

    private void ApplyDeterministicStuckJitter(
        long tickIndex,
        float recovery,
        Vector3 effectiveMove)
    {
        if (!_parameters.EnableStuckBodyJitter
            || !HasMoveIntent
            || !StuckDetourActive
            || recovery <= 0f
            || _parameters.StuckBodyJitter <= 0f)
            return;
        // tickIndex 保留在签名里，便于 smoke 直接验证固定 tick 接缝；方向属于整次 stuck
        // episode，不按全局时钟重采样，避免左右抵消。
        _ = tickIndex;
        _ = effectiveMove;
        Vector3 jitterDirection = StuckDetourDirection;
        Vector3 jitter = jitterDirection * (_parameters.StuckBodyJitter * recovery);
        float speedCap = _parameters.MaxMoveSpeed * _parameters.StuckJitterSpeedCapMultiplier;
        Vector3 velocityCenter = ComputeBodyVelocityCenter();
        Vector3 cappedCenter = (velocityCenter + jitter).LimitLength(speedCap);
        Vector3 commonDelta = cappedCenter - velocityCenter;
        // 速度帽只作用于质量质心；给每球同一个实际 delta，严格保留相对速度。
        foreach (BodyChunk chunk in Body.Chunks)
            chunk.Vel += commonDelta;
    }

    private void UpdateMaterialFrame()
    {
        Vector3 a = Body.Chunks[Morphology.FrameLandmarkA].Pos;
        Vector3 b = Body.Chunks[Morphology.FrameLandmarkB].Pos;
        Vector3 c = Body.Chunks[Morphology.FrameLandmarkC].Pos;
        Vector3 x = NormalizeOr(b - a, MaterialAxisX);
        Vector3 rawY = c - (a + b) * 0.5f;
        Vector3 y = rawY - x * rawY.Dot(x);
        y = NormalizeOr(y, MaterialAxisY);
        Vector3 z = NormalizeOr(x.Cross(y), MaterialAxisZ);
        y = NormalizeOr(z.Cross(x), y);
        if (_frameInitialized && z.Dot(MaterialAxisZ) < 0f)
        {
            y = -y;
            z = -z;
        }
        MaterialAxisX = x;
        MaterialAxisY = y;
        MaterialAxisZ = z;
        _frameInitialized = true;
    }

    private Vector3 TransformPreference(Vector3 local) => NormalizeOr(
        MaterialAxisX * local.X + MaterialAxisY * local.Y + MaterialAxisZ * local.Z,
        MaterialAxisX);

    private Vector3 ComputeBodyCenter()
    {
        Vector3 weighted = Vector3.Zero;
        float mass = 0f;
        foreach (BodyChunk chunk in Body.Chunks)
        {
            weighted += chunk.Pos * chunk.Mass;
            mass += chunk.Mass;
        }
        return mass > 1e-8f ? weighted / mass : Vector3.Zero;
    }

    private Vector3 ComputeBodyVelocityCenter()
    {
        Vector3 weighted = Vector3.Zero;
        float mass = 0f;
        foreach (BodyChunk chunk in Body.Chunks)
        {
            weighted += chunk.Vel * chunk.Mass;
            mass += chunk.Mass;
        }
        return mass > 1e-8f ? weighted / mass : Vector3.Zero;
    }

    private void ClearActiveStuckDetour()
    {
        StuckDetourActive = false;
        StuckDetourDirection = Vector3.Zero;
        _stuckDetourForward = Vector3.Zero;
        _stuckDetourSurfaceNormal = Vector3.Zero;
        _stuckDetourTicks = 0;
        _stuckClearanceMisses = 0;
    }

    private void ResetSupportAndRecovery(bool restoreUnconditionalSupport)
    {
        RawSupport = 0f;
        EffectiveSupport = 0f;
        _unconditionalSupport = restoreUnconditionalSupport ? 1f : 0f;
        _continuousSupport = _unconditionalSupport;
        GravityCancellation = 0f;
        DirectionalSupport = 0f;
        DriveScale = 0f;
        _moveStartAssistTicks = 0;
        _hadMoveIntent = false;
        _lastMoveIntent = Vector3.Zero;
        EndMovementEpisode();
        _startReplantIndex = -1;
        _reserveStarvationTicks = 0;
        LastStartReplantTentacleIndex = -1;
        LastStartReplantReleaseDot = 1f;
        LastStepReleasePredictedGravityCancellation = float.PositiveInfinity;
        MinimumStepReleasePredictedGravityCancellation = float.PositiveInfinity;
        LastStartReplantPredictedGravityCancellation = float.PositiveInfinity;
        MinimumStartReplantPredictedGravityCancellation = float.PositiveInfinity;
        LastReserveStarvationPredictedGravityCancellation = float.PositiveInfinity;
        MinimumReserveStarvationPredictedGravityCancellation = float.PositiveInfinity;
        SupportNormal = Vector3.Up;
        StuckCounter = 0;
        StuckAmount = 0f;
        ClearActiveStuckDetour();
        StuckEpisodeSerial = 0;
        _stuckPairDirection = Vector3.Zero;
        _stuckPairSurfaceNormal = Vector3.Zero;
        _stuckDetourArmed = true;
        _historyHead = 0;
        _historyCount = 0;
        Array.Clear(_positionHistory, 0, _positionHistory.Length);
        Body.GravityScale = 1f;
        Body.AirFriction = _parameters.UnsupportedAirFriction;
        Body.SurfaceFriction = _parameters.UnsupportedSurfaceFriction;
    }

    private void RestoreMinimumDuty()
    {
        for (int i = 0; i < _tentacles.Length; i++)
            if (_tentacles[i].Task == DaddyTentacleTask.Locomotion)
                _tentacles[i].SetIdle();
        int minimum = Math.Min(_parameters.MinimumLocomotionTentacles, _tentacles.Length);
        for (int i = 0; i < minimum; i++)
        {
            int index = Math.Min(_tentacles.Length - 1,
                (int)((long)i * _tentacles.Length / minimum));
            _tentacles[index].SetLocomotion();
        }
        _dutyCooldown = 0;
        _stepCooldown = 0;
    }

    private int CountLocomotionTentacles()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _tentacles)
            if (tentacle.NeededForLocomotion)
                count++;
        return count;
    }

    private int CountInFlightSteps()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _tentacles)
            if (tentacle.StepInFlight)
                count++;
        return count;
    }

    private int CountStepPool()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _tentacles)
        {
            if (tentacle.Task != DaddyTentacleTask.Locomotion
                || tentacle.StunTicks > 0
                || tentacle.TerrainRecoveryActive
                || (!_parameters.EnableIndependentLocomotionDuty
                    && !tentacle.NeededForLocomotion))
            {
                continue;
            }
            if (tentacle.HasLandingTarget
                || tentacle.StepInFlight
                || tentacle.SearchFailureTicks < _parameters.SearchFailureExpandTicks)
            {
                count++;
            }
        }
        return count;
    }

    private int CountArrivedTentacles()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _tentacles)
            if (tentacle.AtGrabDestination && tentacle.HasLandingTarget)
                count++;
        return count;
    }

    private DaddyTentacle TentacleAt(int index)
    {
        if ((uint)index >= (uint)_tentacles.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"Tentacle index must be in [0, {_tentacles.Length - 1}].");
        return _tentacles[index];
    }

    private void ValidateInputs(in TickContext ctx)
    {
        EnsureFinite(MoveDir, nameof(MoveDir));
        if (!float.IsFinite(RunSpeed) || RunSpeed < 0f)
            throw new ArgumentOutOfRangeException(nameof(RunSpeed), RunSpeed,
                "RunSpeed must be finite and non-negative.");
        if (MoveTarget is { } target)
            EnsureFinite(target, nameof(MoveTarget));
        if (!float.IsFinite(MoveTargetArriveRadius) || MoveTargetArriveRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(MoveTargetArriveRadius));
        EnsureFinite(ctx.GravityPerTick, nameof(ctx.GravityPerTick));
        ArgumentNullException.ThrowIfNull(ctx.Terrain);
    }

    private static void EnsureFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException($"{name} must be finite.", name);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f
            ? value.Normalized()
            : fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;

    private sealed class CountingTerrainQuery : ITerrainQuery
    {
        private ITerrainQuery? _inner;
        private int _budget;
        public int Count { get; private set; }
        public bool Exhausted { get; private set; }

        public void Reset(ITerrainQuery inner, int budget)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _budget = budget;
            Count = 0;
            Exhausted = false;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            if (Count >= _budget)
            {
                Exhausted = true;
                hit = default;
                return false;
            }
            Count++;
            return _inner!.Raycast(from, to, out hit);
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            const int sphereCost = 2;
            if (_budget - Count < sphereCost)
            {
                Exhausted = true;
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            Count += sphereCost;
            return _inner!.SpherePenetration(center, radius, out pushDir, out depth);
        }
    }
}
