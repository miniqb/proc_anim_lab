using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.Physics;

/// <summary>
/// 一条腿 = 一个追目标点的粒子（合并 RW BodyPart/Limb/LizardLimb 中走路所需的部分）。
/// 不是解析式多关节 IK：vel = Lerp(vel, 朝目标*HuntSpeed, Quickness)，够近吸附。
/// 步态是 plant-and-trail：脚踩住不动 → 身体前移 → 腿拉伸到极限 → 松开 → 摆到前方
/// 超过 StepLength 阈值 → FindGrip 射线找新落点 → 再踩住。腿长由单侧距离钳制维持，
/// 腿不反推身体——推进力在 Walker 层按抓地腿数另行施加（RW：锚与引擎分离）。
/// 脚不进 Body.Chunks：它是独立受力点，会碰地形但不参与 chunk 间物理（≙ RW 图形层 BodyPart）。
/// </summary>
public sealed class Limb
{
    // —— 粒子态（语义同 BodyChunk：Vel = 米/tick，LastPos 仅渲染插值/碰撞方向）——
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;

    /// <summary>腿锚定的身体 chunk（≙ RW Limb.connection）。</summary>
    public readonly BodyChunk Anchor;

    /// <summary>横向符号：-1 左 / +1 右（RW 2D 侧视 limbNumber%2 的 3D 化）。</summary>
    public readonly int Side;

    /// <summary>成对的另一条腿（互斥推开 + extraLongStep 协调），构造后由工厂互联。</summary>
    public Limb? Pair;

    // —— plant-and-trail 状态 ——
    /// <summary>当前追逐的世界目标点（≙ absoluteHuntPos）。迈步抓到地形后固定 = 落点。</summary>
    public Vector3 HuntPos;

    /// <summary>true = 正在迈步寻找/踩住地形；false = 摆动期（脚漂向髋前静止姿势位）。</summary>
    public bool ReachingForTerrain;

    /// <summary>本 tick 是否已吸附到目标点。</summary>
    public bool ReachedSnapPosition;

    /// <summary>连续踩稳的 tick 数；≥ GripDelay 才算「抓地」计入推进力。</summary>
    public int GripCounter;

    public bool TerrainContact;

    /// <summary>成对腿还没抓稳时本腿延长摆动期（≙ extraLongStep，错开步态）。</summary>
    private bool _extraLongStep;

    /// <summary>延长摆动已持续的 tick 数（超时保险的计时）。</summary>
    private int _extraLongStepTicks;

    /// <summary>延长摆动的只读观测（探针/调试用）。</summary>
    public bool ExtraLongStep => _extraLongStep;

    /// <summary>
    /// 延长摆动的超时上限（tick）。身体被拎到空中时一对腿可能同 tick 双双释放、
    /// 都置 extraLongStep 而互相死等（RW 靠腿随机失能打破，确定性内核没有随机逃生口）；
    /// 正常等待约 5~15 tick，超过即视为死等强制恢复迈步。
    /// </summary>
    private const int ExtraLongStepLimit = 40;

    // —— 参数（≙ LizardBreedParams 子集；M4 收拢成 breed 参数对象）——
    /// <summary>腿长：脚被钳制在锚点这个半径内（≙ jointDist）。</summary>
    public float JointDist = 0.55f;

    /// <summary>每 tick 最大逼近速度（≙ limbSpeed）；追固定落点时自动加上锚点速度。</summary>
    public float HuntSpeed = 0.15f;

    /// <summary>速度插值系数，越大越急促（≙ limbQuickness）。</summary>
    public float Quickness = 0.6f;

    /// <summary>迈步阈值参数 ∈[0,1]：脚摆到髋前 Lerp(-0.5,0.5,·)*JointDist 时触发找落点（≙ stepLength）。</summary>
    public float StepLength = 0.7f;

    /// <summary>摆动期目标向髋部收拢的程度（≙ liftFeet，抬脚感）。</summary>
    public float LiftFeet = 0.2f;

    /// <summary>落点方向的向下偏置（≙ feetDown，脚更贴地）。</summary>
    public float FeetDown = 0.5f;

    /// <summary>落点方向的横向偏置：左右腿各自外撇（≙ legPairDisplacement 的 3D 化）。</summary>
    public float PairLateral = 0.45f;

    public float AirFriction = 0.7f;
    public float SurfaceFriction = 0.4f;

    /// <summary>判定「已抓稳」所需连续 tick 数（≙ limbGripDelay）。</summary>
    public int GripDelay = 4;

    /// <summary>抓地中：这条腿正为身体提供锚点/推进（Walker 按此计数施力）。</summary>
    public bool Gripping => GripCounter >= GripDelay;

    private const float Skin = 0.02f;

