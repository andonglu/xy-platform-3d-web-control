using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;
using DispenseHost.PatternPlan;
using DispenseHost.Testing;

// ---------------------------------------------------------------------------
// 3D 点胶上位机 (DispenseHost)
//   功能:
//     1. 导入 STL CAD 文件
//     2. 设置模型位置/朝向/缩放
//     3. 生成点胶路径 (边界/切片/投影)
//     4. 上传路径到服务端 (ws://localhost:8530)
//     5. 发送执行/回零/急停/复位指令
//   与激光项目的上位机完全分离, 可同时运行。
// ---------------------------------------------------------------------------

Console.WriteLine("=== 3D 点胶上位机 (DispenseHost) ===");
Console.WriteLine();

var client = new DispenseClient();
client.OnLog += msg => Console.WriteLine($"[client] {msg}");
client.OnEvent += msg => Console.WriteLine($"[event] {msg}");
client.OnCollision += msg => Console.WriteLine($"!! 撞机报警 !! {msg}");
client.OnPrograms += list =>
{
    Console.WriteLine($"脱机程序 {list.Length} 个:");
    foreach (var p in list)
        Console.WriteLine($"  #{p.Id} {p.Name}  ({p.StepCount} 步 / {p.PointCount} 点, {p.SavedUtc:MM-dd HH:mm})");
};

Mesh? currentMesh = null;
Mesh? transformedMesh = null;
var transform = new ModelTransform();
List<List<Point3D>>? currentPaths = null;

// 自动连接服务端
try
{
    await client.ConnectAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"连接服务端失败: {ex.Message}");
    Console.WriteLine("请先启动 server\\run.bat (DispenseServer)");
}

