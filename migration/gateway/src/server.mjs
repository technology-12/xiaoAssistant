import http from 'node:http';
import { readFile, mkdir, writeFile, rename, readdir } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { authenticated, digest, json, readJson, validateRun, publicRun, MAX_TOOL_RESULT } from './protocol.mjs';
import { startRuntime } from './opencode-runtime.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export async function createGateway(options = {}) {
  const token = options.token ?? process.env.XIAO_GATEWAY_TOKEN ??
    (process.env.XIAO_GATEWAY_TOKEN_FILE ? (await readFile(process.env.XIAO_GATEWAY_TOKEN_FILE, 'utf8')).trim() : '');
  if (token.length < 32) throw new Error('Set a gateway token with at least 32 characters');
  const dataRoot = path.resolve(options.dataRoot ?? process.env.XIAO_DATA_ROOT ?? path.join(here, '../../.local/server'));
  await mkdir(path.join(dataRoot, 'runs'), { recursive: true, mode: 0o700 });
  const runs = new Map(); const activeSessions = new Set();
  const runtime = options.runtime ?? startRuntime;
  let localUrl;
  async function journal(run) {
    const record = { ...publicRun(run), sessionId: run.input.sessionId, taskId: run.input.taskId,
      remoteSessionId: run.remoteSessionId, updated: new Date().toISOString(),
      commands: run.commands.map(c => ({ ...c.public, state: c.state, result: c.result ?? null })) };
    // Credentials and provider config are deliberately absent. Serialize writes per run.
    run.journal = (run.journal ?? Promise.resolve()).catch(() => {}).then(async () => {
      const file = path.join(dataRoot, 'runs', run.id + '.json');
      await writeFile(file + '.tmp', JSON.stringify(record), { mode: 0o600 });
      await rename(file + '.tmp', file);
    });
    await run.journal;
  }
  function emit(run, kind, text) {
    const value = String(text).slice(0, 32768);
    if (run.events.length >= 2048 || (run.eventChars ?? 0) + value.length > 256 * 1024) return;
    run.eventChars = (run.eventChars ?? 0) + value.length;
    run.events.push({ seq: (run.events.at(-1)?.seq ?? 0) + 1, kind, text: value });
  }
  async function stop(run, state = 'cancelled') {
    if (!['starting', 'running'].includes(run.state)) return;
    run.state = state;
    for (const command of run.commands.filter(c => c.state !== 'replied')) {
      command.resolve({ ok: false, error: { code: 'CANCELLED', message: 'Run stopped; do not replay local operations.' }, data: {}, artifacts: [] });
    }
    await run.runtime?.close();
    await journal(run);
  }
  async function execute(run) {
    const timer = setTimeout(() => { void stop(run, 'interrupted'); }, 5 * 60 * 1000);
    try {
      run.state = 'running'; await journal(run);
      const result = await runtime(run, { dataRoot, localUrl, emit });
      if (run.state === 'running') { run.text = result.text; run.remoteSessionId = result.sessionId; run.usage = result.usage; run.state = 'completed'; }
    } catch (error) {
      if (['starting', 'running'].includes(run.state)) {
        run.state = 'failed';
        run.errorCode = ['MODEL_AUTH_FAILED', 'MODEL_PROTOCOL_ERROR', 'MODEL_UNAVAILABLE'].includes(error.publicCode) ? error.publicCode : 'REMOTE_KERNEL_UNAVAILABLE';
        run.error = error.publicMessage ?? 'OpenCode 执行失败，请检查服务端模型配置、网络和固定版本运行环境。';
      }
    } finally {
      clearTimeout(timer); activeSessions.delete(run.input.sessionId);
      for (const command of run.commands.filter(c => c.state !== 'replied'))
        command.resolve({ ok: false, error: { code: 'WORKER_UNAVAILABLE', message: 'Agent stopped; local result not acknowledged.' }, data: {}, artifacts: [] });
      // Drop the in-memory model credential when the upstream process exits.
      run.input.model.apiKey = ''; await journal(run);
    }
  }
  // Restart recovery is observational only. Claimed local commands are never requeued.
  for (const name of await readdir(path.join(dataRoot, 'runs'))) {
    if (!/^[a-f0-9]{32}\.json$/.test(name)) continue;
    try {
      const saved = JSON.parse(await readFile(path.join(dataRoot, 'runs', name), 'utf8'));
      const run = { ...saved, input: { sessionId: saved.sessionId, taskId: saved.taskId },
        state: ['starting', 'running'].includes(saved.state) ? 'interrupted' : saved.state,
        commands: [], events: saved.events ?? [], bridgeToken: '' };
      runs.set(run.id, run);
    } catch { /* Invalid journal is never executed. */ }
  }
  const server = http.createServer(async (request, response) => {
    try {
      const url = new URL(request.url, 'http://localhost');
      const segments = url.pathname.split('/').filter(Boolean);
      const internal = segments[0] === 'internal';
      const id = internal ? segments[2] : segments[1];
      const run = runs.get(id);
      const supplied = request.headers.authorization;
      if (!authenticated(supplied, 'Bearer ' + (internal ? run?.bridgeToken : token))) { json(response, 401, { error: 'Unauthorized' }); return; }
      if (internal && (!run || !['starting', 'running'].includes(run.state))) { json(response, 409, { error: 'Run inactive' }); return; }
      if (internal && request.method === 'GET' && segments[3] === 'tools') { json(response, 200, run.input.tools); return; }
      if (internal && request.method === 'POST' && segments[3] === 'call') {
        const call = await readJson(request);
        if (!run.input.tools.some(t => t.name === call.name) || !call.arguments || typeof call.arguments !== 'object' || Array.isArray(call.arguments) || JSON.stringify(call.arguments).length > 64 * 1024)
          { json(response, 400, { error: 'Tool not allowed or arguments invalid' }); return; }
        if (run.commands.length >= 24) { json(response, 200, { ok: false, data: {}, artifacts: [], error: { code: 'LIMIT_EXCEEDED', message: 'Tool budget exhausted.' } }); return; }
        const command = { public: { id: randomBytes(16).toString('hex'), name: call.name, arguments: call.arguments }, state: 'queued' };
        const waiting = new Promise(resolve => { command.resolve = resolve; });
        run.commands.push(command); await journal(run);
        const result = await waiting;
        json(response, 200, result); return;
      }
      if (!internal && request.method === 'GET' && url.pathname === '/health') {
        json(response, 200, { version: 1, engine: 'opencode', upstreamVersion: '1.1.20', healthy: true }); return;
      }
      if (!internal && request.method === 'POST' && url.pathname === '/runs') {
        const input = validateRun(await readJson(request));
        if (runs.size >= 1000) { json(response, 503, { error: 'Run journal capacity reached; administrator maintenance required.' }); return; }
        const existing = [...runs.values()].find(r => r.input.taskId === input.taskId);
        if (existing) { json(response, 409, { error: 'Task already submitted; inspect its existing run instead of replaying.', id: existing.id }); return; }
        if (activeSessions.has(input.sessionId) || activeSessions.size >= 4) { json(response, 409, { error: 'Session busy or server capacity reached' }); return; }
        const run = { id: randomBytes(16).toString('hex'), input, state: 'starting', events: [], commands: [], bridgeToken: randomBytes(32).toString('hex') };
        runs.set(run.id, run); activeSessions.add(input.sessionId); await journal(run);
        json(response, 201, publicRun(run)); void execute(run); return;
      }
      if (!internal && segments[0] === 'runs' && run) {
        if (request.method === 'GET' && segments.length === 2) { json(response, 200, publicRun(run, Number(url.searchParams.get('after') ?? 0))); return; }
        if (request.method === 'POST' && segments[2] === 'cancel') { await stop(run); json(response, 200, publicRun(run)); return; }
        if (request.method === 'POST' && segments[2] === 'claim') {
          const { commandId } = await readJson(request);
          const command = run.commands.find(c => c.public.id === commandId);
          if (!command || command.state !== 'queued' || run.state !== 'running') { json(response, 409, { error: 'Command unavailable; never replay a claim' }); return; }
          command.state = 'claimed'; await journal(run); json(response, 200, command.public); return;
        }
        if (request.method === 'POST' && segments[2] === 'results') {
          const { commandId, result } = await readJson(request);
          const command = run.commands.find(c => c.public.id === commandId);
          if (!command || !['claimed', 'replied'].includes(command.state) || typeof result?.ok !== 'boolean' || JSON.stringify(result).length > MAX_TOOL_RESULT)
            { json(response, 400, { error: 'Invalid or excessive tool response' }); return; }
          if (command.state === 'replied') {
            json(response, JSON.stringify(result) === JSON.stringify(command.result) ? 200 : 409, { acknowledged: true }); return;
          }
          command.result = result; command.state = 'replied'; await journal(run);
          command.resolve(result); json(response, 200, { acknowledged: true }); return;
        }
      }
      json(response, 404, { error: 'Not found' });
    } catch {
      if (!response.headersSent) json(response, 400, { error: 'Invalid request or unavailable runtime' }); else response.destroy();
    }
  });
  server.requestTimeout = 330000;
  server.headersTimeout = 15000;
  return { server, runs, dataRoot,
    async listen(port = 4388, host = '127.0.0.1') {
      await new Promise((resolve, reject) => server.once('error', reject).listen(port, host, resolve));
      localUrl = `http://127.0.0.1:${server.address().port}`; return localUrl;
    },
    async close() {
      await Promise.all([...runs.values()].map(r => stop(r)));
      server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    },
  };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const gateway = await createGateway();
  await gateway.listen(Number(process.env.XIAO_PORT ?? 4388), process.env.XIAO_HOST ?? '127.0.0.1');
  console.log('Xiao OpenCode gateway ready on port ' + gateway.server.address().port);
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { void gateway.close().then(() => process.exit(0)); });
}
