import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { STLLoader } from 'three/addons/loaders/STLLoader.js';

// ---------------------------------------------------------------------------
// 3D 点胶机模型: 真正的三轴联动机械结构 (Cartesian / Gantry)
//
// 坐标约定 (重要, 全文件统一):
//   工作坐标 (x, y, z):  x=左右(X轴行程), y=前后(Y轴行程), z=上下(Z轴行程/高度)
//   Three.js (X, Y, Z): X=x, Y=z(高度), Z=y
//   台面平面位于 Three.Y = 0
//
// 运动学层级 (龙门式 gantry):
//   machineRoot (静态机架: 底座/台面/Y导轨/立柱)
//     └─ yCarriage  : position.z = y        (Y 滑座: 立柱 + 龙门横梁 + X导轨)
//          └─ xCarriage : position.x = x    (X 滑座: 滑台板 + Z导轨)
//               └─ zCarriage : position.y = z - gantryHeight  (Z 滑座: 阀体 + 针头)
//                    └─ 针尖 (局部原点) → 世界坐标 = (x, z, y)  ✅ 三轴真实联动
//
// 悬臂式 cantilever: 同 gantry, 但 Y 滑座只有单侧立柱, 横梁悬臂伸出
// 工作台移动式 movingTable: 龙门固定, xCarriage 直挂机架; 台面(工件)随 Y 移动
// ---------------------------------------------------------------------------

let scene, camera, renderer, controls;
let machineRoot = null;
let yCarriage = null, xCarriage = null, zCarriage = null;
let needleTilt = null;               // P4: 针头姿态组 (绕枢轴 = zCarriage 原点)
let needleHead = null, needleTip = null, beamMesh = null;
let sensorGroup = null, workspaceBox = null, labelsGroup = null;
let trailPoints = [], trailLine = null;
let previewLine = null, cadModel = null;
let groundGrid = null;
let productionRoot = null, fixtureRoot = null, workpieceRoot = null;
let productionSourceGeometry = null, productionModelMeta = null;
let productionConfigMsg = null, productionSnapshot = null;
let clampMeshes = [];

// 可复用的传感器标记引用: { x: {home, limPos, limNeg}, y: {...}, z: {...} }
let sensorMarkers = {};

// ---------------------------------------------------------------------------
// 机器配置 (可通过前端面板"创建/重建模型"修改)
// ---------------------------------------------------------------------------
export const DEFAULT_MACHINE = {
  type: 'gantry',             // gantry | cantilever | movingTable
  xMin: -150, xMax: 150,
  yMin: -150, yMax: 150,
  zMin: 0, zMax: 80,
  tableSize: 320,             // 台面边长 mm
  gantryHeight: 130,          // 龙门横梁中心线高度 (世界 Y) mm
  homeSensor: { x: 0, y: 0, z: 40 },
  limitNeg: { x: -148, y: -148, z: 2 },
  limitPos: { x: 148, y: 148, z: 78 },
  showWorkspace: true,
  showSensors: true,
  showLabels: true,
};

let machineConfig = cloneConfig(DEFAULT_MACHINE);

function cloneConfig(c) {
  return {
    ...c,
    homeSensor: { ...c.homeSensor },
    limitNeg: { ...c.limitNeg },
    limitPos: { ...c.limitPos },
  };
}

// 材质调色板 (暗色工业科技风)
const MAT = {
  base: new THREE.MeshPhongMaterial({ color: 0x2b3040, shininess: 40 }),
  table: new THREE.MeshPhongMaterial({ color: 0x1b2030, shininess: 60, side: THREE.DoubleSide }),
  frame: new THREE.MeshPhongMaterial({ color: 0x8b95a8, shininess: 90 }),
  rail: new THREE.MeshPhongMaterial({ color: 0xb8c2d4, shininess: 120 }),
  beam: new THREE.MeshPhongMaterial({ color: 0x46536e, shininess: 70 }),
  carriage: new THREE.MeshPhongMaterial({ color: 0x36507a, shininess: 80 }),
  zrail: new THREE.MeshPhongMaterial({ color: 0x6c7c9c, shininess: 100 }),
  valve: new THREE.MeshPhongMaterial({ color: 0xd2602a, shininess: 90 }),
  needle: new THREE.MeshPhongMaterial({ color: 0x2ad0c0, emissive: 0x003840, shininess: 120 }),
  tip: new THREE.MeshBasicMaterial({ color: 0xffe066 }),
  glue: new THREE.MeshBasicMaterial({ color: 0xff7a1a, transparent: true, opacity: 0.75 }),
  sensorHome: new THREE.MeshPhongMaterial({ color: 0x1a6b46, emissive: 0x001a0c }),
  sensorLimit: new THREE.MeshPhongMaterial({ color: 0x7a2222, emissive: 0x1a0000 }),
};

