using System;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.DropBug;

/// <summary>
/// DropBug 出生参数表与装配器。三个稳定 ID：dropbug/original（逐位换算反编译值）、
/// dropbug/nimble（小体格快节奏）、dropbug/bulky（大体格慢蓄力重扑）。
/// 未知 ID 快速失败（同 TentaclePlant 先例），不静默回落。
/// </summary>
public static class DropBugFactory
{
    /// <summary>反编译 DropBug.cs 逐位换算的基准个体。</summary>
    public static DropBugParams Original() => new();

    /// <summary>小而快：0.85 体格、更快蓄力、更短的腿与步幅。</summary>
    public static DropBugParams Nimble() => new()
    {
        Id = "dropbug/nimble",
        HeadRadius = 0.1275f,
        MidRadius = 0.17f,
        TailRadius = 0.1275f,
        HeadMass = 0.24f,
        MidMass = 0.24f,
        TailMass = 0.12f,
        HeadMidLength = 0.255f,
        MidTailLength = 0.2975f,
        AntiFoldLength = 0.17f,
        HangHeadMidLength = 0.106f,
        HangMidTailLength = 0.0425f,
        MaxMoveSpeed = 0.15f,
        PounceChargeRate = 1f / 12f,
        DiveImpulseHead = 0.47f,
        DiveImpulseMid = 0.36f,
        PounceReachMax = 6.5f,
        LegLength = 0.95f,
    };

    /// <summary>大而沉：1.25 体格、慢蓄力、更强冲量、负重更耐受。</summary>
    public static DropBugParams Bulky() => new()
    {
        Id = "dropbug/bulky",
        HeadRadius = 0.1875f,
        MidRadius = 0.25f,
        TailRadius = 0.1875f,
        HeadMass = 0.48f,
        MidMass = 0.48f,
        TailMass = 0.24f,
        HeadMidLength = 0.36f,
        MidTailLength = 0.42f,
        AntiFoldLength = 0.24f,
        HangHeadMidLength = 0.15f,
        HangMidTailLength = 0.06f,
        MaxMoveSpeed = 0.10f,
        PounceChargeRate = 1f / 20f,
        DiveImpulseHead = 0.60f,
        DiveImpulseMid = 0.46f,
        PounceReachMax = 8.5f,
        CarryMassFull = 6f,
        LegLength = 1.3f,
        HangEngageDistance = 1.15f,
        HangApproachDistance = 1.45f,
    };

    public static DropBugParams[] AllPresets() => new[] { Original(), Nimble(), Bulky() };

    /// <summary>按稳定 ID 取预设；未知 ID 抛出（快速失败，不静默回落）。</summary>
    public static DropBugParams ById(string? id)
    {
        foreach (DropBugParams preset in AllPresets())
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }
        throw new ArgumentException($"未知 DropBug 预设 ID：{id}");
    }

    public static DropBugLocomotionController CreateController(
        Vector3 origin,
        Vector3 forward,
        DropBugParams parameters)
    {
        DropBugParams p = parameters.Snapshot();
        Vector3 fwd = forward.LengthSquared() > 1e-10f ? forward.Normalized() : Vector3.Forward;

        // 出生摆位：mid 在 origin，头/尾沿 forward 排开（宿主负责放在地表上方）。
        var head = new BodyChunk(origin + fwd * p.HeadMidLength, p.HeadRadius, p.HeadMass);
        var mid = new BodyChunk(origin, p.MidRadius, p.MidMass);
        var tail = new BodyChunk(origin - fwd * p.MidTailLength, p.TailRadius, p.TailMass);

        var body = new Body
        {
            AirFriction = p.AirFriction,
            SurfaceFriction = p.SurfaceFriction,
            GravityScale = 1f, // 重力常开；不对称抵消全部在控制器（≙ Footing 块）
        };
        body.Chunks.Add(head);
        body.Chunks.Add(mid);
        body.Chunks.Add(tail);

        // ≙ RW bodyChunkConnections（elasticity 1 = 全额瞬时修正 = 本项目硬约束；
        // weight -1 = 按质量反比分配 → WeightA 取 massB/(massA+massB)）。
        body.Connections.Add(new ChunkConnection(head, mid, p.HeadMidLength,
            p.MidMass / (p.HeadMass + p.MidMass))
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
        });
        body.Connections.Add(new ChunkConnection(mid, tail, p.MidTailLength,
            p.TailMass / (p.MidMass + p.TailMass))
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
        });
        body.Connections.Add(new ChunkConnection(head, tail, p.AntiFoldLength,
            p.TailMass / (p.HeadMass + p.TailMass))
        {
            ConstraintMode = ChunkConnection.Mode.PushOnly,
        });

        // 显式钉定朝向参照（数值上与 RW 建链顺序的副作用一致：conn2 后建 → 头↔尾互绑、
        // conn1 → mid→尾；这里不依赖顺序巧合）。头朝向 = 尾→头 = 全身轴。
        head.RotationChunk = tail;
        mid.RotationChunk = tail;
        tail.RotationChunk = head;

        return new DropBugLocomotionController(body, head, mid, tail, p, fwd);
    }
}
