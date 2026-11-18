import test from 'node:test';
import assert from 'node:assert/strict';
import { SimController } from '../src/sim.js';
import { PRODUCTION_POSITIONS } from '../src/production.js';

function createSim() {
  const messages = [];
  const sim = new SimController(msg => messages.push(msg));
  return { sim, messages };
}

function runUntil(sim, predicate, maxSeconds = 120) {
  const steps = Math.ceil(maxSeconds * 120);
  for (let i = 0; i < steps; i++) {
    sim.tick(1 / 120);
    if (predicate()) return;
  }
  throw new Error(`条件在 ${maxSeconds}s 仿真时间内未满足，当前状态: ${JSON.stringify(sim.production.state())}`);
}

for (const recipe of ['star', 'circle', 'combo']) {
  test(`单件自动生产完成完整闭环: ${recipe}`, () => {
    const { sim } = createSim();
    sim.receive({ type: 'production_start', recipe, count: 1 });
    runUntil(sim, () => sim.production.phase === 'Complete');
    const p = sim.production.state();
    assert.equal(p.completed, 1);
    assert.equal(p.workpieceId, 'P001');
    assert.equal(p.workpieceX, PRODUCTION_POSITIONS.outfeedX);
    assert.equal(p.pusherX, PRODUCTION_POSITIONS.pusherHome);
    assert.equal(p.clamp, 0);
    assert.equal(p.running, false);
    assert.equal(sim.laser, false);
    assert.equal(sim.job, null);
  });
}

test('连续生产收到停止指令后完成当前工件再停机', () => {
  const { sim } = createSim();
  sim.receive({ type: 'production_start', recipe: 'star', count: 0 });
  runUntil(sim, () => sim.production.completed >= 2);
  sim.receive({ type: 'production_stop' });
  assert.equal(sim.production.stopRequested, true);
  runUntil(sim, () => sim.production.phase === 'Complete');
  assert.equal(sim.production.completed, 3); // 第三件已经开始，必须完成并下料
  assert.equal(sim.production.running, false);
  assert.equal(sim.laser, false);
});

test('急停冻结工件和推杆，复位前不能重启', () => {
  const { sim } = createSim();
  sim.receive({ type: 'production_start', recipe: 'star', count: 1 });
  runUntil(sim, () => sim.production.phase === 'Loading');
  sim.receive({ type: 'estop' });
  const frozen = sim.production.state();
  for (let i = 0; i < 120; i++) sim.tick(1 / 120);
  assert.equal(sim.production.phase, 'Faulted');
  assert.equal(sim.production.workpieceX, frozen.workpieceX);
  assert.equal(sim.production.pusherX, frozen.pusherX);
  assert.equal(sim.laser, false);
  sim.receive({ type: 'production_start', recipe: 'star', count: 1 });
  assert.equal(sim.production.phase, 'Faulted');
  sim.receive({ type: 'production_reset' });
  assert.equal(sim.production.phase, 'Idle');
  assert.equal(sim.production.workpieceId, null);
});

test('自动生产期间屏蔽手动作业和手动激光', () => {
  const { sim } = createSim();
  sim.receive({ type: 'laser', on: true });
  sim.receive({ type: 'production_start', recipe: 'star', count: 1 });
  assert.equal(sim.laser, false, '启动自动生产时必须关闭手动激光');
  sim.receive({ type: 'laser', on: true });
  sim.receive({ type: 'job', name: 'combo' });
  assert.equal(sim.laser, false);
  assert.equal(sim.job, null);
  assert.equal(sim.production.running, true);
});
