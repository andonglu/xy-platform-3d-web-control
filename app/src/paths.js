// ---------------------------------------------------------------------------
// 2D 插补路径 (与 C# 端 InterpolationPlanner 同一套语义):
//   每条路径提供 length 与 pointAt(s) —— 按弧长参数化, 控制器以进给速度推进 s。
// ---------------------------------------------------------------------------

export class LinePath {
  constructor(x0, y0, x1, y1) {
    this.x0 = x0; this.y0 = y0; this.x1 = x1; this.y1 = y1;
    this.length = Math.hypot(x1 - x0, y1 - y0);
  }
  pointAt(s) {
    const t = this.length === 0 ? 0 : Math.min(s / this.length, 1);
    return { x: this.x0 + (this.x1 - this.x0) * t, y: this.y0 + (this.y1 - this.y0) * t };
  }
}

export class ArcPath {
  // cx,cy 圆心; a0 起始角(rad); sweep 扫掠角(rad, 正逆时针/负顺时针)
  constructor(cx, cy, r, a0, sweep) {
    this.cx = cx; this.cy = cy; this.r = r; this.a0 = a0; this.sweep = sweep;
    this.length = Math.abs(r * sweep);
  }
  pointAt(s) {
    const t = this.length === 0 ? 0 : Math.min(s / this.length, 1);
    const a = this.a0 + this.sweep * t;
    return { x: this.cx + this.r * Math.cos(a), y: this.cy + this.r * Math.sin(a) };
  }
}

// 把工作平面路径采样成 [x, y] 点列 (用于预览线 & 协议传输)
export function samplePaths(paths, step = 1) {
  const pts = [];
  for (const p of paths) {
    const n = Math.max(2, Math.ceil(p.length / step));
    for (let i = 0; i <= n; i++) {
      const q = p.pointAt((i / n) * p.length);
      pts.push([+q.x.toFixed(3), +q.y.toFixed(3)]);
    }
  }
  return pts;
}

// ---------------------------------------------------------------------------
// 演示作业: 返回步骤序列 (模拟控制器与 C# 上位机执行同一套动作)
//   { kind:'ptp', x?, y?, z? }         定位
//   { kind:'laser', on }               激光开关 (抬笔/落笔)
//   { kind:'path', paths, feed }       连续插补
// ---------------------------------------------------------------------------
const PEN_DOWN = 8;    // 工作高度 mm
const PEN_UP = 30;     // 安全高度 mm

function starJob() {
  const R = 60, r = 24, cx = 0, cy = 0;
  const verts = [];
  for (let i = 0; i < 10; i++) {
    const rad = i % 2 === 0 ? R : r;
    const a = -Math.PI / 2 + (i * Math.PI) / 5;
    verts.push([cx + rad * Math.cos(a), cy + rad * Math.sin(a)]);
  }
  const paths = [];
  for (let i = 0; i < 10; i++) {
    const [x0, y0] = verts[i], [x1, y1] = verts[(i + 1) % 10];
    paths.push(new LinePath(x0, y0, x1, y1));
  }
  return {
    steps: [
      { kind: 'ptp', z: PEN_UP },
      { kind: 'ptp', x: verts[0][0], y: verts[0][1] },
      { kind: 'ptp', z: PEN_DOWN },
      { kind: 'laser', on: true },
      { kind: 'path', paths, feed: 120 },
      { kind: 'laser', on: false },
      { kind: 'ptp', z: PEN_UP },
    ],
    draw: paths,
  };
}

function circleJob() {
  // 整圆 (两段半圆弧, 验证圆弧插补的接续) + 一条直径直线
  const circ1 = new ArcPath(0, 0, 50, 0, Math.PI);
  const circ2 = new ArcPath(0, 0, 50, Math.PI, Math.PI);
  const dia = new LinePath(-50, 0, 50, 0);
  return {
    steps: [
      { kind: 'ptp', z: PEN_UP },
      { kind: 'ptp', x: 50, y: 0 },
      { kind: 'ptp', z: PEN_DOWN },
      { kind: 'laser', on: true },
      { kind: 'path', paths: [circ1, circ2], feed: 100 },
      { kind: 'path', paths: [dia], feed: 100 },
      { kind: 'laser', on: false },
      { kind: 'ptp', z: PEN_UP },
    ],
    draw: [circ1, circ2, dia],
  };
}

function comboJob() {
  // 带圆角的方框 + 内切圆 + 对角线
  const w = 140, h = 100, r = 15, cx = 0, cy = 0;
  const x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2;
  const box = [
    new LinePath(x0 + r, y1, x1 - r, y1),
    new ArcPath(x1 - r, y1 - r, r, Math.PI / 2, -Math.PI / 2),
    new LinePath(x1, y1 - r, x1, y0 + r),
    new ArcPath(x1 - r, y0 + r, r, 0, -Math.PI / 2),
    new LinePath(x1 - r, y0, x0 + r, y0),
    new ArcPath(x0 + r, y0 + r, r, -Math.PI / 2, -Math.PI / 2),
    new LinePath(x0, y0 + r, x0, y1 - r),
    new ArcPath(x0 + r, y1 - r, r, Math.PI, -Math.PI / 2),
  ];
  const inner = [new ArcPath(cx, cy, 26, 0, Math.PI * 2)];
  const cross = [new LinePath(x0, y0, x1, y1), new LinePath(x1, y0, x0, y1)];
  return {
    steps: [
      { kind: 'ptp', z: PEN_UP },
      { kind: 'ptp', x: x0 + r, y: y1 },
      { kind: 'ptp', z: PEN_DOWN },
      { kind: 'laser', on: true },
      { kind: 'path', paths: box, feed: 140 },
      { kind: 'laser', on: false },
      { kind: 'ptp', x: cx + 26, y: cy },
      { kind: 'laser', on: true },
      { kind: 'path', paths: inner, feed: 100 },
      { kind: 'laser', on: false },
      { kind: 'ptp', x: x0, y: y0 },
      { kind: 'laser', on: true },
      { kind: 'path', paths: [cross[0]], feed: 160 },
      { kind: 'laser', on: false },
      { kind: 'ptp', x: x1, y: y0 },
      { kind: 'laser', on: true },
      { kind: 'path', paths: [cross[1]], feed: 160 },
      { kind: 'laser', on: false },
      { kind: 'ptp', z: PEN_UP },
    ],
    draw: [...box, ...inner, ...cross],
  };
}

const DEMOS = { star: starJob, circle: circleJob, combo: comboJob };

export function buildDemo(name) {
  const f = DEMOS[name];
  return f ? f() : null;
}