// ---------------------------------------------------------------------------
// 初始化场景
// ---------------------------------------------------------------------------
export function initScene(container) {
  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0a0d18);
  scene.fog = new THREE.Fog(0x0a0d18, 600, 1400);

  camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 3000);
  camera.position.set(330, 250, 360);

  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  renderer.setSize(container.clientWidth, container.clientHeight);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  container.appendChild(renderer.domElement);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.target.set(0, 45, 0);
  controls.maxPolarAngle = Math.PI * 0.495;   // 不允许翻到地面以下
  controls.minDistance = 120;
  controls.maxDistance = 1200;

  // 灯光
  scene.add(new THREE.AmbientLight(0x8899bb, 0.75));
  const key = new THREE.DirectionalLight(0xffffff, 0.95);
  key.position.set(180, 320, 220);
  scene.add(key);
  const fill = new THREE.DirectionalLight(0x88aaff, 0.45);
  fill.position.set(-220, 160, -180);
  scene.add(fill);
  const rim = new THREE.DirectionalLight(0xffd9a0, 0.3);
  rim.position.set(0, 80, -260);
  scene.add(rim);

  // 地面网格 (GridHelper 默认就在 XZ 水平面, 不能旋转!)
  groundGrid = new THREE.GridHelper(900, 45, 0x334466, 0x1b2236);
  groundGrid.position.y = -14;
  scene.add(groundGrid);

  buildMachine(DEFAULT_MACHINE);

  window.addEventListener('resize', onResize);
  animate();
}

// ---------------------------------------------------------------------------
// 创建 / 重建三轴点胶机模型
// ---------------------------------------------------------------------------
export function buildMachine(cfg) {
  machineConfig = cloneConfig({ ...machineConfig, ...cfg });

  // 清理旧模型 (释放几何体, 材质为全局共享不释放)
  if (machineRoot) {
    scene.remove(machineRoot);
    disposeGeometries(machineRoot);
  }
  sensorMarkers = {};

  const c = machineConfig;
  const span = Math.max(
    Math.abs(c.xMax - c.xMin),
    Math.abs(c.yMax - c.yMin),
    c.tableSize
  ) + 40;                       // 机架外形尺寸
  const H = c.gantryHeight;     // 横梁高度

  machineRoot = new THREE.Group();
  machineRoot.name = 'MachineRoot';
  scene.add(machineRoot);

  buildStaticFrame(c, span, H);
  buildAxes(c, span, H);
  buildSensors(c, span, H);
  buildWorkspace(c);
  buildAxisLabels(c, span, H);

  applyVisibility();
  return cloneConfig(machineConfig);
}

export function getMachineConfig() { return cloneConfig(machineConfig); }

/** 静态机架: 底座、台面、Y 导轨、地脚 */
function buildStaticFrame(c, span, H) {
  const g = new THREE.Group();
  g.name = 'StaticFrame';
  machineRoot.add(g);

  // 大理石底座
  const base = new THREE.Mesh(new THREE.BoxGeometry(span, 14, span), MAT.base);
  base.position.y = -8;
  g.add(base);

  // 工作台面 (工作坐标 z=0 平面)
  const table = new THREE.Mesh(new THREE.BoxGeometry(c.tableSize, 2, c.tableSize), MAT.table);
  table.position.y = 0;
  g.add(table);

  // 台面网格线 (辅助看清 XY 位置)
  const tGrid = new THREE.GridHelper(c.tableSize, 16, 0x3d5a80, 0x28354a);
  tGrid.position.y = 1.2;
  g.add(tGrid);

  // Y 轴导轨 (沿 Three.Z 前后方向, 固定在机架两侧)
  const railGeo = new THREE.BoxGeometry(14, 12, span - 10);
  const railL = new THREE.Mesh(railGeo, MAT.rail);
  railL.position.set(-span / 2 + 12, 2, 0);
  g.add(railL);
  const railR = railL.clone();
  railR.position.x = span / 2 - 12;
  g.add(railR);

  // 四个地脚
  const footGeo = new THREE.CylinderGeometry(9, 11, 6, 12);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
    const foot = new THREE.Mesh(footGeo, MAT.frame);
    foot.position.set(sx * (span / 2 - 22), -17, sz * (span / 2 - 22));
    g.add(foot);
  }

  // 工作台移动式: 龙门固定在机架上 (立柱 + 横梁), 不随 Y 移动
  if (c.type === 'movingTable') {
    const colGeo = new THREE.BoxGeometry(16, H, 16);
    for (const sx of [-1, 1]) {
      const col = new THREE.Mesh(colGeo, MAT.frame);
      col.position.set(sx * (span / 2 - 12), H / 2 + 2, 0);
      g.add(col);
    }
    const cross = new THREE.Mesh(new THREE.BoxGeometry(span - 20, 18, 22), MAT.beam);
    cross.position.set(0, H, 0);
    g.add(cross);
    // X 导轨
    const xr = new THREE.Mesh(new THREE.BoxGeometry(span - 30, 8, 8), MAT.rail);
    xr.position.set(0, H + 11, 12);
    g.add(xr);
  }
}

