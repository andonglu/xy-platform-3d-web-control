import {
  initScene, updatePositions, setPreviewPath, clearTrail,
  loadStlModel, clearModel, buildMachine, getMachineConfig,
  setSensorSignals, setVisibility, resetView, focusWorkspace,
  DEFAULT_MACHINE,
} from './scene.js';
import * as sceneApi from './scene.js';   // 开发期运动学自检入口
import { connect, send, isConnected } from './link.js';

// 开发模式下暴露场景 API 供自检 (生产构建中 import.meta.env.DEV=false, 会被 tree-shake 移除)
if (import.meta.env.DEV) window.__dispenseScene = sceneApi;

// ---------------------------------------------------------------------------
// UI 绑定 + 主循环
// ---------------------------------------------------------------------------

const container = document.getElementById('canvas-container');
initScene(container);

// 状态缓存 (用于平滑插值)
let targetState = { x: 0, y: 0, z: 0, valve: false, collision: false };
let displayState = { x: 0, y: 0, z: 0, valve: false };

const $ = (id) => document.getElementById(id);

// 连接服务端
connect(
  (state) => {
    targetState = {
      x: state.x?.pos ?? 0,
      y: state.y?.pos ?? 0,
      z: state.z?.pos ?? 0,
      valve: state.valve ?? false,
      collision: state.collision ?? false,
    };
    $('x-pos').textContent = targetState.x.toFixed(3);
    $('y-pos').textContent = targetState.y.toFixed(3);
    $('z-pos').textContent = targetState.z.toFixed(3);

    // 更新轴状态和传感器 LED, 同时驱动 3D 传感器标记
    const signals = {};
    ['x', 'y', 'z'].forEach(ax => {
      const axData = state[ax];
      if (!axData) return;
      const stateEl = $(`${ax}-state`);
      if (stateEl) {
        stateEl.textContent = axData.state || 'Idle';
        stateEl.className = 'axis-state ' + (axData.state || 'Idle').toLowerCase();
      }
      const sen = axData.sensors;
      if (sen) {
        const homeEl = $(`${ax}-home`);
        const limPosEl = $(`${ax}-limpos`);
        const limNegEl = $(`${ax}-limneg`);
        if (homeEl) homeEl.classList.toggle('active', !!sen.home);
        if (limPosEl) limPosEl.classList.toggle('active', !!sen.limPos);
        if (limNegEl) limNegEl.classList.toggle('active', !!sen.limNeg);
        signals[ax] = { home: !!sen.home, limPos: !!sen.limPos, limNeg: !!sen.limNeg };
      }
    });
    setSensorSignals(signals);

    const valveBtn = $('btn-valve');
    valveBtn.className = 'toggle ' + (targetState.valve ? 'on' : 'off');
    valveBtn.textContent = '胶阀: ' + (targetState.valve ? '开' : '关');

    if (targetState.collision) showAlert(state.collisionMsg || '检测到撞机风险');
  },
  (pathMsg) => { setPreviewPath(pathMsg.points3d); },
  (event) => { logEvent(event); },
  (isConn) => {
    const badge = $('status-badge');
    if (isConn) {
      badge.className = 'badge connected';
      badge.textContent = '已连接';
    } else {
      badge.className = 'badge disconnected';
      badge.textContent = '连接中...';
    }
  },
  (configMsg) => { applyServerConfig(configMsg); }
);

// ---------------------------------------------------------------------------
// 3 轴模型创建
// ---------------------------------------------------------------------------

/** 读取面板输入 → 机器配置 */
function readFormConfig() {
  const num = (id, fallback) => {
    const v = parseFloat($(id).value);
    return Number.isFinite(v) ? v : fallback;
  };
  const d = DEFAULT_MACHINE;
  const xMin = num('cfg-xmin', d.xMin), xMax = num('cfg-xmax', d.xMax);
  const yMin = num('cfg-ymin', d.yMin), yMax = num('cfg-ymax', d.yMax);
  const zMin = num('cfg-zmin', d.zMin), zMax = num('cfg-zmax', d.zMax);
  if (xMax <= xMin || yMax <= yMin || zMax <= zMin) {
    logEvent({ level: 'error', msg: '行程配置无效: 上限必须大于下限' });
    return null;
  }
  return {
    type: $('machine-type').value,
    xMin, xMax, yMin, yMax, zMin, zMax,
    tableSize: num('cfg-table', d.tableSize),
    gantryHeight: num('cfg-height', d.gantryHeight),
    homeSensor: { x: (xMin + xMax) / 2, y: (yMin + yMax) / 2, z: (zMin + zMax) / 2 },
    limitNeg: { x: xMin + 2, y: yMin + 2, z: zMin + 2 },
    limitPos: { x: xMax - 2, y: yMax - 2, z: zMax - 2 },
    showWorkspace: $('chk-workspace').checked,
    showSensors: $('chk-sensors').checked,
    showLabels: $('chk-labels').checked,
  };
}

