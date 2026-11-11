import * as THREE from 'three';

// ---------------------------------------------------------------------------
// 机械常量
//   机器坐标 (mm): X/Y 为水平面, Z 为升降。世界坐标: 机器 X→世界X, 机器 Y→世界Z, 机器 Z→世界Y。
//   比例 S = 0.05 (1mm = 0.05 世界单位)。
// ---------------------------------------------------------------------------
export const S = 0.05;
export const TABLE_TOP = 1.25;            // 工作台面上表面世界高度
export const TABLE_HALF = 104;            // 工作台面半边长 (mm)
export const LIMITS = { x: [-100, 100], y: [-100, 100], z: [0, 60] };
const TIP_Y0 = 1.6;                        // z=0 时激光头尖端世界高度 (离台面 0.35)
export const TOOL_Z = 1.7;                 // 激光轴线相对 X 滑板中心的前伸量 (世界单位)
export const TOOL_OFFSET_MM = TOOL_Z / S;  // = 34mm 刀具偏移

const MAT = {
  base:     new THREE.MeshStandardMaterial({ color: 0x39404e, metalness: 0.55, roughness: 0.55 }),
  rail:     new THREE.MeshStandardMaterial({ color: 0xb8c0cc, metalness: 0.9,  roughness: 0.28 }),
  block:    new THREE.MeshStandardMaterial({ color: 0x4d5666, metalness: 0.7,  roughness: 0.4 }),
  carriage: new THREE.MeshStandardMaterial({ color: 0x7f8b9e, metalness: 0.75, roughness: 0.38 }),
  plate:    new THREE.MeshStandardMaterial({ color: 0x66718a, metalness: 0.7,  roughness: 0.42 }),
  column:   new THREE.MeshStandardMaterial({ color: 0x54627e, metalness: 0.7,  roughness: 0.45 }),
  head:     new THREE.MeshStandardMaterial({ color: 0x2b2f38, metalness: 0.6,  roughness: 0.5 }),
  nozzle:   new THREE.MeshStandardMaterial({ color: 0xc03a46, metalness: 0.5,  roughness: 0.4 }),
  accent:   new THREE.MeshStandardMaterial({ color: 0x2dd4bf, metalness: 0.3,  roughness: 0.5 }),
};

function box(w, h, d, mat, x = 0, y = 0, z = 0, shadow = true) {
  const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
  m.position.set(x, y, z);
  if (shadow) { m.castShadow = true; m.receiveShadow = true; }
  return m;
}