/** 三轴滑座 (父子联动层级) */
function buildAxes(c, span, H) {
  const isMovingTable = c.type === 'movingTable';
  const isCantilever = c.type === 'cantilever';

  // ---------- Y 轴滑座 ----------
  yCarriage = new THREE.Group();
  yCarriage.name = 'YCarriage';
  machineRoot.add(yCarriage);

  if (isMovingTable) {
    // 工作台移动式: Y 滑座就是移动台面 (工件随 Y 运动)
    const movingTable = new THREE.Mesh(
      new THREE.BoxGeometry(c.tableSize * 0.78, 6, c.tableSize * 0.78), MAT.carriage);
    movingTable.position.y = 4;
    yCarriage.add(movingTable);
    const mGrid = new THREE.GridHelper(c.tableSize * 0.78, 12, 0x5a86c0, 0x2c4468);
    mGrid.position.y = 7.2;
    yCarriage.add(mGrid);
  } else {
    // 龙门式 / 悬臂式: Y 滑座 = 立柱 + 顶部横梁 + X 导轨
    const colGeo = new THREE.BoxGeometry(18, H, 20);
    if (isCantilever) {
      // 悬臂式: 只有单侧立柱
      const col = new THREE.Mesh(colGeo, MAT.frame);
      col.position.set(-span / 2 + 14, H / 2 + 2, 0);
      yCarriage.add(col);
    } else {
      for (const sx of [-1, 1]) {
        const col = new THREE.Mesh(colGeo, MAT.frame);
        col.position.set(sx * (span / 2 - 14), H / 2 + 2, 0);
        yCarriage.add(col);
      }
    }
    // 龙门横梁 (沿 X 方向)
    const cross = new THREE.Mesh(new THREE.BoxGeometry(span - 24, 20, 26), MAT.beam);
    cross.position.set(0, H, 0);
    yCarriage.add(cross);
    // X 轴导轨 (横梁上, 沿 X 方向)
    const xr = new THREE.Mesh(new THREE.BoxGeometry(span - 34, 8, 8), MAT.rail);
    xr.position.set(0, H + 12, 14);
    yCarriage.add(xr);
    const xr2 = xr.clone();
    xr2.position.y = H - 12;
    yCarriage.add(xr2);
    // 拖链 (装饰)
    const chain = new THREE.Mesh(new THREE.BoxGeometry(span * 0.5, 4, 6), MAT.frame);
    chain.position.set(span * 0.18, H + 20, -8);
    yCarriage.add(chain);
  }

  // ---------- X 轴滑座 ----------
  xCarriage = new THREE.Group();
  xCarriage.name = 'XCarriage';
  xCarriage.position.set(0, H, 0);   // 位于横梁高度
  (isMovingTable ? machineRoot : yCarriage).add(xCarriage);

  // 滑台板
  const plate = new THREE.Mesh(new THREE.BoxGeometry(40, 34, 40), MAT.carriage);
  plate.position.set(0, 0, 14);
  xCarriage.add(plate);

  // Z 轴导轨 (竖直, 从横梁向下延伸到工作区; 位于滑座后方)
  const zRailLen = H - c.zMin + 30;
  const zRail = new THREE.Mesh(new THREE.BoxGeometry(10, zRailLen, 10), MAT.zrail);
  zRail.position.set(0, -zRailLen / 2 + 10, -14);
  xCarriage.add(zRail);

  // ---------- Z 轴滑座 ----------
  zCarriage = new THREE.Group();
  zCarriage.name = 'ZCarriage';
  // 相对 X 滑座向下偏移, 使针尖世界高度 = 工作 z
  zCarriage.position.set(0, -H, 0);
  xCarriage.add(zCarriage);

  // 阀体
  const valve = new THREE.Mesh(new THREE.CylinderGeometry(11, 11, 34, 16), MAT.valve);
  valve.position.set(0, 27, 0);
  zCarriage.add(valve);
  // 胶管接头
  const cap = new THREE.Mesh(new THREE.CylinderGeometry(6, 8, 10, 12), MAT.frame);
  cap.position.set(0, 47, 0);
  zCarriage.add(cap);
  // Z 滑块 (连接导轨)
  const slider = new THREE.Mesh(new THREE.BoxGeometry(20, 26, 16), MAT.carriage);
  slider.position.set(0, 24, -14);
  zCarriage.add(slider);

  // 针头 (锥尖朝下, 尖端在局部 y=0 = 针尖); P4: 包进 needleTilt 姿态组, 绕枢轴偏摆
  needleTilt = new THREE.Group();
  zCarriage.add(needleTilt);

  needleHead = new THREE.Mesh(new THREE.ConeGeometry(6, 20, 12), MAT.needle);
  needleHead.rotation.x = Math.PI;   // 尖端朝下
  needleHead.position.set(0, 10, 0);
  needleTilt.add(needleHead);

  // 针尖 (发光点, 局部原点 = 世界 (x, z, y))
  needleTip = new THREE.Mesh(new THREE.SphereGeometry(2, 10, 10), MAT.tip);
  needleTip.position.set(0, 0, 0);
  needleTilt.add(needleTip);

  // 胶束 (针尖到台面, 长度动态; 五轴姿态下隐藏 —— 胶束实际沿针尖流下)
  beamMesh = new THREE.Mesh(new THREE.CylinderGeometry(0.9, 0.9, 1, 8), MAT.glue);
  beamMesh.position.set(0, -0.5, 0);
  beamMesh.scale.y = 1;
  beamMesh.visible = false;
  zCarriage.add(beamMesh);
}

