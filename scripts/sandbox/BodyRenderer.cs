using System.Collections.Generic;
using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 白盒渲染：每 chunk 一个球 MeshInstance3D，连接线用 ImmediateMesh 每帧重画。
/// Draw(t) 用物理插值分数在 LastPos→Pos 之间取位——渲染永远比物理"晚"不到一个 tick。
/// </summary>
public sealed class BodyRenderer
{
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _spheres = new();
    private readonly List<ChunkConnection> _connections = new();
    private ImmediateMesh? _lineMesh;

    public void Build(Node3D parent, IReadOnlyList<Body> bodies)
    {
        var chunkMat = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) };
        foreach (Body body in bodies)
        {
            foreach (BodyChunk chunk in body.Chunks)
            {
                var node = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = chunk.Radius, Height = chunk.Radius * 2f },
                    MaterialOverride = chunkMat,
                };
                parent.AddChild(node);
                _spheres.Add((chunk, node));
            }
            _connections.AddRange(body.Connections);
        }

        _lineMesh = new ImmediateMesh();
        var lineNode = new MeshInstance3D
        {
            Mesh = _lineMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.95f, 0.9f, 0.4f),
            },
        };
        parent.AddChild(lineNode);
    }

    public void Draw(float t)
    {
        foreach ((BodyChunk chunk, MeshInstance3D node) in _spheres)
        {
            node.Position = chunk.LerpPos(t);
        }

        if (_lineMesh is null)
        {
            return;
        }
        _lineMesh.ClearSurfaces();
        if (_connections.Count == 0)
        {
            return;
        }
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (ChunkConnection conn in _connections)
        {
            _lineMesh.SurfaceAddVertex(conn.A.LerpPos(t));
            _lineMesh.SurfaceAddVertex(conn.B.LerpPos(t));
        }
        _lineMesh.SurfaceEnd();
    }
}
