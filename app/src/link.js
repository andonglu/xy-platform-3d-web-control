import { SimController } from './sim.js';

// ---------------------------------------------------------------------------
// 通信链路: 优先 WebSocket (C# 上位机), 未连接时回落到浏览器内置模拟控制器。
// 对上层暴露统一接口: send(obj) / tick(dt) / onMessage 回调。
// ---------------------------------------------------------------------------
export class MotionLink {
  constructor({ onMessage, onMode }) {
    this.onMessage = onMessage;
    this.onMode = onMode;              // 'sim' | 'ws' | 'off'
    this.sim = new SimController(m => this._dispatch(m));
    this.ws = null;
    this.mode = 'sim';
  }

  _dispatch(msg) { this.onMessage?.(msg); }

  _setMode(m) {
    this.mode = m;
    this.onMode?.(m);
  }

  connect(url) {
    this.disconnect();
    let ws;
    try {
      ws = new WebSocket(url);
    } catch {
      this.onMessage?.({ type: 'event', level: 'error', msg: `无效地址: ${url}` });
      return;
    }
    this.ws = ws;
    this._setMode('off'); // 连接中
    ws.onopen = () => {
      this._setMode('ws');
      this.onMessage?.({ type: 'event', level: 'ok', msg: `已连接上位机 ${url}` });
    };
    ws.onmessage = e => {
      try { this._dispatch(JSON.parse(e.data)); }
      catch { /* 忽略坏帧 */ }
    };
    ws.onclose = () => {
      if (this.ws === ws) {
        this.ws = null;
        this._setMode('sim');
        this.onMessage?.({ type: 'event', level: 'warn', msg: '连接断开, 回落到内置模拟' });
      }
    };
    ws.onerror = () => {
      this.onMessage?.({ type: 'event', level: 'error', msg: `连接失败: ${url}` });
    };
  }

  disconnect() {
    if (this.ws) {
      const ws = this.ws;
      this.ws = null;
      ws.onclose = null;
      ws.close();
    }
    if (this.mode !== 'sim') this._setMode('sim');
  }

  send(obj) {
    if (this.mode === 'ws' && this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(obj));
    } else {
      this.sim.receive(obj);
    }
  }

  tick(dt) {
    if (this.mode !== 'ws') this.sim.tick(dt);
  }
}