// ---------------------------------------------------------------------------
// 传感器标记 (零点开关 / 正负极限), 与服务端 config_axis 的位置对应
// ---------------------------------------------------------------------------
function buildSensors(c, span, H) {
  sensorGroup = new THREE.Group();
  sensorGroup.name = 'Sensors';
  machineRoot.add(sensorGroup);

  const S = 9;   // 标记尺寸
  const mkGeo = new THREE.BoxGeometry(S, S, S);

  function marker(parent, pos, kind, axis, which) {
    const mat = (kind === 'home' ? MAT.sensorHome : MAT.sensorLimit).clone();
    const m = new THREE.Mesh(mkGeo, mat);
    m.position.set(...pos);
    m.userData = { axis, which, kind, baseEmissive: mat.emissive.getHex() };
    parent.add(m);
    if (!sensorMarkers[axis]) sensorMarkers[axis] = {};
    sensorMarkers[axis][which] = m;
    return m;
  }

  // X 轴传感器: 沿横梁 (X 方向), 随 Y 滑座移动 (movingTable 时随机架)
  const xParent = c.type === 'movingTable' ? machineRoot : yCarriage;
  const xY = H + 22;
  marker(xParent, [c.limitNeg.x, xY, 0], 'limit', 'x', 'limNeg');
  marker(xParent, [c.homeSensor.x, xY, 0], 'home', 'x', 'home');
  marker(xParent, [c.limitPos.x, xY, 0], 'limit', 'x', 'limPos');

  // Y 轴传感器: 沿底座导轨 (Z 方向), 固定在机架
  const yX = -span / 2 - 4;
  marker(machineRoot, [yX, 4, c.limitNeg.y], 'limit', 'y', 'limNeg');
  marker(machineRoot, [yX, 4, c.homeSensor.y], 'home', 'y', 'home');
  marker(machineRoot, [yX, 4, c.limitPos.y], 'limit', 'y', 'limPos');

  // Z 轴传感器: 沿 Z 导轨, 相对 X 滑座
  const zX = 16;
  const zLocalY = (val) => val - H;
  marker(xCarriage, [zX, zLocalY(c.limitNeg.z), 0], 'limit', 'z', 'limNeg');
  marker(xCarriage, [zX, zLocalY(c.homeSensor.z), 0], 'home', 'z', 'home');
  marker(xCarriage, [zX, zLocalY(c.limitPos.z), 0], 'limit', 'z', 'limPos');
}

/** 传感器信号变化 → 标记亮起 (由服务端 state.sensors 驱动) */
export function setSensorSignals(signals) {
  if (!signals) return;
  for (const axis of ['x', 'y', 'z']) {
    const s = signals[axis];
    const group = sensorMarkers[axis];
    if (!s || !group) continue;
    for (const which of ['home', 'limPos', 'limNeg']) {
      const m = group[which];
      if (!m) continue;
      const on = !!s[which];
      m.material.emissive.setHex(on ? 0x00ff66 : m.userData.baseEmissive);
      m.material.color.setHex(on ? 0x66ffaa : (m.userData.kind === 'home' ? 0x1a6b46 : 0x7a2222));
      m.scale.setScalar(on ? 1.35 : 1);
    }
  }
}

