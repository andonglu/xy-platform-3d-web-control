namespace DispenseServer.Core;

/// <summary>
/// 五轴 RTCP 运动学 (P4) —— AC 双摆头构型:
///   XYZ 控制枢轴点 P, A 轴(绕 X)摆动, C 轴(绕 Z)回转, 工具(针)长 L 沿工具方向。
///   工具方向: t(A,C) = Rot(uC, C) · Rot(uA, A) · (0,0,-1)   —— A=0 时竖直向下
///   正解(真实世界): TCP = P + PivotErr + L · t_real
///   逆解(RTCP, 控制器): 给定目标 TCP 与曲面法向 n →
///     A = acos(−n.z), C = atan2(−n.x, n.y), P_cmd = TCP − PivotErr − L · t_nominal
/// 标定前 Nominal 用理想轴线 (ex/ez) 与零枢轴误差; 真实机床 RealGeometry 带出厂误差,
/// 导致编程轨迹与实际 TCP 系统性偏离; rtcp_calibrate 探测拟合后把误差写回 Nominal, 偏离消除。
/// </summary>
public sealed class FiveAxisKinematics
{
    /// <summary>工具长度 (枢轴 → 针尖, mm)</summary>
    public double ToolLength { get; set; } = 60;

    /// <summary>名义 A 轴轴线 (单位向量; 标定后被修正)</summary>
    public Point3D AxisA { get; set; } = new(1, 0, 0);

    /// <summary>名义 C 轴轴线 (单位向量; 标定后被修正)</summary>
    public Point3D AxisC { get; set; } = new(0, 0, 1);

    /// <summary>名义枢轴误差 (标定后 ≈ 真实值)</summary>
    public Point3D PivotError { get; set; } = new(0, 0, 0);

    public bool Calibrated { get; internal set; }

    // ---------------- 正解 ----------------

    /// <summary>工具方向 (用本模型的轴线); A=0/C=0 → (0,0,-1) 竖直向下</summary>
    public Point3D ToolDir(double aDeg, double cDeg)
    {
        double a = aDeg * Math.PI / 180, c = cDeg * Math.PI / 180;
        var t = Rotate(new Point3D(0, 0, -1), AxisA.Normalize(), a);
        return Rotate(t, AxisC.Normalize(), c);
    }

    /// <summary>正解: 枢轴位置 + 角度 → TCP (本模型自身一致性)</summary>
    public Point3D Forward(Point3D pivot, double aDeg, double cDeg)
        => pivot + PivotError + ToolDir(aDeg, cDeg) * ToolLength;

    // ---------------- 逆解 (RTCP) ----------------

    /// <summary>
    /// RTCP 逆解: 目标 TCP + 曲面法向 → 枢轴指令位置与 A/C 角。
    /// t(A,C) = (−sinA·sinC, sinA·cosC, −cosA) = n ⟹ A = acos(−n.z), C = atan2(−n.x, n.y)。
    /// 返回的 A/C 可能超出行程, 调用方负责限位与告警。
    /// </summary>
    public (Point3D pivot, double aDeg, double cDeg) SolveTcp(Point3D tcp, Point3D normal)
    {
        var n = normal.Normalize();
        double aDeg = Math.Acos(Math.Clamp(-n.Z, -1, 1)) * 180 / Math.PI;  // 0°(竖直)~180°
        double cDeg = Math.Atan2(-n.X, n.Y) * 180 / Math.PI;               // 绕 Z 方位
        var t = ToolDir(aDeg, cDeg);
        return (tcp - PivotError - t * ToolLength, aDeg, cDeg);
    }

    // ---------------- 标定 ----------------

