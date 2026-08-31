using Godot;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 从 Daddy 沙盒提升共享（原 <c>DaddyLongLegsSandboxPlayer</c>，内容不变）。
/// 主仓 <c>FirstPersonPlayer</c> 的**运动规格镜像**：在迷宫里用人的尺寸、人的速度走一圈，
/// 直观感受 Daddy 在同一空间里的体量与快慢。只做「站着走」这一件事。
///
/// 逐项对齐主仓（真相源 <c>random-room-runtime/scripts/game/FirstPersonPlayer.cs</c>，
/// 改主仓规格后必须同步本文件）：胶囊 r0.35 / h1.7 且中心抬 0.85、相机眼高 1.55、
/// FOV 75、移速 5.0m/s、静步 ×0.55、跳跃初速 4.5m/s、鼠标灵敏度 0.0025、俯仰夹 ±1.35、
/// 无加速度（有输入直接赋速、无输入即刻归零）、重力取引擎默认——两仓都没覆盖
/// <c>default_gravity</c>，同为 9.8m/s²。
///
/// 刻意不镜像：武器/机瞄/后坐、手电 rig、脚步与噪音、楼梯助攻、梯子、血量、自动移动。
/// 那些是主仓玩法，与「体量和速度的直观感受」无关。
///
/// **碰撞层刻意错开**：玩家在层 2、只与层 1 的地形碰撞；内核地形查询适配器
/// （<c>RaycastTerrainQuery</c>）掩码恒为 1，因此玩家对生物**完全不可见**——不会被当成
/// 地形抓住，也不会污染任何观测量或残余穿透统计。生物侧没有碰撞体，人可以直接穿过它。
/// </summary>
public partial class ArenaFirstPersonPlayer : CharacterBody3D
{
    // ---- 主仓规格镜像 ----
    private const float MoveSpeed = 5.0f;
    private const float SneakSpeedScale = 0.55f;
    private const float MouseSensitivity = 0.0025f;
    private const float JumpVelocity = 4.5f;
    private const float PitchLimit = 1.35f;
    /// <summary>pitch + 震动叠加后的硬夹（弧度）：必须 &lt; π/2，不然俯仰贴夹角时震动
    /// 峰值能把镜头翻过竖直极点（画面瞬间倒置）。震动只被削顶，不影响 pitch 本值。</summary>
    private const float CameraPitchHardLimit = 1.5f;
    private const float CapsuleRadius = 0.35f;
    private const float CapsuleHeight = 1.7f;
    private const float CapsuleCenterY = 0.85f;
    private const float EyeHeight = 1.55f;
    private const float FieldOfView = 75.0f;

    /// <summary>玩家层 2 / 只碰层 1：地形查询掩码是 1，玩家因此对内核不可见。</summary>
    private const uint PlayerCollisionLayer = 2;
    private const uint TerrainCollisionLayer = 1;

    /// <summary>落点抬一点点，免得出生帧就嵌在楼板里。</summary>
    private const float SpawnClearance = 0.02f;

    private Camera3D _camera = null!;
    private MeshInstance3D _bodyMesh = null!;
    private OmniLight3D _fillLight = null!;
    private float _pitch;
    private Vector3 _shakeOffset;
    private Vector3 _shakeEuler;
    private Vector3 _externalVelocity;

    /// <summary>外部冲量衰减（每物理步保留系数）：0.85^n 几何衰减，40Hz 下 ≈0.5s 耗尽，
    /// 3m/s 冲量净位移 ≈ 3×dt/(1−0.85) ≈ 0.5m。</summary>
    private const float ExternalVelocityDecay = 0.85f;

    /// <summary>
    /// 外部冲量通道（鼠煞竞技场放人推离用，opt-in——默认零、不叠加时行为逐帧不变）：
    /// 「无输入即刻停」语义会把直接写进 <see cref="CharacterBody3D.Velocity"/> 的推离
    /// 速度在下一物理步一步归零（位移恒零，推离成死机制——R18 评审实证）；本通道在
    /// 归零/覆写**之后**叠加、逐步几何衰减，推离才真的把人推开。锁定/冻结期不叠加
    /// （被抓着不该被外力挪动），解锁后残余继续生效。
    /// </summary>
    public void AddImpulse(Vector3 velocity) => _externalVelocity += velocity;

    public bool Active { get; private set; }

    /// <summary>
    /// 束缚门（抓取竞技场用）：true 时吞掉全部玩家输入（鼠标视角、移动、跳跃），但**重力
    /// 自驱仍在**——空中被抓会自然落回地面，不会悬停；朝向交给世界脚本经
    /// <see cref="SetLookAngles"/> 驱动。默认 false，迷宫场景行为不变。
    /// </summary>
    public bool InputLocked { get; set; }