// ---------------------------------------------------------------------------
// 工作空间包围盒 (显示三轴行程范围)
// ---------------------------------------------------------------------------
function buildWorkspace(c) {
  if (workspaceBox) { scene.remove(workspaceBox); disposeGeometries(workspaceBox); }
  const w = c.xMax - c.xMin, h = c.zMax - c.zMin, d = c.yMax - c.yMin;
  const geo = new THREE.BoxGeometry(w, h, d);
  const edges = new THREE.EdgesGeometry(geo);
  workspaceBox = new THREE.LineSegments(edges,
    new THREE.LineDashedMaterial({ color: 0x4d90ff, dashSize: 8, gapSize: 5, transparent: true, opacity: 0.55 }));
  workspaceBox.computeLineDistances();
  workspaceBox.position.set((c.xMin + c.xMax) / 2, (c.zMin + c.zMax) / 2, (c.yMin + c.yMax) / 2);
  workspaceBox.name = 'Workspace';
  scene.add(workspaceBox);
  geo.dispose();
}

// ---------------------------------------------------------------------------
// X/Y/Z 轴方向标签 (canvas sprite, 不引入额外依赖)
// ---------------------------------------------------------------------------
function makeLabel(text, color) {
  const cv = document.createElement('canvas');
  cv.width = 128; cv.height = 64;
  const ctx = cv.getContext('2d');
  ctx.fillStyle = 'rgba(10,14,26,0.75)';
  ctx.fillRect(0, 0, 128, 64);
  ctx.strokeStyle = color; ctx.lineWidth = 3;
  ctx.strokeRect(2, 2, 124, 60);
  ctx.fillStyle = color;
  ctx.font = 'bold 34px Consolas, monospace';
  ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
  ctx.fillText(text, 64, 34);
  const tex = new THREE.CanvasTexture(cv);
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, transparent: true, depthTest: false }));
  sprite.scale.set(28, 14, 1);
  return sprite;
}

function buildAxisLabels(c, span, H) {
  if (labelsGroup) { scene.remove(labelsGroup); disposeGeometries(labelsGroup); }
  labelsGroup = new THREE.Group();
  labelsGroup.name = 'AxisLabels';

  const r = span / 2 + 26;
  const lx = makeLabel('X+', '#ff6b6b'); lx.position.set(r, 6, 0); labelsGroup.add(lx);
  const ly = makeLabel('Y+', '#6bff9e'); ly.position.set(0, 6, r); labelsGroup.add(ly);
  const lz = makeLabel('Z+', '#6bb6ff'); lz.position.set(0, H + 34, 0); labelsGroup.add(lz);
  scene.add(labelsGroup);
}

function applyVisibility() {
  if (workspaceBox) workspaceBox.visible = machineConfig.showWorkspace;
  if (sensorGroup) sensorGroup.visible = machineConfig.showSensors;
  if (labelsGroup) labelsGroup.visible = machineConfig.showLabels;
}

export function setVisibility(opts) {
  Object.assign(machineConfig, opts);
  applyVisibility();
}

// ---------------------------------------------------------------------------
// 实时更新轴位置 (由服务端 state 驱动, 经主循环平滑插值)
// P4 五轴: a/c 为枢轴姿态角(度), toolLen 为工具长度; 针尖 = 枢轴 + 工具方向×L
// ---------------------------------------------------------------------------
const _needleDown = new THREE.Vector3(0, -1, 0);

export function updatePositions(x, y, z, valveOn, aDeg = 0, cDeg = 0, toolLen = 60) {
  if (!yCarriage || !xCarriage || !zCarriage) return;

  // 三轴联动: 设置三个滑座位置 (五轴时滑座 = 枢轴)
  yCarriage.position.z = y;             // 龙门 / 移动台面沿 Y
  xCarriage.position.x = x;             // X 滑座沿横梁移动
  zCarriage.position.y = z - machineConfig.gantryHeight;  // Z 滑座上下

  // P4: 针头姿态 —— 机器坐标工具方向 t = (−sinA·sinC, sinA·cosC, −cosA) (A=0 竖直向下)
  // three.js (X,Y,Z) = 机器 (x, z, y); 针为旋转对称体, 用 setFromUnitVectors 免手性烦恼
  let tip = new THREE.Vector3(x, z, y);          // 默认竖直: 针尖=枢轴投影
  if (needleTilt) {
    const ar = (aDeg * Math.PI) / 180, cr = (cDeg * Math.PI) / 180;
    const sA = Math.sin(ar);
    const dir = new THREE.Vector3(
      -sA * Math.sin(cr),  // three.X = 机器 x
      -Math.cos(ar),       // three.Y = 机器 z
      sA * Math.cos(cr),   // three.Z = 机器 y
    );
    needleTilt.quaternion.setFromUnitVectors(_needleDown, dir);
    tip = new THREE.Vector3(
      x + dir.x * toolLen,
      z + dir.y * toolLen,
      y + dir.z * toolLen,
    );
  }

  // 胶束: 从针尖延伸到台面, 长度 = 针尖高度 (五轴姿态下隐藏)
  if (beamMesh) {
    const len = Math.max(z, 0.1);
    beamMesh.scale.y = len;
    beamMesh.position.y = -len / 2;
    beamMesh.visible = valveOn && z > 0.5 && Math.abs(aDeg) < 0.5;
  }

  // 胶路轨迹: 记录针尖实际位置 (TCP), 距离阈值去重
  if (valveOn) {
    if (trailPoints.length === 0 || tip.distanceTo(trailPoints[trailPoints.length - 1]) > 0.6) {
      trailPoints.push(tip);
      if (trailPoints.length > 20000) trailPoints.shift();
      updateTrailLine();
    }
  }
}

