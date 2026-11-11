using DispenseServer.Core;

namespace DispenseServer.Collision;

/// <summary>
/// 撞机检测器: 实时检查当前末端位置是否越界或即将撞入禁止区。
/// 两种检测:
///   1. 工作空间边界 (各轴 limit)
///   2. 禁止区列表 (AABB 包围盒) —— 用户定义的障碍物
/// 触发时返回报警信息。
/// </summary>
public sealed class CollisionChecker
{
    private readonly IDispenseAxisCard _card;
    private readonly List<Aabb> _forbiddenZones = new();
    private double _safetyMargin = 5.0; // mm, 提前预警距离
    private string? _lastWarnMsg;       // 上一次预警消息 (去重)
    private double _warnCooldown;       // 预警冷却计时 (秒)

    /// <summary>AABB 禁止区</summary>
    public sealed record Aabb(double XMin, double XMax, double YMin, double YMax, double ZMin, double ZMax, string Name);

    public event Action<string>? OnCollision;   // 撞机报警事件

    public CollisionChecker(IDispenseAxisCard card) => _card = card;

    /// <summary>添加禁止区 (如夹具、固定机构)</summary>
    public void AddForbiddenZone(Aabb zone) => _forbiddenZones.Add(zone);

    public void SetSafetyMargin(double mm) => _safetyMargin = mm;

    /// <summary>
    /// 每周期调用: 检查当前轴位置是否越界或即将进入禁止区。
    /// 返回 (triggered, isHard) 元组: triggered=true 表示触发报警, isHard=true 表示硬撞机(已越界), false 表示软预警(接近边界)。
    /// 硬撞机时调用方应立即停机; 软预警只提示不停机。
    /// </summary>
    public (bool triggered, bool isHard) Check()
    {
        // 冷却计时递减
        if (_warnCooldown > 0) _warnCooldown -= 0.008; // ~120Hz

        var sx = _card.Read(Axes.X);
        var sy = _card.Read(Axes.Y);
        var sz = _card.Read(Axes.Z);

        var (xMin, xMax) = _card.GetLimit(Axes.X);
        var (yMin, yMax) = _card.GetLimit(Axes.Y);
        var (zMin, zMax) = _card.GetLimit(Axes.Z);

        // 1a. 工作空间边界硬撞机 (已超出 limit)
        if (sx.Pos < xMin || sx.Pos > xMax ||
            sy.Pos < yMin || sy.Pos > yMax ||
            sz.Pos < zMin || sz.Pos > zMax)
        {
            string msg = $"[硬撞机] 超出工作空间边界! X={sx.Pos:F1}(limit {xMin}~{xMax}) " +
                         $"Y={sy.Pos:F1}(limit {yMin}~{yMax}) Z={sz.Pos:F1}(limit {zMin}~{zMax})";
            OnCollision?.Invoke(msg);
            return (true, true);
        }

        // 1b. 工作空间边界软预警 (接近 limit, 不停机, 节流 3 秒一次)
        if (_warnCooldown <= 0 &&
            (sx.Pos <= xMin + _safetyMargin || sx.Pos >= xMax - _safetyMargin ||
             sy.Pos <= yMin + _safetyMargin || sy.Pos >= yMax - _safetyMargin ||
             sz.Pos <= zMin + _safetyMargin || sz.Pos >= zMax - _safetyMargin))
        {
            string msg = $"[边界预警] 接近工作空间边界 X={sx.Pos:F1}(limit {xMin}~{xMax}) " +
                         $"Y={sy.Pos:F1}(limit {yMin}~{yMax}) Z={sz.Pos:F1}(limit {zMin}~{zMax})";
            OnCollision?.Invoke(msg);
            _warnCooldown = 3.0;
            return (true, false);
        }

        // 2. 禁止区检查
        foreach (var zone in _forbiddenZones)
        {
            // 硬撞机: 末端在禁止区内
            bool inside = sx.Pos >= zone.XMin && sx.Pos <= zone.XMax &&
                          sy.Pos >= zone.YMin && sy.Pos <= zone.YMax &&
                          sz.Pos >= zone.ZMin && sz.Pos <= zone.ZMax;
            if (inside)
            {
                string msg = $"[硬撞机] 进入禁止区「{zone.Name}」! " +
                             $"末端位置 ({sx.Pos:F1}, {sy.Pos:F1}, {sz.Pos:F1}), " +
                             $"禁止区 [{zone.XMin}~{zone.XMax}, {zone.YMin}~{zone.YMax}, {zone.ZMin}~{zone.ZMax}]";
                OnCollision?.Invoke(msg);
                return (true, true);
            }

            // 软预警: 接近禁止区 (在安全余量内, 节流)
            bool near = sx.Pos >= zone.XMin - _safetyMargin && sx.Pos <= zone.XMax + _safetyMargin &&
                        sy.Pos >= zone.YMin - _safetyMargin && sy.Pos <= zone.YMax + _safetyMargin &&
                        sz.Pos >= zone.ZMin - _safetyMargin && sz.Pos <= zone.ZMax + _safetyMargin;
            if (near && _warnCooldown <= 0)
            {
                string msg = $"[禁止区预警] 接近禁止区「{zone.Name}」" +
                             $"末端 ({sx.Pos:F1}, {sy.Pos:F1}, {sz.Pos:F1})";
                OnCollision?.Invoke(msg);
                _warnCooldown = 3.0;
                return (true, false);
            }

            // 速度方向预判: 如果正在高速移动且朝禁止区方向，提前预警
            double speed = Math.Sqrt(sx.Vel * sx.Vel + sy.Vel * sy.Vel + sz.Vel * sz.Vel);
            if (speed > 20)
            {
                double dt = 0.5; // 预判 0.5 秒后的位置
                double fx = sx.Pos + sx.Vel * dt;
                double fy = sy.Pos + sy.Vel * dt;
                double fz = sz.Pos + sz.Vel * dt;
                bool willEnter = fx >= zone.XMin - _safetyMargin && fx <= zone.XMax + _safetyMargin &&
                                 fy >= zone.YMin - _safetyMargin && fy <= zone.YMax + _safetyMargin &&
                                 fz >= zone.ZMin - _safetyMargin && fz <= zone.ZMax + _safetyMargin;
                if (willEnter)
                {
                    string msg = $"[禁止区预警] 0.5 秒后将接近禁止区「{zone.Name}」! " +
                                 $"当前 ({sx.Pos:F1}, {sy.Pos:F1}, {sz.Pos:F1}) " +
                                 $"→ 预判 ({fx:F1}, {fy:F1}, {fz:F1})";
                    OnCollision?.Invoke(msg);
                    return (true, false);
                }
            }
        }

        return (false, false);
    }

    public List<Aabb> GetForbiddenZones() => _forbiddenZones.ToList();
}
