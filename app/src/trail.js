import * as THREE from 'three';
import { TABLE_HALF, TOOL_OFFSET_MM } from './platform.js';

// ---------------------------------------------------------------------------
// 可绑定台面或单个工件的轨迹层: 一张 2048² Canvas 贴图。
// 输入始终是机器坐标；centerX/centerY 指定贴图中心的机器坐标。
// ---------------------------------------------------------------------------
const SIZE = 2048;

export class TableTrail {
  constructor(tableMat, options = {}) {
    // 默认仍是原来的整个台面；自动生产时为每件工件传入独立尺寸。
    this.widthMm = options.widthMm ?? TABLE_HALF * 2;
    this.depthMm = options.depthMm ?? TABLE_HALF * 2;
    this.centerX = options.centerX ?? 0;
    this.centerY = options.centerY ?? TOOL_OFFSET_MM;
    this.baseColor = options.baseColor ?? '#202833';
    this.gridColor = options.gridColor ?? '#2c3646';
    this.crossColor = options.crossColor ?? '#3d4a61';
    this.gridStepMm = options.gridStepMm ?? 20;
    this.showCross = options.showCross ?? true;
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
    c.shadowBlur = 0;
    c.shadowColor = 'transparent';
    c.fillStyle = this.baseColor;
    c.fillRect(0, 0, SIZE, SIZE);
    // 网格按工件实际尺寸映射，避免换料后共用上一件的贴图。
    const stepX = (this.gridStepMm / this.widthMm) * SIZE;
    const stepY = (this.gridStepMm / this.depthMm) * SIZE;
    c.strokeStyle = this.gridColor;
    c.lineWidth = 1;
    c.beginPath();
    for (let p = stepX; p < SIZE; p += stepX) { c.moveTo(p, 0); c.lineTo(p, SIZE); }
    for (let p = stepY; p < SIZE; p += stepY) { c.moveTo(0, p); c.lineTo(SIZE, p); }
    c.stroke();
    if (this.showCross) {
      const cx = this._px(this.centerX), cy = this._py(this.centerY);
      c.strokeStyle = this.crossColor;
      c.lineWidth = 2;
      c.beginPath();
      c.moveTo(cx - 30, cy); c.lineTo(cx + 30, cy);
      c.moveTo(cx, cy - 30); c.lineTo(cx, cy + 30);
      c.stroke();
    }
    this._commit();
  }

  _commit() {
    if (this.texture) this.texture.needsUpdate = true;
  }

  _px(x) { return ((x - this.centerX) / this.widthMm + 0.5) * SIZE; }
  _py(y) { return ((y - this.centerY) / this.depthMm + 0.5) * SIZE; }

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

  dispose() {
    this.texture.dispose();
    this._pen = null;
  }
}