function updateTrailLine() {
  if (trailLine) { scene.remove(trailLine); trailLine.geometry.dispose(); }
  if (trailPoints.length < 2) { trailLine = null; return; }
  const geo = new THREE.BufferGeometry().setFromPoints(trailPoints);
  const mat = new THREE.LineBasicMaterial({ color: 0xff8a2a, linewidth: 2 });
  trailLine = new THREE.Line(geo, mat);
  scene.add(trailLine);
}

/** 路径预览: points3d = [[x,y,z], ...] (工作坐标) */
export function setPreviewPath(points3d) {
  if (previewLine) { scene.remove(previewLine); previewLine.geometry.dispose(); }
  if (!points3d || points3d.length < 2) { previewLine = null; return; }
  const pts = points3d.map(p => new THREE.Vector3(p[0], p[2], p[1]));
  const geo = new THREE.BufferGeometry().setFromPoints(pts);
  const mat = new THREE.LineBasicMaterial({ color: 0x00e5ff, transparent: true, opacity: 0.85 });
  previewLine = new THREE.Line(geo, mat);
  scene.add(previewLine);
}

export function clearTrail() {
  trailPoints = [];
  if (trailLine) { scene.remove(trailLine); trailLine.geometry.dispose(); trailLine = null; }
}

/**
 * 运动学自检: 返回三个滑座位置与针尖世界坐标。
 * 若联动正确, tipWork 应严格等于 updatePositions 传入的 (x, y, z)。
 */
export function getKinematics() {
  if (!needleTip || !yCarriage || !xCarriage || !zCarriage) return null;
  const p = new THREE.Vector3();
  needleTip.getWorldPosition(p);
  return {
    yCarriage: yCarriage.position.z,
    xCarriage: xCarriage.position.x,
    zCarriage: zCarriage.position.y,
    tipWorld: [p.x, p.y, p.z],      // Three.js (X, Y, Z)
    tipWork: [p.x, p.z, p.y],       // 工作坐标 (x, y, z)
  };
}

/** 独立本地 STL 预览: 转成 Y-up, 居中并贴合台面；不参与生产坐标和路径。 */
export function loadStlModel(file) {
  const reader = new FileReader();
  reader.onload = (e) => {
    const loader = new STLLoader();
    const geo = loader.parse(e.target.result);
    if (cadModel) { scene.remove(cadModel); disposeGeometries(cadModel); }
    geo.computeBoundingBox();
    const bb = geo.boundingBox;
    const size = new THREE.Vector3(); bb.getSize(size);
    const center = new THREE.Vector3(); bb.getCenter(center);

    const mat = new THREE.MeshPhongMaterial({
      color: 0x4d8bff, flatShading: true, transparent: true, opacity: 0.72,
      side: THREE.DoubleSide, shininess: 60,
    });
    cadModel = new THREE.Mesh(geo, mat);

    // STL 通常 Z-up → 旋转成 Y-up (绕 X 轴 -90°)
    const wrap = new THREE.Group();
    wrap.rotation.x = -Math.PI / 2;
    wrap.add(cadModel);
    // 旋转后: 原 (x,y,z) → (x, z, -y); 因此平移量按旋转后的坐标计算
    cadModel.position.set(-center.x, -center.z, center.y);

    // 归一化尺寸到台面的 ~60%
    const maxDim = Math.max(size.x, size.y, size.z, 0.001);
    const scale = (machineConfig.tableSize * 0.6) / maxDim;
    wrap.scale.setScalar(scale);

    // 底部贴合台面 (旋转后模型 Y 范围 = ±size.z/2 * scale)
    wrap.position.y = 2 + (size.z / 2) * scale;

    wrap.name = 'CadModel';
    scene.add(wrap);
    cadModel = wrap;   // 用 wrap 作为清理句柄
  };
  reader.readAsArrayBuffer(file);
}

export function clearModel() {
  if (cadModel) { scene.remove(cadModel); disposeGeometries(cadModel); cadModel = null; }
}

// ---------------------------------------------------------------------------
// P9 生产模型: STL 原始坐标只做单位换算，不做居中、归一化或贴台。
// 机器坐标到 Three 使用固定的 (x,y,z) → (x,z,y)；旋转在机器坐标中按
// Rx → Ry → Rz 组合，再转换到 Three，避免交换轴后改变旋转顺序。
// ---------------------------------------------------------------------------
const MACHINE_TO_THREE = new THREE.Matrix4().set(
  1, 0, 0, 0,
  0, 0, 1, 0,
  0, 1, 0, 0,
  0, 0, 0, 1,
);
const DEG = Math.PI / 180;

