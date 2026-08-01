using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Centipede;

/// <summary>
/// 蜈蚣足端：抓取真实地形点并按确定性行波在摆动/支撑间切换。
/// 足端本身不作为刚性约束反拉身体；抓握可信度由控制器用于本节抗重力、贴面与推进。
/// </summary>
public sealed class CentipedeLeg
{
    private static readonly float[] ProbeOffsets = { 0f, -0.18f, 0.18f };
    private const float TerrainSkin = 0.005f;
    private const int TerrainBarrierResetTicks = 4;
    private const int GripVisibilityCheckStride = 4;

    public readonly CentipedeSegment Anchor;
    public readonly int Side;
    public readonly int PairIndex;
    public readonly float Radius;
    public readonly float Length;
    public readonly float Lateral;
    public readonly float Stride;

    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public Vector3 GripPoint;
    public Vector3 GripNormal = Vector3.Up;
    public ulong GripColliderId;
    public float Phase { get; private set; }
    public bool IsSwinging { get; private set; } = true;
    public bool HasGrip { get; private set; }
    public int GripCounter { get; private set; }
    public bool Gripping => HasGrip && GripCounter > 0 && GripCounter >= GripDelay;
    /// <summary>足端被地形隔在锚点另一侧后，累计执行的确定性穿墙复位次数。</summary>
    public int TerrainBarrierRecoveries { get; private set; }

    public float HuntSpeed;
    public float Quickness;
    public int GripDelay;

    private bool _wasStance;
    private bool _retriedInvalidGrip;
    private bool _hasPlannedGrip;
    private Vector3 _plannedGripPoint;
    private Vector3 _plannedGripNormal = Vector3.Up;
    private ulong _plannedGripColliderId;
    // 决定第几个 tick 复位，属于未来行为状态；FoldDeterministicState 必须包含它。
    private int _terrainBarrierTicks;

    public CentipedeLeg(CentipedeSegment anchor, CentipedeSegmentParams p,
        int side, int pairIndex, Vector3 initialPos)
    {
        Anchor = anchor;
        Side = side;
        PairIndex = pairIndex;
        Radius = p.FootRadius;
        Length = p.LegLength;
        Lateral = p.LegLateral * (1f + pairIndex * 0.18f);
        Stride = p.LegStride;
        HuntSpeed = p.LegHuntSpeed;
        Quickness = p.LegQuickness;
        GripDelay = p.LegGripDelay;
        Pos = initialPos;
        LastPos = initialPos;
        GripPoint = initialPos;
        _plannedGripPoint = initialPos;
    }

