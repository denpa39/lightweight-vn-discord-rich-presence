export const MAX_IMAGE_BYTES = 1280 * 1024;
export const SHARDS = 9;
export const RESERVE_SQL = `UPDATE budget SET
  uploads = CASE WHEN day = ? THEN uploads + 1 ELSE 1 END, day = ?
  WHERE id = 1 AND (day <> ? OR uploads < ?)
  RETURNING id`;

// JPEG compatibility for older app versions. No remote URLs or file paths.
export function isCover(bytes) {
  if (bytes.length < 20 || bytes.length > 256 * 1024 || bytes[0] !== 0xff || bytes[1] !== 0xd8 ||
      bytes.at(-2) !== 0xff || bytes.at(-1) !== 0xd9) return false;
  let offset = 2; let square = false;
  while (offset + 4 < bytes.length) {
    if (bytes[offset++] !== 0xff) return false;
    while (bytes[offset] === 0xff) offset++;
    const marker = bytes[offset++];
    if (marker === 0xd9) return false;
    const length = bytes[offset] * 256 + bytes[offset + 1];
    if (length < 2 || offset + length > bytes.length) return false;
    if (marker === 0xda) return square && length === 12 && bytes[offset + 2] === 3 && offset + length < bytes.length - 2;
    if (marker === 0xc0) {
      if (square) return false;
      square = length === 17 && bytes[offset + 2] === 8 &&
        bytes[offset + 3] * 256 + bytes[offset + 4] === 512 &&
        bytes[offset + 5] * 256 + bytes[offset + 6] === 512 && bytes[offset + 7] === 3;
      if (!square) return false;
    }
    else if (marker !== 0xe0 && marker !== 0xdb && marker !== 0xc4 && marker !== 0xdd) return false;
    offset += length;
  }
  return false;
}

// Static 512x512 RGB/RGBA PNGs only; exclude metadata and animated PNG chunks.
export function isPng(bytes) {
  const signature = [137,80,78,71,13,10,26,10];
  if (bytes.length > MAX_IMAGE_BYTES || bytes.length < 57 || signature.some((b, i) => bytes[i] !== b)) return false;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 8; let pixels = false;
  while (offset + 12 <= bytes.length) {
    const length = view.getUint32(offset);
    if (length > bytes.length - offset - 12) return false;
    const type = String.fromCharCode(...bytes.subarray(offset + 4, offset + 8));
    const data = offset + 8;
    if (offset === 8) {
      if (type !== 'IHDR' || length !== 13 || view.getUint32(data) !== 512 || view.getUint32(data + 4) !== 512 ||
        bytes[data + 8] !== 8 || ![2,6].includes(bytes[data + 9]) ||
        bytes[data + 10] !== 0 || bytes[data + 11] !== 0 || bytes[data + 12] !== 0) return false;
    } else if (type === 'IDAT') {
      if (!length) return false;
      pixels = true;
    } else if (type === 'IEND') return pixels && length === 0 && offset + 12 === bytes.length;
    else {
      const allowed = { sRGB: 1, gAMA: 4, cHRM: 32, pHYs: 9 };
      if (allowed[type] !== length || pixels) return false;
    }
    offset += length + 12;
  }
  return false;
}

async function readImage(request) {
  const reader = request.body?.getReader();
  if (!reader) return null;
  const parts = []; let size = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.length;
      if (size > MAX_IMAGE_BYTES) { await reader.cancel(); return null; }
      parts.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.length; }
  return bytes;
}

function imageDb(env, hash) { return env[`IMAGES${parseInt(hash.slice(0, 8), 16) % SHARDS}`]; }
function encode(bytes) {
  let text = '';
  for (let start = 0; start < bytes.length; start += 32768)
    text += String.fromCharCode(...bytes.subarray(start, start + 32768));
  return btoa(text);
}
function decode(text) {
  const raw = atob(text); const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}
