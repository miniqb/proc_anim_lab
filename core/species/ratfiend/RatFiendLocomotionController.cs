using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.RatFiend;

/// <summary>
/// 鼠煞（RatFiend）运动控制器 = Body（胸/髋/头三 chunk）+ 双 Limb 腿 + 双 RatArm 手臂 +
/// 倾斜站立力偶。机制基底 = Humanoid（清醒近地失重伺服木偶，零 knockdown 状态机），
/// 三处物种新造：
/// · **常态驼背**：站立力偶的目标姿态轴从世界上方向朝 Facing 倾斜 HunchAngle——失重伺服
///   体制下整个平衡态被倾斜，静止/走/跑全程驼背（显式姿态轴；阻尼差静止时零输出，做不了常态）。
///   头伺服/髋伺服/重力开关/撑地判定仍用世界上方向。
/// · **走↔跑连续混合**：进哈希的平滑标量 Gait 追 RunSpeed，姿态权重 r = InverseLerp(Gait)——
///   头耷拉↔抬头、双手垂摆↔前伸、嘴微张↔大张全部按 r 连续混合，无 gait 枚举（涌现惯例）。
///   Gait 必须内核平滑并进哈希：手臂目标点是物理量（RatArm 碰地形、断肢交接读它），
///   姿态平滑交给渲染层会让物理端目标点瞬跳、双跑不定。
/// · **断肢与爬行**：Sever 固定断肘/膝（标志位 + 有效长度减半，不换实例——单粒子肢体
///   没有段数自由度要重建）。断任一腿 → CanStand 假 → 重力回归、直立伺服静默，摔倒涌现；
///   爬行 = 重力常开 + 手臂 plant-and-trail 撑地 + 推进 ∝ 抓地肢体数（Vulture CrawlPropulsion
///   推广）；四肢全断 = 周期性内力偶蠕动（动量守恒，靠摩擦整流出近零位移的挣扎）。
/// 攻击接缝走最小面（玩法 100% 宿主侧，Daddy 竞技场惯例）：输入 GrabTarget/MouthDrive，
/// 观测 HandsOnTarget/MouthOpen——抓住判定、咬合计时、位置修正全在宿主。
/// 确定性：零随机零墙钟；蠕动相位用 TickIndex。
/// </summary>
public sealed class RatFiendLocomotionController
{
	public readonly Body Body;

	/// <summary>胸 = 主根（最重）；髋 = 贴地承重点；头 = 极轻的观察端。</summary>
	public readonly BodyChunk Chest;
	public readonly BodyChunk Hips;
	public readonly BodyChunk Head;

	/// <summary>双腿（复用蜥蜴 Limb，anchor=髋；up 恒传世界上）。[0]=左 / [1]=右。</summary>
	public readonly List<Limb> Legs = new();

	/// <summary>双臂（物种私有 RatArm，anchor=胸）。[0]=左 / [1]=右。</summary>
	public readonly List<RatArm> Arms = new();

	// —— 输入面（与人形同三旋钮 + 鼠煞专属）——
	/// <summary>移动意图方向（世界系，内核自行压平到水平面）。</summary>
	public Vector3 MoveDir;

	/// <summary>移动意图强度 ∈ [0,1]。死区语义与蜥蜴共用同一常量。</summary>
	public float RunSpeed;

	/// <summary>可选第三旋钮：路径点直喂（到点判定用水平距离）。</summary>
	public Vector3? MoveTarget;

	public float MoveTargetArriveRadius = 0.4f;

	public bool AtMoveTarget { get; private set; }

	public const float MoveIntentDeadzone = LizardLocomotionController.MoveIntentDeadzone;

