using System.Collections.Generic;
using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 白盒渲染：每 chunk 一个球 MeshInstance3D，连接线/腿线用 ImmediateMesh 每帧重画。
/// 脚球按步态状态换色：绿=抓稳（正推进身体）、橙=迈步找落点、灰蓝=摆动期。
/// 身体按重力开关换色：红=重力在拽（坠落/未站稳）、青=抓稳重力关（站立/攀爬）。
/// Draw(t) 用物理插值分数在 LastPos→Pos 之间取位——渲染永远比物理"晚"不到一个 tick。
/// </summary>
public sealed class BodyRenderer
{
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _spheres = new();
    private readonly List<(Limb Limb, MeshInstance3D Node)> _feet = new();
    private readonly List<ChunkConnection> _connections = new();
    private readonly List<Limb> _limbs = new();
    private Walker? _walker;
    private ImmediateMesh? _lineMesh;
    private MeshInstance3D? _lineNode;

    private StandardMaterial3D _chunkFalling = null!;
    private StandardMaterial3D _chunkFooted = null!;
    private StandardMaterial3D _footGrip = null!;
    private StandardMaterial3D _footReach = null!;
    private StandardMaterial3D _footSwing = null!;

    public void Build(Node3D parent, IReadOnlyList<Body> bodies, IReadOnlyList<Limb>? limbs = null,
        Walker? walker = null)
    {
        _walker = walker;
        _chunkFalling = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) };
        _chunkFooted = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.65f, 0.7f) };
        foreach (Body body in bodies)
        {
            foreach (BodyChunk chunk in body.Chunks)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = chunk.Radius, Height = chunk.Radius * 2f },
                    MaterialOverride = _chunkFalling,
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

        if (limbs is not null)
        {
            _footGrip = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.8f, 0.35f) };
            _footReach = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.6f, 0.2f) };
            _footSwing = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.75f) };
            foreach (Limb limb in limbs)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = limb.Radius, Height = limb.Radius * 2f },
                    MaterialOverride = _footSwing,
                };
                parent.AddChild(node);
                _feet.Add((limb, node));
                _limbs.Add(limb);
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
        _lineNode?.QueueFree();
        _lineNode = null;
        _lineMesh = null;
        _spheres.Clear();
        _feet.Clear();
        _connections.Clear();
        _limbs.Clear();
        _walker = null;
    }

    public void Draw(float t)
    {
        StandardMaterial3D chunkMat = _walker is { ApplyGravity: false } ? _chunkFooted : _chunkFalling;
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

        if (_lineMesh is null)
        {
            return;
        }
        _lineMesh.ClearSurfaces();
        if (_connections.Count == 0 && _limbs.Count == 0)
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
        _lineMesh.SurfaceEnd();
    }
}
