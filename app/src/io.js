import { PRODUCTION_POSITIONS } from './production.js';

// ---------------------------------------------------------------------------
// 虚拟 IO 层 —— 数字量输出驱动执行机构，动作结果映射回数字量输入。
// 上位机（WPF）用这些原语自己编排生产流程；device 模式则由 ProductionCycle 接管。
//   outputs: clamp 夹具 / feeder 料仓出料 / pusher 推料上料 / unloader 推料下料
//   推杆板前沿 = 推杆位置 + 96mm，推杆前进时带动未夹紧的工件。
// ---------------------------------------------------------------------------

const CLAMP_STROKE_TIME = 0.7;                                                       // s，夹具全行程
const FEED_TIME = 0.4;                                                               // s，料仓出料到上料位
const PUSHER_SPEED = 280;                                                            // mm/s
const PUSHER_RETRACTED = PRODUCTION_POSITIONS.pusherHome;                             // -305
const PUSHER_LOADED = -PRODUCTION_POSITIONS.pusherGap;                                // -96  → 板前沿 0
const PUSHER_OUTFEED = PRODUCTION_POSITIONS.outfeedX - PRODUCTION_POSITIONS.pusherGap; // 104 → 板前沿 200
const INFEED_X = PRODUCTION_POSITIONS.infeedX;                                         // -200
const OUTFEEX_X = PRODUCTION_POSITIONS.outfeedX;                                       // 200
const GAP = PRODUCTION_POSITIONS.pusherGap;                                            // 96

export const IO_OUTPUTS = Object.freeze({
  clamp: '夹具夹紧阀',
  feeder: '料仓出料阀',
  pusher: '推料上料阀',
  unloader: '推料下料阀',
});

export const IO_INPUTS = Object.freeze({
  clampClosed: '夹具夹紧到位',
  clampOpen: '夹具松开到位',
  pusherExtended: '推料上料到位',
  pusherRetracted: '推料上料退回到位',
  unloaderExtended: '推料下料到位',
  unloaderRetracted: '推料下料退回到位',
  workpiecePresent: '工件在位',
  workpieceAtFixture: '工件在加工位',
  workpieceAtOutfeed: '工件在下料位',
});

const round = value => +value.toFixed(3);
const approach = (current, target, maxStep) => {
  const dist = target - current;
  return Math.abs(dist) <= maxStep ? target : current + Math.sign(dist) * maxStep;
};

export class IoController {
  constructor({ production, event }) {
    this.production = production;
    this.event = event;
    this.outputs = { clamp: false, feeder: false, pusher: false, unloader: false };
    this.clamp = 0;                     // 0=松开, 1=夹紧
    this.pusherX = PUSHER_RETRACTED;    // 上料推杆位置
    this.unloaderX = PUSHER_RETRACTED;  // 下料推杆位置
    this.workpieceX = null;             // 工件中心位置，null = 产线上无工件
    this.workpieceId = null;
    this.serial = 0;
    this._feedElapsed = 0;
  }

  /** 写数字量输出；返回 { ok, msg }。 */
  write(id, on) {
    if (!(id in this.outputs)) {
      return { ok: false, msg: `未知输出点: ${id}（可用: ${Object.keys(this.outputs).join(', ')}）` };
    }
    if (this.production.running) {
      return { ok: false, msg: '设备本地自动生产中，IO 由内置流程控制，请先停止生产' };
    }
    if (id === 'feeder' && on && this.workpieceX !== null) {
      return { ok: false, msg: '产线上仍有工件，不能出料' };
    }
    if (this.outputs[id] === on) {
      return { ok: true, msg: `IO ${id} 已是 ${on ? 'ON' : 'OFF'}` };
    }
    this.outputs[id] = on;
    if (id === 'feeder') this._feedElapsed = 0;
    return { ok: true, msg: `IO 写入 ${IO_OUTPUTS[id]}(${id}) = ${on ? 'ON' : 'OFF'}` };
  }

