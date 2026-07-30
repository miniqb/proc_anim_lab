using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

public enum CicadaMode
{
    Flying,
    Landing,
    Perched,
    Stunned,
}

public enum CicadaChargePhase
{
    None,
    Windup,
    Dash,
}

/// <summary>显式指定的静态停驻面；Forward 始终位于表面切平面内。</summary>
public readonly struct CicadaPerchTarget
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly Vector3 Forward;
    public readonly ulong ColliderId;

    public CicadaPerchTarget(Vector3 point, Vector3 normal, Vector3 forward, ulong colliderId)
    {
        Point = point;
        Normal = normal;
        Forward = forward;
        ColliderId = colliderId;
    }

    internal CicadaPerchTarget Shifted(Vector3 delta) =>
        new(Point + delta, Normal, Forward, ColliderId);
}

/// <summary>
/// RW Cicada 思路的 3D 独立后端：双 chunk 飞行体、显式停驻、蓄力冲撞以及纯表现附肢。
/// 它与 <see cref="LizardLocomotionController"/> 平行，共享 Body/地形原语但不复用腿或步态。
/// Tick 顺序刻意是 Body.Tick（上轮 Act 的结果）→ 读取接触/状态 → 为下一 tick 注入力。
/// </summary>
public sealed class CicadaLocomotionController
{
    public readonly Body Body;
    public readonly BodyChunk Front;
    public readonly BodyChunk Rear;
    public readonly List<CicadaWingState> Wings = new(4);
    public readonly List<CicadaTentacleState> Tentacles = new(4);

    // 宿主输入。
    public Vector3 MoveDir;
    public float RunSpeed;
    public Vector3? MoveTarget;
    public bool AtMoveTarget { get; private set; }

    // 状态输出。
    public CicadaMode Mode { get; private set; } = CicadaMode.Flying;
    public CicadaChargePhase ChargePhase { get; private set; }
    public int ChargeCounter { get; private set; }
    public float FlightPower { get; private set; }
    public CicadaPerchTarget? ActivePerchTarget { get; private set; }
    public CicadaPerchTarget? ActivePerch => ActivePerchTarget;

    // 纯向量姿态；完整 Basis 留给 Godot 表现层。
    public Vector3 Forward { get; private set; }
    public Vector3 Up { get; private set; }
    public Vector3 Right { get; private set; }
    public float Bank { get; private set; }
    public float WingDeployment { get; private set; }
    public float FlapPhase { get; private set; }

    private Vector3 _transportUp;
    private Vector3 _steeringDirection;
    private Vector3 _chargeDirection;
    private readonly CicadaParams _parameters;
    private readonly float _massResponse;
    private Vector3? _hoverAnchor;
    private int _perchSettledTicks;
    private int _perchIntentTicks;
    private int _stunnedTicks;
    private int _hoverCaptureDelay;

