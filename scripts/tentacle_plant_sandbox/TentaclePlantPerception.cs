using System;
using Godot;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 感知模块的视线接缝：from→to 是否被墙层（碰撞层 1）阻断。玩家在层 2，
/// 天然不会挡住指向自己的射线。宿主实现（竞技场用 DirectSpaceState）。
/// </summary>
public interface IPerceptionRaycast
{
	bool LineBlocked(Vector3 from, Vector3 to);
}

/// <summary>感知器的纯值配置；速率/衰减均已折算到 tick 域（40 tick/s）。</summary>
public sealed class TentaclePlantPerceptionConfig
{
	public float LockCosHalfAngle;
	public float LockLength;
	public float AwareCosHalfAngle;
	public float AwareRadius;
	public float MoveDeadzonePerTick;
	public float LockThreshold;
	public float AwareThreshold;
	public float LockStillDecay;
	public float LockOutDecay;
	public float AwareStillDecay;
	public float AwareOutDecay;
	public float SensitizeGainPerMeter;
	public float SensitizeMax;
	public float SensitizeRecovery;
	public int LockHoldTicks;
	public int ListenStillTicks;
	public int ListenGraceTicks;
	public float ListenGain;
	public float BearingErrorRadians;
	public float RangeErrorFraction;
}

/// <summary>
/// 本 tick 的感知事件集：ProbeRequested / LockAcquired 是阈值沿（触发即消耗对应
/// 累计器）；ListenTwitch 是电平——探头期区内每个被感知的移动 tick 都为真，
/// 不消耗任何累计。
/// </summary>
public readonly record struct TentaclePlantPerceptionEvents(
	bool ProbeRequested,
	bool LockAcquired,
	bool ListenTwitch);

/// <summary>
/// 灯泡"光变化"感知器（纯 C#，无 Node）。设定：怪物无眼，喉部发光器官只能
/// 检测反射光的变化——静止的猎物完全不可见。两区是同一器官的两条等信噪比面：
/// 锁定锥（窄角×短长，锥内精确定位移动）与察觉区（宽角×远半径，只给
/// 粗方位+幅度粗测距）。累计只对移动发生、速率 ∝ 移动量×敏化；衰减只在静止
/// 时发生（出区稍快、不清零）；敏化随刺激线性抬升、指数恢复（Aplysia 式）。
/// 光之外另有一条**触觉通道** <see cref="TouchLock"/>（嘴碰到猎物直接置锁定，不看
/// 移动量）与回伪装的 <see cref="ResetAlertness"/>，都由宿主按自己的判定调用。
/// 策略（何时探头、何时喂真目标）归宿主；本类只产出事件与量。
/// </summary>
public sealed class TentaclePlantPerception
{
	private readonly TentaclePlantPerceptionConfig _c;
	private readonly IPerceptionRaycast _raycast;
	private readonly Random _rng;

	private long _tick;
	private float _aware;
	private float _lock;
	private float _sens = 1f;
	private int _stillTicks;
	private int _twitchGraceLeft;
	private bool _noiseStale = true;
	private long _lockFreshUntil = -1;
	private Vector3 _lastPerceivedPoint;
	private Vector3 _aimLead;
	private long _lastPerceivedTick = long.MinValue;
	private Vector3 _coarseEstimate;
	private long _coarseTick = long.MinValue;
	private float _bearingError;
	private float _rangeError;

	public float Aware => _aware;
	public float Lock => _lock;
	public float Sensitize => _sens;

	/// <summary>锁定新鲜期内为真：突刺仍瞄"最后感知点"的窗口。</summary>
	public bool LockFresh => _tick <= _lockFreshUntil;

	/// <summary>突刺瞄准点 = 最后精确感知的猎物位置 + 触觉引导量（光感知写入时引导量
	/// 清零；触觉锁定时是宿主给的穿过猎物的一段——头已贴着猎物，瞄眼位本身会让内核的
	/// 突刺方向退化成伺服残差）。</summary>
	public Vector3 AimPoint => _lastPerceivedPoint + _aimLead;

