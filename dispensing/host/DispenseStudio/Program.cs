using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DispenseHost.Net;
using DispenseHost.Testing;
using DispenseStudio;

// ---------------------------------------------------------------------------
// 3D 点胶平台上位机 (DispenseStudio)
//   运行模式:
//     (无参数)        WinForms 图形界面 (连接/图案规划/预览/上传/执行/监控)
//     --selftest      无窗口自动测试: 圆形 + 矩形 + 组合图案, 生成测试报告
//     --smoke         仅构造主窗体验证 UI 代码, 不连接服务端
// ---------------------------------------------------------------------------

internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern bool FreeConsole();
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            return RunSelfTest(args);

        if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new MainForm(autoConnect: false);
                Console.WriteLine($"SMOKE OK: {form.Text}, 控件 {form.Controls.Count} 个顶层容器");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SMOKE FAIL: " + ex);
                return 1;
            }
        }

        // GUI 模式: 隐藏控制台窗口, 启动消息循环
        try { Native.FreeConsole(); } catch { }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(autoConnect: true));
        return 0;
    }

    private static int RunSelfTest(string[] args)
    {
        string Arg(string key, string def)
        {
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        double ArgD(string key, double def) =>
            double.TryParse(Arg(key, def.ToString()), out var v) ? v : def;

        var opt = new SelfTestOptions
        {
            ServerUrl = Arg("--url", "ws://localhost:8530/ws"),
            CircleRadius = ArgD("--radius", 30),
            RectWidth = ArgD("--width", 80),
            RectHeight = ArgD("--height", 50),
            Feed = ArgD("--feed", 60),
            CircleSegments = (int)ArgD("--segments", 72),
            IncludeCombined = !args.Contains("--no-combined"),
            TimeoutSeconds = (int)ArgD("--timeout", 60),
        };

        using var client = new DispenseClient(opt.ServerUrl);
        client.OnLog += m => Console.WriteLine("[client] " + m);
        client.OnEvent += m =>
        {
            if (!m.StartsWith("[info]")) Console.WriteLine("[event] " + m);
        };

        var runner = new SelfTestRunner(client, opt);
        runner.OnLog += m => Console.WriteLine(m);
        runner.OnCaseCompleted += r =>
            Console.WriteLine($"    -> {r.Name}: {(r.Passed ? "PASS" : "FAIL")}");

        try
        {
            var report = runner.RunAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine(report.ToText());

            // 报告落盘 (便于归档 / 复核)
            var dir = Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report.ToText(), Encoding.UTF8);
            Console.WriteLine("报告已保存: " + path);
            return report.AllPassed ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动测试失败: " + ex);
            return 1;
        }
    }
}
