using Godot;

namespace ProcAnim.Core.Species.RatFiend;

/// <summary>肢体标识（断肢 API 与部位观测共用）。序号即 Arms/Legs 列表内的下标偏移：
/// ArmLeft/ArmRight ↔ Arms[0/1]，LegLeft/LegRight ↔ Legs[0/1]（工厂按 [-1,+1] 侧序装配）。</summary>
public enum RatFiendLimbId
{
	ArmLeft = 0,
	ArmRight = 1,
	LegLeft = 2,
	LegRight = 3,
}

/// <summary>
/// 断肢交接结构：<see cref="RatFiendLocomotionController.Sever"/> 的返回值，宿主用它播种
/// 断落装饰物（Daddy SeveredPiece 先例——Pos/LastPos/Vel 原样交接，断落瞬间渲染插值无缝）。
/// 末端粒子 = 断前的手/脚；断口点（肘/膝）是渲染派生量，由宿主按与渲染件同源的关节公式
/// 当 tick 解出（内核的腿/臂是单粒子，没有真实的关节 LastPos 可给）。
/// </summary>
public readonly struct RatFiendSeveredLimbState
{
	/// <summary>断前末端粒子（手/脚）状态原样。</summary>
	public readonly Vector3 TipPos;
	public readonly Vector3 TipLastPos;
	public readonly Vector3 TipVel;

	/// <summary>断落段长度（肘→手 / 膝→脚 = 原肢长 × SeveredLengthFactor）。</summary>
	public readonly float SeveredLength;

	/// <summary>末端粒子半径（手/脚）。</summary>
	public readonly float TipRadius;

	public RatFiendSeveredLimbState(Vector3 tipPos, Vector3 tipLastPos, Vector3 tipVel,
		float severedLength, float tipRadius)
	{
		TipPos = tipPos;
		TipLastPos = tipLastPos;
		TipVel = tipVel;
		SeveredLength = severedLength;
		TipRadius = tipRadius;
	}
}
