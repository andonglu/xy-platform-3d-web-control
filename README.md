# XY+Z 三轴运动平台 · Web 3D 控制系统（阶段一 MVP）

Web 端 Three.js 3D 可视化 + C# .NET 8 运动控制上位机，经 WebSocket 实时联动。
未启动上位机时，Web 端自动回落到浏览器内置模拟控制器，可独立演示。

## 目录结构

```
ThreeD/
├─ app/                        Web 前端 (Vite + Three.js)
│  ├─ index.html               控制面板布局
│  └─ src/
│     ├─ platform.js           3D 平台模型 (底座/Y桥/X滑板/Z立柱/激光头/光束)
│     ├─ trail.js              工作台面轨迹烧录 (CanvasTexture)
│     ├─ paths.js              2D 插补路径 (直线/圆弧, 弧长参数化) + 演示图案
│     ├─ sim.js                内置模拟控制器 (与 C# 端同协议)
│     ├─ link.js               WebSocket 链路 (断线自动回落内置模拟)
│     ├─ ui.js                 面板绑定 (状态渲染/点动/滑块/视角)
│     └─ main.js               场景/灯光/相机/主循环
└─ server/
   ├─ build.bat                编译 (补齐本机 NuGet 所需环境变量)
   ├─ run.bat                  启动上位机
   └─ MotionControl/           C# .NET 8 上位机
      ├─ Program.cs            入口: 120Hz 控制环, 30Hz 状态广播
      ├─ Core/
      │  ├─ IAxisCard.cs            轴卡抽象 (模拟卡/真卡统一接口)
      │  ├─ SimulatedAxisCard.cs    模拟轴卡 (三轴梯形速度规划)
      │  ├─ GtsGenAxisCard.cs       固高 GEN EtherCAT 预留骨架 (P/Invoke)
      │  ├─ MotionController.cs     运动控制器 (点动/定位/回零/作业/急停)
      │  ├─ DemoJobs.cs             演示作业 (五角星/整圆/综合图案)
      │  └─ Interpolation/
      │     └─ InterpolationPlanner.cs  IInterpolationPlanner 统一抽象
      │                               (2D 直线/圆弧, 预留空间直线/螺旋线)
      └─ Net/WsServer.cs       WebSocket 服务 (HttpListener, 零第三方依赖)
```

## 运行

```bash
# 1. Web 前端
cd app && npm install && npm run dev     # 浏览器打开终端提示的地址

# 2. C# 上位机 (可选; 不启动则前端用内置模拟)
server\build.bat                          # 编译
server\run.bat                            # 启动, 监听 ws://localhost:8520/ws
```

前端面板点「连接」即接入上位机；徽标显示 `上位机已连接` / `内置模拟`。

## 通信协议 (WebSocket + JSON)

下行 (上位机 → Web, 状态 30Hz):

```json
{ "type": "state", "t": 12.34,
  "x": { "pos": 0.0, "vel": 0.0, "state": "Idle" },
  "y": { "pos": 0.0, "vel": 0.0, "state": "Idle" },
  "z": { "pos": 0.0, "vel": 0.0, "state": "Idle" },
  "laser": false, "mode": "Idle" }
{ "type": "path", "name": "star", "points": [[x, y], ...] }
{ "type": "event", "level": "ok|warn|error", "msg": "..." }
{ "type": "hello", "server": "...", "ver": 1 }
```

上行 (Web → 上位机):

```json
{ "type": "home" }
{ "type": "ptp",  "axis": "x", "pos": 50 }
{ "type": "jog",  "axis": "x", "dir": 1 }        // dir=0 停止
{ "type": "laser", "on": true }
{ "type": "job",  "name": "star|circle|combo" }
{ "type": "estop" }
```

## 阶段一已实现

- 3D 平台：机械层级（Y 桥承载 X 导轨、X 滑板承载 Z 立柱、Z 滑块挂激光头），运动无穿模（激光头经悬臂前伸至横梁前方）；旋转/缩放/透视/俯视/正视/侧视
- 运动控制：单轴点动、滑块定位（PTP）、回零、急停（Faulted/复位）
- 2D 插补：直线（五角星）、圆弧（整圆、圆角矩形），梯形速度规划，轨迹实时烧录到台面贴图，青色预览线 = 规划路径
- 激光：手动开关 + 作业自动抬落笔（Z 8mm 工作 / 30mm 安全）
- 通信：WebSocket 30Hz 状态广播，前端指数平滑到帧率；断线自动回落内置模拟

## 阶段二/三预留

- `GtsGenAxisCard`：固高 GEN EtherCAT 真卡 P/Invoke 骨架（替换 `Program.cs` 中 `SimulatedAxisCard` 即可）
- `IInterpolationPlanner` 已含 `Line3DSegment` / `HelixSegment`（空间直线/螺旋线）
- 图案导入（SVG/DXF/G 代码）：解析为 `MotionSegment` 序列即可复用现有规划器与作业管线

## 验证记录

无头浏览器端到端（内置模拟与 C# 上位机两种模式均通过）：
零控制台错误；五角星/综合图案轨迹烧录像素验证通过；滑块 PTP 到位；
回零归零；急停进 Faulted 且可恢复；作业结束激光自动关闭；60fps（硬件渲染）。
