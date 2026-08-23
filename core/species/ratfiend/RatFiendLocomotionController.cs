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

	/// <summary>
	/// 可选翻越旋钮（宿主逐 tick 写/清），优先于 MoveTarget/MoveDir。内核不选路线、不掷骰，
	/// 只把明确阶段变成确定性物理运动；默认 null 时既有路线逐位不变。
	/// </summary>
	public RatTraversalIntent? TraversalIntent;

	/// <summary>当前翻越阶段已到目标；宿主只在 tick 后读取并推进下一阶段。</summary>
	public bool AtTraversalTarget { get; private set; }

	public const float MoveIntentDeadzone = Limb.MoveIntentDeadzone;

	public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
		&& (TraversalIntent is not null
			? !AtTraversalTarget
			: MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

	/// <summary>意识开关（同人形语义）：false 时全部伺服/推进/手臂链停摆，身体只剩重力与约束。</summary>
	public bool Conscious = true;

	/// <summary>攻击抓取目标（世界点，宿主逐 tick 写/清）：非 null 且清醒时双臂脱离步态摆动、
	/// 沿「胸→目标」前伸（可及半径钳制），头 aim 转向目标。抓住判定归宿主（读 HandsOnTarget）。</summary>
	public Vector3? GrabTarget;

	/// <summary>凝视目标（世界点，宿主逐 tick 写/清）：非 null 且清醒时 = 猎杀姿态——
	/// 头 aim 直视该点 + 走/跑手臂混合权重下限抬到 LookArmRaise（低速逼近也举手备抓，
	/// R16b）。不碰步态/嘴（张嘴走既有 MouthDrive）、不追点抓取（那是 GrabTarget——
	/// 双臂满伸真抓）。GrabTarget 非空时让位；爬行撑地优先于抬臂（推进链不可劫持）。
	/// opt-in：默认 null，回归路线从不设置 → 既有基线零漂移（§6.6 惯例）。</summary>
	public Vector3? LookTarget;

	/// <summary>嘴开度的宿主驱动分量 ∈ [0,1]（咬合时拉满；叠加在 Gait 派生的基础开度上）。</summary>
	public float MouthDrive;

	/// <summary>抓取双手的横向对称分开（米，R19 opt-in——默认 0 = 旧行为逐位不变，回归
	/// 路线不设置 → 基线零漂移）：GrabTarget 分支里两手各朝自己一侧偏开 Side×此值。
	/// 默认两手追同一个点、逐位重合——渲染两套手爪几何完全叠置，z-fighting 闪烁读成
	/// 「手一直在颤抖」（R19 竞技场实测：束缚稳态两手逐 tick 位移逐位相同、亚毫米静止，
	/// 抖的不是物理是深度冲突）。分开后读成「掐住两肩」。</summary>
	public float GrabHandSpread;

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

	/// <summary>
	/// 普通 MoveTarget 是否拒绝把高家具顶误当作可走地面。宿主只有在能力导航 Active 时
	/// 开启；默认 false 保留旧移动语义，显式 TraversalIntent 不受此开关影响。
	/// </summary>
	public bool EnableOrdinaryHighStepGate;

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
		if (TraversalIntent is { } traversal)
		{
			TraversalIntent = traversal with { Target = traversal.Target + delta };
		}
		if (GrabTarget is { } grab)
		{
			GrabTarget = grab + delta;
		}
		if (LookTarget is { } look)
		{
			LookTarget = look + delta;
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
		TraversalIntent = null;
		AtTraversalTarget = false;
		GrabTarget = null;
		LookTarget = null;
	}

	/// <summary>部位冲击（R18，opt-in——回归路线不调用 → 基线零漂移）：给单个 chunk 注入
	/// 速度增量（枪击命中点的顿挫）。与 <see cref="Launch"/>（全身击飞 + 释放肢体 + 清撑地）
	/// 的区别是幅度小且局部：不碰肢体与撑地，站立伺服/腿的 plant-and-trail 自己消化——
	/// 身体被推得后滑时踩住的脚相对落后超阈重迈，踉跄后退步涌现。宿主传打中的 chunk
	/// （头/胸/髋），断肢命中的踉跄仍走 Sever 的 staggerImpulse 通道。</summary>
	public void Impact(BodyChunk chunk, Vector3 velocityPerTick)
	{
		chunk.Vel += velocityPerTick;
	}

	/// <summary>朝向冲击（R19，opt-in——回归路线不调用 → 基线零漂移）：绕 up 轴扭转 Facing
	/// （侧向命中肢体的躯干拧转，宿主按 力臂×枪向 的转矩符号定正负）。Facing 是全套伺服的
	/// 目标轴——扭转即整身姿态偏转；恢复交给既有机制涌现：有移动意图时 SlewFacing 按转速限
	/// 拽回意图方向（拧身—甩回的踉跄），宿主的吃痛窗内无意图则保持拧姿。头伺服若在盯目标
	/// （GrabTarget/LookTarget）会当场反向咬住目标点——身体拧、瞪视不移，正是吃痛的凶相。</summary>
	public void ImpactTwist(float yawRadians, Vector3 up)
	{
		Vector3 axis = SafeNormalized(up, Vector3.Up);
		Facing = SafeNormalized(Facing.Rotated(axis, yawRadians), Facing);
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
		TraversalIntent = null;
		AtTraversalTarget = false;
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

		bool derivedMove = TraversalIntent is not null || MoveTarget is not null;
		bool mounting = TraversalIntent is { Phase: RatTraversalPhase.MountAndCross };
		if (TraversalIntent is { } traversal)
		{
			AtMoveTarget = false;
			DeriveMoveFromTraversal(traversal, up);
		}
		else if (MoveTarget is { } fed)
		{
			AtTraversalTarget = false;
			DeriveMoveFromTarget(fed, up);
		}
		else
		{
			AtMoveTarget = false;
			AtTraversalTarget = false;
		}

		UpdateGrounded(ctx, up, mounting, out bool groundHit, out Vector3 groundPoint,
			out bool highStepBlocked);

		// 爬台阶（手拉体升）的输入：撑稳手的最高标高（读上一 tick 收尾的手臂状态，固定序）。
		// 撑点高出胸底死区外 = 正在攀台阶 → 豁免滑墙——台阶立面对爬行躯干是「墙」，
		// 正对时滑墙会把意图消成零（头对头 slid 长度归零），推进与拽升双双熄火。
		float? climbGrip = Crawling ? MaxPlantedHandHeight(up) : null;
		bool climbing = climbGrip is { } gripH
			&& gripH - (Chest.Pos.Dot(up) - Chest.TerrainRadius) > _p.CrawlClimbDeadzone;

		Vector3 effMove = Conscious && HasMoveIntent
			? FlattenToGround(MoveDir, up)
			: Vector3.Zero;
		if (EnableOrdinaryHighStepGate && highStepBlocked && !mounting)
		{
			effMove = Vector3.Zero;
		}
		if (effMove != Vector3.Zero && !climbing && !mounting)
		{
			effMove = SlideAlongWalls(effMove, up);
		}
		if (effMove != Vector3.Zero)
		{
			// 调头统一画弧（R13 把 R8 的爬行限速回转扩展到直立）：直立瞬时置向会让头伺服
			// 把头从躯干里拽到另一侧、渲染 dorsal（−Facing）低通打转——与爬行头髋穿插同一
			// 病灶。直立限速取爬行 2×（双足原地转身比贴地水平链灵活）；夹角 ≤ 限速时
			// SlewFacing 直接取目标，直线与小角度转向和旧行为逐位相同。
			Facing = SlewFacing(Facing, effMove, up,
				CanStand ? _p.StandTurnRatePerTick : _p.CrawlTurnRatePerTick);
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
				// R13：推进/走倾沿限速回转后的 Facing——沿原始 effMove 推会在意图反向时
				// 先全速倒车再转身（爬行 R8「胸沿 Facing 推」的直立对应件）。直线行走时
				// Facing == effMove，逐位同旧行为。
				// R13b 半径收紧（双态转体门，见 TurnAlignGate 注释——线性节流被
				// BaseSpeed≫天花板的钳制证伪）：转体期间零注入 + 主动刹车 → 滑步刹停、
				// 低速拧身；对齐后全油门沿新方向弹射起步。转速（StandTurnRatePerTick）
				// 不变，只压弧半径。直线对齐度恒 1，逐位不变。
				Vector3 standDrive = effMove == Vector3.Zero ? Vector3.Zero : Facing;
				float align = effMove == Vector3.Zero
					? 0f
					: Facing.Dot(SafeNormalized(effMove, Facing));
				float driveThrottle = align >= _p.TurnAlignGate ? 1f : 0f;
				if (standDrive != Vector3.Zero && driveThrottle == 0f && Grounded)
				{
					BrakeHorizontal(Chest, up);
					BrakeHorizontal(Hips, up);
				}
				ApplyWalkLean(standDrive, driveThrottle);
				ApplyHeadServo(up);
				if (TraversalIntent is { Phase: RatTraversalPhase.MountAndCross } mount)
				{
					ApplyTraversalMountDrive(mount, up);
				}
				else
				{
					ApplyHipServo(up, groundHit, groundPoint);
				}
				ApplyLocomotionForce(standDrive, driveThrottle);
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

	/// <summary>翻越阶段意图导出。MountAndCross 保留 Target.Y 作为顶面标高，但移动方向仍
	/// 水平导出；到点同时要求髋底越过顶面，避免在障碍近侧靠水平容差假完成。</summary>
	private void DeriveMoveFromTraversal(in RatTraversalIntent intent, Vector3 up)
	{
		Vector3 d = intent.Target - Chest.Pos;
		Vector3 dh = d - up * d.Dot(up);
		bool horizontalArrived = dh.Length() <= MoveTargetArriveRadius;
		AtTraversalTarget = intent.Phase switch
		{
			RatTraversalPhase.MountAndCross => horizontalArrived
				&& Hips.Pos.Dot(up) - Hips.TerrainRadius
					>= intent.Target.Dot(up) - _p.CrawlClimbDeadzone,
			RatTraversalPhase.Stabilize => horizontalArrived && Grounded,
			_ => horizontalArrived
		};
		MoveDir = AtTraversalTarget || dh.LengthSquared() < 1e-12f
			? Vector3.Zero
			: dh.Normalized();
	}

	/// <summary>站立翻越的受限竖直牵引。复用爬台阶的速度/增益上限，只写 chunk.Vel；
	/// Body.Tick 与真实地形碰撞仍是唯一位置权威，不 Shift/Teleport。</summary>
	private void ApplyTraversalMountDrive(in RatTraversalIntent intent, Vector3 up)
	{
		float surfaceHeight = intent.Target.Dot(up);
		HaulChunkUp(Chest, surfaceHeight, up);
		HaulChunkUp(Hips, surfaceHeight, up);
	}

	/// <summary>撑地判定 + 重力开关 + 摩擦切档（Humanoid 同构，法线门统一 ≥0.5）。
	/// 鼠煞差异：重力开关多一个 CanStand 门——断腿后失重伺服态永不成立（爬行重力常开），
	/// 摔倒由此涌现。Grounded 本身照常计（爬行手撑地的入场门与摩擦切档用它）。</summary>
	private void UpdateGrounded(in TickContext ctx, Vector3 up, bool allowHighStep,
		out bool groundHit, out Vector3 groundPoint, out bool highStepBlocked)
	{
		groundHit = false;
		groundPoint = default;
		highStepBlocked = false;
		bool leading = Conscious && HasMoveIntent;
		Vector3 origin = Hips.Pos + (leading ? Facing * HipProbeLead : Vector3.Zero);
		if (ctx.Terrain.Raycast(origin + up * HipProbeRise,
				origin - up * (_p.HipRideHeight + _p.GroundProbeSlack), out TerrainHit hit)
			&& IsGroundNormal(hit.Normal, up))
		{
			bool ordinaryStep = !EnableOrdinaryHighStepGate
				|| allowHighStep || !leading || IsOrdinaryStepHeight(hit.Point, up);
			bool supportHit = false;
			TerrainHit support = default;
			if (!ordinaryStep
				&& ctx.Terrain.Raycast(Hips.Pos + up * HipProbeRise,
					Hips.Pos - up * (_p.HipRideHeight + _p.GroundProbeSlack), out support)
				&& IsGroundNormal(support.Normal, up))
			{
				supportHit = true;
				ordinaryStep = hit.Point.Dot(up) - support.Point.Dot(up) <= _p.WalkStepMaxRise;
			}
			if (ordinaryStep)
			{
				groundHit = true;
				groundPoint = hit.Point;
			}
			else
			{
				highStepBlocked = true;
				if (supportHit)
				{
					groundHit = true;
					groundPoint = support.Point;
				}
			}
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

	private bool IsOrdinaryStepHeight(Vector3 point, Vector3 up)
	{
		float estimatedSupportHeight = Hips.Pos.Dot(up) - _p.HipRideHeight;
		return point.Dot(up) - estimatedSupportHeight <= _p.WalkStepMaxRise;
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
	/// <summary>掉头刹车（R13b）：只砍水平速度分量，竖直留给重力/髋伺服。</summary>
	private void BrakeHorizontal(BodyChunk chunk, Vector3 up)
	{
		Vector3 vUp = up * chunk.Vel.Dot(up);
		chunk.Vel = vUp + (chunk.Vel - vUp) * _p.TurnBrakePerTick;
	}

	private void ApplyWalkLean(Vector3 effMove, float throttle)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		WeightedPush(Chest, Hips, effMove, _p.LeanPush * RunSpeed * throttle);
	}

	/// <summary>头部双伺服（Humanoid 同构，aim 按姿态混合）：
	/// 耷拉（前下 ≈42°）↔ 抬头平视 按 r 连续混合；爬行覆盖为微抬头看路；攻击/凝视
	/// （GrabTarget ?? LookTarget 非空）覆盖为直视目标（Grab 优先；凝视也压过爬行——
	/// 断腿爬行的追杀同样盯猎物）。轴外顶沿 aim 顶（低头时顶向前下——与伺服目标同向，
	/// 防打架）。</summary>
	private void ApplyHeadServo(Vector3 up)
	{
		Vector3 aim;
		if ((GrabTarget ?? LookTarget) is { } focus)
		{
			aim = Dir(Chest.Pos, focus);
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
	private void ApplyLocomotionForce(Vector3 effMove, float throttle)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		float bias = EnableHunchTilt ? _p.HunchSpeedBias : 0f;
		// R13b：注入 ∝ 朝向-意图对齐度（throttle）——掉头期熄油门压弧半径；天花板不动
		// （只作瞬态门，不改直线巡航平衡，HunchSpeedBias 的不对称天花板论证不受影响）。
		float frameSpeed = _p.BaseSpeed * RunSpeed * throttle;
		// R11 步态修复：天花板 ∝ 油门。此前天花板与 RunSpeed 无关，注入率在 RunSpeed≥0.15
		// 后全被余量钳制吸收——走/跑巡航速度逐位相同（实测三档全是 0.0859/tick=3.4m/s），
		// 「走」就是用跑的速度挪碎步。乘 RunSpeed 后巡航 ≈ 0.9×天花板×油门：
		// 0.35 档 ≈1.2m/s（人样步行）、1.0 档 ≈3.4m/s（满速追击，与修复前一致）。
		float ceiling = _p.MaxMoveSpeed * RunSpeed;
		float chestRoom = ceiling * (1f + bias) - Chest.Vel.Dot(effMove);
		if (chestRoom > 0f)
		{
			Chest.Vel += effMove * Mathf.Min(frameSpeed, chestRoom);
		}
		float hipsRoom = ceiling * (1f - bias) - Hips.Vel.Dot(effMove);
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

		// R15 走姿慢摆：摆动期逼近速度按 Gait 调制——walk 带宽内全额减速（慢下来的是
		// 抬脚→落下的腾空段；步频由前瞻释放循环决定基本不动），Gait 越过慢摆窗才恢复
		// 全速。踩住的脚在吸附分支里被钳在落点，不受此缩放影响（TickArms 摆臂同款手法）。
		// 爬行豁免（按 CrawlFactor 混回全速）：爬行蹬地是推进链的一部分，慢摆削抓地
		// 占空比——矩阵 crawl-step 在 RunSpeed 0.6 下卡台阶前的红灯实证（smoke 爬行断言
		// 全部驱动 RunSpeed 1.0，Gait 越窗后豁不豁免同值，故只有矩阵路线暴露）。
		float swingScale = Mathf.Lerp(
			Mathf.Lerp(_p.LegSwingSpeedFactor, 1f,
				SaturatedInverseLerp(_p.LegSwingBlendStart, _p.LegSwingBlendEnd, Gait)),
			1f, CrawlFactor);

		// R11 反相自举：前瞻落点是公共的（髋前 ≈0.46m），两腿一旦近同相（前后间距塌缩），
		// 纯时间错位会被逐周期原样保留——出生 stagger 只有 0.1m，长步幅慢步频下走成
		// 双脚并排的「并步跳」。两脚同时踩稳、间距 < LegPhaseMinGap 且居后脚已过髋
		// （即将自然释放）时，把居后脚提前释放重迈：一次干预拉开 ≈半步幅的空间差，
		// 之后释放-重落循环自稳在近反相，规则不再触发（人样迈步的确定性等价物）。
		if (Conscious && CanStand && HasMoveIntent
			&& Legs.Count == 2 && Legs[0].Gripping && Legs[1].Gripping)
		{
			float fwd0 = (Legs[0].Pos - Hips.Pos).Dot(stepDir);
			float fwd1 = (Legs[1].Pos - Hips.Pos).Dot(stepDir);
			if (Mathf.Abs(fwd0 - fwd1) < _p.LegPhaseMinGap && Mathf.Min(fwd0, fwd1) < 0f)
			{
				(fwd0 < fwd1 ? Legs[0] : Legs[1]).ForceRelease();
			}
		}

		for (int i = 0; i < Legs.Count; i++)
		{
			Limb leg = Legs[i];
			if (!LegSevered(i))
			{
				leg.HuntSpeed = _p.LegSpeed * swingScale;
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
	/// ① 昏迷 → 垂落；② 断臂 → 垂落（残肢短摆 + 肩侧垂位弹簧防中线收敛，
	///    EffectiveLength 钳制，永不参与撑地）；
	/// ③ 攻击抓取（GrabTarget）→ 沿「胸→目标」前伸（可及半径钳制）；
	/// ④ 爬行 → 撑地子链（plant-and-trail：锁点身体爬过 = trail，失效/轮到本手重找）；
	/// ⑤ 走/跑混合 → 垂摆（读对侧腿相位，天然反相随步频）↔ 前伸欲抓，按 r 连续混合
	///    （LookTarget 非空时权重下限 LookArmRaise = 猎杀备抓，站定凝视也进本分支）；
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
				// 残肢垂位偏置（R17）：Dangle 的臂长钳制锚在胸心，纯自重的平衡点是胸心
				// 正下方——双断臂时两截残肢会收敛到中线，正面读成「倒三角围脖」。照断腿
				// 残肢的先例加带肩侧偏的垂位弹簧（侧偏 = 渲染肩位同款 Chest.Radius·0.78），
				// 自重/臂长钳制/出地形仍由链尾 RatArm.Tick 完成。注意目标位本身不可达
				// （下垂 0.8·EffLen + 重力沉降 DangleGravity/StumpSpring 超出可及半径，
				// 与腿残肢先例同构）：真实平衡位是钳制球面上沿合力方向的近竖直投影，
				// 侧偏靠弹簧的切向残量存活、被重力约 3:1 稀释——StumpSpring 降到 ~0.04
				// 以下侧偏就塌回中线（smoke [ARM-STUMP] 会红），调参前先算这笔账。
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.Dangle;
				Vector3 stumpDangle = Chest.Pos + right * (arm.Side * Chest.Radius * 0.78f)
					- up * (arm.EffectiveLength * 0.8f);
				arm.Vel = arm.Vel * _p.StumpDamping + (stumpDangle - arm.Pos) * _p.StumpSpring;
			}
			else if (TraversalIntent is { Phase: RatTraversalPhase.MountAndCross } traversal)
			{
				// 翻越时双手朝远侧顶面探出；只驱动手端粒子，身体上升仍走受限 Vel 牵引。
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.HuntAbsolutePosition;
				Vector3 to = traversal.Target - Chest.Pos;
				float dist = to.Length();
				Vector3 dir = dist < 1e-6f ? Facing : to / dist;
				arm.HuntPos = Chest.Pos + dir * Mathf.Min(dist, arm.EffectiveLength);
			}
			else if (GrabTarget is { } grab)
			{
				// 攻击抓取：沿「胸→目标」满伸（超出可及半径钳到肩心球面上）。
				arm.GrabPos = null;
				arm.Mode = RatArm.ArmMode.HuntAbsolutePosition;
				Vector3 to = grab - Chest.Pos;
				float dist = to.Length();
				Vector3 dir = dist < 1e-6f ? Facing : to / dist;
				Vector3 huntPos = Chest.Pos + dir * Mathf.Min(dist, arm.EffectiveLength);
				if (GrabHandSpread != 0f)
				{
					// 显式零判而非无脑加零向量：−0.0 + 0.0 = +0.0 会翻符号位，
					// 默认路径必须与旧行为逐位相同（哈希基线零漂移的硬要求）。
					huntPos += right * (arm.Side * GrabHandSpread);
				}
				arm.HuntPos = huntPos;
			}
			else if (Crawling)
			{
				TickCrawlArm(ctx, up, right, arm, myTurn);
			}
			else if (effMove != Vector3.Zero || LookTarget is not null)
			{
				// 走/跑混合。摆动读对侧腿脚端沿 Facing 的超前量（右臂随左腿）：零新增状态、
				// 天然反相、随真实步频自适应——sin(TickIndex) 会与步频脱钩，低通速度没有相位。
				// R16b 凝视备抓：LookTarget 非空时混合权重下限抬到 LookArmRaise——低速逼近
				// 猎物手保持前伸备抓不落回垂摆；站定凝视（effMove 零）也进本分支不落身侧。
				Limb opposite = Legs[1 - i];
				float swing = Mathf.Clamp(
					(opposite.Pos - Hips.Pos).Dot(Facing) / _p.LegLength, -1f, 1f);
				float armR = LookTarget is not null ? Mathf.Max(r, _p.LookArmRaise) : r;
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
				arm.HuntPos = walkTarget.Lerp(runTarget, armR);
				arm.HuntSpeed = arm.DefaultHuntSpeed * Mathf.Lerp(_p.ArmSwingSpeedFactor, 1f, armR);
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
