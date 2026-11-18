import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { WebSocketServer, WebSocket } from 'ws';
import { SimController } from '../src/sim.js';

const DEFAULT_DIST = fileURLToPath(new URL('../dist/', import.meta.url));
const COMMANDS = new Set([
  'home', 'ptp', 'jog', 'laser', 'job', 'estop', 'mode_set',
  'production_start', 'production_stop', 'production_reset',
  'io_write', 'io_read',
]);
const RECIPES = new Set(['star', 'circle', 'combo']);
const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg',
  '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
  '.woff': 'font/woff', '.woff2': 'font/woff2',
};

function commandError(msg, channel = 'legacy') {
  if (!msg || typeof msg !== 'object' || Array.isArray(msg) || typeof msg.type !== 'string') return '指令必须是含 type 的 JSON 对象';
  if (!COMMANDS.has(msg.type)) return `未知指令: ${msg.type}`;
  if (msg.type === 'ptp' && (!['x', 'y', 'z'].includes(msg.axis) || !Number.isFinite(msg.pos))) return 'ptp 需要有效 axis 和数值 pos';
  if (msg.type === 'jog' && (!['x', 'y', 'z'].includes(msg.axis) || !Number.isFinite(msg.dir))) return 'jog 需要有效 axis 和数值 dir';
  if (msg.type === 'laser' && typeof msg.on !== 'boolean') return 'laser 需要布尔值 on';
  if (msg.type === 'job' && !RECIPES.has(msg.name)) return 'job.name 必须是 star、circle 或 combo';
  if (msg.type === 'production_start') {
    if (!RECIPES.has(msg.recipe)) return 'production_start.recipe 必须是 star、circle 或 combo';
    if (msg.count !== undefined && (!Number.isInteger(msg.count) || msg.count < 0 || msg.count > 999)) return '生产数量 count 必须是 0~999 的整数';
  }
  if (msg.type === 'io_write' && (typeof msg.id !== 'string' || typeof msg.on !== 'boolean')) return 'io_write 需要字符串 id 和布尔值 on';
  if (msg.type === 'mode_set' && msg.mode !== 'host' && msg.mode !== 'device') return 'mode_set.mode 必须是 host 或 device';
  if (channel === 'motion' && msg.type === 'laser') return '激光指令必须发送到 /ws/laser 控制卡通道';
  if (channel === 'laser' && msg.type !== 'laser') return '激光控制卡通道仅接受 laser 指令';
  return null;
}

export class MotionWebService {
  constructor(options = {}) {
    this.host = options.host ?? process.env.HOST ?? '127.0.0.1';
    this.requestedPort = options.port ?? Number(process.env.PORT ?? 8080);
    this.distRoot = path.resolve(options.distRoot ?? DEFAULT_DIST);
    this.timeScale = options.timeScale ?? 1;
    this.clients = new Map();
    this.startedAt = Date.now();
    this.timer = null;
    this.heartbeat = null;

    this.http = http.createServer((req, res) => this._handleHttp(req, res));
    this.wss = new WebSocketServer({ noServer: true, maxPayload: 64 * 1024 });
    this.wss.on('connection', (ws, req) => this._onConnection(ws, req));
    this.http.on('upgrade', (req, socket, head) => {
      const pathname = new URL(req.url, 'http://localhost').pathname;
      if (!['/ws', '/ws/motion', '/ws/laser'].includes(pathname)) { socket.destroy(); return; }
      this.wss.handleUpgrade(req, socket, head, ws => this.wss.emit('connection', ws, req));
    });
    this.sim = new SimController(msg => this.broadcast(msg));
  }

  get port() { return this.http.address()?.port ?? this.requestedPort; }
  get url() { return `http://${this.host}:${this.port}`; }
  get wsUrl() { return `ws://${this.host}:${this.port}/ws`; }
  get motionWsUrl() { return `ws://${this.host}:${this.port}/ws/motion`; }
  get laserWsUrl() { return `ws://${this.host}:${this.port}/ws/laser`; }

  async start() {
    if (this.http.listening) return this;
    await new Promise((resolve, reject) => {
      this.http.once('error', reject);
      this.http.listen(this.requestedPort, this.host, resolve);
    });

    let last = performance.now();
    this.timer = setInterval(() => {
      const now = performance.now();
      const dt = Math.min((now - last) / 1000, 0.1) * this.timeScale;
      last = now;
      this.sim.tick(dt);
    }, 1000 / 120);

    this.heartbeat = setInterval(() => {
      for (const [ws, meta] of this.clients) {
        if (!meta.alive) { ws.terminate(); continue; }
        meta.alive = false;
        ws.ping();
      }
    }, 30000);
    return this;
  }

  async stop() {
    clearInterval(this.timer);
    clearInterval(this.heartbeat);
    this.timer = null;
    this.heartbeat = null;
    for (const ws of this.clients.keys()) ws.terminate();
    this.clients.clear();
    if (!this.http.listening) return;
    await new Promise(resolve => {
      this.wss.close(() => this.http.close(() => resolve()));
    });
  }

