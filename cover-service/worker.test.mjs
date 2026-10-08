import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync, existsSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import worker, { handle, isCover, isPng, MAX_IMAGE_BYTES, RESERVE_SQL, SHARDS } from './worker.mjs';

// JPEG header fixture; the optional desktop fixture checks real GDI+ encoded pixels too.
const header = new Uint8Array([255,216,255,192,0,17,8,2,0,2,0,3,1,17,0,2,17,0,3,17,0,
  255,218,0,12,3,1,0,2,17,3,17,0,63,0,1,255,217]);
const fixture = new URL('../release/cover-test.jpg', import.meta.url);
const image = existsSync(fixture) ? new Uint8Array(readFileSync(fixture)) : header;
// Produce a valid transparent PNG using Node's standard compression and CRC32.
function chunk(type, bytes) {
  const result = Buffer.alloc(bytes.length + 12); result.writeUInt32BE(bytes.length); result.write(type, 4); bytes.copy(result, 8);
  let crc = 0xffffffff;
  for (const byte of result.subarray(4, -4)) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
  result.writeUInt32BE((crc ^ 0xffffffff) >>> 0, result.length - 4); return result;
}
const ihdr = Buffer.from([0,0,2,0,0,0,2,0,8,6,0,0,0]);
const generatedPng = Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', ihdr),
  chunk('IDAT', deflateSync(Buffer.alloc((512 * 4 + 1) * 512))), chunk('IEND', Buffer.alloc(0))]);
const pngFixture = new URL('../release/cover-test.png', import.meta.url);
const png = existsSync(pngFixture) ? readFileSync(pngFixture) : generatedPng;
function setup() {
  const db = new DatabaseSync(':memory:');
  db.exec(readFileSync(new URL('schema.sql', import.meta.url), 'utf8'));
  let puts = 0;
  const images = Array.from({ length: SHARDS }, () => {
    const imageDb = new DatabaseSync(':memory:');
    imageDb.exec('PRAGMA foreign_keys = ON');
    imageDb.exec(readFileSync(new URL('images.sql', import.meta.url), 'utf8')); return imageDb;
  });
  const binding = database => ({ prepare(sql) { return { bind(...args) { return {
    async first() { return database.prepare(sql).get(...args) || null; },
    async all() { return { results: database.prepare(sql).all(...args) }; },
    async run() { const result = database.prepare(sql).run(...args); puts += Number(result.changes); return result; }
  }; } }; } });
  return { db, images, get puts() { return puts; }, close() { db.close(); for (const imageDb of images) imageDb.close(); }, env: {
    UPLOAD_LIMIT: { async limit() { return { success: true }; } },
    USE_LIMIT: { async limit() { return { success: true }; } },
    BUDGET: binding(db),
    ...Object.fromEntries(images.map((database, i) => [`IMAGES${i}`, binding(database)]))
  } };
}
const post = (bytes = image, type = 'image/jpeg') => new Request('https://covers.example/upload',
  { method: 'POST', headers: { 'Content-Type': type, 'CF-Connecting-IP': '127.0.0.1' }, body: bytes });

test('JPEG guard rejects other content, wrong dimensions, metadata and oversize', () => {
  assert.ok(isCover(header)); assert.ok(isCover(image));
  const wrong = header.slice(); wrong[9] = 1; assert.equal(isCover(wrong), false);
  const metadata = header.slice(); metadata[3] = 225; assert.equal(isCover(metadata), false);
  assert.equal(isCover(new TextEncoder().encode('<svg/>')), false);
  assert.equal(isCover(new Uint8Array(MAX_IMAGE_BYTES + 1)), false);
  assert.equal(isCover(header.slice(0, -2)), false);
});