    internal CicadaLocomotionController(
        Body body,
        BodyChunk front,
        BodyChunk rear,
        CicadaParams parameters,
        Vector3 initialForward)
    {
        Body = body;
        Front = front;
        Rear = rear;
        _parameters = parameters;
        _massResponse = 0.55f / parameters.BodyMass;
        Forward = SafeDirection(initialForward, Vector3.Forward);
        _transportUp = StablePerpendicular(Forward, Vector3.Up);
        Right = SafeDirection(Forward.Cross(_transportUp), Vector3.Right);
        Up = SafeDirection(Right.Cross(Forward), _transportUp);
        _transportUp = Up;
        _steeringDirection = Forward;
        _hoverAnchor = Center;
        FlapPhase = parameters.InitialFlapPhase - Mathf.Floor(parameters.InitialFlapPhase);
        WingDeployment = 0f;

        for (int pair = 0; pair < 2; pair++)
        {
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                int side = sideIndex == 0 ? -1 : 1;
                Wings.Add(new CicadaWingState(
                    side,
                    pair,
                    pair * 0.53f + (side > 0 ? 0.08f : 0f)));
                Tentacles.Add(new CicadaTentacleState(
                    side,
                    pair,
                    parameters.TentacleLength * (pair == 0 ? 1f : 0.88f)));
            }
        }
        ResetAppendages();
    }

    private Vector3 Center => (Front.Pos + Rear.Pos) * 0.5f;
    private Vector3 CenterVelocity => (Front.Vel + Rear.Vel) * 0.5f;

    /// <summary>
    /// 请求贴到宿主已经选择的表面。普通碰撞不会调用它，因此不会意外自动落地。
    /// </summary>
    public bool RequestPerch(in TerrainHit hit)
    {
        if (hit.Normal.LengthSquared() <= 1e-10f)
        {
            return false;
        }

        Vector3 normal = hit.Normal.Normalized();
        Vector3 tangent = Forward - normal * Forward.Dot(normal);
        if (tangent.LengthSquared() <= 1e-8f)
        {
            tangent = Up - normal * Up.Dot(normal);
        }
        tangent = SafeDirection(tangent, StablePerpendicular(normal, Vector3.Up));

        ActivePerchTarget = new CicadaPerchTarget(hit.Point, normal, tangent, hit.ColliderId);
        Mode = CicadaMode.Landing;
        ChargePhase = CicadaChargePhase.None;
        ChargeCounter = 0;
        _stunnedTicks = 0;
        _perchSettledTicks = 0;
        _perchIntentTicks = 0;
        _hoverAnchor = null;
        Body.GravityScale = 0f;
        return true;
    }

    /// <summary>从任意停驻姿态复飞；传入方向会与表面法线合成，避免起飞后重新撞面。</summary>
    public void TakeOff(Vector3 direction)
    {
        Vector3 surfaceNormal = ActivePerchTarget?.Normal ?? Vector3.Zero;
        Vector3 takeoff = direction + surfaceNormal;
        takeoff = SafeDirection(takeoff, surfaceNormal.LengthSquared() > 1e-10f
            ? surfaceNormal
            : Up);

        ClearPerch();
        Mode = CicadaMode.Flying;
        FlightPower = 0.5f;
        Front.Vel += takeoff * (_parameters.FrontTakeOffImpulse * _massResponse);
        Rear.Vel += takeoff * (_parameters.RearTakeOffImpulse * _massResponse);
        _steeringDirection = takeoff;
        _hoverAnchor = null;
        _hoverCaptureDelay = 40;
        Body.GravityScale = 1f;
    }

    /// <summary>锁定方向启动 38 tick 的 Windup/Dash 时间线。</summary>
    public bool TryStartCharge(Vector3 direction)
    {
        if (Mode != CicadaMode.Flying || ChargePhase != CicadaChargePhase.None ||
            direction.LengthSquared() <= 1e-10f)
        {
            return false;
        }

        _chargeDirection = direction.Normalized();
        _steeringDirection = _chargeDirection;
        _hoverAnchor = null;
        ChargeCounter = 1;
        ChargePhase = CicadaChargePhase.Windup;
        return true;
    }

    /// <summary>整体世界原点平移；所有世界坐标状态与附肢插值记忆同步移动。</summary>
    public void Shift(Vector3 delta)
    {
        Body.Shift(delta);
        if (MoveTarget is { } target)
        {
            MoveTarget = target + delta;
        }
        if (_hoverAnchor is { } hover)
        {
            _hoverAnchor = hover + delta;
        }
        if (ActivePerchTarget is { } perch)
        {
            ActivePerchTarget = perch.Shifted(delta);
        }
        foreach (CicadaWingState wing in Wings)
        {
            wing.Shift(delta);
        }
        foreach (CicadaTentacleState tentacle in Tentacles)
        {
            tentacle.Shift(delta);
        }
    }

    /// <summary>位置连续地瞬移，但作废目标、停驻、Charge 与所有附肢历史。</summary>
    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        ClearTransientState();
        FlightPower = 0f;
        _hoverCaptureDelay = 0;
        ResetAppendages();
    }

    /// <summary>给两块身体注入同一速度增量并进入自由飞行；不篡改注入值。</summary>
    public void Launch(Vector3 velocityPerTick)
    {
        Front.Vel += velocityPerTick;
        Rear.Vel += velocityPerTick;
        ClearTransientState();
        // 外部击飞先保留一小段弹道；否则无输入锚会在首 tick 就把宿主冲量刹停。
        _hoverCaptureDelay = 90;
        ResetAppendages();
    }

    /// <summary>
    /// 固定 tick 入口。Body 首先消费上 tick 的力，之后才由本控制器根据新接触生成下一 tick 的力。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        ConfigureBodyForCurrentMode();
        Vector3 preFrontVelocity = Front.Vel;
        Vector3 preRearVelocity = Rear.Vel;
        Body.Tick(ctx);

        if (Mode is CicadaMode.Landing or CicadaMode.Perched)
        {
            if (!ValidatePerch(ctx.Terrain))
            {
                ResumeFlightFromLostPerch();
            }
        }

        HandleTerrainImpact(preFrontVelocity, preRearVelocity);
        UpdateChargeTimeline();

        switch (Mode)
        {
            case CicadaMode.Landing:
            case CicadaMode.Perched:
                ActPerch();
                break;
            case CicadaMode.Stunned:
                ActStunned();
                break;
            default:
                ActFlight(ctx);
                break;
        }

        UpdateFrame();
        UpdateAppendages();
    }

    private void ConfigureBodyForCurrentMode()
    {
        Body.EnablePostCollisionStructureRecovery = false;
        switch (Mode)
        {
            case CicadaMode.Landing:
            case CicadaMode.Perched:
                Body.GravityScale = 0f;
                Body.AirFriction = 1f;
                Body.SurfaceFriction = 0.25f;
                break;
            case CicadaMode.Stunned:
                Body.GravityScale = 1f;
                Body.AirFriction = 0.985f;
                Body.SurfaceFriction = 0.45f;
                break;
            default:
                Body.GravityScale = 1f;
                Body.AirFriction = 1f;
                Body.SurfaceFriction = 0.55f;
                break;
        }
    }

    private void ActFlight(in TickContext ctx)
    {
        FlightPower = Mathf.MoveToward(FlightPower, 1f, _parameters.FlightPowerRise);

        Front.Vel *= Mathf.Lerp(1f, _parameters.FrontFlightDamping, FlightPower);
        Rear.Vel *= Mathf.Lerp(1f, _parameters.RearFlightDamping, FlightPower);

        Vector3 gravityUp = ctx.GravityPerTick.LengthSquared() > 1e-12f
            ? -ctx.GravityPerTick.Normalized()
            : _transportUp;
        Front.Vel += gravityUp * (_parameters.FrontLift * FlightPower * _massResponse);
        Rear.Vel += gravityUp * (_parameters.RearLift * FlightPower * _massResponse);

        Vector3 desired = DeriveFlightIntent(out Vector3 positionCorrection);
        if (ChargePhase == CicadaChargePhase.None && desired.LengthSquared() > 1e-10f)
        {
            desired = ApplyAvoidance(ctx.Terrain, desired);
        }

        if (ChargePhase == CicadaChargePhase.None)
        {
            float speed = Mathf.Clamp(RunSpeed, 0f, 1f) * FlightPower;
            Front.Vel += desired * (_parameters.FrontSteer * speed * _massResponse);
            Rear.Vel += desired * (_parameters.RearSteer * speed * _massResponse);
            Front.Vel += positionCorrection * _massResponse;
            Rear.Vel += positionCorrection * _massResponse;
            if (desired.LengthSquared() > 1e-10f)
            {
                _steeringDirection = desired;
            }
            AlignBodyToward(_steeringDirection, 0.026f * _massResponse);
            ClampBodySpeed(_parameters.MaxFlightSpeed);
        }
        else
        {
            ActCharge();
            AlignBodyToward(
                _chargeDirection,
                (ChargePhase == CicadaChargePhase.Dash ? 0.05f : 0.035f) * _massResponse);
            ClampBodySpeed(ChargePhase == CicadaChargePhase.Dash
                ? _parameters.ChargeMaxSpeed
                : _parameters.MaxFlightSpeed);
        }
    }

    private Vector3 DeriveFlightIntent(out Vector3 positionCorrection)
    {
        Vector3 center = Center;
        Vector3 velocity = CenterVelocity;
        positionCorrection = Vector3.Zero;

        if (MoveTarget is { } target)
        {
            _hoverCaptureDelay = 0;
            Vector3 error = target - center;
            float distance = error.Length();
            AtMoveTarget = distance <= _parameters.MoveTargetArriveRadius;
            _hoverAnchor = null;
            if (AtMoveTarget)
            {
                positionCorrection = ClampLength(
                    error * _parameters.HoverGain - velocity * _parameters.HoverDamping,
                    _parameters.HoverMaxCorrection);
                return Vector3.Zero;
            }

            // 越接近目标，越早用位置阻尼收速；到达后继续保持目标而不是清掉锚点。
            if (distance < 1.1f)
            {
                positionCorrection = ClampLength(
                    error * (_parameters.HoverGain * 0.55f) -
                    velocity * (_parameters.HoverDamping * 0.8f),
                    _parameters.HoverMaxCorrection);
            }
            return error / distance;
        }

        AtMoveTarget = false;
        if (MoveDir.LengthSquared() > 1e-10f && RunSpeed > 1e-4f)
        {
            _hoverCaptureDelay = 0;
            _hoverAnchor = null;
            return MoveDir.Normalized();
        }

        if (_hoverCaptureDelay > 0)
        {
            _hoverCaptureDelay--;
            return Vector3.Zero;
        }
        _hoverAnchor ??= center;
        Vector3 holdError = _hoverAnchor.Value - center;
        positionCorrection = ClampLength(
            holdError * _parameters.HoverGain - velocity * _parameters.HoverDamping,
            _parameters.HoverMaxCorrection);
        return Vector3.Zero;
    }

    private Vector3 ApplyAvoidance(ITerrainQuery terrain, Vector3 desired)
    {
        Vector3 center = Center;
        Vector3 correction = Vector3.Zero;
        // 显式展开固定扇序，避免每个 40Hz tick 为五根探针分配数组。
        AccumulateAvoidance(terrain, center, desired, ref correction);
        AccumulateAvoidance(
            terrain,
            center,
            SafeDirection(desired + Right * 0.42f, desired),
            ref correction);
        AccumulateAvoidance(
            terrain,
            center,
            SafeDirection(desired - Right * 0.42f, desired),
            ref correction);
        AccumulateAvoidance(
            terrain,
            center,
            SafeDirection(desired + Up * 0.30f, desired),
            ref correction);
        AccumulateAvoidance(
            terrain,
            center,
            SafeDirection(desired - Up * 0.30f, desired),
            ref correction);
        return SafeDirection(desired + correction, desired);
    }

    private void AccumulateAvoidance(
        ITerrainQuery terrain,
        Vector3 center,
        Vector3 probe,
        ref Vector3 correction)
    {
        Vector3 to = center + probe * _parameters.AvoidanceProbeDistance;
        if (!terrain.Raycast(center, to, out TerrainHit hit) ||
            hit.Normal.LengthSquared() <= 1e-10f)
        {
            return;
        }
        float distance = center.DistanceTo(hit.Point);
        float weight = 1f - Mathf.Clamp(
            distance / _parameters.AvoidanceProbeDistance,
            0f,
            1f);
        correction += hit.Normal.Normalized() * (weight * _parameters.AvoidanceGain);
    }

    private void AlignBodyToward(Vector3 desiredForward, float gain)
    {
        if (desiredForward.LengthSquared() <= 1e-10f)
        {
            return;
        }
        Vector3 current = SafeDirection(Front.Pos - Rear.Pos, Forward);
        Vector3 desired = desiredForward.Normalized();
        Vector3 turn = desired - current * desired.Dot(current);
        if (turn.LengthSquared() <= 1e-10f && desired.Dot(current) < -0.95f)
        {
            turn = Right;
        }
        turn = ClampLength(turn, 1f) * gain;
        Front.Vel += turn;
        Rear.Vel -= turn;
    }

    private void ActCharge()
    {
        if (ChargePhase == CicadaChargePhase.Windup)
        {
            Vector3 targetVelocity = -_chargeDirection * _parameters.ChargeWindupBackstep;
            Vector3 delta = ClampLength(targetVelocity - CenterVelocity, 0.018f);
            Front.Vel += delta;
            Rear.Vel += delta;
            return;
        }
        if (ChargePhase == CicadaChargePhase.Dash && ChargeCounter >= 22)
        {
            Front.Vel += _chargeDirection * (_parameters.ChargeThrust * _massResponse);
            Rear.Vel += _chargeDirection * (_parameters.ChargeThrust * _massResponse);
        }
    }

    private void UpdateChargeTimeline()
    {
        if (ChargePhase == CicadaChargePhase.None || Mode != CicadaMode.Flying)
        {
            return;
        }

        ChargeCounter++;
        if (ChargeCounter <= 20)
        {
            ChargePhase = CicadaChargePhase.Windup;
        }
        else if (ChargeCounter <= 38)
        {
            ChargePhase = CicadaChargePhase.Dash;
        }
        else
        {
            ChargeCounter = 0;
            ChargePhase = CicadaChargePhase.None;
            _hoverAnchor = Center;
        }
    }

    private void HandleTerrainImpact(Vector3 preFrontVelocity, Vector3 preRearVelocity)
    {
        if (Mode != CicadaMode.Flying)
        {
            return;
        }

        Vector3 normal = Vector3.Zero;
        if (Front.TerrainContact && Front.ContactNormal.LengthSquared() > 1e-10f)
        {
            normal += Front.ContactNormal.Normalized();
        }
        if (Rear.TerrainContact && Rear.ContactNormal.LengthSquared() > 1e-10f)
        {
            normal += Rear.ContactNormal.Normalized();
        }
        if (normal.LengthSquared() <= 1e-10f)
        {
            return;
        }
        normal = normal.Normalized();

        Vector3 incoming = (preFrontVelocity + preRearVelocity) * 0.5f;
        float inwardSpeed = Mathf.Max(0f, -incoming.Dot(normal));
        if (ChargePhase == CicadaChargePhase.Dash &&
            inwardSpeed >= _parameters.ChargeHardImpactSpeed)
        {
            ChargePhase = CicadaChargePhase.None;
            ChargeCounter = 0;
            Mode = CicadaMode.Stunned;
            // 本 tick 末已经可观察为 Stunned；多放 1，抵消同 tick ActStunned 的首次递减，
            // 让输出连续恰好保持配置的 StunTicks 个 tick。
            _stunnedTicks = _parameters.StunTicks + 1;
            _hoverAnchor = null;
            return;
        }

        // 低速静态地形撞击反射后继续飞；Body 的共用碰撞语义原本是无反弹，
        // Cicada 的飞行手感在控制器层显式补回轻微回弹。
        if (inwardSpeed > 1e-5f)
        {
            Vector3 reflected = incoming - normal * (1.45f * incoming.Dot(normal));
            Vector3 correction = reflected - CenterVelocity;
            Front.Vel += correction;
            Rear.Vel += correction;
            _hoverAnchor = null;
        }
    }

    private void ActStunned()
    {
        FlightPower = Mathf.MoveToward(FlightPower, 0f, _parameters.FlightPowerFall);
        AtMoveTarget = false;
        _stunnedTicks--;
        if (_stunnedTicks <= 0)
        {
            Mode = CicadaMode.Flying;
            FlightPower = Mathf.Max(FlightPower, 0.25f);
            _hoverAnchor = Center;
        }
    }

    private bool ValidatePerch(ITerrainQuery terrain)
    {
        if (ActivePerchTarget is not { } perch)
        {
            return false;
        }
        Vector3 from = perch.Point + perch.Normal * _parameters.PerchValidationDistance;
        Vector3 to = perch.Point - perch.Normal * _parameters.PerchValidationDistance;
        return terrain.Raycast(from, to, out TerrainHit hit) &&
               hit.Normal.LengthSquared() > 1e-10f &&
               hit.ColliderId == perch.ColliderId &&
               hit.Normal.Normalized().Dot(perch.Normal) > 0.8f;
    }

    private void ActPerch()
    {
        if (ActivePerchTarget is not { } perch)
        {
            ResumeFlightFromLostPerch();
            return;
        }

        FlightPower = Mathf.MoveToward(FlightPower, 0f, _parameters.FlightPowerFall);
        AtMoveTarget = false;
        Vector3 centerTarget = perch.Point + perch.Normal * _parameters.PerchClearance;
        float halfLength = _parameters.BodyLength * 0.5f;
        Vector3 frontTarget = centerTarget + perch.Forward * halfLength;
        Vector3 rearTarget = centerTarget - perch.Forward * halfLength;
        float gain = Mode == CicadaMode.Perched
            ? _parameters.PerchedGain
            : _parameters.LandingGain;
        float damping = Mode == CicadaMode.Perched
            ? _parameters.PerchedDamping
            : _parameters.LandingDamping;
        Front.Vel += ClampLength(
            (frontTarget - Front.Pos) * gain - Front.Vel * damping,
            _parameters.PerchMaxCorrection);
        Rear.Vel += ClampLength(
            (rearTarget - Rear.Pos) * gain - Rear.Vel * damping,
            _parameters.PerchMaxCorrection);

        float maxError = Mathf.Max(
            Front.Pos.DistanceTo(frontTarget),
            Rear.Pos.DistanceTo(rearTarget));
        if (Mode == CicadaMode.Landing)
        {
            bool frameAligned = Up.Dot(perch.Normal) >= 0.985f;
            if (maxError <= _parameters.PerchSettleDistance &&
                CenterVelocity.Length() <= _parameters.PerchSettleSpeed &&
                frameAligned)
            {
                _perchSettledTicks++;
                if (_perchSettledTicks >= _parameters.PerchSettleTicks)
                {
                    Mode = CicadaMode.Perched;
                    Front.Vel = Vector3.Zero;
                    Rear.Vel = Vector3.Zero;
                }
            }
            else
            {
                _perchSettledTicks = 0;
            }
        }

        bool hasIntent = RunSpeed > 1e-3f &&
            (MoveDir.LengthSquared() > 1e-8f ||
             (MoveTarget is { } target &&
              target.DistanceSquaredTo(Center) >
              _parameters.MoveTargetArriveRadius * _parameters.MoveTargetArriveRadius));
        // “停驻后持续 30 tick”从真正进入 Perched 才计；Landing 期间预按输入不能
        // 偷跑计数，否则尚未贴稳或刚贴稳几 tick 就会被自动起飞取消。
        _perchIntentTicks = Mode == CicadaMode.Perched && hasIntent
            ? _perchIntentTicks + 1
            : 0;
        if (_perchIntentTicks >= _parameters.AutoTakeOffTicks)
        {
            Vector3 direction = MoveDir.LengthSquared() > 1e-10f
                ? MoveDir.Normalized()
                : perch.Normal;
            TakeOff(direction);
        }
    }

    private void ResumeFlightFromLostPerch()
    {
        Vector3 normal = ActivePerchTarget?.Normal ?? Up;
        ClearPerch();
        Mode = CicadaMode.Flying;
        FlightPower = Mathf.Max(FlightPower, 0.5f);
        Front.Vel += normal * 0.035f;
        Rear.Vel += normal * 0.035f;
        _hoverAnchor = null;
        _hoverCaptureDelay = 0;
    }

    private void ClearPerch()
    {
        ActivePerchTarget = null;
        _perchSettledTicks = 0;
        _perchIntentTicks = 0;
    }

    private void ClearTransientState()
    {
        MoveTarget = null;
        AtMoveTarget = false;
        ClearPerch();
        ChargePhase = CicadaChargePhase.None;
        ChargeCounter = 0;
        _stunnedTicks = 0;
        Mode = CicadaMode.Flying;
        Body.GravityScale = 1f;
        _hoverAnchor = null;
    }

    private void ClampBodySpeed(float limit)
    {
        Front.Vel = ClampLength(Front.Vel, limit);
        Rear.Vel = ClampLength(Rear.Vel, limit);
    }

    private void UpdateFrame()
    {
        Vector3 nextForward = SafeDirection(Front.Pos - Rear.Pos, Forward);

        // 上一 tick 的无 bank Up 投影到新 forward 的正交平面，相当于最小旋转的平行输运。
        Vector3 transported = _transportUp - nextForward * _transportUp.Dot(nextForward);
        if (transported.LengthSquared() <= 1e-10f)
        {
            transported = StablePerpendicular(nextForward, Up);
        }
        transported = transported.Normalized();

        // 停驻的 roll 不是任意的：Up 必须成为表面外法线，这样 Right 与 Forward 都留在
        // 切平面内。RotateTowardsInPlane 对顶面法线与旧 Up 恰好反向的 180° 情形也选定
        // 固定旋转侧，不会在垂直/反平行处发生 roll 翻转。
        if (ActivePerchTarget is { } perch &&
            Mode is CicadaMode.Landing or CicadaMode.Perched)
        {
            Vector3 surfaceUp = perch.Normal -
                                nextForward * perch.Normal.Dot(nextForward);
            surfaceUp = SafeDirection(surfaceUp, StablePerpendicular(nextForward, transported));
            transported = RotateTowardsInPlane(
                transported,
                surfaceUp,
                nextForward,
                0.38f);
        }

        Vector3 baseRight = SafeDirection(nextForward.Cross(transported), Right);
        transported = SafeDirection(baseRight.Cross(nextForward), transported);
        _transportUp = transported;

        float lateral = _steeringDirection.Dot(baseRight);
        float targetBank = -Mathf.Clamp(lateral, -1f, 1f) * _parameters.BankMaxRadians;
        bool suppressBank = Mode is CicadaMode.Landing or CicadaMode.Perched or CicadaMode.Stunned;
        if (suppressBank)
        {
            targetBank = 0f;
        }
        Bank = suppressBank
            ? Mathf.MoveToward(Bank, 0f, 0.18f)
            : Mathf.Lerp(Bank, targetBank, _parameters.BankResponse);

        float cos = Mathf.Cos(Bank);
        float sin = Mathf.Sin(Bank);
        Forward = nextForward;
        Up = SafeDirection(transported * cos + baseRight * sin, transported);
        Right = SafeDirection(nextForward.Cross(Up), baseRight);
        Up = SafeDirection(Right.Cross(nextForward), Up);
    }

    private void UpdateAppendages()
    {
        float targetDeployment = Mode switch
        {
            CicadaMode.Perched => 0.08f,
            CicadaMode.Landing => 0.32f,
            CicadaMode.Stunned => 0.18f,
            _ when ChargePhase == CicadaChargePhase.Windup => 0.12f,
            _ when ChargePhase == CicadaChargePhase.Dash => 0.92f,
            _ => Mathf.Clamp(0.25f + FlightPower * 0.75f, 0f, 1f),
        };
        WingDeployment = Mathf.MoveToward(WingDeployment, targetDeployment, 0.12f);
        float flapRate = Mathf.Max(0.12f, FlightPower) /
                         Mathf.Max(1f, _parameters.WingPeriodTicks);
        FlapPhase += flapRate;
        FlapPhase -= Mathf.Floor(FlapPhase);

        Vector3 center = Center;
        for (int i = 0; i < Wings.Count; i++)
        {
            CicadaWingState wing = Wings[i];
            wing.LastPos = wing.Pos;
            wing.LastTip = wing.Tip;
            float pairBack = wing.Pair == 0 ? 0.08f : -0.10f;
            float side = wing.Side;
            Vector3 root = center + Forward * pairBack + Right * side * 0.075f + Up * 0.03f;
            float phase = (FlapPhase + wing.PhaseOffset) * Mathf.Tau;
            float flap = Mode == CicadaMode.Perched
                ? 0f
                : Mathf.Sin(phase) * (0.10f + 0.32f * WingDeployment);
            float length = _parameters.WingLength * (wing.Pair == 0 ? 1f : 0.82f);
            Vector3 tip = root +
                          Right * side * length * Mathf.Lerp(0.18f, 1f, WingDeployment) -
                          Forward * length * (wing.Pair == 0 ? 0.08f : 0.32f) +
                          Up * (flap * length);
            wing.Pos = root;
            wing.Tip = tip;
        }

        for (int i = 0; i < Tentacles.Count; i++)
        {
            CicadaTentacleState tentacle = Tentacles[i];
            tentacle.LastPos = tentacle.Pos;
            float side = tentacle.Side;
            Vector3 bodyPoint = tentacle.Pair == 0 ? Front.Pos : Rear.Pos;
            tentacle.Anchor = bodyPoint +
                               Right * side * (tentacle.Pair == 0 ? 0.075f : 0.065f) -
                               Up * 0.035f;
            Vector3 desired;
            if (ActivePerchTarget is { } perch &&
                Mode is CicadaMode.Landing or CicadaMode.Perched)
            {
                desired = perch.Point +
                          perch.Forward * (tentacle.Pair == 0 ? 0.13f : -0.13f) +
                          Right * side * 0.11f +
                          perch.Normal * 0.012f;
                tentacle.Attached = Mode == CicadaMode.Perched;
            }
            else
            {
                desired = tentacle.Anchor -
                          Up * tentacle.Length * (tentacle.Pair == 0 ? 0.52f : 0.38f) -
                          Forward * tentacle.Length * (tentacle.Pair == 0 ? 0.48f : 0.70f) +
                          Right * side * tentacle.Length * 0.20f;
                tentacle.Attached = false;
            }
            tentacle.Vel = tentacle.Vel * 0.76f + (desired - tentacle.Pos) * 0.18f;
            tentacle.Pos += tentacle.Vel;
            Vector3 fromAnchor = tentacle.Pos - tentacle.Anchor;
            if (fromAnchor.LengthSquared() > tentacle.Length * tentacle.Length)
            {
                tentacle.Pos = tentacle.Anchor + fromAnchor.Normalized() * tentacle.Length;
                tentacle.Vel *= 0.45f;
            }
            if (tentacle.Attached && ActivePerchTarget is { } attachedPerch)
            {
                float signedDistance = (tentacle.Pos - attachedPerch.Point)
                    .Dot(attachedPerch.Normal);
                if (signedDistance < 0.006f)
                {
                    tentacle.Pos += attachedPerch.Normal * (0.006f - signedDistance);
                }
                float inwardVelocity = tentacle.Vel.Dot(attachedPerch.Normal);
                if (inwardVelocity < 0f)
                {
                    tentacle.Vel -= attachedPerch.Normal * inwardVelocity;
                }
            }
        }
    }

    private void ResetAppendages()
    {
        Vector3 center = Center;
        foreach (CicadaWingState wing in Wings)
        {
            float side = wing.Side;
            Vector3 root = center + Forward * (wing.Pair == 0 ? 0.08f : -0.10f) +
                           Right * side * 0.075f + Up * 0.03f;
            Vector3 tip = root + Right * side * _parameters.WingLength * 0.22f -
                          Forward * _parameters.WingLength * (wing.Pair == 0 ? 0.08f : 0.28f);
            wing.Reset(root, tip);
        }
        foreach (CicadaTentacleState tentacle in Tentacles)
        {
            Vector3 bodyPoint = tentacle.Pair == 0 ? Front.Pos : Rear.Pos;
            Vector3 anchor = bodyPoint + Right * tentacle.Side * 0.07f - Up * 0.035f;
            Vector3 pos = anchor - Up * tentacle.Length * 0.45f -
                          Forward * tentacle.Length * 0.45f;
            tentacle.Reset(anchor, pos);
        }
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

    private static Vector3 RotateTowardsInPlane(
        Vector3 current,
        Vector3 target,
        Vector3 planeNormal,
        float maxRadians)
    {
        float dot = Mathf.Clamp(current.Dot(target), -1f, 1f);
        if (dot >= 0.999999f)
        {
            return target;
        }

        Vector3 tangent = dot <= -0.999999f
            ? SafeDirection(planeNormal.Cross(current), StablePerpendicular(planeNormal, current))
            : SafeDirection(target - current * dot, planeNormal.Cross(current));
        float step = Mathf.Min(Mathf.Acos(dot), maxRadians);
        return SafeDirection(
            current * Mathf.Cos(step) + tangent * Mathf.Sin(step),
            target);
    }
}
