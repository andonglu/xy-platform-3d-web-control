// ---------------------------------------------------------------------------
// 浏览器远程通信链路：浏览器与上位机都是 Web 服务的 WebSocket 客户端。
// 运动和自动生产只在服务端运行；断线时保持最后画面并自动重连，不启用本地模拟。
// ---------------------------------------------------------------------------
export class MotionLink {
  constructor({ onMessage, onMode }) {
    this.onMessage = onMessage;
    this.onMode = onMode;              // 'ws' | 'off'
    this.ws = null;
    this.mode = 'off';
    this.url = '';
    this.manualDisconnect = true;
    this.reconnectTimer = null;
  }

  _dispatch(msg) { this.onMessage?.(msg); }

  _setMode(mode) {
    this.mode = mode;
    this.onMode?.(mode);
  }

  connect(url) {
    this.manualDisconnect = false;
    clearTimeout(this.reconnectTimer);
    this._closeSocket();
    this.url = url;
    let ws;
    try { ws = new WebSocket(url); }
    catch {
      this._setMode('off');
      this.onMessage?.({ type: 'event', level: 'error', msg: `无效地址: ${url}` });
      this._scheduleReconnect();
      return;
    }
    this.ws = ws;
    this._setMode('off');
    ws.onopen = () => {
      if (this.ws !== ws) return;
      this._setMode('ws');
      this.send({ type: 'register', role: 'browser' });
      this.onMessage?.({ type: 'event', level: 'ok', msg: `已连接 Web 服务 ${url}` });
    };
    ws.onmessage = event => {
      try { this._dispatch(JSON.parse(event.data)); }
      catch { /* 忽略无法解析的数据帧 */ }
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this._setMode('off');
      this.onMessage?.({ type: 'event', level: 'warn', msg: 'Web 服务连接断开，2 秒后重连' });
      this._scheduleReconnect();
    };
    ws.onerror = () => {
      this.onMessage?.({ type: 'event', level: 'error', msg: `Web 服务连接失败: ${url}` });
    };
  }

  _scheduleReconnect() {
    if (this.manualDisconnect || this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (!this.manualDisconnect) this.connect(this.url);
    }, 2000);
  }

  _closeSocket() {
    if (!this.ws) return;
    const ws = this.ws;
    this.ws = null;
    ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null;
    ws.close();
  }

  disconnect() {
    this.manualDisconnect = true;
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this._closeSocket();
    this._setMode('off');
  }

  send(obj) {
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(obj));
    } else if (obj.type !== 'register') {
      this.onMessage?.({ type: 'event', level: 'warn', msg: `Web 服务未连接，指令未发送: ${obj.type}` });
    }
  }

  // 保留原主循环接口；服务端自行推进 120Hz 控制环。
  tick() {}
}
