using Godot;

namespace ProcAnimLab.Physics;

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

    /// <summary>硬约束修正分配给 A 端的权重 ∈ [0,1]（0 = A 视作无限重，全部位移落在 B）。</summary>
    public float WeightA;

    public Mode ConstraintMode = Mode.Rigid;

    public ChunkConnection(BodyChunk a, BodyChunk b, float restLength, float weightA)
    {
        A = a;
        B = b;
        RestLength = restLength;
        WeightA = weightA;
    }

    /// <summary>软弹簧：按距离误差把两端 Vel 向恢复方向推（对标 ConnectToPoint 的 elasticMovement 项）。</summary>
    public void ApplySoft()
    {
        Vector3 delta = B.Pos - A.Pos;
        float dist = delta.Length();
        if (dist < 1e-6f)
        {
            return;
        }
        float err = dist - RestLength;
        Vector3 corr = delta / dist * (err * Elasticity);
        A.Vel += corr * WeightA;
        B.Vel -= corr * (1f - WeightA);
    }

    /// <summary>硬约束：按模式判定触发后把两端拉回目标距离，Pos/Vel 同步修正。</summary>
    public void ApplyHard()
    {
        Vector3 delta = B.Pos - A.Pos;
        float dist = delta.Length();
        // 两点重合时方向未定义——用固定回退方向保证确定性（不读随机数）。
        Vector3 dir = dist < 1e-6f ? Vector3.Up : delta / dist;
        float err = dist - RestLength;

        bool triggered = ConstraintMode switch
        {
            Mode.Rigid => Mathf.Abs(err) > 0f,
            Mode.PullOnly => err > 0f,
            Mode.PushOnly => err < 0f,
            _ => false,
        };
        if (!triggered)
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
}
