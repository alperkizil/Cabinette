-- Cabinette database schema, version 1.
-- Times are ISO-8601 UTC text. Booleans are 0/1.

CREATE TABLE IF NOT EXISTS schema_info (
  version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS categories (
  id             INTEGER PRIMARY KEY,
  parent_id      INTEGER REFERENCES categories(id) ON DELETE CASCADE,
  name           TEXT NOT NULL,
  folder_name    TEXT NOT NULL,              -- sanitized vault folder name, relative to the parent's folder
  kind           INTEGER NOT NULL DEFAULT 0, -- 0 user, 1 built-in, 2 system (Deleted)
  builtin_key    TEXT UNIQUE,                -- stable key of built-in categories ("audio", "code.python", "deleted")
  icon_key       TEXT,                       -- default icon to generate when icon_png is NULL
  icon_png       BLOB,
  is_pinned      INTEGER NOT NULL DEFAULT 0,
  created_at     TEXT NOT NULL,
  last_upload_at TEXT,
  last_viewed_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_categories_sibling_name
  ON categories(COALESCE(parent_id, 0), name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_categories_parent ON categories(parent_id);

CREATE TABLE IF NOT EXISTS items (
  id               INTEGER PRIMARY KEY,
  mode             INTEGER NOT NULL,          -- 0 copy (vault), 1 pointer
  kind             INTEGER NOT NULL,          -- 0 file, 1 note, 2 pasted image, 3 link
  file_name        TEXT NOT NULL,
  extension        TEXT NOT NULL DEFAULT '',  -- lower-case, no dot
  pointer_path     TEXT,                      -- pointer mode: absolute path. Copy mode: NULL (path = vault + home folder chain + file_name)
  home_category_id INTEGER NOT NULL REFERENCES categories(id),
  size_bytes       INTEGER,
  hash             BLOB,                      -- XXH128 (16 bytes); NULL until computed
  file_modified_at TEXT,
  added_at         TEXT NOT NULL,
  added_by         INTEGER NOT NULL,          -- 0 app UI, 1 explorer, 2 vault monitor, 3 AI, 4 paste, 5 downloads
  batch_id         TEXT,                      -- items added together share a batch id (album bubbles)
  original_path    TEXT,                      -- copy mode: where it was copied from
  is_missing       INTEGER NOT NULL DEFAULT 0,
  note_preview     TEXT                       -- notes: first ~300 characters
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_items_copy_location
  ON items(home_category_id, file_name COLLATE NOCASE) WHERE mode = 0;
CREATE UNIQUE INDEX IF NOT EXISTS ux_items_pointer_path
  ON items(pointer_path COLLATE NOCASE) WHERE mode = 1;
CREATE INDEX IF NOT EXISTS ix_items_hash ON items(hash);
CREATE INDEX IF NOT EXISTS ix_items_size ON items(size_bytes);
CREATE INDEX IF NOT EXISTS ix_items_added_at ON items(added_at);
CREATE INDEX IF NOT EXISTS ix_items_home ON items(home_category_id);

CREATE TABLE IF NOT EXISTS item_categories (
  item_id     INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
  category_id INTEGER NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
  added_at    TEXT NOT NULL,
  added_by    INTEGER NOT NULL,
  PRIMARY KEY (item_id, category_id)
);
-- Covering index: chat-list statistics and thread scopes read only these columns.
CREATE INDEX IF NOT EXISTS ix_item_categories_category ON item_categories(category_id, added_at, added_by, item_id);

-- Starred files (SPEC.md section 4), added 2026-10-22. A table of its own rather than a column on items, so this script adds
-- it to existing libraries as it is (no version bump: earlier builds still open the library and simply ignore it).
-- Moves, renames and category changes keep the item's id and so its star; deleting the item removes the star.
CREATE TABLE IF NOT EXISTS item_stars (
  item_id    INTEGER PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  starred_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_item_stars_starred_at ON item_stars(starred_at, item_id);

-- Date taken of photos and videos (SPEC.md section 4, "Gallery"), added 2026-10-24 like item_stars: a table of its own, added
-- to existing libraries and restored backups when they open, without a version bump (earlier builds ignore it). Read from the
-- file in the background (LibraryService.DatesTaken.cs). A row means the file was read: taken_at NULL = it has no date taken.
-- file_size and file_modified_at are the file's size and last-write time (UTC) when it was read; when the file shows others
-- later, it changed and is read again. Deleting the item removes its row.
CREATE TABLE IF NOT EXISTS item_dates (
  item_id          INTEGER PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  taken_at         TEXT,
  file_size        INTEGER NOT NULL,
  file_modified_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tags (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS item_tags (
  item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
  tag_id  INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
  PRIMARY KEY (item_id, tag_id)
);
CREATE INDEX IF NOT EXISTS ix_item_tags_tag ON item_tags(tag_id);

-- Substring search over file names and note text. rowid = items.id.
CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(file_name, note_text, tokenize = 'trigram');

-- Keep search_fts.file_name in sync with items. note_text is written by the library for notes and links.
CREATE TRIGGER IF NOT EXISTS trg_items_fts_insert AFTER INSERT ON items BEGIN
  INSERT INTO search_fts(rowid, file_name, note_text) VALUES (new.id, new.file_name, NULL);
END;
CREATE TRIGGER IF NOT EXISTS trg_items_fts_rename AFTER UPDATE OF file_name ON items BEGIN
  UPDATE search_fts SET file_name = new.file_name WHERE rowid = new.id;
END;
CREATE TRIGGER IF NOT EXISTS trg_items_fts_delete AFTER DELETE ON items BEGIN
  DELETE FROM search_fts WHERE rowid = old.id;
END;

CREATE TABLE IF NOT EXISTS settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- The AI activity log (SPEC.md section 10): one row per tool call, kept for a year. Read newest first by id.
-- AUTOINCREMENT: an id is never handed out twice, even after the prune has emptied the table (ids grow with time). A table made
-- before it is rebuilt with it when the library opens (Database.RebuildActivityLogWithAutoincrement; Database.cs repeats this
-- definition).
CREATE TABLE IF NOT EXISTS activity_log (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  at           TEXT NOT NULL,
  source       TEXT NOT NULL,
  action       TEXT NOT NULL,
  details_json TEXT
);
-- Added 2026-10-24 for stage 4, without a version bump (like item_stars): earlier builds still open the library and its backups.
-- at: the retention prune (entries older than a year). (action, id) and (source, id): the tool and client filters, newest first.
CREATE INDEX IF NOT EXISTS ix_activity_log_at ON activity_log(at);
CREATE INDEX IF NOT EXISTS ix_activity_log_action ON activity_log(action, id);
CREATE INDEX IF NOT EXISTS ix_activity_log_source ON activity_log(source, id);
