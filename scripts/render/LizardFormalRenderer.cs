using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Lizard;

namespace ProcAnimLab.Render;

/// <summary>蜥蜴渲染调色板（≙ RW ApplyPalette：整体剪影黑 + 品种识别色只出现在头/足/梯度端部）。
/// 品种映射对齐 RW 血统：default≙粉蜥、heavy≙绿蜥、sprinter≙黄蜥；hexapod 是本项目原创拓扑，取青。</summary>
internal readonly struct LizardRenderPalette
{
    public readonly Color Body;      // 剪影黑（≙ palette.blackColor，带一点冷蓝）
    public readonly Color Effect;    // 品种识别色（≙ effectColor）
    public readonly Color EffectDim; // 头部脉冲的暗端

    public LizardRenderPalette(Color body, Color effect)
    {
        Body = body;
        Effect = effect;
        EffectDim = body.Lerp(effect, 0.45f);
    }

    private static readonly Color SilhouetteBlack = new(0.051f, 0.055f, 0.086f);

    public static LizardRenderPalette ForBreed(string breedName) => breedName switch
    {
        "heavy" => new LizardRenderPalette(SilhouetteBlack, new Color(0.24f, 0.91f, 0.27f)),
        "sprinter" => new LizardRenderPalette(SilhouetteBlack, new Color(1.0f, 0.87f, 0.24f)),
        "hexapod" => new LizardRenderPalette(SilhouetteBlack, new Color(0.33f, 0.78f, 1.0f)),
        _ => new LizardRenderPalette(SilhouetteBlack, new Color(0.95f, 0.38f, 0.60f)),
    };
}

/// <summary>
/// 蜥蜴正式渲染器（技术验证件一号，≙ LizardGraphics 的 3D 移植）。
/// 身体+尾巴 = **一条**连续扫管：控制点 [头前伸点, 脊柱 chunk…, 尾链 chunk…, 尾尖外推点]，
/// Catmull-Rom 密化（≙ BodyPosition 的 Slerp 中点插值，样条给的是同一件事）；半径剖面与
/// 物理半径解耦、呼吸只在低速时鼓动前胸（≙ pow(1-runSpeed,2) 门控）；尾尖逐顶点渐变到
/// 识别色（≙ BodyColor 梯度）。腿 = 足端粒子 + 渲染期两骨 IK（余弦钳制、bend pole 低通、
/// 前后腿弯向相反）扫管，足端一节识别色（参考图的亮色手足）。头 = 刚性椭球 + 双眼点，
/// 识别色慢脉冲（≙ HeadColor 眨眼正弦）。背脊刺 = 沿管站的双面鳍片（≙ SpineSpikes 装饰，
/// 尺寸包络 sin(pow(s,skew)·π)）。全部状态渲染侧私有，对内核零写入。
/// </summary>
internal sealed class LizardFormalRenderer : IFormalRenderer
{
    private readonly LizardLocomotionController _c;
    private readonly LizardRenderPalette _pal;
    private readonly List<BodyChunk> _spine = new();
    private readonly List<BodyChunk> _tail = new();

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private MeshInstance3D? _head;
    private MeshInstance3D? _eyeL;
    private MeshInstance3D? _eyeR;
    private StandardMaterial3D? _headMat;

    // —— 渲染侧化妆状态（不写回内核、不进哈希）——
    private Vector3 _stableUp = Vector3.Up;
    private float _breath;
    private float _headPulse;
    private Vector3[] _poles = System.Array.Empty<Vector3>();

    // 复用缓冲：控制点/剖面/管站，零逐帧分配增长。
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly List<TubeStation> _stations = new();
    private readonly List<TubeStation> _legStations = new();

    private const int SpikeCount = 13;