	public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
		&& (MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

	/// <summary>意识开关（同人形语义）：false 时全部伺服/推进/手臂链停摆，身体只剩重力与约束。</summary>
	public bool Conscious = true;

	/// <summary>攻击抓取目标（世界点，宿主逐 tick 写/清）：非 null 且清醒时双臂脱离步态摆动、
	/// 沿「胸→目标」前伸（可及半径钳制），头 aim 转向目标。抓住判定归宿主（读 HandsOnTarget）。</summary>
	public Vector3? GrabTarget;

	/// <summary>嘴开度的宿主驱动分量 ∈ [0,1]（咬合时拉满；叠加在 Gait 派生的基础开度上）。</summary>
	public float MouthDrive;

	// —— 参数快照（出生冻结，运行时只读）——
	private readonly RatFiendParams _p;

	// —— 观测面（全部只读）——
	/// <summary>躯干轴与**倾斜姿态轴**的点积 ∈ [-1,1]（站立力偶的误差源——1 = 驼背平衡态）。</summary>
	public float Uprightness { get; private set; } = 1f;

	public int GroundedCounter { get; private set; }

	public bool Grounded => GroundedCounter >= _p.GroundedTicks;

	/// <summary>true = 重力在拽（坠落/昏迷/爬行）；false = 清醒近地可站立（失重伺服态）。</summary>
	public bool ApplyGravity { get; private set; } = true;

	/// <summary>水平朝向（单位向量）：有移动意图时 = 压平后的意图方向，无意图时保持上次值。</summary>
	public Vector3 Facing { get; private set; } = Vector3.Right;

	/// <summary>走↔跑平滑标量 ∈ [0,1]（进哈希）：LerpAndTick 追「清醒有意图且可站立 ? RunSpeed : 0」。</summary>
	public float Gait { get; private set; }

	/// <summary>上一 tick 的 Gait（渲染插值 MouthOpen/RunBlend 用；派生历史，不进哈希）。</summary>
	public float LastGait { get; private set; }

	/// <summary>走→跑姿态权重 r ∈ [0,1]（Gait 的映射；头高/前伸量/嘴开度都按它混合）。</summary>
	public float RunBlend => RunBlendOf(Gait);

	/// <summary>爬行平滑标量 ∈ [0,1]（进哈希；渲染 dorsal 混合/肘 pole 翻转的连续驱动）。</summary>
	public float CrawlFactor { get; private set; }

	/// <summary>上一 tick 的 CrawlFactor（渲染插值用）。</summary>
	public float LastCrawlFactor { get; private set; }

	/// <summary>双腿俱在才能直立（断任一腿 → 摔倒 + 爬行）。</summary>
	public bool CanStand => !_severed[(int)RatFiendLimbId.LegLeft]
		&& !_severed[(int)RatFiendLimbId.LegRight];

	/// <summary>爬行态 = 清醒且不能直立。</summary>
	public bool Crawling => Conscious && !CanStand;

	/// <summary>嘴开度 ∈ [0,1]：基础微张 + 跑步增量（随 r）+ 宿主 MouthDrive。纯派生量。</summary>
	public float MouthOpen => MouthValueOf(Gait);

	/// <summary>上一 tick 语义的嘴开度（渲染 lerp(LastMouthOpen, MouthOpen, alpha) 用）。</summary>
	public float LastMouthOpen => MouthValueOf(LastGait);

	/// <summary>当前抓地肢体数（爬行推进的引擎计数：撑稳的手 + 抓稳的存活腿；直立时也计，供观测）。</summary>
	public int CrawlGripCount { get; private set; }

	/// <summary>断肢事件序号（单调递增；渲染抽搐/宿主特效按沿触发，避免轮询 bool 丢事件）。</summary>
	public int SeverSerial { get; private set; }

	public int SeveredLimbCount { get; private set; }

	/// <summary>逐手「已抓住 GrabTarget」观测（[0]=左 / [1]=右；距目标 &lt; GrabContactRadius）。</summary>
	public readonly bool[] HandsOnTarget = new bool[2];

	// —— 消融开关（默认全 true，仅供 smoke 消融红灯使用，宿主不要在运行时改动）——
	/// <summary>false = 站立力偶目标轴退回世界上方向（驼背消失）——验证驼背来自显式倾角。</summary>
	public bool EnableHunchTilt = true;

	/// <summary>false = 爬行推进力改为常数引擎（相当于恒 3 肢）——验证「推进 ∝ 抓地肢体数」。</summary>
	public bool EnableCrawlGripScaling = true;

	// —— 私有状态 ——
	private readonly bool[] _severed = new bool[4];

	/// <summary>探地射线常量（Humanoid 同款）。</summary>
	private const float HipProbeRise = 0.5f;
	private const float HipProbeLead = 0.3f;

	public RatFiendLocomotionController(Body body, BodyChunk chest, BodyChunk hips, BodyChunk head,
		RatFiendParams parameters, Vector3 forward)
	{
		Body = body;
		Chest = chest;
		Hips = hips;
		Head = head;
		_p = parameters;
		Facing = forward;
	}

	/// <summary>出生参数快照的只读暴露（宿主/渲染读体格与判定半径；运行时不可写）。</summary>
	public RatFiendParams Params => _p;

	public bool IsSevered(RatFiendLimbId id) => _severed[(int)id];

	// ================================================================== 断肢

	/// <summary>
	/// 断肢（固定断肘/膝）：宿主（枪击）触发。返回末端粒子状态供宿主播种断落装饰物
	/// （Pos/LastPos/Vel 原样交接——断落瞬间渲染插值无缝，Daddy 先例）。
	/// · 臂：置 Severed 标志 + ForceRelease——此后链只给 Dangle，可及半径减半（肩→肘残段），
	///   粒子本身就是残肢肘端。走路不受影响（臂本不承力）。
	/// · 腿：ForceRelease + JointDist 减半 + 停用步态机（此后由控制器手动积分残肢膝端粒子——
	///   半长 FindGrip 落点会污染抓地计数）；**存活腿 Pair 置 null**——否则 Lookahead 释放
	///   guard 的 Pair.GripCounter 被冻结为假，存活腿每步拖到 1.75× 失效阀才松。
	///   CanStand 变假 → 下 tick 起重力回归、直立伺服静默，摔倒涌现（零过渡状态机）。
	/// 已断即抛（Daddy 先例：宿主不该叠断）；昏迷也可断。staggerImpulse 默认零（opt-in，
	/// 回归路线不传它 → 基线不受影响）：命中冲击的踉跄，臂断加到胸、腿断加到髋。
	/// 单向不可逆（本物种不做接肢；重开 = 宿主重建控制器）。
	/// </summary>
	public RatFiendSeveredLimbState Sever(RatFiendLimbId id, Vector3 staggerImpulse = default)
	{
		int index = (int)id;
		if (_severed[index])
		{
			throw new InvalidOperationException($"肢体 {id} 已断——不可叠断");
		}
		_severed[index] = true;
		SeveredLimbCount++;
		SeverSerial++;

		RatFiendSeveredLimbState state;
		if (id is RatFiendLimbId.ArmLeft or RatFiendLimbId.ArmRight)
		{
			RatArm arm = Arms[index];
			state = new RatFiendSeveredLimbState(arm.Pos, arm.LastPos, arm.Vel,
				arm.ArmLength * _p.SeveredLengthFactor, arm.Radius);
			arm.Severed = true;
			arm.ForceRelease();
			Chest.Vel += staggerImpulse;
		}
		else
		{
			int legIndex = index - (int)RatFiendLimbId.LegLeft;
			Limb leg = Legs[legIndex];
			state = new RatFiendSeveredLimbState(leg.Pos, leg.LastPos, leg.Vel,
				leg.JointDist * _p.SeveredLengthFactor, leg.Radius);
			leg.ForceRelease();
			leg.JointDist *= _p.SeveredLengthFactor;
			Limb other = Legs[1 - legIndex];
			if (!LegSevered(1 - legIndex))
			{
				other.Pair = null;
			}
			Hips.Vel += staggerImpulse;
		}
		return state;
	}

	private bool LegSevered(int legIndex) => _severed[(int)RatFiendLimbId.LegLeft + legIndex];

	private bool ArmSevered(int armIndex) => _severed[armIndex];

	// ================================================================== 生命周期

	/// <summary>rebase：身体、腿、手臂、各目标整体平移，速度/撑地/断肢状态原样保留。</summary>
	public void Shift(Vector3 delta)
	{
		Body.Shift(delta);
		foreach (Limb leg in Legs)
		{
			leg.Shift(delta);
		}
		foreach (RatArm arm in Arms)
		{
			arm.Shift(delta);
		}
		if (MoveTarget is { } fed)
		{
			MoveTarget = fed + delta;
		}
		if (GrabTarget is { } grab)
		{
			GrabTarget = grab + delta;
		}
	}

	/// <summary>teleport：平移并作废一切位置态记忆（断肢状态保留——它不是位置态）。</summary>
	public void Teleport(Vector3 delta)
	{
		Shift(delta);
		foreach (Limb leg in Legs)
		{
			leg.ForceRelease();
		}
		foreach (RatArm arm in Arms)
		{
			arm.ForceRelease();
		}
		GroundedCounter = 0;
		MoveTarget = null;
		AtMoveTarget = false;
		GrabTarget = null;
	}

	/// <summary>宿主冲量注入（击飞）：全 chunk 同一速度增量，腿手松开、撑地清零；
	/// 落地后站立力偶（或爬行链）自动恢复。</summary>
	public void Launch(Vector3 velocityPerTick)
	{
		foreach (BodyChunk c in Body.Chunks)
		{
			c.Vel += velocityPerTick;
		}
		foreach (Limb leg in Legs)
		{
			leg.ForceRelease();
		}
		foreach (RatArm arm in Arms)
		{
			arm.ForceRelease();
		}
		GroundedCounter = 0;
	}

	// ================================================================== 主循环

	/// <summary>
	/// 唯一入口。固定驱动序（鼠煞的确定性契约）：
	/// 直喂导出 → 撑地判定/重力开关 → 意图压平/滑墙 → Gait/CrawlFactor 平滑 → 抓地计数 →
	/// [可站立：倾斜站立力偶+头伺服+髋伺服+推进 | 爬行：爬行推进/蠕动+头伺服] →
	/// 逐 chunk 阻尼 → Body.Tick → 腿（存活步态/残肢积分）→ 手臂优先级链 → 观测量。
	/// </summary>
	public void Tick(in TickContext ctx)
	{
		Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;

		bool derivedMove = MoveTarget is not null;
		if (MoveTarget is { } fed)
		{
			DeriveMoveFromTarget(fed, up);
		}
		else
		{
			AtMoveTarget = false;
		}

		UpdateGrounded(ctx, up, out bool groundHit, out Vector3 groundPoint);

		// 爬台阶（手拉体升）的输入：撑稳手的最高标高（读上一 tick 收尾的手臂状态，固定序）。
		// 撑点高出胸底死区外 = 正在攀台阶 → 豁免滑墙——台阶立面对爬行躯干是「墙」，
		// 正对时滑墙会把意图消成零（头对头 slid 长度归零），推进与拽升双双熄火。
		float? climbGrip = Crawling ? MaxPlantedHandHeight(up) : null;
		bool climbing = climbGrip is { } gripH
			&& gripH - (Chest.Pos.Dot(up) - Chest.TerrainRadius) > _p.CrawlClimbDeadzone;

		Vector3 effMove = Conscious && HasMoveIntent
			? FlattenToGround(MoveDir, up)
			: Vector3.Zero;
		if (effMove != Vector3.Zero && !climbing)
		{
			effMove = SlideAlongWalls(effMove, up);
		}
		if (effMove != Vector3.Zero)
		{
			// 站立瞬时置向（腿+站立力偶立刻把身体带过来）；爬行限速回转——身体是贴地
			// 水平链，瞬时翻向会让头伺服把头从裆下拽过去（头髋穿插），限速后画弧调头。
			Facing = CanStand
				? effMove
				: SlewFacing(Facing, effMove, up, _p.CrawlTurnRatePerTick);
		}

		LastGait = Gait;
		float gaitTarget = Conscious && HasMoveIntent && CanStand ? RunSpeed : 0f;
		Gait = LerpAndTick(Gait, gaitTarget, _p.GaitLerp, _p.GaitTickStep);
		LastCrawlFactor = CrawlFactor;
		CrawlFactor = LerpAndTick(CrawlFactor, Crawling ? 1f : 0f,
			_p.CrawlFactorLerp, _p.CrawlFactorTickStep);

		// 抓地计数读上一 tick 收尾的肢体状态（腿 GripCounter / 手撑稳三条件）——
		// 推进消费在 Body.Tick 之前、肢体更新之后的一 tick 滞后是确定性的固定序。
		CrawlGripCount = CountGrips();

		Vector3 postureUp = PostureUp(up);
		Uprightness = ComputeUprightness(postureUp);

		if (Conscious)
		{
			if (CanStand)
			{
				ApplyStandCouple(postureUp);
				ApplyWalkLean(effMove);
				ApplyHeadServo(up);
				ApplyHipServo(up, groundHit, groundPoint);
				ApplyLocomotionForce(effMove);
			}
			else
			{
				ApplyCrawl(ctx, up, effMove, climbGrip);
				ApplyHeadServo(up);
			}
		}
		ApplyChunkDamping();
		Body.Tick(ctx);
		TickLegs(ctx, up, effMove);
		TickArms(ctx, up, effMove);
		Uprightness = ComputeUprightness(PostureUp(up));
		UpdateHandsOnTarget();

		if (derivedMove)
		{
			// 派生 MoveDir 不跨 tick 残留（同蜥蜴/人形约定）。
			MoveDir = Vector3.Zero;
		}
	}

	/// <summary>直喂旋钮的意图导出（地面生物：水平距离判到点、水平方向当意图）。</summary>
	private void DeriveMoveFromTarget(Vector3 target, Vector3 up)
	{
		Vector3 d = target - Chest.Pos;
		Vector3 dh = d - up * d.Dot(up);
		AtMoveTarget = dh.Length() <= MoveTargetArriveRadius;
		MoveDir = AtMoveTarget || dh.LengthSquared() < 1e-12f ? Vector3.Zero : dh.Normalized();
	}

	/// <summary>撑地判定 + 重力开关 + 摩擦切档（Humanoid 同构，法线门统一 ≥0.5）。
	/// 鼠煞差异：重力开关多一个 CanStand 门——断腿后失重伺服态永不成立（爬行重力常开），
	/// 摔倒由此涌现。Grounded 本身照常计（爬行手撑地的入场门与摩擦切档用它）。</summary>
	private void UpdateGrounded(in TickContext ctx, Vector3 up, out bool groundHit, out Vector3 groundPoint)
	{
		groundHit = false;
		groundPoint = default;
		bool leading = Conscious && HasMoveIntent;
		Vector3 origin = Hips.Pos + (leading ? Facing * HipProbeLead : Vector3.Zero);
		if (ctx.Terrain.Raycast(origin + up * HipProbeRise,
				origin - up * (_p.HipRideHeight + _p.GroundProbeSlack), out TerrainHit hit)
			&& IsGroundNormal(hit.Normal, up))
		{
			groundHit = true;
			groundPoint = hit.Point;
		}
		else if (leading
			&& ctx.Terrain.Raycast(Hips.Pos + up * HipProbeRise,
				Hips.Pos - up * (_p.HipRideHeight + _p.GroundProbeSlack), out TerrainHit fallback)
			&& IsGroundNormal(fallback.Normal, up))
		{
			groundHit = true;
			groundPoint = fallback.Point;
		}

		bool contact = groundHit
			|| (Chest.TerrainContact && IsGroundNormal(Chest.ContactNormal, up))
			|| (Hips.TerrainContact && IsGroundNormal(Hips.ContactNormal, up));
		GroundedCounter = contact ? Mathf.Min(GroundedCounter + 1, 100) : 0;
		ApplyGravity = !(Conscious && Grounded && CanStand);
		Body.GravityScale = ApplyGravity ? 1f : 0f;
		Body.SurfaceFriction = Grounded ? _p.FootedSurfaceFriction : _p.AirborneSurfaceFriction;
	}

	private static bool IsGroundNormal(Vector3 normal, Vector3 up) =>
		normal.LengthSquared() > 1e-12f && normal.Dot(up) >= 0.5f;

	// ================================================================== 姿态

	/// <summary>倾斜姿态轴：世界上方向朝 Facing 倾斜 HunchAngle——驼背平衡态的目标。</summary>
	private Vector3 PostureUp(Vector3 up)
	{
		if (!EnableHunchTilt)
		{
			return up;
		}
		float theta = Mathf.DegToRad(_p.HunchAngleDegrees);
		Vector3 tilted = up * Mathf.Cos(theta) + Facing * Mathf.Sin(theta);
		return tilted.LengthSquared() < 1e-8f ? up : tilted.Normalized();
	}

	/// <summary>站立力偶（Humanoid 同构，误差与施力轴改为倾斜姿态轴）：失重下它永远赢——
	/// 平衡点本身就是驼背（胸稳定停在髋前上方 sin(HunchAngle)×躯干长处），站/走/跑全程保持。</summary>
	private void ApplyStandCouple(Vector3 postureUp)
	{
		float couple = Mathf.Remap(Uprightness, -1f, 1f, _p.StandCoupleMax, _p.StandCoupleMin);
		WeightedPush(Chest, Hips, postureUp, couple);
	}

	/// <summary>胸朝移动方向前压（加速段质感；满油门被推进余量钳制吸收）。</summary>
	private void ApplyWalkLean(Vector3 effMove)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		WeightedPush(Chest, Hips, effMove, _p.LeanPush * RunSpeed);
	}