test('upload, duplicate reuse, public GET/HEAD and method restriction', async () => {
  const s = setup();
  try {
    const first = await handle(post(), s.env); assert.equal(first.status, 200);
    const { url } = await first.json(); assert.match(url, /^https:\/\/covers.example\/covers\/[a-f0-9]{64}\.jpg$/);
    assert.equal((await handle(post(), s.env)).status, 200); assert.equal(s.puts, 1);
    assert.equal(s.db.prepare('SELECT uploads FROM budget WHERE id=1').get().uploads, 1);
    const get = await handle(new Request(url), s.env); assert.equal(get.headers.get('Content-Type'), 'image/jpeg');
    assert.equal(get.headers.get('X-Content-Type-Options'), 'nosniff');
    assert.deepEqual(new Uint8Array(await get.arrayBuffer()), image);
    const head = await handle(new Request(url, { method: 'HEAD' }), s.env); assert.equal(await head.text(), '');
    assert.equal(head.headers.get('Content-Length'), String(image.length));
    assert.equal((await handle(new Request('https://covers.example/upload'), s.env)).status, 404);
    assert.equal((await handle(new Request(url, { method: 'DELETE' }), s.env)).status, 403);
  } finally { s.close(); }
});

test('transparent PNG uploads round trip unchanged with correct MIME and extension', async () => {
  const s = setup();
  try {
    assert.ok(isPng(generatedPng)); assert.ok(isPng(png));
    const wrong = Buffer.from(png); wrong[19] = 1; assert.equal(isPng(wrong), false);
    assert.equal(isPng(Buffer.concat([png, Buffer.from('extra')])), false);
    assert.equal(isPng(png.subarray(0, -1)), false);
    const metadata = Buffer.concat([generatedPng.subarray(0, 33), chunk('tEXt', Buffer.from('private')), generatedPng.subarray(33)]);
    assert.equal(isPng(metadata), false);
    const response = await handle(post(png, 'image/png'), s.env); assert.equal(response.status, 200);
    const {url} = await response.json(); assert.match(url, /\.png$/);
    assert.equal((await handle(post(png, 'image/png'), s.env)).status, 200); assert.equal(s.puts, 1);
    const get = await handle(new Request(url), s.env); assert.equal(get.headers.get('content-type'), 'image/png');
    assert.deepEqual(Buffer.from(await get.arrayBuffer()), png);
    const head = await handle(new Request(url, {method:'HEAD'}), s.env);
    assert.equal(head.headers.get('content-type'), 'image/png'); assert.equal(await head.text(), '');
    assert.equal((await handle(new Request(url.replace('.png', '.jpg')), s.env)).status, 404);
    assert.equal((await handle(post(png, 'image/jpeg'), s.env)).status, 400);
    assert.equal((await handle(post(image, 'image/png'), s.env)).status, 400);
    assert.equal((await handle(post(new Uint8Array(MAX_IMAGE_BYTES + 1), 'image/png'), s.env)).status, 400);
  } finally { s.close(); }
});

test('rate limit, body limit and strict content type stop uploads', async () => {
  const s = setup();
  try {
    assert.equal((await handle(post(image, 'image/svg+xml'), s.env)).status, 415);
    assert.equal((await handle(post(new Uint8Array(MAX_IMAGE_BYTES + 1)), s.env)).status, 400);
    s.env.UPLOAD_LIMIT.limit = async () => ({ success: false });
    assert.equal((await handle(post(), s.env)).status, 429); assert.equal(s.puts, 0);
  } finally { s.close(); }
});

test('SQLite daily budget caps uploads and resets the day', async () => {
  const s = setup(); const today = new Date().toISOString().slice(0, 10);
  try {
    s.db.prepare('UPDATE budget SET day=?, uploads=1000 WHERE id=1').run(today);
    assert.equal((await handle(post(), s.env)).status, 503); assert.equal(s.puts, 0);
    s.db.prepare("UPDATE budget SET day='2000-01-01' WHERE id=1").run();
    assert.equal((await handle(post(), s.env)).status, 200);
    assert.equal(s.db.prepare('SELECT uploads FROM budget WHERE id=1').get().uploads, 1);
    s.db.exec('UPDATE budget SET uploads=999 WHERE id=1');
    const statement = s.db.prepare(RESERVE_SQL);
    const args = [today, today, today, 1000];
    assert.ok(statement.get(...args)); assert.equal(statement.get(...args), undefined);
  } finally { s.close(); }
});

