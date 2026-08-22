import { readFile } from 'node:fs/promises';
import { parseJsonText, RuntimeClient, RuntimeProtocolError, RuntimeReducer, SCHEMA_SHA256 } from '../src/index.ts';

const fixtureUrl = new URL('../../fixtures/canonical-run.jsonl', import.meta.url);
const lines = (await readFile(fixtureUrl, 'utf8')).trim().split(/\r?\n/u);
const reducer = new RuntimeReducer();
for (const line of lines) reducer.apply(JSON.parse(line));
const snapshot = reducer.snapshot();
if (snapshot.status !== 'completed' || snapshot.lastSequence !== 7 || snapshot.items.length !== 1) {
  throw new Error('TypeScript Runtime Protocol fixture failed.');
}
if (!/^[0-9a-f]{64}$/u.test(SCHEMA_SHA256)) throw new Error('Schema provenance is missing.');
expectProtocolError(() => parseJsonText('{"value":1,"value":2}'), 'duplicate-field');
expectProtocolError(() => parseJsonText(`${'['.repeat(129)}0${']'.repeat(129)}`), 'json-too-deep');

const encoder = new TextEncoder();
const sse = lines.map((line) => {
  const event = JSON.parse(line) as { eventId: string };
  return `event: runtime\nid: ${event.eventId}\ndata: ${line}\n\n`;
}).join('');
const fakeFetch = async (url: string, init: { body: string }) => {
  const path = new URL(url).pathname;
  const body = JSON.parse(init.body) as Record<string, unknown>;
  if (path.endsWith('/initialize')) return textResponse({ version: 1, capabilities: [], serverName: 'fixture', serverVersion: '1' });
  if (path.endsWith('/events')) return textResponse({
    sessionId: body.sessionId, actorId: body.actorId, requestedAfterSequence: body.afterSequence,
    firstRetainedSequence: 1, lastSequence: 7, nextAfterSequence: 7, gap: false, events: [],
  });
  if (path.endsWith('/steer') || path.endsWith('/interrupt')) return textResponse({ status: 'accepted', activeRunId: 'run', activeTurn: 1, accepted: true });
  if (path.endsWith('/stream')) return streamResponse(sse);
  throw new Error('Unexpected Runtime endpoint.');
};
const client = new RuntimeClient({ baseUrl: 'http://127.0.0.1:5157/', fetch: fakeFetch });
await client.initialize();
await client.readEvents({ sessionId: 'session', actorId: 'actor', afterSequence: 0, maximum: 32 });
const control = { sessionId: 'session', actorId: 'actor', expectedRunId: 'run', expectedTurnId: 'turn-1', expectedTurn: 1, messageJson: '{}' };
if (!(await client.steer(control)).accepted || !(await client.interrupt(control)).accepted) throw new Error('TypeScript control client failed.');
let streamed = 0;
const streamResult = await client.stream({ requestId: 'request', inputJson: '{}' }, () => { streamed += 1; });
if (!streamResult.terminal || streamed !== 7 || streamResult.lastSequence !== 7) throw new Error('TypeScript SSE client failed.');
console.log('OPENGAMEAGENT_RUNTIME_TYPESCRIPT_OK');

function expectProtocolError(action: () => unknown, code: string): void {
  try { action(); } catch (error) {
    if (error instanceof RuntimeProtocolError && error.code === code) return;
    throw error;
  }
  throw new Error(`Expected Runtime Protocol error ${code}.`);
}

function textResponse(value: unknown) {
  const text = JSON.stringify(value);
  return { ok: true, status: 200, text: async () => text, body: null };
}

function streamResponse(value: string) {
  let sent = false;
  const reader = {
    read: async () => sent ? { done: true } : (sent = true, { done: false, value: encoder.encode(value) }),
    cancel: async () => {},
    releaseLock: () => {},
  };
  return { ok: true, status: 200, text: async () => '', body: { getReader: () => reader } };
}
