// ---------------------------------------------------------------------------
// 面板 UI 绑定: 把 DOM 事件翻译成协议指令, 把状态包渲染到面板。
// ---------------------------------------------------------------------------

export function initUI(link, { onView, onClearTrail, onLaserUI }) {
  const $ = id => document.getElementById(id);
  const badge = $('conn-badge');
  const logEl = $('log');
  let linkMode = 'off';
  let productionState = { phase: 'Idle', running: false, completed: 0, phaseProgress: 0 };

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
  if (!urlInput.value.trim()) {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    urlInput.value = `${protocol}//${location.host}/ws`;
  }
  function refreshBadge(mode) {
    linkMode = mode;
    if (mode === 'ws') { badge.textContent = 'Web 服务已连接'; badge.className = 'badge badge-ws'; btnConnect.textContent = '断开'; }
    else {
      badge.textContent = link.manualDisconnect ? '服务未连接' : '连接服务…';
      badge.className = 'badge badge-off';
      btnConnect.textContent = link.manualDisconnect ? '连接' : '断开';
    }
    refreshProductionControls();
  }
  btnConnect.addEventListener('click', () => {
    if (link.mode === 'ws' || !link.manualDisconnect) link.disconnect();
    else link.connect(urlInput.value.trim());
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

  // ---------- 自动上下料 ----------
  const recipeSelect = $('production-recipe');
  const btnSingle = $('btn-production-single');
  const btnContinuous = $('btn-production-continuous');
  const btnProductionStop = $('btn-production-stop');
  const phaseNames = {
    Idle: '空闲', Feeding: '料仓出料', Loading: '推料上料', Clamping: '定位夹紧',
    Processing: '激光加工', Unclamping: '松夹', Unloading: '推料下料',
    Retracting: '推杆回程', Complete: '生产完成', Faulted: '故障停机',
  };
  const recipeNames = { star: '五角星', circle: '整圆', combo: '综合图案' };

  function refreshProductionControls() {
    const running = !!productionState.running;
    const faulted = productionState.phase === 'Faulted';
    const unavailable = linkMode !== 'ws';
    btnSingle.disabled = unavailable || running || faulted;
    btnContinuous.disabled = unavailable || running || faulted;
    recipeSelect.disabled = unavailable || running;
    btnProductionStop.disabled = unavailable || (!running && !faulted);
    btnProductionStop.textContent = faulted ? '复位产线' : (productionState.stopRequested ? '等待本件完成…' : '完成后停止');
    btnProductionStop.classList.toggle('fault-reset', faulted);
    btnConnect.disabled = false;
    $('btn-home').disabled = unavailable || running;
    $('btn-laser').disabled = unavailable || running;
    document.querySelectorAll('.demo-btn').forEach(btn => { btn.disabled = unavailable || running; });
    document.querySelectorAll('.jog').forEach(btn => { btn.disabled = unavailable || running; });
    for (const axis of ['x', 'y', 'z']) $(`slider-${axis}`).disabled = unavailable || running;
    $('production-hint').textContent = unavailable
      ? '等待连接 Web 服务；上位机与浏览器连接同一地址即可协同控制'
      : faulted
        ? '产线已冻结：先复位产线，再执行回零并重新启动'
        : '料仓出料 → 推料 → 夹紧 → 加工 → 下料；停止指令在当前工件下料后生效';
  }

  function renderProduction(p) {
    productionState = p ?? { phase: 'Idle', running: false, completed: 0, phaseProgress: 0 };
    const phase = productionState.phase ?? 'Idle';
    $('production-phase').textContent = phaseNames[phase] ?? phase;
    $('production-workpiece').textContent = productionState.workpieceId
      ? `${productionState.workpieceId} · ${recipeNames[productionState.recipe] ?? productionState.recipe}`
      : '暂无工件';
    $('production-completed').textContent = productionState.completed ?? 0;
    $('production-mode').textContent = phase === 'Faulted'
      ? '急停 / 等待复位'
      : productionState.running
        ? (productionState.targetCount === 0 ? '连续生产' : `单件生产 · ${Math.round((productionState.phaseProgress ?? 0) * 100)}%`)
        : (phase === 'Complete' ? '成品已下料' : '等待启动');
    $('production-progress-bar').style.width = `${Math.round((productionState.phaseProgress ?? 0) * 100)}%`;
    const dotClass = phase === 'Faulted' ? 'faulted'
      : phase === 'Processing' ? 'processing'
        : phase === 'Complete' ? 'complete' : (productionState.running ? 'running' : '');
    $('production-dot').className = `production-dot ${dotClass}`;
    refreshProductionControls();
  }

  btnSingle.addEventListener('click', () => {
    link.send({ type: 'production_start', recipe: recipeSelect.value, count: 1 });
    log('ok', `指令: 单件自动加工 (${recipeNames[recipeSelect.value]})`);
  });
  btnContinuous.addEventListener('click', () => {
    link.send({ type: 'production_start', recipe: recipeSelect.value, count: 0 });
    log('ok', `指令: 连续生产 (${recipeNames[recipeSelect.value]})`);
  });
  btnProductionStop.addEventListener('click', () => {
    if (productionState.phase === 'Faulted') {
      link.send({ type: 'production_reset' });
      log('warn', '指令: 复位产线，请随后执行回零');
    } else {
      link.send({ type: 'production_stop' });
    }
  });
  renderProduction(null);

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
    renderProduction(st.production);
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
