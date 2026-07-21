using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 身体的最小物理单元：带质量的球形粒子（对标雨世界 BodyChunk）。
/// 纯数据容器——积分/约束/碰撞逻辑全部在 <see cref="Body"/>，保证 tick 顺序唯一。
/// Vel 的语义是「米/tick 位移」：积分时直接 Pos += Vel，不乘 dt（确定性来源）。
/// LastPos 仅用于渲染插值与碰撞方向判定，不参与速度推算。
/// </summary>
public sealed class BodyChunk
{
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;
    public readonly float Mass;

    public bool CollideWithTerrain = true;

    /// <summary>本 tick 是否接触地形（碰撞阶段开头清 false，命中后置 true）。</summary>
    public bool TerrainContact;

    /// <summary>最近一次接触的表面法线（斜坡探针与 M2 落脚复用）。</summary>
    public Vector3 ContactNormal;

    /// <summary>上一 tick 是否有接触（触发接触法向探针射线）。</summary>
    public bool HadContactLastTick;

    /// <summary>朝向参照 chunk（≙ RW BodyChunk.rotationChunk）。建 <see cref="ChunkConnection"/> 时
    /// 两端自动互绑、后建连接覆盖先建（≙ RW BodyChunkConnection 构造副作用）；装配完成后
    /// <see cref="BodyFactory"/> 显式钉定脊柱指向（≙ RW Deer 构造后显式重申的先例），
    /// 不依赖建链顺序的巧合。装配期拓扑、运行时只读——不进确定性哈希。</summary>
    public BodyChunk? RotationChunk;

    /// <summary>朝向 =「参照 chunk → 自己」的单位向量（≙ RW BodyChunk.Rotation =
    /// (pos - rotationChunk.pos).normalized）。方向语义取决于参照是谁：头参照髋 = 身体前向，
    /// 髋参照头 = 指向后方——消费侧自行翻转（≙ RW LizardLimb 的 connection.index==2 补偿）。
    /// 退化语义照抄 RW：无参照 → Up（RW null 显式回落 (0,1)）；两点近重合 → **零向量**
    /// （Unity normalized 原语义，阈值取 Unity kEpsilon：模长 ≤ 1e-5 即归零——放宽到这里
    /// 才不会把近折叠时 1e-6~1e-5 的数值噪声放大成乱摆的单位方向）——消费端自行回退
    /// （Walker.TickLimbs 回退整体轴；翻转会把非零回退值翻成反向，零向量不会）。</summary>
    public Vector3 Rotation
    {
        get
        {
            if (RotationChunk is null)
            {
                return Vector3.Up;
            }
            Vector3 d = Pos - RotationChunk.Pos;
            return d.LengthSquared() <= 1e-10f ? Vector3.Zero : d.Normalized();
        }
    }

    public BodyChunk(Vector3 pos, float radius, float mass)
    {
        Pos = pos;
        LastPos = pos;
        Vel = Vector3.Zero;
        Radius = radius;
        Mass = mass;
    }

    /// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
    public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
