using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Centipede;

namespace ProcAnimLab.Render;

/// <summary>蜈蚣渲染调色板。参考图：亮橙甲片 + 近黑节间/腿/触须（"亮片/暗膜/亮片"美学，
/// ≙ CentipedeGraphics 的 ShellColor/SecondaryShellColor：甲片 HSL L=0.5、节间膜 L=0.2）。</summary>
internal readonly struct CentipedeRenderPalette
{
    public readonly Color Body;      // 底管/腿/触须的近黑
    public readonly Color Plate;     // 背甲亮色
    public readonly Color PlateDark; // 甲片暗端（近头尾的甲片向它渐变）

    public CentipedeRenderPalette(Color body, Color plate)
    {
        Body = body;
        Plate = plate;
        PlateDark = plate.Lerp(body, 0.55f);
    }

    private static readonly Color SilhouetteBlack = new(0.07f, 0.055f, 0.06f);

    public static CentipedeRenderPalette ForStableId(string stableId) => stableId switch
    {
        "centipede/long" => new CentipedeRenderPalette(SilhouetteBlack, new Color(0.95f, 0.22f, 0.10f)),
        "centipede/armored" => new CentipedeRenderPalette(SilhouetteBlack, new Color(0.62f, 0.36f, 0.14f)),
        "centipede/ribbon" => new CentipedeRenderPalette(SilhouetteBlack, new Color(0.42f, 0.85f, 0.30f)),
        _ => new CentipedeRenderPalette(SilhouetteBlack, new Color(1.0f, 0.42f, 0.10f)),
    };
}

/// <summary>
/// 蜈蚣正式渲染器（技术验证件二号，≙ CentipedeGraphics 的 3D 移植）。
/// 底管 = 全链 chunk + 头尾虚拟外推点的连续扫管（近黑，≙ TubeSprite + 节间暗膜合并：
/// 甲片之间露出的暗管就是"节间暗环"，不需要单独几何）；背甲 = 每节一个扁椭球刚性件，
/// 由中心差分切线 + 每节**真实** SupportNormal（时域低通 + 邻节空间平滑）定向——RW 的
/// 三控制假 roll 在 3D 里被真实逐节支撑法线替代（渲染研究 §1.7 对照表）。
/// 腿 = 足端粒子 + 两骨 IK 细管（弯折 pole = 外侧+上拱，参考图的拱腿轮廓），近黑。
/// 触须 = 两端各两根纯渲染摆管（RW whiskers 是图形层粒子，这里取无状态正弦摆简化）。
/// 对内核只读，零写回。
/// </summary>
internal sealed class CentipedeFormalRenderer : IFormalRenderer
{
    private readonly CentipedeLocomotionController _c;
    private readonly CentipedeRenderPalette _pal;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private readonly List<MeshInstance3D> _plates = new();
    private SphereMesh? _plateMesh;

    // 渲染侧化妆状态：逐节平滑 up、触须摆相位。
    private Vector3[] _segUps = System.Array.Empty<Vector3>();
    private float _sway;

    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly List<TubeStation> _stations = new();

    public CentipedeFormalRenderer(CentipedeLocomotionController controller, string stableId)
    {
        _c = controller;
        _pal = CentipedeRenderPalette.ForStableId(stableId);
        _segUps = new Vector3[controller.Segments.Count];
        for (int i = 0; i < _segUps.Length; i++)
        {
            _segUps[i] = Vector3.Up;
        }
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root);

