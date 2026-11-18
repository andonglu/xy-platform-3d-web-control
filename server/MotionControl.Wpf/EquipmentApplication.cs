using System.IO;
using System.Text.Json;
using MotionControl.Equipment;
using MotionControl.Hardware.Adapters;
using MotionControl.Scripting;
using MotionControl.Wpf.ViewModels;

namespace MotionControl.Wpf;

/// <summary>唯一装配点：在这里选择 Port，服务/平台/UI/脚本不识别设备实现。</summary>
public static class EquipmentApplication
{
    public static MainViewModel CreateViewModel()
    {
        var options = LoadOptions();
        var ports = EquipmentPortFactory.Create(options);
        var service = new LaserEquipmentService(ports.Motion, ports.Laser);
        var platform = new LaserPlatform(service);
        var scripts = new ScriptService(platform);
        scripts.Initialize(Path.Combine(AppContext.BaseDirectory, "Scripts"));
        return new MainViewModel(platform, scripts);
    }

    private static EquipmentChannelOptions LoadOptions()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Config", "equipment.json");
        if (!File.Exists(file)) return new EquipmentChannelOptions("virtual");
        try
        {
            return JsonSerializer.Deserialize<EquipmentChannelOptions>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new EquipmentChannelOptions("virtual");
        }
        catch { return new EquipmentChannelOptions("virtual"); }
    }
}
