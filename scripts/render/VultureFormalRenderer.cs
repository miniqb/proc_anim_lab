using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Vulture;

namespace ProcAnimLab.Render;

/// <summary>秃鹫渲染调色板。参考图：黑紫身体团 + 羽毛根黑梢亮的品种色渐变 + 骨白小头脸 +
/// 浅色肩盾（≙ RW ColorA/ColorB 双色系统 + KrakenMask 骨白面具）。</summary>
internal readonly struct VultureRenderPalette
{
    public readonly Color Body;       // 黑紫剪影
    public readonly Color FeatherTip; // 羽梢品种色
    public readonly Color Bone;       // 头骨/面具
    public readonly Color Shield;     // 肩盾

    public VultureRenderPalette(Color body, Color featherTip)
    {
        Body = body;
        FeatherTip = featherTip;
        Bone = new Color(0.93f, 0.89f, 0.80f);
        Shield = body.Lerp(new Color(0.72f, 0.60f, 0.46f), 0.72f);
    }

    private static readonly Color SilhouetteBlack = new(0.082f, 0.066f, 0.105f);

    public static VultureRenderPalette ForBreed(string breedName) => breedName switch
    {
        "king" => new VultureRenderPalette(SilhouetteBlack, new Color(0.55f, 0.76f, 0.96f)),
        "swift" => new VultureRenderPalette(SilhouetteBlack, new Color(0.62f, 0.78f, 0.32f)),
        "quad" => new VultureRenderPalette(SilhouetteBlack, new Color(0.80f, 0.45f, 0.88f)),
        _ => new VultureRenderPalette(SilhouetteBlack, new Color(0.88f, 0.34f, 0.30f)),
    };
}

/// <summary>
/// 秃鹫正式渲染器（技术验证件三号，≙ VultureGraphics 的 3D 移植）。
/// 躯干走**替换派**（≙ 4 chunk 风筝刚架被一张 KrakenBody 图整体盖住）：K4 刚架给出完整
/// 正交基（forward=脊柱轴、side=双肩轴、up=叉积——飞行生物的真实 roll 不依赖 SupportNormal），
/// 一个椭球刚性件盖住全部躯干 chunk。翅臂 = 沿翅段链扫管，根粗梢细、随 FlyingMode 收放形态
/// （≙ TentacleContour 的 flyingMode 插值剖面）。羽毛 = 弧长锚点刀片扇：链位 sqrt 前密后疏、
/// 后掠随链位增大、长度取翼型轮廓 × 展开度、根黑梢亮渐变（≙ VultureFeather 排布/后掠/
/// 轮廓公式，滞后弹簧粒子留给下一轮——先用无状态几何验证扇形观感）。骨白头 + 暗眼 +
/// 脖管、浅色肩盾补齐识别特征。对内核只读，零写回。
/// </summary>
internal sealed class VultureFormalRenderer : IFormalRenderer
{
    private readonly VultureFlightController _c;
    private readonly VultureRenderPalette _pal;
    private readonly int _feathersPerWing;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private MeshInstance3D? _torso;
    private MeshInstance3D? _head;
    private MeshInstance3D? _eyeL;
    private MeshInstance3D? _eyeR;
    private readonly List<MeshInstance3D> _shields = new();

    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly List<TubeStation> _stations = new();

    // 逐羽滞后状态（≙ VultureFeather 弹簧粒子的方向/长度低通子集）：拍动中扇面保持连贯，
    // 不随链段瞬时切线散开。纯渲染侧，按 [翅][羽] 索引。
    private Vector3[][] _featherDirs = System.Array.Empty<Vector3[]>();
    private float[][] _featherLens = System.Array.Empty<float[]>();

    public VultureFormalRenderer(VultureFlightController controller, string breedName, int feathersPerWing)
    {
        _c = controller;
        _pal = VultureRenderPalette.ForBreed(breedName);
        _feathersPerWing = feathersPerWing;
        _featherDirs = new Vector3[controller.Wings.Count][];
        _featherLens = new float[controller.Wings.Count][];
        for (int w = 0; w < controller.Wings.Count; w++)
        {
            _featherDirs[w] = new Vector3[feathersPerWing];
            _featherLens[w] = new float[feathersPerWing];
        }
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root);

