using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 一条腿 = 一个追目标点的粒子（合并 RW BodyPart/Limb/LizardLimb 中走路所需的部分）。
/// 不是解析式多关节 IK：vel = Lerp(vel, 朝目标*HuntSpeed, Quickness)，够近吸附。
/// 步态是 plant-and-trail：脚踩住不动 → 身体前移 → 腿拉伸到极限 → 松开 → 摆到前方
/// 超过 StepLength 阈值 → FindGrip 射线找新落点 → 再踩住。腿长由单侧距离钳制维持，
/// 腿不反推身体——推进力在 Walker 层按抓地腿数另行施加（RW：锚与引擎分离）。
/// 脚不进 Body.Chunks：它是独立受力点，会碰地形但不参与 chunk 间物理（≙ RW 图形层 BodyPart）。
///
/// M3：整套步进几何跑在「支撑系」里——up 参数 = Walker 的支撑法线，不再是重力反方向。
/// 平地 up=世界上（与 M2 全等），爬墙 up=墙法线：落脚射线自动从「朝下」换成「朝墙」，
/// 走/爬同一条代码（研究文档 §12.3「爬墙 = 换射线方向，逻辑一模一样」）。
/// </summary>
public sealed class Limb
{
	// —— 粒子态（语义同 BodyChunk：Vel = 米/tick，LastPos 仅渲染插值/碰撞方向）——
	public Vector3 Pos;
	public Vector3 LastPos;
	public Vector3 Vel;
	public readonly float Radius;

	/// <summary>腿锚定的身体 chunk（≙ RW Limb.connection）。</summary>
	public readonly BodyChunk Anchor;

	/// <summary>横向符号：-1 左 / +1 右（RW 2D 侧视 limbNumber%2 的 3D 化）。</summary>
	public readonly int Side;

	/// <summary>成对的另一条腿（互斥推开 + extraLongStep 协调），构造后由工厂互联。</summary>
	public Limb? Pair;

	// —— plant-and-trail 状态 ——
	/// <summary>当前追逐的世界目标点（≙ absoluteHuntPos）。迈步抓到地形后固定 = 落点。</summary>
	public Vector3 HuntPos;

	/// <summary>true = 正在迈步寻找/踩住地形；false = 摆动期（脚漂向髋前静止姿势位）。</summary>
	public bool ReachingForTerrain;

	/// <summary>HuntPos 是否是 FindGrip 实际找到的地形落点（≙ RW HuntAbsolutePosition 模式）。
	/// false = 还在追摆动期遗留的空中目标。只有真落点才计抓地——否则脚追上空中胡萝卜
	/// 也算「抓稳」，M3 的重力开关会被它骗到悬空关重力、无限爬天。</summary>
	public bool HasGrip { get; private set; }

	/// <summary>本 tick 是否已吸附到目标点。</summary>
	public bool ReachedSnapPosition;

	/// <summary>true = 闲置休息位（≙ RW Limb.Mode.HuntRelativePosition）：无移动意图且连续
	/// 找不到落点（如翻墙登顶后站在棱线上，本侧脚下全是空气）时，脚不再悬在最大前伸位，
	/// 而是垂回锚点身侧。纯姿态——HasGrip 恒 false，不计抓地、不影响重力开关。</summary>
	public bool IdlePose { get; private set; }

	/// <summary>当前抓握面的法线（FindGrip 命中时记录）。Walker 平均它得到支撑法线。</summary>
	public Vector3 GripNormal = Vector3.Up;

	/// <summary>连续踩稳的 tick 数；≥ GripDelay 才算「抓地」计入推进力。</summary>
	public int GripCounter;

	public bool TerrainContact;

	/// <summary>成对腿还没抓稳时本腿延长摆动期（≙ extraLongStep，错开步态）。</summary>
	private bool _extraLongStep;

	/// <summary>延长摆动已持续的 tick 数（超时保险的计时）。</summary>
	private int _extraLongStepTicks;

	/// <summary>延长摆动的只读观测（探针/调试用）。</summary>
	public bool ExtraLongStep => _extraLongStep;

