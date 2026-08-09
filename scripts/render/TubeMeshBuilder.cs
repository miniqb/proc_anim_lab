using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.Render;

/// <summary>
/// 扫管网格构建器：管站序列 → 平行传输 frame → 截面环 → 三角形，每渲染帧全量重发
/// （≙ RW TriangleMesh 每帧写顶点；单生物几百顶点，CPU 重建可忽略——渲染研究 §3.1）。
/// 一个 builder = 一个 ImmediateMesh = 一个 unshaded 平色材质：整只生物的管件（身体/腿/
/// 装饰鳍）都发进同一网格，逐顶点色承载全部配色（平色融合 ≙ §1.5，内部拼缝因同色不可见）。
/// 平行传输（把上一站的 up 投影到新切线的法平面）杀掉逐段独立取法线的 roll 突跳——
/// ≙ CLAUDE.md「3D 朝向边界」与 RW 蜈蚣三控制 roll 沿链 Slerp 的意图。
/// 微量 CPU 侧"包裹漫反射"烘进顶点色（保平色抽象感的同时给一点体积读数），量可调可归零。
/// </summary>
internal sealed class TubeMeshBuilder
{
    private ImmediateMesh? _mesh;
    private MeshInstance3D? _node;

    private readonly Vector3 _keyLightDir = new Vector3(0.35f, 0.85f, 0.4f).Normalized();
    private const float ShadeAmount = 0.22f; // 0 = 纯平色；只影响顶点色烘焙，不引入实时光照

    private const int MaxRing = 16;
    private readonly Vector3[] _prevRing = new Vector3[MaxRing];
    private readonly Vector3[] _prevNormals = new Vector3[MaxRing];
    private readonly Vector3[] _curRing = new Vector3[MaxRing];
    private readonly Vector3[] _curNormals = new Vector3[MaxRing];