        var bodyMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _pal.Body,
        };
        _torso = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 },
            MaterialOverride = bodyMat,
        };
        _root.AddChild(_torso);

        var boneMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _pal.Bone,
        };
        _head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 10 },
            MaterialOverride = boneMat,
        };
        _root.AddChild(_head);

        var eyeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.05f, 0.04f, 0.06f),
        };
        _eyeL = MakeBall(eyeMat);
        _eyeR = MakeBall(eyeMat);

        var shieldMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _pal.Shield,
        };
        for (int i = 0; i < 2; i++)
        {
            var shield = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 14, Rings = 7 },
                MaterialOverride = shieldMat,
            };
            _root.AddChild(shield);
            _shields.Add(shield);
        }
    }

    private MeshInstance3D MakeBall(StandardMaterial3D mat)
    {
        var ball = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = mat,
        };
        _root!.AddChild(ball);
        return ball;
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _torso = null;
        _head = null;
        _eyeL = null;
        _eyeR = null;
        _shields.Clear();
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

        // —— K4 刚架完整正交基（≙ 渲染研究：秃鹫的真实 roll 来自双肩轴，非 SupportNormal）——
        Vector3 front = _c.FrontSpine.LerpPos(alpha);
        Vector3 rear = _c.RearSpine.LerpPos(alpha);
        Vector3 shL = _c.ShoulderL.LerpPos(alpha);
        Vector3 shR = _c.ShoulderR.LerpPos(alpha);
        Vector3 forward = front - rear;
        forward = forward.LengthSquared() > 1e-8f ? forward.Normalized() : Vector3.Right;
        Vector3 side = shR - shL;
        side -= forward * side.Dot(forward);
        side = side.LengthSquared() > 1e-8f ? side.Normalized() : forward.Cross(Vector3.Up).Normalized();
        Vector3 up = side.Cross(forward).Normalized();

        // —— 躯干替换件：一个椭球盖住整个 4 chunk 风筝（≙ KrakenBody 单图）——
        Vector3 center = (front + rear + shL + shR) * 0.25f;
        float bodyLen = (front - rear).Length() + _c.FrontSpine.Radius + _c.RearSpine.Radius;
        float shoulderSpan = (shR - shL).Length();
        var torsoBasis = new Basis(
            side * (shoulderSpan * 0.52f),
            up * (bodyLen * 0.42f),
            -forward * (bodyLen * 0.64f));
        _torso!.Transform = new Transform3D(torsoBasis, center);

        // 肩盾（≙ KrakenShield：盖住翅根关节的浅色壳）。
        for (int i = 0; i < _shields.Count; i++)
        {
            Vector3 shoulder = i == 0 ? shL : shR;
            Vector3 outward = i == 0 ? -side : side;
            var shieldBasis = new Basis(
                outward * 0.16f,
                up * 0.22f,
                -forward * 0.13f);
            _shields[i].Transform = new Transform3D(shieldBasis,
                shoulder + up * 0.16f + outward * 0.05f);
        }

        // —— 脖管 + 骨白头 + 眼 ——
        Vector3 headPos = _c.Head.LerpPos(alpha);
        Vector3 neckDir = headPos - front;
        neckDir = neckDir.LengthSquared() > 1e-8f ? neckDir.Normalized() : forward;

        _tube.BeginFrame();
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(front);
        _radii.Add(_c.FrontSpine.Radius * 0.55f);
        _colors.Add(_pal.Body);
        _pts.Add(front.Lerp(headPos, 0.5f));
        _radii.Add(_c.Head.Radius * 0.62f);
        _colors.Add(_pal.Body);
        _pts.Add(headPos);
        _radii.Add(_c.Head.Radius * 0.5f);
        _colors.Add(_pal.Body);
        SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
        _tube.AddTube(_stations, up, 8);

        for (int w = 0; w < _c.Wings.Count; w++)
        {
            DrawWing(_c.Wings[w], w, alpha, dt, forward, up);
        }
        _tube.EndFrame();

        float headR = _c.Head.Radius;
        Vector3 hUp = up - neckDir * up.Dot(neckDir);
        hUp = hUp.LengthSquared() > 1e-8f ? hUp.Normalized() : neckDir.Cross(Vector3.Right).Normalized();
        Vector3 hSide = hUp.Cross(neckDir).Normalized();
        var headBasis = new Basis(
            hSide * (headR * 0.85f),
            hUp * (headR * 0.9f),
            -neckDir * (headR * 1.3f));
        _head!.Transform = new Transform3D(headBasis, headPos + neckDir * (headR * 0.2f));

        float eyeR = headR * 0.24f;
        var eyeBasis = new Basis(Vector3.Right * eyeR, Vector3.Up * eyeR, Vector3.Back * eyeR);
        _eyeL!.Transform = new Transform3D(eyeBasis,
            headPos + neckDir * (headR * 0.72f) + hSide * (headR * 0.5f) + hUp * (headR * 0.1f));
        _eyeR!.Transform = new Transform3D(eyeBasis,
            headPos + neckDir * (headR * 0.72f) - hSide * (headR * 0.5f) + hUp * (headR * 0.1f));
    }

    /// <summary>翅 = 臂管 + 羽毛刀片扇。臂管半径剖面随 FlyingMode 收放（≙ TentacleContour
    /// 爬行粗肢↔飞行细梁）；羽毛链位 sqrt 前密后疏、后掠向尖增大、轮廓 sin 包络、
    /// 折叠时贴链缩短变窄（≙ VultureFeather 全套排布公式的无状态子集）。</summary>
    private void DrawWing(VultureWing wing, int wingIndex, float alpha, float dt,
        Vector3 forward, Vector3 bodyUp)
    {
        int segCount = wing.Segments.Length;
        float ext = wing.FlyingMode;

        // 臂管：锚 chunk → 各翅段。
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        Vector3 anchorPos = wing.Anchor.LerpPos(alpha);
        _pts.Add(anchorPos);
        _radii.Add(Mathf.Lerp(0.15f, 0.11f, ext));
        _colors.Add(_pal.Body);
        for (int i = 0; i < segCount; i++)
        {
            float t = (i + 1) / (float)segCount;
            _pts.Add(wing.Segments[i].LerpPos(alpha));
            // 根粗梢细 + 收拢整体加粗（爬行肢形态）。
            float r = Mathf.Lerp(0.11f, 0.022f, Mathf.Pow(t, 0.75f)) * Mathf.Lerp(1.35f, 1f, ext);
            _radii.Add(r);
            _colors.Add(_pal.Body);
        }
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, bodyUp, 7);

        if (_feathersPerWing <= 0)
        {
            return;
        }
        // 整臂方向：羽毛切线与它空间混合——链段急弯（下拍中段）时扇面仍整体朝一个方向
        // 展开，不随局部切线四散（≙ RW 扇面由 sweep 主导的连贯观感）。
        Vector3 armDir = wing.Segments[segCount - 1].LerpPos(alpha) - anchorPos;
        armDir = armDir.LengthSquared() > 1e-8f ? armDir.Normalized() : forward;
        for (int k = 0; k < _feathersPerWing; k++)
        {
            // 前密后疏链位（≙ wingPosition 几何级数/sqrt 混合，取 sqrt）。
            float x = Mathf.Sqrt((k + 0.5f) / _feathersPerWing);
            float chainPos = x * (segCount - 1);
            int i = Mathf.Clamp((int)chainPos, 0, segCount - 2);
            float frac = chainPos - i;
            Vector3 a = wing.Segments[i].LerpPos(alpha);
            Vector3 b = wing.Segments[i + 1].LerpPos(alpha);
            Vector3 rootP = a.Lerp(b, frac);
            Vector3 tangent = b - a;
            tangent = tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : forward;
            tangent = (tangent + armDir).Normalized(); // 局部切线 ⊕ 整臂方向各半

            // 翼面内后缘向（forward 去掉沿链分量取反）。
            Vector3 trailing = -(forward - tangent * forward.Dot(tangent));
            trailing = trailing.LengthSquared() > 1e-8f ? trailing.Normalized() : -forward;
            // 后掠：根近垂直后缘、尖强贴链（≙ Lerp(-0.5, 4, x) 展开分支的收缩版）。
            float sweep = Mathf.Lerp(0.2f, 2.5f, x) * ext + (1f - ext) * 0.8f;
            Vector3 dir = (trailing + tangent * sweep).Normalized();

            // 轮廓 × 展开度 × 个体抖动（哈希，非运行时随机）。轮廓设下限：短羽也要
            // 与邻羽互叠，扇面才连续（第一轮实拍的稀疏断续教训）。
            float jitter = ((k * 2654435761u) & 255u) / 255f;
            float contour = Mathf.Pow(
                Mathf.Sin(Mathf.Clamp((x + 0.1f) / 1.1f, 0f, 1f) * Mathf.Pi), 0.7f);
            contour = Mathf.Max(contour, 0.55f);
            float length = contour * Mathf.Lerp(0.55f, 1.7f, ext) * Mathf.Lerp(0.85f, 1.05f, jitter);
            if (length < 0.05f)
            {
                continue;
            }
            float halfWidth = Mathf.Lerp(0.075f, 0.13f, contour) * Mathf.Lerp(0.55f, 1f, ext);

            // 刀片横向 = 翼面法线 × 羽向（刀片躺在翼面内，俯视可见宽度）。
            Vector3 planeNormal = tangent.Cross(trailing);
            planeNormal = planeNormal.LengthSquared() > 1e-8f ? planeNormal.Normalized() : bodyUp;
            Vector3 widthDir = planeNormal.Cross(dir);
            widthDir = widthDir.LengthSquared() > 1e-8f ? widthDir.Normalized() : tangent;

            // 逐羽滞后（≙ VultureFeather 弹簧低通）：方向/长度都追目标而非瞬时取值，
            // 拍动下行程链段急弯时扇面整体平滑跟随，不再散开。
            Vector3[] dirs = _featherDirs[wingIndex];
            float[] lens = _featherLens[wingIndex];
            float lag = 1f - Mathf.Exp(-7f * dt);
            dirs[k] = dirs[k].LengthSquared() < 1e-8f
                ? dir
                : (dirs[k] + (dir - dirs[k]) * lag).Normalized();
            lens[k] = lens[k] <= 0f ? length : Mathf.Lerp(lens[k], length, lag);
            dir = dirs[k];
            length = lens[k];

            // 根黑梢亮（参考图羽毛渐变；羽梢亮度随链位轻微增强 ≙ ColorB 向翼尖偏移）。
            // 根部沿羽向回收一点，扎进臂管内（≙ Daddy 黑根熔接：接缝藏进体内）。
            Color tipColor = _pal.Body.Lerp(_pal.FeatherTip, 0.45f + 0.55f * x);
            Vector3 tuckedRoot = rootP - dir * 0.06f;
            _tube.AddBlade(tuckedRoot, tuckedRoot + dir * (length + 0.06f), widthDir, halfWidth,
                0.42f, _pal.Body, tipColor);
        }
    }
}
