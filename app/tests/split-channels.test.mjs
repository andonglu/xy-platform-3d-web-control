import test from 'node:test';
import assert from 'node:assert/strict';
import { WebSocket } from 'ws';
import { MotionWebService } from '../server/index.mjs';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function connect(url) {
  const ws = new WebSocket(url);
  const messages = [];
  ws.on('message', data => messages.push(JSON.parse(data)));
  await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
  return {
    ws, messages,
    send(msg) { ws.send(JSON.stringify(msg)); },
    async waitFor(predicate) {
      for (let i = 0; i < 500; i++) {
        const found = messages.find(predicate);
        if (found) return found;
        await sleep(10);
      }
      throw new Error(`消息等待超时: ${JSON.stringify(messages.slice(-4))}`);
    },
  };
}

test('轴卡与激光控制卡通道独立校验命令并共享设备状态', async () => {
  const service = new MotionWebService({ host: '127.0.0.1', port: 0 });
  await service.start();
  const clients = [];
  try {
    const base = `ws://127.0.0.1:${service.port}`;
    const observer = await connect(`${base}/ws`); clients.push(observer);
    const motion = await connect(`${base}/ws/motion`); clients.push(motion);
    const laser = await connect(`${base}/ws/laser`); clients.push(laser);

    motion.send({ type: 'laser', on: true });
    await motion.waitFor(m => m.type === 'event' && m.msg.includes('/ws/laser'));
    laser.send({ type: 'home' });
    await laser.waitFor(m => m.type === 'event' && m.msg.includes('仅接受 laser'));

    laser.send({ type: 'laser', on: true });
    await observer.waitFor(m => m.type === 'state' && m.laser === true);
    laser.send({ type: 'laser', on: false });
    await observer.waitFor(m => m.type === 'state' && m.laser === false);

    motion.send({ type: 'ptp', axis: 'x', pos: 10 });
    await observer.waitFor(m => m.type === 'state' && m.x?.pos === 10);
    const health = await fetch(`${service.url}/health`).then(r => r.json());
    assert.equal(health.channels.legacy, 1);
    assert.equal(health.channels.motion, 1);
    assert.equal(health.channels.laser, 1);
  } finally {
    for (const client of clients) client.ws.terminate();
    await service.stop();
  }
});