    public void Tick(in TickContext ctx, float phase, float stanceFraction, float runSpeed)
    {
        Phase = phase - Mathf.Floor(phase);
        bool stance = Phase < stanceFraction;
        bool enteringStance = stance && !_wasStance;
        bool enteringSwing = !stance && _wasStance;
        _wasStance = stance;
        IsSwinging = !stance;

        Vector3 forward = SafeNormal(Anchor.Forward, Vector3.Right);
        Vector3 normal = SafeNormal(Anchor.SupportNormal, Vector3.Up);
        Vector3 side = SafeNormal(Anchor.Side, forward.Cross(normal));

        if (enteringSwing)
        {
            HasGrip = false;
            GripCounter = 0;
            _retriedInvalidGrip = false;
            FindPlannedGrip(ctx, forward, side, normal);
        }
        else if (!stance)
        {
            HasGrip = false;
            GripCounter = 0;
        }
        else if (enteringStance)
        {
            _retriedInvalidGrip = false;
            if (_hasPlannedGrip && PlannedGripStillReachable()
                && GripPointIsVisible(ctx, _plannedGripPoint, _plannedGripNormal))
            {
                ApplyPlannedGrip();
            }
            else
            {
                FindGrip(ctx, forward, side, normal);
            }
        }
        else if (HasGrip && ShouldCheckGripVisibility(ctx.TickIndex)
            && !GripPointIsVisible(ctx, GripPoint, GripNormal))
        {
            // 已种下的脚也可能在身体绕过薄墙后留在另一侧；若步态正好停在 stance，
            // 单靠下一次 swing 永远不会释放。错峰低频 LOS 给出直接的隔墙证据后立即复位。
            ResetAcrossTerrainBarrier();
            return;
        }
        else if (HasGrip && !GripStillReachable())
        {
            // 身体把已种下的脚拖出可达圈：这是一次真实抓点失效事件，允许立即重搜；
            // 搜不到也只尝试这一次，不能在余下 stance 每 tick 扫完整探针带。
            HasGrip = false;
            GripCounter = 0;
            if (!_retriedInvalidGrip)
            {
                FindGrip(ctx, forward, side, normal);
                _retriedInvalidGrip = true;
            }
        }

        Vector3 target;
        if (stance && HasGrip)
        {
            // GripPoint 是地形表面点；足端粒子是有半径的球，中心必须停在表面外。
            // 直接追表面点会让球心穿进 collider，再依赖 MTD 每 tick 推出，深嵌时甚至
            // 可能从错误一侧弹出。把球心目标显式放在抓握法线外侧才是稳定的 plant。
            target = GripPoint + GripNormal * (Radius + TerrainSkin);
        }
        else
        {
            float swingT = stanceFraction >= 0.999f
                ? 1f
                : Mathf.Clamp((Phase - stanceFraction) / (1f - stanceFraction), 0f, 1f);
            if (!stance && _hasPlannedGrip && PlannedGripStillReachable())
            {
                Vector3 plannedCenter = _plannedGripPoint
                    + _plannedGripNormal * (Radius + TerrainSkin);
                target = Pos.Lerp(plannedCenter, Mathf.Clamp(0.2f + swingT * 0.8f, 0f, 1f))
                    + _plannedGripNormal
                    * (Mathf.Sin(swingT * Mathf.Pi) * Length * 0.28f);
            }
            else
            {
                float along = Mathf.Lerp(-Stride * 0.5f, Stride * 0.5f, swingT);
                target = Anchor.Chunk.Pos
                    + side * (Side * Lateral)
                    + forward * along
                    - normal * (Length * 0.72f)
                    + normal * (Mathf.Sin(swingT * Mathf.Pi) * Length * 0.28f);
            }
        }

        LastPos = Pos;
        Vector3 toTarget = target - Pos;
        float speed = HuntSpeed + Anchor.Chunk.Vel.Length();
        Vector3 desired = toTarget.LengthSquared() <= speed * speed
            ? toTarget
            : toTarget.Normalized() * speed;
        Vel = Vel.Lerp(desired, Quickness);
        Pos += Vel;
        Vel *= 0.78f;

        ConstrainToAnchor();
        bool blockedAcrossBarrier = SweepAndProjectOutOfTerrain(ctx, normal, target);
        blockedAcrossBarrier |= PushOutOfTerrain(ctx, target);
        if (blockedAcrossBarrier)
        {
            if (++_terrainBarrierTicks >= TerrainBarrierResetTicks)
            {
                ResetAcrossTerrainBarrier();
            }
        }
        else
        {
            _terrainBarrierTicks = 0;
        }

        Vector3 gripCenter = GripPoint + GripNormal * (Radius + TerrainSkin);
        if (stance && HasGrip && Pos.DistanceTo(gripCenter) <= 0.035f)
        {
            GripCounter++;
        }
        else
        {
            GripCounter = 0;
        }
    }

    public void Shift(Vector3 delta)
    {
        Pos += delta;
        LastPos += delta;
        GripPoint += delta;
        _plannedGripPoint += delta;
    }

    public void ForceRelease()
    {
        HasGrip = false;
        GripCounter = 0;
        IsSwinging = true;
        _wasStance = false;
        _retriedInvalidGrip = false;
        _hasPlannedGrip = false;
        _plannedGripPoint = Pos;
        _plannedGripNormal = SafeNormal(Anchor.SupportNormal, Vector3.Up);
        _plannedGripColliderId = 0;
        GripColliderId = 0;
        _terrainBarrierTicks = 0;
    }