    /// <summary>
    /// 全冻结门（拖拽/吞没阶段用）：true 时自驱物理整段停掉（含重力与 MoveAndSlide），
    /// 位置完全交给世界脚本外部写 <c>GlobalPosition</c>——两者同时跑会互相打架。默认 false。
    /// </summary>
    public bool MotionFrozen { get; set; }

    // Eye* 三个观测量刻意**不读相机节点**、从未震动的 yaw/pitch 刚体推导：震动是纯化妆，
    // 只落在相机节点这最后一层；拖拽物理、吞没判定、对准判定、枪线全都不能被它污染
    // （否则物理结果随渲染帧率漂移）。无震动时两种算法逐位等价（身体只转 yaw、无缩放）。

    /// <summary>视点世界位置（HUD/拖拽/枪口用）——未震动刚体值，见上。</summary>
    public Vector3 EyePosition => GlobalPosition + new Vector3(0f, EyeHeight, 0f);

    /// <summary>视线方向（世界系，指向画面深处）——未震动刚体值，见上。</summary>
    public Vector3 EyeForward => -EyeBasis.Z;

    /// <summary>视点世界基（枪口偏移等相机系偏移用）——未震动刚体值，见上。</summary>
    public Basis EyeBasis =>
        GlobalTransform.Basis * Basis.FromEuler(new Vector3(_pitch, 0f, 0f));

    /// <summary>身体 yaw（弧度）。镜头接管读取当前值做阻尼插值。</summary>
    public float Yaw => Rotation.Y;

    /// <summary>相机俯仰（弧度，向上为正）。镜头接管读取当前值做阻尼插值。</summary>
    public float CameraPitch => _pitch;

    /// <summary>
    /// 外部镜头接管入口：直接写 yaw + pitch（pitch 仍夹既有限幅）。只在
    /// <see cref="InputLocked"/> 期间由世界脚本调用；语义与鼠标路径完全一致——
    /// yaw 落在身体、pitch 落在相机子节点。
    /// </summary>
    public void SetLookAngles(float yaw, float pitch)
    {
        Rotation = new Vector3(0f, yaw, 0f);
        _pitch = Mathf.Clamp(pitch, -PitchLimit, PitchLimit);
        ApplyCameraRotation();
    }

    /// <summary>
    /// 相机震动偏移（局部位移 + 欧拉增量，弧度）：挣脱等高强度时刻由世界脚本每帧喂值、
    /// 结束喂零。纯化妆姿态，只写相机节点——Yaw/CameraPitch 与 Eye* 全套观测量都
    /// **保证不受震动影响**（Eye* 从刚体推导，见上），逻辑侧可放心在震动期继续消费。
    /// </summary>
    public void SetCameraShake(Vector3 localOffset, Vector3 eulerRadians)
    {
        _shakeOffset = localOffset;
        _shakeEuler = eulerRadians;
        if (_camera is not null)
            _camera.Position = new Vector3(0f, EyeHeight, 0f) + localOffset;
        ApplyCameraRotation();
    }

    /// <summary>
    /// 观感级配置：调整随身补光强度/范围（灯光机制场景要保住明暗对比时压暗它）。
    /// 纯化妆配置面（同 <see cref="SetCameraShake"/> 性质），不碰运动规格；
    /// 不调用则保持默认 3.4/18——既有场景行为逐字节不变。
    /// </summary>
    public void ConfigureFillLight(float energy, float range)
    {
        _fillLight.LightEnergy = energy;
        _fillLight.OmniRange = range;
    }

    public override void _Ready()
    {
        CollisionLayer = PlayerCollisionLayer;
        CollisionMask = TerrainCollisionLayer;

        AddChild(new CollisionShape3D
        {
            Name = "CollisionShape3D",
            Shape = new CapsuleShape3D { Radius = CapsuleRadius, Height = CapsuleHeight },
            Position = new Vector3(0f, CapsuleCenterY, 0f),
        });

        _camera = new Camera3D
        {
            Name = "Camera3D",
            Position = new Vector3(0f, EyeHeight, 0f),
            Fov = FieldOfView,
            Far = 200f,
            Current = false,
        };
        AddChild(_camera);

        // 场景的环境光是给俯视调的，站到地面上后近处基本全黑；给个短程补光把身前
        // 一两格照出来——纯观察辅助，不是主仓那套手电 rig。
        _fillLight = new OmniLight3D
        {
            Name = "PlayerFill",
            LightColor = new Color(1f, 0.93f, 0.84f),
            LightEnergy = 3.4f,
            OmniRange = 18f,
        };
        _camera.AddChild(_fillLight);

        // 退出第一人称后从俯视相机看得见人站在哪；第一人称下藏起来（免得糊在近裁剪面上）。
        _bodyMesh = new MeshInstance3D
        {
            Name = "BodyMesh",
            Mesh = new CapsuleMesh { Radius = CapsuleRadius, Height = CapsuleHeight },
            Position = new Vector3(0f, CapsuleCenterY, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.95f, 0.86f, 0.42f),
                EmissionEnabled = true,
                Emission = new Color(0.35f, 0.30f, 0.10f),
                EmissionEnergyMultiplier = 1.2f,
                Roughness = 0.6f,
            },
        };
        AddChild(_bodyMesh);