	/// <summary>最后精确感知的猎物位置本身（不含引导量）：探头规划与冷却退距用。</summary>
	public Vector3 PreyPoint => _lastPerceivedPoint;

	/// <summary>
	/// 触觉锁定：嘴碰到猎物是最强的"变化"信号，不经光感知累计直接置锁定——猎物
	/// 位置钉到眼位、瞄点引导量记为 <paramref name="aimLead"/>（宿主给的穿过猎物的
	/// 一段，头已经贴在猎物上，瞄眼位本身会让内核的突刺方向退化成伺服残差）、新鲜期
	/// 从现在起算、锁定累计归零（与阈值沿同款消耗）。宿主在接触判定成立的每个 tick
	/// 调用（接触期间持续续期）；不看 moveAmount，攻击门、失聪门与相位门都归宿主。
	/// </summary>
	public void TouchLock(Vector3 preyEyePos, Vector3 aimLead)
	{
		_lastPerceivedPoint = preyEyePos;
		_aimLead = aimLead;
		_lastPerceivedTick = _tick;
		_lockFreshUntil = _tick + _c.LockHoldTicks;
		_lock = 0f;
	}

	/// <summary>
	/// 回伪装时的清醒度重置：敏化回 1、两累计器清零——重新蜷成吸顶灯的是个干净的
	/// 伏击者，不带着上一轮搜索的火气慢慢消（否则回伪装后轻轻一动就又探头）。
	/// 锁定新鲜期与最后感知点是"当前知识"而非敏感度，不动；移动回合计数也不动。
	/// </summary>
	public void ResetAlertness()
	{
		_sens = 1f;
		_aware = 0f;
		_lock = 0f;
	}

	public TentaclePlantPerception(
		TentaclePlantPerceptionConfig config,
		IPerceptionRaycast raycast,
		int seed)
	{
		_c = config;
		_raycast = raycast;
		_rng = new Random(seed);
	}

	/// <summary>
	/// 察觉区给出的粗估计（幅度测距 + 冻结噪声对）；无任何感知历史时回退 fallback。
	/// 锁定锥内的精确感知点比粗估计新时优先。
	/// </summary>
	public Vector3 BestEstimate(Vector3 fallback)
	{
		if (_lastPerceivedTick == long.MinValue && _coarseTick == long.MinValue)
		{
			return fallback;
		}
		return _lastPerceivedTick >= _coarseTick ? _lastPerceivedPoint : _coarseEstimate;
	}