function finite(value) {
  const n = Number(value);
  return Number.isFinite(n) ? n : 0;
}

function poseToMachineMatrix(pose = {}) {
  const rx = finite(pose.rx) * DEG;
  const ry = finite(pose.ry) * DEG;
  const rz = finite(pose.rz) * DEG;
  const rotation = new THREE.Matrix4().makeRotationZ(rz)
    .multiply(new THREE.Matrix4().makeRotationY(ry))
    .multiply(new THREE.Matrix4().makeRotationX(rx));
  return new THREE.Matrix4().makeTranslation(
    finite(pose.x), finite(pose.y), finite(pose.z)).multiply(rotation);
}

function machineMatrixToThree(machineMatrix) {
  // MACHINE_TO_THREE 是对换矩阵，其逆矩阵等于自身。
  return MACHINE_TO_THREE.clone().multiply(machineMatrix).multiply(MACHINE_TO_THREE);
}

function applyMachineMatrix(group, machineMatrix) {
  const matrix = machineMatrixToThree(machineMatrix);
  matrix.decompose(group.position, group.quaternion, group.scale);
  group.updateMatrix();
}

function ensureProductionRoot() {
  if (productionRoot) return;
  productionRoot = new THREE.Group();
  productionRoot.name = 'ProductionScene';
  scene.add(productionRoot);
}

function unitScale(unit) {
  switch (String(unit || 'mm').toLowerCase()) {
    case 'mm': return 1;
    case 'cm': return 10;
    case 'inch':
    case 'in': return 25.4;
    default: throw new Error(`不支持的模型单位: ${unit}`);
  }
}

function reverseGeometryWinding(geometry) {
  if (geometry.index) {
    const index = geometry.index;
    for (let i = 0; i < index.count; i += 3) {
      const b = index.getX(i + 1);
      index.setX(i + 1, index.getX(i + 2));
      index.setX(i + 2, b);
    }
    index.needsUpdate = true;
  } else {
    for (const attribute of Object.values(geometry.attributes)) {
      const { array, itemSize, count } = attribute;
      for (let i = 0; i < count; i += 3) {
        for (let k = 0; k < itemSize; k++) {
          const bi = (i + 1) * itemSize + k;
          const ci = (i + 2) * itemSize + k;
          const b = array[bi];
          array[bi] = array[ci];
          array[ci] = b;
        }
      }
      attribute.needsUpdate = true;
    }
  }
  geometry.computeVertexNormals();
}

function configuredWorkpieceMatrix() {
  const config = productionConfigMsg?.config || {};
  const fixture = poseToMachineMatrix(config.fixture?.machinePose);
  const relative = poseToMachineMatrix(config.placement?.relativePose);
  return fixture.multiply(relative);
}

function updateClampVisual(clamped) {
  for (const mesh of clampMeshes) {
    mesh.material.color.setHex(clamped ? 0x38d17a : 0xd49a38);
    mesh.material.emissive.setHex(clamped ? 0x06351c : 0x2c1800);
  }
}

function rebuildFixture() {
  ensureProductionRoot();
  if (fixtureRoot) {
    productionRoot.remove(fixtureRoot);
    disposeGeometries(fixtureRoot);
  }
  fixtureRoot = new THREE.Group();
  fixtureRoot.name = 'ProductionFixture';
  productionRoot.add(fixtureRoot);
  clampMeshes = [];

  const plateMaterial = new THREE.MeshPhongMaterial({ color: 0x59687b, shininess: 70 });
  const plate = new THREE.Mesh(new THREE.BoxGeometry(120, 4, 100), plateMaterial);
  plate.position.y = -2; // 定位板上表面与夹具机器原点 z=0 对齐
  fixtureRoot.add(plate);
  const pinMaterial = new THREE.MeshPhongMaterial({ color: 0xaab7c8, shininess: 100 });
  for (const x of [-38, 38]) {
    const pin = new THREE.Mesh(new THREE.CylinderGeometry(3, 3, 8, 12), pinMaterial.clone());
    pin.position.set(x, 4, -32);
    fixtureRoot.add(pin);
  }
  for (const x of [-53, 53]) {
    const material = new THREE.MeshPhongMaterial({ color: 0xd49a38, emissive: 0x2c1800 });
    const clamp = new THREE.Mesh(new THREE.BoxGeometry(10, 8, 36), material);
    clamp.position.set(x, 4, 0);
    fixtureRoot.add(clamp);
    clampMeshes.push(clamp);
  }
  applyMachineMatrix(fixtureRoot,
    poseToMachineMatrix(productionConfigMsg?.config?.fixture?.machinePose));
  updateClampVisual(!!productionSnapshot?.clamped);
}