// ---------------------------------------------------------------------------
// 构建平台。返回可操作句柄:
//   setAxes(x,y,z)  按机器坐标 (mm) 放置各轴滑块与激光光束
//   setLaser(on)    激光束 / 光斑开关
//   beamHit         {x,y} 当前激光落点 (机器坐标), 由 main 每帧读取
// ---------------------------------------------------------------------------
export function buildPlatform(scene) {
  const root = new THREE.Group();
  scene.add(root);

  // —— 固定底座 ——————————————————————————————————————
  root.add(box(15.5, 1.0, 15.5, MAT.base, 0, 0.5, 0));
  // 底座装饰腰线
  root.add(box(15.7, 0.08, 15.7, MAT.accent, 0, 0.96, 0, false));

  // —— 工作台面 (XY 绘制区), 贴图由 trail.js 提供 ——————————
  // 台面中心在机器 Y=+34mm (激光头相对滑块前伸 1.7 世界单位的刀具偏移)
  const tableGeo = new THREE.PlaneGeometry(TABLE_HALF * 2 * S, TABLE_HALF * 2 * S).rotateX(-Math.PI / 2);
  const tableMat = new THREE.MeshStandardMaterial({ color: 0x202833, metalness: 0.2, roughness: 0.85 });
  const table = new THREE.Mesh(tableGeo, tableMat);
  table.position.set(0, TABLE_TOP, TOOL_Z);
  table.receiveShadow = true;
  root.add(table);
  // 台面金属边框
  const frameT = 0.35, frameH = 0.22, span = TABLE_HALF * 2 * S + frameT;
  root.add(box(span, frameH, frameT, MAT.block, 0, TABLE_TOP - frameH / 2, TOOL_Z - (TABLE_HALF * S + frameT / 2)));
  root.add(box(span, frameH, frameT, MAT.block, 0, TABLE_TOP - frameH / 2, TOOL_Z + TABLE_HALF * S + frameT / 2));
  root.add(box(frameT, frameH, span, MAT.block, -(TABLE_HALF * S + frameT / 2), TABLE_TOP - frameH / 2, TOOL_Z));
  root.add(box(frameT, frameH, span, MAT.block, TABLE_HALF * S + frameT / 2, TABLE_TOP - frameH / 2, TOOL_Z));

  // —— Y 轴: 两根导轨 (沿世界 Z), 固定在底座上, 位于台面两侧 ——————————
  const RAIL_X = 5.9;
  for (const sx of [-1, 1]) {
    root.add(box(0.5, 0.35, 15, MAT.rail, sx * RAIL_X, 1.175, 0));
    root.add(box(0.9, 0.18, 15, MAT.block, sx * RAIL_X, 1.09, 0)); // 导轨安装座
  }

  // —— Y 滑块 (桥式横梁, 承载 X 轴) ————————————————————————
  const yCarriage = new THREE.Group();
  root.add(yCarriage);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
    yCarriage.add(box(0.85, 0.32, 0.9, MAT.block, sx * RAIL_X, 1.5, sz * 0.95)); // 滑块
  }
  yCarriage.add(box(14.2, 0.5, 2.6, MAT.carriage, 0, 2.1, 0));            // 横梁
  yCarriage.add(box(14.2, 0.12, 0.5, MAT.accent, 0, 2.31, 0, false));     // 横梁饰条
  // 端部连接板
  for (const sx of [-1, 1]) yCarriage.add(box(1.3, 0.6, 2.6, MAT.plate, sx * 6.45, 1.8, 0));

  // —— X 轴导轨 (沿世界 X), 安装在 Y 横梁上 ——————————————————
  for (const sz of [-1, 1]) yCarriage.add(box(12.6, 0.3, 0.5, MAT.rail, 0, 2.5, sz * 0.95));

  // —— X 滑块 (承载 Z 轴立柱) ———————————————————————————
  const xCarriage = new THREE.Group();
  yCarriage.add(xCarriage);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
    xCarriage.add(box(0.9, 0.28, 0.8, MAT.block, sx * 1.1, 2.68, sz * 0.95)); // 滑块
  }
  xCarriage.add(box(3.4, 0.4, 2.8, MAT.plate, 0, 2.95, 0));               // X 滑板

  // —— Z 轴立柱 ——————————————————————————————————————
  xCarriage.add(box(1.5, 5.2, 1.2, MAT.column, 0, 5.75, -0.7));
  xCarriage.add(box(1.9, 0.5, 1.6, MAT.plate, 0, 3.35, -0.7));            // 立柱底座
  xCarriage.add(box(0.4, 4.6, 0.18, MAT.rail, 0, 5.6, -0.02));            // Z 导轨 (立柱前面)

  // —— Z 滑块 + 激光头 —————————————————————————————————
  // 激光头通过悬臂前伸到横梁前方 (TOOL_Z=1.7), 避免与横梁干涉
  const zSlider = new THREE.Group();
  xCarriage.add(zSlider);
  zSlider.add(box(1.0, 1.3, 0.55, MAT.block, 0, 3.9, 0.32));              // Z 滑块
  zSlider.add(box(0.5, 0.3, 1.35, MAT.plate, 0, 3.35, 1.15));             // 前伸悬臂
  // 激光头主体 + 喷嘴 (轴线在悬臂前端)
  const headBody = new THREE.Mesh(new THREE.CylinderGeometry(0.22, 0.22, 1.35, 24), MAT.head);
  headBody.position.set(0, 2.55, TOOL_Z);
  headBody.castShadow = true;
  zSlider.add(headBody);
  const headRing = new THREE.Mesh(new THREE.CylinderGeometry(0.26, 0.26, 0.18, 24), MAT.nozzle);
  headRing.position.set(0, 2.9, TOOL_Z);
  zSlider.add(headRing);
  const nozzle = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.2, 0.3, 24), MAT.nozzle);
  nozzle.position.set(0, 1.75, TOOL_Z);
  zSlider.add(nozzle);

  // —— 激光束 (外层光晕 + 内芯), 顶端挂在喷嘴, 长度随 Z 调整 ——————————
  const beamGroup = new THREE.Group();
  zSlider.add(beamGroup);
  const beamMat = new THREE.MeshBasicMaterial({
    color: 0xff2233, transparent: true, opacity: 0.28,
    blending: THREE.AdditiveBlending, depthWrite: false,
  });
  const beamCoreMat = new THREE.MeshBasicMaterial({
    color: 0xffa0a8, transparent: true, opacity: 0.9,
    blending: THREE.AdditiveBlending, depthWrite: false,
  });
  const beam = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.05, 1, 12, 1, true), beamMat);
  const beamCore = new THREE.Mesh(new THREE.CylinderGeometry(0.025, 0.018, 1, 8, 1, true), beamCoreMat);
  beamGroup.add(beam, beamCore);

  // 台面光斑 + 点光源
  const spot = new THREE.Mesh(
    new THREE.CircleGeometry(0.16, 24).rotateX(-Math.PI / 2),
    new THREE.MeshBasicMaterial({ color: 0xff4d5e, transparent: true, opacity: 0.85, blending: THREE.AdditiveBlending, depthWrite: false }),
  );
  spot.position.y = TABLE_TOP + 0.012;
  spot.visible = false;
  scene.add(spot);
  const spotLight = new THREE.PointLight(0xff3040, 0, 6);
  scene.add(spotLight);

  // —— 状态 ——————————————————————————————————————————
  let laserOn = false;
  const cur = { x: 0, y: 0, z: 0 };

  function setAxes(x, y, z) {
    cur.x = x; cur.y = y; cur.z = z;
    yCarriage.position.z = y * S;      // 机器 Y → 世界 Z
    xCarriage.position.x = x * S;
    zSlider.position.y = z * S;        // 机器 Z → 世界 Y
    // 光束: 顶端固定于喷嘴尖端 (局部 y=TIP_Y0), 底端落在台面
    const tipWorld = TIP_Y0 + z * S;
    const len = Math.max(tipWorld - TABLE_TOP, 0.001);
    const centerLocal = (TIP_Y0 + (TABLE_TOP - z * S)) / 2;
    beam.scale.y = len; beamCore.scale.y = len;
    beam.position.set(0, centerLocal, TOOL_Z);
    beamCore.position.set(0, centerLocal, TOOL_Z);
    // 光斑位置 (世界坐标)
    spot.position.x = x * S;
    spot.position.z = y * S + TOOL_Z;
    spotLight.position.set(x * S, TABLE_TOP + 0.5, y * S + TOOL_Z);
  }

  function setLaser(on) {
    laserOn = on;
    beam.visible = on; beamCore.visible = on;
    spot.visible = on;
    spotLight.intensity = on ? 2.2 : 0;
  }
  setLaser(false);

  return {
    root, table, setAxes, setLaser,
    get laserOn() { return laserOn; },
    // 当前激光落点 (机器坐标 mm, 含刀具前伸偏移 TOOL_Z/S=34mm)
    beamHit() { return { x: cur.x, y: cur.y + TOOL_Z / S }; },
    tableMat,
  };
}
