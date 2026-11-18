// ---------------------------------------------------------------------------
// 激光加工自动上下料流程。
// 只负责生产节拍与工件状态，不依赖 Three.js；位置单位均为机器毫米。
// Three.js 场景只消费 state()，因此后续可在 C# 端复用同一协议语义。
// ---------------------------------------------------------------------------

export const PRODUCTION_POSITIONS = Object.freeze({
  infeedX: -200,
  outfeedX: 200,
  pusherHome: -305,
  pusherGap: 96,       // 推杆位于工件左侧，避免遮住加工区域
});

const DURATION = Object.freeze({
  Feeding: 0.65,
  Loading: 1.8,
  Clamping: 0.7,
  Unclamping: 0.55,
  Unloading: 1.8,
  Retracting: 0.85,
});

const clamp01 = v => Math.max(0, Math.min(1, v));
const ease = t => { const k = clamp01(t); return k * k * (3 - 2 * k); };
const mix = (a, b, t) => a + (b - a) * t;
const round = v => Math.round(v * 1000) / 1000;

export class ProductionCycle {
  constructor({ startJob, abortJob, isJobRunning, getJobProgress, validateRecipe, event }) {
    this.startJob = startJob;
    this.abortJob = abortJob;
    this.isJobRunning = isJobRunning;
    this.getJobProgress = getJobProgress;
    this.validateRecipe = validateRecipe;
    this.event = event;

    this.phase = 'Idle';
    this.running = false;
    this.recipe = 'star';
    this.targetCount = 1;
    this.completed = 0;
    this.serial = 0;
    this.workpieceId = null;
    this.workpieceX = null;
    this.pusherX = PRODUCTION_POSITIONS.pusherHome;
    this.clamp = 0;
    this.elapsed = 0;
    this.phaseProgress = 0;
    this.stopRequested = false;
  }

  start(recipe = 'star', targetCount = 1) {
    if (this.running) {
      this.event('warn', '自动生产线正在运行');
      return false;
    }
    if (this.phase === 'Faulted') {
      this.event('error', '产线处于故障状态，请先执行 production_reset 和回零');
      return false;
    }
    if (!this.validateRecipe(recipe)) {
      this.event('error', `未知加工程序: ${recipe}`);
      return false;
    }

    this.recipe = recipe;
    this.targetCount = targetCount === 0 ? 0 : Math.max(1, Math.min(999, Math.round(targetCount)));
    this.completed = 0;
    this.stopRequested = false;
    this.running = true;
    this.event('ok', this.targetCount === 0
      ? `连续生产启动: ${recipe}，将在收到停止指令后完成当前工件`
      : `自动生产启动: ${recipe} · ${this.targetCount} 件`);
    this._newWorkpiece();
    return true;
  }

  stop() {
    if (!this.running || this.stopRequested) return;
    this.stopRequested = true;
    this.event('warn', '已请求停止：当前工件加工并下料后停机');
  }

  reset() {
    if (this.running) return false;
    this.phase = 'Idle';
    this.workpieceId = null;
    this.workpieceX = null;
    this.pusherX = PRODUCTION_POSITIONS.pusherHome;
    this.clamp = 0;
    this.elapsed = 0;
    this.phaseProgress = 0;
    this.completed = 0;
    this.stopRequested = false;
    this.event('ok', '产线已复位，可回零后重新启动');
    return true;
  }

  fault() {
    if (!this.running) return;
    this.running = false;
    this.phase = 'Faulted';
    this.stopRequested = false;
    this.abortJob();
    // 工件、推杆和夹具位置保持不变，直观呈现急停时的现场状态。
  }

  _enter(phase) {
    this.phase = phase;
    this.elapsed = 0;
    this.phaseProgress = 0;
  }

  _newWorkpiece() {
    this.serial++;
    this.workpieceId = `P${String(this.serial).padStart(3, '0')}`;
    this.workpieceX = PRODUCTION_POSITIONS.infeedX;
    this.pusherX = PRODUCTION_POSITIONS.pusherHome;
    this.clamp = 0;
    this._enter('Feeding');
    this.event('info', `${this.workpieceId} 料仓出料`);
  }

  _progress(duration) {
    this.phaseProgress = clamp01(this.elapsed / duration);
    return this.phaseProgress;
  }

  tick(dt) {
    if (!this.running) return;
    this.elapsed += dt;
    const pos = PRODUCTION_POSITIONS;

    switch (this.phase) {
      case 'Feeding': {
        this._progress(DURATION.Feeding);
        if (this.phaseProgress >= 1) {
          this._enter('Loading');
          this.event('info', `${this.workpieceId} 推料上料`);
        }
        break;
      }

      case 'Loading': {
        const t = ease(this._progress(DURATION.Loading));
        this.workpieceX = mix(pos.infeedX, 0, t);
        this.pusherX = this.workpieceX - pos.pusherGap;
        if (this.phaseProgress >= 1) {
          this.workpieceX = 0;
          this.pusherX = -pos.pusherGap;
          this._enter('Clamping');
        }
        break;
      }

      case 'Clamping': {
        const t = ease(this._progress(DURATION.Clamping));
        this.clamp = t;
        if (this.phaseProgress >= 1) {
          this.clamp = 1;
          this._enter('Processing');
          this.event('info', `${this.workpieceId} 已定位夹紧，开始激光加工`);
          this.startJob(this.recipe);
        }
        break;
      }

      case 'Processing': {
        this.phaseProgress = clamp01(this.getJobProgress());
        if (!this.isJobRunning()) {
          this.phaseProgress = 1;
          this._enter('Unclamping');
          this.event('ok', `${this.workpieceId} 加工完成，松夹并准备下料`);
        }
        break;
      }

      case 'Unclamping': {
        const t = ease(this._progress(DURATION.Unclamping));
        this.clamp = 1 - t;
        if (this.phaseProgress >= 1) {
          this.clamp = 0;
          this._enter('Unloading');
        }
        break;
      }

      case 'Unloading': {
        const t = ease(this._progress(DURATION.Unloading));
        this.workpieceX = mix(0, pos.outfeedX, t);
        this.pusherX = this.workpieceX - pos.pusherGap;
        if (this.phaseProgress >= 1) {
          this.workpieceX = pos.outfeedX;
          this.pusherX = pos.outfeedX - pos.pusherGap;
          this._enter('Retracting');
        }
        break;
      }

      case 'Retracting': {
        const t = ease(this._progress(DURATION.Retracting));
        this.pusherX = mix(pos.outfeedX - pos.pusherGap, pos.pusherHome, t);
        if (this.phaseProgress >= 1) {
          this.pusherX = pos.pusherHome;
          this.completed++;
          if ((!this.stopRequested && this.targetCount === 0) || this.completed < this.targetCount) {
            this._newWorkpiece();
          } else {
            this.running = false;
            this._enter('Complete');
            this.phaseProgress = 1;
            this.event('ok', `生产完成：${this.completed} 件工件已下料`);
          }
        }
        break;
      }
    }
  }

  state() {
    return {
      phase: this.phase,
      running: this.running,
      stopRequested: this.stopRequested,
      recipe: this.recipe,
      targetCount: this.targetCount,
      completed: this.completed,
      workpieceId: this.workpieceId,
      workpieceX: this.workpieceX === null ? null : round(this.workpieceX),
      pusherX: round(this.pusherX),
      clamp: round(this.clamp),
      phaseProgress: round(this.phaseProgress),
    };
  }
}