	/// <summary>头部双伺服（Humanoid 同构，aim 按姿态混合）：
	/// 耷拉（前下 ≈42°）↔ 抬头平视 按 r 连续混合；爬行覆盖为微抬头看路；攻击（GrabTarget
	/// 非空）覆盖为直视目标。轴外顶沿 aim 顶（低头时顶向前下——与伺服目标同向，防打架）。</summary>
	private void ApplyHeadServo(Vector3 up)
	{
		Vector3 aim;
		if (GrabTarget is { } grab)
		{
			aim = Dir(Chest.Pos, grab);
		}
		else if (Crawling)
		{
			aim = SafeNormalized(Facing + up * _p.HeadCrawlUp, Facing);
		}
		else
		{
			Vector3 droopAim = SafeNormalized(Facing - up * _p.HeadDroopDown, Facing);
			Vector3 runAim = SafeNormalized(Facing + up * _p.HeadRunUp, Facing);
			aim = SafeNormalized(droopAim.Lerp(runAim, RunBlend), Facing);
		}
		WeightedPush(Head, Chest, aim, _p.HeadPush);
		Vector3 target = Chest.Pos + aim * _p.NeckLength;
		Vector3 pull = (target - Head.Pos).LimitLength(_p.HeadServoRange);
		WeightedPush(Head, Chest, pull, _p.HeadServoGain);
	}

