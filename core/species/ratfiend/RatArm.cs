using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.RatFiend;

/// <summary>
/// 鼠煞的手臂粒子：Humanoid <c>Arm</c> 的同构物种私有版（三模式追猎 + 臂长钳制 +
/// 腋窝排斥 + 出地形，机器逐行同源），额外承载本物种的断肢语义：
/// · <see cref="Severed"/>：断臂标志——上层链只给它 Dangle，粒子本身就是残肢的肘端；
/// · <see cref="EffectiveLength"/>：断后可及半径减半（固定断肘），臂长钳制与目标点钳制都用它。
/// 不直接引用 Humanoid.Arm 的原因：需要改臂长钳制本体，而 Arm.cs 是 humanoid 物种文件——
/// 为新物种动别家后端比动共享层更糟（跨物种模块边界，CLAUDE.md §6.4）。
/// 手不进 Body.Chunks：独立受力点，碰地形但不反推身体。
/// </summary>
public sealed class RatArm
{
	/// <summary>追猎模式（≙ RW Limb.Mode 减去 Slugcat 专用的 Retracted）。</summary>
	public enum ArmMode
	{
		Dangle,
		HuntAbsolutePosition,
		HuntRelativePosition,
	}

	// —— 粒子态（语义同 Limb：Vel = 米/tick，LastPos 仅渲染插值/碰撞方向）——
	public Vector3 Pos;
	public Vector3 LastPos;
	public Vector3 Vel;
	public readonly float Radius;

	/// <summary>手臂锚定的身体 chunk（胸）。</summary>
	public readonly BodyChunk Shoulder;

	/// <summary>横向符号：-1 左 / +1 右。</summary>
	public readonly int Side;

	/// <summary>本 tick 的追猎模式，由控制器优先级链写入（Dangle 下 HuntPos 不参与积分）。</summary>
	public ArmMode Mode = ArmMode.Dangle;

	/// <summary>当前追逐的世界目标点（相对模式由控制器换算成世界坐标写入）。</summary>
	public Vector3 HuntPos;

	/// <summary>爬行撑点（世界坐标，锁定期非 null）：由控制器爬行子链写入/失效清除，
	/// RatArm 只负责 Shift 时随世界平移（≙ Humanoid Arm.GrabPos 的语义位）。</summary>
	public Vector3? GrabPos;

	/// <summary>本 tick 是否已吸附到目标点。</summary>
	public bool ReachedSnapPosition;

	public bool TerrainContact;

	/// <summary>断臂标志（固定断肘）：由 <see cref="RatFiendLocomotionController.Sever"/> 置位，
	/// 单向不可逆。置位后上层链只给 Dangle，本粒子即残肢肘端——渲染/命中判定直接消费。</summary>
	public bool Severed;

	// —— 参数（工厂从 RatFiendParams 拷入）——
	/// <summary>完整臂长（出生值，断臂后不改——可及半径走 <see cref="EffectiveLength"/>）。</summary>
	public float ArmLength = 1.15f;

	/// <summary>断臂后的可及长度比例（= RatFiendParams.SeveredLengthFactor，工厂拷入）。</summary>
	public float SeveredLengthFactor = 0.5f;

	/// <summary>当前可及半径：完好 = 臂长，断臂 = 臂长 × 断口比例（肩→肘残段）。</summary>
	public float EffectiveLength => Severed ? ArmLength * SeveredLengthFactor : ArmLength;

	/// <summary>本 tick 生效的最大逼近速度（帧末复位到 <see cref="DefaultHuntSpeed"/>）。</summary>
	public float HuntSpeed = 0.6f;

	/// <summary>本 tick 生效的速度插值急促度（帧末复位到 <see cref="DefaultQuickness"/>）。</summary>
	public float Quickness = 0.95f;

	public float DefaultHuntSpeed = 0.6f;
	public float DefaultQuickness = 0.95f;

	/// <summary>臂长钳制后速度向肩速靠拢的比例（宿主参考系阻尼）。</summary>
	public float AdaptVel = 0.4f;

	/// <summary>额外叠加的肩速份额（身体加速时手的甩动感）。</summary>
	public float Exaggerate = 0.1f;

	/// <summary>手到肩（胸心）的最小距离——防手穿进躯干。</summary>
	public float ArmpitGap = 0.3f;

	/// <summary>Dangle 模式的自重（米/tick²）。</summary>
	public float DangleGravity = 0.0225f;

	public float AirFriction = 0.99f;
	public float SurfaceFriction = 0.5f;

	private const float Skin = 0.02f;

	public RatArm(BodyChunk shoulder, Vector3 pos, float radius, int side)
	{
		Shoulder = shoulder;
		Pos = pos;
		LastPos = pos;
		Vel = Vector3.Zero;
		Radius = radius;
		Side = side;
		HuntPos = pos;
	}

