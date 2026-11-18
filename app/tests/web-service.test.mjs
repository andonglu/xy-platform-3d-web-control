import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { WebSocket } from 'ws';
import { MotionWebService } from '../server/index.mjs';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function connectClient(url, role) {
  const ws = new WebSocket(url);
  const messages = [];
  ws.on('message', data => messages.push(JSON.parse(data.toString('utf8'))));
  await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
  const client = {
    ws,
    messages,
    send(msg) { ws.send(JSON.stringify(msg)); },
    async waitFor(predicate, timeout = 10000) {
      const start = Date.now();
      while (Date.now() - start < timeout) {
        const found = messages.find(predicate);
        if (found) return found;
        await sleep(10);
      }
      throw new Error(`等待 WebSocket 消息超时，最近消息: ${JSON.stringify(messages.slice(-5))}`);
    },
  };
  client.send({ type: 'register', role });
  await client.waitFor(msg => msg.type === 'event' && msg.msg.includes(role));
  return client;
}

async function closeClient(client) {
  if (client.ws.readyState === WebSocket.CLOSED) return;
  await new Promise(resolve => {
    client.ws.once('close', resolve);
    client.ws.close();
  });
}

test('Web 服务接受上位机控制并向浏览器广播完整生产状态', async () => {
  const distRoot = fileURLToPath(new URL('../node_modules/.cache/web-service-test-dist/', import.meta.url));
  await mkdir(distRoot, { recursive: true });
  await writeFile(`${distRoot}/index.html`, '<!doctype html><title>Web service test</title>');
  const service = new MotionWebService({ host: '127.0.0.1', port: 0, distRoot, timeScale: 12 });
  await service.start();
  let host, browser;
  try {
    const page = await fetch(`${service.url}/`);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /Web service test/);

    host = await connectClient(service.wsUrl, 'host');
    browser = await connectClient(service.wsUrl, 'browser');
    const health = await fetch(`${service.url}/health`).then(res => res.json());
    assert.equal(health.ok, true);
    assert.equal(health.roles.host, 1);
    assert.equal(health.roles.browser, 1);

    host.ws.send('{bad json');
    await host.waitFor(msg => msg.type === 'event' && msg.level === 'error');
    host.send({ type: 'unknown_command' });
    await host.waitFor(msg => msg.type === 'event' && msg.msg.includes('未知指令'));

    host.send({ type: 'production_start', recipe: 'star', count: 1 });
    await browser.waitFor(msg => msg.type === 'path' && msg.name === 'star');
    const complete = await browser.waitFor(msg =>
      msg.type === 'state' && msg.production?.phase === 'Complete' && msg.production.completed === 1);
    assert.equal(complete.production.workpieceId, 'P001');
    assert.equal(complete.laser, false);

    const phases = new Set(browser.messages.filter(msg => msg.type === 'state').map(msg => msg.production?.phase));
    for (const expected of ['Feeding', 'Loading', 'Clamping', 'Processing', 'Unclamping', 'Unloading', 'Retracting', 'Complete']) {
      assert.ok(phases.has(expected), `浏览器未收到工序 ${expected}`);
    }

    host.send({ type: 'home' });
    await browser.waitFor(msg => msg.type === 'event' && msg.msg === '回零完成');
    const idle = await browser.waitFor(msg => msg.type === 'state' && ['x', 'y', 'z'].every(axis => msg[axis]?.pos === 0));
    assert.ok(idle);
  } finally {
    if (host) await closeClient(host);
    if (browser) await closeClient(browser);
    await service.stop();
  }
});
