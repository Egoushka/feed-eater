-- Ideas the weekly shortlist filed in Plane; one row per source URL keeps a retried or later run from filing it twice.
create table weekly_ideas (
    canonical_url  text primary key,
    week           text not null,
    title          text not null,
    plane_project  text not null,
    plane_issue_id text not null,
    at             timestamptz not null default now()
);