    /// <summary>srgbVertexColors=true 时顶点色按 sRGB 语义解读（与 AlbedoColor 同一空间）：
    /// 管件顶点色与球体材质写同一数值 → 屏幕同色，根部融合才逐位成立（Daddy 场景带
    /// tonemap 环境时线性误读会把剪影黑抬亮 4 倍）。默认 false 保持既有三物种已调校观感
    /// ——它们的调色在线性解读下定型，翻转需整体重调，记入渲染研究遗留清单。</summary>
    public void Build(Node3D parent, bool srgbVertexColors = false)
    {
        _mesh = new ImmediateMesh();
        _node = new MeshInstance3D
        {
            Mesh = _mesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = srgbVertexColors,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        parent.AddChild(_node);
    }

    public void Clear()
    {
        _node?.QueueFree();
        _node = null;
        _mesh = null;
    }

    public void SetVisible(bool visible)
    {
        if (_node is not null)
        {
            _node.Visible = visible;
        }
    }

    // ImmediateMesh 不允许零顶点 surface（SurfaceEnd 会报错），所以 SurfaceBegin
    // 推迟到本帧第一次 AddTube；空帧（如枪线消隐后）只清面，合法且画面为空。
    public void BeginFrame()
    {
        if (_mesh is null)
        {
            return;
        }
        _mesh.ClearSurfaces();
        _frameOpen = true;
    }

    public void EndFrame()
    {
        if (_mesh is null || !_frameOpen)
        {
            return;
        }
        _frameOpen = false;
        if (_hasSurface)
        {
            _mesh.SurfaceEnd();
            _hasSurface = false;
        }
    }

    private bool _frameOpen;
    private bool _hasSurface;

    /// <summary>沿管站序列扫掠 ringVerts 边截面。upHint 是 frame 种子（通常 SupportNormal
    /// 的低通），只影响第一站；后续站由平行传输接管。两端各盖一个圆锥端帽。</summary>
    public void AddTube(List<TubeStation> stations, Vector3 upHint, int ringVerts = 10)
    {
        if (_mesh is null || stations.Count < 2)
        {
            return;
        }
        ringVerts = Math.Min(ringVerts, MaxRing);

        // 首站 frame：切线 + upHint 正交化；退化时逐级回退。
        Vector3 tangent = Tangent(stations, 0);
        Vector3 up = upHint - tangent * upHint.Dot(tangent);
        if (up.LengthSquared() < 1e-8f)
        {
            up = tangent.Cross(Vector3.Right);
            if (up.LengthSquared() < 1e-8f)
            {
                up = tangent.Cross(Vector3.Up);
            }
        }
        up = up.Normalized();

        for (int i = 0; i < stations.Count; i++)
        {
            if (i > 0)
            {
                tangent = Tangent(stations, i);
                // 平行传输：旧 up 投影到新切线的法平面（小角度下即最小旋转传输）。
                Vector3 carried = up - tangent * up.Dot(tangent);
                if (carried.LengthSquared() > 1e-8f)
                {
                    up = carried.Normalized();
                }
            }
            Vector3 side = tangent.Cross(up).Normalized();

            TubeStation st = stations[i];
            for (int j = 0; j < ringVerts; j++)
            {
                float ang = Mathf.Tau * j / ringVerts;
                Vector3 normal = side * Mathf.Cos(ang) + up * Mathf.Sin(ang);
                _curNormals[j] = normal;
                _curRing[j] = st.Pos + normal * st.Radius;
            }

            if (i == 0)
            {
                // 起始端帽：极点沿 -切线外推，扇面封口。
                EmitCap(st, -tangent, ringVerts, reverse: true);
            }
            else
            {
                TubeStation prev = stations[i - 1];
                for (int j = 0; j < ringVerts; j++)
                {
                    int j1 = (j + 1) % ringVerts;
                    EmitVertex(prev.Color, _prevNormals[j], _prevRing[j]);
                    EmitVertex(prev.Color, _prevNormals[j1], _prevRing[j1]);
                    EmitVertex(st.Color, _curNormals[j1], _curRing[j1]);

                    EmitVertex(prev.Color, _prevNormals[j], _prevRing[j]);
                    EmitVertex(st.Color, _curNormals[j1], _curRing[j1]);
                    EmitVertex(st.Color, _curNormals[j], _curRing[j]);
                }
            }
            Array.Copy(_curRing, _prevRing, ringVerts);
            Array.Copy(_curNormals, _prevNormals, ringVerts);
        }
        EmitCap(stations[^1], Tangent(stations, stations.Count - 1), ringVerts, reverse: false);
    }

    /// <summary>羽毛刀片：菱形双三角（根点—两侧中点—尖点），根→尖逐顶点渐变
    /// （≙ VultureFeather 单 sprite 拉伸 + 羽色梯度；双面材质下两枚三角形即可）。</summary>
    public void AddBlade(Vector3 root, Vector3 tip, Vector3 halfWidthDir, float halfWidth,
        float midFraction, Color rootColor, Color tipColor)
    {
        if (_mesh is null)
        {
            return;
        }
        Vector3 mid = root.Lerp(tip, midFraction);
        Vector3 mp = mid + halfWidthDir * halfWidth;
        Vector3 mm = mid - halfWidthDir * halfWidth;
        Color midColor = rootColor.Lerp(tipColor, midFraction);
        Vector3 n = (tip - root).Cross(halfWidthDir);
        n = n.LengthSquared() > 1e-10f ? n.Normalized() : Vector3.Up;
        EmitVertex(rootColor, n, root);
        EmitVertex(midColor, n, mp);
        EmitVertex(tipColor, n, tip);
        EmitVertex(rootColor, n, root);
        EmitVertex(tipColor, n, tip);
        EmitVertex(midColor, n, mm);
    }

    /// <summary>小瘤球（触手疣珠等）：一次细分的八面体球，32 三角。小尺寸下轮廓略带棱角
    /// 正合适——RW 的 Daddy 疣珠本来就是要把管轮廓搅成有机噪声（≙ DaddyGraphics 的
    /// Circle20 bump 离管缘散布）。</summary>
    public void AddKnob(Vector3 center, float radius, Color color)
    {
        if (_mesh is null)
        {
            return;
        }
        Vector3[] verts = KnobVerts;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 n = verts[i];
            EmitVertex(color, n, center + n * radius);
        }
    }

