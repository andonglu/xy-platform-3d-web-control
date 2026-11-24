using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;

namespace DispenseHost.Verification;

/// <summary>P9 数学、工程、区域及客户端协议回归验证。</summary>
internal static class P9Verification
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void RunCore()
    {
        VerifyMath();
        VerifyProjectAndFingerprint();
        VerifyRegions();
    }

    private static void VerifyMath()
    {
        var placement = new Pose3D(10, 0, 0, 0, 0, 90);
        var fixture = new Pose3D(0, 20, 0, 0, 0, 90);
        var point = WorkpieceTransform.Apply(new Point3D(1, 2, 3), fixture, placement, 2);
        Near(point, new Point3D(-2, 26, 6), 1e-8, "工件先 placement 后 fixture");
        var normal = WorkpieceTransform.ApplyNormal(new Point3D(1, 0, 0), fixture, placement);
        Near(normal, new Point3D(-1, 0, 0), 1e-8, "法向只旋转");
        Near(WorkpieceTransform.ApplyNormal(new Point3D(1, 0, 0), fixture, placement, flip: true),
            normal * -1, 1e-8, "全局法向反向");

        var scaled = new ModelTransform { Position = new Point3D(100, 0, 0), Scale = 25.4 };
        Near(scaled.ApplyNormal(new Point3D(0, 0, 4)), new Point3D(0, 0, 1), 1e-12,
            "单位换算和平移不影响法向");
    }

    private static void VerifyProjectAndFingerprint()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "dispense-p9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] stl = BinaryStl(2);
            var model = MakeModel(stl, "embedded-part.stl", "cm");
            Check(model.UnitToMm == 10, "cm 到 mm 换算");
            var mesh = StlImporter.ImportBytes(model.GetBytes());
            Check(mesh.Triangles.Count == 2, "内嵌模型直接读取");
            var settings = Settings();
            var path = new SurfacePath(
                [new Point3D(0, 0, 0), new Point3D(10, 0, 0)],
                [new Point3D(0, 0, 1), new Point3D(0, 0, 1)]);
            var item = new SurfaceJobItem("region", 60, new DispenseTiming(), path);
            var legacyPath = Path.Combine(dir, "legacy.dpsurf.json");
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(
                new SurfaceProject(1, "missing-external.stl", settings, [item])));
            var legacy = SurfaceProjectStore.Load(legacyPath);
            Check(legacy.Version == 1 && legacy.Model is null && legacy.Production is null, "V1 工程兼容读取");
            Check(ProductionFingerprint.IsCurrent(legacy, item), "无指纹旧工程保持兼容");

            var production = ProductionConfig.CreateDefault();
            var sourcedItem = item with { Source = RegionSource.Faces([0]), Settings = settings };
            string fingerprint = ProductionFingerprint.Compute(model, production, sourcedItem.Source, settings);
            var current = sourcedItem with { Fingerprint = fingerprint };
            var p9 = new SurfaceProject(2, "not-required.stl", settings, [current], model, production);
            string file = Path.Combine(dir, "production.dpsurf.json");
            SurfaceProjectStore.Save(file, p9);
            var loaded = SurfaceProjectStore.Load(file);
            Check(loaded.Version == 2 && loaded.Production == production, "生产配置完整往返");
            Check(loaded.Model is not null && StlImporter.ImportBytes(loaded.Model.GetBytes()).Triangles.Count == 2,
                "不依赖外部 STL 恢复内嵌模型");
            Check(ProductionFingerprint.IsCurrent(loaded, loaded.Items[0]), "当前指纹有效");
            var changed = production with { Placement = production.Placement with { Scale = 1.01 } };
            Check(!ProductionFingerprint.IsCurrent(loaded with { Production = changed }, loaded.Items[0]),
                "放置改变使旧路径指纹失效");
            var settingsChanged = current with { Settings = settings with { Feed = 55 } };
            Check(!ProductionFingerprint.IsCurrent(loaded, settingsChanged), "工艺参数改变使指纹失效");
            Check(Directory.GetFiles(dir, "*.tmp-*").Length == 0, "原子保存无临时文件残留");

            // 修改内嵌字节但不更新 SHA，加载必须拒绝。
            string json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            string encoded = doc.RootElement.GetProperty("Model").GetProperty("Base64").GetString()!;
            byte[] tampered = Convert.FromBase64String(encoded);
            tampered[^1] ^= 0x5a;
            string newEncoded = Convert.ToBase64String(tampered);
            File.WriteAllText(file, json.Replace(encoded, newEncoded));
            Throws<InvalidDataException>(() => SurfaceProjectStore.Load(file), "损坏内嵌模型被 SHA-256 拦截");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void VerifyRegions()
    {
        var generator = new PathGenerator();
        var mesh = StlImporter.ImportBytes(BinaryStl(2));
        var settings = Settings() with { NormalOffset = 0.5 };
        var boundary = generator.GenerateRegionPaths(mesh, RegionSource.Boundary(), settings);
        Check(boundary.Count == 1 && boundary[0].Points.Count >= 4, "整网边界串接");
        Check(boundary[0].Normals.Count == boundary[0].Points.Count, "边界逐点法向数量");
        Check(boundary[0].Normals.All(n => Math.Abs(n.Length - 1) < 1e-8), "边界法向单位化");
        Check(boundary[0].Points.All(p => Math.Abs(p.Z - 0.5) < 1e-8), "边界法向偏置");

        var faces = generator.GenerateRegionPaths(mesh, RegionSource.Faces([0]), settings);
        Check(faces.Count == 1 && faces[0].Points.Count == 4, "单三角面子集闭合边界");
        var vertical = WorkpieceTransform.Apply(mesh, new Pose3D(), new Pose3D(0, 0, 0, 90, 0, 0));
        var slice = generator.GenerateRegionPaths(vertical, RegionSource.Slice(),
            settings with { ZStart = 5, ZEnd = 5, NormalOffset = 0 });
        Check(slice.Count == 1, "区域 Slice 复用等高层生成");
        Throws<InvalidDataException>(() => generator.GenerateRegionPaths(mesh, RegionSource.Faces([99]), settings),
            "面索引越界验证");
        Throws<InvalidDataException>(() => generator.GenerateRegionPaths(mesh, RegionSource.Edges([999]), settings),
            "唯一边索引越界验证");
        var edges = generator.GenerateRegionPaths(mesh, RegionSource.Edges([0]), settings);
        Check(edges.Count == 1 && edges[0].Points.Count >= 2 && edges[0].Points.Count == edges[0].Normals.Count &&
              edges[0].Normals.All(n => Math.Abs(n.Length - 1) < 1e-8),
            "Edges 引用可生成路径且法向来自原始网格");
    }

    public static async Task RunClientAsync()
    {
        int port = FreePort();
        using var probe = new P9Probe(port);
        Task probeTask = probe.RunAsync();
        using var client = new DispenseClient($"ws://127.0.0.1:{port}/ws");
        var snapshotWait = new TaskCompletionSource<ProductionSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnProduction += s => snapshotWait.TrySetResult(s);
        await client.ConnectAsync();

        byte[] bytes = BinaryStl(2000);
        var model = MakeModel(bytes, "chunks.stl");
        await client.UploadModelAsync(model, TimeSpan.FromSeconds(3));
        var messages = probe.Messages.Select(m => JsonDocument.Parse(m)).ToArray();
        var begin = messages.Select(m => m.RootElement).First(r => ReadType(r) == "model_begin");
        Check(begin.GetProperty("cmd").GetString() == "model_begin" &&
              begin.GetProperty("totalBytes").GetInt64() == bytes.Length && begin.GetProperty("totalChunks").GetInt32() == 3,
            "model_begin 元数据与分片数");
        var chunks = messages.Select(m => m.RootElement).Where(r => ReadType(r) == "model_chunk")
            .OrderBy(r => r.GetProperty("index").GetInt32()).ToArray();
        Check(chunks.Length == 3 && chunks.Select(r => Convert.FromBase64String(r.GetProperty("data").GetString()!).Length)
            .SequenceEqual([49152, 49152, bytes.Length - 98304]), "原始字节 48KiB 分片再 Base64");
        byte[] joined = chunks.SelectMany(r => Convert.FromBase64String(r.GetProperty("data").GetString()!)).ToArray();
        Check(joined.AsSpan().SequenceEqual(bytes), "分片重组字节完全一致");
        foreach (var document in messages) document.Dispose();

        var production = ProductionConfig.CreateDefault();
        production = production with
        {
            FullProduction = true,
            Fixture = production.Fixture with { MachinePose = new Pose3D() },
            Placement = production.Placement with { RelativePose = new Pose3D() },
        };
        var steps = new List<DispenseClient.UploadStep>
        {
            new("dispense", [new Point3D(1, 2, 3), new Point3D(4, 5, 6)], 60, true,
                20, 10, 30, 5, [new Point3D(0, 0, 1), new Point3D(0, 0, 1)]),
        };
        await client.UploadProductionConfigAsync(production.ProjectId, 7, model.Id, production, steps,
            TimeSpan.FromSeconds(3));
        string configJson = probe.Messages.Last(m => ReadType(JsonDocument.Parse(m).RootElement) == "production_config");
        using (var configDoc = JsonDocument.Parse(configJson))
        {
            var root = configDoc.RootElement;
            Check(root.GetProperty("cmd").GetString() == "production_config" &&
                  root.GetProperty("revision").GetInt64() == 7 && root.GetProperty("modelId").GetString() == model.Id,
                "production_config 工程修订与模型引用");
            var config = root.GetProperty("config");
            Check(config.GetProperty("fullProduction").GetBoolean() &&
                  config.GetProperty("fixture").GetProperty("autoPartConfirm").GetBoolean() &&
                  config.GetProperty("placement").GetProperty("relativePose").GetProperty("x").GetDouble() == 0,
                "ProductionConfig camelCase 嵌套序列化");
            var step = root.GetProperty("steps")[0];
            Check(step.GetProperty("openDelayMs").GetDouble() == 20 && step.GetProperty("earlyCloseMs").GetDouble() == 10 &&
                  step.GetProperty("closeDelayMs").GetDouble() == 30 && step.GetProperty("retractMm").GetDouble() == 5 &&
                  step.GetProperty("normals").GetArrayLength() == 2, "生产步骤胶阀时序与法向序列化");
        }

        await client.ProductionStartAsync(production.ProjectId, 7);
        await client.ProductionConfirmAsync(production.ProjectId, 7, "PART_PRESENT");
        var snapshot = await snapshotWait.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(snapshot.Phase == "Dispensing" && snapshot.BatchCount == 3 && snapshot.WorkpiecePose.Z == 12,
            "生产快照事件完整反序列化");
        await ThrowsAsync<InvalidOperationException>(() => client.ProductionStopAsync(production.ProjectId, 7),
            "服务端失败 ACK 转成异常");
        await ThrowsAsync<TimeoutException>(() => client.UploadProductionConfigAsync(production.ProjectId, 99,
            model.Id, production, steps, TimeSpan.FromMilliseconds(60)), "ACK 超时明确失败");
        await client.DisconnectAsync();
        await Task.WhenAny(probeTask, Task.Delay(1000));
    }

    private static SurfaceProjectSettings Settings()
        => new(0, 0, 1, 0, 60, new DispenseTiming(), new ModelTransform(), 0, 0, 0.5, 30);

    private static ModelAsset MakeModel(byte[] bytes, string fileName, string unit = "mm")
    {
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new ModelAsset(sha[..16], fileName, unit, sha, Convert.ToBase64String(bytes));
    }

    private static byte[] BinaryStl(int triangleCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(new byte[80]);
            writer.Write((uint)triangleCount);
            for (int i = 0; i < triangleCount; i++)
            {
                writer.Write(0f); writer.Write(0f); writer.Write(1f);
                if (triangleCount == 2 && i < 2)
                {
                    Point3D[] vertices = i == 0
                        ? [new Point3D(0, 0, 0), new Point3D(10, 0, 0), new Point3D(10, 10, 0)]
                        : [new Point3D(0, 0, 0), new Point3D(10, 10, 0), new Point3D(0, 10, 0)];
                    foreach (var p in vertices) { writer.Write((float)p.X); writer.Write((float)p.Y); writer.Write((float)p.Z); }
                }
                else
                {
                    float x = i * 2f;
                    float[] vertices = [x, 0, 0, x + 1, 0, 0, x, 1, 0];
                    foreach (float value in vertices) writer.Write(value);
                }
                writer.Write((ushort)0);
            }
        }
        return stream.ToArray();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ReadType(JsonElement root)
        => root.TryGetProperty("type", out var type) ? type.GetString() ?? "" : root.GetProperty("cmd").GetString() ?? "";

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private static void Near(Point3D actual, Point3D expected, double tolerance, string name)
        => Check((actual - expected).Length <= tolerance, $"{name}: 实际 {actual}, 期望 {expected}");

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(name + ": 未抛出预期异常 " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(name + ": 未抛出预期异常 " + typeof(T).Name);
    }

    /// <summary>仅监听本机回环地址的协议探针，不依赖被测服务端实现。</summary>
    private sealed class P9Probe : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentQueue<string> _messages = new();
        private TcpClient? _client;
        private WebSocket? _socket;

        public P9Probe(int port) => _listener = new TcpListener(IPAddress.Loopback, port);
        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public async Task RunAsync()
        {
            _listener.Start();
            try
            {
                _client = await _listener.AcceptTcpClientAsync();
                var stream = _client.GetStream();
                await WebSocketHandshake(stream);
                _socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));
                while (_socket.State == WebSocketState.Open)
                {
                    string text = await ReceiveText(_socket);
                    if (text.Length == 0)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    }
                    _messages.Enqueue(text);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    string type = ReadType(root);
                    if (type.StartsWith("model_"))
                        await Send(_socket, new { type = "model_ack", modelId = root.GetProperty("modelId").GetString(), ok = true, error = (string?)null });
                    else if (type == "production_config" && root.GetProperty("revision").GetInt64() != 99)
                        await Send(_socket, ProductionAck(root, true));
                    else if (type.StartsWith("production_") && type != "production_config")
                    {
                        if (type == "production_confirm")
                            await Send(_socket, new
                            {
                                type = "production", phase = "Dispensing", runId = "run-test", pieceIndex = 1,
                                completedCount = 0, batchCount = 3, progress = 0.25, partPresent = true, clamped = true,
                                workpiecePose = new { x = 1, y = 2, z = 12, rx = 0, ry = 0, rz = 90 },
                                error = (string?)null, paused = false, canRetry = false,
                            });
                        await Send(_socket, ProductionAck(root, type != "production_stop"));
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or WebSocketException or OperationCanceledException) { }
        }

        private static async Task WebSocketHandshake(Stream stream)
        {
            var request = new MemoryStream();
            var one = new byte[1];
            while (request.Length < 16384)
            {
                int read = await stream.ReadAsync(one);
                if (read == 0) throw new IOException("WebSocket 握手提前结束");
                request.WriteByte(one[0]);
                if (request.Length >= 4 && Encoding.ASCII.GetString(request.GetBuffer(), (int)request.Length - 4, 4) == "\r\n\r\n") break;
            }
            string headers = Encoding.ASCII.GetString(request.ToArray());
            string? key = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Split(':', 2)[1].Trim()).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key)) throw new IOException("缺少 Sec-WebSocket-Key");
            string accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response);
        }

        private static object ProductionAck(JsonElement root, bool ok) => new
        {
            type = "production_ack", projectId = root.GetProperty("projectId").GetString(),
            revision = root.GetProperty("revision").GetInt64(), ok, error = ok ? null : "测试拒绝",
        };

        private static async Task<string> ReceiveText(WebSocket socket)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return "";
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static Task Send(WebSocket socket, object message)
            => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

        public void Dispose()
        {
            try { _socket?.Abort(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }
}
