-- Runs on first postgres startup via /docker-entrypoint-initdb.d/

SELECT 'CREATE DATABASE "komento-db"'
WHERE NOT EXISTS (
    SELECT FROM pg_database WHERE datname = 'komento-db'
)\gexec

\connect "komento-db"

CREATE TABLE IF NOT EXISTS vip_users (
    user_id TEXT PRIMARY KEY
);
