import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { buildPlatform, S, TABLE_TOP, TOOL_Z } from './platform.js';
import { TableTrail } from './trail.js';
import { MotionLink } from './link.js';
import { initUI } from './ui.js';

// ---------------------------------------------------------------------------
// 入口: 场景 / 相机 / 灯光 + 平台模型 + 通信链路 + 面板
// 渲染与运动控制解耦: 前端只消费状态包 {x,y,z 位置 / 激光}, 不反向推导运动。
// ---------------------------------------------------------------------------
const stage = document.getElementById('stage');
const SKY = new THREE.Color(0x10141b);

const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.shadowMap.enabled = true;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
stage.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = SKY;
scene.fog = new THREE.Fog(SKY, 45, 140);

const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 500);
camera.position.set(15, 11, 17);

const controls = new OrbitControls(camera, renderer.domElement);
controls.target.set(0, 2.5, 0);
controls.enableDamping = true;
controls.maxPolarAngle = Math.PI * 0.495;

// 灯光
scene.add(new THREE.HemisphereLight(0xbfd4ff, 0x2a2118, 0.85));
const key = new THREE.DirectionalLight(0xfff1dd, 2.0);
key.position.set(14, 22, 10);
key.castShadow = true;
key.shadow.mapSize.set(2048, 2048);
key.shadow.camera.left = key.shadow.camera.bottom = -14;
key.shadow.camera.right = key.shadow.camera.top = 14;
key.shadow.bias = -0.0004;
scene.add(key);
const rim = new THREE.DirectionalLight(0x6f8dff, 0.5);
rim.position.set(-12, 8, -14);
scene.add(rim);

// 地面
const ground = new THREE.Mesh(
  new THREE.CircleGeometry(300, 64).rotateX(-Math.PI / 2),
  new THREE.MeshStandardMaterial({ color: 0x151a23, roughness: 1 }),
);
ground.receiveShadow = true;
scene.add(ground);
const grid = new THREE.GridHelper(120, 60, 0x232c3d, 0x1a2130);
grid.position.y = 0.01;
scene.add(grid);

// 平台 + 轨迹层
const platform = buildPlatform(scene);
const trail = new TableTrail(platform.tableMat);

// 规划路径预览线 (台面之上的青色细线)
const previewMat = new THREE.LineBasicMaterial({ color: 0x2dd4bf, transparent: true, opacity: 0.55 });
let preview = null;
function showPreview(points) {
  if (preview) { scene.remove(preview); preview.geometry.dispose(); }
  const pts = points.map(([x, y]) => new THREE.Vector3(x * S, TABLE_TOP + 0.02, y * S + TOOL_Z));
  preview = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), previewMat);
  scene.add(preview);
}

// ---------- 通信 ----------
let ui; // initUI 在 link 之后赋值; 构造期 hello 消息用可选链兜底
const link = new MotionLink({
  onMessage(msg) {
    switch (msg.type) {
      case 'hello': ui?.log('ok', `对端: ${msg.server}`); break;
      case 'state': ui?.renderState(msg); latest = msg; break;
      case 'path': showPreview(msg.points); ui?.log('ok', `收到规划路径 (${msg.points.length} 点)`); break;
      case 'event': ui?.log(msg.level, msg.msg); break;
    }
  },
  onMode(mode) { ui?.refreshBadge(mode); },
});

// ---------- 视角切换 ----------
const VIEWS = {
  persp: { p: [15, 11, 17], t: [0, 2.5, 0] },
  top:   { p: [0, 26, 0.01], t: [0, 0, 0] },
  front: { p: [0, 5, 22], t: [0, 3.5, 0] },
  side:  { p: [22, 5, 0], t: [0, 3.5, 0] },
};
let camTween = null;
function setView(name) {
  const v = VIEWS[name];
  if (!v) return;
  camTween = {
    p0: camera.position.clone(), p1: new THREE.Vector3(...v.p),
    t0: controls.target.clone(), t1: new THREE.Vector3(...v.t),
    k: 0,
  };
}

// ---------- UI ----------
ui = initUI(link, {
  onView: setView,
  onClearTrail: () => { trail.clear(); if (preview) { scene.remove(preview); preview = null; } },
  onLaserUI: () => {},
});

// ---------- 主循环 ----------
const hud = document.createElement('div');
hud.id = 'hud';
stage.appendChild(hud);

let latest = null;                 // 最新状态包
const shown = { x: 0, y: 0, z: 0 }; // 平滑后的显示位置
let laserWasOn = false;
let frames = 0, fpsT = 0, fps = 0;

function resize() {
  const w = stage.clientWidth, h = stage.clientHeight;
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  renderer.setSize(w, h);
}
addEventListener('resize', resize);
resize();

const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = Math.min(clock.getDelta(), 0.1);
  link.tick(dt);

  // 状态 → 模型 (指数平滑, 把 30Hz 状态包插值到帧率, 观感延迟 <100ms)
  if (latest) {
    const k = Math.min(1, dt * 25);
    shown.x += (latest.x.pos - shown.x) * k;
    shown.y += (latest.y.pos - shown.y) * k;
    shown.z += (latest.z.pos - shown.z) * k;
    platform.setAxes(shown.x, shown.y, shown.z);
    platform.setLaser(!!latest.laser);

    // 轨迹烧录
    const hit = platform.beamHit();
    if (latest.laser) {
      if (!laserWasOn) trail.moveTo(hit.x, hit.y);
      else trail.lineTo(hit.x, hit.y);
    }
    laserWasOn = !!latest.laser;
  }

  // 相机补间
  if (camTween) {
    camTween.k = Math.min(1, camTween.k + dt / 0.45);
    const e = 1 - Math.pow(1 - camTween.k, 3); // easeOutCubic
    camera.position.lerpVectors(camTween.p0, camTween.p1, e);
    controls.target.lerpVectors(camTween.t0, camTween.t1, e);
    if (camTween.k >= 1) camTween = null;
  }
  controls.update();
  renderer.render(scene, camera);

  frames++; fpsT += dt;
  if (fpsT >= 0.5) { fps = Math.round(frames / fpsT); frames = 0; fpsT = 0; }
  hud.textContent = `${fps} fps · calls ${renderer.info.render.calls} · tris ${(renderer.info.render.triangles / 1000).toFixed(0)}k`;
});

window.__SCENE_READY__ = true;
// 验证钩子: 统计台面轨迹红色像素数
window.__trailDebug = () => {
  const d = trail.ctx.getImageData(0, 0, trail.canvas.width, trail.canvas.height).data;
  let n = 0;
  for (let i = 0; i < d.length; i += 16) if (d[i] > 150 && d[i + 1] < 90) n++;
  return n;
};
