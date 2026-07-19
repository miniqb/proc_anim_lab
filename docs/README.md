# 文档索引

本目录是 `proc_anim_lab` 的参考资料。项目目标与路线图见根目录 [`../CLAUDE.md`](../CLAUDE.md)。

| 文档 | 内容 |
|------|------|
| [rainworld_procedural_animation_research.md](rainworld_procedural_animation_research.md) | **核心参考**：《雨世界》程序化生物动画/运动系统深度研究。含开发者一手资料、社区反编译整理，以及**本机反编译实证**（§11：`BodyPart`/`Limb`/`LizardLimb`/`TailSegment`/`TerrainCurve` 等真实实现，代码级）与 **Godot(C#) 移植策略**（§12：为什么用射线而不是细网格）。 |
| [porting_contract.md](porting_contract.md) | **M5 产物**：`ProcAnim.Core` → `random-room-runtime` 回迁契约。模块清单与依赖面、装配/驱动/输入/输出四契约、`ITerrainQuery` 接缝语义（射线 + 球体穿透两原语）、宿主 tether 配方（`Shift`/`Launch`）、确定性守则与三层回归、两条迁移路线与两种集成姿态（含 60Hz 宿主累加器与 `MonsterMotionSnapshot` 映射表）、单位常量表。 |

## 快速定位（研究文档内锚点）

- **身体物理**（chunk + 弹簧 + Verlet）→ §3
- **腿 IK + 落点搜索**（单点追目标、`FindGrip`）→ §11.4 / §11.5
- **地形两套表示**（方块格 + 有机样条 `SnapToTerrain`）→ §11.4b
- **身子不掉墙**（重力开关 + `LegsGripping`）→ §11.6b / §11.6c
- **locomotion 模式**（Player 显式状态机 vs Lizard 涌现）→ §11.6
- **确定性 / tick / 插值**（40 TPS + timeStacker）→ §7
- **移植到 Godot 的落地配方与策略** → §11.9 / §12

> 说明：本文件是从主项目 `random-room-runtime/docs/` 拷来的**工作副本**；文中指向主项目其它文档的相对链接在本项目内会失效，属正常。回迁时以主项目版本为准。
