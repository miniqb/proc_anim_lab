using System;
using System.Collections.Generic;
using Godot;
using ProcAnimLab.Render;

namespace ProcAnimLab.RatArena;

/// <summary>
/// 被枪击打断的鼠煞肢体远端段（前臂 肘→腕→手 / 小腿 膝→踝→脚）：宿主侧独立 Verlet
/// 三点短链，与内核完全脱钩。生命周期只有两态：Falling（重力 + 房间盒碰撞 + 落地强摩擦）
/// → Resting（完全睡眠，「完全掉在地上之后不再动弹」）。本物种不做接肢——没有牵引/重接。
/// 模板 = DaddyLongLegsSeveredPiece（物性常量逐字沿用），差异：3 点链 + **逐边静息长度
/// 数组**（播种时按实距冻结——肘→腕短、腕→手长，不是等距链）。
/// 碰撞 = 房间盒常量钳位（构造传入 min/max）；点序固定：index 0 = 断口端（肘/膝）。
/// 渲染复用正式层 TubeMeshBuilder（srgb 空间调色，苍白灰管 + 断口端暗红）。
/// </summary>
public sealed class RatSeveredPiece
{
    public enum PieceState
    {
        Falling,
        Resting,
    }

    private const int ConstraintIterations = 4;
    private const float AirDamping = 0.985f;
    private const float GroundBounce = 0.22f;
    private const float GroundFriction = 0.55f;
    // 睡眠判定：全点近地 + 近静止连续这么多 tick 才冻结，避免弹跳间隙误睡。
    private const int RestRequiredStillTicks = 20;
    private const float RestVelocityThreshold = 0.004f;
    private const float RestGroundSlack = 0.03f;

    // 管色：苍白灰皮 + 断口端第一站染暗红（正式渲染件 Wound 同源色值）。
    private static readonly Color FleshColor = new(0.55f, 0.53f, 0.50f);
    private static readonly Color WoundColor = new(0.24f, 0.09f, 0.08f);

    private readonly Vector3[] _pos;
    private readonly Vector3[] _lastPos;
    private readonly Vector3[] _vel;
    private readonly float[] _radii;
    private readonly float[] _restLengths;
    private readonly bool _isArm;
    private readonly Vector3 _boundsMin;
    private readonly Vector3 _boundsMax;
    private int _stillTicks;

    private TubeMeshBuilder? _tube;
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _visRadii = new();
    private readonly List<Color> _colors = new();
    private readonly List<TubeStation> _stations = new();

    public PieceState State { get; private set; } = PieceState.Falling;

    /// <summary>播种：三点（断口→末梢序）+ 统一初速；LastPos = Pos − velocity，
    /// 断落瞬间的渲染插值无缝（Daddy 先例）。逐边静息长度按播种实距冻结。</summary>
    public RatSeveredPiece(Vector3[] pts3, Vector3 velocity, float[] radii, bool isArm,
        Vector3 roomMin, Vector3 roomMax)
    {
        ArgumentNullException.ThrowIfNull(pts3);
        ArgumentNullException.ThrowIfNull(radii);
        if (pts3.Length != 3 || radii.Length != 3)
            throw new ArgumentException("RatSeveredPiece 是固定三点链（断口/中节/末梢）。");
        _pos = new Vector3[3];
        _lastPos = new Vector3[3];
        _vel = new Vector3[3];
        _radii = new float[3];
        for (int i = 0; i < 3; i++)
        {
            _pos[i] = pts3[i];
            _lastPos[i] = pts3[i] - velocity;
            _vel[i] = velocity;
            _radii[i] = MathF.Max(0.015f, radii[i]);
        }
        _restLengths = new float[2];
        for (int i = 0; i < 2; i++)
            _restLengths[i] = _pos[i + 1].DistanceTo(_pos[i]);
        _isArm = isArm;
        _boundsMin = roomMin;
        _boundsMax = roomMax;
    }

    public void Tick(Vector3 gravityPerTick)
    {
        if (State == PieceState.Resting)
            return;

        for (int i = 0; i < _pos.Length; i++)
        {
            _lastPos[i] = _pos[i];
            _vel[i] *= AirDamping;
            _vel[i] += gravityPerTick;
        }
        for (int i = 0; i < _pos.Length; i++)
            _pos[i] += _vel[i];

        SolveConstraints();
        CollideBounds();
        UpdateRest();
    }

    private void SolveConstraints()
    {
        for (int iteration = 0; iteration < ConstraintIterations; iteration++)
        {
            for (int i = 1; i < _pos.Length; i++)
            {
                Vector3 delta = _pos[i] - _pos[i - 1];
                float distance = delta.Length();
                if (distance <= 1e-6f)
                    continue;
                // 双向精确距离约束（逐边静息长度）：断肢是两截刚性小节，不是橡皮筋。
                Vector3 correction = delta / distance * ((distance - _restLengths[i - 1]) * 0.5f);
                _pos[i - 1] += correction;
                _vel[i - 1] += correction;
                _pos[i] -= correction;
                _vel[i] -= correction;
            }
        }
    }