    /// <summary>把包含预落点/相位门的完整步态状态按固定顺序折入公共确定性哈希。</summary>
    public void FoldDeterministicState(DeterminismHasher hasher)
    {
        hasher.Fold(Pos);
        hasher.Fold(LastPos);
        hasher.Fold(Vel);
        hasher.Fold(GripPoint);
        hasher.Fold(GripNormal);
        hasher.FoldOpaqueId(GripColliderId);
        hasher.Fold(Phase);
        hasher.Fold(IsSwinging);
        hasher.Fold(HasGrip);
        hasher.Fold(GripCounter);
        hasher.Fold(_wasStance);
        hasher.Fold(_retriedInvalidGrip);
        hasher.Fold(_hasPlannedGrip);
        hasher.Fold(_plannedGripPoint);
        hasher.Fold(_plannedGripNormal);
        hasher.FoldOpaqueId(_plannedGripColliderId);
        hasher.Fold(_terrainBarrierTicks);
    }

    public bool DeterministicStateIsFinite =>
        Pos.IsFinite() && LastPos.IsFinite() && Vel.IsFinite()
        && GripPoint.IsFinite() && GripNormal.IsFinite() && float.IsFinite(Phase)
        && _plannedGripPoint.IsFinite() && _plannedGripNormal.IsFinite();

    private bool GripStillReachable() =>
        GripPoint.DistanceTo(Anchor.Chunk.Pos) <= Length + Radius + 0.05f;

    private bool PlannedGripStillReachable() =>
        _plannedGripPoint.DistanceTo(Anchor.Chunk.Pos) <= Length + Radius + 0.05f;

    private void FindPlannedGrip(in TickContext ctx, Vector3 forward,
        Vector3 side, Vector3 normal)
    {
        if (TryFindGrip(ctx, forward, side, normal, out TerrainHit hit))
        {
            _plannedGripPoint = hit.Point;
            _plannedGripNormal = hit.Normal.Normalized();
            _plannedGripColliderId = hit.ColliderId;
            _hasPlannedGrip = true;
        }
        else
        {
            _hasPlannedGrip = false;
            _plannedGripColliderId = 0;
        }
    }

    private void ApplyPlannedGrip()
    {
        GripPoint = _plannedGripPoint;
        GripNormal = _plannedGripNormal;
        GripColliderId = _plannedGripColliderId;
        HasGrip = true;
        _hasPlannedGrip = false;
        _plannedGripColliderId = 0;
    }

    private void FindGrip(in TickContext ctx, Vector3 forward, Vector3 side, Vector3 normal)
    {
        if (TryFindGrip(ctx, forward, side, normal, out TerrainHit hit))
        {
            GripPoint = hit.Point;
            GripNormal = hit.Normal.Normalized();
            GripColliderId = hit.ColliderId;
            HasGrip = true;
        }
        else
        {
            HasGrip = false;
            GripColliderId = 0;
        }
    }

    private bool TryFindGrip(in TickContext ctx, Vector3 forward, Vector3 side,
        Vector3 normal, out TerrainHit result)
    {
        Vector3 desired = Anchor.Chunk.Pos
            + side * (Side * Lateral)
            + forward * (Stride * 0.5f)
            - normal * (Length * 0.70f);
        float best = float.MaxValue;
        TerrainHit bestHit = default;
        bool found = false;
        TickContext gripContext = ctx;

        void Consider(in TerrainHit hit)
        {
            if (!hit.Point.IsFinite() || !hit.Normal.IsFinite()
                || hit.Normal.LengthSquared() < 1e-10f
                || hit.Point.DistanceTo(Anchor.Chunk.Pos) > Length + Radius
                || !GripPointIsVisible(gripContext, hit.Point, hit.Normal))
            {
                return;
            }
            float score = hit.Point.DistanceSquaredTo(desired);
            if (score < best)
            {
                best = score;
                bestHit = hit;
                found = true;
            }
        }

        if (ctx.Terrain.Raycast(Anchor.Chunk.Pos,
            desired + (desired - Anchor.Chunk.Pos).Normalized() * Radius, out TerrainHit direct))
        {
            Consider(direct);
        }
        foreach (float offset in ProbeOffsets)
        {
            Vector3 probe = desired + forward * offset;
            if (ctx.Terrain.Raycast(probe + normal * (Length * 0.45f),
                probe - normal * (Length * 0.75f), out TerrainHit hit))
            {
                Consider(hit);
            }
        }

        result = bestHit;
        return found;
    }

