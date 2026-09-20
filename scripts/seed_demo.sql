CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '3');
CREATE TABLE IF NOT EXISTS entries (
  pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL,
  first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
  src_size INTEGER, src_mtime INTEGER, copied_size INTEGER, copied_mtime INTEGER,
  via_link INTEGER NOT NULL DEFAULT 0, status_changed INTEGER,
  remote_id TEXT, remote_version TEXT,
  PRIMARY KEY (pair, path)) STRICT, WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS pending (
  id INTEGER PRIMARY KEY,
  pair TEXT NOT NULL,
  path TEXT NOT NULL,
  from_path TEXT,
  change INTEGER NOT NULL,
  kind INTEGER NOT NULL,
  src_size INTEGER, src_mtime INTEGER,
  dst_size INTEGER, dst_mtime INTEGER,
  detected INTEGER NOT NULL,
  status INTEGER NOT NULL,
  resolved INTEGER,
  version_path TEXT
) STRICT;
CREATE INDEX IF NOT EXISTS pending_open ON pending (pair, status, detected);
CREATE UNIQUE INDEX IF NOT EXISTS pending_one_per_path ON pending (pair, path) WHERE status = 0;

DELETE FROM pending WHERE pair = 'Тестове завдання (Погодження)';

-- 1. Added
INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path)
VALUES ('Тестове завдання (Погодження)', 'Нові матеріали\Договір_поставки.docx', NULL, 1, 0, NULL, NULL, 24576, 1789768000000, 1789768000000, 0, NULL, NULL);

-- 2. Modified
INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path)
VALUES ('Тестове завдання (Погодження)', 'Документи\Звіт_2026.docx', NULL, 2, 0, 10240, 1789600000000, 15360, 1789769000000, 1789769000000, 0, NULL, NULL);

-- 3. Deleted
INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path)
VALUES ('Тестове завдання (Погодження)', 'Проєкт\Архів_нотаток.txt', NULL, 3, 0, 2048, 1789500000000, NULL, NULL, 1789766000000, 0, NULL, NULL);

-- 4. Renamed
INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path)
VALUES ('Тестове завдання (Погодження)', 'Креслення\Схема_v2.pdf', 'Креслення\Схема_v1.pdf', 4, 0, 40960, 1789700000000, 40960, 1789770000000, 1789770000000, 0, NULL, NULL);
