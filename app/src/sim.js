import { LIMITS } from './machine.js';
import { buildDemo, samplePaths } from './paths.js';
import { ProductionCycle } from './production.js';
import { IoController } from './io.js';

// ---------------------------------------------------------------------------
// 共享模拟控制器 —— 在 Node Web 服务中运行，浏览器和上位机均为客户端。
//   接收: home / ptp / jog / laser / job / production_* / io_write / io_read / estop
//   发出: hello / state(30Hz) / path / io / event
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
    this.production = new ProductionCycle({
      startJob: name => this._startJob(name),
      abortJob: () => { this.job = null; this.laser = false; },
      isJobRunning: () => this.job !== null,
      getJobProgress: () => this._jobProgress(),
      validateRecipe: name => buildDemo(name) !== null,
      event: (level, msg) => this._event(level, msg),
    });
    this.io = new IoController({
      production: this.production,
      event: (level, msg) => this._event(level, msg),
    });
    // device = 服务端内置 ProductionCycle 自跑；host = 由上位机按 IO/作业原语编排
    this.controlMode = 'device';
    this._emitAcc = 0;
    this._t = 0;
    emit({ type: 'hello', server: 'Node MotionSimulation · 自动上下料', ver: 6 });
  }

  // ---------------- 指令入口 ----------------
  receive(msg) {
    const a = this.axes;
    switch (msg.type) {
      case 'home':
        if (this.production.running) { this._event('warn', '自动生产运行中，请先停止生产'); break; }
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
        if (this.job || this.production.running) break;
        this._clearFault();
        const ax = a[msg.axis];
        if (ax) { ax.jogDir = 0; ax.ptp(+msg.pos || 0); }
        break;
      }
      case 'jog': {
        if (this.job || this.production.running) break;
        this._clearFault();
        const ax = a[msg.axis];
        if (ax) { ax.target = null; ax.jogDir = Math.sign(msg.dir || 0); }
        break;
      }
      case 'laser':
        if (!this.production.running) this.laser = !!msg.on;
        break;
      case 'job':
        if (this.production.running) { this._event('warn', '自动生产运行中，不能手动启动作业'); break; }
        this._clearFault();
        this._startJob(msg.name);
        break;
      case 'mode_set': {
        const mode = msg.mode === 'host' ? 'host' : 'device';
        if (this.controlMode === mode) { this._event('ok', `控制模式已是 ${mode}`); break; }
        if (this.production.running || this.job || this._homing) {
          this._event('warn', '生产或作业运行中不能切换控制模式');
          break;
        }
        this.controlMode = mode;
        this.io.reset();
        this._event('ok', mode === 'host'
          ? '已切换到上位机模式：服务端不再自跑生产，只接受 IO / 作业原语'
          : '已切换到设备本地模式：production_start 由服务端内置流程执行');
        break;
      }
      case 'production_start':
        if (this.controlMode === 'host') {
          this._event('warn', '上位机模式下由上位机编排生产流程，production_start 已禁用');
          break;
        }
        if (this.job || this._homing) { this._event('warn', '请先等待当前运动或回零完成'); break; }
        this._clearFault();
        this.laser = false;
        this.production.start(msg.recipe || 'star', msg.count === 0 ? 0 : (msg.count || 1));
        break;
      case 'production_stop':
        if (this.controlMode === 'host') {
          this._event('warn', '上位机模式下请由上位机流程停止生产');
          break;
        }
        this.production.stop();
        break;
      case 'production_reset':
        if (this.controlMode === 'host') {
          this._event('warn', '上位机模式下请由上位机复位产线');
          break;
        }
        if (!this.production.reset()) this._event('warn', '生产运行中不能复位产线');
        break;
      case 'io_write': {
        const result = this.io.write(msg.id, !!msg.on);
        this._event(result.ok ? 'ok' : 'warn', result.msg);
        break;
      }
      case 'io_read':
        this.emit({ type: 'io', t: +this._t.toFixed(3), ...this.io.state() });
        break;
      case 'estop':
        this.production.fault();
        this.job = null;
        this.laser = false;
        this._homing = false;
        for (const k of ['x', 'y', 'z']) {
          a[k].vel = 0; a[k].target = null; a[k].jogDir = 0; a[k].state = 'Faulted';
        }
        this._event('error', '急停触发, 各轴 Faulted (任意指令复位)');
        break;
    }
  }

  _startJob(name) {
    const demo = buildDemo(name);
    if (!demo) { this._event('error', `未知作业: ${name}`); return false; }
    this.job = { name, steps: demo.steps, idx: 0, path: null };
    this.emit({ type: 'path', name, points: samplePaths(demo.draw) });
    this._event('ok', `作业开始: ${name}`);
    return true;
  }

  _jobProgress() {
    const j = this.job;
    if (!j) return 1;
    const pathPart = j.path && j.path.total > 0 ? j.path.s / j.path.total : 0;
    return Math.max(0, Math.min(1, (Math.max(0, j.idx - 1) + pathPart) / j.steps.length));
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
      // 结构化完成消息：上位机流程据此推进「松夹 → 下料」
      this.emit({ type: 'job_done', name: j.name, t: +this._t.toFixed(3) });
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
    this.production.tick(dt);
    this.io.tick(dt);

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
      const production = this.production.state();
      const io = this.io.state();
      this.emit({
        type: 'state', t: +this._t.toFixed(3),
        x: s('x'), y: s('y'), z: s('z'),
        laser: this.laser,
        controlMode: this.controlMode,
        mode: this.production.running
          ? `Auto:${this.production.phase}`
          : (this.job ? `Job:${this.job.name}` : (this._homing ? 'Homing' : (this.production.phase === 'Faulted' ? 'Auto:Faulted' : 'Idle'))),
        // 夹具/推杆/工件的位置由 IO 层持有，上位机模式下也能被 3D 场景直接消费
        production: {
          ...production,
          clamp: io.clamp,
          pusherX: io.pusherX,
          workpieceX: production.running ? production.workpieceX : io.workpieceX,
          workpieceId: production.running ? production.workpieceId : io.workpieceId,
        },
        io,
      });
    }
  }
}