    private void ConstrainToAnchor()
    {
        Vector3 delta = Pos - Anchor.Chunk.Pos;
        float distance = delta.Length();
        if (distance <= Length || distance < 1e-8f)
        {
            return;
        }
        Pos = Anchor.Chunk.Pos + delta / distance * Length;
        float outward = Vel.Dot(delta / distance);
        if (outward > 0f)
        {
            Vel -= delta / distance * outward;
        }
    }

    /// <summary>
    /// 足端速度可高于脚半径，单看候选点的球体 MTD 会允许中心在一个 tick 内穿到碰撞体
    /// 另一侧。先扫上一位置→候选位置拦住跨面，再沿本节支撑法线检查一个脚半径带，
    /// 处理「中心尚未越面但球壳已穿入」的情形。HitFromInside 的零法线不是可用表面，
    /// 绝不据它猜方向；角落/出生嵌入仍交给后续球体 MTD。
    /// </summary>
    private bool SweepAndProjectOutOfTerrain(in TickContext ctx, Vector3 supportNormal,
        Vector3 target)
    {
        bool blockedAcrossBarrier = false;
        if ((Pos - LastPos).LengthSquared() > 1e-10f
            && ctx.Terrain.Raycast(LastPos, Pos, out TerrainHit swept)
            && TrySurfaceNormal(swept, out Vector3 sweptNormal)
            && (Pos - LastPos).Dot(sweptNormal) < 0f)
        {
            blockedAcrossBarrier = BarrierSeparatesAnchorAndTarget(
                swept.Point, sweptNormal, target);
            Pos = swept.Point + sweptNormal * (Radius + TerrainSkin);
            RemoveVelocityInto(sweptNormal);
        }

        Vector3 probeNormal = SafeNormal(supportNormal, Anchor.SupportNormal);
        float reach = Radius + TerrainSkin;
        Vector3 from = Pos + probeNormal * reach;
        Vector3 to = Pos - probeNormal * reach;
        if (!ctx.Terrain.Raycast(from, to, out TerrainHit projected)
            || !TrySurfaceNormal(projected, out Vector3 surfaceNormal))
        {
            return blockedAcrossBarrier;
        }

        float signedDistance = (Pos - projected.Point).Dot(surfaceNormal);
        if (signedDistance >= reach)
        {
            return blockedAcrossBarrier;
        }
        blockedAcrossBarrier |= BarrierSeparatesAnchorAndTarget(
            projected.Point, surfaceNormal, target);
        Pos += surfaceNormal * (reach - signedDistance);
        RemoveVelocityInto(surfaceNormal);
        return blockedAcrossBarrier;
    }

    /// <summary>
    /// 足端不是 Body 的连接节点，不能使用 Body.ReleaseSnags。连续被同一类“锚点和目标都在
    /// 墙后”的运动扫掠挡住时，采用 RW BodyPart.Reset 同类语义：脚端直接叠回锚点并清掉
    /// 旧抓点/速度。LastPos 同步，下一 tick 不会把这次有意传送再次当作穿墙运动。
    /// </summary>
    private void ResetAcrossTerrainBarrier()
    {
        GripNormal = SafeNormal(Anchor.SupportNormal, Vector3.Up);
        // 默认脚小于身体球，重叠锚点天然位于支撑面外；自定义大脚则沿锚点支撑法线补足
        // 半径差，避免“穿回正确侧”后短暂嵌进锚点当前支撑面。
        float supportOffset = Mathf.Max(
            0f, Radius + TerrainSkin - Anchor.Chunk.TerrainRadius);
        Pos = Anchor.Chunk.Pos + GripNormal * supportOffset;
        LastPos = Pos;
        Vel = Anchor.Chunk.Vel;
        GripPoint = Pos;
        GripColliderId = 0;
        HasGrip = false;
        GripCounter = 0;
        IsSwinging = true;
        _wasStance = false;
        _retriedInvalidGrip = false;
        _hasPlannedGrip = false;
        _plannedGripPoint = Pos;
        _plannedGripNormal = GripNormal;
        _plannedGripColliderId = 0;
        _terrainBarrierTicks = 0;
        TerrainBarrierRecoveries++;
    }

