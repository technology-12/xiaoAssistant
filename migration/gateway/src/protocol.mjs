import { timingSafeEqual, createHash } from 'node:crypto';

export const VERSION = 1;
export const MAX_BODY = 1024 * 1024;
export const MAX_TOOL_RESULT = 64 * 1024;
export function authenticated(actual, expected) {
  const a = Buffer.from(actual ?? ''); const b = Buffer.from(expected ?? '');
  return a.length === b.length && timingSafeEqual(a, b);
}
export function digest(value) { return createHash('sha256').update(value).digest('hex'); }
export function normalizeBaseUrl(value) {
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash)
    throw new Error('Invalid model endpoint');
  return url.href.replace(/\/$/, '');
}
export function validateRun(body) {
  if (body.version !== VERSION || !/^[a-f0-9]{32}$/.test(body.taskId ?? '') || !/^[a-f0-9]{32}$/.test(body.sessionId ?? ''))
    throw new Error('Invalid task/session or protocol version');
  if (!['Execute', 'ReadOnly'].includes(body.mode) || typeof body.request !== 'string' || body.request.length > 100000)
    throw new Error('Invalid execution mode or prompt');
  normalizeBaseUrl(body.model?.baseUrl);
  if (typeof body.model?.modelName !== 'string' || !/^[a-zA-Z0-9._:/-]{1,128}$/.test(body.model.modelName) || typeof body.model.apiKey !== 'string')
    throw new Error('Invalid model configuration');
  if (!Array.isArray(body.tools) || body.tools.length > 32) throw new Error('Invalid tool catalog');
  const names = new Set();
  for (const tool of body.tools) {
    if (!/^[a-zA-Z0-9_-]{1,64}$/.test(tool.name) || names.has(tool.name) || typeof tool.description !== 'string' || tool.inputSchema?.type !== 'object')
      throw new Error('Invalid tool definition');
    names.add(tool.name);
  }
  if (typeof body.system !== 'string' || body.system.length > 200000 || JSON.stringify(body.history ?? []).length > 512 * 1024)
    throw new Error('Run context too large');
  return body;
}
export async function readJson(request) {
  let bytes = 0; const parts = [];
  for await (const part of request) {
    bytes += part.length;
    if (bytes > MAX_BODY) throw new Error('Request too large');
    parts.push(part);
  }
  return JSON.parse(Buffer.concat(parts).toString('utf8') || '{}');
}
export function json(response, code, body) {
  response.writeHead(code, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  response.end(JSON.stringify(body));
}
export function publicRun(run, after = 0) {
  return { version: VERSION, id: run.id, state: run.state, text: run.text ?? '', error: run.error ?? null,
    errorCode: run.errorCode ?? null,
    events: run.events.filter(e => e.seq > after), next: run.events.at(-1)?.seq ?? after,
    command: run.commands.find(c => c.state === 'queued')?.public ?? null };
}