	/// <summary>
	/// 推进一个 tick（顺序同 Humanoid Arm.Tick）：模式积分 → 臂长钳制（含 exaggerate/adaptVel）
	/// → 腋窝排斥 → 出地形 → 参数帧末复位。控制器必须在调用前写好 Mode/HuntPos。
	/// </summary>
	public void Tick(in TickContext ctx)
	{
		Vector3 down = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? ctx.GravityPerTick.Normalized()
			: Vector3.Down;

		LastPos = Pos;
		switch (Mode)
		{
			case ArmMode.Dangle:
				ReachedSnapPosition = false;
				Vel += down * DangleGravity;
				break;
			case ArmMode.HuntAbsolutePosition:
				// 追固定世界点时手要跟得上移动的身体。
				SeekHuntPos(HuntSpeed + Shoulder.Vel.Length());
				break;
			case ArmMode.HuntRelativePosition:
				SeekHuntPos(HuntSpeed);
				break;
		}
		Pos += Vel;
		if (Mode == ArmMode.HuntRelativePosition)
		{
			// 完全跟随宿主：目标本身随身体走，手不该因身体移动而落后。
			Pos += Shoulder.Vel;
		}
		Vel *= AirFriction;

		ConnectToShoulder();
		RepelFromShoulder();
		PushOutOfTerrain(ctx, -down);

		// 帧末复位：上层的单帧提速/降速覆盖不跨 tick 残留。
		HuntSpeed = DefaultHuntSpeed;
		Quickness = DefaultQuickness;
	}

	/// <summary>追目标积分：够近吸附，否则 Lerp 逼近。</summary>
	private void SeekHuntPos(float huntSpeed)
	{
		if ((HuntPos - Pos).Length() < huntSpeed)
		{
			Vel = HuntPos - Pos;
			ReachedSnapPosition = true;
		}
		else
		{
			Vel = Vel.Lerp((HuntPos - Pos).Normalized() * huntSpeed, Quickness);
			ReachedSnapPosition = false;
		}
	}

	/// <summary>臂长单侧钳制（只拉手不推身体）：先叠肩速份额、再硬钳距离、最后速度向肩速靠拢。
	/// 钳制半径用 <see cref="EffectiveLength"/>——断臂后残段自动缩到肩→肘。
	/// Dangle 跳过肩速两项：垂摆的手臂只受自重与臂长约束自由甩动。</summary>
	private void ConnectToShoulder()
	{
		bool dangle = Mode == ArmMode.Dangle;
		if (!dangle)
		{
			Vel += Shoulder.Vel * Exaggerate;
		}
		float reach = EffectiveLength;
		Vector3 delta = Pos - Shoulder.Pos;
		float dist = delta.Length();
		if (dist > reach && dist > 1e-6f)
		{
			Vector3 corr = delta / dist * (dist - reach);
			Pos -= corr;
			Vel -= corr;
		}
		if (!dangle)
		{
			Vel = Vel.Lerp(Shoulder.Vel, AdaptVel);
		}
	}

	/// <summary>腋窝排斥：手离胸心太近就硬推开（Pos/Vel 同步）。</summary>
	private void RepelFromShoulder()
	{
		Vector3 gap = Pos - Shoulder.Pos;
		float dist = gap.Length();
		if (dist >= ArmpitGap)
		{
			return;
		}
		// 手与胸心重合的退化情形：固定回退方向保确定性。
		Vector3 dir = dist < 1e-6f ? Vector3.Up : gap / dist;
		Vector3 push = dir * (ArmpitGap - dist);
		Pos += push;
		Vel += push;
	}

	/// <summary>手 vs 地形（同 Limb.PushOutOfTerrain 三段：运动扫掠 + 向下探面 + 球体 MTD 兜底）。</summary>
	private void PushOutOfTerrain(in TickContext ctx, Vector3 up)
	{
		TerrainContact = false;
		Vector3 down = -up;

		Vector3 motion = Pos - LastPos;
		float motionLen = motion.Length();
		if (motionLen > 1e-6f)
		{
			Vector3 dir = motion / motionLen;
			if (ctx.Terrain.Raycast(LastPos, Pos + dir * Radius, out TerrainHit hit1)
				&& SphereTerrain.Resolve(hit1, Radius, SurfaceFriction, ref Pos, ref Vel, out _))
			{
				TerrainContact = true;
			}
		}

		if (ctx.Terrain.Raycast(Pos, Pos + down * (Radius + Skin), out TerrainHit hit2)
			&& SphereTerrain.Resolve(hit2, Radius, SurfaceFriction, ref Pos, ref Vel, out _))
		{
			TerrainContact = true;
		}

		if (ctx.Terrain.SpherePenetration(Pos, Radius, out Vector3 pushDir, out float depth))
		{
			Pos += pushDir * depth;
			SphereTerrain.RespondVelocity(pushDir, SurfaceFriction, ref Vel);
			TerrainContact = true;
		}
	}

	/// <summary>整体平移：位置、插值历史、追逐目标、撑点全部随世界移动。</summary>
	public void Shift(Vector3 delta)
	{
		Pos += delta;
		LastPos += delta;
		HuntPos += delta;
		if (GrabPos is { } gp)
		{
			GrabPos = gp + delta;
		}
	}

	/// <summary>强制松开（Teleport/Launch/Sever 用）：撑点作废、回 Dangle，下 tick 由链重新接管。</summary>
	public void ForceRelease()
	{
		GrabPos = null;
		Mode = ArmMode.Dangle;
		ReachedSnapPosition = false;
	}

	/// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
	public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