  broadcast(msg) {
    const text = JSON.stringify(msg);
    for (const ws of this.clients.keys()) {
      if (ws.readyState !== WebSocket.OPEN) continue;
      if (msg.type === 'state' && ws.bufferedAmount > 2 * 1024 * 1024) continue;
      ws.send(text);
    }
  }

  _send(ws, msg) {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  }

  _onConnection(ws, req) {
    const pathname = new URL(req.url, 'http://localhost').pathname;
    const channel = pathname === '/ws/motion' ? 'motion' : (pathname === '/ws/laser' ? 'laser' : 'legacy');
    const meta = { role: 'guest', alive: true, channel };
    this.clients.set(ws, meta);
    this._send(ws, {
      type: 'hello',
      server: `ThreeD Web Motion Service (Node · ${channel})`,
      ver: 6,
      role: meta.role,
      channel,
      commands: channel === 'laser' ? ['laser'] : [...COMMANDS].filter(c => channel !== 'motion' || c !== 'laser'),
    });

    ws.on('pong', () => { meta.alive = true; });
    ws.on('message', (data, isBinary) => {
      if (isBinary) { this._send(ws, { type: 'event', level: 'error', msg: '仅支持 JSON 文本指令' }); return; }
      let msg;
      try { msg = JSON.parse(data.toString('utf8')); }
      catch { this._send(ws, { type: 'event', level: 'error', msg: '指令不是有效 JSON' }); return; }

      if (msg?.type === 'register') {
        if (!['host', 'browser'].includes(msg.role)) {
          this._send(ws, { type: 'event', level: 'error', msg: 'register.role 必须是 host 或 browser' });
          return;
        }
        meta.role = msg.role;
        this._send(ws, { type: 'event', level: 'ok', msg: `已注册为 ${msg.role} 客户端` });
        return;
      }

      const error = commandError(msg, meta.channel);
      if (error) { this._send(ws, { type: 'event', level: 'warn', msg: error }); return; }
      this.sim.receive(msg);
    });
    ws.on('close', () => this.clients.delete(ws));
    ws.on('error', () => this.clients.delete(ws));
  }

  async _handleHttp(req, res) {
    const url = new URL(req.url, `http://${req.headers.host ?? 'localhost'}`);
    if (url.pathname === '/health') {
      const roles = { host: 0, browser: 0, guest: 0 };
      const channels = { legacy: 0, motion: 0, laser: 0 };
      for (const meta of this.clients.values()) { roles[meta.role]++; channels[meta.channel]++; }
      this._json(res, 200, {
        ok: true,
        service: 'ThreeD Web Motion Service',
        mode: 'simulation',
        uptime: Math.round((Date.now() - this.startedAt) / 1000),
        clients: this.clients.size,
        roles,
        channels,
        production: this.sim.production.state(),
        controlMode: this.sim.controlMode,
        io: this.sim.io.state(),
      });
      return;
    }
    if (!['GET', 'HEAD'].includes(req.method)) {
      this._json(res, 405, { ok: false, error: 'Method Not Allowed' });
      return;
    }

    let pathname;
    try { pathname = decodeURIComponent(url.pathname); }
    catch { this._json(res, 400, { ok: false, error: 'Bad URL' }); return; }
    const relative = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
    const file = path.resolve(this.distRoot, relative);
    if (file !== this.distRoot && !file.startsWith(this.distRoot + path.sep)) {
      this._json(res, 403, { ok: false, error: 'Forbidden' });
      return;
    }

    try {
      const data = await readFile(file);
      res.writeHead(200, {
        'Content-Type': MIME[path.extname(file).toLowerCase()] ?? 'application/octet-stream',
        'Cache-Control': file.endsWith('.html') ? 'no-cache' : 'public, max-age=3600',
        'X-Content-Type-Options': 'nosniff',
      });
      res.end(req.method === 'HEAD' ? undefined : data);
    } catch {
      this._json(res, 404, { ok: false, error: 'Not Found' });
    }
  }

  _json(res, status, obj) {
    const data = JSON.stringify(obj);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(data);
  }
}

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const service = new MotionWebService();
  await service.start();
  console.log('ThreeD Web Motion Service 已启动（模拟设备，无真实轴卡）');
  console.log(`  Web 页面: ${service.url}`);
  console.log(`  兼容 WebSocket: ${service.wsUrl}`);
  console.log(`  虚拟轴卡: ${service.motionWsUrl}`);
  console.log(`  虚拟激光控制卡: ${service.laserWsUrl}`);
  console.log(`  健康检查: ${service.url}/health`);
  console.log('  控制环: 120 Hz，状态广播: 30 Hz，Ctrl+C 退出');

  let stopping = false;
  const shutdown = async () => {
    if (stopping) return;
    stopping = true;
    await service.stop();
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}
