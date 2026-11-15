-- Each commit on a project's base that touched an area's code, with the files it touched there and
-- the Delivery tickets it belongs to (stg_board__ticket_commits, by its own sha or a commit it merged).
with code as (
    select project_key, area, code_path, {{ recon_code_regex('code_path') }} as code_regex
    from (select project_key, area, unnest(code) as code_path from {{ ref('stg_recon__areas') }})
    where trim(code_path) <> ''
),
touched as (
    select distinct c.project_key, ac.area, c.repo, c.sha, c.fp_index, c.committed_at, c.subject, c.path
    from {{ ref('stg_recon__commits') }} c
    join code ac
      on ac.project_key = c.project_key and c.path is not null and regexp_full_match(c.path, ac.code_regex)
),
tickets as (
    select m.project_key, m.repo, m.sha, list(distinct tc.ticket_id order by tc.ticket_id) as ticket_ids
    from {{ ref('stg_recon__commit_members') }} m
    join {{ ref('stg_board__ticket_commits') }} tc
      on tc.project_key = m.project_key and tc.sha = m.member_sha
    group by all
)
select t.*, coalesce(k.ticket_ids, []) as ticket_ids
from touched t
left join tickets k using (project_key, repo, sha)
