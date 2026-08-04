using System;
using Godot;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>
/// 宿主/HUD 使用的派生状态。Idle 与 Locomotion 都执行地形任务；二者只表示当前是否
/// 被 needed-for-locomotion 预算征用，不能拿来替代触手任务本身。
/// </summary>
public enum DaddyTentacleRole
{
    Idle,
    Locomotion,
    ExternalReach,
    Stunned,
}

/// <summary>
/// 一条触手正在执行的真实任务。原作的 task 与 neededForLocomotion 是正交状态；
/// Stunned 由独立计时器叠加，不是任务。
/// </summary>
public enum DaddyTentacleTask
{
    Locomotion,
    ExternalReach,
}

/// <summary>
/// 宿主权威目标的单 tick 纯值快照。控制器不持有 Node/实体引用，也不会直接修改目标。
/// PullTowardBody 只决定是否在效果输出中请求把目标拉向身体。
/// </summary>
public readonly struct DaddyLongLegsTargetSnapshot
{
    public readonly ulong StableId;
    public readonly Vector3 Position;
    public readonly Vector3 VelocityPerTick;
    public readonly float Radius;
    public readonly float Mass;
    public readonly bool PullTowardBody;

    public DaddyLongLegsTargetSnapshot(
        ulong stableId,
        Vector3 position,
        Vector3 velocityPerTick,
        float radius,
        float mass,
        bool pullTowardBody)
    {
        StableId = stableId;
        Position = position;
        VelocityPerTick = velocityPerTick;
        Radius = radius;
        Mass = mass;
        PullTowardBody = pullTowardBody;
        Validate();
    }

    internal DaddyLongLegsTargetSnapshot Shifted(Vector3 delta) =>
        new(StableId, Position + delta, VelocityPerTick, Radius, Mass, PullTowardBody);

    internal void Validate()
    {
        if (StableId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(StableId), "Target stable ID must be non-zero.");
        if (!Finite(Position) || !Finite(VelocityPerTick))
            throw new ArgumentException("Target position and velocity must be finite.");
        if (!float.IsFinite(Radius) || Radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(Radius), Radius,
                "Target radius must be finite and non-negative.");
        if (!float.IsFinite(Mass) || Mass < 0f)
            throw new ArgumentOutOfRangeException(nameof(Mass), Mass,
                "Target mass must be finite and non-negative.");
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// 本 tick 对某条宿主目标的纯值效果请求。宿主可应用 PositionCorrection/VelocityDelta，
/// 并在下一 tick 回喂更新后的快照；Reached/Held/Released 是事件或状态，不代表伤害/吞入。
/// </summary>
public readonly struct DaddyLongLegsTargetEffect
{
    public readonly int TentacleIndex;
    public readonly ulong TargetId;
    public readonly bool Reached;
    public readonly bool Held;
    public readonly bool Released;
    public readonly Vector3 PositionCorrection;
    public readonly Vector3 VelocityDelta;

    public DaddyLongLegsTargetEffect(
        int tentacleIndex,
        ulong targetId,
        bool reached,
        bool held,
        bool released,
        Vector3 positionCorrection,
        Vector3 velocityDelta)
    {
        TentacleIndex = tentacleIndex;
        TargetId = targetId;
        Reached = reached;
        Held = held;
        Released = released;
        PositionCorrection = positionCorrection;
        VelocityDelta = velocityDelta;
    }
}