/** 用配置更新面板输入 */
function writeFormConfig(c) {
  $('machine-type').value = c.type || 'gantry';
  $('cfg-xmin').value = c.xMin; $('cfg-xmax').value = c.xMax;
  $('cfg-ymin').value = c.yMin; $('cfg-ymax').value = c.yMax;
  $('cfg-zmin').value = c.zMin; $('cfg-zmax').value = c.zMax;
  $('cfg-table').value = c.tableSize;
  $('cfg-height').value = c.gantryHeight;
  $('chk-workspace').checked = c.showWorkspace !== false;
  $('chk-sensors').checked = c.showSensors !== false;
  $('chk-labels').checked = c.showLabels !== false;
}

function updateMachineInfo(c) {
  const names = { gantry: '龙门式', cantilever: '悬臂式', movingTable: '工作台移动式' };
  $('machine-info').textContent =
    `${names[c.type] || c.type} · X[${c.xMin},${c.xMax}] Y[${c.yMin},${c.yMax}] Z[${c.zMin},${c.zMax}] · ` +
    `台面 ${c.tableSize} · 横梁 ${c.gantryHeight} · 三轴联动已启用`;
}

$('btn-build-machine').onclick = () => {
  const cfg = readFormConfig();
  if (!cfg) return;
  buildMachine(cfg);
  updateMachineInfo(getMachineConfig());
  focusWorkspace();
  logEvent({ level: 'ok', msg: `已创建 3 轴点胶机模型: ${cfg.type}, 三轴联动层级 Y→X→Z→针尖` });
};

// 显示开关
['chk-workspace', 'chk-sensors', 'chk-labels'].forEach(id => {
  $(id).addEventListener('change', () => {
    setVisibility({
      showWorkspace: $('chk-workspace').checked,
      showSensors: $('chk-sensors').checked,
      showLabels: $('chk-labels').checked,
    });
  });
});

// 同步配置到服务端 (让 3D 传感器标记位置 = 服务端实际触发位置)
$('btn-sync-config').onclick = () => {
  const cfg = readFormConfig();
  if (!cfg) return;
  const axes = [
    { name: 'x', min: cfg.xMin, max: cfg.xMax },
    { name: 'y', min: cfg.yMin, max: cfg.yMax },
    { name: 'z', min: cfg.zMin, max: cfg.zMax },
  ];
  axes.forEach(a => {
    send({
      type: 'config_axis', axis: a.name,
      travelMin: a.min, travelMax: a.max,
      homePos: cfg.homeSensor[a.name],
      limitNeg: cfg.limitNeg[a.name],
      limitPos: cfg.limitPos[a.name],
      homeSensor: cfg.homeSensor[a.name],
      homeVel: 20,
    });
  });
  logEvent({ level: 'ok', msg: '已将三轴行程/零点/传感器位置同步到服务端' });
};

$('btn-get-config').onclick = () => {
  send({ type: 'get_config' });
  logEvent({ level: 'info', msg: '已请求服务端轴配置' });
};

