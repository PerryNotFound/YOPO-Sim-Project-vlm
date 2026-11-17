# YOPO-Sim 使用与改造指南

> 适用工程：`/home/admin0/YOPO-Rally-Whole/YOPO-Sim-Project`（Unity 2022.3.62f3c1，URP）
> 配套 ROS2 工作区：`/home/admin0/YOPO-Rally-Whole/YOPO-Rally`
> 最后更新：2026-11-03

## 目录

1. [地形与树密度：问题原因与修复](#1-地形与树密度问题原因与修复)
2. [随机水坑设计方案](#2-随机水坑设计方案)
3. [YOPO-Sim 整体架构与 Unity 操作逻辑](#3-yopo-sim-整体架构与-unity-操作逻辑)
4. [EvaluationScene 与 DataGenerationScene 的关系](#4-evaluationscene-与-datagenerationscene-的关系)
5. [目标点、GoalPublisher 与 RViz 操作](#5-目标点goalpublisher-与-rviz-操作)

---

## 0. 速查表

**仿真闭环启动顺序**（顺序不能乱）：

```bash
# 1. 终端 A：ROS-TCP 桥
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=127.0.0.1

# 2. Unity：打开 EvaluationScene → Play → 点 Connect（左上角 ROS2 IP 面板）

# 3. 终端 B：算法栈
ros2 launch diablo_ros_wrapper diablo_algorithm.launch.py

# 4. 终端 C：M0-M4 之一（以 M0 基线为例，需要 conda base + .venv）
conda activate base && source ~/YOPO-Rally-Whole/YOPO-Rally/.venv/bin/activate
python3 ~/YOPO-Rally-Whole/yopo-vlm-sys2/eval/m0_passthrough.py

# 5. 终端 D：RViz
rviz2 -d ~/YOPO-Rally-Whole/YOPO-Rally/src/ros_pkgs/diablo_ros_wrapper/configs/visualize.rviz
# 工具栏点 "2D Goal Pose" 在地图里点目标 → 小车开始走
```

**常见问题速查**：

| 现象 | 原因 | 处理 |
|---|---|---|
| 点新目标 1 秒后跳回旧点 | Unity 里 GoalPublisher 激活着，每秒覆盖 | Hierarchy 找到 GoalPublisher → 取消勾选激活（当前已禁用） |
| 切 Tree Density 后地形变崎岖 | 旧版 TreeDensitySetter 全量再生成覆盖了烘焙地形 | 已修复（见第 1 节）；资产被写坏时用 git 恢复（命令见下） |
| 小车出生就掉下去/埋住 | 地形被再生成，起点高度变了 | 同上，修复后不会再发生 |
| Unity 刷 "Queue full! Messages dropped" | ROS-TCP 拥塞（深度图流量大） | 已知现象，不影响闭环 |
| 水面一闪一闪 | 水面与地形几乎共面，地形 LOD 动态细分反复穿过水面 | `Surface Offset` ≥ 0.06；Terrain 设置的 Pixel Error 调到 1~3 |
| 改了 yopo_node.py 不生效 | 忘重新 build | `colcon build --packages-select diablo_ros_wrapper`（禁止 --symlink-install） |

---

## 1. 地形与树密度：问题原因与修复

### 1.1 为什么切密度会把地形搞坏

- 三张密度图资产 `Assets/TerrainData/TerrainGraph{Dense,Normal,Sparse}.asset` **只有树的参数不同**（间距/密度倍率），地形高度节点完全相同。
- 但 Vista 的 `VistaManager.Generate()` 是**全量再生成**：高度、纹理、树全部重写进 TerrainData。于是切密度时地形被"重新生成"了一遍——再生成结果 ≠ 场景里烘焙好的原版地形，所以变得崎岖、出现陡坡和悬崖。
- 更坑的是：**Play 模式下对 TerrainData 的修改会永久写回 `.asset` 文件**。点一次下拉，`Assets/TerrainData/EvaluationTerrainData.asset` 就被覆盖，退出 Play 也不会还原。
- 小车起点是场景里写死的固定坐标（EvaluationScene 为 `(2.24, 0.266, 5.28)`），起点高度不变而地面变高/变低，就会出现"出生即悬空掉落"或"埋进土里"。

### 1.2 已做的修复（2026-11-02）

改动了 `Assets/Scripts/UI/TreeDensitySetter.cs` 的 `GenerateTerrain()` 协程：

1. **生成前快照**：对 Vista 管理的每个地块，先 `terrainData.GetHeights()` 把当前（原版烘焙）高度图完整复制下来；
2. Vista 全量再生成（树按新密度种好）；
3. **生成后恢复**：把高度图原样 `SetHeights()` 写回，若 heightmapResolution 被改也一并还原；
4. **树木落地**：新种的树是按"再生成地形"的高度放的，恢复旧高度后树会悬空/下陷，所以逐棵树用 `GetInterpolatedHeight()` 重新采样地面高度贴地；
5. 最后照常刷新 TerrainCollider。

效果：**切 Tree Density 只换树的疏密，地形起伏永远保持原版**，小车起点不会再悬空。恢复操作同时也把 `.asset` 文件里的高度图写回原版值，相当于资产"自愈"。

### 1.3 如果地形资产还是被搞坏了（手动恢复）

```bash
# 先完全退出 Unity（不要在 Unity 里保存场景/资产）
cd ~/YOPO-Rally-Whole/YOPO-Sim-Project
git checkout -- Assets/TerrainData/EvaluationTerrainData.asset
# 恢复后应为 2262360 字节
ls -la Assets/TerrainData/EvaluationTerrainData.asset
```

> 2026-11-02 已执行过一次恢复。DataGenerationScene 用的是另一个资产 `DataGenerationTerrainData.asset`，如果它也坏了同理 `git checkout`。

### 1.4 注意事项

- 密度下拉只在 **Play 模式中**有效（它是运行时 UI）。
- DataGenerationScene 点 "Generate Data" 时**故意**用不同种子再生成地形（每个 terrain round 换 `_biome.seed`），那是数据增强的设计目的，不是 bug；但它写的是 `DataGenerationTerrainData.asset`，和评测场景互不影响。

---

## 2. 随机水坑设计方案

### 2.1 现状盘点

EvaluationScene 里现有 **6 个手动摆的水坑**（Hierarchy 里的 Plane 2/3/5/6/7/8）：

- 全是**压扁的内置 Cylinder 网格**，统一材质 `Assets/ThirdParty/TerrainDemoScene_URP/Prefabs/Water/Materials/WaterDepthBased.mat`，没有任何脚本；
- 分布：x∈[7, 61]、z∈[7, 37]，半径约 1.7~4.1m，大多半埋（y≈-1，突出地面一点点），各绕 y 轴随机转了约 ±32°；
- **DataGenerationScene 里一个水坑都没有**——也就是说你目前生成的训练数据里不含水，VLM 评测时遇到的水坑它从没在仿真数据里见过。这一点对后续做数据生成/微调很重要。

### 2.2 设计目标

1. 每次（或每个种子）自动生成 N 个位置、大小随机的水坑；
2. 贴地：采样地形高度，半埋 5~15cm，看起来是"洼地积水"而不是"浮着的盘子"；
3. 只落在**平缓区域**（坡度过滤），避免斜坡上出现不合理的水平面；
4. 可复现：给定种子生成同样布局，方便对照实验；
5. 避开小车出生点（(2.24, 0.266, 5.28) 附近）和目标常见区域，别把出生点泡在水里。

### 2.3 实现：`PuddleRandomizer.cs`（已写好）

脚本已创建：`Assets/Scripts/PuddleRandomizer.cs`（不依赖 Vista，两个场景都能用），挂在场景 LOGICAL 组下即可。工作方式：Play 时用固定 seed 自动生成一次；在 DataGenerationScene 里会订阅 `DataGenerationEvents.BeforePoissonGeneration` 事件，**每个 terrain round 自动换 seed 重摆一轮**，不需要改 DataGenerationManager 任何代码。

**首次使用前的准备（在 Unity 里做一次）**：

1. 把水坑做成 prefab：Hierarchy 里右键现有的 Plane (3) → 拖到 Project 窗口的 `Assets/Prefabs/` 生成 prefab；双击打开它，**删掉 Mesh Collider**（方案 A：视觉障碍、物理可通行）；
2. 场景里 GameObject → Create Empty 命名 `PuddleRandomizer`，挂 `PuddleRandomizer` 脚本，把 prefab 拖进 `Puddle Prefab` 栏；
3. 想完全用随机水坑就把手动摆的 Plane (2)-(8) 删掉；想保留就留着，两批会共存。

**Inspector 参数**（默认值已按现有 6 个手动水坑的分布调好）：

| 参数 | 建议默认 | 说明 |
|---|---|---|
| `seed` | 0 | 随机种子，固定则布局可复现 |
| `count` | 6 | 水坑数量 |
| `areaMin / areaMax` | (7, 7) / (62, 38) | 生成区域（xz 矩形），参考现有分布 |
| `radiusRange` | 1.5 ~ 4.5 m | 水坑半径（轮廓经正弦扰动，非正圆） |
| `maxSlopeDeg` | 10° | 坡度上限，用 `TerrainData.GetSteepness()` 过滤 |
| `maxHeightDiff` | 0.05 m | 水坑范围内地面高差上限（中心+8 边缘采样）。不挖坑后靠它保证水面贴地：地面微起伏 ≤5cm，低处进水、高处自然露头成岸线 |
| `minGap` | 1 m | 水坑之间水缘的最小间距，防止连成一片 |
| `surfaceOffset` | 0.08 m | 水面高出范围内**最低**原地面多少。抬高可让水面离开草丛；太大会在水缘显得悬空 |

**挖坑代码已整体移除（2026-11-05）**：地形完全不被修改，水面就是铺在平地上的不规则薄片，无需快照/恢复，也就不存在"崩溃留坑"的问题。

**⚠️ 血泪教训：Inspector 序列化值优先于 C# 脚本默认值**。组件挂进场景后，场景文件（`EvaluationScene.unity`）保存的是当时的字段值，之后改脚本里的 `[SerializeField]` 默认值**对已有场景实例无效**（新字段才会用脚本默认）。本次事故：`maxHeightDiff` 场景里一直是旧值 0.5 m，脚本改 0.05 从未生效，"坡度差"久治不愈的根因。**调参要在 Inspector（选中 PuddleRandomizer 对象）里改，或直接改场景文件；改完脚本默认值记得核对 Inspector。**
| `exclusionCenter / exclusionRadius` | (2.24, 5.28) / 8 m | 小车出生点保护圈 |
| `reRollPerTerrainRound` | ✓ | DataGenerationScene 里每个地形自动重摆 |

**生成流程**（`Generate(int seed)`，已按上述逻辑实现）：

1. `Random.InitState(seed)`；清掉上一轮生成的水坑；
2. **等地形稳定**：Play 开始的前若干帧 Vista 在异步再生成地形，`Start()` 协程持续探测 9 个采样点高度，连续 5 帧不变（或超时 600 帧）才开始摆——否则会采到旧高度，水坑被埋到地形下面；
3. 拒绝采样：区域内随机取点 → 出生点保护圈、坡度、**水坑间距**（`minGap`）、**平坦度**（中心+8 边缘高差 ≤ `maxHeightDiff`）任一不过就重取，每个水坑最多试 `_maxAttemptsPerPuddle`（100）次；
4. 通过的点：生成水坑轮廓参数（`BlobOutline`：半径 + 两个低频正弦扰动），**不挖地**——水面取 9 个采样点（中心+8 边缘）的**最低高度** + `surfaceOffset`，直接铺成**零厚度不规则水平面**（实例化 prefab 只取它的水材质，网格用轮廓参数程序生成）；`maxHeightDiff=0.05` 的选址保证地面起伏都在水面上下几厘米内，低处被淹、高处自然露头，形成贴齐的岸线。地形完全不被修改，物理上没有任何坡度差，小车涉水通过。

**水材质调色**（`WaterDepthBased.mat`，直接改的 .mat 资产，2026-11-05）：原配色在低机位掠射角下被菲涅尔天空反光洗成灰白色，VLM/HSV 都认不出。已把 `Shallow Water Color` 调成饱和蓝绿 `(0.10, 0.55, 0.85)`、`Deep Water Color` 调成深蓝 `(0.03, 0.18, 0.45)`、`Smoothness` 从 0.979 降到 0.75 减弱白色眩光。形状不用改——shader 靠"水面到水下地形的深度差"混色，平面/圆柱混色结果一样，圆柱没有侧壁以外的任何好处。**（2026-11-12 更新：该透明方案在车相机里仍近黑，已被不透明 VLMWater.mat 取代，见 2.3.1。）**

**碰撞体策略（先想清楚实验语义）**：

- **方案 A（推荐，配合 VLM 实验）**：水坑 prefab **不带 Collider**。物理上车可以开过去（涉水），"危险"完全靠视觉——这正是 M1-M4 语义否决要处理的场景：几何上可通行，语义上应避让；
- **方案 B**：保留 Collider，水坑同时是几何障碍，深度相机也能看到它凸起。但这样 YOPO 基线（M0）自己就能绕，体现不出 VLM 的价值，不建议。

**接入时序（事件驱动，不用改 DataGenerationManager）**：

- **EvaluationScene**：挂到场景里，`Start()` 时用固定 seed 自动生成一次；
- **DataGenerationScene**：脚本在 `OnEnable` 里订阅 `DataGenerationEvents.BeforePoissonGeneration`——这个事件恰好由 DataGenerationManager 在"地形再生成+点云保存完成之后、Poisson 采样之前"抛出（`DataGenerationManager.cs:117`）。收到事件就 `_layoutIndex++` 并用 `seed + _layoutIndex` 重摆一轮，**每张地形自动配一套确定性的水坑布局**，深度图里就有水了。注意点云（terrain.ply/tree.ply）在此事件之前已保存，水坑不会进点云（点云保持地形+树的纯净几何）。

### 2.3.1 渲染可见性修复（2026-11-12，闭环实测通过）

11-09 起排查"车相机里看不到水 / VLM 只报 mud 不报 water"，结论和修复如下，按根因排序：

1. **水材质换为不透明亮蓝 `Assets/Materials/VLMWater.mat`**（URP/Lit，BaseColor `(0.10, 0.40, 0.88)`，Smoothness 0.55，Opaque）。`WaterTemp.prefab` 的 m_Materials 已指向它。原因：WaterDepthBased 透明深度材质在 640×480 车相机掠射角下渲染成近黑色块，与树影/泥无法区分（VLM 报 mud）。不透明蓝绕开了 2.4 节"反射导致无蓝色簇"的原理性失效。想恢复真实感水材质，把 prefab 的材质 GUID 改回 `606a2cf006c309d47bb99f421ada20ca` 即可。
2. **⚠️ 手写 .meta 的 GUID 必须是 32 位十六进制**。少 2 位（30 位）时 Unity 报 "does not have a valid GUID ... asset will be ignored"，**材质静默丢失，物体渲染成品红（magenta）**。排查路径：Game 视图看到品红块 → `~/.config/unity3d/Editor.log` 搜资产名。
3. **RGB 相机 FOV 30°→70°**：改 `Assets/Samples/UnitySensorsROS/3.0.0/Sample/Prefabs/Camera/RGBCamera_ros.prefab` 的 `_fov`（场景无覆盖，直接改 prefab；深度相机不动）。30° 时水坑只在细条视锥内偶尔入画，是"VLM 时灵时不灵"的主因之一；70° 后水面在预瞄距离内常驻画面。`camera_info` 内参随 Unity 自动更新（70° 时 fx≈457）。YOPO 用深度相机，不受影响。
4. **水坑更大更稳**：场景序列化 `maxHeightDiff` 0.05→0.08，配合 `PuddleRandomizer` 新增的**半径递减重试**（同一采样点依次按 1.0/0.7/0.5/0.35 倍半径尝试，代码里的 `ShrinkSteps`）。之前 0.05 只能放出 2/6 且半径被压到 0.6-1.7m；现在稳定 6/6、半径 1.0-2.0m。每个水坑坐标会打到 Console：`PuddleRandomizer: puddle N at (x, z) r=…`，可直接用来发目标点做穿越测试。
5. **全链路可脚本启动**：`Assets/Editor/AutoPlay.cs`（命令行加 `-autoPlay`：先把构建目标强制切回 StandaloneLinux64——**WebGL 目标下 ROS-TCP 根本连不上，是"连不上 ROS"的隐藏根因**——再自动进 Play）；`Assets/Resources/ROSConnectionPrefab.prefab` 的 `m_ConnectOnStart` 已改 1（以前每次 Play 都要手点 HUD 上的连接按钮）。启动顺序：endpoint → Unity `-autoPlay` → 算法栈 → VLM 节点。注意算法栈环境是 `source /opt/ros/humble/setup.bash` + 激活 `YOPO-Rally/.venv`（venv 是 Python 3.10 且装了 torch，`#!/usr/bin/env python3` 的节点脚本会用到）+ `source install/setup.bash`，**不要碰 conda base**（Python 3.13）。
6. **Unity 重启后必须重启算法栈**（yopo/mpc 的 TF 缓冲会拿到断档时间戳，cmd_vel 归零）。
7. **实测记录**：北向穿越水坑时 VLM 连续报 `('water','center')` 且 `veto_paths` 生效；有一趟南向穿越未检出——水坑在微洼地里（waterY 比周边低约 0.3m）+ 草遮挡，远处被洼地边缘挡住，属物理遮挡而非识别失败。如需"远处必可见"的水坑，可把选址改成偏好局部高点。

### 2.4 与语义标签的关系（重要事实）

- Unity 工程里**没有语义分割相机**（全工程无任何 segmentation 相机/替换 shader）；传感器只有深度相机 + RGB 相机。
- **2026-11-04 标定结论**：车相机离地半米、掠射角看水面时，水面反射的是树木/天空，K-means 聚类里**没有任何蓝色簇**（全部 H 13~69 黄绿系，与草地/阴影混在一起）——HSV 颜色通道在该工况下原理性失效。**M2/M3 已切换到 `MODE = "vlm_zone"`（纯 VLM 分区感知，跳过 HSV）**；`hybrid`/`color` 模式代码保留，作为论文里"颜色法失效"的对照证据。
- `build_semantic_dataset.py` 里的水/沙/泥颜色编码是数据集后处理的调色板，与 Unity 渲染无关。

---

## 3. YOPO-Sim 整体架构与 Unity 操作逻辑

### 3.1 系统架构（仿真闭环）

```
┌───────────────────────── Unity (EvaluationScene) ─────────────────────────┐
│  DiabloWithSensors (小车 prefab, 起点 (2.24, 0.266, 5.28))                 │
│   ├─ DepthCamera_ros → /camera/depth/image, /camera/depth/camera_info,    │
│   │                    /camera/depth/points   (FOV 58°, 30Hz, frame "depth")│
│   ├─ RGBCamera_ros   → /camera/color/image/compressed, .../camera_info    │
│   ├─ DiabloControlROS: 发 /odom (frame "map", 10Hz), 订阅 /cmd_vel 驱动车轮 │
│   └─ TFMap: 发 odom→base_link TF                                          │
│  VistaManager + LocalProceduralBiome: 程序化地形（seed=0, TerrainGraphNormal）│
│  TreeDensitySettings: 密度下拉 UI（已修：只换树不动地形）                    │
│  GoalPublisher: 每秒发固定目标点（当前已禁用，见第 5 节）                    │
└──────────────┬──────────────────────────────────────▲─────────────────────┘
               │ ROS-TCP-Endpoint (127.0.0.1:10000)   │ /cmd_vel
┌──────────────▼──────────────────────────────────────┴─────────────────────┐
│  ROS2 (diablo_algorithm.launch.py)                                        │
│   yopo_node.py: 深度图→5 条候选轨迹→argmin(cost)+滞后切换→/end_state       │
│   mpc_node.py:  跟踪轨迹→/cmd_vel_mpc（launch 里 remap，原本叫 /cmd_vel）  │
│   M0-M4 节点:   /cmd_vel_mpc →（转发/否决/回退）→ /cmd_vel                │
│   RViz:        2D Goal Pose → /goal_pose；2D Pose Estimate → /initialpose │
└───────────────────────────────────────────────────────────────────────────┘
```

### 3.2 话题速查（仿真链路）

| 话题 | 方向 | 说明 |
|---|---|---|
| `/camera/depth/image` 等 | Unity → ROS | 深度图，YOPO 网络输入 |
| `/camera/color/image/compressed` | Unity → ROS | RGB 图，VLM 输入 |
| `/odom` | Unity → ROS | frame "map"，10Hz，小车里程计 |
| `/goal_pose` | RViz/GoalPublisher → yopo_node & mpc_node | 目标点（只有 x/y 有效） |
| `/initialpose` | RViz → mpc_node | 只重置 MPC 内部状态，**不动小车**（见 5.2） |
| `/cmd_vel_mpc` | mpc_node → M0-M4 | MPC 原始速度指令 |
| `/cmd_vel` | M0-M4 → Unity | 最终速度指令，Unity 只认这个 |
| `/vlm_veto` `/vlm_penalty` `/vlm_goal` | VLM 节点 → yopo_node | M2/M3 的语义接口 |

> 注意：`depolyment_msg_convertor.py`（T265、`/diablo/MotionCmd` 那套）是**真机部署链路**，仿真 launch 不启动它，仿真里 `/odom` 由 Unity 直接发布。

### 3.3 坐标系换算（RViz ↔ Unity）

ROS-TCP 用 FLU 约定，Unity (x, y, z) → ROS (x, y, z) 的映射为：

```
ROS.x = Unity.z      ROS.y = -Unity.x      ROS.z = Unity.y
yaw_ros = -yaw_unity（数据 toml 里写作 yaw_ros = 360 - yaw_unity）
```

所以 **RViz 里的 map 原点 = Unity 世界原点**，RViz 网格区 ≈ Unity 里 (0,0) 附近。举例：Unity 里 GoalPublisher 的位置 (136.7, 0.71, 125.4) 对应 ROS 坐标 (125.4, **-136.7**)。想反查"RViz 点的目标在 Unity 哪"：`Unity.z = ROS.x`，`Unity.x = -ROS.y`。

### 3.4 Unity 侧关键物体

| 物体 | 作用 |
|---|---|
| `DiabloWithSensors` | 小车 + 传感器 prefab（EvaluationScene 用带 ROS 版；DataGenerationScene 用 DiabloNoROSWithSensors） |
| `VistaManager` + `Local Procedural Biome` | Vista 程序化地形管线（seed=0，图 = TerrainGraphNormal） |
| `TreeDensitySettings` | 树密度下拉 UI（Dense/Normal/Sparse 三张图只差树参数） |
| `GoalPublisher` | 每秒把自己坐标发到 `/goal_pose`（**当前已禁用**） |
| `Plane (2)-(8)` | 手动水坑（见第 2 节） |
| Canvas 上的 `DepthImage` | 把深度图实时显示在屏幕角落的调试窗口 |

---

## 4. EvaluationScene 与 DataGenerationScene 的关系

### 4.1 设计思路：同一条管线，两种用法

两个场景**共用同一套 Vista 程序化生成管线和同一张 TerrainGraphNormal 图**（biome 配置、seed=0 都一样），区别在于：

| | EvaluationScene | DataGenerationScene |
|---|---|---|
| 用途 | **闭环评测**：人在 RViz 点目标，跑 M0-M4 | **批量产数据**：自动换地形、自动撒点拍深度图 |
| 小车 prefab | DiabloWithSensors（带 ROS 相机） | DiabloNoROSWithSensors（无 ROS，省流量） |
| 地形资产 | `EvaluationTerrainData.asset` | `DataGenerationTerrainData.asset`（只是 Vista 覆盖前的占位，两者互不影响） |
| 地形种子 | 固定 seed=0 | 每个 terrain round 换种子（`_biome.seed = terrainSeed`）再生成 |
| 水坑 | 6 个手动水坑 | **0 个** |
| 独有组件 | TreeDensity 下拉、GoalPublisher（已禁用） | DataGenerationManager、ImageSampler、ScenePointCloudSaver、PoissonGenerator、FilePathManager、TomlDataManager、VoxelGenerator |

**"两个场景是不是要设计得一样？"** ——不需要完全一样，而且现状就是刻意不同：评测场景追求"固定、可复现、可交互"；数据场景追求"批量、多样、无人值守"。但如果你打算用水坑做论文实验，**目前数据场景没有水坑是一个真实缺口**（见 2.1），建议按 2.3 把 PuddleRandomizer 接进数据生成流程。

### 4.2 DataGenerationScene 界面按钮含义

**Training / Testing 两组滑条**（各 0~10，脚本 `DataGenerationManager.cs` 的 `[Range(0,10)]`）：

- **Terrain Round** = 要遍历的**地形种子数量**。第 i 轮用种子 `_terrainSeedStart + i` 让 Vista 重新生成**整张地形**，并保存一份地形/树点云；
- **Poisson Round** = **每张地形上的泊松采样轮数**。第 j 轮用种子 `_poissonSeedStart + j` 在 100×100m 区域（中心 (75,10,75)）重新撒一整套泊松盘采样点（最小间距 10m），小车被逐个传送到这些点（离地 +0.5m、随机水平朝向）各拍一张深度图；
- 某组设为 0 = 整组跳过。场景当前配置：Training 1×1，Testing 0×0。

**Generate Data 按钮** → `DataGenerationManager.StartDataGeneration()`，协程流程：

1. 关闭帧率限制和 VSync，全速跑；
2. 先 Training 后 Testing：**每组开始前删除对应的旧数据文件夹**（注意！TrainingData/TestingData 会被整个清掉，重要数据先备份）；
3. 每个 terrain round：换种子再生成地形 → `ScenePointCloudSaver` 用 VoxelGenerator 保存 `terrain.ply` + `tree.ply`；
4. 每个 poisson round：`ImageSampler` 把小车刚体设为 kinematic 逐点传送，等物理帧后手动触发相机抓帧，保存 **EXR 深度图**（只有深度，没有 RGB/语义）；
5. 每轮图片的"文件名 + 位姿"记入 `data.toml`（位姿已转成右手系：`startPos=(z,-x)`、`yaw=360-yaw`，并记录深度相机 far clip 和水平 FOV）。

**输出目录**：`YOPO-Sim-Project/TrainingData/`（或 `TestingData/`）`/Scene_{i}/`，内含：

```
Scene_0/
├── Textures/depth_0.exr, depth_1.exr, ...
├── data.toml        # 每张图的位姿 + 相机参数
├── terrain.ply      # 地形点云
└── tree.ply         # 树点云
```

**EvaluationScene 的两个场景切换按钮**（"EvaluationScene" / "DataGenerationScene"）就是运行时切场景，等价于 File → Open Scene，只是方便。

---

## 5. 目标点、GoalPublisher 与 RViz 操作

### 5.1 谁在发 `/goal_pose`

一共有三个可能的发布者，同一时刻**应该只有一个在活动**：

1. **RViz 工具栏 "2D Goal Pose"**：你点一下就发一次（frame "map"）——日常评测用这个；
2. **Unity 的 GoalPublisher 物体**：激活时**每 1 秒**把自己的坐标 (136.7, 0.71, 125.4) 发到 `/goal_pose`。它的用途是"不用 RViz、自动给固定目标"的演示/回归测试。**只要它激活着，你在 RViz 点的目标 1 秒内就会被它覆盖**——这就是之前"点新点跳回旧点"的原因。**当前它已是禁用状态**（场景里 `m_IsActive: 0`），不要再去开它，除非你就想要固定目标；
3. `waypoint_tracker_node.py`：按硬编码航点列表 1Hz 重发目标——**不在仿真 launch 里**，正常不会遇到。

**订阅侧**：`yopo_node.py` 收到 `/goal_pose` 后转 map 系取 (x, y)，**直接覆盖式存储**喂给网络（到达容差 4m，没有"取消目标"机制，重选 = 覆盖）；`mpc_node.py` 也订阅，目标跳变超阈值时重置 MPC 内部状态。

### 5.2 2D Pose Estimate vs 2D Goal Pose（为什么有时两个都要点）

| RViz 工具 | 发到 | 谁接收 | 实际作用 |
|---|---|---|---|
| **2D Pose Estimate** | `/initialpose` | 只有 `mpc_node.py` | 置 `_reset_init_pose_flag`、**启动/重置 MPC**。仿真里**没有任何东西**用它重置小车位姿（Unity 全工程不订阅这个话题） |
| **2D Goal Pose** | `/goal_pose` | yopo_node + mpc_node | 设置导航目标 |

所以"两个都要点车才动"的原因是：**MPC 在收到 `/initialpose` 之前不工作**——它是 MPC 的"启动键"。正确手势：

1. 先点 **2D Pose Estimate**，在小车当前位置附近随便点一下（只是激活 MPC，位置内容在仿真里没人用）；
2. 再点 **2D Goal Pose** 给真正的目标。

已经跑起来之后想换目标，只点 2D Goal Pose 就行。点了 Goal 车不动时，补点一下 Pose Estimate 基本都能解决。

### 5.3 RViz 画面与 Unity 的对应关系

- RViz Fixed Frame = `map`，与 Unity 世界原点重合（换算公式见 3.3）；
- RViz 里的小车模型、绿色候选轨迹（PredPath）、参考路径（RefPath）、紫色 LocalGoal 都在 map 系；网格默认 1m 一格；
- 你在 RViz 点 (x, y) → Unity 里的实际位置是 `(Unity.x, Unity.z) = (-y, x)`。反过来想知道"Unity 里那棵树/水坑在 RViz 哪"：`ROS.x = Unity.z`，`ROS.y = -Unity.x`；
- 你在 Unity Inspector 里看到的目标点坐标（比如 GoalPublisher 的 Position 136.7/0.71/125.4）是 **Unity 坐标**，别直接拿去 RViz 对照，先换算；
- RViz 里目标的朝向箭头对 YOPO 没有意义——`yopo_node` 只取 x/y，yaw 只有 mpc_node 用（到达后的朝向）。

### 5.4 选错目标怎么办

- 直接重新点一次 2D Goal Pose 即可，新目标**覆盖**旧目标，无需取消；
- 点到地图外也没关系：YOPO 会朝那个方向规划，走到地形边界自然走不动；想让它停，把目标点到小车当前位置附近（< 4m 容差内）就等于"到达即停"。

---

## 附：相关文件索引

| 内容 | 路径 |
|---|---|
| 树密度切换（已修复） | `YOPO-Sim-Project/Assets/Scripts/UI/TreeDensitySetter.cs` |
| 随机水坑（已实现） | `YOPO-Sim-Project/Assets/Scripts/PuddleRandomizer.cs` |
| 数据生成主流程 | `YOPO-Sim-Project/Assets/Scripts/DataGenerationManager.cs` |
| 采样拍照 | `YOPO-Sim-Project/Assets/Scripts/ImageSampler.cs`、`PoissonGenerator.cs` |
| 点云保存 | `YOPO-Sim-Project/Assets/Scripts/ScenePointCloudSaver.cs` |
| 输出路径/位姿记录 | `YOPO-Sim-Project/Assets/Scripts/FilePathManager.cs`、`TomlDataManager.cs` |
| 规划节点 | `YOPO-Rally/src/ros_pkgs/diablo_ros_wrapper/diablo_ros_wrapper/yopo_node.py` |
| MPC 节点 | 同上目录 `mpc_node.py` |
| RViz 配置 | `YOPO-Rally/src/ros_pkgs/diablo_ros_wrapper/configs/visualize.rviz` |
| M0-M4 实验节点 | `yopo-vlm-sys2/eval/m0_passthrough.py` ~ `m4_recovery.py` |