    /// <summary>吸附/重叠判定余量（RW 用 rad+1px；1px = 0.025m）。</summary>
    private const float OverlapPad = 0.025f;

    /// <summary>FindGrip 沿步进方向的采样偏移（米）——固定顺序保确定性（≙ RW ±20px 步 5 的收敛版）。</summary>
    private static readonly float[] GripSamples = { 0f, -0.125f, 0.125f, -0.25f, 0.25f };

    public Limb(BodyChunk anchor, Vector3 pos, float radius, int side)
    {
        Anchor = anchor;
        Pos = pos;
        LastPos = pos;
        Vel = Vector3.Zero;
        Radius = radius;
        Side = side;
        HuntPos = pos;
    }

    /// <summary>
    /// 推进一个 tick。stepDir = 本 tick 步进方向（身体朝向与移动意图的混合，单位向量）；
    /// up = 重力反方向；allLimbs/smoothGait/runSpeed 用于多腿步态错开。
    /// 顺序镜像 LizardLimb.Update：状态机 → 成对互斥 → 追目标积分 → 腿长钳制 → 抓地计数。
    /// </summary>
    public void Tick(in TickContext ctx, Vector3 stepDir, Vector3 up,
        IReadOnlyList<Limb> allLimbs, bool smoothGait, float runSpeed)
    {
        // 脚相对锚点沿步进方向的有符号超前量（RW num 的反号：>0 = 脚在髋前）。
        float advance = (Pos - Anchor.Pos).Dot(stepDir);
        float stepThreshold = Mathf.Lerp(-0.5f, 0.5f, StepLength);

        if (!ReachingForTerrain)
        {
            // 摆动期：目标 = 脚向髋收拢 LiftFeet 后再往前 JointDist——脚一路漂向最大前伸位。
            HuntPos = Pos.Lerp(Anchor.Pos, LiftFeet) + stepDir * (JointDist + OverlapPad);
            if (_extraLongStep)
            {
                _extraLongStepTicks++;
                if ((Pair is not null && Pair.GripCounter > GripDelay)
                    || _extraLongStepTicks > ExtraLongStepLimit)
                {
                    _extraLongStep = false;
                }
            }
            if (!_extraLongStep && advance > JointDist * stepThreshold)
            {
                ReachingForTerrain = true;
            }
        }
        else
        {
            if (!OverlappingHuntPos())
            {
                FindGrip(ctx, stepDir, up);
            }
            else
            {
                // 已踩到落点：等身体走过、腿重新拉伸到极限才松开（plant-and-trail 的 trail）。
                if (advance < JointDist * 0.5f * (StepLength + 0.1f)
                    && (Pos - Anchor.Pos).Length() >= JointDist - OverlapPad
                    && (HuntPos - Anchor.Pos).Length() >= JointDist)
                {
                    _extraLongStep = smoothGait && Pair is not null && Pair.GripCounter < 1;
                    _extraLongStepTicks = 0;
                    ReachingForTerrain = false;
                }

                // 步态错开：跑动中若本腿抓得最久且其余腿都已抓稳 → 主动松开迈步。
                if (ReachingForTerrain && runSpeed > 0.1f && smoothGait)
                {
                    bool oldestGrip = true;
                    foreach (Limb other in allLimbs)
                    {
                        if (other != this && (other.GripCounter > GripCounter || other.GripCounter == 0))
                        {
                            oldestGrip = false;
                            break;
                        }
                    }
                    if (oldestGrip)
                    {
                        ReachingForTerrain = false;
                    }
                }
            }
        }

        // 成对腿互斥：两脚过近互相推开（RW 双侧各推一半，两腿的 Tick 都会执行——保持一致）。
        if (Pair is not null)
        {
            Vector3 gap = Pair.Pos - Pos;
            float dist = gap.Length();
            if (dist < Radius * 3f && dist > 1e-6f)
            {
                Vector3 push = gap / dist * ((Radius * 3f - dist) * 0.5f);
                Vel -= push;
                Pair.Vel += push;
            }
        }

        IntegrateHunt(ctx);
        ConnectToAnchor();

        // 抓地计数：迈步中吸附到落点、或贴着落点且踩实地形，连续累计。
        if (ReachingForTerrain && (ReachedSnapPosition || (OverlappingHuntPos() && TerrainContact)))
        {
            GripCounter++;
        }
        else
        {
            GripCounter = 0;
        }
    }

    private bool OverlappingHuntPos()
    {
        return ReachedSnapPosition || (HuntPos - Pos).Length() < Radius + OverlapPad;
    }