	/// <summary>
	/// 延长摆动的超时上限（tick）。身体被拎到空中时一对腿可能同 tick 双双释放、
	/// 都置 extraLongStep 而互相死等（RW 靠腿随机失能打破，确定性内核没有随机逃生口）；
	/// 正常等待约 5~15 tick，超过即视为死等强制恢复迈步。
	/// </summary>
	private const int ExtraLongStepLimit = 40;

	// —— 参数（≙ LizardBreedParams 子集；M4 收拢成 breed 参数对象）——
	/// <summary>腿长：脚被钳制在锚点这个半径内（≙ jointDist）。</summary>
	public float JointDist = 0.55f;

	/// <summary>每 tick 最大逼近速度（≙ limbSpeed）；追固定落点时自动加上锚点速度。</summary>
	public float HuntSpeed = 0.15f;

	/// <summary>速度插值系数，越大越急促（≙ limbQuickness）。</summary>
	public float Quickness = 0.6f;

	/// <summary>迈步阈值参数 ∈[0,1]：脚摆到髋前 Lerp(-0.5,0.5,·)*JointDist 时触发找落点（≙ stepLength）。</summary>
	public float StepLength = 0.7f;

	/// <summary>摆动期目标向髋部收拢的程度（≙ liftFeet，抬脚感）。</summary>
	public float LiftFeet = 0.2f;

	/// <summary>落点方向的向下偏置（≙ feetDown，脚更贴地）。</summary>
	public float FeetDown = 0.5f;

	/// <summary>落点方向的横向偏置：左右腿各自外撇（≙ legPairDisplacement 的 3D 化）。</summary>
	public float PairLateral = 0.45f;

	public float AirFriction = 0.7f;
	public float SurfaceFriction = 0.4f;

	/// <summary>判定「已抓稳」所需连续 tick 数（≙ limbGripDelay）。</summary>
	public int GripDelay = 4;

	/// <summary>触发闲置休息位所需的连续找不到落点 tick 数（只在无移动意图时累计）。</summary>
	public int IdleAfterTicks = 20;

	/// <summary>连续 FindGrip 失败计数（找到落点或有移动意图即清零）。</summary>
	private int _gripFailTicks;

	/// <summary>抓地中：这条腿正为身体提供锚点/推进（Walker 按此计数施力）。</summary>
	public bool Gripping => GripCounter >= GripDelay;

	private const float Skin = 0.02f;

	/// <summary>吸附/重叠判定余量（RW 用 rad+1px；1px = 0.025m）。</summary>
	private const float OverlapPad = 0.025f;

	/// <summary>FindGrip 沿步进方向的采样偏移（米）——固定顺序保确定性（≙ RW ±20px 步 5 的收敛版）。</summary>
	private static readonly float[] GripSamples = { 0f, -0.125f, 0.125f, -0.25f, 0.25f };

	/// <summary>翻越棱线候选带的单侧偏移（米）：从期望落点水平朝墙内推进——薄墙（0.4m）顶面
	/// 正好被中段探针罩住，竖直射线从其正上方打到顶面。</summary>
	private static readonly float[] CrestSamples = { 0f, 0.125f, 0.25f, 0.375f, 0.5f };

	public Limb(BodyChunk anchor, Vector3 pos, float radius, int side)
	{
		Anchor = anchor;
		Pos = pos;
		LastPos = pos;
		Vel = Vector3.Zero;
		Radius = radius;
		Side = side;
		HuntPos = pos;
	}

