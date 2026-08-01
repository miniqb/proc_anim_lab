namespace ProcAnim.Core.Species.Humanoid;

/// <summary>
/// 人形品种参数表（≙ <see cref="BreedParams"/> 之于蜥蜴）：人形怪物的"手感"全部收拢在这一张表上，
/// 工厂读它装配 Body/HumanoidLocomotionController/Limb/Arm，内核运行时不再回读——纯出生配置，零行为分支。
/// 数值蓝本 = 反编译 Scavenger（拾荒者）的直接换算（1px = 0.025m），字段注释标注 RW 原值便于对照。
/// 与 BreedParams 分表：字段面几乎不相交（脊柱/尾巴 vs 手臂/站立力偶/knuckle），且蜥蜴的
/// AllBreeds() 路由表被回归拓扑断言绑定——人形走自己的 AllHumanoids() 表。
/// </summary>
public sealed class HumanoidParams
{
	public string Name = "scavenger";

	// —— 体格（≙ Scavenger 3 chunk：胸 rad9.5/m0.5 主根、髋 7/0.3、头 5/0.05）——
	public float ChestRadius = 0.24f;
	public float HipsRadius = 0.175f;
	public float HeadRadius = 0.125f;
	public float ChestMass = 0.5f;
	public float HipsMass = 0.3f;
	public float HeadMass = 0.05f;

	/// <summary>胸↔髋刚性连接长度（≙ bodyChunkConnections[0] dist 18px = 0.45m）。</summary>
	public float ChestHipsDist = 0.45f;

	/// <summary>胸↔头 PullOnly 脖长（≙ bodyChunkConnections[1] dist 22px = 0.55m——脖比躯干长，
	/// 长颈剪影的来源；只防拉长不防压缩）。</summary>
	public float NeckLength = 0.55f;

	// —— 站立（≙ Act 站立力偶/头轴外顶/髋高度伺服/逐 chunk 阻尼）——
	/// <summary>站立力偶在完全倒立（躯干轴·up = -1）时的强度（≙ LerpMap 端点 5.5px = 0.1375）。</summary>
	public float StandCoupleMax = 0.1375f;

	/// <summary>站立力偶在完全正立时的强度（≙ 端点 0.3px = 0.0075——正立时几乎不施力，不抖）。</summary>
	public float StandCoupleMin = 0.0075f;

	/// <summary>头沿躯干轴外顶的力（≙ WeightedPush(头,胸,髋→胸轴,0.3px)——脖子不塌的来源）。</summary>
	public float HeadPush = 0.0075f;

	/// <summary>胸朝移动方向前压的力偶（≙ Scavenger L1956 WeightedPush(0,1,HeadLookDir,
	/// 0.05px=0.00125)；RW 视线幅值用 RunSpeed×移动意图代替，停驶/蓄力归零）。注意：满油门巡航时
	/// 它被推进的 MaxMoveSpeed 余量钳制**完全吸收**（多给的前向速度让推进少注入等量，消融实证
	/// 净效果为零）——巡航弓背的真实来源是 Chest/Hips 阻尼差（见 ChestDamping），此项只在
	/// 低油门/加速段起作用。保留是为 L1956 保真，不是巡航姿态的旋钮。</summary>
	public float LeanPush = 0.00125f;

	/// <summary>行走时头伺服目标从躯干轴向朝向的混合比（≙ L1957 头目标 = 胸 + HeadLookDir×脖长
	/// ——行进中拾荒者的头是探在胸前方的，不是顶在躯干轴上；弓背剪影的主要来源。满速取此值，
	/// 随 RunSpeed 线性；RW 满幅 = 1.0（头全水平探出），白盒取小些防「掉头」观感）。</summary>
	public float HeadLean = 0.7f;

	/// <summary>髋高度伺服的目标高度（≙ 拉向地格中心：RW 髋 rad 7px 贴地 + 格中心 10px ≈ 目标在
	/// 物理静息位上方 3px——伺服只给轻微上提，体重由地面/刚性躯干杆承担）。</summary>
	public float HipRideHeight = 0.25f;

	/// <summary>髋高度伺服增益（≙ (格中心y - 髋y) * 0.1）。</summary>
	public float HipLiftGain = 0.1f;

