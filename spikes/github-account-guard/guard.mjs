// Research prototype only: the credential provider and transport belong inside a CLI,
// never in Chaos. This module must not be connected to real GitHub credentials.
export async function guardedRequest({ expectedId, url, method = 'GET', body, resolveCredential, send }) {
  const target = new URL(url);
  if (target.protocol !== 'https:' || target.hostname !== 'api.github.com' || target.port || target.username || target.password || target.hash) {
    throw new Error('Destination rejected');
  }
  if (!Number.isSafeInteger(expectedId) || expectedId <= 0) throw new Error('Invalid expected account');
  const credential = await resolveCredential();
  if (!credential) throw new Error('Authentication unavailable');
  const request = (address, options) => send(address, {
    ...options, redirect: 'manual', headers: { authorization: `Bearer ${credential}` },
  });
  const identity = await request('https://api.github.com/user', { method: 'GET' });
  if (identity.status !== 200) throw new Error('Identity check failed');
  let account;
  try { account = await identity.json(); } catch { throw new Error('Identity check failed'); }
  if (!Number.isSafeInteger(account?.id) || account.id !== expectedId) throw new Error('Account mismatch');
  const response = await request(target.href, { method, body });
  if (response.status >= 300 && response.status < 400 && response.status !== 304) throw new Error('Redirect rejected');
  return response;
}