/** 服务端 config 响应 → 更新面板并重建模型 */
function applyServerConfig(msg) {
  if (!msg.axes || msg.axes.length < 3) return;
  const map = {};
  msg.axes.forEach(a => { map[a.name] = a; });
  const cur = getMachineConfig();
  const cfg = {
    ...cur,
    xMin: map.x?.travelMin ?? cur.xMin, xMax: map.x?.travelMax ?? cur.xMax,
    yMin: map.y?.travelMin ?? cur.yMin, yMax: map.y?.travelMax ?? cur.yMax,
    zMin: map.z?.travelMin ?? cur.zMin, zMax: map.z?.travelMax ?? cur.zMax,
    homeSensor: {
      x: map.x?.homeSensor ?? cur.homeSensor.x,
      y: map.y?.homeSensor ?? cur.homeSensor.y,
      z: map.z?.homeSensor ?? cur.homeSensor.z,
    },
    limitNeg: {
      x: map.x?.limitNeg ?? cur.limitNeg.x,
      y: map.y?.limitNeg ?? cur.limitNeg.y,
      z: map.z?.limitNeg ?? cur.limitNeg.z,
    },
    limitPos: {
      x: map.x?.limitPos ?? cur.limitPos.x,
      y: map.y?.limitPos ?? cur.limitPos.y,
      z: map.z?.limitPos ?? cur.limitPos.z,
    },
  };
  writeFormConfig(cfg);
  buildMachine(cfg);
  updateMachineInfo(getMachineConfig());
  logEvent({ level: 'ok', msg: '已按服务端轴配置重建 3 轴模型 (传感器标记已就位)' });
}

// ---------------------------------------------------------------------------
// 运动控制按钮
// ---------------------------------------------------------------------------
$('btn-home').onclick = () => send({ type: 'home' });
$('btn-estop').onclick = () => send({ type: 'estop' });
$('btn-reset').onclick = () => { send({ type: 'reset_alarm' }); hideAlert(); };
$('btn-run').onclick = () => send({ type: 'run' });
$('btn-valve').onclick = () => send({ type: 'valve', on: !targetState.valve });
$('btn-ptp').onclick = () => {
  send({
    type: 'ptp',
    axis: $('ptp-axis').value,
    pos: parseFloat($('ptp-pos').value) || 0,
  });
};

// 点动按钮
document.querySelectorAll('[data-axis]').forEach(btn => {
  const stop = () => send({ type: 'jog', axis: btn.dataset.axis, dir: 0 });
  btn.addEventListener('mousedown', () => send({ type: 'jog', axis: btn.dataset.axis, dir: parseInt(btn.dataset.dir) }));
  btn.addEventListener('mouseup', stop);
  btn.addEventListener('mouseleave', stop);
});

// 视图 / 胶路
$('btn-reset-view').onclick = () => resetView();
$('btn-clear-trail').onclick = () => clearTrail();

// STL 文件上传 (前端 3D 预览)
$('file-stl').addEventListener('change', (e) => {
  const file = e.target.files[0];
  if (file) {
    loadStlModel(file);
    logEvent({ level: 'info', msg: `已加载模型: ${file.name} (已贴合台面, 路径规划请用上位机)` });
  }
});
$('btn-clear-model').onclick = () => clearModel();

// 报警弹窗
$('btn-alert-reset').onclick = () => { send({ type: 'reset_alarm' }); hideAlert(); };

function showAlert(msg) {
  $('collision-msg').textContent = msg;
  $('collision-alert').classList.remove('hidden');
}
function hideAlert() {
  $('collision-alert').classList.add('hidden');
}

function logEvent(event) {
  const logDiv = $('log');
  const line = document.createElement('div');
  const level = event.level || 'info';
  line.className = level;
  const time = new Date().toLocaleTimeString();
  if (event.type === 'zones') {
    const list = $('zone-list');
    list.innerHTML = '';
    event.zones.forEach(z => {
      const div = document.createElement('div');
      div.className = 'zone-item';
      div.textContent = `${z.name}: X[${z.xMin}~${z.xMax}] Y[${z.yMin}~${z.yMax}] Z[${z.zMin}~${z.zMax}]`;
      list.appendChild(div);
    });
    return;
  }
  line.textContent = `[${time}] [${level}] ${event.msg}`;
  logDiv.appendChild(line);
  logDiv.scrollTop = logDiv.scrollHeight;
  while (logDiv.children.length > 50) logDiv.removeChild(logDiv.firstChild);
}

// 初始化面板显示
writeFormConfig(getMachineConfig());
updateMachineInfo(getMachineConfig());

// ---- 平滑插值主循环 ----
function animate() {
  requestAnimationFrame(animate);
  const alpha = 0.25;   // 指数平滑 (60fps 渲染 / 30Hz 状态)
  displayState.x += (targetState.x - displayState.x) * alpha;
  displayState.y += (targetState.y - displayState.y) * alpha;
  displayState.z += (targetState.z - displayState.z) * alpha;
  displayState.valve = targetState.valve;
  updatePositions(displayState.x, displayState.y, displayState.z, displayState.valve);
}
animate();