    /// <summary>
    /// 找落点（≙ Limb.FindGrip 的射线版）：把目标方向加上向下/横向偏置得到期望落点，
    /// 在其周围沿步进方向做固定序竖直投影射线（≙ SnapToTerrain），
    /// 选「离期望点最近且腿够得着」的命中。找到 → HuntPos 固定为落点（plant）。
    /// </summary>
    private void FindGrip(in TickContext ctx, Vector3 stepDir, Vector3 up)
    {
        Vector3 right = stepDir.Cross(up);
        if (right.LengthSquared() < 1e-8f)
        {
            right = Vector3.Right; // 步进方向与重力共线的退化情形：固定回退方向保确定性
        }
        else
        {
            right = right.Normalized();
        }

        Vector3 dir = stepDir + right * (Side * PairLateral) - up * (0.3f * FeetDown);
        dir = dir.Normalized();
        float maxRadius = JointDist - OverlapPad;
        Vector3 goal = Anchor.Pos + dir * maxRadius;

        // 采样带沿步进方向的水平投影铺开（goal 上下扫的是竖直射线，横向才需要错开）。
        Vector3 alongHoriz = stepDir - up * stepDir.Dot(up);
        alongHoriz = alongHoriz.LengthSquared() < 1e-8f ? Vector3.Zero : alongHoriz.Normalized();

        bool found = false;
        Vector3 best = default;
        float bestDistSq = float.MaxValue;
        foreach (float offset in GripSamples)
        {
            Vector3 probe = goal + alongHoriz * offset;
            if (!ctx.Terrain.Raycast(probe + up * 0.35f, probe - up * 0.55f, out TerrainHit hit))
            {
                continue;
            }
            // 零法线 = 射线起点已陷入地形；朝下的面（悬垂底面）也不是 M2 的落脚面。
            if (hit.Normal.LengthSquared() < 1e-12f || hit.Normal.Dot(up) < 0.3f)
            {
                continue;
            }
            if ((hit.Point - Anchor.Pos).Length() > maxRadius)
            {
                continue;
            }
            float distSq = (hit.Point - goal).LengthSquared();
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = hit.Point;
                found = true;
            }
        }

        if (found)
        {
            HuntPos = best;
        }
        // 没找到：HuntPos 维持原值，下 tick 重试（身体在动，goal 会变）——RW 同款行为。
    }

    /// <summary>追目标积分（≙ Limb.Update 主体）：吸附或 Lerp 逼近，然后出地形。</summary>
    private void IntegrateHunt(in TickContext ctx)
    {
        // 追固定世界落点时脚要跟得上移动的身体（≙ huntSpeed += connection.vel.magnitude）。
        float huntSpeed = HuntSpeed + Anchor.Vel.Length();

        LastPos = Pos;
        if ((HuntPos - Pos).Length() < huntSpeed)
        {
            Vel = HuntPos - Pos;
            ReachedSnapPosition = true;
        }
        else
        {
            Vel = Vel.Lerp((HuntPos - Pos).Normalized() * huntSpeed, Quickness);
            ReachedSnapPosition = false;
        }
        Pos += Vel;
        Vel *= AirFriction;
        PushOutOfTerrain(ctx);
    }

    /// <summary>腿长单侧钳制（≙ ConnectToPoint(connection.pos, jointDist)）：只拉脚、不推身体。</summary>
    private void ConnectToAnchor()
    {
        Vector3 delta = Pos - Anchor.Pos;
        float dist = delta.Length();
        if (dist <= JointDist || dist < 1e-6f)
        {
            return;
        }
        Vector3 corr = delta / dist * (dist - JointDist);
        Pos -= corr;
        Vel -= corr;
    }

    /// <summary>脚 vs 地形（≙ BodyPart.PushOutOfTerrain 的射线版）：运动扫掠 + 重力向支撑，同 chunk 语义。</summary>
    private void PushOutOfTerrain(in TickContext ctx)
    {
        TerrainContact = false;
        Vector3 gravity = ctx.GravityPerTick;
        Vector3 down = gravity.LengthSquared() > 1e-12f ? gravity.Normalized() : Vector3.Down;

        Vector3 motion = Pos - LastPos;
        float motionLen = motion.Length();
        if (motionLen > 1e-6f)
        {
            Vector3 dir = motion / motionLen;
            if (ctx.Terrain.Raycast(LastPos, Pos + dir * Radius, out TerrainHit hit1)
                && SphereTerrain.Resolve(hit1, Radius, SurfaceFriction, ref Pos, ref Vel, LastPos, out _))
            {
                TerrainContact = true;
            }
        }

        if (ctx.Terrain.Raycast(Pos, Pos + down * (Radius + Skin), out TerrainHit hit2)
            && SphereTerrain.Resolve(hit2, Radius, SurfaceFriction, ref Pos, ref Vel, LastPos, out _))
        {
            TerrainContact = true;
        }
    }

    /// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
    public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
