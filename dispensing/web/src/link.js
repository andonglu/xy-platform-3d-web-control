// WebSocket 链路: 连接点胶服务端 (端口 8530)
// 断线自动重连

const WS_URL = 'ws://localhost:8530/ws';
let ws = null;
let connected = false;
let reconnectTimer = null;

export function connect(onState, onPath, onEvent, onConnect) {
  function doConnect() {
    ws = new WebSocket(WS_URL);
    ws.onopen = () => {
      connected = true;
      onConnect(true);
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    };
    ws.onclose = () => {
      connected = false;
      onConnect(false);
      reconnectTimer = setTimeout(doConnect, 2000);
    };
    ws.onerror = () => { ws.close(); };
    ws.onmessage = (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        switch (msg.type) {
          case 'state': onState(msg); break;
          case 'path': onPath(msg); break;
          case 'event': onEvent(msg); break;
          case 'zones': onEvent({ type: 'zones', zones: msg.zones }); break;
        }
      } catch (e) { console.error('parse error', e); }
    };
  }
  doConnect();
}

export function send(msg) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
  }
}

export function isConnected() { return connected; }