Console.WriteLine();
PrintHelp();

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;                      // EOF (管道输入结束)
    if (string.IsNullOrWhiteSpace(line)) continue;
    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();

    try
    {
        switch (cmd)
        {
            case "import" or "load":
                if (parts.Length < 2) { Console.WriteLine("用法: import <stl文件路径>"); break; }
                currentMesh = StlImporter.Import(parts[1]);
                var bb = currentMesh.BoundingBox();
                Console.WriteLine($"导入成功: {currentMesh.Triangles.Count} 三角形");
                Console.WriteLine($"  包围盒: ({bb.Min.X:F1},{bb.Min.Y:F1},{bb.Min.Z:F1}) → ({bb.Max.X:F1},{bb.Max.Y:F1},{bb.Max.Z:F1})");
                Console.WriteLine($"  尺寸: {bb.Max.X - bb.Min.X:F1} x {bb.Max.Y - bb.Min.Y:F1} x {bb.Max.Z - bb.Min.Z:F1} mm");
                transformedMesh = transform.ApplyTo(currentMesh);
                break;

            case "pos":
                if (parts.Length >= 4)
                {
                    transform.Position = new Point3D(
                        double.Parse(parts[1]), double.Parse(parts[2]), double.Parse(parts[3]));
                    ApplyTransform();
                }
                else Console.WriteLine($"当前位置: ({transform.Position.X}, {transform.Position.Y}, {transform.Position.Z})");
                break;

            case "rot":
                if (parts.Length >= 4)
                {
                    transform.RotationDeg = new Point3D(
                        double.Parse(parts[1]), double.Parse(parts[2]), double.Parse(parts[3]));
                    ApplyTransform();
                }
                else Console.WriteLine($"当前旋转: ({transform.RotationDeg.X}, {transform.RotationDeg.Y}, {transform.RotationDeg.Z}) 度");
                break;

            case "scale":
                if (parts.Length >= 2)
                {
                    transform.Scale = double.Parse(parts[1]);
                    ApplyTransform();
                }
                else Console.WriteLine($"当前缩放: {transform.Scale}");
                break;

            case "importdxf":
            case "loaddxf":
            {
                if (parts.Length < 2) { Console.WriteLine("用法: importdxf <dxf文件路径> [默认Z]"); break; }
                double dz = parts.Length >= 3 ? double.Parse(parts[2]) : 8;
                int skipped = 0;
                var opts = new DxfImportOptions { DefaultZ = dz, OnSkipped = n => skipped = n };
                var entities = DxfImporter.Import(parts[1], opts);
                currentPaths = entities.Select(e => e.Points).ToList();
                Console.WriteLine($"DXF 导入: {entities.Count} 条路径{(skipped > 0 ? $", 跳过 {skipped} 个不支持实体" : "")}");
                foreach (var (e, i) in entities.Select((e, i) => (e, i)))
                    Console.WriteLine($"  [{i}] 图层 {e.Layer}, 闭合={e.IsClosed}, {e.Points.Count} 点, Z={e.Points[0].Z:F1}");
                if (skipped > 0) Console.WriteLine("  (SPLINE/ELLIPSE/INSERT 等实体暂不支持, 导入前请 exploding 为折线/圆弧)");
                break;
            }

            case "gen":
            case "generate":
                if (transformedMesh is null) { Console.WriteLine("请先 import STL"); break; }
                var gen = new PathGenerator();
                var mode = parts.Length >= 2 ? parts[1] : "boundary";
                switch (mode)
                {
                    case "boundary":
                        currentPaths = gen.GenerateBoundaryPaths(transformedMesh, zOffset: 0, simplifyTol: 0.2);
                        break;
                    case "slice":
                        var z = parts.Length >= 3 ? double.Parse(parts[2]) : 10;
                        currentPaths = gen.GenerateSlice(transformedMesh, z);
                        break;
                    case "project":
                        var bz = parts.Length >= 3 ? double.Parse(parts[2]) : 8;
                        var bp = gen.GenerateBoundaryPaths(transformedMesh, 0, 0.2);
                        currentPaths = gen.ProjectToXY(bp, bz);
                        break;
                    case "layers":
                    {
                        // 多 Z 分层切片: gen layers <zStart> <zEnd> <dz>
                        if (parts.Length < 5) { Console.WriteLine("用法: gen layers <zStart> <zEnd> <dz>"); break; }
                        var layers = gen.GenerateLayeredSlice(transformedMesh,
                            double.Parse(parts[2]), double.Parse(parts[3]), double.Parse(parts[4]));
                        currentPaths = layers.SelectMany(l => l.Chains).ToList();
                        Console.WriteLine($"分层切片: {layers.Count} 层, 共 {currentPaths.Count} 条轮廓");
                        foreach (var (l, i) in layers.Select((l, i) => (l, i)))
                            Console.WriteLine($"  层[{i}] Z={l.Z:F1}: {l.Chains.Count} 条, 总点数 {l.Chains.Sum(c => c.Count)}");
                        break;
                    }
                    default:
                        Console.WriteLine("模式: boundary / slice <z> / project <z> / layers <z0> <z1> <dz>");
                        break;
                }
                if (currentPaths is not null)
                {
                    Console.WriteLine($"路径生成: {currentPaths.Count} 条, 总点数 {currentPaths.Sum(p => p.Count)}");
                    foreach (var (p, i) in currentPaths.Select((p, i) => (p, i)))
                        Console.WriteLine($"  [{i}] {p.Count} 点, 长度 {PathLength(p):F1}mm");
                }
                break;

            case "pattern":
            {
                // 平面图案测试: 圆形 / 矩形 / 组合, 不依赖 STL 文件
                var kind = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "circle";
                var patternFeed = parts.Length >= 3 ? double.Parse(parts[2]) : 60;
                var job = new PatternJob();
                if (kind is "circle" or "both")
                    job.Add(PatternPlanner.Plan(new PatternSpec
                    {
                        Kind = PatternKind.Circle, Name = "圆形 R30",
                        Radius = 30, Segments = 72, Z = PatternPlanner.WorkHeight, Feed = patternFeed,
                    }));
                if (kind is "rect" or "rectangle" or "both")
                    job.Add(PatternPlanner.Plan(new PatternSpec
                    {
                        Kind = PatternKind.Rectangle, Name = "矩形 80x50",
                        Width = 80, Height = 50, Z = PatternPlanner.WorkHeight, Feed = patternFeed,
                    }));
                if (job.Patterns.Count == 0) { Console.WriteLine("模式: circle / rect / both"); break; }
                currentPaths = job.Patterns.Select(p => p.Points).ToList();
                Console.WriteLine($"平面图案已生成: {job.Patterns.Count} 段, {job.TotalPoints} 点, 总长 {job.TotalLength:F1}mm");
                foreach (var p in job.Patterns)
                    Console.WriteLine($"  {p.Name}: {p.PointCount} 点, {p.Length:F1}mm, 闭合={p.IsClosed}");
                break;
            }

            case "selftest":
            {
                var runner = new SelfTestRunner(client);
                runner.OnLog += m => Console.WriteLine(m);
                var report = await runner.RunAsync();
                Console.WriteLine();
                Console.WriteLine(report.ToText());
                break;
            }

            case "upload":
                if (currentPaths is null || currentPaths.Count == 0) { Console.WriteLine("请先 gen/importdxf 路径"); break; }
                var feed = parts.Length >= 2 ? double.Parse(parts[1]) : 80;
                // 可选: upload [feed] [earlyCloseMs] [closeDelayMs] —— 演示 P1 指令级出胶时序
                var timing = parts.Length >= 3
                    ? new DispenseTiming(
                        EarlyCloseMs: double.Parse(parts[2]),
                        CloseDelayMs: parts.Length >= 4 ? double.Parse(parts[3]) : 0,
                        RetractMm: 2)
                    : null;
                var name = $"job_{DateTime.Now:HHmmss}";
                await client.UploadStepsAsync(name, JobAssembler.Assemble(currentPaths, feed, timing: timing));
                break;

            case "program":
            {
                var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
                switch (sub)
                {
                    case "save":
                        if (currentPaths is null || currentPaths.Count == 0 || parts.Length < 3)
                        { Console.WriteLine("用法: program save <名称>  (需先生成/导入路径)"); break; }
                        await client.ProgramSaveAsync(parts[2],
                            JobAssembler.Assemble(currentPaths, 80,
                                timing: new DispenseTiming(EarlyCloseMs: 200, CloseDelayMs: 100, RetractMm: 2)));
                        break;
                    case "list": await client.ProgramListAsync(); break;
                    case "load": if (parts.Length >= 3) await client.ProgramLoadAsync(int.Parse(parts[2])); break;
                    case "run": if (parts.Length >= 3) await client.ProgramRunAsync(int.Parse(parts[2])); break;
                    case "delete": if (parts.Length >= 3) await client.ProgramDeleteAsync(int.Parse(parts[2])); break;
                    default: Console.WriteLine("用法: program save <名> / list / load <id> / run <id> / delete <id>"); break;
                }
                break;
            }

            case "io":
                if (parts.Length >= 3)
                    await client.IoWriteAsync(parts[1].ToLowerInvariant(),
                        parts[2] is "on" or "1" or "true");
                else Console.WriteLine("用法: io <start|pause|reset|sel0..sel3> <on|off>");
                break;

            case "iodefs":
                await client.IoListAsync();
                break;

            case "run":
                await client.RunJobAsync();
                break;

            case "home":
                await client.HomeAsync();
                break;

            case "estop":
                await client.EstopAsync();
                break;

            case "reset":
                await client.ResetAlarmAsync();
                break;

            case "preview":
                if (currentPaths is null) { Console.WriteLine("无路径"); break; }
                foreach (var (p, i) in currentPaths.Select((p, i) => (p, i)))
                {
                    Console.WriteLine($"  路径[{i}] ({p.Count} 点):");
                    foreach (var pt in p.Take(5))
                        Console.WriteLine($"    ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2})");
                    if (p.Count > 5) Console.WriteLine($"    ... 还有 {p.Count - 5} 点");
                }
                break;

            case "help":
                PrintHelp();
                break;

            case "exit" or "quit":
                client.Dispose();
                return;

            default:
                Console.WriteLine($"未知指令: {cmd} (输入 help 查看帮助)");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
}

void ApplyTransform()
{
    if (currentMesh is null) return;
    transformedMesh = transform.ApplyTo(currentMesh);
    var bb = transformedMesh.BoundingBox();
    Console.WriteLine($"变换后包围盒: ({bb.Min.X:F1},{bb.Min.Y:F1},{bb.Min.Z:F1}) → ({bb.Max.X:F1},{bb.Max.Y:F1},{bb.Max.Z:F1})");
}

static double PathLength(List<Point3D> path)
{
    double len = 0;
    for (int i = 0; i < path.Count - 1; i++)
        len += (path[i + 1] - path[i]).Length;
    return len;
}

static void PrintHelp()
{
    Console.WriteLine("""
    指令:
      import <path.stl>          导入 STL 文件
      importdxf <path.dxf> [z]   导入 DXF (LINE/CIRCLE/ARC/LWPOLYLINE, 默认 Z=8)
      pos <x> <y> <z>            设置模型位置 (mm)
      rot <rx> <ry> <rz>         设置模型旋转 (度)
      scale <s>                  设置缩放
      gen boundary               从边界生成 3D 路径
      gen slice <z>              在 Z 高度切片生成路径
      gen project <z>            边界投影到 XY 平面 (Z 固定)
      gen layers <z0> <z1> <dz>  多 Z 分层切片 (3D 打印式涂胶)
      pattern circle [feed]      生成平面圆形图案 (R30, Z=8)
      pattern rect [feed]        生成平面矩形图案 (80x50, Z=8)
      pattern both [feed]        生成圆形+矩形组合图案
      selftest                   自动测试: 圆形/矩形/组合, 输出偏差报告
      preview                    预览当前路径
      upload [feed] [earlyClose] [closeDelay]
                                 上传路径 (后两项演示 P1 出胶时序, 单位 ms)
      program save <名>          当前路径存为脱机程序 (带 P1 时序)
      program list               列出服务端脱机程序
      program run <id>           装载并运行脱机程序
      program load/delete <id>   装载 / 删除脱机程序
      io <点> <on|off>           写虚拟输入点 (start/pause/reset/sel0..3)
      iodefs                     查看 IO 点定义
      run                        发送执行指令
      home                       回零
      estop                      急停
      reset                      复位撞机报警
      help                       显示帮助
      exit                       退出
    """);
}
