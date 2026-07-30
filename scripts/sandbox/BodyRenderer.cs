using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 白盒渲染：每 chunk 一个球 MeshInstance3D，连接线/腿线用 ImmediateMesh 每帧重画。
/// 脚球按步态状态换色：绿=抓稳（正推进身体）、橙=迈步找落点、灰蓝=摆动期。
/// 蜥蜴身体按重力开关换色：红=重力在拽（坠落/未站稳）、青=抓稳重力关（站立/攀爬）。
/// 秃鹫身体按翅膀模式组合换色：蓝紫=飞行（全翅 Flap）、青=栖息（有翅吸附）、红=翻滚
/// （既不飞也没抓着）；翅节球：浅蓝=拍动、橙=找抓点、绿=已吸附。
/// 羽毛 = 无状态线扇（每帧按翅链切线/展开度重算，纯视觉零物理态——RW 的滞后羽毛粒子
/// 系统是图形层增强，白盒阶段用线扇呈现「拍动的羽翼」观感即可）。
/// Draw(t) 用物理插值分数在 LastPos→Pos 之间取位——渲染永远比物理"晚"不到一个 tick。
/// </summary>
public sealed class BodyRenderer
{
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _spheres = new();
    private readonly List<(Limb Limb, MeshInstance3D Node)> _feet = new();
    private readonly List<(VultureWing.WingSegment Segment, VultureWing Wing, MeshInstance3D Node)> _wingNodes = new();
    private readonly List<(ISandboxAppendageView Appendage, MeshInstance3D Node)> _otherFeet = new();
    private readonly List<ChunkConnection> _connections = new();
    private readonly List<Limb> _limbs = new();
    private readonly List<VultureWing> _wings = new();
    private readonly List<ISandboxAppendageView> _otherAppendages = new();
    private LizardLocomotionController? _lizardController;
    private VultureFlightController? _vultureController;
    private int _feathersPerWing;
    private Func<bool>? _isSupported;
    private ImmediateMesh? _lineMesh;
    private MeshInstance3D? _lineNode;

    private StandardMaterial3D _chunkFalling = null!;
    private StandardMaterial3D _chunkFooted = null!;
    private StandardMaterial3D _chunkFlying = null!;
    private StandardMaterial3D _footGrip = null!;
    private StandardMaterial3D _footReach = null!;
    private StandardMaterial3D _footSwing = null!;

