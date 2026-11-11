// ---------------------------------------------------------------------------
// 面板 UI 绑定: 把 DOM 事件翻译成协议指令, 把状态包渲染到面板。
// ---------------------------------------------------------------------------

export function initUI(link, { onView, onClearTrail, onLaserUI }) {
  const $ = id => document.getElementById(id);
  const badge = $('conn-badge');
  const logEl = $('log');

  // ---------- 日志 ----------
  function log(level, msg) {
    const li = document.createElement('li');
    li.className = level;
    const t = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    li.innerHTML = `<span class="t">${t}</span>${msg}`;
    logEl.prepend(li);
    while (logEl.children.length > 60) logEl.lastChild.remove();
  }

  // ---------- 连接 ----------
  const urlInput = $('ws-url');
  const btnConnect = $('btn-connect');
  function refreshBadge(mode) {
    if (mode === 'ws') { badge.textContent = '上位机已连接'; badge.className = 'badge badge-ws'; btnConnect.textContent = '断开'; }
    else if (mode === 'off') { badge.textContent = '连接中…'; badge.className = 'badge badge-off'; btnConnect.textContent = '断开'; }
    else { badge.textContent = '内置模拟'; badge.className = 'badge badge-sim'; btnConnect.textContent = '连接'; }
  }
  btnConnect.addEventListener('click', () => {
    if (link.mode === 'sim') link.connect(urlInput.value.trim());
    else link.disconnect();
  });

  // ---------- 视角 ----------
  document.querySelectorAll('.view-btn').forEach(b =>
    b.addEventListener('click', () => onView(b.dataset.view)));

  // ---------- 滑块 (单轴定位) ----------
  const sliders = { x: $('slider-x'), y: $('slider-y'), z: $('slider-z') };
  const dragging = { x: false, y: false, z: false };
  const throttle = { x: 0, y: 0, z: 0 };
  for (const k of ['x', 'y', 'z']) {
    const s = sliders[k];
    s.addEventListener('pointerdown', () => { dragging[k] = true; });
    addEventListener('pointerup', () => { dragging[k] = false; });
    s.addEventListener('input', () => {
      const now = performance.now();
      if (now - throttle[k] < 60) return;   // ~16Hz 下发
      throttle[k] = now;
      link.send({ type: 'ptp', axis: k, pos: +s.value });
    });
  }

  // ---------- 点动 ----------
  document.querySelectorAll('.jog-row').forEach(row => {
    const axis = row.dataset.axis;
    row.querySelectorAll('.jog').forEach(btn => {
      const dir = +btn.dataset.dir;
      const start = e => { e.preventDefault(); link.send({ type: 'jog', axis, dir }); };
      const stop = () => link.send({ type: 'jog', axis, dir: 0 });
      btn.addEventListener('pointerdown', start);
      btn.addEventListener('pointerup', stop);
      btn.addEventListener('pointerleave', stop);
      btn.addEventListener('pointercancel', stop);
    });
  });

  // ---------- 回零 / 急停 ----------
  $('btn-home').addEventListener('click', () => { link.send({ type: 'home' }); log('ok', '指令: 回零'); });
  $('btn-estop').addEventListener('click', () => { link.send({ type: 'estop' }); });

  // ---------- 激光 ----------
  const btnLaser = $('btn-laser');
  btnLaser.addEventListener('click', () => {
    const on = btnLaser.getAttribute('aria-pressed') !== 'true';
    link.send({ type: 'laser', on });
  });
  $('btn-clear').addEventListener('click', () => { onClearTrail(); log('ok', '轨迹已清除'); });

  // ---------- 演示作业 ----------
  document.querySelectorAll('.demo-btn').forEach(b =>
    b.addEventListener('click', () => link.send({ type: 'job', name: b.dataset.demo })));

  // ---------- 状态渲染 ----------
  let laserShown = null;
  function renderState(st) {
    for (const k of ['x', 'y', 'z']) {
      const a = st[k];
      if (!a) continue;
      $(`pos-${k}`).textContent = a.pos.toFixed(2);
      $(`vel-${k}`).textContent = a.vel.toFixed(1);
      const stEl = $(`state-${k}`);
      stEl.textContent = a.state;
      stEl.className = 'axis-state ' + (a.state === 'Moving' ? 'moving' : a.state === 'Faulted' ? 'faulted' : a.state === 'Homing' ? 'homing' : '');
      if (!dragging[k]) sliders[k].value = a.pos;
    }
    $('mode-state').textContent = st.mode ?? 'Idle';
    if (st.laser !== laserShown) {
      laserShown = st.laser;
      $('laser-state').textContent = st.laser ? '开启' : '关闭';
      $('laser-state').className = st.laser ? 'laser-on' : 'laser-off';
      btnLaser.setAttribute('aria-pressed', String(!!st.laser));
      $('laser-label').textContent = st.laser ? '关闭激光' : '开启激光';
      onLaserUI?.(!!st.laser);
    }
  }

  return { log, renderState, refreshBadge };
}