  /** 复位：输出全关、机构回初始位、清空工件。 */
  reset() {
    this.outputs = { clamp: false, feeder: false, pusher: false, unloader: false };
    this.clamp = 0;
    this.pusherX = PUSHER_RETRACTED;
    this.unloaderX = PUSHER_RETRACTED;
    this.workpieceX = null;
    this.workpieceId = null;
    this._feedElapsed = 0;
  }

  tick(dt) {
    const prod = this.production.state();
    if (this.production.running) {
      // 设备本地模式：IO 跟随内置生产流程
      this.clamp = prod.clamp;
      this.pusherX = prod.pusherX;
      this.unloaderX = PUSHER_RETRACTED;
      this.workpieceX = prod.workpieceX;
      this.workpieceId = prod.workpieceId;
      this.outputs.clamp = prod.clamp > 0.5;
      this.outputs.pusher = prod.pusherX > PUSHER_RETRACTED + 20;
      this.outputs.feeder = false;
      this.outputs.unloader = false;
      return;
    }

    // 手动 / 上位机模式：输出点驱动机构
    this.clamp = approach(this.clamp, this.outputs.clamp ? 1 : 0, dt / CLAMP_STROKE_TIME);
    const step = PUSHER_SPEED * dt;
    this.pusherX = approach(this.pusherX, this.outputs.pusher ? PUSHER_LOADED : PUSHER_RETRACTED, step);
    this.unloaderX = approach(this.unloaderX, this.outputs.unloader ? PUSHER_OUTFEED : PUSHER_RETRACTED, step);

    if (this.outputs.feeder) {
      this._feedElapsed += dt;
      if (this.workpieceX === null && this._feedElapsed >= FEED_TIME) {
        this.serial++;
        this.workpieceId = `P${String(this.serial).padStart(3, '0')}`;
        this.workpieceX = INFEED_X;
        this.event('info', `${this.workpieceId} 料仓出料到位`);
      }
    }

    this._pushWorkpiece();
  }

  /** 推杆板前沿超过工件中心且未夹紧时带动工件前进。 */
  _pushWorkpiece() {
    if (this.workpieceX === null || this.clamp > 0.5) return;
    const front = Math.max(this.pusherX, this.unloaderX) + GAP;
    if (front > this.workpieceX) {
      this.workpieceX = Math.min(front, OUTFEEX_X);
    }
    // 工件已到下料位且推杆全部退回 → 由下游接走，产线清空
    if (this.workpieceX >= OUTFEEX_X - 0.5
      && this.pusherX <= PUSHER_RETRACTED + 1
      && this.unloaderX <= PUSHER_RETRACTED + 1) {
      this.event('info', `${this.workpieceId} 已下料`);
      this.workpieceX = null;
      this.workpieceId = null;
    }
  }

  inputs() {
    const wx = this.workpieceX;
    return {
      clampClosed: this.clamp >= 0.98,
      clampOpen: this.clamp <= 0.02,
      pusherExtended: Math.abs(this.pusherX - PUSHER_LOADED) <= 1,
      pusherRetracted: Math.abs(this.pusherX - PUSHER_RETRACTED) <= 1,
      unloaderExtended: Math.abs(this.unloaderX - PUSHER_OUTFEED) <= 1,
      unloaderRetracted: Math.abs(this.unloaderX - PUSHER_RETRACTED) <= 1,
      workpiecePresent: wx !== null,
      workpieceAtFixture: wx !== null && Math.abs(wx) <= 1,
      workpieceAtOutfeed: wx !== null && wx >= OUTFEEX_X - 1,
    };
  }

  state() {
    return {
      mode: this.production.running ? 'Auto' : 'Manual',
      outputs: { ...this.outputs },
      inputs: this.inputs(),
      clamp: round(this.clamp),
      pusherX: round(Math.max(this.pusherX, this.unloaderX)),
      workpieceId: this.workpieceId,
      workpieceX: this.workpieceX === null ? null : round(this.workpieceX),
    };
  }
}