	/// <summary>
	/// 每内核 tick 调一次。moveAmount 为猎物眼位的逐 tick 位移（米/tick）；
	/// probing 为真时区内每个移动 tick 都发 ListenTwitch（触发性聆听：光束立即
	/// 照过去、持续移动就持续跟），并对移动回合开头一小段的**察觉累计**给近零
	/// 折扣（灯照到=免费警告；回合 = 静止够久后的一段移动）；锁定累计无折扣。
	/// 至多发 1 条视线射线（仅当猎物在几何区内且本 tick 有移动）。
	/// </summary>
	public TentaclePlantPerceptionEvents Tick(
		Vector3 headPos,
		Vector3 headForward,
		Vector3 preyEyePos,
		float moveAmount,
		bool probing)
	{
		_tick++;
		bool moving = moveAmount > _c.MoveDeadzonePerTick;

		Vector3 offset = preyEyePos - headPos;
		float distance = offset.Length();
		bool inLock = false;
		bool inAware = false;
		if (distance <= Mathf.Max(_c.AwareRadius, _c.LockLength))
		{
			float cosAngle = distance > 1e-4f
				? offset.Dot(headForward) / distance
				: 1f;
			bool lockGeometry = distance <= _c.LockLength &&
				cosAngle >= _c.LockCosHalfAngle;
			bool awareGeometry = distance <= _c.AwareRadius &&
				cosAngle >= _c.AwareCosHalfAngle;
			// 视线只在"会产生感知"时才花（静止的猎物本来就不可见，衰减分档
			// 用纯几何判定即可）。
			if (moving && (lockGeometry || awareGeometry) &&
				_raycast.LineBlocked(headPos, preyEyePos))
			{
				lockGeometry = false;
				awareGeometry = false;
			}
			inLock = lockGeometry;
			inAware = awareGeometry || lockGeometry;
		}

		// 移动回合：静止够久后的一段移动（按猎物真实动静计，不看是否被感知——
		// 回合是猎物的行为单元）。回合开头 ListenGraceTicks 个移动 tick 是
		// "twitch"，回合计数只在移动 tick 消耗，短暂停顿不重开回合；粗估计噪声对
		// 也按回合冻结（标脏，待下一个被感知的移动 tick 重抽）。
		if (moving && _stillTicks >= _c.ListenStillTicks)
		{
			_twitchGraceLeft = _c.ListenGraceTicks;
			_noiseStale = true;
		}
		bool listenTwitch = false;
		float awareGain = 1f;
		if (probing && moving && inAware)
		{
			// 触发性聆听：探头期区内任何被感知到的移动都立即照过去（事件每个
			// 移动 tick 都发，规划器连续重瞄——头跟着猎物转）。twitch 的折扣只压
			// 察觉累计（"灯照到=免费警告"说的是光束还没照到你的那一下）；锁定锥内
			// 灯已经在你身上，锁定累计与伪装态同速——否则点按式停走能在探照灯下
			// 永远不被锁定。
			listenTwitch = true;
			if (_twitchGraceLeft > 0)
			{
				awareGain = _c.ListenGain;
			}
		}
		if (moving && _twitchGraceLeft > 0)
		{
			_twitchGraceLeft--;
		}

		bool sensedMovement = moving && inAware;
		if (sensedMovement)
		{
			_sens = Mathf.Min(
				_c.SensitizeMax, _sens + _c.SensitizeGainPerMeter * moveAmount);
			float weighted = _sens * moveAmount;
			_aware = Mathf.Min(_c.AwareThreshold, _aware + weighted * awareGain);
			if (inLock)
			{
				_lock = Mathf.Min(_c.LockThreshold, _lock + weighted);
				_lastPerceivedPoint = preyEyePos;
				_aimLead = Vector3.Zero;
				_lastPerceivedTick = _tick;
				if (LockFresh)
				{
					_lockFreshUntil = _tick + _c.LockHoldTicks;
				}
			}
			else
			{
				_lock *= _c.LockOutDecay;
			}
			// 粗估计：每个"移动回合"冻结一次噪声对（回合开启时标脏，首个被感知的
			// 移动 tick 重抽——回合在视线外开启也不会沿用上一回合的误差）。
			if (_noiseStale)
			{
				_bearingError = ((float)_rng.NextDouble() * 2f - 1f) *
					_c.BearingErrorRadians;
				_rangeError = ((float)_rng.NextDouble() * 2f - 1f) *
					_c.RangeErrorFraction;
				_noiseStale = false;
			}
			_coarseEstimate = headPos +
				offset.Rotated(Vector3.Up, _bearingError) * (1f + _rangeError);
			_coarseTick = _tick;
		}
		else
		{
			_sens = 1f + (_sens - 1f) * _c.SensitizeRecovery;
			_aware *= inAware ? _c.AwareStillDecay : _c.AwareOutDecay;
			_lock *= inLock ? _c.LockStillDecay : _c.LockOutDecay;
		}

		bool probeRequested = false;
		if (_aware >= _c.AwareThreshold)
		{
			// 触发即消耗：天然阻尼"边界横跳反复探头"。
			probeRequested = true;
			_aware = 0f;
		}
		bool lockAcquired = false;
		if (_lock >= _c.LockThreshold)
		{
			lockAcquired = true;
			_lock = 0f;
			_lockFreshUntil = _tick + _c.LockHoldTicks;
		}

		_stillTicks = moving ? 0 : _stillTicks + 1;
		return new TentaclePlantPerceptionEvents(
			probeRequested, lockAcquired, listenTwitch);
	}
}

