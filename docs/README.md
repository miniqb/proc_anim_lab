# 文档索引

本目录是 `proc_anim_lab` 的参考资料。项目目标与路线图见根目录 [`../CLAUDE.md`](../CLAUDE.md)。

| 文档 | 内容 |
|------|------|
| [rainworld_procedural_animation_research.md](rainworld_procedural_animation_research.md) | **核心参考**：《雨世界》程序化生物动画/运动系统深度研究。含开发者一手资料、社区反编译整理，以及**本机反编译实证**（§11：`BodyPart`/`Limb`/`LizardLimb`/`TailSegment`/`TerrainCurve` 等真实实现，代码级）与 **Godot(C#) 移植策略**（§12：为什么用射线而不是细网格）。 |
| [porting_contract.md](porting_contract.md) | **M5 产物**：`ProcAnim.Core` → `random-room-runtime` 回迁契约。模块清单与依赖面、装配/驱动/输入/输出四契约、`ITerrainQuery` 接缝语义（射线 + 球体穿透两原语）、宿主 tether 配方（`Shift`/`Launch`）、确定性守则与三层回归、两条迁移路线与两种集成姿态（含 60Hz 宿主累加器与 `MonsterMotionSnapshot` 映射表）、单位常量表。 |
| [centipede_controller.md](centipede_controller.md) | **并列蜈蚣后端**：任意节出生配置与逐节覆写、质量加权装配、双端表面轨迹、有限实体前视、真实抓足与跨墙复位、确定性行波/自避、宿主生命周期契约、四个稳定预设，以及固定领航端下阶梯/窄墙回归与其在 45 项 Godot 完整矩阵中的 13 项边界。 |
| [rainworld_creature_taxonomy.md](rainworld_creature_taxonomy.md) | **反编译实证**：雨世界 92 个物种 / 54 个 `Creature` 实现类的分类地图。三条正交分类轴（物种枚举 / 模板继承 / 实现类树）、`Creature` / `BodyPart` 继承树与独立 `Tentacle` 段链、按构造函数统计的**七大身体架构**（珠链+腿 / 单点+图形腿 / 全连接珠团 / 长链条 / 飞行 / 固定 / 水生）、模板参数抽样表，以及对本项目多节脊柱和独立触手扩展的结论。 |
| [cicada_controller.md](cicada_controller.md) | **Cicada 3D 后端**：双 chunk 差分飞行、稳定 3D 姿态、显式停驻、起飞/Charge、四翼四触须的宿主接口与专项回归。 |
| [tentacle_plant_controller.md](tentacle_plant_controller.md) | **拟态草 3D 后端**：直接反编译 TentaclePlant/Tentacle 并以 PoleMimic、GarbageWorm 对照；记录锚定式身体、独立触手段链、确定性三维游荡、蓄势—突刺—回收、纯值目标/效果接缝、三种安装朝向与专项回归。 |
| [deer_controller.md](deer_controller.md) | **Deer 3D 后端**：直接反编译 Deer / DeerTentacle / Tentacle；记录高站姿、头顶大轻鹿角物理代理、粗重叠躯干、动态 reach 的独立多节腿、重力常开下的连续支撑/推进/迈步，以及 3D 外撇、有向 bend pole、三预设 180° 换向真实弓向、地形接缝、宿主生命周期、白盒沙盒与专项回归。 |
| [known_issue_three_chunk_turn_response.md](known_issue_three_chunk_turn_response.md) | **历史问题（已随 RearBrace 轮消失，保留复现与指标）**：`heavy`/`hexapod` 行进中切换胡萝卜时中段局部轴领先头段。含确定性复现、当时与当前的量化对照、当前回归边界。 |

> **没有独立文档的三个并列后端**：蜘蛛（`SpiderLocomotionController`）、秃鹫
> （`VultureFlightController`）、人形（`HumanoidLocomotionController`）的契约分别写在
> [porting_contract.md](porting_contract.md) §2.3 / §2.5 / §4.2·§5.4 与
> [`../CLAUDE.md`](../CLAUDE.md) §5 的对应段落；可执行基线在 `tools/run_matrix.sh`、
> `tools/run_spider_matrix.sh` 与 `core/smoke`、`core/spider_smoke`。

## 快速定位（研究文档内锚点）

- **身体物理**（chunk + 弹簧 + Verlet）→ §3
- **腿 IK + 落点搜索**（单点追目标、`FindGrip`）→ §11.4 / §11.5
- **地形两套表示**（方块格 + 有机样条 `SnapToTerrain`）→ §11.4b
- **身子不掉墙**（重力开关 + `LegsGripping`）→ §11.6b / §11.6c
- **locomotion 模式**（Player 显式状态机 vs Lizard 涌现）→ §11.6
- **确定性 / tick / 插值**（40 TPS + timeStacker）→ §7
- **移植到 Godot 的落地配方与策略** → §11.9 / §12

> 说明：本文件是从主项目 `random-room-runtime/docs/` 拷来的**工作副本**；文中指向主项目其它文档的相对链接在本项目内会失效，属正常。回迁时以主项目版本为准。
