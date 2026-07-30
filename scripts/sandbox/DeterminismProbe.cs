using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 确定性探针：每 tick 把生物的全部可演化状态喂给内核的 <see cref="DeterminismHasher"/>
/// （FNV-1a 64 bit-exact，算法与 smoke/ 无引擎回归共用同一实现），
/// 每隔 checkpoint 打印一次，跑满 runTicks 后打终值。
/// 既有蜥蜴重载保持历史 chunk/limb 序列；并列控制器由适配器追加物种专属隐式状态。
/// </summary>
public sealed class DeterminismProbe
{
    private readonly int _runTicks;
    private readonly int _checkpointInterval;
    private readonly DeterminismHasher _hasher = new();

    public bool Finished { get; private set; }

    /// <summary>当前折叠哈希（跑满后 = 终值,供 --expect-hash 断言)。</summary>
    public ulong Hash => _hasher.Value;

    public DeterminismProbe(int runTicks, int checkpointInterval = 100)
    {
        _runTicks = runTicks;
        _checkpointInterval = checkpointInterval;
    }

    public void Record(long tick, IReadOnlyList<Body> bodies, IReadOnlyList<Limb>? limbs = null,
        IReadOnlyList<VultureWing>? wings = null, IReadOnlyList<Arm>? arms = null)
    {
        if (Finished)
        {
            return;
        }

        foreach (Body body in bodies)
        {
            _hasher.FoldBody(body);
        }
        if (limbs is not null)
        {
            _hasher.FoldLimbs(limbs);
        }
        if (wings is not null)
        {
            _hasher.FoldWings(wings);
        }
        if (arms is not null)
        {
            _hasher.FoldArms(arms);
        }

        FinishTick(tick);
    }

    /// <summary>
    /// 并列控制器入口：身体折叠顺序与既有重载一致，附肢的稳定装配顺序由物种适配器提供。
    /// </summary>
    internal void Record(long tick, IReadOnlyList<Body> bodies, ISandboxCreatureAdapter creature)
    {
        if (Finished)
        {
            return;
        }

        foreach (Body body in bodies)
        {
            _hasher.FoldBody(body);
        }
        creature.FoldDeterministicState(_hasher);
        FinishTick(tick);
    }

    private void FinishTick(long tick)
    {
        if (tick % _checkpointInterval == 0 || tick >= _runTicks)
        {
            GD.Print($"[DET] tick={tick} hash={_hasher.Value:X16}");
        }
        if (tick >= _runTicks)
        {
            Finished = true;
        }
    }
}
