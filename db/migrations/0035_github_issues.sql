-- Ideas go to GitHub issues instead of Plane: a repo (owner/name) and an issue number. The Plane columns stay, readable, for rows
-- filed before; readers take coalesce(repo, plane_project).
alter table ideas
    add column repo text,
    add column issue_number integer,
    alter column plane_project drop not null,
    alter column plane_issue_id drop not null;

alter table weekly_ideas
    add column repo text,
    add column issue_number integer,
    alter column plane_project drop not null;

alter table profiles add column repo text;
