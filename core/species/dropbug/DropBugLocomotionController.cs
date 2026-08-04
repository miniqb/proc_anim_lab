using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.DropBug;

/// <summary>悬挂点被拒的原因（宿主/沙盒 HUD 观测用）。</summary>
public enum DropBugHangRejection
{
    None,
    InvalidNormal,
    NotCeiling,
    TooThin,
    NoBodyClearance,
    NoDropClearance,
}

/// <summary>悬挂意图的宏观阶段（由锚与 HangFactor 派生，非独立状态）。</summary>
public enum DropBugHangState
{
    None,
    Approaching,
    Settling,
    Hung,
}

/// <summary>已验证的悬挂锚：表面点 + 朝外（向下）法线 + 切平面基 + collider 身份。</summary>
public readonly struct DropBugHangAnchor
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly Vector3 TangentU;
    public readonly Vector3 TangentV;
    public readonly ulong ColliderId;

    public DropBugHangAnchor(Vector3 point, Vector3 normal, Vector3 tangentU,
        Vector3 tangentV, ulong colliderId)
    {
        Point = point;
        Normal = normal;
        TangentU = tangentU;
        TangentV = tangentV;
        ColliderId = colliderId;
    }

    internal DropBugHangAnchor Shifted(Vector3 delta) =>
        new(Point + delta, Normal, TangentU, TangentV, ColliderId);
}

/// <summary>宿主喂的扑击/俯冲目标：固定点传一次，随动点每 tick 覆写。</summary>
public readonly struct DropBugAttackTarget
{
    public readonly Vector3 Point;
    public readonly Vector3 VelocityPerTick;

    public DropBugAttackTarget(Vector3 point, Vector3 velocityPerTick)
    {
        Point = point;
        VelocityPerTick = velocityPerTick;
    }

    internal DropBugAttackTarget Shifted(Vector3 delta) =>
        new(Point + delta, VelocityPerTick);
}

/// <summary>
/// RW DropBug 的 3D 独立后端：三节短链身体、前后不对称重力、运行时收放的悬挂态、
/// 弹道俯冲与地面蓄力扑击。与其它物种平行，只共享 Body/连接/地形原语。
/// 固定序 = Body.Tick（消费上轮 Act 的力）→ 读取接触 → 为下一 tick 注入力
/// （≙ 反编译 DropBug.Update 中 base.Update 之后的全部块）。
///
/// 显式 vs 涌现（任务第 12 条的取舍，理由详见 docs/dropbug_controller.md §7）：
/// 保留的显式状态只有四个跨 tick 意图量——HangFactor（连续收放进度，直接驱动
/// 静息长度形变）、PounceCharge（蓄力进度，驱动力 ramp）、Diving（俯冲意图：
/// 「因俯冲而腾空」与「被宿主击飞」物理不可分辨，结束条件与冷却不同）、
/// AttackCooldown（计时器）。行走/站立/坠落/倒退/越障全部由支撑计数与几何派生，
/// 无 locomotion 模式枚举。
/// </summary>
public sealed class DropBugLocomotionController
{
    public readonly Body Body;
    public readonly BodyChunk Head;
    public readonly BodyChunk Mid;
    public readonly BodyChunk Tail;

    // —— 宿主输入 ——
    public Vector3 MoveDir;
    public float RunSpeed;
    public Vector3? MoveTarget;
    /// <summary>携带负重质量（RW 质量单位，≙ carryObjectMass；&gt;0 视为携带中，
    /// 削减行进力并禁止蓄力扑击）。</summary>
    public float CarriedMass;
    public DropBugAttackTarget? AttackTarget;

    // —— 消融开关（专项 smoke 用；正式预设一律保持 true，只切断机制不开新模式）——
    public bool EnableTailGravityAsymmetry = true;
    public bool EnableFootingGrace = true;
    public bool EnableHangMorph = true;
    public bool EnableDiveSteering = true;
    public bool EnablePounceReachGate = true;
    public bool EnableObstacleHop = true;
    public bool EnableStuckShake = true;
    public bool EnableBackwardsWalk = true;

    // —— 观测输出 ——
    public bool AtMoveTarget { get; private set; }
    public int FootingCounter { get; private set; }
    public bool Footing => FootingCounter > _p.FootingThreshold;
    public float HangFactor { get; private set; }
    public bool Hanging => HangFactor >= 0.999f;
    public DropBugHangAnchor? HangAnchor => _hangAnchor;
    public DropBugHangRejection LastHangRejection { get; private set; }
    public DropBugHangState HangState =>
        _hangAnchor is null ? DropBugHangState.None
        : Hanging ? DropBugHangState.Hung
        : HangFactor > 0f ? DropBugHangState.Settling
        : DropBugHangState.Approaching;
    /// <summary>Launch 后的悬挂重贴附冷却剩余 tick（≙ 原作 stun 窗口，见参数注释）。
    /// 大于 0 时贴附、爬升辅助与锚面支撑全部停摆，意图锚保留。</summary>
    public int HangRegrabDelay { get; private set; }
    public float PounceCharge { get; private set; }
    public bool ChargingPounce => PounceCharge > 0f;
    public bool Jumping { get; private set; }
    public bool Diving { get; private set; }
    public int AttackCooldown { get; private set; }
    public bool MovingBackwards { get; private set; }
    public bool Sitting { get; private set; }
    public float StuckSignal { get; private set; }
    public float StuckShake { get; private set; }
    public float RunCycle { get; private set; }
    public Vector3 TravelDir { get; private set; }
    public Vector3 Forward { get; private set; }
    public Vector3 Up { get; private set; }
    public Vector3 Right { get; private set; }
    public IReadOnlyList<DropBugLeg> Legs => _legs;

    // —— 事件序号 / 落点观测（回归门用；不参与物理决策）——
    public long PounceLeapSerial { get; private set; }
    public long PounceAbandonSerial { get; private set; }
    public long DiveSerial { get; private set; }
    public long HopSerial { get; private set; }
    public long LastDiveLandingTick { get; private set; } = -1;
    public Vector3 LastDiveLandingPoint { get; private set; }
    public long LastJumpLandingTick { get; private set; } = -1;

