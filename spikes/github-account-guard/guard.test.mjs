import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { guardedRequest } from './guard.mjs';

async function fixture(run) {
  const calls = [];
  let active = 'fixture-a';
  let identityStatus = 200;
  let identityBody = JSON.stringify({ id: 1 });
  let writeStatus = 200;
  const server = createServer((req, res) => {
    calls.push({ path: req.url, authorization: req.headers.authorization });
    if (req.url === '/user') {
      res.writeHead(identityStatus, { 'content-type': 'application/json' });
      res.end(identityBody);
    } else {
      res.writeHead(writeStatus, { location: 'https://example.invalid/stolen' });
      res.end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const send = (url, options) => fetch(`http://127.0.0.1:${server.address().port}${new URL(url).pathname}`, options);
  const args = { expectedId: 1, url: 'https://api.github.com/repos/example/issues/1', method: 'PATCH', body: '{}', resolveCredential: async () => active, send };
  try { await run({ args, calls, switchAccount: () => { active = 'fixture-b'; }, setIdentity: (status, body) => { identityStatus = status; identityBody = body; }, setWriteStatus: status => { writeStatus = status; } }); }
  finally { await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve())); }
}

test('account switch after identity response keeps the same credential snapshot', async () => fixture(async ({ args, calls, switchAccount }) => {
  const send = args.send;
  args.send = async (...input) => { const response = await send(...input); if (calls.length === 1) switchAccount(); return response; };
  assert.equal((await guardedRequest(args)).status, 200);
  assert.deepEqual(calls.map(call => call.authorization), ['Bearer fixture-a', 'Bearer fixture-a']);
}));

test('wrong or malformed identity never reaches the write', async () => {
  for (const body of ['{"id":2}', '{', '{"id":"1"}']) await fixture(async ({ args, calls, setIdentity }) => {
    setIdentity(200, body);
    await assert.rejects(guardedRequest(args), /Identity check failed|Account mismatch/);
    assert.deepEqual(calls.map(call => call.path), ['/user']);
  });
});

test('failed identity check never reaches the write', async () => fixture(async ({ args, calls, setIdentity }) => {
  setIdentity(401, '{}');
  await assert.rejects(guardedRequest(args), /Identity check failed/);
  assert.equal(calls.length, 1);
}));

test('redirects are not followed', async () => fixture(async ({ args, calls, setWriteStatus }) => {
  setWriteStatus(307);
  await assert.rejects(guardedRequest(args), /Redirect rejected/);
  assert.equal(calls.length, 2);
}));

test('untrusted destinations are rejected before credential resolution', async () => fixture(async ({ args, calls }) => {
  for (const url of ['http://api.github.com/user', 'https://api.github.com.evil.test/user', 'https://api.github.com:444/user', 'https://user@api.github.com/user']) {
    let resolved = false;
    await assert.rejects(guardedRequest({ ...args, url, resolveCredential: async () => { resolved = true; return 'fixture-a'; } }), /Destination rejected/);
    assert.equal(resolved, false);
  }
  assert.equal(calls.length, 0);
}));

test('credential lookup failure sends nothing', async () => fixture(async ({ args, calls }) => {
  await assert.rejects(guardedRequest({ ...args, resolveCredential: async () => null }), /Authentication unavailable/);
  assert.equal(calls.length, 0);
}));

test('revoked snapshot does not trigger a new credential lookup', async () => fixture(async ({ args, calls, setWriteStatus }) => {
  let lookups = 0;
  const original = args.resolveCredential;
  args.resolveCredential = async () => { lookups++; return original(); };
  setWriteStatus(401);
  assert.equal((await guardedRequest(args)).status, 401);
  assert.equal(lookups, 1);
  assert.deepEqual(calls.map(call => call.authorization), ['Bearer fixture-a', 'Bearer fixture-a']);
}));

test('identity redirects fail closed without a write', async () => fixture(async ({ args, calls, setIdentity }) => {
  setIdentity(302, '{}');
  await assert.rejects(guardedRequest(args), /Identity check failed/);
  assert.equal(calls.length, 1);
}));
