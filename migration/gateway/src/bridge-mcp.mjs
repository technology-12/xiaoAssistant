// MCP stdio: stdout is JSON-RPC only. Local files stay on the desktop.
import readline from 'node:readline';
const endpoint = process.env.XIAO_BRIDGE_URL;
const token = process.env.XIAO_BRIDGE_TOKEN;
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
async function bridge(path, body) {
  const response = await fetch(endpoint + path, { method: body ? 'POST' : 'GET',
    headers: { authorization: 'Bearer ' + token, 'content-type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(300000) });
  if (!response.ok) throw new Error('Local tool bridge failed: ' + response.status);
  return response.json();
}
async function handle(message) {
  if (message.id === undefined) return;
  try {
    let result;
    switch (message.method) {
      case 'initialize': result = { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'xiao-local-tools', version: '0.1.0' } }; break;
      case 'ping': result = {}; break;
      case 'tools/list': result = { tools: await bridge('/tools') }; break;
      case 'tools/call': {
        const output = await bridge('/call', { name: message.params.name, arguments: message.params.arguments ?? {} });
        result = { content: [{ type: 'text', text: JSON.stringify(output) }], isError: !output.ok }; break;
      }
      default: send({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Method not found' } }); return;
    }
    send({ jsonrpc: '2.0', id: message.id, result });
  } catch { send({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: 'Local tool execution unavailable' } }); }
}
readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('close', () => process.exit(0)).on('line', line => {
  if (line.length > 128 * 1024) return;
  try { void handle(JSON.parse(line)); } catch { send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } }); }
});
