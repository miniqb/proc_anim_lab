using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.Physics;

/// <summary>
/// 会走路的生物 = Body（chunk 物理）+ 若干 Limb（plant-and-trail 腿）+ 推进力。
/// 镜像 RW Lizard 的移动块：腿的抓握既是锚也是引擎——推进力 ∝ 抓地腿数
/// （frameSpeed = BaseSpeed · gripFac · RunSpeed），没腿抓地就几乎使不上劲。
///
/// M3 核心：走/爬无模式分支，全部由「支撑系」涌现（≙ RW §11.6b 重力开关）——
/// · SupportNormal = 抓地腿抓握面法线的平滑平均：平地=上，墙面=墙法线；
///   腿的落脚射线沿 -SupportNormal 打（走=朝下、爬=朝墙，同一条代码）。
/// · 站稳（FootingCounter 足够 且 有腿抓地）→ 重力关成 0：没有吸墙力，
///   是重力本身被开关——这就是身体不从墙上掉下来的全部原因。
/// · 移动意图被支撑面挡住的分量沿面内上坡方向重定向：推着墙走自然变成往上爬。
/// 无状态机：MoveDir/RunSpeed 是唯一输入，「抓住/没抓住」是唯一开关。
/// </summary>
public sealed class Walker
{
    public readonly Body Body;
    public readonly List<Limb> Limbs = new();

    /// <summary>约定：Chunks[0] = 头（施力主点），Chunks[1] = 髋。</summary>
    public BodyChunk Head => Body.Chunks[0];
    public BodyChunk Hips => Body.Chunks[1];

    /// <summary>移动意图方向（单位向量或零向量）。由输入/AI 每 tick 写入。</summary>
    public Vector3 MoveDir;

    /// <summary>移动意图强度 ∈ [0,1]（≙ AI.runSpeed）。</summary>
    public float RunSpeed;

    /// <summary>满抓地满速时每 tick 注入的速度（米/tick，≙ lizardParams.baseSpeed）。</summary>
    public float BaseSpeed = 0.06f;

    /// <summary>引擎极速（米/tick）：沿推进方向已有这个速度就不再注入。
    /// RW 瞄「下一个路径格」的伺服天然限速；我们的目标点是随头移动的胡萝卜，必须显式封顶——
    /// 平地上碰撞/腿阻先饱和（实测 ~0.03），贴墙无阻滑升时这是唯一的刹车。</summary>
    public float MaxMoveSpeed = 0.08f;

    /// <summary>零腿抓地时仍保留的推进比例（≙ noGripSpeed，防止完全瘫死）。</summary>
    public float NoGripSpeed = 0.15f;

    /// <summary>推进目标点在头前方的距离（米，≙ 瞄下一个路径格）。</summary>
    public float LookAhead = 0.5f;

    /// <summary>推进目标离支撑面的高度（米，≙ 路径格中心在地表上方半格 10px = 0.25m）。
    /// 目标钉在支撑面上：身体飘离面时头部力自动带回中分量——RW「不飘离墙」的真正来源。</summary>
    public float RideHeight = 0.25f;

    /// <summary>支撑面射线打空（悬崖边/探进墙里）时相对目标的上抬量（米，M2 的 floorLeverage 回退）。</summary>
    public float FloorLeverage = 0.15f;

    /// <summary>步进方向中移动意图对身体朝向的混合权重（≙ LizardLimb 的 0.4）。</summary>
    public float SteerBlend = 0.4f;

    /// <summary>是否按 gripCounter 协调多腿错开抬脚（≙ smoothenLegMovement）。</summary>
    public bool SmoothGait = true;

    // —— 站稳/重力开关参数（≙ Lizard applyGravity 块，数值直接取 RW）——
    /// <summary>坠落后需连续站稳这么多 tick 重力才重新关闭（≙ regainFootingCounter）。</summary>
    public int RegainFootingTicks = 15;

    /// <summary>连续这么多 tick 没有任何腿抓地 → 重力恢复（≙ NoGripCounter > 10）。</summary>
    public int LoseGripTicks = 10;

    public float FootedAirFriction = 0.8f;
    public float FootedSurfaceFriction = 0.5f;
    public float AirborneAirFriction = 0.999f;
    public float AirborneSurfaceFriction = 0.3f;

    /// <summary>支撑法线每 tick 向目标法线的插值系数（换面过渡的平滑度）。</summary>
    public float SupportBlend = 0.15f;

    /// <summary>悬空但没坠远时「支撑面就在附近」的探测余量（米，≙ RW 查相邻可达格）。</summary>
    public float NearTerrainRange = 0.4f;

    /// <summary>当前抓地腿数（≙ legsGrabbing，每 tick 腿更新后重算）。</summary>
    public int LegsGripping { get; private set; }