    public void Build(Node3D parent, IReadOnlyList<Body> bodies, IReadOnlyList<Limb>? limbs = null,
        LizardLocomotionController? controller = null, IReadOnlyList<VultureWing>? wings = null,
        VultureFlightController? vultureController = null, int feathersPerWing = 0)
    {
        _lizardController = controller;
        _vultureController = vultureController;
        _feathersPerWing = feathersPerWing;
        _chunkFalling = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) };
        _chunkFooted = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.65f, 0.7f) };
        _chunkFlying = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.5f, 0.9f) };
        foreach (Body body in bodies)
        {
            foreach (BodyChunk chunk in body.Chunks)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = chunk.Radius, Height = chunk.Radius * 2f },
                    MaterialOverride = _chunkFalling,
                    // 物理坐标是世界系；TopLevel 让 Position 直写世界——父节点带变换也不错位
                    TopLevel = true,
                };
                parent.AddChild(node);
                _spheres.Add((chunk, node));
            }
            foreach (ChunkConnection conn in body.Connections)
            {
                if (!conn.SoftOnly) // 防折叠支柱是姿态弹簧不是"骨头"，不画
                {
                    _connections.Add(conn);
                }
            }
        }

        _footGrip = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.8f, 0.35f) };
        _footReach = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.6f, 0.2f) };
        _footSwing = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.75f) };
        if (limbs is not null)
        {
            foreach (Limb limb in limbs)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = limb.Radius, Height = limb.Radius * 2f },
                    MaterialOverride = _footSwing,
                    TopLevel = true,
                };
                parent.AddChild(node);
                _feet.Add((limb, node));
                _limbs.Add(limb);
            }
        }

        if (wings is not null)
        {
            foreach (VultureWing wing in wings)
            {
                _wings.Add(wing);
                foreach (VultureWing.WingSegment seg in wing.Segments)
                {
                    var node = new MeshInstance3D
                    {
                        Mesh = new SphereMesh { Radius = seg.Radius, Height = seg.Radius * 2f },
                        MaterialOverride = _footSwing,
                        TopLevel = true,
                    };
                    parent.AddChild(node);
                    _wingNodes.Add((seg, wing, node));
                }
            }
        }

        _lineMesh = new ImmediateMesh();
        _lineNode = new MeshInstance3D
        {
            Mesh = _lineMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.95f, 0.9f, 0.4f),
            },
            TopLevel = true, // 顶点直接写世界坐标，节点必须钉在世界原点
        };
        parent.AddChild(_lineNode);
    }

    /// <summary>
    /// 并列控制器的白盒渲染入口。附肢通过沙盒只读视图接入，不要求核心腿类型继承
    /// <see cref="Limb"/>；isSupported 只决定身体颜色，不参与运动。
    /// </summary>
    internal void Build(Node3D parent, IReadOnlyList<Body> bodies,
        IReadOnlyList<ISandboxAppendageView> appendages, Func<bool> isSupported)
    {
        _isSupported = isSupported;
        _chunkFalling = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) };
        _chunkFooted = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.65f, 0.7f) };
        _footGrip = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.8f, 0.35f) };
        _footReach = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.6f, 0.2f) };
        _footSwing = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.75f) };

        BuildBodies(parent, bodies);
        foreach (ISandboxAppendageView appendage in appendages)
        {
            var node = new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = appendage.Radius,
                    Height = appendage.Radius * 2f,
                },
                MaterialOverride = _footSwing,
                TopLevel = true,
            };
            parent.AddChild(node);
            _otherFeet.Add((appendage, node));
            _otherAppendages.Add(appendage);
        }
        BuildLines(parent);
    }

    private void BuildBodies(Node3D parent, IReadOnlyList<Body> bodies)
    {
        foreach (Body body in bodies)
        {
            foreach (BodyChunk chunk in body.Chunks)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = chunk.Radius, Height = chunk.Radius * 2f },
                    MaterialOverride = _chunkFalling,
                    TopLevel = true,
                };
                parent.AddChild(node);
                _spheres.Add((chunk, node));
            }
            foreach (ChunkConnection conn in body.Connections)
            {
                if (!conn.SoftOnly)
                {
                    _connections.Add(conn);
                }
            }
        }
    }

    private void BuildLines(Node3D parent)
    {
        _lineMesh = new ImmediateMesh();
        _lineNode = new MeshInstance3D
        {
            Mesh = _lineMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.95f, 0.9f, 0.4f),
            },
            TopLevel = true,
        };
        parent.AddChild(_lineNode);
    }

    /// <summary>拆掉本次 Build 创建的全部节点（品种切换重生用），之后可再次 Build。</summary>
    public void Clear()
    {
        foreach ((_, MeshInstance3D node) in _spheres)
        {
            node.QueueFree();
        }
        foreach ((_, MeshInstance3D node) in _feet)
        {
            node.QueueFree();
        }
        foreach ((_, _, MeshInstance3D node) in _wingNodes)
        {
            node.QueueFree();
        }
        foreach ((_, MeshInstance3D node) in _otherFeet)
        {
            node.QueueFree();
        }
        _lineNode?.QueueFree();
        _lineNode = null;
        _lineMesh = null;
        _spheres.Clear();
        _feet.Clear();
        _wingNodes.Clear();
        _otherFeet.Clear();
        _connections.Clear();
        _limbs.Clear();
        _wings.Clear();
        _otherAppendages.Clear();
        _lizardController = null;
        _vultureController = null;
        _isSupported = null;
    }

    public void Draw(float t)
    {
        StandardMaterial3D chunkMat;
        if (_vultureController is { } vulture)
        {
            chunkMat = vulture.AirBorne ? _chunkFlying
                : vulture.AnyWingAttached ? _chunkFooted
                : _chunkFalling;
        }
        else
        {
            bool supported = _lizardController is { ApplyGravity: false } || (_isSupported?.Invoke() ?? false);
            chunkMat = supported ? _chunkFooted : _chunkFalling;
        }
        foreach ((BodyChunk chunk, MeshInstance3D node) in _spheres)
        {
            node.Position = chunk.LerpPos(t);
            node.MaterialOverride = chunkMat;
        }
        foreach ((Limb limb, MeshInstance3D node) in _feet)
        {
            node.Position = limb.LerpPos(t);
            node.MaterialOverride = limb.Gripping ? _footGrip
                : limb.ReachingForTerrain && !limb.IdlePose ? _footReach
                : _footSwing;
        }
        foreach ((VultureWing.WingSegment seg, VultureWing wing, MeshInstance3D node) in _wingNodes)
        {
            node.Position = seg.LerpPos(t);
            node.MaterialOverride = wing.Mode == VultureWing.WingMode.Flap ? _footSwing
                : wing.Attached ? _footGrip
                : _footReach;
        }
        foreach ((ISandboxAppendageView appendage, MeshInstance3D node) in _otherFeet)
        {
            node.Position = appendage.LastPos.Lerp(appendage.Pos, t);
            node.MaterialOverride = appendage.Phase switch
            {
                SandboxAppendagePhase.Grip => _footGrip,
                SandboxAppendagePhase.Reach => _footReach,
                _ => _footSwing,
            };
        }

        if (_lineMesh is null)
        {
            return;
        }
        _lineMesh.ClearSurfaces();
        if (_connections.Count == 0 && _limbs.Count == 0 && _wings.Count == 0 && _otherAppendages.Count == 0)
        {
            return;
        }
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (ChunkConnection conn in _connections)
        {
            _lineMesh.SurfaceAddVertex(conn.A.LerpPos(t));
            _lineMesh.SurfaceAddVertex(conn.B.LerpPos(t));
        }
        foreach (Limb limb in _limbs)
        {
            _lineMesh.SurfaceAddVertex(limb.Anchor.LerpPos(t));
            _lineMesh.SurfaceAddVertex(limb.LerpPos(t));
        }
        foreach (VultureWing wing in _wings)
        {
            DrawWingLines(wing, t);
        }
        foreach (ISandboxAppendageView appendage in _otherAppendages)
        {
            _lineMesh.SurfaceAddVertex(appendage.AnchorLastPos.Lerp(appendage.AnchorPos, t));
            _lineMesh.SurfaceAddVertex(appendage.LastPos.Lerp(appendage.Pos, t));
        }
        _lineMesh.SurfaceEnd();
    }

    /// <summary>翅链骨线（锚→节0→…→尖）+ 无状态羽毛线扇。羽毛每帧从翅链几何重算：
    /// 根部钉在链上插值点（≙ RW wingPosition 前密后疏排布的简化），方向 = 翼面内的
    /// 后掠向（尾向分量随链位增大，≙ RW num 从根 −0.5 到尖 4 的后掠系数），长度按
    /// 翼型轮廓 × 展开度（收拢约为展开一半，收拢时羽毛倒伏贴链）。纯视觉零状态——
    /// RW 的每羽滞后粒子留给正式图形层。</summary>
    private void DrawWingLines(VultureWing wing, float t)
    {
        Vector3 anchorPos = wing.Anchor.LerpPos(t);
        Vector3 prev = anchorPos;
        foreach (VultureWing.WingSegment seg in wing.Segments)
        {
            Vector3 pos = seg.LerpPos(t);
            _lineMesh!.SurfaceAddVertex(prev);
            _lineMesh.SurfaceAddVertex(pos);
            prev = pos;
        }

        if (_feathersPerWing <= 0 || _vultureController is not { } vulture)
        {
            return;
        }
        Vector3 forward = vulture.FrontSpine.LerpPos(t) - vulture.RearSpine.LerpPos(t);
        forward = forward.LengthSquared() < 1e-8f ? Vector3.Right : forward.Normalized();
        float ext = wing.FlyingMode;
        int count = wing.Segments.Length;

        for (int k = 0; k < _feathersPerWing; k++)
        {
            // 前密后疏的链位分布（≙ RW 几何级数与 sqrt 的混合，取 sqrt 简化）。
            float x = Mathf.Sqrt((k + 0.5f) / _feathersPerWing);
            float chainPos = x * (count - 1);
            int i = Mathf.Clamp((int)chainPos, 0, count - 2);
            float frac = chainPos - i;
            Vector3 a = wing.Segments[i].LerpPos(t);
            Vector3 b = wing.Segments[i + 1].LerpPos(t);
            Vector3 root = a.Lerp(b, frac);
            Vector3 tangent = b - a;
            tangent = tangent.LengthSquared() < 1e-8f ? Vector3.Up : tangent.Normalized();

            // 翼面内的尾向（forward 去掉沿链分量）：羽毛从链后缘伸出。
            Vector3 trailing = -(forward - tangent * forward.Dot(tangent));
            trailing = trailing.LengthSquared() < 1e-8f ? -forward : trailing.Normalized();
            // 后掠：根部近垂直后缘，越往尖越贴链向（≙ RW Lerp(-0.5, 4, x) 展开分支）。
            float sweep = Mathf.Lerp(0.2f, 2.5f, x) * ext + (1f - ext) * 0.6f;
            Vector3 dir = (trailing + tangent * sweep).Normalized();

            // 翼型轮廓 × 展开度：中段最长；收拢时约减半（≙ contracted ≈ extended/2）。
            float rand = ((k * 2654435761u) & 255u) / 255f;
            float contour = Mathf.Pow(Mathf.Sin(Mathf.Clamp((x + 0.1f) / 1.1f, 0f, 1f) * Mathf.Pi), 0.7f);
            float length = contour * Mathf.Lerp(0.9f, 1.75f, ext) * Mathf.Lerp(0.85f, 1.05f, rand);

            _lineMesh!.SurfaceAddVertex(root);
            _lineMesh.SurfaceAddVertex(root + dir * length);
        }
    }
}
