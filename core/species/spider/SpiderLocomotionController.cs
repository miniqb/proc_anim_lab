using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Spider;

/// <summary>
/// 蜘蛛式运动后端：有序身体链 + 可挂在任意身体节上的多对 <see cref="SpiderLeg"/>。
/// 它与 <see cref="LizardLocomotionController"/> 并列，只共享 Body/地形碰撞原语。
///
/// 地面、斜坡、墙、角落与天花板没有模式枚举：真实足端各自寻找可达表面，
/// 抓握法线汇总为 SupportNormal；抓稳后关闭重力，失去足够抓点后恢复重力。
/// 身体总推进按抓地比例计算，再按各锚点的抓地贡献归一化分配，腿或身体节增多不会增速。
/// </summary>
public sealed class SpiderLocomotionController
{
    public const float MoveIntentDeadzone = 0.1f;

    public readonly Body Body;
    public readonly IReadOnlyList<BodyChunk> Segments;
    public readonly BodyChunk Primary;
    public readonly BodyChunk Rear;
    public readonly List<SpiderLeg> Legs = new();

    public Vector3 MoveDir;
    public float RunSpeed;
    public Vector3? MoveTarget;
    public float MoveTargetArriveRadius = 0.4f;
    public bool AtMoveTarget { get; private set; }

    public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
        && (MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

    public float BaseSpeed = 0.055f;
    public float MaxMoveSpeed = 0.08f;
    public float NoGripSpeed = 0.1f;
    public float LookAhead = 0.55f;
    public float RideHeight = 0.22f;

    public int MinGroundedLegs = 2;
    public int MinPlantedLegs = 4;
    public int RegainFootingTicks = 8;
    public int LoseGripTicks = 8;
    public int GaitPhaseTicks = 8;
    public float SupportBlend = 0.2f;
    public float TrailingGain = 0.18f;

    public float FootedAirFriction = 0.82f;
    public float FootedSurfaceFriction = 0.55f;
    public float AirborneAirFriction = 0.995f;
    public float AirborneSurfaceFriction = 0.35f;

    public int StallReleaseTicks = 18;
    public float StallSpeed = 0.006f;
    public int StallTicks { get; private set; }

    public int LegsGripping { get; private set; }
    public int NoGripCounter { get; private set; }
    public int FootingCounter { get; private set; }
    public bool ApplyGravity { get; private set; } = true;
    public Vector3 SupportNormal { get; private set; } = Vector3.Up;

    public MoveTargetKind LastMoveTargetKind { get; private set; }
    public Vector3 LastMoveTarget { get; private set; }
    public Vector3 Forward => _forward;

    private readonly float[] _linkLengths;
    private readonly int[] _gripsPerSegment;
    private bool[] _stepPermits = System.Array.Empty<bool>();
    private Vector3 _forward = Vector3.Forward;

    private const float HardStepUrgency = 1.05f;

    public SpiderLocomotionController(Body body, IReadOnlyList<BodyChunk> segments)
    {
        if (segments.Count < 2)
        {
            throw new System.ArgumentException("Spider body requires at least two ordered segments.", nameof(segments));
        }

        Body = body;
        Segments = segments;
        Primary = segments[0];
        Rear = segments[segments.Count - 1];
        _gripsPerSegment = new int[segments.Count];
        _linkLengths = new float[segments.Count - 1];

        for (int i = 0; i < _linkLengths.Length; i++)
        {
            float found = 0f;
            foreach (ChunkConnection connection in body.Connections)
            {
                if ((connection.A == segments[i] && connection.B == segments[i + 1])
                    || (connection.B == segments[i] && connection.A == segments[i + 1]))
                {
                    found = connection.RestLength;
                    break;
                }
            }
            _linkLengths[i] = found > 0f
                ? found
                : (segments[i + 1].Pos - segments[i].Pos).Length();
        }

        Vector3 initialForward = segments[0].Pos - segments[1].Pos;
        if (initialForward.LengthSquared() > 1e-10f)
        {
            _forward = initialForward.Normalized();
        }
    }

    /// <summary>
    /// 世界整体平移：保留速度、抓握与步态相位；所有位置态（含膝、抓点与直喂目标）同步移动。
    /// </summary>
    public void Shift(Vector3 delta)
    {
        Body.Shift(delta);
        foreach (SpiderLeg leg in Legs)
        {
            leg.Shift(delta);
        }
        if (MoveTarget is { } target)
        {
            MoveTarget = target + delta;
        }
        LastMoveTarget += delta;
    }

    /// <summary>地形不随身体移动的瞬移：平移后作废抓点、路径点和站稳状态。</summary>
    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        foreach (SpiderLeg leg in Legs)
        {
            leg.ForceRelease();
        }
        ResetSupportState();
        MoveTarget = null;
        AtMoveTarget = false;
        LastMoveTargetKind = MoveTargetKind.None;
    }