	/// <summary>髋高度伺服（Humanoid 原样；仅可站立时调用）。</summary>
	private void ApplyHipServo(Vector3 up, bool groundHit, Vector3 groundPoint)
	{
		if (!groundHit || !Grounded)
		{
			return;
		}
		float velUp = Hips.Vel.Dot(up);
		Hips.Vel += up * (velUp * (_p.HipVelYDamp - 1f));
		float err = (groundPoint + up * _p.HipRideHeight - Hips.Pos).Dot(up);
		Hips.Vel += up * (err * _p.HipLiftGain);
	}

	/// <summary>直立推进（Humanoid 基础上加驼背差分）：胸+髋各按天花板余量钳制注入，头不参与。
	/// 天花板不对称（胸 ×(1+bias) / 髋 ×(1−bias)）是行进驼背的唯一有效差分通道——
	/// 巡航时两 chunk 双双钉在天花板上，力偶/阻尼差通道都被钳制吸收或带静立漂移副作用
	/// （见 RatFiendParams.HunchSpeedBias 注释）。</summary>
	private void ApplyLocomotionForce(Vector3 effMove)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		float bias = EnableHunchTilt ? _p.HunchSpeedBias : 0f;
		float frameSpeed = _p.BaseSpeed * RunSpeed;
		float chestRoom = _p.MaxMoveSpeed * (1f + bias) - Chest.Vel.Dot(effMove);
		if (chestRoom > 0f)
		{
			Chest.Vel += effMove * Mathf.Min(frameSpeed, chestRoom);
		}
		float hipsRoom = _p.MaxMoveSpeed * (1f - bias) - Hips.Vel.Dot(effMove);
		if (hipsRoom > 0f)
		{
			Hips.Vel += effMove * Mathf.Min(frameSpeed, hipsRoom);
		}
	}

	// ================================================================== 爬行

	/// <summary>爬行推进（涌现核心）：force = 单肢力 × 抓地肢体数 × RunSpeed，胸髋各按
	/// CrawlMaxSpeed 余量钳制——断一臂推进自动掉 1/3，「手也断了则更弱」免专门代码。
	/// 有支撑时叠微小抬鼻力偶（吻部离地）。撑稳手高于 chunk 底面 → 手拉体升（爬台阶）。
	/// 四肢全断且有意图 → 周期性内力偶蠕动
	/// （动量守恒内力，靠地面摩擦整流出近零位移——看着在挣扎、基本走不了）。</summary>
	private void ApplyCrawl(in TickContext ctx, Vector3 up, Vector3 effMove, float? climbGrip)
	{
		int grips = CrawlGripCount;
		float engine = EnableCrawlGripScaling ? grips : 3f;
		if (grips > 0)
		{
			WeightedPush(Chest, Hips, up, _p.CrawlNoseLift);
		}
		if (engine > 0f && effMove != Vector3.Zero)
		{
			// 调头两件套（缺一都会「头从裆下穿过」）：
			// · 胸沿 Facing（限速回转后的身体朝向）推——沿原始 effMove 推会在目标反向时
			//   拽着身体倒滑；
			// · 髋沿「追胸」的拖车轴推（投影到地面）——胸髋同向推绕不出转矩，Facing 转了
			//   身体轴还倒着；拖车轴让杆被胸拖着自然回转。
			// 直线爬行时两方向都收敛等于 effMove，逐位不变。
			float force = _p.CrawlForcePerLimb * engine * RunSpeed;
			float chestRoom = _p.CrawlMaxSpeed - Chest.Vel.Dot(Facing);
			if (chestRoom > 0f)
			{
				Chest.Vel += Facing * Mathf.Min(force, chestRoom);
			}
			Vector3 axle = Chest.Pos - Hips.Pos;
			Vector3 hipsDir = SafeNormalized(axle - up * axle.Dot(up), Facing);
			float hipsRoom = _p.CrawlMaxSpeed - Hips.Vel.Dot(hipsDir);
			if (hipsRoom > 0f)
			{
				Hips.Vel += hipsDir * Mathf.Min(force, hipsRoom);
			}
			// 爬台阶（手拉体升）：撑稳的手高出 chunk 底面（死区外）→ 朝上拽升。
			// chunk 底面追平撑点标高即熄火——身体正好落在台面上，自终止。
			// 平地撑点与底面同高 → 恒为零：平地爬行（含调头）逐位不变。
			if (climbGrip is { } gh)
			{
				HaulChunkUp(Chest, gh, up);
				HaulChunkUp(Hips, gh, up);
			}
		}
		else if (grips == 0 && HasMoveIntent && _p.WrigglePeriodTicks > 0)
		{
			float phase = (float)(ctx.TickIndex % _p.WrigglePeriodTicks) / _p.WrigglePeriodTicks;
			WeightedPush(Chest, Hips, Facing, _p.WrigglePulse * Mathf.Sin(Mathf.Tau * phase));
		}
	}

	/// <summary>手「撑稳」判定（有撑点 + 吸附/够近 + 地形接触）——抓地计数与爬台阶拽升共用，
	/// 两处条件必须一致：数进引擎的手才有资格拽人。</summary>
	private bool HandPlanted(RatArm arm, out Vector3 gripPos)
	{
		gripPos = default;
		if (arm.Severed || arm.GrabPos is not { } gp)
		{
			return false;
		}
		bool near = arm.ReachedSnapPosition || (arm.Pos - gp).Length() < arm.Radius + _p.ArmPlantPad;
		if (near && arm.TerrainContact)
		{
			gripPos = gp;
			return true;
		}
		return false;
	}

	/// <summary>抓地肢体计数：撑稳的手 + 抓稳的存活腿。</summary>
	private int CountGrips()
	{
		int count = 0;
		for (int i = 0; i < Arms.Count; i++)
		{
			if (HandPlanted(Arms[i], out _))
			{
				count++;
			}
		}
		for (int i = 0; i < Legs.Count; i++)
		{
			if (!LegSevered(i) && Legs[i].Gripping)
			{
				count++;
			}
		}
		return count;
	}

	/// <summary>撑稳手沿 up 的最高标高（无撑稳手 → null）。爬台阶的输入：撑点比 chunk 底面
	/// 高多少决定拽升力度，也是 SlideAlongWalls 的台阶豁免依据。</summary>
	private float? MaxPlantedHandHeight(Vector3 up)
	{
		float? best = null;
		for (int i = 0; i < Arms.Count; i++)
		{
			if (HandPlanted(Arms[i], out Vector3 gp))
			{
				float h = gp.Dot(up);
				best = best is { } b ? Mathf.Max(b, h) : h;
			}
		}
		return best;
	}

	/// <summary>拽升单 chunk：底面低于撑点标高（死区外）→ 竖直速度朝 CrawlClimbMaxRise 伺服，
	/// 注入量 ≤ 增益 × 高差（临顶自然减力，防过冲振铃）。</summary>
	private void HaulChunkUp(BodyChunk chunk, float gripHeight, Vector3 up)
	{
		float deficit = gripHeight - (chunk.Pos.Dot(up) - chunk.TerrainRadius) - _p.CrawlClimbDeadzone;
		if (deficit <= 0f)
		{
			return;
		}
		float room = _p.CrawlClimbMaxRise - chunk.Vel.Dot(up);
		if (room > 0f)
		{
			chunk.Vel += up * Mathf.Min(_p.CrawlClimbGain * deficit, room);
		}
	}

	// ================================================================== 肢体

	/// <summary>逐 chunk 各向异性阻尼（昏迷时跳过——瘫倒的身体像死物一样滑）。</summary>
	private void ApplyChunkDamping()
	{
		if (!Conscious)
		{
			return;
		}
		Chest.Vel *= _p.ChestDamping;
		Hips.Vel *= _p.HipsDamping;
		Head.Vel *= _p.HeadDamping;
	}

	/// <summary>腿更新：存活腿走正常步态机（爬行时髋贴地，FindGrip 自动退化成短促蹬地）；
	/// 断腿改由控制器手动积分残肢膝端粒子（阻尼 + 垂位弹簧 + 重力 + 距髋钳制 + 出地形）——
	/// 半长 Limb 若继续走步态机，FindGrip 落点计抓地会污染推进计数。</summary>
	private void TickLegs(in TickContext ctx, Vector3 up, Vector3 effMove)
	{
		Vector3 aim = effMove == Vector3.Zero ? Facing : effMove;
		Vector3 stepDir = Facing.Lerp(aim, _p.SteerBlend);
		stepDir = stepDir.LengthSquared() < 1e-8f ? Facing : stepDir.Normalized();
		float runSpeed = Conscious && effMove != Vector3.Zero ? RunSpeed : 0f;
		Vector3 right = SafeNormalized(Facing.Cross(up), Vector3.Right);

		for (int i = 0; i < Legs.Count; i++)
		{
			Limb leg = Legs[i];
			if (!LegSevered(i))
			{
				leg.Tick(ctx, stepDir, up, Legs, _p.SmoothenLegMovement, runSpeed);
				continue;
			}
			// 残肢积分：垂在髋侧下方的短摆锤（JointDist 已减半 = 膝端）。
			leg.LastPos = leg.Pos;
			Vector3 dangle = Hips.Pos + right * (leg.Side * 0.15f) - up * (leg.JointDist * 0.8f);
			leg.Vel = leg.Vel * _p.StumpDamping + (dangle - leg.Pos) * _p.StumpSpring
				+ ctx.GravityPerTick;
			leg.Pos += leg.Vel;
			Vector3 fromHips = leg.Pos - Hips.Pos;
			float dist = fromHips.Length();
			if (dist > leg.JointDist && dist > 1e-6f)
			{
				leg.Pos = Hips.Pos + fromHips / dist * leg.JointDist;
				leg.Vel *= 0.5f;
			}
			if (ctx.Terrain.SpherePenetration(leg.Pos, leg.Radius, out Vector3 pushDir, out float depth))
			{
				leg.Pos += pushDir * depth;
				SphereTerrain.RespondVelocity(pushDir, _p.FootedSurfaceFriction, ref leg.Vel);
			}
		}
	}

	/// <summary>
	/// 手臂优先级链（Humanoid TickArms 的物种版）：对每只手固定序取第一条命中的分支——
	/// ① 昏迷 → 垂落；② 断臂 → 垂落（残肢短摆，EffectiveLength 钳制，永不参与撑地）；
	/// ③ 攻击抓取（GrabTarget）→ 沿「胸→目标」前伸（可及半径钳制）；
	/// ④ 爬行 → 撑地子链（plant-and-trail：锁点身体爬过 = trail，失效/轮到本手重找）；
	/// ⑤ 走/跑混合 → 垂摆（读对侧腿相位，天然反相随步频）↔ 前伸欲抓，按 r 连续混合；
	/// ⑥ 闲置 → 慢速垂到身侧。链尾统一 RatArm.Tick 做臂长约束/腋窝排斥/出地形。
	/// </summary>
	private void TickArms(in TickContext ctx, Vector3 up, Vector3 effMove)
	{
		Vector3 right = SafeNormalized(Facing.Cross(up), Vector3.Right);
		float r = RunBlend;

		for (int i = 0; i < Arms.Count; i++)
		{
			RatArm arm = Arms[i];
			bool myTurn = (int)(ctx.TickIndex & 1) == i;

			if (!Conscious)
			{
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.Dangle;
			}
			else if (arm.Severed)
			{
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.Dangle;
			}
			else if (GrabTarget is { } grab)
			{
				// 攻击抓取：沿「胸→目标」满伸（超出可及半径钳到肩心球面上）。
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.HuntAbsolutePosition;
				Vector3 to = grab - Chest.Pos;
				float dist = to.Length();
				Vector3 dir = dist < 1e-6f ? Facing : to / dist;
				arm.HuntPos = Chest.Pos + dir * Mathf.Min(dist, arm.EffectiveLength);
			}
			else if (Crawling)
			{
				TickCrawlArm(ctx, up, right, arm, myTurn);
			}
			else if (effMove != Vector3.Zero)
			{
				// 走/跑混合。摆动读对侧腿脚端沿 Facing 的超前量（右臂随左腿）：零新增状态、
				// 天然反相、随真实步频自适应——sin(TickIndex) 会与步频脱钩，低通速度没有相位。
				Limb opposite = Legs[1 - i];
				float swing = Mathf.Clamp(
					(opposite.Pos - Hips.Pos).Dot(Facing) / _p.LegLength, -1f, 1f);
				Vector3 walkTarget = Chest.Pos
					+ right * (arm.Side * (Chest.Radius + arm.Radius + 0.02f))
					+ Facing * (_p.ArmWalkForwardBias + swing * _p.ArmSwingAmplitude)
					- up * (arm.EffectiveLength * _p.ArmWalkDangleDrop);
				Vector3 runTarget = Chest.Pos
					+ Facing * (arm.EffectiveLength * _p.RunReachForward)
					+ right * (arm.Side * _p.RunReachLateral)
					+ up * (arm.EffectiveLength * _p.RunReachUp);
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.HuntRelativePosition;
				arm.HuntPos = walkTarget.Lerp(runTarget, r);
				arm.HuntSpeed = arm.DefaultHuntSpeed * Mathf.Lerp(_p.ArmSwingSpeedFactor, 1f, r);
			}
			else
			{
				// 闲置：慢速垂到身侧（Humanoid 闲置分支原样——驼背垂手的常态剪影）。
				arm.GrabPos = null;
				arm.HuntSpeed = arm.DefaultHuntSpeed * 0.2f;
				arm.Mode = RatArm.ArmMode.HuntRelativePosition;
				arm.HuntPos = Chest.Pos + right * (arm.Side * (Chest.Radius + arm.Radius))
					+ Facing * 0.1f - up * (arm.EffectiveLength * 0.55f);
			}

			arm.Tick(ctx);
		}
	}

	/// <summary>爬行撑地子链（控制器层 plant-and-trail 驱动哑粒子——Humanoid 手撑地的既有形态）：
	/// 入场门 = Grounded（摔倒还在空中时不许抢先扎手，零额外射线）；找点 = 胸前竖直下探
	/// （tick 奇偶交替，每 tick 至多 1 根射线）；锁定后身体从旁爬过 = trail；合法窗超限重找。</summary>
	private void TickCrawlArm(in TickContext ctx, Vector3 up, Vector3 right, RatArm arm, bool myTurn)
	{
		if (!Grounded)
		{
			arm.GrabPos = null;
			arm.Mode = RatArm.ArmMode.Dangle;
			return;
		}
		if (arm.GrabPos is { } existing)
		{
			Vector3 fromChest = existing - Chest.Pos;
			float along = fromChest.Dot(Facing);
			if (along < -_p.CrawlBehindWindow
				|| along > arm.EffectiveLength * _p.CrawlAheadWindowFactor
				|| fromChest.Length() > arm.EffectiveLength * _p.CrawlReachFactor)
			{
				arm.GrabPos = null;
			}
		}
		if (arm.GrabPos is null && myTurn)
		{
			Vector3 from = Chest.Pos + up * _p.CrawlProbeRise
				+ Facing * (arm.EffectiveLength * _p.CrawlProbeForward)
				+ right * (arm.Side * _p.CrawlProbeSide);
			if (ctx.Terrain.Raycast(from, from - up * _p.CrawlProbeDepth, out TerrainHit hit)
				&& IsGroundNormal(hit.Normal, up))
			{
				arm.GrabPos = hit.Point;
			}
		}
		if (arm.GrabPos is { } lockPos)
		{
			arm.Mode = RatArm.ArmMode.HuntAbsolutePosition;
			arm.HuntPos = lockPos;
		}
		else
		{
			arm.Mode = RatArm.ArmMode.HuntRelativePosition;
			arm.HuntPos = Chest.Pos + Facing * (arm.EffectiveLength * _p.CrawlHandTargetForward)
				- up * _p.CrawlHandTargetDown;
		}
	}

	/// <summary>抓住判定观测：手到 GrabTarget 的真实距离（不是钳制后的 HuntPos）。</summary>
	private void UpdateHandsOnTarget()
	{
		for (int i = 0; i < Arms.Count; i++)
		{
			HandsOnTarget[i] = Conscious
				&& GrabTarget is { } grab
				&& !Arms[i].Severed
				&& (Arms[i].Pos - grab).Length() < _p.GrabContactRadius;
		}
	}

	// ================================================================== 意图几何（Humanoid 同款）

	private static Vector3 FlattenToGround(Vector3 move, Vector3 up)
	{
		Vector3 flat = move - up * move.Dot(up);
		return flat.LengthSquared() < 1e-8f ? Vector3.Zero : flat.Normalized();
	}

	private Vector3 SlideAlongWalls(Vector3 move, Vector3 up)
	{
		if (move == Vector3.Zero
			|| (!Chest.TerrainContact && !Chest.HadContactLastTick)
			|| Chest.ContactNormal.LengthSquared() < 1e-12f)
		{
			return move;
		}
		Vector3 n = Chest.ContactNormal;
		if (Mathf.Abs(n.Dot(up)) >= 0.5f)
		{
			return move;
		}
		Vector3 wall = n - up * n.Dot(up);
		if (wall.LengthSquared() < 1e-8f)
		{
			return move;
		}
		wall = wall.Normalized();
		float into = -move.Dot(wall);
		if (into <= 0.01f)
		{
			return move;
		}
		Vector3 slid = move + wall * into;
		return slid.LengthSquared() < 1e-6f ? Vector3.Zero : slid.Normalized();
	}

	private float ComputeUprightness(Vector3 postureUp)
	{
		Vector3 axis = Chest.Pos - Hips.Pos;
		return axis.LengthSquared() < 1e-10f ? 0f : axis.Normalized().Dot(postureUp);
	}

	// ================================================================== 派生量

	private float RunBlendOf(float gait) =>
		SaturatedInverseLerp(_p.RunBlendStart, _p.RunBlendEnd, gait);

	private float MouthValueOf(float gait) => Mathf.Clamp(
		_p.MouthBase + _p.MouthRunGain * RunBlendOf(gait) + Mathf.Clamp(MouthDrive, 0f, 1f),
		0f, 1f);

	// ================================================================== 确定性折叠

	/// <summary>状态哈希折叠（smoke 与沙盒共用同一顺序，可互证）。折叠顺序固定：
	/// Body → Facing → Gait/CrawlFactor → 撑地计数 → 断肢（序号+逐肢标志）→ 逐腿 → 逐臂。
	/// MouthOpen/HandsOnTarget/CrawlGripCount 是派生量不折叠；Last* 是派生历史不折叠。</summary>
	public void FoldState(DeterminismHasher hasher)
	{
		hasher.FoldBody(Body);
		hasher.Fold(Facing);
		hasher.Fold(Gait);
		hasher.Fold(CrawlFactor);
		hasher.Fold(GroundedCounter);
		hasher.Fold(SeverSerial);
		for (int i = 0; i < _severed.Length; i++)
		{
			hasher.Fold(_severed[i]);
		}
		foreach (Limb leg in Legs)
		{
			hasher.Fold(leg.Pos);
			hasher.Fold(leg.Vel);
			hasher.Fold(leg.ReachingForTerrain);
			hasher.Fold(leg.HasGrip);
			hasher.Fold(leg.GripCounter);
		}
		foreach (RatArm arm in Arms)
		{
			hasher.Fold(arm.Pos);
			hasher.Fold(arm.Vel);
			hasher.Fold((int)arm.Mode);
			hasher.Fold(arm.GrabPos.HasValue);
			if (arm.GrabPos is { } gp)
			{
				hasher.Fold(gp);
			}
			hasher.Fold(arm.ReachedSnapPosition);
			hasher.Fold(arm.TerrainContact);
			hasher.Fold(arm.Severed);
		}
	}

	// ================================================================== 工具

	/// <summary>按质量分配的等量反向力偶（总动量不变，轻的一端动得多）。</summary>
	private static void WeightedPush(BodyChunk a, BodyChunk b, Vector3 dir, float force)
	{
		float total = a.Mass + b.Mass;
		if (total <= 0f)
		{
			return;
		}
		float shareA = b.Mass / total;
		a.Vel += dir * (force * shareA);
		b.Vel -= dir * (force * (1f - shareA));
	}

	/// <summary>≙ RWCustom.Custom.LerpAndTick：先按比例 Lerp，再线性步进钳到目标。</summary>
	private static float LerpAndTick(float value, float target, float lerpFactor, float tickStep)
	{
		value = Mathf.Lerp(value, target, lerpFactor);
		return Mathf.MoveToward(value, target, tickStep);
	}

	private static float SaturatedInverseLerp(float from, float to, float value) =>
		Mathf.Clamp(Mathf.InverseLerp(from, to, value), 0f, 1f);

	private static Vector3 SafeNormalized(Vector3 v, Vector3 fallback)
	{
		if (v.LengthSquared() > 1e-8f)
		{
			return v.Normalized();
		}
		return fallback.LengthSquared() > 1e-8f ? fallback.Normalized() : Vector3.Right;
	}

	/// <summary>朝向限速回转：夹角 ≤ 限速时直接取目标（直线爬行逐位不变）；否则绕
	/// current×target 轴转 maxRadians。近共线（含正对身后的反平行）时 cross 退化为零轴，
	/// 回退绕重力上轴——地面爬行调头的自然回转轴。</summary>
	private static Vector3 SlewFacing(Vector3 current, Vector3 target, Vector3 up, float maxRadians)
	{
		float dot = Mathf.Clamp(current.Dot(target), -1f, 1f);
		if (Mathf.Acos(dot) <= maxRadians)
		{
			return target;
		}
		Vector3 axis = SafeNormalized(current.Cross(target), up);
		return SafeNormalized(current.Rotated(axis, maxRadians), target);
	}

	private static Vector3 Dir(Vector3 from, Vector3 to)
	{
		Vector3 d = to - from;
		return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
	}
}