test('full storage evicts only enough oldest covers, preserves duplicates, and rolls back on failure', () => {
  const s = setup(); const db = s.images[0]; const capacity = 300_000_000;
  const insert = db.prepare('INSERT OR IGNORE INTO covers (hash, size, data, last_used) VALUES (?, ?, ?, unixepoch()-7862400)');
  try {
    // Fill using cheap fake pixel data; sizes exercise the real SQLite triggers.
    insert.run('oldest', 100, 'old'); insert.run('next', 200, 'next');
    let remaining = capacity - 50 - 300; let i = 0;
    while (remaining) { const size = Math.min(remaining, MAX_IMAGE_BYTES); insert.run('filler'+i++, size, ''); remaining -= size; }
    insert.run('oldest', 100, 'ignored duplicate');
    assert.equal(db.prepare('SELECT count(*) AS n FROM covers WHERE hash=?').get('oldest').n, 1);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, capacity - 50);
    insert.run('new', 175, 'new');
    assert.equal(db.prepare("SELECT count(*) AS n FROM covers WHERE hash IN ('oldest','next')").get().n, 0);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, capacity - 175);
    assert.equal(db.prepare('SELECT SUM(size) AS n FROM covers').get().n, capacity - 175);
    const before = db.prepare('SELECT count(*) AS n FROM covers').get().n;
    db.exec("CREATE TRIGGER reject_test AFTER INSERT ON covers WHEN NEW.hash='fail' BEGIN SELECT RAISE(ABORT,'test failure'); END");
    assert.throws(() => insert.run('fail', 200, 'fail'));
    assert.equal(db.prepare('SELECT count(*) AS n FROM covers').get().n, before);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, capacity - 175);
    db.exec('DELETE FROM covers');
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, 0);
  } finally { s.close(); }
});

test('legacy JPEG covers migrate to PNG-capable storage without losing order or counters', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec('CREATE TABLE covers (hash TEXT PRIMARY KEY, size INTEGER CHECK(size<=262144), data TEXT)');
    db.exec("INSERT INTO covers (rowid,hash,size,data) VALUES (7,'old',123,'kept')");
    db.exec('CREATE TABLE storage (id INTEGER PRIMARY KEY, bytes INTEGER); INSERT INTO storage VALUES (1,123)');
    db.exec('BEGIN');
    db.exec(readFileSync(new URL('migrate-png.sql', import.meta.url), 'utf8'));
    db.exec(readFileSync(new URL('migrate-usage.sql', import.meta.url), 'utf8'));
    const schema = readFileSync(new URL('images.sql', import.meta.url), 'utf8');
    db.exec(schema); db.exec('COMMIT'); db.exec(schema);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, 123);
    assert.deepEqual({...db.prepare('SELECT rowid,data,format FROM covers').get()}, {rowid:7,data:'kept',format:'jpg'});
    db.prepare('INSERT INTO covers (hash,size,data,format) VALUES (?,?,?,?)').run('png', MAX_IMAGE_BYTES, '', 'png');
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, 123 + MAX_IMAGE_BYTES);
    db.exec('DELETE FROM covers'); assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes, 0);
  } finally { db.close(); }
});

test('storage failure gives a safe error and retains its daily reservation', async () => {
  const s = setup();
  try {
    for (let i = 0; i < SHARDS; i++) s.env[`IMAGES${i}`].prepare = sql => ({ bind() { return {
      async first() { return null; }, async run() { throw Error('private backend diagnostic'); }
    }; } });
    const response = await worker.fetch(post(), s.env); assert.equal(response.status, 503);
    assert.equal((await response.text()).includes('private backend'), false);
    assert.equal(s.db.prepare('SELECT uploads FROM budget WHERE id=1').get().uploads, 1);
  } finally { s.close(); }
});

test('activity signals renew old covers once per day without counting every viewer', async () => {
  const s = setup();
  try {
    const {url} = await (await handle(post(png, 'image/png'), s.env)).json();
    const hash = /\/([a-f0-9]{64})\.png$/.exec(url)[1];
    const db = s.images[parseInt(hash.slice(0, 8), 16) % SHARDS];
    db.prepare('UPDATE covers SET last_used=unixepoch()-172800 WHERE hash=?').run(hash);
    assert.equal((await handle(new Request(url, {method:'POST'}), s.env)).status, 204);
    assert.equal(db.prepare('SELECT use_days FROM covers').get().use_days, 1);
    assert.equal((await handle(new Request(url, {method:'POST'}), s.env)).status, 204);
    await handle(new Request(url), s.env);
    assert.equal(db.prepare('SELECT use_days FROM covers').get().use_days, 1);
    s.env.USE_LIMIT.limit = async () => ({success:false});
    assert.equal((await handle(new Request(url, {method:'POST'}), s.env)).status, 429);
    assert.equal((await handle(new Request(url.replace('.png','.jpg'), {method:'HEAD'}), s.env)).status, 404);
  } finally { s.close(); }
});

