import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, readFile, mkdir, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createGateway } from '../src/server.mjs';
import { validateRun, authenticated } from '../src/protocol.mjs';
import { runtimeConfig, classifyUpstreamError } from '../src/opencode-runtime.mjs';

const token = 'synthetic-gateway-token-for-offline-tests';
function input() { return { version: 1, taskId: 'a'.repeat(32), sessionId: 'b'.repeat(32), mode: 'ReadOnly', request: 'hello', system: '', history: [],
  model: { baseUrl: 'http://127.0.0.1:12345', modelName: 'mock', apiKey: 'synthetic' }, tools: [{ name: 'list_files', description: 'List', inputSchema: { type: 'object' } }] }; }
test('protocol rejects duplicate tools and malformed sessions', () => {
  const body = input(); validateRun(body); body.tools.push(body.tools[0]); assert.throws(() => validateRun(body));
  const next = input(); next.sessionId = '../escape'; assert.throws(() => validateRun(next));
  const mode = input(); mode.mode = 'Administrator'; assert.throws(() => validateRun(mode));
});
test('credentials use exact authentication and runtime denies native tools', () => {
  assert.equal(authenticated('Bearer abc', 'Bearer abc'), true); assert.equal(authenticated('', 'Bearer abc'), false);
  const config = runtimeConfig({ input: input() }, 'http://127.0.0.1:1/internal', 'capability');
  assert.equal(config.permission['*'], 'deny'); assert.equal(config.permission['xiaolocal_*'], 'allow');
  assert.equal(config.provider.xiao.options.apiKey, '{env:XIAO_MODEL_API_KEY}');
  const error = classifyUpstreamError({ name: 'APIError', data: { statusCode: 401, message: 'secret-that-must-not-be-echoed' } });
  assert.equal(error.code, 'MODEL_AUTH_FAILED'); assert.equal(error.message.includes('secret'), false);
});
test('tool claim is at most once, acknowledgement idempotent, journal has no model key', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'xiao-gateway-'));
  let gateway;
  try {
    gateway = await createGateway({ token, dataRoot: root, runtime: async (run, ctx) => {
      const res = await fetch(`${ctx.localUrl}/internal/runs/${run.id}/call`, { method: 'POST', headers: { authorization: 'Bearer ' + run.bridgeToken }, body: JSON.stringify({ name: 'list_files', arguments: {} }) });
      const result = await res.json(); return { text: result.ok ? 'done' : 'failed', sessionId: 'synthetic' };
    } });
    const base = await gateway.listen(0);
    const api = (route, body, auth = token) => fetch(base + route, { method: body ? 'POST' : 'GET', headers: { authorization: 'Bearer ' + auth, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    assert.equal((await api('/health', null, 'wrong')).status, 401);
    const created = await (await api('/runs', input())).json();
    let status;
    for (let i = 0; i < 100; i++) { status = await (await api('/runs/' + created.id)).json(); if (status.command) break; await new Promise(r => setTimeout(r, 10)); }
    assert.ok(status.command);
    const claim = { commandId: status.command.id };
    assert.equal((await api(`/runs/${created.id}/claim`, claim)).status, 200);
    assert.equal((await api(`/runs/${created.id}/claim`, claim)).status, 409);
    const reply = { ...claim, result: { ok: true, data: {}, artifacts: [] } };
    assert.equal((await api(`/runs/${created.id}/results`, reply)).status, 200);
    assert.equal((await api(`/runs/${created.id}/results`, reply)).status, 200);
    assert.equal((await api(`/runs/${created.id}/results`, { ...reply, result: { ok: false } })).status, 409);
    await new Promise(r => setTimeout(r, 30));
    assert.equal((await (await api('/runs/' + created.id)).json()).state, 'completed');
    assert.equal((await api('/runs', input())).status, 409);
    assert.equal((await readFile(path.join(root, 'runs', created.id + '.json'), 'utf8')).includes('synthetic-gateway-token'), false);
    assert.equal((await readFile(path.join(root, 'runs', created.id + '.json'), 'utf8')).includes('apiKey'), false);
  } finally { await gateway?.close(); await rm(root, { recursive: true, force: true }); }
});
test('restart observes an interrupted run and never redispatches a claimed operation', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'xiao-recovery-')); let gateway; let executed = 0;
  try {
    await mkdir(path.join(root, 'runs'));
    const id = 'c'.repeat(32);
    await writeFile(path.join(root, 'runs', id + '.json'), JSON.stringify({ id, state: 'running', taskId: 'a'.repeat(32), sessionId: 'b'.repeat(32), events: [],
      commands: [{ id: 'd'.repeat(32), name: 'apply_file_plan', state: 'claimed', arguments: {} }] }));
    gateway = await createGateway({ token, dataRoot: root, runtime: async () => { executed++; throw new Error('Must not run'); } });
    const base = await gateway.listen(0);
    const response = await fetch(base + '/runs/' + id, { headers: { authorization: 'Bearer ' + token } });
    const state = await response.json(); assert.equal(state.state, 'interrupted'); assert.equal(state.command, null); assert.equal(executed, 0);
  } finally { await gateway?.close(); await rm(root, { recursive: true, force: true }); }
});
