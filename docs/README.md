# 文档索引

本目录是 `proc_anim_lab` 的参考资料。项目目标与路线图见根目录 [`../CLAUDE.md`](../CLAUDE.md)。

## 研究与契约（跨物种）

| 文档 | 内容 |
|------|------|
| [rainworld_procedural_animation_research.md](rainworld_procedural_animation_research.md) | **核心参考**：《雨世界》程序化生物动画/运动系统深度研究。含开发者一手资料、社区反编译整理，以及**本机反编译实证**（§11：`BodyPart`/`Limb`/`LizardLimb`/`TailSegment`/`TerrainCurve` 等真实实现，代码级）与 **Godot(C#) 移植策略**（§12：为什么用射线而不是细网格）。 |
| [rainworld_render_research.md](rainworld_render_research.md) | **渲染研究（正式渲染层真相源）**：反编译十个物种 Graphics 类 + `GraphicsModule`/`TriangleMesh`/`RopeGraphic` 基建，提炼 RW"去球感"手法词汇表（render spine 双缓冲、万能管带、三种密化策略、解析半径剖面、平色融合、渲染期两骨 IK、假 3D 的 3D 归宿、seed 装饰系统、涌现生命感）、十物种策略速查、Godot 3D 渲染架构（四层 + 确定性边界），以及 §5 的实施状态（当前六个渲染件：Lizard / Centipede / Vulture / DaddyLongLegs / Spider / Humanoid）与遗留打磨清单。 |
| [rendering_principles.html](rendering_principles.html) | **正式渲染原理导览（HTML 长文）**：面向阅读的叙述版——从一团物理球到样条、扫管网格、调色板与渲染侧化妆状态。与上一篇分工：渲染研究是**取证与契约**，这篇是**讲清楚为什么这样画**。用浏览器打开。 |
| [porting_contract.md](porting_contract.md) | **M5 产物**：`ProcAnim.Core` → `random-room-runtime` 回迁契约。目录分层与文件清单、十物种各自的装配/驱动/输入/输出四契约、`ITerrainQuery` 接缝语义（射线 + 球体穿透两原语）、宿主 tether 配方（`Shift`/`Launch`）、确定性守则与三层回归、两条迁移路线与两种集成姿态（含 60Hz 宿主累加器与 `MonsterMotionSnapshot` 映射表）、单位常量表。**回迁时以本文为准。** |
| [rainworld_creature_taxonomy.md](rainworld_creature_taxonomy.md) | **反编译实证**：雨世界 92 个物种 / 54 个 `Creature` 实现类的分类地图。三条正交分类轴（物种枚举 / 模板继承 / 实现类树）、`Creature` / `BodyPart` 继承树与独立 `Tentacle` 段链、按构造函数统计的**七大身体架构**（珠链+腿 / 单点+图形腿 / 全连接珠团 / 长链条 / 飞行 / 固定 / 水生）、模板参数抽样表。**扩多节脊柱、肢体或固定触手前先查这里的先例。** |

## 十一个并列物种后端

每篇记录该后端的身体结构、机制来源（反编译取证）、各轮修复的症状/根因/被推翻的初判、
3D 偏离原作的理由与验证边界。装配/输入/输出契约统一以 `porting_contract.md` 为准。

| 文档 | 后端 |
|------|------|
| [spider_controller.md](spider_controller.md) | **Spider**：足端粒子 + 渲染期两骨 IK 分层、窄墙抱边、完整迈步（PEP→AEP 事务）、急转腿槽镜像与同面站距回收、spider-lean 场景门。 |
| [humanoid_controller.md](humanoid_controller.md) | **Humanoid（拾荒者）**：清醒近地失重伺服木偶、站立力偶零 knockdown 状态机、手臂两条独立通道与优先级链、WalkLean / HandSpread / HeadTurn / LegGait 四轮实测修复。 |
| [vulture_controller.md](vulture_controller.md) | **Vulture**：重力常开 + 拍翅同步 sin² 升力脉冲、K4 风筝刚架、翅膀模式涌现的起降、四预设与落地即修的对抗性评审轮。 |
| [centipede_controller.md](centipede_controller.md) | **Centipede**：任意节出生配置与逐节覆写、质量加权装配、双端表面轨迹、有限实体前视、真实抓足与跨墙复位、确定性行波/自避、四个稳定预设。 |
| [cicada_controller.md](cicada_controller.md) | **Cicada**：双 chunk 差分飞行、稳定 3D 姿态、显式停驻、起飞/Charge、四翼四触须。 |
| [tentacle_plant_controller.md](tentacle_plant_controller.md) | **TentaclePlant（拟态草）**：锚定式身体、独立触手段链、确定性三维游荡、蓄势—突刺—回收、纯值目标/效果接缝、三种安装朝向。 |
| [deer_controller.md](deer_controller.md) | **Deer**：高站姿、头顶大轻鹿角物理代理、粗重叠躯干、动态 reach 的独立多节腿、常开重力下的连续支撑/推进/迈步、有向 bend pole。 |
| [daddy_long_legs_controller.md](daddy_long_legs_controller.md) | **DaddyLongLegs**：无头尾完整图球团、seed 冻结的可变形态、Fibonacci sphere 材料偏好、主动/被动贴附分层、渐进剥离与余长弧形 guide、tick-end 邻边审计与原子后缀恢复、失速-回冲修复轮。 |
| [dropbug_controller.md](dropbug_controller.md) | **DropBug（掉落虫）**：三节短链、前后不对称重力、运行时收放静息长度的悬挂态、3D 悬挂点判据、弹道俯冲、蓄力扑击与轴向可及、腿为纯图形件的实证。 |
| [ratfiend_controller.md](ratfiend_controller.md) | **RatFiend（鼠煞）**：倾斜站立力偶（常态驼背）与推进天花板差分、Gait 走跑姿态连续混合、断肢固定断肘/膝与爬行（推进 ∝ 抓地肢体数）、最小攻击接缝、首个可动颌渲染件、枪击部位判定竞技场。 |
| — | **Lizard** 是最初的基线后端，没有独立文档：机制沿 M1~M5 里程碑分段记录在 [`../CLAUDE.md`](../CLAUDE.md) §5，契约在 `porting_contract.md`。 |

## 归档

| 文档 | 说明 |
|------|------|
| [archive/known_issue_three_chunk_turn_response.md](archive/known_issue_three_chunk_turn_response.md) | **已解决**（随 RearBrace 轮消失，2026-07-30 重跑确认）：`heavy`/`hexapod` 行进中切换胡萝卜时中段局部轴领先头段。保留确定性复现、指标定义与前后量化对照，供同类问题参考。 |

## 快速定位（研究文档内锚点）

- **身体物理**（chunk + 弹簧 + Verlet）→ §3
- **腿 IK + 落点搜索**（单点追目标、`FindGrip`）→ §11.4 / §11.5
- **地形两套表示**（方块格 + 有机样条 `SnapToTerrain`）→ §11.4b
- **身子不掉墙**（重力开关 + `LegsGripping`）→ §11.6b / §11.6c
- **locomotion 模式**（Player 显式状态机 vs Lizard 涌现）→ §11.6
- **确定性 / tick / 插值**（40 TPS + timeStacker）→ §7
- **移植到 Godot 的落地配方与策略** → §11.9 / §12

> 说明：`rainworld_procedural_animation_research.md` 是从主项目 `random-room-runtime/docs/`
> 拷来的**工作副本**；文中指向主项目其它文档的相对链接在本项目内会失效，属正常。
> 2026-08-06 已用本仓版本（含蜘蛛 §11.1a/§11.1b 扩充）反向同步回主项目，两边当前一致。