    /// <summary>连续无任何腿抓地的 tick 数（≙ LizardGraphics.noGripCounter）。</summary>
    public int NoGripCounter { get; private set; }

    /// <summary>连续「站在/贴着可停留地形」的 tick 数，封顶 100（≙ inAllowedTerrainCounter）。</summary>
    public int FootingCounter { get; private set; }

    /// <summary>true = 重力在拽（坠落态摩擦）；false = 抓稳，重力 0（≙ applyGravity）。</summary>
    public bool ApplyGravity { get; private set; } = true;

    /// <summary>支撑系的「上」：抓地腿抓握面法线的平滑平均。平地=世界上，爬墙=墙法线。</summary>
    public Vector3 SupportNormal { get; private set; } = Vector3.Up;

    public Walker(Body body)
    {
        Body = body;
    }

    /// <summary>
    /// 唯一入口：站稳判定 → 推进力 → 身体物理 → 腿 → 支撑系更新
    /// （腿在图形层语义上晚于物理，与 RW 帧序一致；支撑法线滞后一 tick 使用）。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
            ? -ctx.GravityPerTick.Normalized()
            : Vector3.Up;

        UpdateFooting(ctx);
        Vector3 effMove = RedirectMove(up);
        ApplyLocomotionForce(ctx, effMove, up);
        Body.Tick(ctx);
        TickLimbs(ctx, effMove);
        UpdateSupportNormal(up);
    }

    /// <summary>
    /// 站稳计数与重力开关（≙ Lizard ~1778 行）：
    /// applyGravity = 站稳不足 || 抓空过久。重力关时摩擦切「贴地档」（阻尼大，力快速到平衡，
    /// 身体不因失重漂移）；开时切「坠落档」（近乎无风阻）。
    /// </summary>
    private void UpdateFooting(in TickContext ctx)
    {
        bool anyGrip = false;
        bool anyContact = false;
        foreach (Limb limb in Limbs)
        {
            anyGrip |= limb.GripCounter > 0;
            anyContact |= limb.TerrainContact;
        }
        foreach (BodyChunk c in Body.Chunks)
        {
            anyContact |= c.TerrainContact;
        }

        NoGripCounter = anyGrip ? 0 : NoGripCounter + 1;

        if (anyGrip || anyContact)
        {
            FootingCounter = Mathf.Min(FootingCounter + 1, 100);
        }
        else if (ctx.Terrain.Raycast(Hips.Pos,
                     Hips.Pos - SupportNormal * (Hips.Radius + NearTerrainRange), out _))
        {
            // 悬空但支撑面就在附近（迈步腾空/小颠簸）：缓扣不清零，免得重力闪开又闪关。
            FootingCounter = Mathf.Max(0, FootingCounter - 10);
        }
        else
        {
            FootingCounter = 0;
        }

        ApplyGravity = FootingCounter < RegainFootingTicks || NoGripCounter > LoseGripTicks;

        if (ApplyGravity)
        {
            Body.GravityScale = 1f;
            Body.AirFriction = AirborneAirFriction;
            Body.SurfaceFriction = AirborneSurfaceFriction;
        }
        else
        {
            Body.GravityScale = 0f;
            Body.AirFriction = FootedAirFriction;
            Body.SurfaceFriction = FootedSurfaceFriction;
        }
    }

    /// <summary>
    /// 把移动意图里被支撑面挡住的分量沿面内「上坡」方向重定向：
    /// 平地（意图不顶面）原样通过；斜坡上顶坡的分量变成沿坡上行；推着墙走变成竖直向上爬。
    /// 一条公式覆盖走/爬，没有模式分支（RW 靠寻路格给出同样的「往上」，这里用几何重定向替代）。
    /// 意图背离支撑面时不干预——身体被拉离墙、抓空、重力回归，「松手掉落」自然涌现。
    /// </summary>
    private Vector3 RedirectMove(Vector3 up)
    {
        if (MoveDir == Vector3.Zero)
        {
            return Vector3.Zero;
        }
        Vector3 n = SupportNormal;
        float into = -MoveDir.Dot(n);
        if (into <= 0.01f)
        {
            return MoveDir;
        }
        Vector3 upOnPlane = up - n * up.Dot(n);
        if (upOnPlane.LengthSquared() < 1e-6f)
        {
            return MoveDir; // 支撑面近乎水平：没有「坡上」可言
        }
        Vector3 redirected = MoveDir + n * into + upOnPlane.Normalized() * into;
        return redirected.LengthSquared() < 1e-6f ? MoveDir : redirected.Normalized();
    }

    /// <summary>
    /// 推进力（≙ Lizard.FollowConnection）：头朝目标点、髋朝「目标点身后一节」，
    /// 双点施力让身体沿移动方向拉直。力按抓地腿数缩放——腿是引擎。
    /// </summary>
    private void ApplyLocomotionForce(in TickContext ctx, Vector3 effMove, Vector3 up)
    {
        if (RunSpeed <= 0f || effMove == Vector3.Zero)
        {
            return;
        }

        float gripFac = Limbs.Count == 0
            ? 1f
            : (float)LegsGripping / Limbs.Count * (1f - NoGripSpeed) + NoGripSpeed;
        float frameSpeed = BaseSpeed * gripFac * RunSpeed;

        Vector3 target = FindMoveTarget(ctx, effMove, up);
        Vector3 headDir = Dir(Head.Pos, target);
        if (Head.Vel.Dot(headDir) < MaxMoveSpeed)
        {
            Head.Vel += headDir * frameSpeed;
        }

        float restLength = Body.Connections[0].RestLength;
        Vector3 trail = target + Dir(target, Head.Pos) * restLength;
        Vector3 hipsDir = Dir(Hips.Pos, trail);
        // 身体折叠（髋看目标与看拖尾点方向相反）时衰减髋部力，防止两点对拉（≙ RW 的 dot LerpMap）。
        float fold = Mathf.Remap(Dir(Hips.Pos, target).Dot(hipsDir), -1f, 1f, 0.5f, 1f);
        if (Hips.Vel.Dot(hipsDir) < MaxMoveSpeed)
        {
            Hips.Vel += hipsDir * (frameSpeed * fold);
        }
    }

    /// <summary>
    /// 推进目标钉在支撑面上（≙ RW 瞄路径格中心——格中心天然贴着地形）：
    /// 头前 LookAhead 处沿 -SupportNormal 投影到面、抬 RideHeight。
    /// 打空/探进墙里（零法线）/朝下悬垂面 → 退回 M2 的头前相对目标。
    /// </summary>
    private Vector3 FindMoveTarget(in TickContext ctx, Vector3 effMove, Vector3 up)
    {
        Vector3 n = SupportNormal;
        Vector3 ahead = Head.Pos + effMove * LookAhead;
        if (ctx.Terrain.Raycast(ahead + n * 0.35f, ahead - n * 0.65f, out TerrainHit hit)
            && hit.Normal.LengthSquared() > 1e-12f
            && hit.Normal.Dot(up) > -0.3f)
        {
            return hit.Point + n * RideHeight;
        }
        return ahead + up * FloorLeverage;
    }

    /// <summary>固定顺序更新每条腿，然后重算抓地数（供下 tick 推进力与外部读取）。</summary>
    private void TickLimbs(in TickContext ctx, Vector3 effMove)
    {
        Vector3 forward = Dir(Hips.Pos, Head.Pos);
        Vector3 aim = effMove == Vector3.Zero ? forward : effMove;
        Vector3 stepDir = forward.Lerp(aim, SteerBlend);
        stepDir = stepDir.LengthSquared() < 1e-8f ? forward : stepDir.Normalized();

        foreach (Limb limb in Limbs)
        {
            limb.Tick(ctx, stepDir, SupportNormal, Limbs, SmoothGait, RunSpeed);
        }

        int gripping = 0;
        foreach (Limb limb in Limbs)
        {
            if (limb.Gripping)
            {
                gripping++;
            }
        }
        LegsGripping = gripping;
    }

    /// <summary>
    /// 支撑法线 = 抓地腿抓握面法线的平滑平均。抓着墙 → 转向墙法线（腿的射线随之朝墙打）；
    /// 抓空过久 → 衰减回世界上方向（坠落后落脚射线自动恢复朝下）。
    /// 短暂全腿腾空维持现值——换步瞬间支撑系不抖。
    /// </summary>
    private void UpdateSupportNormal(Vector3 up)
    {
        Vector3 sum = Vector3.Zero;
        foreach (Limb limb in Limbs)
        {
            if (limb.GripCounter > 0)
            {
                sum += limb.GripNormal;
            }
        }

        Vector3 target;
        if (sum.LengthSquared() > 1e-6f)
        {
            target = sum.Normalized();
        }
        else if (NoGripCounter > LoseGripTicks)
        {
            target = up;
        }
        else
        {
            return;
        }

        Vector3 blended = SupportNormal.Lerp(target, SupportBlend);
        // 目标与现值几乎反向时插值可能过零——固定回退到目标本身保确定性。
        SupportNormal = blended.LengthSquared() < 1e-6f ? target : blended.Normalized();
    }

    private static Vector3 Dir(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        // 两点重合时方向未定义——固定回退方向保确定性。
        return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
    }
}