	/// <summary>
	/// 推进一个 tick。stepDir = 本 tick 步进方向（身体朝向与移动意图的混合，单位向量）；
	/// up = 支撑法线（平地=世界上，爬墙=墙法线——走/爬的射线换向全由它承载）；
	/// allLimbs/smoothGait/runSpeed 用于多腿步态错开。
	/// 顺序镜像 LizardLimb.Update：状态机 → 成对互斥 → 追目标积分 → 腿长钳制 → 抓地计数。
	/// </summary>
	public void Tick(in TickContext ctx, Vector3 stepDir, Vector3 up,
		IReadOnlyList<Limb> allLimbs, bool smoothGait, float runSpeed)
	{
		// 脚相对锚点沿步进方向的有符号超前量（RW num 的反号：>0 = 脚在髋前）。
		float advance = (Pos - Anchor.Pos).Dot(stepDir);
		float stepThreshold = Mathf.Lerp(-0.5f, 0.5f, StepLength);

		if (!ReachingForTerrain)
		{
			// 摆动期：目标 = 脚向髋收拢 LiftFeet 后再往前 JointDist——脚一路漂向最大前伸位。
			HuntPos = Pos.Lerp(Anchor.Pos, LiftFeet) + stepDir * (JointDist + OverlapPad);
			if (_extraLongStep)
			{
				_extraLongStepTicks++;
				if ((Pair is not null && Pair.GripCounter > GripDelay)
					|| _extraLongStepTicks > ExtraLongStepLimit)
				{
					_extraLongStep = false;
				}
			}
			if (!_extraLongStep && advance > JointDist * stepThreshold)
			{
				ReachingForTerrain = true;
				_gripFailTicks = 0;
			}
		}
		else
		{
			// 闲置休息位：有移动意图立即退出恢复迈步；闲置中目标每 tick 跟着锚点重算
			// （≙ relativeHuntPos 旋进身体系），脚自然垂在身侧随身体漂移。
			// 意图判定统一走 Walker.MoveIntentDeadzone——曾经推进层用 >0、这里用 >0.1，
			// 0.05 的输入会推着一具永远退不出 IdlePose 的身体滑行（评审 P1-5）。
			if (IdlePose && runSpeed > Walker.MoveIntentDeadzone)
			{
				IdlePose = false;
				_gripFailTicks = 0;
			}
			if (IdlePose)
			{
				HuntPos = RestHuntPos(stepDir, up);
			}
			// 没有真落点时持续找（哪怕脚已贴着摆动期遗留的空中目标）；有真落点则等踩到再说。
			else if (!HasGrip || !OverlappingHuntPos())
			{
				FindGrip(ctx, stepDir, up);
				if (HasGrip || runSpeed > Walker.MoveIntentDeadzone)
				{
					_gripFailTicks = 0;
				}
				else if (++_gripFailTicks > IdleAfterTicks)
				{
					IdlePose = true; // 站着没事干还够不着地：收脚休息，别橙色悬在最大前伸位
				}
			}
			else
			{
				// 已踩到落点：等身体走过、腿重新拉伸到极限才松开（plant-and-trail 的 trail）。
				if (advance < JointDist * 0.5f * (StepLength + 0.1f)
					&& (Pos - Anchor.Pos).Length() >= JointDist - OverlapPad
					&& (HuntPos - Anchor.Pos).Length() >= JointDist)
				{
					_extraLongStep = smoothGait && Pair is not null && Pair.GripCounter < 1;
					_extraLongStepTicks = 0;
					ReachingForTerrain = false;
					HasGrip = false;
				}

				// 步态错开：跑动中若本腿抓得最久且其余腿都已抓稳 → 主动松开迈步。
				if (ReachingForTerrain && runSpeed > Walker.MoveIntentDeadzone && smoothGait)
				{
					bool oldestGrip = true;
					foreach (Limb other in allLimbs)
					{
						if (other != this && (other.GripCounter > GripCounter || other.GripCounter == 0))
						{
							oldestGrip = false;
							break;
						}
					}
					if (oldestGrip)
					{
						ReachingForTerrain = false;
						HasGrip = false;
					}
				}
			}
		}

		// 成对腿互斥：两脚过近互相推开（RW 双侧各推一半，两腿的 Tick 都会执行——保持一致）。
		if (Pair is not null)
		{
			Vector3 gap = Pair.Pos - Pos;
			float dist = gap.Length();
			if (dist < Radius * 3f && dist > 1e-6f)
			{
				Vector3 push = gap / dist * ((Radius * 3f - dist) * 0.5f);
				Vel -= push;
				Pair.Vel += push;
			}
		}

		IntegrateHunt(ctx, up);
		ConnectToAnchor();

		// 抓地计数：迈步中吸附到真落点、或贴着真落点且踩实地形，连续累计。
		if (ReachingForTerrain && HasGrip
			&& (ReachedSnapPosition || (OverlappingHuntPos() && TerrainContact)))
		{
			GripCounter++;
		}
		else
		{
			GripCounter = 0;
		}
	}

