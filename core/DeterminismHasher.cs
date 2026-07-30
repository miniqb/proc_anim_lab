using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 状态哈希器：按调用顺序把选定状态的 float 原始位（小端逐字节）折叠进 FNV-1a 64。
/// 不量化——任何一位漂移（偷读 delta/墙钟/迭代顺序不稳）都会被抓到。
/// 沙盒确定性探针与 smoke/ 无引擎回归共用此实现：同一具身体同一路线下
/// 蜥蜴折叠序一致（先逐 chunk Pos/Vel、再逐 limb Pos/Vel）；蜘蛛另把足端目标、pole 与膝点
/// 纳入独立基线。各自的沙盒与 smoke 使用相同顺序，可直接互证。
/// </summary>
public sealed class DeterminismHasher
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private ulong _hash = FnvOffset;
    private readonly Dictionary<ulong, ulong> _opaqueIdOrdinals = new();
    private ulong _nextOpaqueIdOrdinal = 1;

    public ulong Value => _hash;

    public void Fold(in Vector3 v)
    {
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.X));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Y));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Z));
    }

    public void Fold(float value) =>
        FoldBits(System.BitConverter.SingleToUInt32Bits(value));

    public void Fold(int value) => FoldBits(unchecked((uint)value));

    public void Fold(bool value) => FoldBits(value ? 1u : 0u);

    public void Fold(ulong value)
    {
        FoldBits(unchecked((uint)value));
        FoldBits(unchecked((uint)(value >> 32)));
    }

    /// <summary>
    /// 折叠只具备“零/非零、相同/不同”语义的宿主句柄。Godot collider_id 是进程内
    /// ObjectID，数值跨进程不稳定；按本次哈希中首次出现顺序规范化，保留身份关系而不让
    /// 外部标签制造无行为差异的哈希漂移。
    /// </summary>
    public void FoldOpaqueId(ulong value)
    {
        if (value == 0)
        {
            Fold(0UL);
            return;
        }
        if (!_opaqueIdOrdinals.TryGetValue(value, out ulong ordinal))
        {
            ordinal = _nextOpaqueIdOrdinal++;
            _opaqueIdOrdinals.Add(value, ordinal);
        }
        Fold(ordinal);
    }

    /// <summary>逐 chunk 折叠 Pos/Vel（Chunks 列表序 = 装配序，固定）。</summary>
    public void FoldBody(Body body)
    {
        foreach (BodyChunk c in body.Chunks)
        {
            Fold(c.Pos);
            Fold(c.Vel);
        }
    }

    public void FoldLimbs(IReadOnlyList<Limb> limbs)
    {
        foreach (Limb limb in limbs)
        {
            Fold(limb.Pos);
            Fold(limb.Vel);
        }
    }

    /// <summary>逐翅逐节折叠 Pos/Vel（Wings/Segments 列表序 = 装配序，固定）。
    /// 拍翅相位等控制器标量不进哈希（同守则：影响轨迹的状态由轨迹哈希抓到）。</summary>
    public void FoldWings(IReadOnlyList<VultureWing> wings)
    {
        foreach (VultureWing wing in wings)
        {
            foreach (VultureWing.WingSegment seg in wing.Segments)
            {
                Fold(seg.Pos);
                Fold(seg.Vel);
            }
        }
    }

    /// <summary>
    /// 蜘蛛腿按装配序折叠足端动力学与会影响后续 IK 的 pole 状态。
    /// KneePos 是上述状态的派生输出，也一并折叠，让渲染姿态回归与运动回归使用同一条证据链。
    /// </summary>
    public void FoldSpiderLegs(IReadOnlyList<SpiderLeg> legs)
    {
        foreach (SpiderLeg leg in legs)
        {
            Fold(leg.Pos);
            Fold(leg.Vel);
            Fold(leg.HuntPos);
            Fold(leg.GripNormal);
            Fold(leg.LastGripNormal);
            Fold(leg.BendPole);
            Fold(leg.KneePos);
            Fold(leg.FrameForward);
            Fold(leg.FrameUp);
            Fold(leg.FrameRight);
            Fold(leg.LastIkAxis);
            Fold(leg.SwingLandingGoal);
            Fold(leg.SwingHuntTarget);
            FoldDiscrete(leg.HasGrip);
            FoldDiscrete(leg.ReachingForTerrain);
            FoldDiscrete(leg.ReachedSnapPosition);
            FoldDiscrete(leg.TerrainContact);
            FoldDiscrete(leg.TargetSurfaceContact);
            FoldDiscrete(leg.Gripping);
            FoldDiscrete(leg.HasFrame);
            FoldDiscrete(leg.HasSolvedIk);
            FoldDiscrete(leg.HasFrozenSwingTarget);
            FoldDiscrete(leg.GripCounter);
            FoldDiscrete(leg.AcquisitionTicks);
            FoldDiscrete(leg.SwingTicks);
            FoldDiscrete(leg.StepSerial);
            FoldDiscrete(leg.EmergencyStepCount);
            FoldDiscrete(leg.LandingSerial);
            FoldDiscrete((int)leg.LastStepReason);
        }
    }

    /// <summary>
    /// 折叠蜘蛛控制器会影响后续 tick 的连续/离散状态。与 <see cref="FoldSpiderLegs"/>
    /// 分开调用，避免改变既有蜥蜴哈希顺序。
    /// </summary>
    public void FoldSpiderControllerState(SpiderLocomotionController controller)
    {
        Fold(controller.SupportNormal);
        Fold(controller.Forward);
        Fold(controller.MoveDir);
        Fold(controller.LastMoveTarget);
        FoldScalar(controller.RunSpeed);
        FoldDiscrete(controller.MoveTarget is not null);
        if (controller.MoveTarget is { } moveTarget)
        {
            Fold(moveTarget);
        }
        FoldDiscrete(controller.AtMoveTarget);
        FoldDiscrete(controller.ApplyGravity);
        FoldDiscrete(controller.LegsGripping);
        FoldDiscrete(controller.NoGripCounter);
        FoldDiscrete(controller.FootingCounter);
        FoldDiscrete(controller.StallTicks);
        FoldDiscrete((int)controller.LastMoveTargetKind);
    }

    /// <summary>
    /// 蜘蛛回归额外折叠共享 Body 中会改变下一 tick 碰撞/卡链分支的记忆态。
    /// 旧蜥蜴仍只调用既有 <see cref="FoldBody"/>，所以其基线顺序不变。
    /// </summary>
    public void FoldSpiderBodyState(Body body)
    {
        FoldScalar(body.GravityScale);
        FoldScalar(body.AirFriction);
        FoldScalar(body.SurfaceFriction);
        FoldDiscrete(body.EnablePostCollisionStructureRecovery);
        foreach (BodyChunk chunk in body.Chunks)
        {
            Fold(chunk.ContactNormal);
            FoldScalar(chunk.TerrainSqueeze);
            FoldDiscrete(chunk.TerrainContact);
            FoldDiscrete(chunk.HadContactLastTick);
        }
        foreach (ChunkConnection connection in body.Connections)
        {
            FoldDiscrete(connection.SnagTicks);
        }
    }

    private void FoldScalar(float value) =>
        FoldBits(System.BitConverter.SingleToUInt32Bits(value));

    private void FoldDiscrete(bool value) => FoldBits(value ? 1u : 0u);

    private void FoldDiscrete(int value) => FoldBits(unchecked((uint)value));

    private void FoldBits(uint bits)
    {
        for (int i = 0; i < 4; i++)
        {
            _hash = (_hash ^ ((bits >> (i * 8)) & 0xFF)) * FnvPrime;
        }
    }
}
