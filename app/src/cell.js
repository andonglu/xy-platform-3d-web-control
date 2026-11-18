import * as THREE from 'three';
import { S, TABLE_TOP, TOOL_Z, TOOL_OFFSET_MM } from './platform.js';
import { TableTrail } from './trail.js';
import { PRODUCTION_POSITIONS } from './production.js';

// 自动上下料单元的纯可视化层。所有位置均来自 ProductionCycle 状态包。
export const WORKPIECE = Object.freeze({ widthMm: 180, depthMm: 160, thicknessMm: 3 });

const TOP_Y = TABLE_TOP + WORKPIECE.thicknessMm * S;
const INFEED_X = PRODUCTION_POSITIONS.infeedX * S;
const OUTFEED_X = PRODUCTION_POSITIONS.outfeedX * S;
const PUSHER_HOME = PRODUCTION_POSITIONS.pusherHome * S;
const PUSHER_GAP = PRODUCTION_POSITIONS.pusherGap * S;
const ROD_START_X = PUSHER_HOME - 0.85;

const MAT = {
  frame: new THREE.MeshStandardMaterial({ color: 0x465165, metalness: 0.72, roughness: 0.42 }),
  rail: new THREE.MeshStandardMaterial({ color: 0x9aa8b9, metalness: 0.85, roughness: 0.28 }),
  roller: new THREE.MeshStandardMaterial({ color: 0x273243, metalness: 0.55, roughness: 0.5 }),
  belt: new THREE.MeshStandardMaterial({ color: 0x17202c, metalness: 0.15, roughness: 0.86 }),
  cylinder: new THREE.MeshStandardMaterial({ color: 0x69788c, metalness: 0.8, roughness: 0.3 }),
  clamp: new THREE.MeshStandardMaterial({ color: 0xd59b32, metalness: 0.55, roughness: 0.38 }),
  blank: new THREE.MeshStandardMaterial({ color: 0x9ca9b7, metalness: 0.42, roughness: 0.55 }),
};

function box(w, h, d, mat, x = 0, y = 0, z = 0) {
  const mesh = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
  mesh.position.set(x, y, z);
  mesh.castShadow = true;
  mesh.receiveShadow = true;
  return mesh;
}

function makeLabel(text, x, z) {
  const canvas = document.createElement('canvas');
  canvas.width = 256; canvas.height = 64;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = 'rgba(12,18,28,0.88)';
  ctx.fillRect(0, 0, 256, 64);
  ctx.strokeStyle = '#2dd4bf'; ctx.lineWidth = 2; ctx.strokeRect(2, 2, 252, 60);
  ctx.fillStyle = '#d7dee9'; ctx.font = '600 28px Microsoft YaHei, sans-serif';
  ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(text, 128, 33);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: texture, transparent: true, depthTest: false }));
  sprite.scale.set(3.2, 0.8, 1);
  sprite.position.set(x, TABLE_TOP + 2.1, z);
  return sprite;
}

export class ProductionCell {
  constructor(scene) {
    this.scene = scene;
    this.root = new THREE.Group();
    this.root.name = 'AutomaticProductionCell';
    scene.add(this.root);
    this.current = null;
    this.leaving = [];
    this._buildMechanics();
  }