/// <summary>探头搜索规划器的纯值配置（tick 域）。</summary>
public sealed class TentaclePlantProbeConfig
{
	public float HeadRadius;
	public float ConeLength;
	public float StopBackoffRatio;
	public float MinDrop;
	public float SpeedPerTick;
	public float TurnBoost;
	public int DwellMinTicks;
	public int DwellMaxTicks;
	public int GazeMinTicks;
	public int GazeMaxTicks;
	public float LookbackProbability;
	public float HypoPreySpeedPerTick;
	public float SearchRadiusMax;
	public float BudgetTicks;
	public float StretchCost;
	public float ChainLength;
}

/// <summary>
/// 探头搜索状态机（纯 C#，无 Node）：动画一个"探测点"，头端由内核伺服跟随。
/// 策略 = 贝叶斯式搜索：起手直奔最佳估计（停头 = 估计距离 − 锥长大半，让锥尖
/// 而非嘴去够猎物）；无新信息时路点包络随时间扩张（∝ 假想猎物速度）；触发性
/// 聆听 → 光束急转 + 长凝视 + 包络坍缩重铺；常态性聆听 = 路点间短停顿；低概率
/// 回头急转复查初始估计点；预算随时间消耗、伸得越长耗得越快。
/// </summary>
public sealed class TentaclePlantProbePlanner
{
	private const float ArriveRadius = 0.12f;

	private readonly TentaclePlantProbeConfig _c;
	private readonly Random _rng;

	private Vector3 _root;
	private Vector3 _fallbackDir = Vector3.Down;
	private Vector3 _probePoint;
	private Vector3 _waypoint;
	private Vector3 _gazePoint;
	private Vector3 _estimate;
	private Vector3 _initialEstimate;
	private float _speed;
	private float _boost = 1f;
	private int _dwellLeft;
	private int _arrivalDwell;
	private bool _listening;
	private bool _gazeIsEstimate;
	private float _budget;
	private long _lastInfoTick;

	/// <summary>宿主每 tick 喂给内核的探测点（合成隐藏目标的位置）。</summary>
	public Vector3 ProbePoint => _probePoint;

	/// <summary>
	/// 当前凝视点 = 本路点对应的假想猎物位置（估计点/环采样点/回头杀的初始
	/// 估计点）。停头刻意停在它前方一截锥长处，所以"照哪"不能从探测点推导——
	/// 宿主用它构造光束朝向（感知锥轴 + 渲染嘴朝向）。
	/// </summary>
	public Vector3 GazePoint => _gazePoint;

	/// <summary>当前凝视点是否为**真实猎物估计**（Begin 的最佳估计 / 聆听重瞄），而非
	/// 路点采样或回头杀的假想位置。宿主的凝视前瞻只推假想点——光必须落在猎物身上，锁定才涨。</summary>
	public bool GazeIsEstimate => _gazeIsEstimate;

	/// <summary>剩余预算（tick 当量），HUD/调参用。</summary>
	public float Budget => _budget;

	public TentaclePlantProbePlanner(TentaclePlantProbeConfig config, int seed)
	{
		_c = config;
		_rng = new Random(seed);
	}

	/// <summary>开始一轮探头：直奔当前最佳估计。outward 用于退化方向回退。</summary>
	public void Begin(Vector3 headPos, Vector3 rootPos, Vector3 outward,
		Vector3 estimate, long tick)
	{
		_root = rootPos;
		_fallbackDir = outward;
		_estimate = estimate;
		_initialEstimate = estimate;
		_lastInfoTick = tick;
		_budget = _c.BudgetTicks;
		_boost = 1f;
		_speed = 0f;
		_probePoint = headPos;
		_waypoint = Stop(estimate);
		_gazePoint = estimate;
		_gazeIsEstimate = true;
		_dwellLeft = 0;
		_arrivalDwell = RollDwell();
		_listening = false;
	}

	/// <summary>
	/// 触发性聆听：猎物在区内的每个移动 tick 都调——光束急转并持续重瞄最新估计
	/// （头跟着猎物转），猎物一停就到位定住较长凝视；包络重置。凝视时长只在
	/// 进入聆听时掷一次，连续重瞄不重掷。
	/// </summary>
	public void OnListen(Vector3 estimate, long tick)
	{
		_estimate = estimate;
		_lastInfoTick = tick;
		_waypoint = Stop(estimate);
		_gazePoint = estimate;
		_gazeIsEstimate = true;
		_boost = _c.TurnBoost;
		_dwellLeft = 0;
		if (!_listening)
		{
			_listening = true;
			_arrivalDwell = RollGaze();
		}
	}

