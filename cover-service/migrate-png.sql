-- Rebuild the legacy JPEG table with a larger PNG limit, preserving FIFO order.
-- Run only on the legacy schema, then apply images.sql to recreate its triggers.
DROP TRIGGER IF EXISTS cover_make_room;
DROP TRIGGER IF EXISTS cover_added;
DROP TRIGGER IF EXISTS cover_removed;
CREATE TABLE covers_png_migration (
  hash TEXT PRIMARY KEY,
  size INTEGER NOT NULL CHECK (size > 0 AND size <= 1310720),
  data TEXT NOT NULL,
  format TEXT NOT NULL DEFAULT 'jpg' CHECK (format IN ('jpg', 'png'))
);
INSERT INTO covers_png_migration (rowid, hash, size, data)
  SELECT rowid, hash, size, data FROM covers;
DROP TABLE covers;
ALTER TABLE covers_png_migration RENAME TO covers;