test('capacity evicts least recently used covers and breaks ties by usage', () => {
  const s = setup(); const db = s.images[0];
  try {
    const insert = db.prepare("INSERT INTO covers(hash,size,data,last_used,use_days) VALUES (?,?,'',unixepoch()-?,?)");
    insert.run('frequently-used',100,20*86400,20);
    insert.run('unused',200,20*86400,0);
    let remaining=300_000_000-350; let i=0;
    while(remaining) { const size=Math.min(remaining,MAX_IMAGE_BYTES); insert.run('active'+i++,size,0,1); remaining-=size; }
    insert.run('new',175,0,0);
    assert.ok(db.prepare("SELECT hash FROM covers WHERE hash='frequently-used'").get());
    assert.equal(db.prepare("SELECT hash FROM covers WHERE hash='unused'").get(),undefined);
    insert.run('another-upload',MAX_IMAGE_BYTES,0,0);
    assert.ok(db.prepare("SELECT hash FROM covers WHERE hash='another-upload'").get());
  } finally { s.close(); }
});

test('usage migration preserves images and starts their inactivity clock today', () => {
  const db = new DatabaseSync(':memory:');
  try {
    db.exec("CREATE TABLE covers(hash TEXT PRIMARY KEY,size INTEGER,data TEXT,format TEXT DEFAULT 'jpg'); INSERT INTO covers VALUES('old',5,'pixels','jpg')");
    db.exec(readFileSync(new URL('migrate-usage.sql',import.meta.url),'utf8'));
    db.exec(readFileSync(new URL('images.sql',import.meta.url),'utf8'));
    const row=db.prepare('SELECT data,last_used FROM covers').get();
    assert.equal(row.data,'pixels'); assert.ok(row.last_used>=Math.floor(Date.now()/1000)-5);
    db.exec("INSERT INTO covers(hash,size,data) VALUES('new',1,'new pixels')");
    assert.ok(db.prepare("SELECT last_used FROM covers WHERE hash='new'").get().last_used>0);
  } finally { db.close(); }
});

test('community gallery isolates games, deduplicates pixels, and removes deleted covers', async () => {
  const s = setup();
  try {
    const upload = game => { const request = post(png, 'image/png'); request.headers.set('X-VNDB-ID', game); return request; };
    const {url} = await (await handle(upload('v17'),s.env)).json();
    await handle(upload('v17'),s.env); await handle(upload('v18'),s.env);
    const list = game => handle(new Request('https://covers.example/gallery/'+game),s.env);
    assert.deepEqual(await (await list('v17')).json(), [{url}]);
    assert.deepEqual(await (await list('v18')).json(), [{url}]);
    assert.deepEqual(await (await list('v19')).json(), []);
    assert.equal(s.db.prepare('SELECT uploads FROM budget').get().uploads,1);
    assert.equal((await handle(upload('../private'),s.env)).status,400);
    const hash=url.split('/').at(-1).split('.')[0];
    s.images[parseInt(hash.slice(0,8),16)%SHARDS].prepare('DELETE FROM covers WHERE hash=?').run(hash);
    assert.deepEqual(await (await list('v17')).json(), []);
    assert.equal(s.images.reduce((n,db)=>n+db.prepare('SELECT count(*) AS n FROM game_covers').get().n,0),0);
    s.env.USE_LIMIT.limit=async()=>({success:false});
    assert.equal((await list('v17')).status,429);
  } finally { s.close(); }
});