    public LizardFormalRenderer(LizardLocomotionController controller, string breedName)
    {
        _c = controller;
        _pal = LizardRenderPalette.ForBreed(breedName);
        // Body.Chunks 布局 = 脊柱（头…髋）后接尾链（BodyFactory 装配序）；用髋索引切分。
        int hipsIdx = controller.Body.Chunks.IndexOf(controller.Hips);
        for (int i = 0; i < controller.Body.Chunks.Count; i++)
        {
            (i <= hipsIdx ? _spine : _tail).Add(controller.Body.Chunks[i]);
        }
        _poles = new Vector3[controller.Limbs.Count];
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true }; // 世界系直写，父节点变换不影响
        parent.AddChild(_root);
        _tube.Build(_root);

        _headMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _pal.Effect,
        };
        _head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 },
            MaterialOverride = _headMat,
        };
        _root.AddChild(_head);

        var eyeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.02f, 0.02f, 0.04f),
        };
        _eyeL = MakeEye(eyeMat);
        _eyeR = MakeEye(eyeMat);
    }

    private MeshInstance3D MakeEye(StandardMaterial3D mat)
    {
        var eye = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = mat,
        };
        _root!.AddChild(eye);
        return eye;
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _head = null;
        _eyeL = null;
        _eyeR = null;
        _headMat = null;
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

        // —— 帧基础量 ——
        Vector3 headPos = _spine[0].LerpPos(alpha);
        Vector3 hipsPos = _spine[^1].LerpPos(alpha);
        Vector3 bodyAxis = headPos - hipsPos;
        bodyAxis = bodyAxis.LengthSquared() > 1e-8f ? bodyAxis.Normalized() : Vector3.Right;

        // 稳定 up：SupportNormal 低通（forward 近共线时沿用旧值——CLAUDE.md 3D 朝向边界）。
        Vector3 upTarget = _c.SupportNormal;
        if (Mathf.Abs(upTarget.Dot(bodyAxis)) < 0.985f)
        {
            float k = 1f - Mathf.Exp(-6f * dt);
            _stableUp = (_stableUp + (upTarget - _stableUp) * k).Normalized();
        }

        float run = Mathf.Clamp(_c.RunSpeed, 0f, 1f);
        _breath += dt * 0.55f;
        _headPulse += dt * (0.45f + run * 0.5f);
        // 呼吸只在停驶时可见（≙ RW pow(1-runSpeed,2) 门控），鼓动量向头端衰减。
        float breathPulse = (Mathf.Sin(_breath * Mathf.Tau) + 1f) * 0.5f
            * Mathf.Pow(1f - run, 2f);

        // —— 身体+尾巴一条管（≙ 身尾条带同点同宽对接 + BodyColor 单一 s 参数化）——
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();

        Vector3 neckDir = _spine.Count > 1 ? headPos - _spine[1].LerpPos(alpha) : bodyAxis;
        neckDir = neckDir.LengthSquared() > 1e-8f ? neckDir.Normalized() : bodyAxis;
        float headR = _spine[0].Radius;

        // 头前伸控制点（≙ RW 头部粒子把条带延到嘴前；头椭球会盖住这段的端帽）。
        _pts.Add(headPos + neckDir * (headR * 0.8f));
        _radii.Add(headR * 0.5f);
        _colors.Add(_pal.Body);

        // 显示半径与物理半径解耦（≙ BodyChunkDisplayRad，渲染研究 §1.4）：物理球为碰撞
        // 相切大幅重叠，直接照画会读成一团球——收窄成修长躯干，髋部再收向尾基半径。
        for (int i = 0; i < _spine.Count; i++)
        {
            bool hips = i == _spine.Count - 1;
            float display = _spine[i].Radius * (hips ? 0.72f : 0.85f);
            float breathHere = 1f + breathPulse * 0.07f * (1f - i / (float)_spine.Count);
            _pts.Add(_spine[i].LerpPos(alpha));
            _radii.Add(display * breathHere);
            _colors.Add(_pal.Body);
        }
        int tailCount = _tail.Count;
        for (int i = 0; i < tailCount; i++)
        {
            float f = tailCount <= 1 ? 1f : i / (float)(tailCount - 1);
            _pts.Add(_tail[i].LerpPos(alpha));
            _radii.Add(_tail[i].Radius * 1.15f);
            // 尾梢渐变到识别色（≙ BodyColor：pow 曲线、只染最后一段）。
            float tint = Mathf.Pow(Mathf.Clamp((f - 0.45f) / 0.55f, 0f, 1f), 1.8f) * 0.5f;
            _colors.Add(_pal.Body.Lerp(_pal.Effect, tint));
        }
        if (tailCount > 0)
        {
            // 尾尖外推点收锥（≙ pointyTip + 蜈蚣 ±10px 虚拟端点）。
            Vector3 tipDir = _tail[^1].LerpPos(alpha)
                - (tailCount > 1 ? _tail[^2].LerpPos(alpha) : hipsPos);
            tipDir = tipDir.LengthSquared() > 1e-8f ? tipDir.Normalized() : -bodyAxis;
            _pts.Add(_tail[^1].LerpPos(alpha) + tipDir * 0.09f);
            _radii.Add(0.004f);
            _colors.Add(_pal.Body.Lerp(_pal.Effect, 0.5f));
        }

        _tube.BeginFrame();
        SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
        _tube.AddTube(_stations, _stableUp, 10);

        DrawSpikes();
        DrawLegs(alpha, dt, bodyAxis);
        _tube.EndFrame();

        DrawHead(headPos, neckDir, headR);
    }

    /// <summary>背脊刺（≙ SpineSpikes：沿 SpinePosition 的 s 站位，尺寸包络 sin(pow(s,·)·π)，
    /// 挂在截面背侧站位——管站的传输 up 即背脊方向，随真实 roll 自动滑动）。</summary>
    private void DrawSpikes()
    {
        int count = _stations.Count;
        if (count < 8)
        {
            return;
        }
        Color spikeColor = _pal.Body.Lerp(_pal.Effect, 0.2f);
        // 与 AddTube 相同的 frame 推导（切线中心差分 + up 平行传输），保证刺贴着管背。
        Vector3 up = _stableUp;
        Vector3 prevTan = (_stations[1].Pos - _stations[0].Pos).Normalized();
        up = (up - prevTan * up.Dot(prevTan)).Normalized();

        int start = count / 8; // 跳过头前伸区
        for (int k = 0; k < SpikeCount; k++)
        {
            float s = (k + 0.5f) / SpikeCount;
            int idx = start + (int)(s * (count - 1 - start));
            idx = Mathf.Clamp(idx, 1, count - 2);
            // 逐站重放传输，避免刺与管的 up 漂移不同步（站数小，代价可忽略）。
            // 简化：从上一根刺的 up 续传到本站。
            Vector3 tan = (_stations[idx + 1].Pos - _stations[idx - 1].Pos);
            tan = tan.LengthSquared() > 1e-10f ? tan.Normalized() : prevTan;
            Vector3 carried = up - tan * up.Dot(tan);
            if (carried.LengthSquared() > 1e-8f)
            {
                up = carried.Normalized();
            }
            prevTan = tan;

            TubeStation st = _stations[idx];
            float envelope = Mathf.Sin(Mathf.Pow(s, 0.85f) * Mathf.Pi);
            float len = st.Radius * (0.25f + envelope * 0.5f);
            float halfW = st.Radius * 0.24f;
            Vector3 baseCenter = st.Pos + up * (st.Radius * 0.9f);
            // 后掠向尾（tangent 指向尾端）。
            _tube.AddFin(
                baseCenter - tan * halfW,
                baseCenter + tan * halfW,
                baseCenter + up * len + tan * (len * 0.55f),
                spikeColor);
        }
    }

    private void DrawLegs(float alpha, float dt, Vector3 bodyAxis)
    {
        var limbs = _c.Limbs;
        for (int i = 0; i < limbs.Count; i++)
        {
            Limb limb = limbs[i];
            Vector3 rootPos = limb.Anchor.LerpPos(alpha);
            Vector3 foot = limb.LerpPos(alpha);

            // bend pole：外侧 + 前腿肘向后 / 后腿膝向前（≙ RW 腿 atlas 编码的弯向），低通防突跳。
            Vector3 outward = bodyAxis.Cross(_stableUp);
            outward = outward.LengthSquared() > 1e-8f ? outward.Normalized() * limb.Side : Vector3.Right * limb.Side;
            bool front = limb.Anchor == _c.Head;
            Vector3 poleTarget = (outward + bodyAxis * (front ? -0.32f : 0.42f) - _stableUp * 0.18f).Normalized();
            _poles[i] = _poles[i].LengthSquared() < 1e-8f
                ? poleTarget
                : (_poles[i] + (poleTarget - _poles[i]) * (1f - Mathf.Exp(-10f * dt))).Normalized();

            float bone = limb.JointDist * 0.62f; // 两骨和 1.24×JointDist：配合 IK 钳制永不绷直
            Vector3 knee = TwoBoneIk.Solve(rootPos, foot, bone, bone, _poles[i]);

            float scale = limb.Radius / 0.06f; // FootRadius 基准反推 LimbSize
            _pts.Clear();
            _radii.Clear();
            _colors.Clear();
            _pts.Add(rootPos);
            _radii.Add(0.08f * scale);
            _colors.Add(_pal.Body);
            _pts.Add(knee);
            _radii.Add(0.052f * scale);
            _colors.Add(_pal.Body);
            // 踝控制点：把识别色渐变压到腿的最后一小段（≙ 参考图：腿身暗色、只有足端亮）。
            _pts.Add(knee.Lerp(foot, 0.62f));
            _radii.Add(0.045f * scale);
            _colors.Add(_pal.Body.Lerp(_pal.Effect, 0.18f));
            _pts.Add(foot);
            _radii.Add(0.042f * scale);
            _colors.Add(_pal.Body.Lerp(_pal.Effect, 0.8f));

            SplineSampler.Sample(_pts, _radii, _colors, 4, _legStations);
            _tube.AddTube(_legStations, _stableUp, 7);
        }
    }

    private void DrawHead(Vector3 headPos, Vector3 neckDir, float headR)
    {
        if (_head is null)
        {
            return;
        }
        // 头椭球基：forward=neckDir、up=稳定 up 正交化；近共线时 AddTube 同款回退。
        Vector3 up = _stableUp - neckDir * _stableUp.Dot(neckDir);
        if (up.LengthSquared() < 1e-8f)
        {
            up = neckDir.Cross(Vector3.Right);
            if (up.LengthSquared() < 1e-8f)
            {
                up = neckDir.Cross(Vector3.Up);
            }
        }
        up = up.Normalized();
        Vector3 side = up.Cross(neckDir).Normalized();

        // 列基 = (side, up, -forward)（Godot -Z 前向），逐列乘椭球缩放。
        var basis = new Basis(
            side * (headR * 0.88f),
            up * (headR * 0.72f),
            -neckDir * (headR * 1.32f));
        Vector3 center = headPos + neckDir * (headR * 0.42f);
        _head.Transform = new Transform3D(basis, center);

        // 头色慢脉冲（≙ HeadColor 眨眼正弦：黑↔识别色，兴奋加速——这里以 run 提速近似）。
        // 底色保持明亮（参考图：头是全身最亮的识别色块），脉冲只做轻微呼吸式明暗。
        float pulse = 0.5f + 0.5f * Mathf.Sin(_headPulse * Mathf.Tau * 0.35f);
        _headMat!.AlbedoColor = _pal.EffectDim.Lerp(_pal.Effect, 0.6f + 0.4f * pulse);

        float eyeR = headR * 0.24f;
        var eyeBasis = new Basis(
            Vector3.Right * eyeR, Vector3.Up * eyeR, Vector3.Back * eyeR);
        _eyeL!.Transform = new Transform3D(eyeBasis,
            center + neckDir * (headR * 0.62f) + side * (headR * 0.68f) + up * (headR * 0.18f));
        _eyeR!.Transform = new Transform3D(eyeBasis,
            center + neckDir * (headR * 0.62f) - side * (headR * 0.68f) + up * (headR * 0.18f));
    }
}