	/// <summary>
	/// 推进一 tick；返回 false 表示预算耗尽（宿主应回伪装）。锁定新鲜期内
	/// 宿主不调本方法（规划器与预算冻结）。
	/// </summary>
	public bool TickActive(long tick)
	{
		float extension = (_probePoint - _root).Length() /
			Mathf.Max(_c.ChainLength, 1e-3f);
		_budget -= 1f + _c.StretchCost * extension;
		if (_budget <= 0f)
		{
			return false;
		}

		if (_dwellLeft > 0)
		{
			// 聆听停顿（常态短、触发性凝视长）；结束后再挑下一路点。凝视真正
			// 结束才退出聆听——到位不算（头已停在猎物旁时每个移动 tick 都会
			// "重瞄→到位"，若到位就退出，凝视时长会逐 tick 重掷）。
			_speed = 0f;
			if (--_dwellLeft == 0)
			{
				_listening = false;
				PickNextWaypoint(tick);
			}
			return true;
		}

		Vector3 to = _waypoint - _probePoint;
		float distance = to.Length();
		if (distance <= ArriveRadius)
		{
			_dwellLeft = Mathf.Max(1, _arrivalDwell);
			_boost = 1f;
			return true;
		}
		// 缓入缓出 + 速度上限：起步斜坡 ~0.75s，接近路点时按距离比例减速。
		float accel = _c.SpeedPerTick / 30f;
		_speed = Mathf.Min(
			_c.SpeedPerTick * _boost,
			Mathf.Min(_speed + accel, 0.15f * distance + 0.002f));
		_probePoint += to / distance * Mathf.Min(_speed, distance);
		return true;
	}

	private void PickNextWaypoint(long tick)
	{
		_arrivalDwell = RollDwell();
		_gazeIsEstimate = false;
		if (_rng.NextDouble() < _c.LookbackProbability)
		{
			// 回头杀：急转回初始估计点复查一眼（"它是不是又回来了"）。
			_waypoint = Stop(_initialEstimate);
			_gazePoint = _initialEstimate;
			_boost = _c.TurnBoost;
			return;
		}
		// 包络扩张：不确定度 ∝ 无信息时长 × 假想猎物速度；在估计点周围的
		// 水平环内采样假想位置（扩张体现在包络上，路径仍是缓移+停顿）。
		float radius = Mathf.Min(
			_c.SearchRadiusMax,
			_c.HypoPreySpeedPerTick * (tick - _lastInfoTick));
		float angle = (float)(_rng.NextDouble() * Math.Tau);
		float ring = (0.3f + 0.7f * (float)_rng.NextDouble()) * radius;
		Vector3 hypo = _estimate +
			new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ring;
		hypo.Y = _estimate.Y;
		_waypoint = Stop(hypo);
		_gazePoint = hypo;
	}

	/// <summary>
	/// 停头位置：沿根→估计方向，停在"估计距离 − 锥长大半"处——锥尖够猎物、
	/// 嘴不过冲（自动避免把猎物甩进脑后盲区），并钳进舒适半径。
	/// </summary>
	private Vector3 Stop(Vector3 estimate)
	{
		Vector3 direction = estimate - _root;
		float distance = direction.Length();
		if (distance <= 1e-4f)
		{
			return _root + _fallbackDir * _c.MinDrop;
		}
		float stop = Mathf.Clamp(
			distance - _c.StopBackoffRatio * _c.ConeLength,
			_c.MinDrop,
			_c.HeadRadius);
		return _root + direction / distance * stop;
	}

	private int RollDwell() => RollRange(_c.DwellMinTicks, _c.DwellMaxTicks);

	private int RollGaze() => RollRange(_c.GazeMinTicks, _c.GazeMaxTicks);

	private int RollRange(int minimum, int maximum) =>
		minimum >= maximum ? minimum : minimum + _rng.Next(maximum - minimum + 1);
}