    /// <summary>统一冲量注入：所有身体节获得同一速度，腿松手，重力立即恢复。</summary>
    public void Launch(Vector3 velocityPerTick)
    {
        foreach (BodyChunk chunk in Body.Chunks)
        {
            chunk.Vel += velocityPerTick;
        }
        foreach (SpiderLeg leg in Legs)
        {
            leg.ForceRelease();
        }
        ResetSupportState();
    }

    /// <summary>
    /// 固定 tick：读取上 tick 抓地 → 更新支撑/推进 → 身体物理 → 足端/IK → 汇总下一 tick 支撑。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        Vector3 worldUp = ctx.GravityPerTick.LengthSquared() > 1e-12f
            ? -ctx.GravityPerTick.Normalized()
            : Vector3.Up;

        bool derivedMove = MoveTarget is not null;
        if (MoveTarget is { } target)
        {
            DeriveMoveFromTarget(target);
        }
        else
        {
            AtMoveTarget = false;
        }

        UpdateFooting();
        Vector3 effectiveMove = HasMoveIntent ? RedirectMove(worldUp) : Vector3.Zero;
        UpdateForward(effectiveMove, worldUp);
        ApplyLocomotionForce(ctx, effectiveMove);
        ApplyTrailingPose();
        Body.Tick(ctx);

        StallTicks = HasMoveIntent && Primary.Vel.Length() < StallSpeed
            ? StallTicks + 1
            : 0;
        if (StallTicks >= StallReleaseTicks)
        {
            ReleaseOldestLeg();
            StallTicks = 0;
        }

        TickLegs(ctx, effectiveMove);
        UpdateSupportNormal(worldUp);