    private bool PushOutOfTerrain(in TickContext ctx, Vector3 target)
    {
        bool blockedAcrossBarrier = false;
        // 墙角可能同时重叠两个 collider/面；一次 MTD 只保证离开当前最先返回的那一个，
        // 位移还可能轻微进入另一面。固定上限迭代收敛到共同可行区，顺序确定且不制造
        // 与节数相关的无界查询。
        for (int iteration = 0; iteration < 4; iteration++)
        {
            if (!ctx.Terrain.SpherePenetration(Pos, Radius,
                    out Vector3 pushDir, out float depth)
                || pushDir.LengthSquared() < 1e-10f || depth <= 0f)
            {
                break;
            }
            Vector3 n = pushDir.Normalized();
            // 球壳可能每 tick 侵入后就被 MTD 推回，脚中心从未越过碰撞面，因而运动射线
            // 看不到阻挡。由 MTD 反推接触面点，把这种低速/大脚卡墙也计入同一恢复门。
            Vector3 surfacePoint = Pos + n * (depth - Radius);
            blockedAcrossBarrier |= BarrierSeparatesAnchorAndTarget(
                surfacePoint, n, target);
            Pos += n * depth;
            float into = Vel.Dot(n);
            if (into < 0f)
            {
                Vel -= n * into;
            }
        }
        return blockedAcrossBarrier;
    }

    private void RemoveVelocityInto(Vector3 normal)
    {
        float into = Vel.Dot(normal);
        if (into < 0f)
        {
            Vel -= normal * into;
        }
    }

    private bool BarrierSeparatesAnchorAndTarget(Vector3 point, Vector3 normal,
        Vector3 target)
    {
        Vector3 n = SafeNormal(normal, Anchor.SupportNormal);
        float clearance = Radius + TerrainSkin;
        return (Anchor.Chunk.Pos - point).Dot(n) < -clearance
            && (target - point).Dot(n) < -TerrainSkin;
    }

    /// <summary>
    /// 抓点查询本身可能从墙后发射（例如侧向探针落在薄墙另一侧），所以仅靠腿长不能证明
    /// 可达。候选足球中心应与锚点直线可见；终点位于表面外一个足半径，任何射线命中都代表
    /// 中间有实体阻断。只在找点/正式落脚事件查询，不增加每 tick 全腿扫描。
    /// </summary>
    private bool GripPointIsVisible(in TickContext ctx, Vector3 point, Vector3 normal)
    {
        if (!point.IsFinite() || !normal.IsFinite() || normal.LengthSquared() < 1e-10f)
        {
            return false;
        }
        Vector3 center = point + normal.Normalized() * (Radius + TerrainSkin);
        return !ctx.Terrain.Raycast(Anchor.Chunk.Pos, center, out _);
    }

    private bool ShouldCheckGripVisibility(long tickIndex)
    {
        long lane = Anchor.Index * 3L + PairIndex * 2L + (Side > 0 ? 1L : 0L);
        return ((tickIndex + lane) & (GripVisibilityCheckStride - 1L)) == 0L;
    }

    private static bool TrySurfaceNormal(in TerrainHit hit, out Vector3 normal)
    {
        normal = hit.Normal;
        if (!hit.Point.IsFinite() || !normal.IsFinite() || normal.LengthSquared() < 1e-10f)
        {
            normal = Vector3.Zero;
            return false;
        }
        normal = normal.Normalized();
        return true;
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;
    }
}
