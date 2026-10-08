-- Apply once to existing JPEG/PNG tables. Existing covers get a fresh 90-day grace period.
ALTER TABLE covers ADD COLUMN last_used INTEGER NOT NULL DEFAULT 0;
ALTER TABLE covers ADD COLUMN use_days INTEGER NOT NULL DEFAULT 0;
UPDATE covers SET last_used = unixepoch();
CREATE TRIGGER IF NOT EXISTS cover_initial_usage AFTER INSERT ON covers
WHEN NEW.last_used = 0
BEGIN
  UPDATE covers SET last_used = unixepoch() WHERE hash = NEW.hash;
END;
