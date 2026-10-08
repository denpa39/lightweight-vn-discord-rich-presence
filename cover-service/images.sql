CREATE TABLE IF NOT EXISTS covers (
  hash TEXT PRIMARY KEY,
  size INTEGER NOT NULL CHECK (size > 0 AND size <= 1310720),
  data TEXT NOT NULL,
  format TEXT NOT NULL DEFAULT 'jpg' CHECK (format IN ('jpg', 'png')),
  last_used INTEGER NOT NULL DEFAULT (unixepoch()),
  use_days INTEGER NOT NULL DEFAULT 0,
  owner TEXT CHECK (owner IS NULL OR length(owner) = 64)
);
CREATE INDEX IF NOT EXISTS cover_usage ON covers(last_used, use_days);
CREATE TABLE IF NOT EXISTS game_covers (
  game TEXT NOT NULL,
  hash TEXT NOT NULL REFERENCES covers(hash) ON DELETE CASCADE,
  PRIMARY KEY (game, hash)
);
CREATE INDEX IF NOT EXISTS game_cover_hash ON game_covers(hash);
CREATE TABLE IF NOT EXISTS storage (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  bytes INTEGER NOT NULL DEFAULT 0 CHECK (bytes >= 0)
);
INSERT OR IGNORE INTO storage (id, bytes) SELECT 1, COALESCE(SUM(size), 0) FROM covers;

CREATE TRIGGER IF NOT EXISTS cover_added AFTER INSERT ON covers
BEGIN
  UPDATE storage SET bytes = bytes + NEW.size WHERE id = 1;
END;
CREATE TRIGGER IF NOT EXISTS cover_removed AFTER DELETE ON covers
BEGIN
  UPDATE storage SET bytes = bytes - OLD.size WHERE id = 1;
END;
-- Keep all covers while there is room. Evict least recently used covers only at capacity.
-- Eviction and insertion share one transaction; errors roll both back.
DROP TRIGGER IF EXISTS cover_make_room;
CREATE TRIGGER cover_make_room BEFORE INSERT ON covers
WHEN NOT EXISTS (SELECT 1 FROM covers WHERE hash = NEW.hash)
  AND (SELECT bytes FROM storage WHERE id = 1) + NEW.size > 300000000
BEGIN
  DELETE FROM covers WHERE rowid IN (
    SELECT rowid FROM (
      SELECT rowid, size, SUM(size) OVER (ORDER BY last_used, use_days, rowid) AS freed FROM covers
    ) WHERE freed - size < (SELECT bytes FROM storage WHERE id = 1) + NEW.size - 300000000
  );
  SELECT CASE WHEN (SELECT bytes FROM storage WHERE id = 1) + NEW.size > 300000000
    THEN RAISE(ABORT, 'Cover storage limit reached') END;
END;
