-- v1.0 API tokens (docs/specs/2026-10-19-v1.0-app.md): only a hash is stored; the token is shown once.
create table api_tokens (
    id           bigserial primary key,
    name         text not null,
    hash         bytea not null unique,      -- SHA-256 of the token
    scopes       text[] not null,
    created_at   timestamptz not null default now(),
    last_used_at timestamptz,
    revoked_at   timestamptz
);