	/// <summary>髋竖直速度阻尼（≙ hips.vel.y *= 0.5）。</summary>
	public float HipVelYDamp = 0.5f;

	/// <summary>逐 chunk 阻尼（≙ Act 各向异性阻尼 胸0.9/髋0.8/头0.8——胸惯性最大）。
	/// 胸-髋阻尼差是**巡航弓背深度的真实旋钮**（消融实证）：等量推进注入下胸留速多、髋留速少，
	/// 刚性躯干杆把持续速度差转成前倾角；差拉平（0.85/0.85）躯干即完全直立。预设按此调弓背：
	/// brute 0.88/0.72（最深 up≈0.82）、scavenger 0.9/0.8（中等 ≈0.85）、waif 0.92/0.9（近直立 ≈0.95）。
	/// 头阻尼要满足「头伺服最大牵引 ≥ (1-HeadDamping)×巡航速度」，否则头追不上巡航退化成
	/// 被脖子拖行、垂在胸后（waif 0.82 时曾复现，提到 0.88 修复）。HeadDamping 三预设统一 0.88
	/// （掉头响应轮从 RW 原值 0.8 提高：0.8 时巡航拖拽吃光伺服牵引余量，掉头后头要 ~80 tick
	/// 才横越到新前方——配合 HeadServoRange 放宽，就位缩到 ~14 tick，零振铃零站姿抖动）。</summary>
	public float ChestDamping = 0.9f;
	public float HipsDamping = 0.8f;
	public float HeadDamping = 0.88f;

	// —— 推进（≙ 对胸+髋沿路径方向施力 0.8*MovementSpeed）——
	public float BaseSpeed = 0.06f;
	public float MaxMoveSpeed = 0.08f;

	// —— 腿（复用 Limb，anchor=髋；≙ ScavengerLeg：connection=髋、legLength 20~40px、huntSpeed 12px=0.3）——
	public float LegLength = 0.6f;
	public float FootRadius = 0.06f;
	public float LegSpeed = 0.3f;
	public float LegQuickness = 0.7f;
	public int LegGripDelay = 3;
	public float StepLength = 0.6f;
	public float LiftFeet = 0.2f;
	public float FeetDown = 0.6f;
	public float LegLateral = 0.3f;
	public bool SmoothenLegMovement = true;

	// —— 手臂（Arm；≙ ScavengerHand：armLength 50px=1.25、huntSpeed 24px=0.6、quickness 0.95）——
	public float ArmLength = 1.25f;
	public float HandRadius = 0.06f;
	public float ArmSpeed = 0.6f;
	public float ArmQuickness = 0.95f;

	/// <summary>臂长钳制后速度向肩速靠拢比例（≙ ConnectToPoint adaptVel 0.4；Slugcat 用 0 = 贴身不甩）。</summary>
	public float ArmAdaptVel = 0.4f;

	/// <summary>额外叠加的肩速份额（≙ exaggerateVel 0.1——两个标量调出两种手感的那对旋钮之二）。</summary>
	public float ArmExaggerate = 0.1f;

	// —— knuckle-walk（≙ knucklePos 合法窗 + 腾空俯仰泵）——
	/// <summary>撑点落到身后超过此距离即失效（≙ KnucklePosLegal 身后 20px = 0.5m 单侧窗）。</summary>
	public float KnuckleBehindWindow = 0.5f;

	/// <summary>撑点在身前的最大合法距离（≙ 前方 150px = 3.75m）。</summary>
	public float KnuckleAheadWindow = 3.75f;

	/// <summary>俯仰泵的距离映射远端（≙ InverseLerp(40px,10px,·) 的 40px = 1.0m：撑点还远 → 伸手够）。</summary>
	public float KnucklePumpFar = 1.0f;

	/// <summary>俯仰泵的距离映射近端（≙ 10px = 0.25m：撑点已近 → 蹬地过身）。</summary>
	public float KnucklePumpNear = 0.25f;

	/// <summary>俯仰泵力度下限（≙ LerpMap(速度,0,5px, 0.5px,3.5px) 的 0.5px = 0.0125）。</summary>
	public float KnucklePumpMin = 0.0125f;

	/// <summary>俯仰泵力度上限（≙ 3.5px = 0.0875，随速度增长）。</summary>
	public float KnucklePumpMax = 0.0875f;
}
