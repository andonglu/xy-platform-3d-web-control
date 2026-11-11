import { initScene, updatePositions, setPreviewPath, clearTrail, loadStlModel, clearModel } from './scene.js';
import { connect, send, isConnected } from './link.js';

// ---------------------------------------------------------------------------
// UI 绑定 + 主循环
// ---------------------------------------------------------------------------

const container = document.getElementById('canvas-container');
initScene(container);

// 状态缓存 (用于平滑插值)
let targetState = { x: 0, y: 0, z: 0, valve: false, collision: false };
let displayState = { x: 0, y: 0, z: 0, valve: false };

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
    // 更新位置显示
    document.getElementById('x-pos').textContent = targetState.x.toFixed(3);
    document.getElementById('y-pos').textContent = targetState.y.toFixed(3);
    document.getElementById('z-pos').textContent = targetState.z.toFixed(3);

    // 更新轴状态和传感器信号
    ['x', 'y', 'z'].forEach(ax => {
      const axData = state[ax];
      if (!axData) return;
      const stateEl = document.getElementById(`${ax}-state`);
      if (stateEl) {
        stateEl.textContent = axData.state || 'Idle';
        stateEl.className = 'axis-state ' + (axData.state || 'Idle').toLowerCase();
      }
      const sen = axData.sensors;
      if (sen) {
        const homeEl = document.getElementById(`${ax}-home`);
        const limPosEl = document.getElementById(`${ax}-limpos`);
        const limNegEl = document.getElementById(`${ax}-limneg`);
        if (homeEl) homeEl.classList.toggle('active', !!sen.home);
        if (limPosEl) limPosEl.classList.toggle('active', !!sen.limPos);
        if (limNegEl) limNegEl.classList.toggle('active', !!sen.limNeg);
      }
    });

    // 胶阀状态
    const valveBtn = document.getElementById('btn-valve');
    valveBtn.className = 'toggle ' + (targetState.valve ? 'on' : 'off');
    valveBtn.textContent = '胶阀: ' + (targetState.valve ? '开' : '关');

    // 撞机报警
    if (targetState.collision) {
      showAlert(state.collisionMsg || '检测到撞机风险');
    }
  },
  (pathMsg) => {
    setPreviewPath(pathMsg.points3d);
  },
  (event) => {
    logEvent(event);
  },
  (isConn) => {
    const badge = document.getElementById('status-badge');
    if (isConn) {
      badge.className = 'badge connected';
      badge.textContent = '已连接';
    } else {
      badge.className = 'badge disconnected';
      badge.textContent = '连接中...';
    }
  }
);

// ---- 按钮绑定 ----
document.getElementById('btn-home').onclick = () => send({ type: 'home' });
document.getElementById('btn-estop').onclick = () => send({ type: 'estop' });
document.getElementById('btn-reset').onclick = () => { send({ type: 'reset_alarm' }); hideAlert(); };
document.getElementById('btn-run').onclick = () => send({ type: 'run' });
document.getElementById('btn-valve').onclick = () => {
  const on = !targetState.valve;
  send({ type: 'valve', on });
};
document.getElementById('btn-ptp').onclick = () => {
  const axis = document.getElementById('ptp-axis').value;
  const pos = parseFloat(document.getElementById('ptp-pos').value);
  send({ type: 'ptp', axis, pos });
};

// 点动按钮
document.querySelectorAll('[data-axis]').forEach(btn => {
  btn.addEventListener('mousedown', () => {
    send({ type: 'jog', axis: btn.dataset.axis, dir: parseInt(btn.dataset.dir) });
  });
  btn.addEventListener('mouseup', () => {
    send({ type: 'jog', axis: btn.dataset.axis, dir: 0 });
  });
  btn.addEventListener('mouseleave', () => {
    send({ type: 'jog', axis: btn.dataset.axis, dir: 0 });
  });
});

// STL 文件上传 (仅前端预览, 路径需通过上位机处理)
document.getElementById('file-stl').addEventListener('change', (e) => {
  const file = e.target.files[0];
  if (file) {
    loadStlModel(file);
    logEvent({ level: 'info', msg: `已加载模型: ${file.name} (仅预览, 路径规划请用上位机)` });
  }
});
document.getElementById('btn-clear-model').onclick = () => clearModel();

// 报警弹窗
document.getElementById('btn-alert-reset').onclick = () => {
  send({ type: 'reset_alarm' });
  hideAlert();
};

function showAlert(msg) {
  document.getElementById('collision-msg').textContent = msg;
  document.getElementById('collision-alert').classList.remove('hidden');
}
function hideAlert() {
  document.getElementById('collision-alert').classList.add('hidden');
}

function logEvent(event) {
  const logDiv = document.getElementById('log');
  const line = document.createElement('div');
  const level = event.level || 'info';
  line.className = level;
  const time = new Date().toLocaleTimeString();
  if (event.type === 'zones') {
    const list = document.getElementById('zone-list');
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
  // 保留最近 50 条
  while (logDiv.children.length > 50) logDiv.removeChild(logDiv.firstChild);
}

// ---- 平滑插值主循环 ----
function animate() {
  requestAnimationFrame(animate);
  // 指数平滑 (帧率 ~60fps, 状态 30Hz)
  const alpha = 0.25;
  displayState.x += (targetState.x - displayState.x) * alpha;
  displayState.y += (targetState.y - displayState.y) * alpha;
  displayState.z += (targetState.z - displayState.z) * alpha;
  displayState.valve = targetState.valve;
  updatePositions(displayState.x, displayState.y, displayState.z, displayState.valve);
}
animate();
