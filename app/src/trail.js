import * as THREE from 'three';
import { TABLE_HALF, TOOL_OFFSET_MM } from './platform.js';

// ---------------------------------------------------------------------------
// 工作台面轨迹层: 一张 2048² 的 Canvas 贴图, 激光开启时把落点轨迹烧录上去。
// 入参为物理机器坐标 (含刀具偏移): 台面物理范围 X∈[-104,104], Y∈[-70,138]。
//   px = (x + 104) / 208 * SIZE,  py = (y + 70) / 208 * SIZE   (mm → 像素)
// ---------------------------------------------------------------------------
const SIZE = 2048;
const SPAN = TABLE_HALF * 2;              // 208mm
const Y_MIN = TOOL_OFFSET_MM - TABLE_HALF; // -70

export class TableTrail {
  constructor(tableMat) {
    this.canvas = document.createElement('canvas');
    this.canvas.width = this.canvas.height = SIZE;
    this.ctx = this.canvas.getContext('2d');
    this._paintBase();
    this.texture = new THREE.CanvasTexture(this.canvas);
    this.texture.colorSpace = THREE.SRGBColorSpace;
    this.texture.anisotropy = 4;
    tableMat.map = this.texture;
    tableMat.color.set(0xffffff);
    tableMat.needsUpdate = true;
    this._pen = null; // {x,y} 上一落点
  }

  _paintBase() {
    const c = this.ctx;
    c.fillStyle = '#202833';
    c.fillRect(0, 0, SIZE, SIZE);
    // 网格 (每 20mm 一格)
    const step = (20 / (TABLE_HALF * 2)) * SIZE;
    c.strokeStyle = '#2c3646';
    c.lineWidth = 1;
    c.beginPath();
    for (let p = step; p < SIZE; p += step) {
      c.moveTo(p, 0); c.lineTo(p, SIZE);
      c.moveTo(0, p); c.lineTo(SIZE, p);
    }
    c.stroke();
    // 中心十字 (原点)
    c.strokeStyle = '#3d4a61';
    c.lineWidth = 2;
    c.beginPath();
    c.moveTo(SIZE / 2 - 30, SIZE / 2); c.lineTo(SIZE / 2 + 30, SIZE / 2);
    c.moveTo(SIZE / 2, SIZE / 2 - 30); c.lineTo(SIZE / 2, SIZE / 2 + 30);
    c.stroke();
    this._commit();
  }

  _commit() {
    if (this.texture) this.texture.needsUpdate = true;
  }

  _px(x) { return ((x + TABLE_HALF) / SPAN) * SIZE; }
  _py(y) { return ((y - Y_MIN) / SPAN) * SIZE; }

  // 抬笔移动 (不留痕)
  moveTo(x, y) { this._pen = { x, y }; }

  // 落笔绘制到 (x, y)
  lineTo(x, y) {
    const c = this.ctx;
    if (!this._pen) { this._pen = { x, y }; return; }
    c.strokeStyle = '#ff3040';
    c.lineWidth = 3.5;
    c.lineCap = 'round';
    c.lineJoin = 'round';
    c.shadowColor = '#ff3040';
    c.shadowBlur = 10;
    c.beginPath();
    c.moveTo(this._px(this._pen.x), this._py(this._pen.y));
    c.lineTo(this._px(x), this._py(y));
    c.stroke();
    this._pen = { x, y };
    this._commit();
  }

  lift() { this._pen = null; }

  clear() {
    this._pen = null;
    this._paintBase();
  }
}
