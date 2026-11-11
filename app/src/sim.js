import { LIMITS } from './platform.js';
import { buildDemo, samplePaths } from './paths.js';

// ---------------------------------------------------------------------------
// 内置模拟控制器 —— 与 C# 上位机 MotionController 同一份协议语义:
//   接收: home / ptp / jog / laser / job / estop
//   发出: hello / state(30Hz) / path / event
// 运动模型: 梯形速度规划 (加速 → 巡航 → 减速), 插补按弧长推进。
// ---------------------------------------------------------------------------

class Axis {
  constructor(name, [min, max]) {
    this.name = name;
    this.min = min; this.max = max;
    this.pos = 0; this.vel = 0;
    this.state = 'Idle';       // Idle | Moving | Homing | Faulted
    this.target = null;
    this.vmax = 120;           // mm/s
    this.accel = 600;          // mm/s²
    this.jogDir = 0;
  }

  // 朝 target 梯形推进, 返回是否到位
  _stepTo(target, vmax, dt) {
    const dist = target - this.pos;
    const dir = Math.sign(dist);
    const vStop = Math.sqrt(2 * this.accel * Math.abs(dist)); // 减速距离约束
    const vWant = dir * Math.min(vmax, vStop);
    const dv = this.accel * dt;
    this.vel += Math.max(-dv, Math.min(dv, vWant - this.vel));
    this.pos += this.vel * dt;
    if ((dir > 0 && this.pos >= target) || (dir < 0 && this.pos <= target) ||
        (Math.abs(dist) < 0.02 && Math.abs(this.vel) < 2)) {
      this.pos = target; this.vel = 0;
      return true;
    }
    return false;
  }

  ptp(pos, vmax = this.vmax) {
    this.target = Math.max(this.min, Math.min(this.max, pos));
    this._vmaxCur = vmax;
    this.state = 'Moving';
  }

  tick(dt) {
    if (this.state === 'Faulted') { this.vel = 0; return; }
    if (this.jogDir !== 0) {
      this.state = 'Moving';
      this.target = null;
      const vWant = this.jogDir * 50;
      const dv = this.accel * dt;
      this.vel += Math.max(-dv, Math.min(dv, vWant - this.vel));
      this.pos += this.vel * dt;
      if (this.pos <= this.min || this.pos >= this.max) {
        this.pos = Math.max(this.min, Math.min(this.max, this.pos));
        this.vel = 0;
      }
      return;
    }
    if (this.target !== null) {
      if (this._stepTo(this.target, this._vmaxCur ?? this.vmax, dt)) {
        this.target = null;
        this.vel = 0;
        this.state = 'Idle';
      }
    } else if (this.state === 'Moving' && this.jogDir === 0) {
      // 点动停止: 减速到 0
      const dv = this.accel * dt;
      const v = Math.abs(this.vel) <= dv ? 0 : this.vel - Math.sign(this.vel) * dv;
      this.vel = v;
      this.pos += this.vel * dt;
      if (this.vel === 0) this.state = 'Idle';
    }
  }
}

export class SimController {
  constructor(emit) {
    this.emit = emit;                      // (obj) => void  发往前端
    this.axes = { x: new Axis('x', LIMITS.x), y: new Axis('y', LIMITS.y), z: new Axis('z', LIMITS.z) };
    this.laser = false;
    this.job = null;                       // { name, steps, idx, pathState }
    this._emitAcc = 0;
    this._t = 0;
    emit({ type: 'hello', server: 'SimController (浏览器内置)', ver: 1 });
  }

  // ---------------- 指令入口 (协议同 C# 端) ----------------
  receive(msg) {
    const a = this.axes;
    switch (msg.type) {
      case 'home':
        this._clearFault();
        this.job = null;
        for (const k of ['x', 'y', 'z']) {
          a[k].jogDir = 0;
          a[k].ptp(0, 80);
          a[k].state = 'Homing';
        }
        this._homing = true;
        this._event('ok', '回零开始');
        break;
      case 'ptp': {
        if (this.job) break;
        this._clearFault();
        const ax = a[msg.axis];
        if (ax) { ax.jogDir = 0; ax.ptp(+msg.pos || 0); }
        break;
      }
      case 'jog': {
        if (this.job) break;
        this._clearFault();
        const ax = a[msg.axis];
        if (ax) { ax.target = null; ax.jogDir = Math.sign(msg.dir || 0); }
        break;
      }
      case 'laser':
        this.laser = !!msg.on;
        break;
      case 'job': {
        this._clearFault();
        const demo = buildDemo(msg.name);
        if (!demo) { this._event('error', `未知作业: ${msg.name}`); break; }
        this.job = { name: msg.name, steps: demo.steps, idx: 0, path: null };
        this.emit({ type: 'path', name: msg.name, points: samplePaths(demo.draw) });
        this._event('ok', `作业开始: ${msg.name}`);
        break;
      }
      case 'estop':
        this.job = null;
        this.laser = false;
        for (const k of ['x', 'y', 'z']) {
          a[k].vel = 0; a[k].target = null; a[k].jogDir = 0; a[k].state = 'Faulted';
        }
        this._event('error', '急停触发, 各轴 Faulted (任意指令复位)');
        break;
    }
  }

