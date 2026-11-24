// WebSocket 链路: 连接点胶服务端 (端口 8530)
// 断线自动重连；未完成的模型分片在断线后丢弃，以服务端重放为准。

const WS_URL = 'ws://localhost:8530/ws';
const MODEL_CHUNK_BYTES = 48 * 1024;
let ws = null;
let connected = false;
let reconnectTimer = null;
const modelTransfers = new Map();

function modelProgress(transfer, status) {
  return {
    status,
    modelId: transfer.modelId,
    fileName: transfer.fileName,
    unit: transfer.unit,
    totalBytes: transfer.totalBytes,
    totalChunks: transfer.totalChunks,
    receivedBytes: transfer.receivedBytes,
    receivedChunks: transfer.receivedChunks,
  };
}

function modelError(handlers, transfer, message) {
  if (transfer) modelTransfers.delete(transfer.modelId);
  handlers.onModelError?.({
    ...(transfer ? modelProgress(transfer, 'error') : { status: 'error' }),
    message,
  });
}

function decodeChunk(data) {
  const binary = atob(data);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

async function commitModel(msg, handlers) {
  const transfer = modelTransfers.get(msg.modelId);
  if (!transfer) {
    modelError(handlers, null, `模型 ${msg.modelId || '(未知)'} 提交失败：缺少分片，等待服务端重放`);
    return;
  }
  if (transfer.receivedChunks !== transfer.totalChunks || transfer.receivedBytes !== transfer.totalBytes) {
    modelError(handlers, transfer,
      `模型 ${transfer.fileName} 分片不完整 (${transfer.receivedChunks}/${transfer.totalChunks})`);
    return;
  }

  const data = transfer.bytes.buffer;
  if (transfer.sha256 && globalThis.crypto?.subtle) {
    const digest = await crypto.subtle.digest('SHA-256', data);
    const actual = Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
    if (actual.toLowerCase() !== transfer.sha256.toLowerCase()) {
      modelError(handlers, transfer, `模型 ${transfer.fileName} SHA-256 校验失败`);
      return;
    }
  }
  if (modelTransfers.get(msg.modelId) !== transfer) return;
  modelTransfers.delete(msg.modelId);
  handlers.onModelProgress?.(modelProgress(transfer, 'ready'));
  handlers.onModelReady?.({ ...modelProgress(transfer, 'ready'), data });
}

export function connect(onState, onPath, onEvent, onConnect, onConfig, handlers = {}) {
  function doConnect() {
    ws = new WebSocket(WS_URL);
    ws.onopen = () => {
      connected = true;
      onConnect(true);
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      // 连接成功后主动读取服务端轴配置
      send({ type: 'get_config' });
    };
    ws.onclose = () => {
      connected = false;
      modelTransfers.clear();
      handlers.onModelProgress?.({ status: 'waiting', receivedBytes: 0, receivedChunks: 0 });
      onConnect(false);
      reconnectTimer = setTimeout(doConnect, 2000);
    };
    ws.onerror = () => { ws.close(); };
    ws.onmessage = (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        switch (msg.type) {
          case 'state':
            onState(msg);
            if (msg.production) handlers.onProduction?.(msg.production);
            break;
          case 'path': onPath(msg); break;
          case 'event': onEvent(msg); break;
          case 'zones': onEvent({ type: 'zones', zones: msg.zones }); break;
          case 'config': if (onConfig) onConfig(msg); break;
          case 'production_config': handlers.onProductionConfig?.(msg); break;
          case 'production': handlers.onProduction?.(msg.production || msg); break;
          case 'model_begin': {
            const totalBytes = Number(msg.totalBytes);
            const totalChunks = Number(msg.totalChunks);
            if (!msg.modelId || !Number.isSafeInteger(totalBytes) || totalBytes <= 0 ||
                !Number.isSafeInteger(totalChunks) || totalChunks <= 0 ||
                totalChunks !== Math.ceil(totalBytes / MODEL_CHUNK_BYTES)) {
              modelError(handlers, null, `模型 ${msg.fileName || msg.modelId || '(未知)'} 的分片描述无效`);
              break;
            }
            modelTransfers.clear();
            const transfer = {
              ...msg,
              totalBytes,
              totalChunks,
              bytes: new Uint8Array(totalBytes),
              received: new Uint8Array(totalChunks),
              receivedBytes: 0,
              receivedChunks: 0,
            };
            modelTransfers.set(msg.modelId, transfer);
            handlers.onModelProgress?.(modelProgress(transfer, 'receiving'));
            break;
          }
          case 'model_chunk': {
            const transfer = modelTransfers.get(msg.modelId);
            if (!transfer) break;
            const index = Number(msg.index);
            if (!Number.isSafeInteger(index) || index < 0 || index >= transfer.totalChunks) {
              modelError(handlers, transfer, `模型 ${transfer.fileName} 的分片序号无效`);
              break;
            }
            if (transfer.received[index]) break;
            const bytes = decodeChunk(msg.data || '');
            const offset = index * MODEL_CHUNK_BYTES;
            const expected = Math.min(MODEL_CHUNK_BYTES, transfer.totalBytes - offset);
            if (bytes.length !== expected) {
              modelError(handlers, transfer, `模型 ${transfer.fileName} 的第 ${index + 1} 个分片长度无效`);
              break;
            }
            transfer.bytes.set(bytes, offset);
            transfer.received[index] = 1;
            transfer.receivedBytes += bytes.length;
            transfer.receivedChunks += 1;
            handlers.onModelProgress?.(modelProgress(transfer, 'receiving'));
            break;
          }
          case 'model_commit':
            commitModel(msg, handlers).catch(e => {
              console.error('model commit error', e);
              modelError(handlers, modelTransfers.get(msg.modelId), `模型提交失败：${e.message}`);
            });
            break;
        }
      } catch (e) {
        console.error('parse error', e);
        handlers.onModelError?.({ status: 'error', message: `服务端消息处理失败：${e.message}` });
      }
    };
  }
  doConnect();
}

export function send(msg) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
    return true;
  }
  return false;
}

export function isConnected() { return connected; }
