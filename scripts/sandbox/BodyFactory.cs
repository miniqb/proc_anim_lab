using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 测试身体工厂。单位约定：1 RW tile (20px) = 0.5 m，即 1px = 0.025 m。
/// slugcat ≈ 2 球 chunk + 1 根 Rigid 距离连接（两点+连接才能翻滚——雨世界实证）。
/// </summary>
public static class BodyFactory
{
    /// <summary>2-chunk slugcat 式身体：头 0.20m / 髋 0.25m，连接 0.3m。</summary>
    public static Body CreateSlugcat(Vector3 origin)
    {
        var body = new Body();
        var head = new BodyChunk(origin + new Vector3(0f, 0.3f, 0f), 0.20f, 0.4f);
        var hips = new BodyChunk(origin, 0.25f, 0.6f);
        body.Chunks.Add(head);
        body.Chunks.Add(hips);
        body.Connections.Add(new ChunkConnection(head, hips, 0.3f, weightA: 0.5f)
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
            Elasticity = 0.25f,
        });
        return body;
    }

    /// <summary>
    /// M2 四腿行走体：slugcat+尾 的身体，前对腿锚在头、后对腿锚在髋。
    /// 初始脚位按对角步态错开（FL/RR 靠前、FR/RL 靠后）——步态错开逻辑用 gripCounter
    /// 严格比较打破平局，四腿完全对称落地会同抬同落，出生错位给它一个确定性的相位种子。
    /// </summary>
    public static Walker CreateWalker(Vector3 origin)
    {
        Body body = CreateSlugcatWithTail(origin);
        var walker = new Walker(body);
        BodyChunk head = body.Chunks[0];
        BodyChunk hips = body.Chunks[1];

        // (锚, 横向符号, 初始前后错位)：+X 为出生朝向的前方（与尾巴伸展方向相反）。
        (BodyChunk anchor, int side, float stagger)[] legs =
        {
            (head, -1, 0.10f),  // 前左
            (head, +1, -0.10f), // 前右
            (hips, -1, -0.10f), // 后左
            (hips, +1, 0.10f),  // 后右
        };
        foreach ((BodyChunk anchor, int side, float stagger) in legs)
        {
            Vector3 foot = anchor.Pos + new Vector3(-0.15f + stagger, -anchor.Radius, side * 0.25f);
            walker.Limbs.Add(new Limb(anchor, foot, 0.06f, side));
        }
        walker.Limbs[0].Pair = walker.Limbs[1];
        walker.Limbs[1].Pair = walker.Limbs[0];
        walker.Limbs[2].Pair = walker.Limbs[3];
        walker.Limbs[3].Pair = walker.Limbs[2];
        return walker;
    }

    /// <summary>slugcat + 渐细尾链：PullOnly（只防拉长）+ WeightA 沿链递减（≙ tailStiffnessDecline）。</summary>
    public static Body CreateSlugcatWithTail(Vector3 origin, int segments = 6)
    {
        Body body = CreateSlugcat(origin);
        BodyChunk prev = body.Chunks[1]; // 尾巴接在髋部
        for (int i = 0; i < segments; i++)
        {
            float f = segments <= 1 ? 0f : i / (float)(segments - 1);
            float radius = Mathf.Lerp(0.12f, 0.04f, f);
            float mass = Mathf.Lerp(0.05f, 0.01f, f);
            var seg = new BodyChunk(prev.Pos + new Vector3(0.15f, 0f, 0f), radius, mass);
            body.Chunks.Add(seg);
            body.Connections.Add(new ChunkConnection(prev, seg, 0.15f, weightA: Mathf.Lerp(0.35f, 0.05f, f))
            {
                ConstraintMode = ChunkConnection.Mode.PullOnly,
            });
            prev = seg;
        }
        return body;
    }
}
