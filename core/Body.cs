using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// chunk + 连接的容器；每 tick 按固定顺序推进：受力 → 积分 → 约束松弛 → 地形碰撞。
/// 积分是显式存 Vel 的半隐式欧拉（反编译 GenericBodyPart.Update 的确切顺序），
/// 不是经典 position-Verlet：LastPos 只用于渲染插值与碰撞方向判定。
/// 整个类不读 delta、不读引擎状态——确定性由固定顺序 + 常量步长保证。
/// </summary>
public sealed class Body
{
    public readonly List<BodyChunk> Chunks = new();
    public readonly List<ChunkConnection> Connections = new();

    /// <summary>重力倍率——M3 抓地重力开关的落点（抓住可达地形 → 置 0）。</summary>
    public float GravityScale = 1f;

    /// <summary>空气阻力：每 tick Vel 乘以该值（雨世界 0.8~0.999）。</summary>
    public float AirFriction = 0.98f;

    /// <summary>表面摩擦：接触地形时切向速度乘以该值（雨世界 0.3~0.5）。</summary>
    public float SurfaceFriction = 0.4f;

    /// <summary>硬约束松弛迭代次数（反编译缺失该参数，自定可调）。</summary>
    public int ConstraintIterations = 3;

    /// <summary>支撑射线的冗余长度（米）：过大→悬浮感，过小→接触断续。</summary>
    public float Skin = 0.02f;

    /// <summary>本 tick 约束松弛结束时的最大距离偏差（米）——求解器质量观测值，调参用。</summary>
    public float LastRelaxDeviation { get; private set; }

    /// <summary>卡链释放的触发比：碰撞后偏差超过 RestLength×此值才开始计数（1 = 拉到 2 倍距离）。</summary>
    public float SnagStretchRatio = 1f;

    /// <summary>深度违反持续这么多 tick 后强制释放（0.25s；瞬态冲击撑不满，跨墙僵持必然满）。</summary>
    public int SnagReleaseTicks = 10;

    /// <summary>累计卡链释放次数（回归指标：健康路线应为个位数——每次翻墙尾链至多逐节各释放一次）。</summary>
    public long SnagReleases { get; private set; }

    /// <summary>唯一入口：推进一个物理 tick。</summary>
    public void Tick(in TickContext ctx)
    {
        ApplyForces(ctx);
        Integrate();
        RelaxConstraints();
        Collide(ctx);
        ReleaseSnags();
    }

    private void ApplyForces(in TickContext ctx)
    {
        Vector3 g = ctx.GravityPerTick * GravityScale;
        foreach (BodyChunk c in Chunks)
        {
            c.Vel += g;
        }
    }

    private void Integrate()
    {
        foreach (BodyChunk c in Chunks)
        {
            c.LastPos = c.Pos;
            c.Pos += c.Vel;
            c.Vel *= AirFriction;
        }
    }

    private void RelaxConstraints()
    {
        for (int it = 0; it < ConstraintIterations; it++)
        {
            foreach (ChunkConnection conn in Connections)
            {
                // 软弹簧写的是 Vel（下一 tick 才生效），只加一次——
                // 否则刚度与迭代次数耦合，Elasticity 就不是正交参数了。
                if (it == 0 && conn.Elasticity > 0f)
                {
                    conn.ApplySoft();
                }
                conn.ApplyHard();
            }
        }

        LastRelaxDeviation = 0f;
        foreach (ChunkConnection conn in Connections)
        {
            if (conn.SoftOnly)
            {
                continue; // 姿态弹簧不归硬求解器管，常态就有"距离误差"，不计入求解质量
            }
            float err = (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength;
            float dev = conn.ConstraintMode switch
            {
                ChunkConnection.Mode.PullOnly => Mathf.Max(0f, err),
                ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -err),
                _ => Mathf.Abs(err),
            };
            if (dev > LastRelaxDeviation)
            {
                LastRelaxDeviation = dev;
            }
        }
    }

    /// <summary>当前时点的最大连接距离偏差（米，按各连接触发方向计，SoftOnly 不计）。
    /// 与 <see cref="LastRelaxDeviation"/> 的区别：这是「现在量」——tick 末尾调用可见
    /// 碰撞对约束的破坏（跨墙卡链等持续断裂靠它暴露，松弛末的观测值看不见）。</summary>
    public float CurrentMaxDeviation()
    {
        float max = 0f;
        foreach (ChunkConnection conn in Connections)
        {
            if (conn.SoftOnly)
            {
                continue;
            }
            float err = (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength;
            float dev = conn.ConstraintMode switch
            {
                ChunkConnection.Mode.PullOnly => Mathf.Max(0f, err),
                ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -err),
                _ => Mathf.Abs(err),
            };
            if (dev > max)
            {
                max = dev;
            }
        }
        return max;
    }

    private void Collide(in TickContext ctx)
    {
        foreach (BodyChunk c in Chunks)
        {
            c.HadContactLastTick = c.TerrainContact;
            c.TerrainContact = false;
            if (c.CollideWithTerrain)
            {
                CollideChunk(c, ctx);
            }
        }
    }

