using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Humanoid;

/// <summary>
/// 一条手臂 = 一个追目标点的粒子（≙ RW Limb 基类的追猎核心 + ScavengerHand 的机械部分）。
/// 与 <see cref="Limb"/>（蜥蜴腿）平行的第二种肢体：腿的机器是 plant-and-trail 步态状态机，
/// 手臂的机器是「三模式追猎」——模式由上层控制器的优先级链每 tick 写入：
/// · Dangle：无目标自由垂摆（自重 + 臂长约束），≙ RW Limb.Mode.Dangle；
/// · HuntAbsolutePosition：追固定世界点（撑地锁点/指向/投掷），追时补肩速，≙ RW 同名模式；
/// · HuntRelativePosition：追随身体移动的目标点（持物位/休息位——上层已换算成世界坐标），
///   积分后额外 Pos += 肩速（完全跟随宿主），≙ RW 同名模式的 pos += connection.vel。
/// 手不进 Body.Chunks：独立受力点，碰地形但不反推身体（≙ RW 图形层 BodyPart 语义，与 Limb 同约定）。
/// HuntSpeed/Quickness 帧末复位到 Default*（≙ RW Limb.Update 尾部复位）——上层可以随手做
/// 「这一帧提速」的覆盖，不用管恢复。
/// </summary>
public sealed class Arm
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

	/// <summary>手臂锚定的身体 chunk（≙ RW ScavengerHand.connection = mainBodyChunk 胸）。</summary>
	public readonly BodyChunk Shoulder;

	/// <summary>横向符号：-1 左 / +1 右。</summary>
	public readonly int Side;

	/// <summary>本 tick 的追猎模式，由控制器优先级链写入（Dangle 下 HuntPos 不参与积分）。</summary>
	public ArmMode Mode = ArmMode.Dangle;

	/// <summary>当前追逐的世界目标点（≙ absoluteHuntPos；相对模式由控制器换算成世界坐标写入）。</summary>
	public Vector3 HuntPos;

	/// <summary>指关节撑点（世界坐标，锁定期非 null；≙ ScavengerHand.grabPos）。
	/// 由控制器扇扫写入/失效清除，Arm 只负责 Shift 时随世界平移。</summary>
	public Vector3? GrabPos;

	/// <summary>本 tick 是否已吸附到目标点（≙ reachedSnapPosition——RW 用它切换握拳/张开）。</summary>
	public bool ReachedSnapPosition;

	public bool TerrainContact;

	// —— 参数（工厂从 HumanoidParams 拷入）——
	/// <summary>臂长：手被钳制在肩点这个半径内（≙ ScavengerHand.armLength 50px = 1.25m）。</summary>
	public float ArmLength = 1.25f;

	/// <summary>本 tick 生效的最大逼近速度（帧末复位到 <see cref="DefaultHuntSpeed"/>）。</summary>
	public float HuntSpeed = 0.6f;

	/// <summary>本 tick 生效的速度插值急促度（帧末复位到 <see cref="DefaultQuickness"/>）。</summary>
	public float Quickness = 0.95f;

	/// <summary>默认逼近速度（≙ defaultHuntSpeed 24px = 0.6 m/tick，全身最敏捷的部件）。</summary>
	public float DefaultHuntSpeed = 0.6f;

	/// <summary>默认急促度（≙ defaultQuickness 0.95）。</summary>
	public float DefaultQuickness = 0.95f;

	/// <summary>臂长钳制后速度向肩速靠拢的比例（≙ ConnectToPoint 的 adaptVel 0.4——宿主参考系阻尼）。</summary>
	public float AdaptVel = 0.4f;

	/// <summary>额外叠加的肩速份额（≙ ConnectToPoint 的 exaggerateVel 0.1——身体加速时手的甩动感）。</summary>
	public float Exaggerate = 0.1f;

	/// <summary>手到肩（胸心）的最小距离（≙ RW 腋窝排斥：手离肩点 &lt;10px 硬推开）——防手穿进躯干。</summary>
	public float ArmpitGap = 0.3f;

	/// <summary>Dangle 模式的自重（米/tick²，≙ RW vel.y -= 0.9px）。</summary>
	public float DangleGravity = 0.0225f;

	public float AirFriction = 0.99f;
	public float SurfaceFriction = 0.5f;

	private const float Skin = 0.02f;

	public Arm(BodyChunk shoulder, Vector3 pos, float radius, int side)
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
	/// 推进一个 tick。顺序镜像 RW Limb.Update + BodyPart.ConnectToPoint：
	/// 模式积分 → 臂长钳制（含 exaggerate/adaptVel）→ 腋窝排斥 → 出地形 → 参数帧末复位。
	/// 控制器必须在调用前写好 Mode/HuntPos（及可选的单帧 HuntSpeed/Quickness 覆盖）。
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
				// 追固定世界点时手要跟得上移动的身体（≙ Limb 的 huntSpeed += connection.vel）。
				SeekHuntPos(HuntSpeed + Shoulder.Vel.Length());
				break;
			case ArmMode.HuntRelativePosition:
				SeekHuntPos(HuntSpeed);
				break;
		}
		Pos += Vel;
		if (Mode == ArmMode.HuntRelativePosition)
		{
			// 完全跟随宿主（≙ RW HuntRelativePosition 的 pos += connection.vel）：
			// 目标本身随身体走，手不该因身体移动而落后。
			Pos += Shoulder.Vel;
		}
		Vel *= AirFriction;

		ConnectToShoulder();
		RepelFromShoulder();
		PushOutOfTerrain(ctx, -down);

		// 帧末复位（≙ RW Limb.Update 尾部）：上层的单帧提速/降速覆盖不跨 tick 残留。
		HuntSpeed = DefaultHuntSpeed;
		Quickness = DefaultQuickness;
	}

	/// <summary>追目标积分（≙ Limb.Update 主体）：够近吸附，否则 Lerp 逼近。</summary>
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

	/// <summary>臂长单侧钳制（≙ ConnectToPoint(胸, armLength, push:false, elastic:0,
	/// hostVel, adaptVel, exaggerateVel)）：先叠肩速份额（甩动感）、再硬钳距离（只拉手不推身体）、
	/// 最后速度向肩速靠拢（宿主参考系阻尼）。RW 原序。
	/// Dangle 跳过肩速两项（≙ RW Dangle 分支传 hostVel=(0,0)、adaptVel=0、exaggerate=0）：
	/// 垂摆的手臂只受自重与臂长约束自由甩动——跟着肩速走就不是「垂摆」而是僵直随行了。</summary>
	private void ConnectToShoulder()
	{
		bool dangle = Mode == ArmMode.Dangle;
		if (!dangle)
		{
			Vel += Shoulder.Vel * Exaggerate;
		}
		Vector3 delta = Pos - Shoulder.Pos;
		float dist = delta.Length();
		if (dist > ArmLength && dist > 1e-6f)
		{
			Vector3 corr = delta / dist * (dist - ArmLength);
			Pos -= corr;
			Vel -= corr;
		}
		if (!dangle)
		{
			Vel = Vel.Lerp(Shoulder.Vel, AdaptVel);
		}
	}

	/// <summary>腋窝排斥（≙ ScavengerHand.Update 链尾无条件段）：手离胸心太近就硬推开
	/// （Pos/Vel 同步，RW 原语义）。RW 用平移出的肩点，3D 化取胸心——目的相同：手不穿躯干。</summary>
	private void RepelFromShoulder()
	{
		Vector3 gap = Pos - Shoulder.Pos;
		float dist = gap.Length();
		if (dist >= ArmpitGap)
		{
			return;
		}
		// 手与胸心重合的退化情形：固定回退方向保确定性（与内核 Dir 约定一致）。
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

	/// <summary>整体平移（HumanoidLocomotionController.Shift 的手臂部分）：位置、插值历史、
	/// 追逐目标、撑点全部随世界移动。</summary>
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

	/// <summary>强制松开（Teleport/Launch 用）：撑点作废、回 Dangle，下 tick 由优先级链重新接管。</summary>
	public void ForceRelease()
	{
		GrabPos = null;
		Mode = ArmMode.Dangle;
		ReachedSnapPosition = false;
	}

	/// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
	public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
