import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { STLLoader } from 'three/addons/loaders/STLLoader.js';

// ---------------------------------------------------------------------------
// 3D 点胶场景: 平台 + 针头 + 胶路轨迹 + CAD 模型预览
// ---------------------------------------------------------------------------

let scene, camera, renderer, controls;
let platform, needleHead, needleTip, beamMesh;
let trailPoints = [];
let trailLine = null;
let previewLine = null;
let cadModel = null;

export function initScene(container) {
  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0a0a1a);

  camera = new THREE.PerspectiveCamera(50, container.clientWidth / container.clientHeight, 0.1, 2000);
  camera.position.set(200, 180, 200);

  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setSize(container.clientWidth, container.clientHeight);
  renderer.setPixelRatio(window.devicePixelRatio);
  container.appendChild(renderer.domElement);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.target.set(0, 0, 20);

  // 灯光
  scene.add(new THREE.AmbientLight(0x606080, 0.8));
  const dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
  dirLight.position.set(100, 200, 150);
  scene.add(dirLight);

  // 网格地面
  const grid = new THREE.GridHelper(300, 30, 0x444466, 0x222244);
  grid.rotation.x = Math.PI / 2;
  scene.add(grid);

  buildPlatform();
  buildNeedleHead();

  window.addEventListener('resize', onResize);
  animate();
}

function buildPlatform() {
  // 底座
  const baseGeo = new THREE.BoxGeometry(320, 320, 10);
  const baseMat = new THREE.MeshPhongMaterial({ color: 0x333355 });
  platform = new THREE.Mesh(baseGeo, baseMat);
  platform.position.set(0, 0, -5);
  scene.add(platform);

  // Y 轴导轨
  const railMat = new THREE.MeshPhongMaterial({ color: 0x666688 });
  const railY1 = new THREE.Mesh(new THREE.BoxGeometry(310, 20, 15), railMat);
  railY1.position.set(0, 155, 8);
  scene.add(railY1);
  const railY2 = railY1.clone();
  railY2.position.set(0, -155, 8);
  scene.add(railY2);

  // 工作台面 (胶路绘制面)
  const tableGeo = new THREE.PlaneGeometry(280, 280);
  const tableMat = new THREE.MeshPhongMaterial({ color: 0x1a1a2e, side: THREE.DoubleSide });
  const table = new THREE.Mesh(tableGeo, tableMat);
  table.position.set(0, 0, 2);
  scene.add(table);
}

function buildNeedleHead() {
  // Z 轴立柱
  const columnGeo = new THREE.BoxGeometry(15, 15, 80);
  const columnMat = new THREE.MeshPhongMaterial({ color: 0x888899 });
  const column = new THREE.Mesh(columnGeo, columnMat);
  column.position.set(0, 0, 40);
  scene.add(column);

  // 针头 (点胶头)
  const headGeo = new THREE.ConeGeometry(6, 20, 8);
  const headMat = new THREE.MeshPhongMaterial({ color: 0x22aabb, emissive: 0x002244 });
  needleHead = new THREE.Mesh(headGeo, headMat);
  needleHead.position.set(0, 0, 8);
  scene.add(needleHead);

  // 针尖
  const tipGeo = new THREE.SphereGeometry(1.5, 8, 8);
  needleTip = new THREE.Mesh(tipGeo, new THREE.MeshBasicMaterial({ color: 0xffff00 }));
  needleTip.position.set(0, 0, -2);
  scene.add(needleTip);

  // 胶束 (可视化, 开胶时显示)
  const beamGeo = new THREE.CylinderGeometry(0.5, 0.5, 10, 6);
  beamMesh = new THREE.Mesh(beamGeo, new THREE.MeshBasicMaterial({ color: 0xff6600, transparent: true, opacity: 0.7 }));
  beamMesh.position.set(0, 0, -7);
  beamMesh.visible = false;
  scene.add(beamMesh);
}

export function updatePositions(x, y, z, valveOn) {
  // 针头跟随 XYZ 运动 (在场景中 X->右, Y->前, Z->上)
  // 但 Three.js Y 是上, 所以做坐标映射: X->x, Y->z(Three的Y), Z->y(Three的Z)
  needleHead.position.set(x, z, y);
  needleTip.position.set(x, z - 10, y);
  beamMesh.position.set(x, z - 15, y);
  beamMesh.visible = valveOn;

  // 如果开胶, 记录轨迹点
  if (valveOn) {
    trailPoints.push(new THREE.Vector3(x, z - 10, y));
    updateTrailLine();
  }
}

function updateTrailLine() {
  if (trailLine) scene.remove(trailLine);
  if (trailPoints.length < 2) return;
  const geo = new THREE.BufferGeometry().setFromPoints(trailPoints);
  const mat = new THREE.LineBasicMaterial({ color: 0xff6600, linewidth: 2 });
  trailLine = new THREE.Line(geo, mat);
  scene.add(trailLine);
}

export function setPreviewPath(points3d) {
  // points3d: [[x,y,z], ...]
  if (previewLine) scene.remove(previewLine);
  if (!points3d || points3d.length < 2) return;
  const pts = points3d.map(p => new THREE.Vector3(p[0], p[2], p[1])); // X, Z->Y, Y->Z
  const geo = new THREE.BufferGeometry().setFromPoints(pts);
  const mat = new THREE.LineBasicMaterial({ color: 0x00ffff, transparent: true, opacity: 0.5 });
  previewLine = new THREE.Line(geo, mat);
  scene.add(previewLine);
}

export function clearTrail() {
  trailPoints = [];
  if (trailLine) { scene.remove(trailLine); trailLine = null; }
}

export function loadStlModel(file) {
  const reader = new FileReader();
  reader.onload = (e) => {
    const loader = new STLLoader();
    const geo = loader.parse(e.target.result);
    if (cadModel) scene.remove(cadModel);
    const mat = new THREE.MeshPhongMaterial({ color: 0x4488ff, flatShading: true, transparent: true, opacity: 0.6 });
    cadModel = new THREE.Mesh(geo, mat);
    // 居中并放到台面上
    geo.computeBoundingBox();
    const bb = geo.boundingBox;
    const center = new THREE.Vector3();
    bb.getCenter(center);
    cadModel.position.sub(center);
    cadModel.position.z += 20; // 抬到台面以上
    scene.add(cadModel);
  };
  reader.readAsArrayBuffer(file);
}

export function clearModel() {
  if (cadModel) { scene.remove(cadModel); cadModel = null; }
}

function onResize() {
  const container = renderer.domElement.parentElement;
  camera.aspect = container.clientWidth / container.clientHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(container.clientWidth, container.clientHeight);
}

function animate() {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
}