        SetActive(false);
    }

    /// <summary>进/出第一人称：接管相机与鼠标，退出时整个节点冻结（位置保留）。</summary>
    public void SetActive(bool active)
    {
        Active = active;
        ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        if (_camera is not null)
            _camera.Current = active;
        if (_bodyMesh is not null)
            _bodyMesh.Visible = !active;
        if (active)
        {
            Velocity = Vector3.Zero;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    /// <summary>把人放到 <paramref name="floorPosition"/>（地面高度）并转向 <paramref name="lookAt"/>。</summary>
    public void Place(Vector3 floorPosition, Vector3 lookAt)
    {
        GlobalPosition = floorPosition + new Vector3(0f, SpawnClearance, 0f);
        Velocity = Vector3.Zero;
        _externalVelocity = Vector3.Zero;
        Vector3 to = lookAt - GlobalPosition;
        // Godot 里 -Z 是前方：yaw 使 -basis.Z 指向目标。
        if (to.LengthSquared() > 1e-6f)
            Rotation = new Vector3(0f, Mathf.Atan2(-to.X, -to.Z), 0f);
        _pitch = 0f;
        ApplyCameraRotation();
    }

    public override void _Input(InputEvent @event)
    {
        if (!Active || InputLocked || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;
        if (@event is not InputEventMouseMotion motion)
            return;
        RotateY(-motion.Relative.X * MouseSensitivity);
        _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -PitchLimit, PitchLimit);
        ApplyCameraRotation();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Active || MotionFrozen)
            return;

        Vector3 velocity = Velocity;
        if (!IsOnFloor())
            velocity += GetGravity() * (float)delta;

        if (InputLocked)
        {
            // 束缚下只剩重力自驱：水平分量沿用「无输入即刻停」语义归零，然后照常落地。
            velocity.X = Mathf.MoveToward(velocity.X, 0f, MoveSpeed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0f, MoveSpeed);
            Velocity = velocity;
            MoveAndSlide();
            return;
        }

        if (Input.IsPhysicalKeyPressed(Key.Space) && IsOnFloor())
            velocity.Y = JumpVelocity;

        Vector2 input = MovementInput();
        Vector3 direction = (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized();
        if (direction != Vector3.Zero)
        {
            float speed = MoveSpeed
                * (Input.IsPhysicalKeyPressed(Key.Shift) ? SneakSpeedScale : 1.0f);
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            // ≙ 主仓：无输入即刻停（MoveToward 的步长就是满速，一步归零）。
            velocity.X = Mathf.MoveToward(velocity.X, 0f, MoveSpeed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0f, MoveSpeed);
        }

        // 外部冲量在归零/覆写之后叠加（见 AddImpulse）；未用时恒零，行为逐帧不变。
        // 衰减按时间常数换算（0.85/40Hz 步）：常数每步 ×0.85 会让高 HostPhysicsTps
        // 场景（导出范围允许到 1000）的推离净位移随 tps 反比缩水一个数量级——
        // 拟态草竞技场评审实测。40Hz 下 Pow(0.85, 1) ≡ 0.85，既有场景逐帧不变。
        velocity += _externalVelocity;
        _externalVelocity *= Mathf.Pow(
            ExternalVelocityDecay, (float)GetPhysicsProcessDeltaTime() * 40f);
        if (_externalVelocity.LengthSquared() < 1e-6f)
            _externalVelocity = Vector3.Zero;

        Velocity = velocity;
        MoveAndSlide();
    }

    private void ApplyCameraRotation()
    {
        if (_camera is not null)
            _camera.Rotation = new Vector3(
                Mathf.Clamp(_pitch + _shakeEuler.X,
                    -CameraPitchHardLimit, CameraPitchHardLimit),
                _shakeEuler.Y, _shakeEuler.Z);
    }

    private static Vector2 MovementInput()
    {
        Vector2 input = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1f;
        return input.Normalized();
    }
}