  _buildMechanics() {
    // 上下料输送线：滚筒轴线沿世界 Z，工件沿世界 X 流动。
    const buildConveyor = (centerX, length) => {
      this.root.add(box(length, 0.32, 9.1, MAT.frame, centerX, TABLE_TOP - 0.34, TOOL_Z));
      this.root.add(box(length - 0.3, 0.08, 8.5, MAT.belt, centerX, TABLE_TOP - 0.17, TOOL_Z));
      for (const side of [-1, 1]) {
        this.root.add(box(length, 0.3, 0.18, MAT.rail, centerX, TABLE_TOP + 0.02, TOOL_Z + side * 4.45));
      }
      const count = Math.floor(length / 0.72);
      for (let i = 0; i <= count; i++) {
        const roller = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.12, 8.25, 12), MAT.roller);
        roller.rotation.x = Math.PI / 2;
        roller.position.set(centerX - length / 2 + 0.3 + i * (length - 0.6) / count, TABLE_TOP - 0.12, TOOL_Z);
        roller.castShadow = true;
        this.root.add(roller);
      }
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
        this.root.add(box(0.35, TABLE_TOP - 0.25, 0.35, MAT.frame,
          centerX + sx * (length / 2 - 0.5), (TABLE_TOP - 0.25) / 2, TOOL_Z + sz * 3.8));
      }
    };
    buildConveyor(INFEED_X, 10.5);
    buildConveyor(OUTFEED_X + 1.5, 13);

    // 料仓框架及待加工毛坯，当前工件从最底层被推出。
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
      this.root.add(box(0.18, 2.25, 0.18, MAT.rail,
        INFEED_X + sx * 4.65, TABLE_TOP + 1.05, TOOL_Z + sz * 4.15));
    }
    for (let i = 0; i < 3; i++) {
      this.root.add(box(WORKPIECE.widthMm * S, 0.11, WORKPIECE.depthMm * S, MAT.blank,
        INFEED_X, TOP_Y + 0.25 + i * 0.15, TOOL_Z));
    }

    // 长行程推料气缸，推杆长度随状态包中的 pusherX 改变。
    const cylinder = new THREE.Mesh(new THREE.CylinderGeometry(0.34, 0.34, 1.5, 20), MAT.cylinder);
    cylinder.rotation.z = Math.PI / 2;
    cylinder.position.set(ROD_START_X - 0.55, TABLE_TOP + 0.43, TOOL_Z);
    cylinder.castShadow = true;
    this.root.add(cylinder);
    this.pusherRod = new THREE.Mesh(new THREE.CylinderGeometry(0.11, 0.11, 1, 12), MAT.rail);
    this.pusherRod.rotation.z = Math.PI / 2;
    this.root.add(this.pusherRod);
    this.pusherPlate = box(0.22, 0.78, 8.35, MAT.clamp, PUSHER_HOME, TABLE_TOP + 0.43, TOOL_Z);
    this.root.add(this.pusherPlate);

    // 加工位定位挡块与左右夹爪。
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
      this.root.add(box(0.32, 0.28, 0.5, MAT.frame, sx * 4.82, TABLE_TOP + 0.14, TOOL_Z + sz * 4.15));
    }
    this.clampLeft = box(0.62, 0.38, 1.65, MAT.clamp, -6, TABLE_TOP + 0.25, TOOL_Z);
    this.clampRight = box(0.62, 0.38, 1.65, MAT.clamp, 6, TABLE_TOP + 0.25, TOOL_Z);
    this.root.add(this.clampLeft, this.clampRight);
    this.root.add(box(1.2, 0.3, 2.1, MAT.frame, -6.35, TABLE_TOP - 0.02, TOOL_Z));
    this.root.add(box(1.2, 0.3, 2.1, MAT.frame, 6.35, TABLE_TOP - 0.02, TOOL_Z));

    // 三色状态灯。
    this.root.add(box(0.1, 2.3, 0.1, MAT.rail, -6.7, TABLE_TOP + 1.15, TOOL_Z - 4.7));
    this.lampMat = new THREE.MeshStandardMaterial({ color: 0x245b48, emissive: 0x123d30, emissiveIntensity: 1.2 });
    this.lamp = new THREE.Mesh(new THREE.SphereGeometry(0.24, 16, 12), this.lampMat);
    this.lamp.position.set(-6.7, TABLE_TOP + 2.4, TOOL_Z - 4.7);
    this.root.add(this.lamp);

    this.root.add(makeLabel('料仓 / 上料', INFEED_X, TOOL_Z - 4.9));
    this.root.add(makeLabel('激光加工位', 0, TOOL_Z - 5.6));
    this.root.add(makeLabel('成品 / 下料', OUTFEED_X + 1, TOOL_Z - 4.9));
  }

  _createWorkpiece(id, xMm) {
    const group = new THREE.Group();
    group.name = `Workpiece:${id}`;
    const width = WORKPIECE.widthMm * S;
    const depth = WORKPIECE.depthMm * S;
    const thickness = WORKPIECE.thicknessMm * S;

    const body = new THREE.Mesh(new THREE.BoxGeometry(width, thickness, depth), MAT.blank);
    body.position.y = -thickness / 2;
    body.castShadow = true;
    body.receiveShadow = true;
    group.add(body);

    const topMat = new THREE.MeshStandardMaterial({ color: 0xffffff, metalness: 0.25, roughness: 0.62 });
    const trail = new TableTrail(topMat, {
      widthMm: WORKPIECE.widthMm,
      depthMm: WORKPIECE.depthMm,
      centerX: 0,
      centerY: TOOL_OFFSET_MM,
      baseColor: '#aeb9c5',
      gridColor: '#9aa7b4',
      crossColor: '#8796a6',
      gridStepMm: 20,
    });
    const top = new THREE.Mesh(new THREE.PlaneGeometry(width, depth).rotateX(-Math.PI / 2), topMat);
    top.position.y = 0.003;
    top.receiveShadow = true;
    group.add(top);

    group.position.set(xMm * S, TOP_Y, TOOL_Z);
    this.root.add(group);
    return { id, group, trail, topMat, targetX: xMm * S };
  }

  _disposeWorkpiece(part) {
    this.root.remove(part.group);
    part.group.traverse(obj => { if (obj.geometry) obj.geometry.dispose(); });
    part.trail.dispose();
    part.topMat.dispose();
  }

  update(production, dt) {
    const id = production?.workpieceId ?? null;
    if (id !== (this.current?.id ?? null)) {
      if (this.current) {
        if (id === null) this._disposeWorkpiece(this.current);
        else {
          // 新毛坯出料时，上一成品继续随下料线离开画面。
          this.current.targetX = (PRODUCTION_POSITIONS.outfeedX + 140) * S;
          this.leaving.push(this.current);
        }
      }
      this.current = id === null ? null : this._createWorkpiece(id, production.workpieceX ?? PRODUCTION_POSITIONS.infeedX);
    }

    const k = 1 - Math.exp(-dt * 18);
    if (this.current && production) {
      this.current.targetX = (production.workpieceX ?? PRODUCTION_POSITIONS.infeedX) * S;
      this.current.group.position.x += (this.current.targetX - this.current.group.position.x) * k;
    }

    for (let i = this.leaving.length - 1; i >= 0; i--) {
      const part = this.leaving[i];
      part.group.position.x += (part.targetX - part.group.position.x) * (1 - Math.exp(-dt * 4));
      if (Math.abs(part.targetX - part.group.position.x) < 0.2) {
        this._disposeWorkpiece(part);
        this.leaving.splice(i, 1);
      }
    }

    const pusherTarget = (production?.pusherX ?? PRODUCTION_POSITIONS.pusherHome) * S;
    this.pusherPlate.position.x += (pusherTarget - this.pusherPlate.position.x) * k;
    const rodEnd = this.pusherPlate.position.x - 0.12;
    const rodLength = Math.max(0.05, rodEnd - ROD_START_X);
    this.pusherRod.scale.y = rodLength;
    this.pusherRod.position.set(ROD_START_X + rodLength / 2, TABLE_TOP + 0.43, TOOL_Z);

    const clamp = production?.clamp ?? 0;
    const clampX = (120 - 22 * clamp) * S;
    this.clampLeft.position.x += (-clampX - this.clampLeft.position.x) * k;
    this.clampRight.position.x += (clampX - this.clampRight.position.x) * k;

    const phase = production?.phase ?? 'Idle';
    const color = phase === 'Faulted' ? 0xff3344 : (phase === 'Processing' ? 0xffb82e : (production?.running ? 0x2dd4bf : 0x245b48));
    this.lampMat.color.setHex(color);
    this.lampMat.emissive.setHex(color);
  }

  getProcessingTrail(production) {
    return this.current && production?.phase === 'Processing' ? this.current.trail : null;
  }

  getSurfaceHeight(production) {
    if (!this.current || !production) return TABLE_TOP;
    return ['Clamping', 'Processing', 'Unclamping'].includes(production.phase) ? TOP_Y : TABLE_TOP;
  }

  get currentTrail() { return this.current?.trail ?? null; }

  clearWorkpieces() {
    if (this.current) this._disposeWorkpiece(this.current);
    this.current = null;
    for (const part of this.leaving) this._disposeWorkpiece(part);
    this.leaving.length = 0;
  }
}
