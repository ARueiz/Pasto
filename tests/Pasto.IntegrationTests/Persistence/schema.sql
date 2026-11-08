-- S1-01 test schema only. The connection search_path selects an isolated schema.
CREATE TABLE room_code_reservations (
    code varchar(6) PRIMARY KEY CHECK (code ~ '^[A-Z0-9]{6}$'),
    reusable_after timestamptz NOT NULL
);

CREATE TABLE rooms (
    id uuid PRIMARY KEY,
    code varchar(6) NOT NULL UNIQUE REFERENCES room_code_reservations(code),
    creator_participant_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
    deleted_at timestamptz,
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    item_count integer NOT NULL DEFAULT 0 CHECK (item_count BETWEEN 0 AND 300),
    total_bytes bigint NOT NULL DEFAULT 0 CHECK (total_bytes >= 0)
);

CREATE TABLE room_participants (
    id uuid PRIMARY KEY,
    room_id uuid NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    credential_hash bytea NOT NULL UNIQUE CHECK (octet_length(credential_hash) = 32),
    created_at timestamptz NOT NULL,
    UNIQUE (room_id, id)
);

ALTER TABLE rooms ADD CONSTRAINT fk_rooms_creator
    FOREIGN KEY (id, creator_participant_id)
    REFERENCES room_participants(room_id, id)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE clipboard_items (
    id uuid PRIMARY KEY,
    room_id uuid NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    owner_participant_id uuid NOT NULL,
    content text NOT NULL,
    content_bytes integer NOT NULL CHECK (
        content_bytes BETWEEN 1 AND 8192 AND content_bytes = octet_length(content)
    ),
    created_at timestamptz NOT NULL,
    created_version bigint NOT NULL CHECK (created_version > 0),
    CONSTRAINT fk_items_owner FOREIGN KEY (room_id, owner_participant_id)
        REFERENCES room_participants(room_id, id)
        ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_rooms_expiry ON rooms(expires_at, id) WHERE deleted_at IS NULL;
CREATE INDEX ix_rooms_deleted ON rooms(deleted_at, id) WHERE deleted_at IS NOT NULL;
CREATE INDEX ix_items_order ON clipboard_items(room_id, created_version DESC, id DESC);
CREATE INDEX ix_items_owner ON clipboard_items(room_id, owner_participant_id);
CREATE INDEX ix_code_reuse ON room_code_reservations(reusable_after, code);