function rebuildProductionWorkpiece() {
  ensureProductionRoot();
  if (workpieceRoot) {
    productionRoot.remove(workpieceRoot);
    disposeGeometries(workpieceRoot);
    workpieceRoot = null;
  }
  if (!productionSourceGeometry || !productionModelMeta) return;

  const config = productionConfigMsg?.config || {};
  const placementScale = finite(config.placement?.scale) || 1;
  const geometry = productionSourceGeometry.clone();
  const scale = unitScale(productionModelMeta.unit) * placementScale;
  geometry.scale(scale, scale, scale); // 仅单位及工程缩放，不做尺寸归一化
  if (config.placement?.flipNormals) reverseGeometryWinding(geometry);
  geometry.applyMatrix4(MACHINE_TO_THREE);
  geometry.computeBoundingSphere();

  const material = new THREE.MeshPhongMaterial({
    color: 0x4d8bff, flatShading: true, transparent: true, opacity: 0.82,
    side: THREE.DoubleSide, shininess: 60,
  });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.name = 'ProductionWorkpieceMesh';
  workpieceRoot = new THREE.Group();
  workpieceRoot.name = 'ProductionWorkpiece';
  workpieceRoot.add(mesh);
  productionRoot.add(workpieceRoot);

  const pose = productionSnapshot?.workpiecePose;
  applyMachineMatrix(workpieceRoot,
    pose ? poseToMachineMatrix(pose) : configuredWorkpieceMatrix());
  const matchesConfig = !productionConfigMsg?.modelId ||
    productionConfigMsg.modelId === productionModelMeta.modelId;
  workpieceRoot.visible = matchesConfig && productionSnapshot?.partPresent !== false;
}

/** 接收生产工程配置；Web 只展示，不编辑工艺。 */
export function setProductionConfig(msg) {
  productionConfigMsg = msg;
  rebuildFixture();
  rebuildProductionWorkpiece();
}

/** 从服务端重组完成的 ArrayBuffer 解析生产 STL。 */
export function loadProductionModel(data, meta) {
  if (!(data instanceof ArrayBuffer)) throw new Error('生产模型数据不是 ArrayBuffer');
  const geometry = new STLLoader().parse(data);
  if (!geometry.getAttribute('position') || geometry.getAttribute('position').count < 3) {
    geometry.dispose();
    throw new Error('生产 STL 不包含有效三角面');
  }
  unitScale(meta.unit); // 提前拒绝未知单位，保留旧模型不被替换
  if (productionSourceGeometry) productionSourceGeometry.dispose();
  productionSourceGeometry = geometry;
  productionModelMeta = { modelId: meta.modelId, fileName: meta.fileName, unit: meta.unit };
  productionSnapshot = null;
  rebuildProductionWorkpiece();
  return {
    modelId: meta.modelId,
    triangles: geometry.getAttribute('position').count / 3,
  };
}

/** 服务端生产快照直接驱动工件；Web 不推进动作、不做暂停计时。 */
export function updateProduction(snapshot) {
  productionSnapshot = snapshot || null;
  if (workpieceRoot) {
    if (snapshot?.workpiecePose) {
      applyMachineMatrix(workpieceRoot, poseToMachineMatrix(snapshot.workpiecePose));
    }
    workpieceRoot.visible = snapshot?.partPresent !== false;
  }
  updateClampVisual(!!snapshot?.clamped);
}

// ---------------------------------------------------------------------------
// 视图控制
// ---------------------------------------------------------------------------
export function resetView() {
  camera.position.set(330, 250, 360);
  controls.target.set(0, 45, 0);
  controls.update();
}

export function focusWorkspace() {
  const c = machineConfig;
  const cx = (c.xMin + c.xMax) / 2, cy = (c.zMin + c.zMax) / 2, cz = (c.yMin + c.yMax) / 2;
  controls.target.set(cx, cy, cz);
  camera.position.set(cx + 260, cy + 210, cz + 280);
  controls.update();
}

function disposeGeometries(root) {
  root.traverse(o => {
    if (o.geometry) o.geometry.dispose();
    // 仅释放克隆/独立材质, 全局共享 MAT 不释放
    if (o.material && !Object.values(MAT).includes(o.material)) {
      if (Array.isArray(o.material)) o.material.forEach(m => m.dispose());
      else o.material.dispose();
    }
    if (o.isSprite && o.material?.map) o.material.map.dispose();
  });
}

function onResize() {
  const container = renderer.domElement.parentElement;
  if (!container) return;
  camera.aspect = container.clientWidth / container.clientHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(container.clientWidth, container.clientHeight);
}

function animate() {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
}