	/// <summary>整体平移（Walker.Shift 的腿部分）：位置、插值历史、追逐目标同步移动。</summary>
	public void Shift(Vector3 delta)
	{
		Pos += delta;
		LastPos += delta;
		HuntPos += delta;
	}

	/// <summary>强制松开重迈步（Walker 顶死解锁 / Launch 击飞用，≙ RW timeSpentTryingThisMove
	/// 的升级动作）。GripCounter 必须当场清零：Launch 在两个 tick 之间调用，下个 tick 的
	/// UpdateFooting 先于腿更新读 Gripping——残留旧计数会把刚写好的站稳清零又冲掉（终审 C10）。</summary>
	public void ForceRelease()
	{
		ReachingForTerrain = false;
		HasGrip = false;
		IdlePose = false;
		_extraLongStep = false;
		GripCounter = 0;
	}

	/// <summary>休息位（≙ relativeHuntPos 的支撑系版）：锚点沿支撑方向垂下、向本侧微撇。</summary>
	private Vector3 RestHuntPos(Vector3 stepDir, Vector3 up)
	{
		Vector3 side = stepDir.Cross(up);
		side = side.LengthSquared() < 1e-8f ? Vector3.Right : side.Normalized();
		return Anchor.Pos - up * (JointDist * 0.6f) + side * (Side * JointDist * 0.3f);
	}

	private bool OverlappingHuntPos()
	{
		return ReachedSnapPosition || (HuntPos - Pos).Length() < Radius + OverlapPad;
	}