        if (derivedMove)
        {
            MoveDir = Vector3.Zero;
        }
    }

    private void DeriveMoveFromTarget(Vector3 target)
    {
        Vector3 carrot = target + SupportNormal * RideHeight;
        AtMoveTarget = (carrot - Primary.Pos).Length() <= MoveTargetArriveRadius;
        MoveDir = AtMoveTarget ? Vector3.Zero : Dir(Primary.Pos, carrot);
    }

    private void UpdateFooting()
    {
        int gripping = 0;
        foreach (SpiderLeg leg in Legs)
        {
            if (leg.Gripping)
            {
                gripping++;
            }
        }
        LegsGripping = gripping;

        if (gripping >= MinGroundedLegs)
        {
            NoGripCounter = 0;
            FootingCounter = Mathf.Min(100, FootingCounter + 1);
        }
        else
        {
            if (NoGripCounter < int.MaxValue)
            {
                NoGripCounter++;
            }
            FootingCounter = Mathf.Max(0, FootingCounter - 2);
        }

        ApplyGravity = FootingCounter < RegainFootingTicks || NoGripCounter > LoseGripTicks;
        Body.GravityScale = ApplyGravity ? 1f : 0f;
        Body.AirFriction = ApplyGravity ? AirborneAirFriction : FootedAirFriction;
        Body.SurfaceFriction = ApplyGravity ? AirborneSurfaceFriction : FootedSurfaceFriction;
    }

    private Vector3 RedirectMove(Vector3 worldUp)
    {
        if (MoveDir == Vector3.Zero)
        {
            return Vector3.Zero;
        }

        Vector3 normal = SupportNormal;
        float into = -MoveDir.Dot(normal);
        if (into <= 0.01f)
        {
            return MoveDir.Normalized();
        }

        Vector3 uphill = worldUp - normal * worldUp.Dot(normal);
        if (uphill.LengthSquared() < 1e-8f)
        {
            return MoveDir.Normalized();
        }

        Vector3 along = MoveDir + normal * into;
        Vector3 redirected = along + uphill.Normalized() * into;
        return redirected.LengthSquared() < 1e-8f ? MoveDir.Normalized() : redirected.Normalized();
    }

    private void UpdateForward(Vector3 effectiveMove, Vector3 worldUp)
    {
        Vector3 desired = effectiveMove;
        if (desired.LengthSquared() < 1e-8f)
        {
            desired = Segments[0].Pos - Segments[1].Pos;
        }

        desired -= SupportNormal * desired.Dot(SupportNormal);
        if (desired.LengthSquared() < 1e-8f)
        {
            desired = _forward - SupportNormal * _forward.Dot(SupportNormal);
        }
        if (desired.LengthSquared() < 1e-8f)
        {
            Vector3 fallback = Mathf.Abs(SupportNormal.Dot(worldUp)) < 0.9f
                ? worldUp
                : Vector3.Forward;
            desired = fallback - SupportNormal * fallback.Dot(SupportNormal);
        }

        desired = desired.Normalized();
        _forward = BlendDirection(
            _forward,
            desired,
            HasMoveIntent ? 0.25f : 0.08f,
            SupportNormal.Cross(_forward));
    }

    private void ApplyLocomotionForce(in TickContext ctx, Vector3 effectiveMove)
    {
        LastMoveTargetKind = MoveTargetKind.None;
        if (!HasMoveIntent || effectiveMove.LengthSquared() < 1e-8f)
        {
            return;
        }

        System.Array.Clear(_gripsPerSegment, 0, _gripsPerSegment.Length);
        int totalGrips = 0;
        foreach (SpiderLeg leg in Legs)
        {
            if (!leg.Gripping)
            {
                continue;
            }
            int segment = SegmentIndexOf(leg.Anchor);
            if (segment >= 0)
            {
                _gripsPerSegment[segment]++;
                totalGrips++;
            }
        }

        float gripRatio = Legs.Count == 0 ? 1f : (float)totalGrips / Legs.Count;
        float frameSpeed = BaseSpeed
            * Mathf.Lerp(NoGripSpeed, 1f, gripRatio)
            * Mathf.Clamp(RunSpeed, 0f, 1f);
        Vector3 target = FindMoveTarget(ctx, effectiveMove, out MoveTargetKind kind);
        LastMoveTargetKind = kind;
        LastMoveTarget = target;

        Vector3 driveDirection = Dir(Primary.Pos, target);
        if (totalGrips == 0)
        {
            AddCappedVelocity(Primary, driveDirection, frameSpeed);
            return;
        }

        for (int i = 0; i < Segments.Count; i++)
        {
            int count = _gripsPerSegment[i];
            if (count == 0)
            {
                continue;
            }
            float share = count / (float)totalGrips;
            AddCappedVelocity(Segments[i], driveDirection, frameSpeed * share);
        }
    }

    private void ApplyTrailingPose()
    {
        if (TrailingGain <= 0f)
        {
            return;
        }

        for (int i = 1; i < Segments.Count; i++)
        {
            BodyChunk previous = Segments[i - 1];
            BodyChunk current = Segments[i];
            Vector3 radialRaw = current.Pos - previous.Pos;
            float radius = radialRaw.Length();
            if (radius < 1e-6f)
            {
                continue;
            }

            Vector3 radial = radialRaw / radius;
            Vector3 desiredDirection = -_forward;
            Vector3 desired = previous.Pos + desiredDirection * _linkLengths[i - 1];
            Vector3 correction = desired - current.Pos;
            correction -= radial * correction.Dot(radial);
            float magnitude = correction.Length();
            float escapeFloor = _linkLengths[i - 1] * 0.05f;
            if (magnitude < escapeFloor && radial.Dot(desiredDirection) < -0.95f)
            {
                if (magnitude >= 1e-6f)
                {
                    // 近反折保留原扰动决定的旋转侧，只给它一个能在有限 tick 内离开
                    // 不稳定平衡点的最小切向速度。
                    correction *= escapeFloor / magnitude;
                }
                else
                {
                    // 精确 180° 反折没有可沿用的旋转侧；按节索引给唯一固定手性。
                    float hand = (i & 1) == 0 ? -1f : 1f;
                    correction = SupportNormal.Cross(radial) * hand;
                    if (correction.LengthSquared() < 1e-10f)
                    {
                        correction = radial.Cross(Vector3.Forward) * hand;
                    }
                    if (correction.LengthSquared() < 1e-10f)
                    {
                        correction = radial.Cross(Vector3.Right) * hand;
                    }
                    correction = correction.Normalized() * escapeFloor;
                }
                magnitude = correction.Length();
            }
            if (magnitude < 1e-6f)
            {
                continue;
            }

            Vector3 direction = correction / magnitude;
            float targetSpeed = Mathf.Min(magnitude * TrailingGain, MaxMoveSpeed * 0.75f);
            float missing = targetSpeed - current.Vel.Dot(direction);
            if (missing > 0f)
            {
                current.Vel += direction * missing;
            }
        }
    }

    private void TickLegs(in TickContext ctx, Vector3 effectiveMove)
    {
        int phaseTicks = Mathf.Max(1, GaitPhaseTicks);
        int activePhase = (int)((ctx.TickIndex / phaseTicks) & 1L);
        Vector3 fallbackForward =
            effectiveMove.LengthSquared() > 1e-8f ? effectiveMove : _forward;
        foreach (SpiderLeg leg in Legs)
        {
            Vector3 localForward = AnchorForward(leg.Anchor, fallbackForward);
            leg.PrepareTickFrame(localForward, SupportNormal);
        }

        int plantedForGait = 0;
        foreach (SpiderLeg leg in Legs)
        {
            if (leg.PlantedForGait)
            {
                plantedForGait++;
            }
        }

        if (_stepPermits.Length != Legs.Count)
        {
            _stepPermits = new bool[Legs.Count];
        }
        else
        {
            System.Array.Clear(_stepPermits, 0, _stepPermits.Length);
        }

        int releaseBudget = HasMoveIntent
            ? Mathf.Max(0, plantedForGait - MinPlantedLegs)
            : 0;
        for (int permit = 0; permit < releaseBudget; permit++)
        {
            int best = -1;
            bool bestHard = false;
            float bestUrgency = float.NegativeInfinity;
            for (int i = 0; i < Legs.Count; i++)
            {
                SpiderLeg leg = Legs[i];
                if (_stepPermits[i] || !leg.Gripping)
                {
                    continue;
                }
                float urgency = leg.StepUrgency;
                if (urgency < 1f)
                {
                    continue;
                }
                bool hard = urgency >= HardStepUrgency;
                bool phaseMatches = (leg.PhaseGroup & 1) == activePhase;
                if (!phaseMatches && !hard)
                {
                    continue;
                }
                if (best < 0
                    || (hard && !bestHard)
                    || (hard == bestHard && urgency > bestUrgency + 1e-6f))
                {
                    best = i;
                    bestHard = hard;
                    bestUrgency = urgency;
                }
            }
            if (best < 0)
            {
                break;
            }
            _stepPermits[best] = true;
        }

        int gripping = 0;
        for (int i = 0; i < Legs.Count; i++)
        {
            SpiderLeg leg = Legs[i];
            bool canGiveUpPlant = HasMoveIntent && _stepPermits[i];
            leg.TickPrepared(ctx,
                HasMoveIntent ? RunSpeed : 0f, canGiveUpPlant, Legs);
            if (leg.Gripping)
            {
                gripping++;
            }
        }

        LegsGripping = gripping;
    }

    /// <summary>
    /// 把腿根局部 X 轴绑定到其实际锚点所在的线性链段，而不是全局移动意图。
    /// 首/中节取“本节背离下一节”，链尾取“前一节指向本节”的反向；因此链条弯曲时
    /// 各节上的腿根、扇形与 bend pole 会随自己的身体节旋转。
    /// </summary>
    private Vector3 AnchorForward(BodyChunk anchor, Vector3 fallback)
    {
        int index = SegmentIndexOf(anchor);
        Vector3 local = index switch
        {
            < 0 => fallback,
            var i when i < Segments.Count - 1 => Segments[i].Pos - Segments[i + 1].Pos,
            _ => Segments[^2].Pos - Segments[^1].Pos,
        };
        return local.LengthSquared() < 1e-10f ? fallback : local.Normalized();
    }

    private void UpdateSupportNormal(Vector3 worldUp)
    {
        Vector3 sum = Vector3.Zero;
        float weight = 0f;
        foreach (SpiderLeg leg in Legs)
        {
            if (leg.GripCounter <= 0 || leg.GripNormal.LengthSquared() < 1e-10f)
            {
                continue;
            }
            // 抓稳后每条腿等权：若按抓点年龄加权，旧墙面上的长驻腿会在外角
            // 数十倍压过刚抓到新面的前腿，把 SupportNormal 锁死在旧面。
            // 抓稳前只按确认进度渐入，仍保持真实命中才有贡献。
            float w = Mathf.Min(1f,
                leg.GripCounter / (float)System.Math.Max(1, leg.GripDelay));
            sum += leg.GripNormal.Normalized() * w;
            weight += w;
        }

        Vector3 target;
        if (weight > 0f && sum.LengthSquared() > 1e-8f)
        {
            target = sum.Normalized();
        }
        else if (NoGripCounter > LoseGripTicks)
        {
            target = worldUp;
        }
        else
        {
            return;
        }

        SupportNormal = BlendDirection(
            SupportNormal,
            target,
            SupportBlend,
            _forward);
    }

    private Vector3 FindMoveTarget(in TickContext ctx, Vector3 effectiveMove,
        out MoveTargetKind kind)
    {
        if (MoveTarget is { } fed)
        {
            kind = MoveTargetKind.External;
            Vector3 fedCarrot = fed + SupportNormal * RideHeight;
            return Primary.Pos + effectiveMove * (fedCarrot - Primary.Pos).Length();
        }

        Vector3 ahead = Primary.Pos + effectiveMove * LookAhead;
        Vector3 normal = SupportNormal;
        if (ctx.Terrain.Raycast(ahead + normal * 0.4f, ahead - normal * 0.8f, out TerrainHit hit)
            && hit.Normal.LengthSquared() > 1e-12f)
        {
            kind = MoveTargetKind.Support;
            return hit.Point + hit.Normal.Normalized() * RideHeight;
        }

        kind = MoveTargetKind.Fallback;
        return ahead;
    }

    private void ReleaseOldestLeg()
    {
        SpiderLeg? oldestConfirmed = null;
        SpiderLeg? oldestCandidate = null;
        int oldestCandidateTicks = -1;
        foreach (SpiderLeg leg in Legs)
        {
            if (leg.GripCounter > (oldestConfirmed?.GripCounter ?? 0))
            {
                oldestConfirmed = leg;
            }
            else if (leg.GripCounter == 0 && leg.HasGrip
                && leg.AcquisitionTicks > oldestCandidateTicks)
            {
                // 顶死可能发生在所有候选都尚未踩实的阶段。严格 > 保留装配顺序作为
                // 同龄候选的确定性 tie-break；否则旧实现找不到 GripCounter>0 就无人松手。
                oldestCandidate = leg;
                oldestCandidateTicks = leg.AcquisitionTicks;
            }
        }
        (oldestConfirmed ?? oldestCandidate)?.ForceRelease();
    }

    private void ResetSupportState()
    {
        FootingCounter = 0;
        NoGripCounter = LoseGripTicks < int.MaxValue ? LoseGripTicks + 1 : int.MaxValue;
        LegsGripping = 0;
        ApplyGravity = true;
        SupportNormal = Vector3.Up;
        StallTicks = 0;
        Body.GravityScale = 1f;
    }

    private void AddCappedVelocity(BodyChunk chunk, Vector3 direction, float amount)
    {
        float headroom = MaxMoveSpeed - chunk.Vel.Dot(direction);
        if (headroom > 0f)
        {
            chunk.Vel += direction * Mathf.Min(amount, headroom);
        }
    }

    private int SegmentIndexOf(BodyChunk chunk)
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            if (Segments[i] == chunk)
            {
                return i;
            }
        }
        return -1;
    }

    private static Vector3 Dir(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        return delta.LengthSquared() < 1e-12f ? Vector3.Forward : delta.Normalized();
    }

    /// <summary>
    /// 方向低通的确定性反平行保护。普通情况保持既有 normalized-lerp；精确或近 180°
    /// 时先朝指定切向中继一步，避免权重小于 0.5 时零和/原方向归一化造成永久死锁。
    /// </summary>
    private static Vector3 BlendDirection(
        Vector3 current, Vector3 target, float weight, Vector3 antipodalTangent)
    {
        current = current.LengthSquared() < 1e-12f ? target : current.Normalized();
        target = target.LengthSquared() < 1e-12f ? current : target.Normalized();
        Vector3 destination = target;
        if (current.Dot(target) < -0.999f)
        {
            Vector3 tangent = antipodalTangent
                - current * antipodalTangent.Dot(current);
            if (tangent.LengthSquared() < 1e-10f)
            {
                Vector3 axis = Mathf.Abs(current.Dot(Vector3.Up)) < 0.9f
                    ? Vector3.Up
                    : Vector3.Right;
                tangent = axis - current * axis.Dot(current);
            }
            destination = tangent.Normalized();
        }

        Vector3 blended = current.Lerp(destination, Mathf.Clamp(weight, 0f, 1f));
        return blended.LengthSquared() < 1e-12f ? destination : blended.Normalized();
    }
}
