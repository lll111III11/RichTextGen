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

export async function onRequestGet({ request, env }) {
  if (!env.DOWNLOADS) return json({ ok: false, reason: 'kv-not-bound' }, 503);

  const url = new URL(request.url);
  const list = (url.searchParams.get('files') || '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .slice(0, 20);

  const files = {};
  for (const id of list) files[id] = await readCount(env.DOWNLOADS, `file:${id}`);

  return json({ ok: true, total: await readCount(env.DOWNLOADS, 'total'), files });
}

export async function onRequestPost({ request, env }) {
  if (!env.DOWNLOADS) return json({ ok: false, reason: 'kv-not-bound' }, 503);

  let body = {};
  try {
    body = await request.json();
  } catch {
    body = {};
  }

  const file = String(body.file || 'unknown').slice(0, 40).replace(/[^\w.-]/g, '');
  const mirror = String(body.mirror || '').slice(0, 32).replace(/[^\w.-]/g, '');

  const total = (await readCount(env.DOWNLOADS, 'total')) + 1;
  const fileCount = (await readCount(env.DOWNLOADS, `file:${file}`)) + 1;

  await env.DOWNLOADS.put('total', String(total));
  await env.DOWNLOADS.put(`file:${file}`, String(fileCount));
  if (mirror) {
    const key = `mirror:${mirror}`;
    await env.DOWNLOADS.put(key, String((await readCount(env.DOWNLOADS, key)) + 1));
  }

  return json({ ok: true, total, file, count: fileCount, mirror });
}