    private void CollideBounds()
    {
        for (int i = 0; i < _pos.Length; i++)
        {
            float radius = _radii[i];
            Vector3 position = _pos[i];
            Vector3 velocity = _vel[i];
            if (position.Y < _boundsMin.Y + radius)
            {
                position.Y = _boundsMin.Y + radius;
                if (velocity.Y < 0f)
                    velocity.Y = -velocity.Y * GroundBounce;
                // 微弹直接吸收：不然逐 tick 的重力→反弹会持续注入 ~0.005 的竖直速度，
                // 恰好卡在入睡阈值上方，断落段永远睡不着。
                if (MathF.Abs(velocity.Y) < 0.01f)
                    velocity.Y = 0f;
                velocity.X *= GroundFriction;
                velocity.Z *= GroundFriction;
            }
            if (position.Y > _boundsMax.Y - radius)
            {
                position.Y = _boundsMax.Y - radius;
                velocity.Y = MathF.Min(velocity.Y, 0f);
            }
            if (position.X < _boundsMin.X + radius)
            {
                position.X = _boundsMin.X + radius;
                velocity.X = MathF.Max(velocity.X, 0f);
            }
            else if (position.X > _boundsMax.X - radius)
            {
                position.X = _boundsMax.X - radius;
                velocity.X = MathF.Min(velocity.X, 0f);
            }
            if (position.Z < _boundsMin.Z + radius)
            {
                position.Z = _boundsMin.Z + radius;
                velocity.Z = MathF.Max(velocity.Z, 0f);
            }
            else if (position.Z > _boundsMax.Z - radius)
            {
                position.Z = _boundsMax.Z - radius;
                velocity.Z = MathF.Min(velocity.Z, 0f);
            }
            _pos[i] = position;
            _vel[i] = velocity;
        }
    }

    private void UpdateRest()
    {
        bool still = true;
        for (int i = 0; i < _pos.Length; i++)
        {
            if (_pos[i].Y > _boundsMin.Y + _radii[i] + RestGroundSlack
                || _vel[i].Length() > RestVelocityThreshold)
            {
                still = false;
                break;
            }
        }
        _stillTicks = still ? _stillTicks + 1 : 0;
        if (_stillTicks < RestRequiredStillTicks)
            return;
        State = PieceState.Resting;
        for (int i = 0; i < _pos.Length; i++)
        {
            _vel[i] = Vector3.Zero;
            _lastPos[i] = _pos[i];
        }
    }

    public void BuildVisual(Node3D parent)
    {
        _tube = new TubeMeshBuilder();
        _tube.Build(parent, srgbVertexColors: true);
    }

    public void ClearVisual()
    {
        _tube?.Clear();
        _tube = null;
    }

    /// <summary>渲染：前臂剖面 [0.042→0.028] + 手爪瘤（r0.07）；小腿 [0.050→0.030] +
    /// 脚板短管。断口端第一站染暗红——落在地上也读得出「这是打下来的」。</summary>
    public void Render(float interpolation)
    {
        if (_tube is null)
            return;
        _tube.BeginFrame();
        _pts.Clear();
        _visRadii.Clear();
        _colors.Clear();
        float rootRadius = _isArm ? 0.042f : 0.050f;
        float tipRadius = _isArm ? 0.028f : 0.030f;
        for (int i = 0; i < _pos.Length; i++)
        {
            float t = i / (float)(_pos.Length - 1);
            _pts.Add(_lastPos[i].Lerp(_pos[i], interpolation));
            _visRadii.Add(Mathf.Lerp(rootRadius, tipRadius, t));
            _colors.Add(i == 0 ? WoundColor : FleshColor);
        }
        SplineSampler.Sample(_pts, _visRadii, _colors, 4, _stations);
        if (_stations.Count >= 2)
            _tube.AddTube(_stations, Vector3.Up, 7);

        Vector3 tip = _pts[^1];
        Vector3 axis = tip - _pts[1];
        Vector3 dir = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Forward;
        if (_isArm)
        {
            // 手爪瘤：末梢一颗钝圆手团（渲染件手瘤的断落版）。
            _tube.AddKnob(tip, 0.07f, FleshColor.Darkened(0.15f));
        }
        else
        {
            // 脚板短管：沿链末向前伸出一小截脚掌。
            _stations.Clear();
            _stations.Add(new TubeStation(tip, 0.034f, FleshColor.Darkened(0.1f)));
            _stations.Add(new TubeStation(tip + dir * 0.09f, 0.018f, FleshColor.Darkened(0.2f)));
            _tube.AddTube(_stations, Vector3.Up, 5);
        }
        _tube.EndFrame();
    }
}