test('unused covers remain indefinitely when storage has space', () => {
  const s=setup(); const db=s.images[0];
  try {
    db.exec("INSERT INTO covers(hash,size,data,last_used) VALUES('unused-for-a-year',100,'pixels',unixepoch()-31536000)");
    db.exec("INSERT INTO covers(hash,size,data) VALUES('new',200,'new pixels')");
    assert.ok(db.prepare("SELECT hash FROM covers WHERE hash='unused-for-a-year'").get());
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes,300);
    assert.equal(worker.scheduled,undefined);
    assert.deepEqual(JSON.parse(readFileSync(new URL('wrangler.jsonc',import.meta.url),'utf8')).triggers.crons,[]);
  } finally {s.close();}
});

test('only the original upload owner can delete; duplicate uploads cannot claim it', async () => {
  const s=setup(); const owner='a'.repeat(64), stranger='b'.repeat(64);
  const upload = key => {const request=post(png,'image/png');request.headers.set('X-VNDB-ID','v17');request.headers.set('X-Cover-Owner',key);return request;};
  const remove = (url,key) => handle(new Request(url,{method:'DELETE',headers:{'X-Cover-Owner':key}}),s.env);
  const gallery = key => handle(new Request('https://covers.example/gallery/v17',{headers:{'X-Cover-Owner':key}}),s.env);
  try {
    const {url}=await (await handle(upload(owner),s.env)).json();
    assert.deepEqual(await (await gallery(owner)).json(),[{url,canRemove:true}]);
    assert.equal((await gallery(owner)).headers.get('cache-control'),'no-store');
    await handle(upload(stranger),s.env);
    assert.deepEqual(await (await gallery(stranger)).json(),[{url,canRemove:false}]);
    assert.equal((await remove(url,stranger)).status,403);
    const hash=url.split('/').at(-1).split('.')[0];const db=s.images[parseInt(hash.slice(0,8),16)%SHARDS];
    const stored=db.prepare('SELECT owner FROM covers WHERE hash=?').get(hash).owner;
    assert.notEqual(stored,owner);assert.equal(stored.length,64);
    const otherGame=new Request(url,{method:'POST',headers:{'X-VNDB-ID':'v18'}});await handle(otherGame,s.env);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes,png.length);
    assert.equal((await remove(url,owner)).status,204);
    assert.equal(db.prepare('SELECT bytes FROM storage').get().bytes,0);
    assert.equal(db.prepare('SELECT count(*) AS n FROM game_covers').get().n,0);
    assert.deepEqual(await (await gallery(owner)).json(),[]);
    assert.equal((await handle(new Request(url),s.env)).status,404);
    assert.equal((await remove(url,owner)).status,404);
    assert.equal(s.db.prepare('SELECT uploads FROM budget WHERE id=1').get().uploads,1);
  } finally{s.close();}
});

test('legacy anonymous uploads cannot be claimed; malformed and rate-limited deletion fail safely', async () => {
  const s=setup();const key='c'.repeat(64);
  try {
    const {url}=await (await handle(post(png,'image/png'),s.env)).json();
    const claim=post(png,'image/png');claim.headers.set('X-Cover-Owner',key);await handle(claim,s.env);
    assert.equal((await handle(new Request(url,{method:'DELETE',headers:{'X-Cover-Owner':key}}),s.env)).status,403);
    assert.equal((await handle(new Request(url,{method:'DELETE',headers:{'X-Cover-Owner':'bad'}}),s.env)).status,400);
    const invalid=post(png,'image/png');invalid.headers.set('X-Cover-Owner','bad');assert.equal((await handle(invalid,s.env)).status,400);
    s.env.USE_LIMIT.limit=async()=>({success:false});
    assert.equal((await handle(new Request(url,{method:'DELETE',headers:{'X-Cover-Owner':key}}),s.env)).status,429);
  }finally{s.close();}
});

test('ownership migration keeps old cover data and leaves legacy ownership unset', () => {
  const db=new DatabaseSync(':memory:');
  try {
    db.exec("CREATE TABLE covers(hash TEXT PRIMARY KEY,size INTEGER,data TEXT,format TEXT,last_used INTEGER,use_days INTEGER); INSERT INTO covers VALUES('old',5,'pixels','png',123,7)");
    db.exec(readFileSync(new URL('migrate-owner.sql',import.meta.url),'utf8'));
    const row=db.prepare('SELECT * FROM covers').get();
    assert.equal(row.owner,null);assert.equal(row.data,'pixels');assert.equal(row.last_used,123);assert.equal(row.use_days,7);
  }finally{db.close();}
});