	/// <summary>
	/// 找落点（≙ Limb.FindGrip 的射线版）：把目标方向加上向面/横向偏置得到期望落点 goal，
	/// 候选来自两类固定序射线（≙ RW 9 格邻域搜索的收敛版）：
	/// ① 锚点沿期望方向的直射——面前有墙/陡坡时率先够到其暴露面（平地上通常打空）；
	/// ② goal 周围沿步进方向排开的支撑向投影射线（≙ SnapToTerrain，走=竖直、爬=垂直于墙）。
	/// 统一选「离期望点最近且腿够得着」的命中——离墙远时地面赢、抵墙时墙面赢，
	/// 走→爬的切换从纯几何涌现。找到 → HuntPos 固定为落点（plant）、记录抓握面法线。
	/// </summary>
	private void FindGrip(in TickContext ctx, Vector3 stepDir, Vector3 up)
	{
		// 悬垂/天花板底面的过滤基于世界上方向（支撑系旋转后 up 已不指天）。
		Vector3 worldUp = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;

		Vector3 right = stepDir.Cross(up);
		if (right.LengthSquared() < 1e-8f)
		{
			right = Vector3.Right; // 步进方向与支撑法线共线的退化情形：固定回退方向保确定性
		}
		else
		{
			right = right.Normalized();
		}

		Vector3 dir = stepDir + right * (Side * PairLateral) - up * (0.3f * FeetDown);
		dir = dir.Normalized();
		float maxRadius = JointDist - OverlapPad;
		Vector3 goal = Anchor.Pos + dir * maxRadius;

		// 采样带沿步进方向的面内投影铺开（goal 上下扫的是支撑向射线，面内才需要错开）。
		Vector3 alongSurface = stepDir - up * stepDir.Dot(up);
		alongSurface = alongSurface.LengthSquared() < 1e-8f ? Vector3.Zero : alongSurface.Normalized();

		bool found = false;
		Vector3 best = default;
		Vector3 bestNormal = Vector3.Up;
		float bestDistSq = float.MaxValue;

		void Consider(in TerrainHit hit)
		{
			// 零法线 = 射线起点已陷入地形；朝下的面（悬垂/天花板底面）M3 仍不落脚。
			if (hit.Normal.LengthSquared() < 1e-12f || hit.Normal.Dot(worldUp) < -0.3f)
			{
				return;
			}
			if ((hit.Point - Anchor.Pos).Length() > maxRadius)
			{
				return;
			}
			float distSq = (hit.Point - goal).LengthSquared();
			if (distSq < bestDistSq)
			{
				bestDistSq = distSq;
				best = hit.Point;
				bestNormal = hit.Normal;
				found = true;
			}
		}

		if (ctx.Terrain.Raycast(Anchor.Pos, Anchor.Pos + dir * (maxRadius + Radius), out TerrainHit direct))
		{
			Consider(direct);
		}
		foreach (float offset in GripSamples)
		{
			Vector3 probe = goal + alongSurface * offset;
			if (ctx.Terrain.Raycast(probe + up * 0.35f, probe - up * 0.55f, out TerrainHit hit))
			{
				Consider(hit);
			}
		}

		// 攀爬中（支撑系偏离世界系）补一组世界向下的投影：翻越棱线时支撑向射线还朝墙打空，
		// 这组先够到墙顶/台面——RW 9 格搜索天然全向，跨面翻越靠的就是它（射线版等价物）。
		// 起点沿「支撑法线的水平反方向」（水平指向墙内）单侧排开：goal 本身在墙面外侧，
		// 沿 alongSurface（爬墙时=竖直）铺开会叠成一条悬在空气里的竖线，永远打不到顶面。
		if (up.Dot(worldUp) < 0.95f)
		{
			// 0.95 门限保证水平分量 ≥ sin(18°)，归一化安全。
			Vector3 crestDir = -(up - worldUp * up.Dot(worldUp)).Normalized();
			foreach (float offset in CrestSamples)
			{
				Vector3 probe = goal + crestDir * offset;
				if (ctx.Terrain.Raycast(probe + worldUp * 0.35f, probe - worldUp * 0.55f, out TerrainHit hit))
				{
					Consider(hit);
				}
			}
		}

		if (found)
		{
			HuntPos = best;
			GripNormal = bestNormal;
			HasGrip = true;
		}
		else
		{
			// 没找到：HuntPos 维持原值下 tick 重试（身体在动，goal 会变），但 HasGrip 必须
			// 摘掉——它的语义是「HuntPos 是本次搜索背书的地形点」。留着旧 true 会让闲置
			// 失败计数被永久清零、旧落点变不可达后腿吊死在钳制位（评审「旧落点残留」）。
			HasGrip = false;
		}
	}

	/// <summary>追目标积分（≙ Limb.Update 主体）：吸附或 Lerp 逼近，然后出地形。</summary>
	private void IntegrateHunt(in TickContext ctx, Vector3 up)
	{
		// 追固定世界落点时脚要跟得上移动的身体（≙ huntSpeed += connection.vel.magnitude）。
		float huntSpeed = HuntSpeed + Anchor.Vel.Length();

		LastPos = Pos;
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
		Pos += Vel;
		Vel *= AirFriction;
		PushOutOfTerrain(ctx, up);
	}

	/// <summary>腿长单侧钳制（≙ ConnectToPoint(connection.pos, jointDist)）：只拉脚、不推身体。</summary>
	private void ConnectToAnchor()
	{
		Vector3 delta = Pos - Anchor.Pos;
		float dist = delta.Length();
		if (dist <= JointDist || dist < 1e-6f)
		{
			return;
		}
		Vector3 corr = delta / dist * (dist - JointDist);
		Pos -= corr;
		Vel -= corr;
	}

	/// <summary>脚 vs 地形（≙ BodyPart.PushOutOfTerrain 的射线+MTD 版）：运动扫掠 + 支撑向探面
	/// + 球体重叠去穿透（擦边/嵌入兜底），同 chunk 语义。</summary>
	private void PushOutOfTerrain(in TickContext ctx, Vector3 supportUp)
	{
		TerrainContact = false;
		Vector3 down = -supportUp; // 走=向下探地，爬=向墙探面（脚部版的射线换向）

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

	/// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
	public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