    private static Vector3[]? _knobVerts;

    private static Vector3[] KnobVerts => _knobVerts ??= BuildKnobVerts();

    private static Vector3[] BuildKnobVerts()
    {
        // 八面体 8 面 × 各细分 4 → 32 三角形，顶点归一化到单位球。
        Vector3[] axes = { Vector3.Right, Vector3.Up, Vector3.Back };
        var verts = new List<Vector3>(96);
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 a = axes[0] * sx;
                    Vector3 b = axes[1] * sy;
                    Vector3 c = axes[2] * sz;
                    // 卦限朝向修正：保持外向环绕（奇数个负号时翻转）。
                    if (sx * sy * sz < 0)
                    {
                        (b, c) = (c, b);
                    }
                    Vector3 ab = (a + b).Normalized();
                    Vector3 bc = (b + c).Normalized();
                    Vector3 ca = (c + a).Normalized();
                    verts.AddRange(new[] { a, ab, ca });
                    verts.AddRange(new[] { b, bc, ab });
                    verts.AddRange(new[] { c, ca, bc });
                    verts.AddRange(new[] { ab, bc, ca });
                }
            }
        }
        return verts.ToArray();
    }

    /// <summary>装饰鳍片（背脊刺等）：单三角形，双面材质下一枚即可。</summary>
    public void AddFin(Vector3 baseA, Vector3 baseB, Vector3 tip, Color color)
    {
        if (_mesh is null)
        {
            return;
        }
        Vector3 normal = (tip - baseA).Cross(baseB - baseA);
        normal = normal.LengthSquared() > 1e-10f ? normal.Normalized() : Vector3.Up;
        EmitVertex(color, normal, baseA);
        EmitVertex(color, normal, baseB);
        EmitVertex(color, normal, tip);
    }

    private void EmitCap(TubeStation st, Vector3 outward, int ringVerts, bool reverse)
    {
        Vector3 pole = st.Pos + outward * (st.Radius * 0.7f);
        for (int j = 0; j < ringVerts; j++)
        {
            int j1 = (j + 1) % ringVerts;
            // _curRing 此刻正是端站的环（起帽在环生成后立即调用，尾帽在循环收尾后调用）。
            EmitVertex(st.Color, outward, pole);
            if (reverse)
            {
                EmitVertex(st.Color, _curNormals[j1], _curRing[j1]);
                EmitVertex(st.Color, _curNormals[j], _curRing[j]);
            }
            else
            {
                EmitVertex(st.Color, _curNormals[j], _curRing[j]);
                EmitVertex(st.Color, _curNormals[j1], _curRing[j1]);
            }
        }
    }

    private void EmitVertex(Color color, Vector3 normal, Vector3 pos)
    {
        if (!_hasSurface)
        {
            _mesh!.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            _hasSurface = true;
        }
        // 包裹漫反射烘进顶点色：dot∈[-1,1] → 亮度 [1-Shade, 1]，保持平色抽象感的体积暗示。
        float wrap = 0.5f + 0.5f * normal.Dot(_keyLightDir);
        float shade = 1f - ShadeAmount + ShadeAmount * wrap;
        _mesh!.SurfaceSetColor(new Color(color.R * shade, color.G * shade, color.B * shade, color.A));
        _mesh.SurfaceSetNormal(normal);
        _mesh.SurfaceAddVertex(pos);
    }

    private static Vector3 Tangent(List<TubeStation> stations, int i)
    {
        Vector3 a = stations[Math.Max(0, i - 1)].Pos;
        Vector3 b = stations[Math.Min(stations.Count - 1, i + 1)].Pos;
        Vector3 d = b - a;
        // 中心差分（≙ RW 蜈蚣 normalize(prev-next)——朝向沿链平滑变化，弯折处不打折）。
        return d.LengthSquared() > 1e-10f ? d.Normalized() : Vector3.Right;
    }
}