    /// <summary>
    /// RTCP 标定: 两个枢轴高度 × 八个姿态的探针测量, 差分消除枢轴误差后
    /// 高斯-牛顿最小二乘拟合 A/C 轴线误差 (4 参数), 再求枢轴误差均值。
    /// 拟合结果写回本模型 (AxisA/AxisC/PivotError), Calibrated=true。
    /// 返回标定残差 (mm, 拟合后预测误差 RMS)。
    /// </summary>
    public double Calibrate(Func<Point3D, double, double, Point3D> measureTrueTcp)
    {
        double L = ToolLength;
        // 探针姿态: A ∈ {-40,-15,15,40}, C ∈ {0, 90, 180, 270} 组合取 8 个
        var poses = new List<(double a, double c)>();
        foreach (double a in new[] { -40.0, -15.0, 15.0, 40.0 })
            foreach (double c in new[] { 0.0, 90.0, 180.0, 270.0 })
                poses.Add((a, c));
        double[] heights = [20.0, 60.0];   // 两个枢轴 Z 高度, 差分消除 PivotError

        // 基准模型 (理想轴线) 用于计算预测值
        var ideal = new FiveAxisKinematics { ToolLength = L };

        // 参数 p = [εay, εaz, εcx, εcy]: uA = normalize(ex + εa), uC = normalize(ez + εc)
        var p = new double[4];
        const double eps = 1e-7;

        // 残差函数: 每探针 = 实测TCP - 预测TCP(参数化模型) —— 用双高度差分
        void Residuals(double[] prm, List<Point3D> outR, List<double[]>? outJ)
        {
            outR.Clear();
            if (outJ is not null) outJ.Clear();
            foreach (var (a, c) in poses)
            {
                Point3D m0 = measureTrueTcp(new Point3D(0, 0, heights[0]), a, c);
                Point3D m1 = measureTrueTcp(new Point3D(0, 0, heights[1]), a, c);
                Point3D diff = m1 - m0;                       // 真实方向矢量 (含枢轴无关)
                // 参数化预测差分 (基向量与 ToolDir 一致: (0,0,-1))
                var uA = new Point3D(1, prm[0], prm[1]).Normalize();
                var uC = new Point3D(prm[2], prm[3], 1).Normalize();
                double ar = a * Math.PI / 180, cr = c * Math.PI / 180;
                var pred = Rotate(Rotate(new Point3D(0, 0, -1), uA, ar), uC, cr) * L;
                outR.Add(diff - pred);
                if (outJ is not null)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        var pp = (double[])prm.Clone();
                        pp[k] += eps;
                        var uA2 = new Point3D(1, pp[0], pp[1]).Normalize();
                        var uC2 = new Point3D(pp[2], pp[3], 1).Normalize();
                        var pred2 = Rotate(Rotate(new Point3D(0, 0, -1), uA2, ar), uC2, cr) * L;
                        var d = pred2 - pred;
                        outJ.Add([d.X / eps, d.Y / eps, d.Z / eps]);   // 3 行/参数
                    }
                }
            }
        }

        // 高斯-牛顿迭代 (4 参数, 24 残差方程)
        var r = new List<Point3D>();
        var jCols = new List<double[]>();
        for (int iter = 0; iter < 8; iter++)
        {
            Residuals(p, r, jCols);
            int m = r.Count;
            // 调试: 打印每轮残差范数 (诊断标定收敛)
            double dbg = 0; foreach (var v in r) dbg += v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            Console.WriteLine($"    [calib iter {iter}] p=({p[0]:F5},{p[1]:F5},{p[2]:F5},{p[3]:F5}) |r|={Math.Sqrt(dbg / (m * 3)):F4}");
            // 正规方程 (JᵀJ)₄ₓ₄ Δ = -Jᵀr (jCols 按 i-major 存储: [i*4+k])
            var jtj = new double[4, 4];
            var jtr = new double[4];
            for (int i = 0; i < m; i++)
            {
                var ri = new[] { r[i].X, r[i].Y, r[i].Z };
                for (int k = 0; k < 4; k++)
                {
                    var jk = jCols[i * 4 + k];   // 参数 k 对测量 i 的雅可比 (3)
                    for (int l = 0; l < 4; l++)
                    {
                        var jl = jCols[i * 4 + l];
                        jtj[k, l] += jk[0] * jl[0] + jk[1] * jl[1] + jk[2] * jl[2];
                    }
                    jtr[k] += jk[0] * ri[0] + jk[1] * ri[1] + jk[2] * ri[2];
                }
            }
            // 4×4 高斯消元
            var delta = Solve4(jtj, jtr);
            for (int k = 0; k < 4; k++) p[k] -= delta[k];
        }

        // 写回修正后的轴线
        AxisA = new Point3D(1, p[0], p[1]).Normalize();
        AxisC = new Point3D(p[2], p[3], 1).Normalize();

        // 枢轴误差: 标定后预测残差均值 (此时仅差常值枢轴项)
        Residuals(p, r, null);
        var mean = new Point3D(r.Average(v => v.X), r.Average(v => v.Y), r.Average(v => v.Z));
        PivotError = PivotError + mean;

        // 最终残差 RMS (差分域)
        double sum = 0;
        foreach (var v in r) { var q = v - mean; sum += q.X * q.X + q.Y * q.Y + q.Z * q.Z; }
        Calibrated = true;
        return Math.Sqrt(sum / (r.Count * 3));
    }

    private static double[] Solve4(double[,] a, double[] b)
    {
        int n = 4;
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[piv, col])) piv = row;
            for (int k = col; k <= n; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            double d = m[col, col];
            if (Math.Abs(d) < 1e-12) d = 1e-12;
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double f = m[row, col] / d;
                for (int k = col; k <= n; k++) m[row, k] -= f * m[col, k];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = m[i, n] / (Math.Abs(m[i, i]) < 1e-12 ? 1e-12 : m[i, i]);
        return x;
    }

    /// <summary>Rodrigues 旋转公式</summary>
    public static Point3D Rotate(Point3D v, Point3D axisUnit, double angleRad)
    {
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        double dot = Point3D.Dot(axisUnit, v);
        var cr = Point3D.Cross(axisUnit, v);
        return v * c + cr * s + axisUnit * (dot * (1 - c));
    }
}
