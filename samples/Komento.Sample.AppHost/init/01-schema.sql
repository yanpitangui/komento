-- Runs on first postgres startup via /docker-entrypoint-initdb.d/

SELECT 'CREATE DATABASE "komento-db"'
WHERE NOT EXISTS (
    SELECT FROM pg_database WHERE datname = 'komento-db'
)\gexec

\connect "komento-db"

CREATE TABLE IF NOT EXISTS vip_users (
    user_id TEXT PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS exposures (
    id           BIGSERIAL PRIMARY KEY,
    experiment   TEXT        NOT NULL,
    subject_id   TEXT        NOT NULL,
    subject_type TEXT        NOT NULL,
    variant      TEXT        NOT NULL,
    is_eligible  BOOLEAN     NOT NULL,
    is_outsider  BOOLEAN     NOT NULL,
    exposed_at   TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS exposures_subject_idx ON exposures (subject_id);

CREATE TABLE IF NOT EXISTS conversions (
    id          BIGSERIAL PRIMARY KEY,
    event_name  TEXT             NOT NULL,
    subject_id  TEXT             NOT NULL,
    value       DOUBLE PRECISION NULL,
    properties  JSONB            NULL,
    context     JSONB            NULL,
    recorded_at TIMESTAMPTZ      NOT NULL
);

CREATE INDEX IF NOT EXISTS conversions_subject_idx ON conversions (subject_id);
