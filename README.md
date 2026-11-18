# XY+Z 三轴运动平台 · 硬件—服务—平台—脚本

Three.js 3D 虚拟设备 + Node.js Web 服务 + WPF 上位机。参照 `EquipmentShell` 的硬件 → 服务 → 平台方式分层，并提供 C# `.csx` 脚本。当前默认使用 3D 虚拟设备，不要求真实轴卡。

## 当前架构

```text
WPF / 脚本
    ↓ 仅调用 ILaserPlatform / ILaserMachineScriptApi
LaserPlatform（业务动作、互锁与完成等待、操作租约）
    ↓
LaserEquipmentService（组合双卡、路由命令、合并强类型快照）
    ↓
    ├─ IMotionAxisCardPort
    │   ├─ VirtualAxisCardPort → /ws/motion → 3D 虚拟轴卡
    │   └─ RealAxisCardPort    → 真实运动控制卡/网关
    └─ ILaserControlPort
        ├─ VirtualLaserControlPort → /ws/laser → 3D 虚拟激光控制卡
        └─ RealLaserControlPort    → 真实激光控制卡/网关
```

WPF 不再直接发送 WebSocket JSON。脚本也不能接触 Port、传输协议或真实设备 API，只能通过受限 `Machine` 门面调用平台动作。切换硬件通道时，服务、平台、WPF 和脚本无需修改。

## 快速启动

### 1. 启动 Web 服务

```powershell
cd D:\Lry\ThreeD\app
npm install
npm run build
npm start
```

启动后：

- Web 页面：http://127.0.0.1:8080
- 上位机 WebSocket：`ws://127.0.0.1:8080/ws`
- 健康检查：http://127.0.0.1:8080/health

可通过 `PORT` 和 `HOST` 环境变量修改监听地址。对外提供局域网访问时，例如设置 `HOST=0.0.0.0`，然后上位机连接运行机器的 IP。

### 2. 启动 WPF 上位机（推荐）

直接双击 `server\run-wpf.bat`，或运行：

```powershell
cd D:\Lry\ThreeD
server\run-wpf.bat
```

脚本会编译并打开 **“激光设备上位机 · WPF”** 窗口。默认地址为 `ws://127.0.0.1:8080/ws`，窗口打开后自动连接。

WPF 窗口提供：连接管理、三轴位置/速度/状态、PTP、点动、回零、急停、激光开关、独立作业、单件/连续生产、生产进度和运行日志。点击“打开 3D 页面”可同时查看浏览器中的三维动作；点击“脚本”可打开 C# 脚本编辑器。

### 3. 使用控制台上位机（可选）

原控制台客户端仍保留：

```powershell
cd D:\Lry\ThreeD
server\build.bat
server\run-web-client.bat
```

交互命令：

```text
home
star / circle / combo
laser on / laser off
ptp x 20
jog x 1
auto star [数量]
continuous [star|circle|combo]
stop
reset
estop
help / quit
```

也可以执行一次性命令并观察 20 秒：

```powershell
server\run-web-client.bat --command "auto star 1" --watch-seconds 20
```

浏览器会先显示连接状态，然后呈现由上位机命令触发的完整 3D 生产流程。

## 激光自动上下料

- **单件自动加工**：料仓出料 → 推料上料 → 定位夹紧 → 激光加工 → 松夹 → 推料下料 → 推杆回程。
- **连续生产**：自动加工后续工件；`production_stop` 在当前工件加工并下料后停止。
- **急停**：关闭激光并冻结工件、推杆和夹具；`production_reset` 后可回零并重新启动。
- **工件结果**：每件工件拥有独立 Canvas 加工贴图，图案随成品下料，新工件不会继承旧轨迹。
- **远程控制**：浏览器按钮与上位机命令调用同一服务端控制器，不生成两份生产现场。

## 硬件通道与真实设备接入

唯一通道装配点位于 `MotionControl.Wpf\EquipmentApplication.cs`，配置位于：