async function markUsed(database, hash) {
  const now = Math.floor(Date.now() / 1000);
  await database.prepare("UPDATE covers SET last_used = ?, use_days = use_days + 1 WHERE hash = ? AND last_used < ?")
    .bind(now, hash, now - 86400).run();
}
async function ownerHash(request) {
  const key = request.headers.get("X-Cover-Owner");
  if (key === null) return null;
  if (!/^[a-f0-9]{64}$/.test(key)) throw new TypeError("Invalid ownership key");
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(key)))]
    .map(b => b.toString(16).padStart(2, "0")).join("");
}
export async function handle(request, env) {
  const url = new URL(request.url);
  const gallery = /^\/gallery\/(v[1-9][0-9]{0,8})$/.exec(url.pathname);
  if (request.method === "GET" && gallery) {
    if (!(await env.USE_LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") || "unknown" })).success)
      return new Response("Too many requests", { status: 429 });
    let owner;
    try { owner = await ownerHash(request); } catch { return new Response("Invalid ownership key", {status: 400}); }
    // ponytail: show up to 40 covers per game; add pagination if games outgrow it.
    const results = await Promise.all(Array.from({length: SHARDS}, (_, i) => env[`IMAGES${i}`]
      .prepare("SELECT covers.hash, format, use_days, last_used, owner FROM game_covers JOIN covers USING(hash) WHERE game = ? ORDER BY use_days DESC, last_used DESC LIMIT 40")
      .bind(gallery[1]).all()));
    const covers = results.flatMap(r => r.results).sort((a,b) => b.use_days-a.use_days || b.last_used-a.last_used).slice(0,40);
    return Response.json(covers.map(c => ({url: `${url.origin}/covers/${c.hash}.${c.format}`, ...(owner ? {canRemove: c.owner === owner} : {})})),
      {headers: {"Cache-Control": "no-store"}});
  }
  if (request.method === "DELETE") {
    const match = /^\/covers\/([a-f0-9]{64})\.(jpg|png)$/.exec(url.pathname);
    if (!match) return new Response("Not found", {status: 404});
    if (!(await env.USE_LIMIT.limit({key: request.headers.get("CF-Connecting-IP") || "unknown"})).success)
      return new Response("Too many requests", {status: 429});
    let owner;
    try { owner = await ownerHash(request); } catch { return new Response("Invalid ownership key", {status: 400}); }
    if (!owner) return new Response("Not your upload", {status: 403});
    const database = imageDb(env, match[1]);
    const object = await database.prepare("SELECT format, owner FROM covers WHERE hash = ?").bind(match[1]).first();
    if (!object || object.format !== match[2]) return new Response("Not found", {status: 404});
    if (object.owner !== owner) return new Response("Not your upload", {status: 403});
    await database.prepare("DELETE FROM covers WHERE hash = ? AND format = ? AND owner = ?")
      .bind(match[1], match[2], owner).run();
    return new Response(null, {status: 204});
  }
  if (request.method === "POST" && /^\/covers\/[a-f0-9]{64}\.(jpg|png)$/.test(url.pathname)) {
    if (!(await env.USE_LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") || "unknown" })).success)
      return new Response("Too many requests", { status: 429 });
    const [, hash, format] = /^\/covers\/([a-f0-9]{64})\.(jpg|png)$/.exec(url.pathname);
    const database = imageDb(env, hash);
    const object = await database.prepare("SELECT format FROM covers WHERE hash = ?").bind(hash).first();
    if (!object || object.format !== format) return new Response("Not found", { status: 404 });
    const game = request.headers.get("X-VNDB-ID");
    if (game !== null && !/^v[1-9][0-9]{0,8}$/.test(game)) return new Response("Invalid game ID", {status: 400});
    await markUsed(database, hash);
    if (game) await database.prepare("INSERT OR IGNORE INTO game_covers (game, hash) VALUES (?, ?)").bind(game, hash).run();
    return new Response(null, { status: 204 });
  }
  if (request.method === "GET" || request.method === "HEAD") {
    const match = /^\/covers\/([a-f0-9]{64})\.(jpg|png)$/.exec(url.pathname);
    if (!match) return new Response("Not found", { status: 404 });
    const hash = match[1];
    const object = await imageDb(env, hash).prepare(request.method === "HEAD" ?
      "SELECT size, format FROM covers WHERE hash = ?" : "SELECT size, format, data FROM covers WHERE hash = ?").bind(hash).first();
    if (!object || object.format !== match[2]) return new Response("Not found", { status: 404 });
    // Serving an uncached image counts as use too; cached Discord reads need the app signal.
    await markUsed(imageDb(env, hash), hash);
    return new Response(request.method === "HEAD" ? null : decode(object.data), { headers: {
      "Content-Type": object.format === 'png' ? 'image/png' : 'image/jpeg', "Content-Length": String(object.size),
      "Cache-Control": "public, max-age=31536000, immutable", "ETag": `"${hash}"`,
      "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "default-src 'none'"
    } });
  }
  if (url.pathname !== "/upload") return new Response("Not found", { status: 404 });
  if (request.method !== "POST") return new Response("Method not allowed", { status: 405, headers: { Allow: "POST" } });
  let owner;
  try { owner = await ownerHash(request); } catch { return new Response("Invalid ownership key", {status: 400}); }
  const game = request.headers.get("X-VNDB-ID");
  if (game !== null && !/^v[1-9][0-9]{0,8}$/.test(game)) return new Response("Invalid game ID", {status: 400});
  // ponytail: anonymous cover uploads; IP limits can also affect users sharing a connection.
  if (!(await env.UPLOAD_LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") || "unknown" })).success)
    return new Response("Too many uploads", { status: 429, headers: { "Retry-After": "60" } });
  const type = request.headers.get("Content-Type");
  if (type !== 'image/jpeg' && type !== 'image/png') return new Response("PNG or JPEG required", { status: 415 });
  const bytes = await readImage(request);
  if (!bytes || !(type === 'image/png' ? isPng(bytes) : isCover(bytes)))
    return new Response("Expected a 512 by 512 PNG (up to 1.25 MiB) or JPEG (up to 256 KiB)", { status: 400 });
  const format = type === 'image/png' ? 'png' : 'jpg';
  const hash = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
    .map(b => b.toString(16).padStart(2, "0")).join("");
  const key = `covers/${hash}.${format}`;
  const database = imageDb(env, hash);
  if (!await database.prepare("SELECT size FROM covers WHERE hash = ?").bind(hash).first()) {
    const day = new Date().toISOString().slice(0, 10);
    // Daily limits are not storage limits: deleting images cannot reset them.
    const reserved = await env.BUDGET.prepare(RESERVE_SQL)
      .bind(day, day, day, 1000).first();
    if (!reserved) return new Response("Cover hosting upload limit reached", { status: 503 });
    // SQLite triggers evict inactive covers in this shard and insert atomically.
    // ponytail: hash chooses one storage slot; deleting in another slot cannot free this one.
    await database.prepare("INSERT OR IGNORE INTO covers (hash, size, data, format, owner) VALUES (?, ?, ?, ?, ?)")
      .bind(hash, bytes.length, encode(bytes), format, owner).run();
  } else await markUsed(database, hash);
  if (game) await database.prepare("INSERT OR IGNORE INTO game_covers (game, hash) VALUES (?, ?)").bind(game, hash).run();
  return Response.json({ url: `${url.origin}/${key}` });
}

export default {
  async fetch(request, env) {
    try { return await handle(request, env); }
    catch { return new Response("Cover hosting is unavailable. Try again later.", { status: 503 }); }
  }
};