    private readonly DropBugParams _p;
    private readonly List<DropBugLeg> _legs = new();
    private DropBugHangAnchor? _hangAnchor;
    private Vector3 _transportUp;
    private Vector3 _arriveBoundTarget = new(float.MaxValue, 0f, 0f);
    private bool _headSupported;
    private bool _midSupported;
    private int _stuckTicks;
    private readonly Vector3[] _stuckHistory;
    private int _stuckHistoryCount;
    private int _legDangleTicks;
    private long _tick;

    private const float MoveIntentDeadzone = 1e-4f;

    internal DropBugLocomotionController(
        Body body,
        BodyChunk head,
        BodyChunk mid,
        BodyChunk tail,
        DropBugParams parameters,
        Vector3 initialForward)
    {
        Body = body;
        Head = head;
        Mid = mid;
        Tail = tail;
        _p = parameters;
        _stuckHistory = new Vector3[Mathf.Max(2, parameters.StuckWindowTicks)];
        Forward = SafeDirection(initialForward, Vector3.Forward);
        _transportUp = StablePerpendicular(Forward, Vector3.Up);
        Right = SafeDirection(Forward.Cross(_transportUp), Vector3.Right);
        Up = SafeDirection(Right.Cross(Forward), _transportUp);
        _transportUp = Up;

        int index = 0;
        for (int pair = 0; pair < parameters.LegPairs; pair++)
        {
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                int side = sideIndex == 0 ? -1 : 1;
                Vector3 start = head.Pos + Right * (side * 0.3f) - Up * 0.1f;
                _legs.Add(new DropBugLeg(side, pair, index, start));
                index++;
            }
        }
    }

    // ================================================================== 宿主 API

    /// <summary>
    /// 指定悬挂点：宿主给一个表面命中（点 + 法线），控制器按 3D 判据验证——
    /// 法线朝下 ≥ MinCeilingDot、实体厚度 ≥ SolidProbeDepth、沿法线的身体净空、
    /// 沿世界竖直的落差 ≥ MinDropClearance。拒绝时原因写入 LastHangRejection。
    /// 通过只建立「意图」；真正进入悬挂需要身体自己接近到 HangEngageDistance。
    /// </summary>
    public bool TryAssignHangAnchor(in TerrainHit hit, ITerrainQuery terrain)
    {
        if (hit.Normal.LengthSquared() <= 1e-10f)
        {
            LastHangRejection = DropBugHangRejection.InvalidNormal;
            return false;
        }
        Vector3 n = hit.Normal.Normalized();
        if (n.Dot(Vector3.Up) > -_p.MinCeilingDot)
        {
            LastHangRejection = DropBugHangRejection.NotCeiling;
            return false;
        }
        // 厚度探针（≙ 原作「上方连续 2 实心 tile」）：表面里侧 SolidProbeDepth 处必须仍在实体内。
        Vector3 probe = hit.Point - n * _p.SolidProbeDepth;
        if (!terrain.SpherePenetration(probe, 0.02f, out _, out _))
        {
            LastHangRejection = DropBugHangRejection.TooThin;
            return false;
        }
        // 身体净空（≙ 原作「下方 tile 为空」）：沿法线方向必须留得下团起来的身体。
        if (terrain.Raycast(hit.Point + n * 0.05f, hit.Point + n * _p.BodyClearance, out _))
        {
            LastHangRejection = DropBugHangRejection.NoBodyClearance;
            return false;
        }
        // 落差（≙ floorAltitude ≥ 6 tile）：俯冲是重力弹道，落差沿世界竖直量。
        Vector3 dropFrom = hit.Point + n * 0.1f;
        if (terrain.Raycast(dropFrom, dropFrom + Vector3.Down * _p.MinDropClearance, out _))
        {
            LastHangRejection = DropBugHangRejection.NoDropClearance;
            return false;
        }

        Vector3 tangentU = StablePerpendicular(n, Forward);
        Vector3 tangentV = SafeDirection(n.Cross(tangentU), Vector3.Right);
        _hangAnchor = new DropBugHangAnchor(hit.Point, n, tangentU, tangentV, hit.ColliderId);
        LastHangRejection = DropBugHangRejection.None;
        HangRegrabDelay = 0; // 宿主显式重申意图 → 冷却让位（≙ AI 重新选点）
        return true;
    }

    /// <summary>撤销悬挂意图。已在悬挂中则立即展开（≙ 原作退出恒为瞬时：
    /// inCeilingMode 直接归 0，静息长度下一 tick 由公式恢复）。</summary>
    public void ClearHangAnchor()
    {
        _hangAnchor = null;
        HangFactor = 0f;
        HangRegrabDelay = 0;
    }

    /// <summary>
    /// 脱离悬挂 = 一次俯冲（≙ JumpFromCeiling）。有 AttackTarget 时按距离预判提前量
    /// 瞄准（≙ AI SitUpdate 的 ClampMagnitude(vel)×LerpMap 预判），否则竖直向下
    /// （≙ Dislodge 的 (0,-1)）。只有已开始进入悬挂（HangFactor &gt; 0）才有效。
    /// </summary>
    public bool ReleaseHangDive()
    {
        if (HangFactor <= 0f || _hangAnchor is null)
        {
            return false;
        }
        Vector3 dir = Vector3.Down;
        if (AttackTarget is { } target)
        {
            float distance = Head.Pos.DistanceTo(target.Point);
            float lead = LerpMap(distance, _p.DiveLeadNear, _p.DiveLeadFar,
                0f, _p.DiveLeadMaxTicks, _p.DiveLeadPower);
            Vector3 aim = target.Point +
                          ClampLength(target.VelocityPerTick, _p.DiveLeadVelocityClamp) * lead;
            dir = SafeDirection(aim - Head.Pos, Vector3.Down);
        }
        HangFactor = 0f;
        _hangAnchor = null;
        Diving = true;
        DiveSerial++;
        Jump(dir);
        return true;
    }

    /// <summary>
    /// 开始地面蓄力扑击（≙ InitiateJump）：需要站稳、有目标、非悬挂/腾空/冷却/负重。
    /// 蓄满自动弹射；目标出可及范围或被清除则放弃归零。
    /// </summary>
    public bool TryStartPounce()
    {
        if (PounceCharge > 0f || Jumping || Diving || HangFactor > 0f ||
            CarriedMass > 0f || AttackCooldown > 0 || AttackTarget is null || !Footing)
        {
            return false;
        }
        PounceCharge = _p.PounceChargeStart;
        return true;
    }

    /// <summary>宿主主动放弃蓄力。</summary>
    public void CancelPounce()
    {
        if (PounceCharge > 0f)
        {
            PounceCharge = 0f;
            PounceAbandonSerial++;
        }
    }

    /// <summary>当前身体姿态下沿 direction 扑击的可及距离（≙ Attack 的
    /// LerpMap(dot(扑向, 身体轴), -0.1, 0.8, 0, 300px, 0.4)；侧对目标显著缩短）。</summary>
    public float PounceReach(Vector3 direction)
    {
        Vector3 axis = SafeDirection(Head.Pos - Mid.Pos, Forward);
        float dot = SafeDirection(direction, axis).Dot(axis);
        return LerpMap(dot, _p.ReachDotLow, _p.ReachDotHigh, 0f, _p.PounceReachMax,
            _p.ReachCurvePower);
    }

    /// <summary>整体世界原点平移：所有世界坐标状态同步移动，动力学无缝继续。</summary>
    public void Shift(Vector3 delta)
    {
        Body.Shift(delta);
        if (MoveTarget is { } target)
        {
            MoveTarget = target + delta;
        }
        if (_arriveBoundTarget.X != float.MaxValue)
        {
            _arriveBoundTarget += delta;
        }
        if (AttackTarget is { } attack)
        {
            AttackTarget = attack.Shifted(delta);
        }
        if (_hangAnchor is { } anchor)
        {
            _hangAnchor = anchor.Shifted(delta);
        }
        for (int i = 0; i < _stuckHistory.Length; i++)
        {
            _stuckHistory[i] += delta;
        }
        foreach (DropBugLeg leg in _legs)
        {
            leg.Shift(delta);
        }
    }

    /// <summary>位置连续地瞬移，作废全部暂态：目标、悬挂、蓄力、俯冲、冷却与支撑记忆。</summary>
    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        MoveTarget = null;
        AttackTarget = null;
        AtMoveTarget = false;
        _arriveBoundTarget = new Vector3(float.MaxValue, 0f, 0f);
        _hangAnchor = null;
        HangFactor = 0f;
        HangRegrabDelay = 0;
        PounceCharge = 0f;
        Jumping = false;
        Diving = false;
        AttackCooldown = 0;
        FootingCounter = 0;
        _stuckTicks = 0;
        _stuckHistoryCount = 0;
        StuckSignal = 0f;
        StuckShake = 0f;
        TravelDir = Vector3.Zero;
        _legDangleTicks = 0;
        foreach (DropBugLeg leg in _legs)
        {
            leg.Planted = false;
            leg.Vel = Vector3.Zero;
            leg.TravelDir = Vector3.Zero;
        }
    }

    /// <summary>击飞：全部身体节注入同一速度增量。打断悬挂进入与蓄力（保留悬挂意图锚与
    /// MoveTarget/AttackTarget，≙ Deer Launch 保留目标的先例），支撑清零、腿进入 dangle。
    /// 悬挂重贴附进入 HangRegrabDelayTicks 冷却（≙ 原作 stun 窗口）——否则 1m 圈内的
    /// 击飞下一 tick 就被吸附伺服吃掉（外部评审 P1，无引擎实测 ≤0.30 m/tick 全部被吃）。</summary>
    public void Launch(Vector3 velocityPerTick)
    {
        Head.Vel += velocityPerTick;
        Mid.Vel += velocityPerTick;
        Tail.Vel += velocityPerTick;
        HangFactor = 0f;
        HangRegrabDelay = _p.HangRegrabDelayTicks;
        if (PounceCharge > 0f)
        {
            PounceCharge = 0f;
            PounceAbandonSerial++;
        }
        Jumping = false;
        Diving = false;
        FootingCounter = 0;
        _stuckHistoryCount = 0;
        _legDangleTicks = _p.LegDangleTicks;
        foreach (DropBugLeg leg in _legs)
        {
            leg.Planted = false;
        }
    }

    // ================================================================== 固定 tick

    public void Tick(in TickContext ctx)
    {
        _tick = ctx.TickIndex;
        ConfigureBody();
        Body.Tick(ctx);

        if (AttackCooldown > 0)
        {
            AttackCooldown--;
        }
        if (HangRegrabDelay > 0)
        {
            HangRegrabDelay--;
        }
        // 自撑力只在非静止时注入：原作恒定注入（头 +0.5px / 尾 −1px）带 −0.5px/tick
        // 净轴向动量，静止个体会缓慢滑移；任务语义是「在运动中保持舒展」，静止
        // （上 tick Sitting）时归零，换取严格的静止不动（偏离原作，见文档）。
        if (!Sitting)
        {
            SelfExtend();
        }
        UpdateSupport(ctx);
        UpdateDiveEnd();
        Sitting = false;

        bool actedByHang = UpdateHang(ctx);
        if (!actedByHang)
        {
            if (Jumping)
            {
                ActAirborne(ctx);
            }
            else
            {
                ActGround(ctx);
            }
        }

        ApplyFootingGravity(ctx);
        TravelDir *= Sitting ? _p.TravelDirSitDecay : _p.TravelDirDecay;
        ClampGroundSpeed();
        UpdateFrame();
        UpdateLegs(ctx);
    }

    /// <summary>静息长度与碰撞开关按 HangFactor 配置（≙ RW Update 尾部按 inCeilingMode
    /// 的赋值；上一 tick 的因子作用于本 tick 物理，顺序同 RW）。运行时改
    /// <see cref="ChunkConnection.RestLength"/> 是既有公开可变字段，共享层零改动。</summary>
    private void ConfigureBody()
    {
        float f = EnableHangMorph ? HangFactor : 0f;
        Body.Connections[0].RestLength = Mathf.Lerp(_p.HeadMidLength, _p.HangHeadMidLength, f);
        Body.Connections[1].RestLength = Mathf.Lerp(_p.MidTailLength, _p.HangMidTailLength, f);
        Body.Connections[2].RestLength = Mathf.Lerp(_p.AntiFoldLength, _p.HangAntiFoldLength, f);
        bool collide = HangFactor < _p.HangCollisionToggle;
        Mid.CollideWithTerrain = collide;
        Tail.CollideWithTerrain = collide;
    }

    /// <summary>每 tick 恒定自撑力（≙ Update：头被推离尾、尾被推向后方），保持身体舒展。</summary>
    private void SelfExtend()
    {
        Vector3 axis = SafeDirection(Head.Pos - Tail.Pos, Forward);
        Head.Vel += axis * _p.HeadExtension;
        Tail.Vel -= axis * _p.TailExtension;
    }

    /// <summary>支撑探测与站稳计数。普通态：头或中段可站立 → +1，双双失去 → -3/tick 且
    /// 上限 35（宽限期，≙ footingCounter 的 IntClamp(c-3, 0, 35)）；腾空（Jumping）态改用
    /// 接触判据（任一节接触可站立面 → +1，否则清零，≙ jumping 块）；悬挂钉 20。</summary>
    private void UpdateSupport(in TickContext ctx)
    {
        _headSupported = ChunkSupported(Head, ctx);
        _midSupported = ChunkSupported(Mid, ctx);

        if (HangFactor > 0f)
        {
            FootingCounter = _p.HangFootingPin;
            return;
        }
        if (Jumping)
        {
            bool contact = StandableContact(Head) || StandableContact(Mid) || StandableContact(Tail);
            FootingCounter = contact ? FootingCounter + 1 : 0;
            return;
        }
        if (_headSupported || _midSupported)
        {
            FootingCounter++;
        }
        else if (EnableFootingGrace)
        {
            FootingCounter = Mathf.Clamp(FootingCounter - _p.FootingLossDecay, 0,
                _p.FootingGraceCap);
        }
        else
        {
            FootingCounter = 0;
        }
    }

    private bool ChunkSupported(BodyChunk chunk, in TickContext ctx)
    {
        if (StandableContact(chunk))
        {
            return true;
        }
        float depth = chunk.Radius + _p.FootingProbeDepth;
        if (ctx.Terrain.Raycast(chunk.Pos, chunk.Pos + Vector3.Down * depth, out TerrainHit hit) &&
            Standable(hit.Normal))
        {
            return true;
        }
        // 悬挂意图存在且已接近锚点时，锚面也算支撑（≙ RW 天花板 tile 对 DropBug 可达，
        // 使贴顶爬升期间重力被 Footing 块抵消）；重贴附冷却期内锚面不作数，
        // 否则击飞后悬在锚旁的身体仍被抵消重力、弹道被抹平。
        if (HangRegrabDelay == 0 && _hangAnchor is { } anchor &&
            chunk.Pos.DistanceTo(anchor.Point) < _p.HangApproachDistance * 2f &&
            ctx.Terrain.Raycast(chunk.Pos, chunk.Pos - anchor.Normal * depth, out TerrainHit up) &&
            up.Normal.LengthSquared() > 1e-10f &&
            up.Normal.Normalized().Dot(anchor.Normal) > _p.MinGroundDot)
        {
            return true;
        }
        return false;
    }

    private bool StandableContact(BodyChunk chunk) =>
        chunk.TerrainContact && Standable(chunk.ContactNormal);

    private bool Standable(Vector3 normal) =>
        normal.LengthSquared() <= 1e-10f || // HitFromInside：嵌入即有支撑，方向未知
        normal.Normalized().Dot(Vector3.Up) >= _p.MinGroundDot;

    /// <summary>俯冲结束判定（≙ fromCeilingJump 块：任一节向下接触即结束，进入 20 tick
    /// 攻击冷却；水面结束不移植——本项目明确不做水）。</summary>
    private void UpdateDiveEnd()
    {
        if (!Diving)
        {
            return;
        }
        if (StandableContact(Head) || StandableContact(Mid) || StandableContact(Tail))
        {
            AttackCooldown = _p.AttackCooldownTicks;
            Diving = false;
            Jumping = false;
            PounceCharge = 0f;
            LastDiveLandingTick = _tick;
            LastDiveLandingPoint = (Head.Pos + Mid.Pos + Tail.Pos) / 3f;
        }
    }

    // ================================================================== 悬挂

    /// <summary>悬挂意图处理。返回 true 表示本 tick 由悬挂态接管（跳过地面/空中 Act，
    /// ≙ RW SittingInCeiling 块的 return）。</summary>
    private bool UpdateHang(in TickContext ctx)
    {
        if (_hangAnchor is not { } anchor)
        {
            return false;
        }

        // 已开始贴附时每 tick 复验锚面仍在（3D 附加：动态地形下面消失则直接掉落）。
        if (HangFactor > 0f && !ValidateAnchorSurface(anchor, ctx.Terrain))
        {
            _hangAnchor = null;
            HangFactor = 0f;
            return false;
        }

        Vector3 engagePoint = anchor.Point + anchor.Normal * _p.HangSurfaceInset;
        float distance = Mid.Pos.DistanceTo(engagePoint);

        if (distance < _p.HangEngageDistance && !Jumping && !Diving &&
            HangRegrabDelay == 0)
        {
            HangFactor = Mathf.Min(1f, HangFactor + _p.HangEnterRate);
            float f = HangFactor;
            Vector3 headTarget = anchor.Point +
                                 anchor.Normal * (_p.HangSurfaceInset + _p.HangHeadExtra * f);
            Vector3 midTarget = anchor.Point +
                                anchor.Normal * (_p.HangSurfaceInset - _p.HangMidRise * f);
            Vector3 tailTarget = anchor.Point +
                                 anchor.Normal * (_p.HangSurfaceInset - _p.HangTailRise * f);
            Head.Pos = Head.Pos.Lerp(headTarget, _p.HangLerpHead * f);
            Mid.Pos = Mid.Pos.Lerp(midTarget, _p.HangLerpMid * f);
            Tail.Pos = Tail.Pos.Lerp(tailTarget, _p.HangLerpTail * f);
            Head.Vel *= 1f - f;
            Mid.Vel *= 1f - f;
            Tail.Vel *= 1f - f;
            FootingCounter = _p.HangFootingPin;
            Sitting = true;
            MovingBackwards = false;
            return true;
        }

        // 身体被挤出贴附半径：瞬时展开（≙ RW SittingInCeiling 失效 → inCeilingMode = 0），
        // 意图锚保留，重新接近即重新贴附。
        if (HangFactor > 0f)
        {
            HangFactor = 0f;
        }

        // 最后一米的爬升辅助（≙ AI SitInCeiling 行为块：50px 内有视线时 mid.pos += 1px）。
        if (!Jumping && !Diving && HangRegrabDelay == 0 &&
            distance > _p.HangApproachMin && distance < _p.HangApproachDistance &&
            !ctx.Terrain.Raycast(Mid.Pos, engagePoint, out _))
        {
            Mid.Pos += (engagePoint - Mid.Pos).Normalized() * _p.HangApproachStep;
        }
        return false;
    }

    private bool ValidateAnchorSurface(in DropBugHangAnchor anchor, ITerrainQuery terrain)
    {
        Vector3 from = anchor.Point + anchor.Normal * 0.15f;
        Vector3 to = anchor.Point - anchor.Normal * 0.15f;
        return terrain.Raycast(from, to, out TerrainHit hit) &&
               hit.Normal.LengthSquared() > 1e-10f &&
               hit.ColliderId == anchor.ColliderId &&
               hit.Normal.Normalized().Dot(anchor.Normal) > 0.8f;
    }

    // ================================================================== 腾空

    /// <summary>腾空（扑击/俯冲弹道）中的持续修正（≙ jumping 块）：头朝目标、中尾反向
    /// 形成头朝前的力偶；俯冲且明显高于目标、距离在修正窗内时另做水平修正。</summary>
    private void ActAirborne(in TickContext ctx)
    {
        MovingBackwards = false;
        if (EnableDiveSteering && AttackTarget is { } target &&
            !ctx.Terrain.Raycast(Head.Pos, target.Point, out _))
        {
            Vector3 dir = SafeDirection(target.Point - Head.Pos, Forward);
            Head.Vel += dir * _p.DiveSteerHead;
            Mid.Vel -= dir * _p.DiveSteerBack;
            Tail.Vel -= dir * _p.DiveSteerBack;

            // ≙ 250px 高差 + 350px 距离窗的 vel.x += dir.x × 3px：
            // 3D 版取单位方向的水平分量（不归一化，保持原作幅度语义）。
            if (Diving &&
                (Head.Pos - target.Point).Dot(Vector3.Up) > _p.DiveHighAbove &&
                Head.Pos.DistanceTo(target.Point) < _p.DiveCorrectionRange)
            {
                Vector3 predicted = target.Point + target.VelocityPerTick;
                Vector3 toPredicted = SafeDirection(predicted - Head.Pos, dir);
                Vector3 horizontal = toPredicted - Vector3.Up * toPredicted.Dot(Vector3.Up);
                Head.Vel += horizontal * _p.DiveHorizontalCorrection;
            }
        }

        if (Footing)
        {
            Jumping = false;
            LastJumpLandingTick = _tick;
        }
    }

    // ================================================================== 地面

    private void ActGround(in TickContext ctx)
    {
        UpdateStuck();

        if (PounceCharge > 0f)
        {
            ActPounceCharge(ctx);
            UpdateRunCycle();
            return;
        }

        MovingBackwards = EnableBackwardsWalk && _hangAnchor is { } anchor &&
                          HangFactor <= 0f && StuckSignal <= 1e-4f &&
                          Mid.Pos.DistanceTo(anchor.Point + anchor.Normal * _p.HangSurfaceInset) <
                          _p.BackwardsApproachDistance;

        Vector3 intent = DeriveIntent(out float strength);
        bool intentActive = strength > MoveIntentDeadzone && intent.LengthSquared() > 1e-10f;

        ApplyStuckShake(intentActive);

        if (intentActive)
        {
            Vector3 vec = intent * strength;
            if (!Footing)
            {
                vec *= _p.NoFootingMoveFactor; // ≙ MoveTowards 的失稳衰减
            }
            TryObstacleHop(vec, ctx);

            float forceScale =
                LerpMap(CarriedMass, 0f, _p.CarryMassFull, 1f, _p.CarryForceFloor,
                    _p.CarryCurvePower) *
                Mathf.Lerp(1f, _p.StuckForceBoost, StuckShake);
            if (MovingBackwards)
            {
                Tail.Vel += vec * (_p.BackwardForceTail * forceScale);
                Mid.Vel += vec * (_p.BackwardForceMid * forceScale);
                Head.Vel -= vec * (_p.BackwardForceHead * forceScale);
            }
            else
            {
                Head.Vel += vec * (_p.MoveForceHead * forceScale);
                Mid.Vel -= vec * (_p.MoveForceMidBack * forceScale);
                Tail.Vel -= vec * (_p.MoveForceTailBack * forceScale);
            }
            TravelDir = TravelDir.Lerp(intent, _p.TravelDirLerp);
        }
        else if (Footing)
        {
            Sitting = true;
        }

        UpdateRunCycle();
    }

    private Vector3 DeriveIntent(out float strength)
    {
        strength = 0f;
        if (MoveTarget is { } target)
        {
            // 到达迟滞绑定具体目标（秃鹫评审教训：换点即复位，不连环假到达）。
            if ((target - _arriveBoundTarget).LengthSquared() > 1e-6f)
            {
                AtMoveTarget = false;
                _arriveBoundTarget = target;
            }
            Vector3 lead = MovingBackwards ? Tail.Pos : Head.Pos;
            Vector3 d = target - lead;
            float distance = d.Length();
            if (AtMoveTarget)
            {
                if (distance > _p.MoveTargetResumeRadius)
                {
                    AtMoveTarget = false;
                }
            }
            else if (distance < _p.MoveTargetArriveRadius)
            {
                AtMoveTarget = true;
            }
            if (AtMoveTarget || distance < 1e-6f)
            {
                return Vector3.Zero;
            }
            strength = Mathf.Clamp(RunSpeed, 0f, 1f);
            return d / distance;
        }

        AtMoveTarget = false;
        if (MoveDir.LengthSquared() > 1e-10f && RunSpeed > MoveIntentDeadzone)
        {
            strength = Mathf.Clamp(RunSpeed, 0f, 1f);
            return MoveDir.Normalized();
        }
        return Vector3.Zero;
    }

    /// <summary>确定性卡住检测（≙ StuckTracker 记录历史位置的等价物）：有移动意图但
    /// 身体中心在 StuckWindowTicks 窗口内的净位移均速低于阈值则累计；净位移对抖动的
    /// 随机游走钝感，信号在 [RampStart, RampFull] tick 区间内 0→1。</summary>
    private void UpdateStuck()
    {
        bool wantsMove = RunSpeed > MoveIntentDeadzone &&
                         (MoveDir.LengthSquared() > 1e-10f ||
                          (MoveTarget is not null && !AtMoveTarget));
        Vector3 center = (Head.Pos + Mid.Pos + Tail.Pos) / 3f;
        int slot = (int)(_tick % _stuckHistory.Length);
        bool windowFull = _stuckHistoryCount >= _stuckHistory.Length;
        Vector3 oldest = _stuckHistory[slot];
        _stuckHistory[slot] = center;
        if (!windowFull)
        {
            _stuckHistoryCount++;
        }
        float windowSpeed = windowFull
            ? center.DistanceTo(oldest) / _stuckHistory.Length
            : float.MaxValue;
        if (wantsMove && windowSpeed < _p.StuckSpeedThreshold)
        {
            _stuckTicks = Mathf.Min(_stuckTicks + 1, 400);
        }
        else
        {
            _stuckTicks = Mathf.Max(0, _stuckTicks - 4);
        }
        StuckSignal = Mathf.Clamp(
            (float)(_stuckTicks - _p.StuckRampStart) /
            Mathf.Max(1, _p.StuckRampFull - _p.StuckRampStart), 0f, 1f);

        // ≙ stuckShake 的 LerpAndTick 升降参数。
        if (StuckSignal > 0.9f)
        {
            StuckShake = LerpAndTick(StuckShake, 1f, 0.07f, 1f / 70f);
        }
        else if (StuckSignal < 0.2f)
        {
            StuckShake = LerpAndTick(StuckShake, 0f, 0.07f, 0.05f);
        }
    }

    /// <summary>卡住抖动（≙ Act 的 stuckShake 块：pos 与 vel 各加随机方向 ≤5px；
    /// 原作 Random.value/RNV → 整数模数伪随机，逐位确定）。</summary>
    private void ApplyStuckShake(bool intentActive)
    {
        if (!EnableStuckShake || StuckShake <= 0f || !intentActive)
        {
            return;
        }
        for (int i = 0; i < Body.Chunks.Count; i++)
        {
            BodyChunk chunk = Body.Chunks[i];
            float ampV = _p.StuckShakeAmplitude * StuckShake * Pseudo01(_tick, i * 6 + 1);
            float ampP = _p.StuckShakeAmplitude * StuckShake * Pseudo01(_tick, i * 6 + 2);
            chunk.Vel += PseudoUnit(_tick, i * 6 + 3) * ampV;
            chunk.Pos += PseudoUnit(_tick, i * 6 + 4) * ampP;
        }
    }

    /// <summary>越障抬升（≙ MoveTowards 中段）：前进意图强、中段踩地、头落在中段
    /// 后面（推进被挡的涌现签名）→ 头向后上翘起、中段前送，头顶无实心再补一跳。</summary>
    private void TryObstacleHop(Vector3 vec, in TickContext ctx)
    {
        if (!EnableObstacleHop || MovingBackwards || !_midSupported)
        {
            return;
        }
        Vector3 horizontal = vec - Vector3.Up * vec.Dot(Vector3.Up);
        if (horizontal.LengthSquared() <= 0.25f)
        {
            return;
        }
        Vector3 hd = horizontal.Normalized();
        if ((Head.Pos - Mid.Pos).Dot(hd) >= -_p.HopHeadLag)
        {
            return;
        }
        Head.Vel -= hd * _p.HopLateralForce;
        Mid.Vel += hd * _p.HopMidForward;
        HopSerial++;
        if (!ctx.Terrain.Raycast(Head.Pos,
                Head.Pos + Vector3.Up * (Head.Radius + _p.HopCeilingProbe), out _))
        {
            Head.Vel += Vector3.Up * _p.HopRise;
        }
    }

    // ================================================================== 蓄力扑击

    private void ActPounceCharge(in TickContext ctx)
    {
        MovingBackwards = false;
        if (!Footing || AttackTarget is null || CarriedMass > 0f)
        {
            AbandonPounce();
            return;
        }
        DropBugAttackTarget target = AttackTarget.Value;
        Vector3 dir = SafeDirection(target.Point - Head.Pos, Forward);

        // 逐 tick 可及复核（原作只在蓄满的 Attack() 复核；提前复核 = 目标离开即放弃，
        // 等价且响应更快，见文档）。
        if (EnablePounceReachGate &&
            Head.Pos.DistanceTo(target.Point) > PounceReach(dir))
        {
            AbandonPounce();
            return;
        }

        Sitting = true;
        PounceCharge += _p.PounceChargeRate;
        Head.Vel += dir * (_p.PounceHeadForce * PounceCharge * PounceCharge);
        Mid.Vel -= dir * (_p.PounceMidBackForce * PounceCharge);

        if (PounceCharge >= 1f)
        {
            ReleasePounce(target, ctx);
        }
    }

    private void AbandonPounce()
    {
        PounceCharge = 0f;
        PounceAbandonSerial++;
    }

    /// <summary>蓄满弹射（≙ Attack）：目标上方无实心时按距离抬高瞄点；自己头/中上方
    /// 无实心时按距离把方向向上 Slerp；末次可及复核失败则放弃。</summary>
    private void ReleasePounce(in DropBugAttackTarget target, in TickContext ctx)
    {
        Vector3 p = target.Point;
        Vector3 aim = p;
        if (!ctx.Terrain.Raycast(p, p + Vector3.Up * _p.CeilingProbeHeight, out _))
        {
            aim += Vector3.Up * (LerpMap(Head.Pos.DistanceTo(p), _p.AimLiftNear,
                _p.AimLiftFar, 0f, 1f, 1f) * _p.AimLiftMax);
        }
        Vector3 dir = SafeDirection(aim - Head.Pos, Forward);
        if (EnablePounceReachGate && Head.Pos.DistanceTo(p) > PounceReach(dir))
        {
            AbandonPounce();
            return;
        }
        bool headClear = !ctx.Terrain.Raycast(Head.Pos,
            Head.Pos + Vector3.Up * (Head.Radius + _p.CeilingProbeHeight), out _);
        bool midClear = !ctx.Terrain.Raycast(Mid.Pos,
            Mid.Pos + Vector3.Up * (Mid.Radius + _p.CeilingProbeHeight), out _);
        if (headClear && midClear)
        {
            float tilt = LerpMap(Head.Pos.DistanceTo(p), _p.AimLiftNear, _p.AimLiftFar,
                _p.TiltUpNear, _p.TiltUpFar, 1f);
            dir = SafeDirection(dir.Slerp(Vector3.Up, tilt), dir);
        }
        PounceLeapSerial++;
        Jump(dir);
    }

    /// <summary>弹射本体（≙ Jump）：先削当前速度再按方向上扬度施冲量，前段冲量大于中段。</summary>
    private void Jump(Vector3 dir)
    {
        float power = LerpMap(dir.Dot(Vector3.Up), -1f, 1f, _p.JumpPowerDown,
            _p.JumpPowerUp, _p.JumpPowerExp);
        FootingCounter = 0;
        Head.Vel *= _p.DiveVelocityCut;
        Mid.Vel *= _p.DiveVelocityCut;
        Head.Vel += dir * (_p.DiveImpulseHead * power);
        Mid.Vel += dir * (_p.DiveImpulseMid * power);
        PounceCharge = 0f;
        Jumping = true;
    }

    // ================================================================== 通用尾段

    /// <summary>站稳时的前后不对称重力（≙ Footing 块）：前两节强阻尼 + 全额抵消；
    /// 尾节默认只抵消 Lerp(0.5, 1, stuck) 且无阻尼——尾巴因此自然下垂；
    /// 倒退行走时尾节按前节处理（它是领航端）。</summary>
    private void ApplyFootingGravity(in TickContext ctx)
    {
        if (!Footing)
        {
            return;
        }
        Vector3 g = ctx.GravityPerTick;
        Head.Vel *= _p.FrontFootingDamping;
        Head.Vel -= g;
        Mid.Vel *= _p.FrontFootingDamping;
        Mid.Vel -= g;
        if (MovingBackwards || !EnableTailGravityAsymmetry)
        {
            Tail.Vel *= _p.FrontFootingDamping;
            Tail.Vel -= g;
        }
        else
        {
            Tail.Vel -= g * Mathf.Lerp(_p.TailGravityCancelMin, 1f, StuckSignal);
        }
    }

    /// <summary>3D 追加的地面极速钳制（连续胡萝卜无 RW 瞄格中心的天然限速）。
    /// 弹道（扑击/俯冲）、蓄力与悬挂不钳。</summary>
    private void ClampGroundSpeed()
    {
        if (!Footing || Jumping || Diving || PounceCharge > 0f || HangFactor > 0f)
        {
            return;
        }
        Head.Vel = ClampLength(Head.Vel, _p.MaxMoveSpeed);
        Mid.Vel = ClampLength(Mid.Vel, _p.MaxMoveSpeed);
        Tail.Vel = ClampLength(Tail.Vel, _p.MaxMoveSpeed);
    }

    /// <summary>步频驱动：原作为头位移 ≥2px 的 tick 固定 +0.125，本项目按位移比例
    /// （RunCycleStride 米 → RunCycleRate 周期，上限 MaxFactor 倍），静止严格不进。</summary>
    private void UpdateRunCycle()
    {
        float distance = (Head.Pos - Head.LastPos).Length();
        if (distance < _p.RunCycleDeadband)
        {
            return;
        }
        RunCycle += _p.RunCycleRate *
                    Mathf.Min(distance / _p.RunCycleStride, _p.RunCycleMaxFactor);
    }

    private void UpdateFrame()
    {
        Vector3 nf = SafeDirection(Head.Pos - Mid.Pos, Forward);
        Vector3 transported = _transportUp - nf * _transportUp.Dot(nf);
        if (transported.LengthSquared() <= 1e-10f)
        {
            transported = StablePerpendicular(nf, Up);
        }
        transported = transported.Normalized();
        // 地面生物：Up 缓慢回归世界竖直（悬挂头朝下时投影退化，保持运输值）。
        Vector3 worldProjected = Vector3.Up - nf * nf.Dot(Vector3.Up);
        if (worldProjected.LengthSquared() > 1e-6f)
        {
            transported = SafeDirection(
                transported.Lerp(worldProjected.Normalized(), 0.15f), transported);
        }
        Forward = nf;
        Right = SafeDirection(nf.Cross(transported), Right);
        Up = SafeDirection(Right.Cross(nf), transported);
        _transportUp = Up;
    }

    // ================================================================== 表现腿

    /// <summary>纯表现腿更新（≙ DropBugGraphics 腿块的确定性收缩）：步频由 RunCycle
    /// 驱动、失稳 dangle、悬挂时收拢到锚面固定点；落点用一根支撑向射线钉在真实表面。</summary>
    private void UpdateLegs(in TickContext ctx)
    {
        if (!Footing && HangFactor <= 0f)
        {
            _legDangleTicks = _p.LegDangleTicks;
        }
        else if (_legDangleTicks > 0 && (_headSupported || _midSupported || HangFactor > 0f))
        {
            _legDangleTicks = 0;
        }
        else if (_legDangleTicks > 0)
        {
            _legDangleTicks--;
        }

        Vector3 up = Vector3.Up;
        Vector3 heading = Forward - up * Forward.Dot(up);
        heading = SafeDirection(heading, StablePerpendicular(up, Forward));

        foreach (DropBugLeg leg in _legs)
        {
            leg.LastPos = leg.Pos;
            float lift = 0.5f + 0.5f * Mathf.Sin(
                (RunCycle + leg.Index * _p.LegPhaseStep) * Mathf.Pi);

            if (HangFactor > 0.01f && _hangAnchor is { } anchor)
            {
                // ≙ 悬挂时 legs hunt ceilingPos 附近的固定绝对点。
                Vector3 target = anchor.Point +
                                 anchor.TangentU * (leg.Side * (0.14f + leg.Pair * 0.10f)) +
                                 anchor.TangentV * (leg.Pair == 0 ? 0.12f : -0.16f) +
                                 anchor.Normal * 0.02f;
                leg.Vel = Vector3.Zero;
                leg.Pos = leg.Pos.Lerp(target, 0.25f);
                if (!leg.Planted && leg.Pos.DistanceTo(target) < 0.03f)
                {
                    leg.Planted = true;
                    leg.PlantNormal = anchor.Normal;
                    leg.StepSerial++;
                }
                continue;
            }

            // 确定性替代原作 pow(Random.value, 1-0.9·lift) 的权重：lift 高 → 跟得紧。
            Vector3 travelTarget = PounceCharge > 0f ? Vector3.Zero : TravelDir;
            leg.TravelDir = leg.TravelDir.Lerp(travelTarget, Mathf.Lerp(0.1f, 0.9f, lift));

            float fanAngle = Mathf.DegToRad(Mathf.Lerp(_p.LegFanNearDeg, _p.LegFanFarDeg,
                leg.Pair / Mathf.Max(1f, _p.LegPairs - 1f))) * leg.Side;
            Vector3 fan = RotateAround(heading, up, fanAngle);
            Vector3 blend = SafeDirection(
                (leg.TravelDir.LengthSquared() > 1e-8f ? leg.TravelDir : fan).Lerp(fan, 0.1f),
                fan);
            Vector3 idealBase = Head.Pos + blend *
                (_p.LegLength * _p.LegIdealScale * Mathf.Sqrt(Mathf.Max(0f, lift)));

            bool wantDangle = _legDangleTicks > 0 || !Footing;
            if (!wantDangle && leg.Planted)
            {
                float allow = _p.LegLength * _p.LegReachRatio *
                              Mathf.Pow(Mathf.Max(0f, 1f - lift), _p.LegReachShrinkPower);
                if (idealBase.DistanceTo(leg.Pos) > allow)
                {
                    leg.Planted = false; // ≙ 越出可达环 → Dangle 后重新找落点
                }
            }
            if (!wantDangle && !leg.Planted && lift >= 0.1f)
            {
                Vector3 from = idealBase + up * (_p.LegLength * 0.4f);
                Vector3 to = idealBase - up * (_p.LegLength * 0.6f);
                if (ctx.Terrain.Raycast(from, to, out TerrainHit hit) &&
                    hit.Normal.LengthSquared() > 1e-10f &&
                    hit.Normal.Normalized().Dot(up) >= _p.MinGroundDot)
                {
                    leg.Pos = hit.Point;
                    leg.Vel = Vector3.Zero;
                    leg.Planted = true;
                    leg.PlantNormal = hit.Normal.Normalized();
                    leg.StepSerial++;
                }
            }
            if (!leg.Planted)
            {
                Vector3 dangleTarget = idealBase + fan * (_p.LegLength * 0.5f) -
                                       up * (_p.LegLength * 0.25f);
                leg.Vel = leg.Vel * 0.7f + (dangleTarget - leg.Pos) * 0.08f -
                          up * 0.01f;
                leg.Pos += leg.Vel;
                Vector3 fromHead = leg.Pos - Head.Pos;
                if (fromHead.LengthSquared() > _p.LegLength * _p.LegLength)
                {
                    leg.Pos = Head.Pos + fromHead.Normalized() * _p.LegLength;
                    leg.Vel *= 0.5f;
                }
            }
        }
    }

    // ================================================================== 确定性折叠

    /// <summary>状态哈希折叠（smoke 与沙盒共用同一顺序，可互证）。折叠顺序固定：
    /// Body → 姿态帧 → travelDir → 连续标量 → 离散状态 → 逐腿。</summary>
    public void FoldState(DeterminismHasher hasher)
    {
        hasher.FoldBody(Body);
        hasher.Fold(Forward);
        hasher.Fold(Up);
        hasher.Fold(Right);
        hasher.Fold(TravelDir);
        hasher.Fold(HangFactor);
        hasher.Fold(PounceCharge);
        hasher.Fold(RunCycle);
        hasher.Fold(StuckShake);
        hasher.Fold(FootingCounter);
        hasher.Fold(_stuckTicks);
        hasher.Fold(AttackCooldown);
        hasher.Fold(HangRegrabDelay);
        hasher.Fold(Jumping);
        hasher.Fold(Diving);
        hasher.Fold(MovingBackwards);
        hasher.Fold(Sitting);
        hasher.Fold(HangFactor > 0f && _hangAnchor is not null);
        foreach (DropBugLeg leg in _legs)
        {
            hasher.Fold(leg.Pos);
            hasher.Fold(leg.Vel);
            hasher.Fold(leg.Planted);
            hasher.Fold((int)leg.StepSerial);
        }
    }

    // ================================================================== 工具

    private static float LerpMap(float x, float a, float b, float c, float d, float power)
    {
        float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f);
        return Mathf.Lerp(c, d, Mathf.Pow(t, power));
    }

    /// <summary>≙ RWCustom.Custom.LerpAndTick：先按比例 Lerp，再线性步进钳到目标。</summary>
    private static float LerpAndTick(float value, float target, float lerpFactor,
        float tickStep)
    {
        value = Mathf.Lerp(value, target, lerpFactor);
        return Mathf.MoveToward(value, target, tickStep);
    }

    /// <summary>整数模数伪随机 ∈ [0,1)（原作 Random.value 的确定性等价；
    /// 乘数为 Knuth 乘法散列常数，与 tick/通道解耦）。</summary>
    private static float Pseudo01(long tick, int channel)
    {
        long v = (tick * 2654435761L + channel * 40503L) & 0xFFFFFF;
        return v / 16777216f;
    }

    /// <summary>确定性单位方向（原作 Custom.RNV 的等价）。</summary>
    private static Vector3 PseudoUnit(long tick, int channel)
    {
        float azimuth = Pseudo01(tick, channel) * Mathf.Tau;
        float y = Pseudo01(tick, channel + 97) * 2f - 1f;
        float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
        return new Vector3(r * Mathf.Cos(azimuth), y, r * Mathf.Sin(azimuth));
    }

    private static Vector3 RotateAround(Vector3 v, Vector3 axis, float radians)
    {
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return v * cos + axis.Cross(v) * sin + axis * (axis.Dot(v) * (1f - cos));
    }

    private static Vector3 ClampLength(Vector3 value, float limit)
    {
        float lengthSquared = value.LengthSquared();
        if (lengthSquared <= limit * limit || lengthSquared <= 1e-12f)
        {
            return value;
        }
        return value * (limit / Mathf.Sqrt(lengthSquared));
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        if (fallback.LengthSquared() > 1e-10f)
        {
            return fallback.Normalized();
        }
        return Vector3.Forward;
    }

    private static Vector3 StablePerpendicular(Vector3 forward, Vector3 preferred)
    {
        Vector3 projected = preferred - forward * preferred.Dot(forward);
        if (projected.LengthSquared() > 1e-10f)
        {
            return projected.Normalized();
        }
        Vector3 fallback = Mathf.Abs(forward.Dot(Vector3.Right)) < 0.8f
            ? Vector3.Right
            : Vector3.Forward;
        projected = fallback - forward * fallback.Dot(forward);
        return SafeDirection(projected, Vector3.Up);
    }
}
