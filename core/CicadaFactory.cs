using System;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// Cicada 出生参数表与装配器。Light/Dark 的差异全部在参数里；
/// <see cref="CicadaLocomotionController"/> 不读取预设名，也不含物种分支。
/// </summary>
public static class CicadaFactory
{
    /// <summary>轻型、暖色、拍翼较快的基准 Cicada。</summary>
    public static CicadaParams Light() => new();

    /// <summary>较重、较宽、拍翼略慢的深色 Cicada。</summary>
    public static CicadaParams Dark() => new()
    {
        Name = "dark",
        FrontRadius = 0.195f,
        RearRadius = 0.1825f,
        BodyMass = 0.68f,
        FrontLift = 0.0215f,
        RearLift = 0.032f,
        FrontSteer = 0.025f,
        RearSteer = 0.015f,
        MaxFlightSpeed = 0.165f,
        WingLength = 0.88f,
        WingPeriodTicks = 6.25f,
        TentacleLength = 0.64f,
        BodyColor = new Vector3(0.12f, 0.13f, 0.17f),
        AccentColor = new Vector3(0.45f, 0.20f, 0.55f),
        WingColor = new Vector3(0.38f, 0.32f, 0.55f),
        InitialFlapPhase = 0.43f,
    };

    public static CicadaParams[] AllPresets() => new[] { Light(), Dark() };

    /// <summary>按名取预设；未知名回落 light，内核本身不记录日志。</summary>
    public static CicadaParams ByName(string? name)
    {
        foreach (CicadaParams preset in AllPresets())
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }
        return Light();
    }

    public static CicadaLocomotionController CreateController(
        Vector3 origin,
        Vector3 forward,
        CicadaParams parameters)
    {
        CicadaParams p = parameters.Snapshot();
        p.BodyMass = Mathf.Max(0.01f, p.BodyMass);
        p.BodyLength = Mathf.Max(0.05f, p.BodyLength);
        Vector3 bodyForward = forward.LengthSquared() > 1e-10f
            ? forward.Normalized()
            : Vector3.Forward;
        float halfLength = p.BodyLength * 0.5f;

        var front = new BodyChunk(
            origin + bodyForward * halfLength,
            p.FrontRadius,
            p.BodyMass * 0.48f);
        var rear = new BodyChunk(
            origin - bodyForward * halfLength,
            p.RearRadius,
            p.BodyMass * 0.52f);
        var body = new Body
        {
            AirFriction = 1f,
            SurfaceFriction = 0.55f,
            ConstraintIterations = 4,
        };
        body.Chunks.Add(front);
        body.Chunks.Add(rear);
        body.Connections.Add(new ChunkConnection(front, rear, p.BodyLength, 0.5f)
        {
            ConstraintMode = ChunkConnection.Mode.Rigid,
            Elasticity = 0.12f,
        });

        // 连接构造已互绑；这里重申方向语义，避免后续扩展装配顺序改变身体朝向。
        front.RotationChunk = rear;
        rear.RotationChunk = front;
        return new CicadaLocomotionController(body, front, rear, p, bodyForward);
    }
}
