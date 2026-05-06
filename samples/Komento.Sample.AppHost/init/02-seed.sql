\connect "komento-db"

INSERT INTO vip_users (user_id) VALUES
    ('user-1'), ('user-2'), ('user-3'), ('user-4'), ('user-5'),
    ('user-6'), ('user-7'), ('user-8'), ('user-9'), ('user-10')
ON CONFLICT DO NOTHING;