```text
MotionControl.Wpf\Config\equipment.json
```

默认配置：

```json
{
  "activeChannel": "virtual",
  "virtualMotionEndpoint": "ws://127.0.0.1:8080/ws/motion",
  "virtualLaserEndpoint": "ws://127.0.0.1:8080/ws/laser",
  "realMotionEndpoint": "ws://127.0.0.1:8521/ws/motion",
  "realLaserEndpoint": "ws://127.0.0.1:8521/ws/laser"
}
```

- `virtual`：分别创建 `VirtualAxisCardPort` 和 `VirtualLaserControlPort`，连接本项目的两个虚拟硬件通道。
- `real`：分别创建 `RealAxisCardPort` 和 `RealLaserControlPort`，连接真实轴卡与激光控制卡网关。网关需要提供相同的强类型命令/状态语义；厂商设备也可以实现对应 Port 接口接入。
- 上层服务、平台、脚本和 WPF 不读取 `activeChannel`，也不根据真实/虚拟执行不同逻辑。

当前没有连接物理硬件；真实双卡通信通道已经提供，具体厂商 SDK/PLC/轴卡驱动在各自 Port 适配层实现，不需要改上层。

## C# 脚本功能

WPF 主窗口点击“脚本”，支持：

- 扫描 `Scripts\Stage\**\*.csx` 和 `Scripts\Machine\**\*.csx`；
- 编辑器修改、原子保存、备份和 Roslyn 编译检查；
- 运行、重新扫描、停止并急停、脚本日志与状态显示；
- 脚本运行期间持有 `MachineOperationSource.Script` 独占租约，手动操作与其他脚本不能并发占用设备；
- 脚本只能通过 `ILaserMachineScriptApi` 调用平台，不能绕过平台直接访问硬件。

示例：

```csharp
Log("开始自动加工");
var started = await Machine.StartProductionAsync("star", 1, CancellationToken);
if (!started.Succeeded) throw new Exception(started.Message);
var completed = await Machine.WaitForProductionCompleteAsync(CancellationToken);
if (!completed.Succeeded) throw new Exception(completed.Message);
Log("加工完成");
```

脚本内还可使用 `Machine.HomeAsync`、`MoveAxisAsync`、`RunRecipeAsync`、`WaitForIdleAsync`、`GetSnapshot`、`ResetProductionAsync`、`EmergencyStopAsync`、`DelayAsync` 和 `CancellationToken`。这些 `.csx` 按受信任的本地设备脚本管理，不作为不可信代码沙箱。

## WebSocket 协议

通道地址，均为 JSON 文本帧：

- `ws://<Web服务地址>:8080/ws/motion`：轴卡通道，接受 `home / ptp / jog / job / production_* / estop`。
- `ws://<Web服务地址>:8080/ws/laser`：激光控制卡通道，只接受 `laser`。
- `ws://<Web服务地址>:8080/ws`：原兼容通道，保留全部命令，浏览器继续使用。

连接后可注册角色；角色用于连接统计，不限制控制命令：

```json
{ "type": "register", "role": "host" }
{ "type": "register", "role": "browser" }
```

### 上位机或浏览器 → Web 服务

```json
{ "type": "home" }
{ "type": "ptp", "axis": "x", "pos": 50 }
{ "type": "jog", "axis": "x", "dir": 1 }
{ "type": "laser", "on": true }
{ "type": "job", "name": "star" }
{ "type": "production_start", "recipe": "star", "count": 1 }
{ "type": "production_start", "recipe": "star", "count": 0 }
{ "type": "production_stop" }
{ "type": "production_reset" }
{ "type": "estop" }
```

`count: 0` 表示连续生产；单件或多件使用 `1~999`。非法 JSON、未知指令和无效参数会返回错误事件，不会进入控制器。

### Web 服务 → 所有客户端

