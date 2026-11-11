using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;

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
                    default:
                        Console.WriteLine("模式: boundary / slice <z> / project <z>");
                        break;
                }
                if (currentPaths is not null)
                {
                    Console.WriteLine($"路径生成: {currentPaths.Count} 条, 总点数 {currentPaths.Sum(p => p.Count)}");
                    foreach (var (p, i) in currentPaths.Select((p, i) => (p, i)))
                        Console.WriteLine($"  [{i}] {p.Count} 点, 长度 {PathLength(p):F1}mm");
                }
                break;

            case "upload":
                if (currentPaths is null || currentPaths.Count == 0) { Console.WriteLine("请先 gen 路径"); break; }
                var feed = parts.Length >= 2 ? double.Parse(parts[1]) : 80;
                var name = $"job_{DateTime.Now:HHmmss}";
                await client.UploadPathsAsync(name, currentPaths, feed);
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
      pos <x> <y> <z>            设置模型位置 (mm)
      rot <rx> <ry> <rz>         设置模型旋转 (度)
      scale <s>                  设置缩放
      gen boundary               从边界生成 3D 路径
      gen slice <z>              在 Z 高度切片生成路径
      gen project <z>            边界投影到 XY 平面 (Z 固定)
      preview                    预览当前路径
      upload [feed]              上传路径到服务端 (feed=进给速度 mm/s)
      run                        发送执行指令
      home                       回零
      estop                      急停
      reset                      复位撞机报警
      help                       显示帮助
      exit                       退出
    """);
}