  _clearFault() {
    for (const k of ['x', 'y', 'z']) if (this.axes[k].state === 'Faulted') this.axes[k].state = 'Idle';
  }

  _event(level, msg) { this.emit({ type: 'event', level, msg }); }

  // ---------------- 作业执行 ----------------
  _tickJob(dt) {
    const j = this.job;
    if (!j) return;
    const a = this.axes;

    // 当前路径段推进
    if (j.path) {
      const p = j.path;
      const dist = p.total - p.s;
      const vStop = Math.sqrt(2 * 500 * Math.max(dist, 0));
      const vWant = Math.min(p.feed, vStop);
      const dv = 500 * dt;
      p.v += Math.max(-dv, Math.min(dv, vWant - p.v));
      p.s += p.v * dt;
      // 沿各子路径取点
      let rest = p.s, pt = null;
      for (const sub of p.paths) {
        if (rest <= sub.length) { pt = sub.pointAt(rest); break; }
        rest -= sub.length;
      }
      if (!pt) pt = p.paths[p.paths.length - 1].pointAt(p.paths[p.paths.length - 1].length);
      a.x.pos = pt.x; a.y.pos = pt.y;
      a.x.vel = p.v; a.y.vel = p.v;   // 合成速度近似上报
      a.x.state = a.y.state = 'Moving';
      if (p.s >= p.total) {
        p.v = 0;
        a.x.vel = a.y.vel = 0;
        a.x.state = a.y.state = 'Idle';
        j.path = null;   // idx 在取步骤时已推进, 这里不再 ++
      }
      return;
    }

    // 等待定位完成
    if (a.x.target !== null || a.y.target !== null || a.z.target !== null) return;

    // 取下一步
    const step = j.steps[j.idx];
    if (!step) {
      this._event('ok', `作业完成: ${j.name}`);
      this.job = null;
      return;
    }
    j.idx++;
    if (step.kind === 'ptp') {
      if (step.x !== undefined) a.x.ptp(step.x, 160);
      if (step.y !== undefined) a.y.ptp(step.y, 160);
      if (step.z !== undefined) a.z.ptp(step.z, 60);
    } else if (step.kind === 'laser') {
      this.laser = step.on;
    } else if (step.kind === 'path') {
      j.path = {
        paths: step.paths, feed: step.feed,
        total: step.paths.reduce((s, p) => s + p.length, 0),
        s: 0, v: 0,
      };
    }
  }

  // ---------------- 主循环 ----------------
  tick(dt) {
    this._t += dt;
    this._tickJob(dt);
    for (const k of ['x', 'y', 'z']) this.axes[k].tick(dt);

    if (this._homing) {
      const done = ['x', 'y', 'z'].every(k => this.axes[k].target === null);
      if (done) {
        this._homing = false;
        for (const k of ['x', 'y', 'z']) if (this.axes[k].state === 'Homing') this.axes[k].state = 'Idle';
        this._event('ok', '回零完成');
      }
    }

    this._emitAcc += dt;
    if (this._emitAcc >= 1 / 30) {          // 30Hz 状态广播
      this._emitAcc = 0;
      const s = k => ({
        pos: +this.axes[k].pos.toFixed(3),
        vel: +this.axes[k].vel.toFixed(2),
        state: this.axes[k].state,
      });
      this.emit({
        type: 'state', t: +this._t.toFixed(3),
        x: s('x'), y: s('y'), z: s('z'),
        laser: this.laser,
        mode: this.job ? `Job:${this.job.name}` : (this._homing ? 'Homing' : 'Idle'),
      });
    }
  }
}