```json
{ "type": "hello", "server": "ThreeD Web Motion Service", "ver": 3 }
{ "type": "path", "name": "star", "points": [[0, 0]] }
{ "type": "event", "level": "ok", "msg": "..." }
{
  "type": "state",
  "x": { "pos": 0, "vel": 0, "state": "Idle" },
  "y": { "pos": 0, "vel": 0, "state": "Idle" },
  "z": { "pos": 30, "vel": 0, "state": "Idle" },
  "laser": false,
  "mode": "Idle",
  "production": {
    "phase": "Complete",
    "running": false,
    "workpieceId": "P001",
    "workpieceX": 200,
    "pusherX": -305,
    "clamp": 0,
    "phaseProgress": 1,
    "completed": 1
  }
}
```

服务端包含 30 秒 WebSocket 心跳、慢客户端状态包保护和最大 64KB 指令限制。

## 开发模式

终端 1，启动带自动重载的 Web 服务：

```powershell
cd app
npm run dev:server
```

终端 2，启动 Vite：

```powershell
cd app
npm run dev
```

Vite 会把页面的 `/ws` 和 `/health` 请求代理到 `127.0.0.1:8080`。

## 测试与验证

```powershell
cd app
npm test
cd ..\server
dotnet build MotionControl.Wpf\MotionControl.Wpf.csproj --disable-build-servers
dotnet run --project MotionControl.Verification\MotionControl.Verification.csproj
```

当前验证结果：

- 8 项 Node 测试通过：三种加工图案、单件/连续生产、停止、急停复位、指令互锁、上位机与浏览器 WebSocket 集成、轴卡/激光控制卡独立通道。
- 硬件抽象、虚拟/真实双卡适配器、设备平台、脚本库和 WPF 联合构建通过。
- `MotionControl.Verification` 分别验证虚拟轴卡与虚拟激光控制卡的独立命令通道，再使用相同服务、平台与脚本通过真实协议双卡完成加工。
- 脚本生产、取消与急停验证通过；生产完成等待会排除上一轮 `Complete` 快照。
- WPF 脚本窗口实测通过：打开脚本编辑器并运行 `HomeAndMark.csx`，服务端完成一件加工，窗口显示“脚本执行完成”。
- 三端端到端验证通过：C# 上位机发送 `auto star 1` → Node 服务执行 → 远程 Edge 浏览器完成 3D 上料、加工、下料。

## 目录说明

```text
app/
├─ server/index.mjs          Node HTTP/WebSocket 服务
├─ src/
│  ├─ sim.js                 服务端共享运动与生产模拟控制器
│  ├─ production.js          自动生产节拍状态机
│  ├─ machine.js             服务端/浏览器共享轴行程常量
│  ├─ paths.js               直线、圆弧与演示加工程序
│  ├─ cell.js                工件、推杆、夹具和输送线动画
│  ├─ platform.js            三轴平台与激光模型
│  ├─ trail.js               台面/工件独立加工贴图
│  ├─ link.js                浏览器远程连接与断线重连
│  └─ main.js / ui.js        场景和面板
└─ tests/                    模拟控制与 Web 服务测试
server/
├─ run-wpf.bat                       编译并启动 WPF 上位机
├─ MotionControl.Hardware.Abstractions/  轴卡/激光控制卡契约、命令、快照、结果
├─ MotionControl.Hardware.Adapters/      虚拟/真实轴卡、虚拟/真实激光控制卡、通道工厂
├─ MotionControl.Equipment/             服务、平台、操作租约、受限脚本 API
├─ MotionControl.Scripting/             CSX 发现、编译、运行、保存、停止
├─ MotionControl.Verification/          通道替换与脚本端到端验证
├─ MotionControl.Wpf/                  WPF、装配配置、脚本窗口、示例脚本
└─ MotionControl/                      原控制台客户端/独立服务端（保留）
```

原 `server\run.bat` 和 `GtsGenAxisCard` 仅作为旧独立模式及真实硬件扩展骨架保留；当前 Web 服务运行不需要它们，也不依赖真实设备。
