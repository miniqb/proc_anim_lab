using Godot;

namespace ProcAnim.Core.Species.RatFiend;

/// <summary>宿主喂给鼠煞运动内核的确定性翻越阶段；路线选择与阶段推进仍归宿主。</summary>
public enum RatTraversalPhase
{
	Approach,
	MountAndCross,
	Stabilize
}

/// <summary>
/// 单 tick 翻越意图。Approach/Stabilize 的 Target 是地面路径点；MountAndCross 的
/// Target.XZ 是障碍远侧路径点，Target 沿世界 up 的标高是稳定顶面标高。
/// </summary>
public readonly record struct RatTraversalIntent(RatTraversalPhase Phase, Vector3 Target);