        _plateMesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 10 };
        int n = _c.Segments.Count;
        for (int i = 0; i < n; i++)
        {
            float t = n <= 1 ? 0.5f : i / (float)(n - 1);
            // 端部甲片向暗端渐变（≙ RW 端节窄甲 + 颜色收暗），中段最亮。
            float bright = Mathf.Sin(Mathf.Clamp(t, 0.02f, 0.98f) * Mathf.Pi);
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = _pal.PlateDark.Lerp(_pal.Plate, Mathf.Pow(bright, 0.45f)),
            };
            var plate = new MeshInstance3D { Mesh = _plateMesh, MaterialOverride = mat };
            _root.AddChild(plate);
            _plates.Add(plate);
        }
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _plates.Clear();
        _plateMesh = null;
    }

    public void SetVisible(bool visible)
    {
        if (_root is not null)
        {
            _root.Visible = visible;
        }
    }

    public void Draw(float alpha, float dt)
    {
        if (_root is null)
        {
            return;
        }
        var segments = _c.Segments;
        int n = segments.Count;
        _sway += dt;

        // —— 逐节平滑 up：真实支撑法线时域低通 + 邻节平均（≙ RW bodyRotations 沿链 Slerp 的
        // 真法线版；无支撑节继承邻节，整链永不突跳）。——
        float k = 1f - Mathf.Exp(-5f * dt);
        for (int i = 0; i < n; i++)
        {
            Vector3 target = segments[i].SupportConfidence > 0.1f
                ? segments[i].SupportNormal
                : _segUps[i];
            _segUps[i] = (_segUps[i] + (target - _segUps[i]) * k).Normalized();
        }

        // —— 底管：全链 + 头尾外推（≙ 虚拟端点 ±10px），近黑。——
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        Vector3 p0 = segments[0].Chunk.LerpPos(alpha);
        Vector3 p1 = segments[Mathf.Min(1, n - 1)].Chunk.LerpPos(alpha);
        Vector3 headDir = p0 - p1;
        headDir = headDir.LengthSquared() > 1e-8f ? headDir.Normalized() : Vector3.Right;
        float headR = segments[0].Chunk.Radius;
        _pts.Add(p0 + headDir * (headR * 0.9f));
        _radii.Add(headR * 0.4f);
        _colors.Add(_pal.Body);
        for (int i = 0; i < n; i++)
        {
            _pts.Add(segments[i].Chunk.LerpPos(alpha));
            _radii.Add(segments[i].Chunk.Radius * 0.85f);
            _colors.Add(_pal.Body);
        }
        Vector3 pn = segments[n - 1].Chunk.LerpPos(alpha);
        Vector3 tailDir = pn - segments[Mathf.Max(0, n - 2)].Chunk.LerpPos(alpha);
        tailDir = tailDir.LengthSquared() > 1e-8f ? tailDir.Normalized() : -headDir;
        float tailR = segments[n - 1].Chunk.Radius;
        _pts.Add(pn + tailDir * (tailR * 0.9f));
        _radii.Add(tailR * 0.35f);
        _colors.Add(_pal.Body);

        _tube.BeginFrame();
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, _segUps[0], 9);

        DrawLegs(alpha, dt);
        DrawAntennae(alpha);
        _tube.EndFrame();

        DrawPlates(alpha);
    }

    /// <summary>背甲：扁椭球，中心差分切线 + 平滑 up 定向，沿 up 抬到管背。甲长略短于
    /// 节距 → 甲片间露出暗底管 = 节间暗环（RW 识别度最高的连接美学，零额外几何）。</summary>
    private void DrawPlates(float alpha)
    {
        var segments = _c.Segments;
        int n = segments.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 prev = segments[Mathf.Max(0, i - 1)].Chunk.LerpPos(alpha);
            Vector3 next = segments[Mathf.Min(n - 1, i + 1)].Chunk.LerpPos(alpha);
            Vector3 tangent = prev - next; // 头向为正（渲染只要轴向，正负不影响椭球）
            tangent = tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : Vector3.Right;
            Vector3 up = _segUps[i] - tangent * _segUps[i].Dot(tangent);
            if (up.LengthSquared() < 1e-8f)
            {
                up = tangent.Cross(Vector3.Right);
                if (up.LengthSquared() < 1e-8f)
                {
                    up = tangent.Cross(Vector3.Up);
                }
            }
            up = up.Normalized();
            Vector3 side = tangent.Cross(up).Normalized();

            Vector3 pos = segments[i].Chunk.LerpPos(alpha);
            float r = segments[i].Chunk.Radius;
            var basis = new Basis(
                side * (r * 1.28f),
                up * (r * 0.55f),
                tangent * (r * 1.05f));
            _plates[i].Transform = new Transform3D(basis, pos + up * (r * 0.42f));
        }
    }

    private void DrawLegs(float alpha, float dt)
    {
        foreach (CentipedeLeg leg in _c.Legs)
        {
            Vector3 rootPos = leg.Anchor.Chunk.LerpPos(alpha);
            Vector3 foot = leg.LastPos.Lerp(leg.Pos, alpha);
            int segIdx = leg.Anchor.Index;
            Vector3 up = _segUps[Mathf.Clamp(segIdx, 0, _segUps.Length - 1)];

            // 弯折 pole：外侧 + 上拱（参考图：蜈蚣腿先向外上方拱起再落地）。
            Vector3 sideDir = leg.Anchor.Side * leg.Side;
            if (sideDir.LengthSquared() < 1e-8f)
            {
                sideDir = Vector3.Back * leg.Side;
            }
            Vector3 pole = (sideDir.Normalized() + up * 0.9f).Normalized();

            float bone = leg.Length * 0.58f;
            Vector3 knee = TwoBoneIk.Solve(rootPos, foot, bone, bone, pole);

            _pts.Clear();
            _radii.Clear();
            _colors.Clear();
            _pts.Add(rootPos);
            _radii.Add(0.026f);
            _colors.Add(_pal.Body);
            _pts.Add(knee);
            _radii.Add(0.016f);
            _colors.Add(_pal.Body);
            _pts.Add(foot);
            _radii.Add(0.007f);
            _colors.Add(_pal.Body);
            SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
            _tube.AddTube(_stations, up, 5);
        }
    }

    /// <summary>触须：两端各两根，无状态正弦摆（RW whiskers 是图形层垂摆粒子；纯渲染简化，
    /// 领航端的触须前伸更长——头尾可切换，观感跟随 LeadEnd）。</summary>
    private void DrawAntennae(float alpha)
    {
        var segments = _c.Segments;
        int n = segments.Count;
        if (n < 2)
        {
            return;
        }
        bool leadIsStart = _c.LeadEnd == CentipedeLeadEnd.Start;
        DrawAntennaPair(alpha, 0, 1, leadIsStart ? 1.0f : 0.55f, 0f);
        DrawAntennaPair(alpha, n - 1, n - 2, leadIsStart ? 0.55f : 1.0f, Mathf.Pi * 0.7f);
    }

    private void DrawAntennaPair(float alpha, int endIdx, int innerIdx, float lengthScale, float phase)
    {
        var segments = _c.Segments;
        Vector3 endPos = segments[endIdx].Chunk.LerpPos(alpha);
        Vector3 fwd = endPos - segments[innerIdx].Chunk.LerpPos(alpha);
        fwd = fwd.LengthSquared() > 1e-8f ? fwd.Normalized() : Vector3.Right;
        Vector3 up = _segUps[endIdx];
        Vector3 side = fwd.Cross(up);
        side = side.LengthSquared() > 1e-8f ? side.Normalized() : Vector3.Back;
        float r = segments[endIdx].Chunk.Radius;
        float len = r * 4.2f * lengthScale;

        for (int s = -1; s <= 1; s += 2)
        {
            float sway = Mathf.Sin(_sway * 2.3f + phase + s * 1.3f) * 0.22f;
            Vector3 dir = (fwd + side * (s * (0.45f + sway)) + up * (0.1f + sway * 0.5f)).Normalized();
            Vector3 baseP = endPos + fwd * (r * 0.4f);
            Vector3 mid = baseP + dir * (len * 0.5f) + up * (len * 0.12f);
            Vector3 tip = baseP + dir * len + up * (len * -0.05f);

            _pts.Clear();
            _radii.Clear();
            _colors.Clear();
            _pts.Add(baseP);
            _radii.Add(r * 0.16f);
            _colors.Add(_pal.Body);
            _pts.Add(mid);
            _radii.Add(r * 0.09f);
            _colors.Add(_pal.Body);
            _pts.Add(tip);
            _radii.Add(0.004f);
            _colors.Add(_pal.Body.Lerp(_pal.Plate, 0.25f));
            SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
            _tube.AddTube(_stations, up, 5);
        }
    }
}
