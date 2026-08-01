using Godot;

namespace ProcAnim.Core.Physics;

/// <summary>
/// 两 chunk 间的距离连接：可选软弹簧项 + 硬距离约束项
/// （对标雨世界 BodyChunkConnection / BodyPart.ConnectToPoint 的两段逻辑）。
/// 硬约束的关键语义：超差才拉回，且 Pos 与 Vel 同步修正（pos -= v; vel -= v），
/// 两端按 WeightA 分配位移（≙ 反编译 TailSegment 的 affectPrevious 权重）。
/// </summary>
public sealed class ChunkConnection
{
    /// <summary>硬约束触发方向。Rigid=双向锁距（slugcat 躯干）；PullOnly=只防拉长（尾链）；PushOnly=只防压缩。</summary>
    public enum Mode
    {
        Rigid,
        PullOnly,
        PushOnly,
    }

    /// <summary>链式约定：A = 前段（靠身体），B = 后段（靠末梢）。</summary>
    public readonly BodyChunk A;
    public readonly BodyChunk B;

    /// <summary>目标距离（米）。</summary>
    public float RestLength;

    /// <summary>软弹簧系数 ∈ [0,1]；0 = 纯硬约束。每 tick 只施加一次（与迭代次数解耦）。</summary>
    public float Elasticity;

    /// <summary>true = 只走软弹簧、跳过硬约束（≙ RW BodyChunkConnection 的纯弹性修正——
    /// 它全部连接都是软推，本项目默认硬约束，防折叠支柱这类"姿态弹簧"才用此档）。</summary>
    public bool SoftOnly;

    /// <summary>碰撞后只恢复「由碰撞新增」的结构违反。蜥蜴仅给隔节 SoftOnly 姿态支柱
    /// 开启；蜈蚣也给相邻 Rigid 链开启，以免逐节跨面时 MTD 覆盖刚完成的距离松弛。
    /// PullOnly 长尾保持关闭，避免尾链反向拖死身体。</summary>
    public bool TerrainCoupled;

    /// <summary>硬约束修正分配给 A 端的权重 ∈ [0,1]（0 = A 视作无限重，全部位移落在 B）。</summary>
    public float WeightA;

    public Mode ConstraintMode = Mode.Rigid;

    /// <summary>碰撞后仍深度违反的连续 tick 计数（Body.ReleaseSnags 维护）。
    /// 由轨迹确定性导出，不进状态哈希。</summary>
    public int SnagTicks;

    /// <summary>该连接自身累计触发的卡链释放次数；供测试/诊断把尾链 PullOnly 释放与
    /// 脊柱 Rigid 释放分开归因，不参与物理决策或确定性哈希。</summary>
    public long SnagReleases { get; internal set; }

    public ChunkConnection(BodyChunk a, BodyChunk b, float restLength, float weightA)
    {
        A = a;
        B = b;
        RestLength = restLength;
        WeightA = weightA;
        // ≙ RW BodyChunkConnection 构造副作用：两端无条件互设朝向参照（不分连接类型/模式），
        // 后建连接覆盖先建。脊柱的最终指向不靠这里的建链顺序——工厂装配完显式钉定。
        a.RotationChunk = b;
        b.RotationChunk = a;
    }

    /// <summary>软弹簧：按距离误差把两端向恢复方向推（对标 ConnectToPoint 的 elasticMovement 项）。
    /// 与硬约束同一套触发方向：PullOnly 只在拉长时推回，PushOnly 只在过近时撑开。
    /// SoftOnly 档用 RW BodyChunkConnection.Update 的原语义——Pos/Vel 同步修正（按 Elasticity
    /// 缩放的位置修正，立即生效）；普通软项只写 Vel（下 tick 生效，弱得多，配合硬约束用）。</summary>
    public void ApplySoft()
    {
        Vector3 delta = B.Pos - A.Pos;
        float dist = delta.Length();
        float err = dist - RestLength;
        if (!Triggered(err))
        {
            return;
        }
        // 两点重合时方向未定义——固定回退方向（同 ApplyHard）。不能早退：防折叠支柱是
        // SoftOnly PushOnly，脊柱精确 180° 对折、头与第三节重合时恰恰最需要它撑开，
        // 确定性系统又没有随机扰动可以蹭出一个方向。
        Vector3 dir = dist < 1e-6f ? Vector3.Up : delta / dist;
        Vector3 corr = dir * (err * Elasticity);
        Vector3 corrA = corr * WeightA;
        Vector3 corrB = corr * (1f - WeightA);
        if (SoftOnly)
        {
            A.Pos += corrA;
            A.Vel += corrA;
            B.Pos -= corrB;
            B.Vel -= corrB;
        }
        else
        {
            A.Vel += corrA;
            B.Vel -= corrB;
        }
    }

    /// <summary>硬约束：按模式判定触发后把两端拉回目标距离，Pos/Vel 同步修正。</summary>
    public void ApplyHard()
    {
        if (SoftOnly)
        {
            return;
        }
        Vector3 delta = B.Pos - A.Pos;
        float dist = delta.Length();
        // 两点重合时方向未定义——用固定回退方向保证确定性（不读随机数）。
        Vector3 dir = dist < 1e-6f ? Vector3.Up : delta / dist;
        float err = dist - RestLength;

        if (!Triggered(err))
        {
            return;
        }

        Vector3 corr = dir * err;
        Vector3 corrA = corr * WeightA;
        Vector3 corrB = corr * (1f - WeightA);
        A.Pos += corrA;
        A.Vel += corrA;
        B.Pos -= corrB;
        B.Vel -= corrB;
    }

    private bool Triggered(float err) => ConstraintMode switch
    {
        Mode.Rigid => Mathf.Abs(err) > 0f,
        Mode.PullOnly => err > 0f,
        Mode.PushOnly => err < 0f,
        _ => false,
    };
}
