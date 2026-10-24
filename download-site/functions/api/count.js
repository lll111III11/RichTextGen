const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
  'Access-Control-Max-Age': '86400'
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...CORS }
  });
}

async function readCount(kv, key) {
  const raw = await kv.get(key);
  const n = parseInt(raw || '0', 10);
  return Number.isFinite(n) && n > 0 ? n : 0;
}

export async function onRequestOptions() {
  return new Response(null, { status: 204, headers: CORS });
}

/** 读取安装计数。total = 全部安装次数，files = 各安装包次数，versions = 按版本分开的次数 */
export async function onRequestGet({ request, env }) {
  if (!env.DOWNLOADS) return json({ ok: false, reason: 'kv-not-bound' }, 503);

  const url = new URL(request.url);
  const list = (url.searchParams.get('files') || '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .slice(0, 20);

  const files = {};
  for (const id of list) files[id] = await readCount(env.DOWNLOADS, `install:file:${id}`);

  // 每个版本单独一行，不再只看一个累计数
  const versions = {};
  const keys = await env.DOWNLOADS.list({ prefix: 'install:ver:' });
  for (const k of keys.keys) {
    const ver = k.name.slice('install:ver:'.length);
    if (ver) versions[ver] = await readCount(env.DOWNLOADS, k.name);
  }

  return json({
    ok: true,
    total: await readCount(env.DOWNLOADS, 'install:total'),
    files,
    versions
  });
}

/**
 * 只在安装成功后由安装器调用，代表「安装量 +1」。
 * 点击下载不计入，避免误点与重复下载把数字刷高。
 * body: { file: "setup", event: "install", version: "5.9.0.9", os: "win" }
 */
export async function onRequestPost({ request, env }) {
  if (!env.DOWNLOADS) return json({ ok: false, reason: 'kv-not-bound' }, 503);

  let body = {};
  try {
    body = await request.json();
  } catch {
    body = {};
  }

  if (String(body.event || '') !== 'install') {
    return json({ ok: false, reason: 'install-only' }, 400);
  }

  const file = String(body.file || 'unknown').slice(0, 40).replace(/[^\w.-]/g, '') || 'unknown';
  const version = String(body.version || '').slice(0, 24).replace(/[^\w.-]/g, '');
  const os = String(body.os || '').slice(0, 12).replace(/[^\w.-]/g, '');

  const total = (await readCount(env.DOWNLOADS, 'install:total')) + 1;
  const fileCount = (await readCount(env.DOWNLOADS, `install:file:${file}`)) + 1;

  await env.DOWNLOADS.put('install:total', String(total));
  await env.DOWNLOADS.put(`install:file:${file}`, String(fileCount));
  if (version) {
    const key = `install:ver:${version}`;
    await env.DOWNLOADS.put(key, String((await readCount(env.DOWNLOADS, key)) + 1));
  }
  if (os) {
    const key = `install:os:${os}`;
    await env.DOWNLOADS.put(key, String((await readCount(env.DOWNLOADS, key)) + 1));
  }

  return json({ ok: true, total, file, count: fileCount, version, os });
}