    /// <summary>
    /// 球 vs 地形：三射线 + 一次形状查询，固定发射与解算顺序（R1→R2→R3→S4，后解覆盖前解）
    /// 保确定性。R1 运动扫掠防隧穿/撞墙；R2 重力向支撑管静息贴地（R1 近零速失效时兜底）；
    /// R3 上帧接触法向探针维持斜坡滚动/贴墙滑动的连续接触；
    /// S4 球体重叠去穿透（MTD）——射线全是「球心线」，与运动平行的墙面擦边侵入
    /// 三根射线都看不见（评审 P1-2 实测髋球穿墙 5cm 无接触），整球嵌入（HitFromInside）
    /// 射线给不出方向（评审 P1-3 出生嵌入永久冻结），球形碰撞的承诺由这一步兜底。
    /// </summary>
    private void CollideChunk(BodyChunk c, in TickContext ctx)
    {
        Vector3 gravity = ctx.GravityPerTick;
        // down 取自重力方向而非硬编码 -Y：M3 换重力方向时支撑射线自动换向。
        Vector3 down = gravity.LengthSquared() > 1e-12f ? gravity.Normalized() : Vector3.Down;

        Vector3 motion = c.Pos - c.LastPos;
        float motionLen = motion.Length();
        if (motionLen > 1e-6f)
        {
            Vector3 dir = motion / motionLen;
            if (ctx.Terrain.Raycast(c.LastPos, c.Pos + dir * c.Radius, out TerrainHit hit1))
            {
                Resolve(c, hit1);
            }
        }

        if (ctx.Terrain.Raycast(c.Pos, c.Pos + down * (c.Radius + Skin), out TerrainHit hit2))
        {
            Resolve(c, hit2);
        }

        if (c.HadContactLastTick && c.ContactNormal.Dot(-down) < 0.99f
            && ctx.Terrain.Raycast(c.Pos, c.Pos - c.ContactNormal * (c.Radius + Skin), out TerrainHit hit3))
        {
            Resolve(c, hit3);
        }

        if (ctx.Terrain.SpherePenetration(c.Pos, c.Radius, out Vector3 pushDir, out float depth))
        {
            c.Pos += pushDir * depth;
            SphereTerrain.RespondVelocity(pushDir, SurfaceFriction, ref c.Vel);
            c.TerrainContact = true;
            c.ContactNormal = pushDir;
        }
    }

    /// <summary>
    /// 卡链释放（≙ RW BodyPart.Reset 的链版）：约束在碰撞后仍深度违反、且持续多 tick——
    /// 典型场景是尾链被薄墙隔在身体另一侧：松弛把它拉进墙、碰撞又推回墙后，逐 tick 僵持
    /// 到永远（hexapod 实测卡满 800 tick 直到跑完）。此时把轻侧 chunk 直接传送回锚侧的
    /// 约束满足位（沿现偏差方向、距离 = RestLength——锚侧半径内必是可达空间），继承锚速。
    /// 长链逐节释放：一次只解最靠身体的断裂环，其余环下个 tick 起依次接力（0.25s 一节）。
    /// 固定阈值 + 固定计数，无随机——确定性版的「RW 图形件离太远就 Reset」。
    /// </summary>
    private void ReleaseSnags()
    {
        foreach (ChunkConnection conn in Connections)
        {
            if (conn.SoftOnly)
            {
                continue;
            }
            float err = (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength;
            float dev = conn.ConstraintMode switch
            {
                ChunkConnection.Mode.PullOnly => Mathf.Max(0f, err),
                ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -err),
                _ => Mathf.Abs(err),
            };
            if (dev <= conn.RestLength * SnagStretchRatio)
            {
                conn.SnagTicks = 0;
                continue;
            }
            if (++conn.SnagTicks < SnagReleaseTicks)
            {
                continue;
            }
            conn.SnagTicks = 0;
            SnagReleases++;

            // WeightA 是 A 端的修正份额：份额小 = 惯性权威大 = 当锚（并列取 A，保确定性）。
            bool anchorIsA = conn.WeightA <= 0.5f;
            BodyChunk anchor = anchorIsA ? conn.A : conn.B;
            BodyChunk mover = anchorIsA ? conn.B : conn.A;
            Vector3 delta = mover.Pos - anchor.Pos;
            Vector3 dir = delta.LengthSquared() < 1e-12f ? Vector3.Up : delta.Normalized();
            mover.Pos = anchor.Pos + dir * conn.RestLength;
            mover.LastPos = mover.Pos; // 清运动扫掠：下 tick 不把「传送」当成穿墙位移
            mover.Vel = anchor.Vel;
        }
    }

    /// <summary>
    /// 解穿透 + 速度响应：法向速度清零（无反弹，雨世界手感）、切向乘 SurfaceFriction。
    /// 与硬约束不同，这里只推 Pos 不同步改 Vel——否则静息接触时反复注入向上速度产生抖动。
    /// </summary>
    private void Resolve(BodyChunk c, in TerrainHit hit)
    {
        if (SphereTerrain.Resolve(hit, c.Radius, SurfaceFriction, ref c.Pos, ref c.Vel, out Vector3 n))
        {
            c.TerrainContact = true;
            c.ContactNormal = n;
        }
    }
}
