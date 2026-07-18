using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.Physics;

/// <summary>
/// 会走路的生物 = Body（chunk 物理）+ 若干 Limb（plant-and-trail 腿）+ 推进力。
/// 镜像 RW Lizard 的移动块：腿的抓握既是锚也是引擎——推进力 ∝ 抓地腿数
/// （frameSpeed = BaseSpeed · gripFac · RunSpeed），没腿抓地就几乎使不上劲。
/// M2 范围：重力常开（GravityScale=1），平地行走；重力开关（爬墙不掉）留给 M3。
/// 无状态机：走路从「踩住/没踩住」涌现，MoveDir/RunSpeed 是唯一输入。
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

    /// <summary>零腿抓地时仍保留的推进比例（≙ noGripSpeed，防止完全瘫死）。</summary>
    public float NoGripSpeed = 0.15f;

    /// <summary>推进目标点在头前方的距离（米，≙ 瞄下一个路径格）。</summary>
    public float LookAhead = 0.5f;

    /// <summary>推进目标的上抬量（米，≙ floorLeverage）：让身体前端微微昂起、减小蹭地阻力。</summary>
    public float FloorLeverage = 0.15f;

    /// <summary>步进方向中移动意图对身体朝向的混合权重（≙ LizardLimb 的 0.4）。</summary>
    public float SteerBlend = 0.4f;

    /// <summary>是否按 gripCounter 协调多腿错开抬脚（≙ smoothenLegMovement）。</summary>
    public bool SmoothGait = true;

    /// <summary>当前抓地腿数（≙ legsGrabbing，每 tick 腿更新后重算）。</summary>
    public int LegsGripping { get; private set; }

    public Walker(Body body)
    {
        Body = body;
    }

    /// <summary>唯一入口：推进力 → 身体物理 → 腿（腿在图形层语义上晚于物理，与 RW 帧序一致）。</summary>
    public void Tick(in TickContext ctx)
    {
        Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
            ? -ctx.GravityPerTick.Normalized()
            : Vector3.Up;

        ApplyLocomotionForce();
        Body.Tick(ctx);
        TickLimbs(ctx, up);
    }

    /// <summary>
    /// 推进力（≙ Lizard.FollowConnection 的平地路径）：头朝目标点、髋朝「目标点身后一节」，
    /// 双点施力让身体沿移动方向拉直。力按抓地腿数缩放——腿是引擎。
    /// </summary>
    private void ApplyLocomotionForce()
    {
        if (RunSpeed <= 0f || MoveDir == Vector3.Zero)
        {
            return;
        }

        float gripFac = Limbs.Count == 0
            ? 1f
            : (float)LegsGripping / Limbs.Count * (1f - NoGripSpeed) + NoGripSpeed;
        float frameSpeed = BaseSpeed * gripFac * RunSpeed;

        Vector3 target = Head.Pos + MoveDir * LookAhead + Vector3.Up * FloorLeverage;
        Vector3 headDir = Dir(Head.Pos, target);
        Head.Vel += headDir * frameSpeed;

        float restLength = Body.Connections[0].RestLength;
        Vector3 trail = target + Dir(target, Head.Pos) * restLength;
        Vector3 hipsDir = Dir(Hips.Pos, trail);
        // 身体折叠（髋看目标与看拖尾点方向相反）时衰减髋部力，防止两点对拉（≙ RW 的 dot LerpMap）。
        float fold = Mathf.Remap(Dir(Hips.Pos, target).Dot(hipsDir), -1f, 1f, 0.5f, 1f);
        Hips.Vel += hipsDir * (frameSpeed * fold);
    }

    /// <summary>固定顺序更新每条腿，然后重算抓地数（供下 tick 推进力与外部读取）。</summary>
    private void TickLimbs(in TickContext ctx, Vector3 up)
    {
        Vector3 forward = Dir(Hips.Pos, Head.Pos);
        Vector3 aim = MoveDir == Vector3.Zero ? forward : MoveDir;
        Vector3 stepDir = forward.Lerp(aim, SteerBlend);
        stepDir = stepDir.LengthSquared() < 1e-8f ? forward : stepDir.Normalized();

        foreach (Limb limb in Limbs)
        {
            limb.Tick(ctx, stepDir, up, Limbs, SmoothGait, RunSpeed);
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

    private static Vector3 Dir(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        // 两点重合时方向未定义——固定回退方向保确定性。
        return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
    }
}
