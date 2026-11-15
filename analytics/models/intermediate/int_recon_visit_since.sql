-- What changed in a visit's area's code since that visit, on the repo its commit is in: commits
-- after it on the first-parent line (a commit merged in by the first-parent commit at fp_index
-- counts that merge as after), the files they touched there and the tickets they belong to. Only
-- for placed visits in areas with code paths; a visit off the base has no row and reads 'unknown'.
-- On a project of several repos, the other repos' commits cannot be placed against this one's
-- line and are not counted.
with v as (
    select * from {{ ref('int_recon_visit_area') }}
    where placement in ('line', 'merged') and fp_index is not null
),
after as (
    select v.visit_id, c.sha, c.path, c.ticket_ids
    from v
    join {{ ref('int_recon_area_commits') }} c
      on c.project_key = v.project_key and c.area = v.area and c.repo = v.commit_repo
     and (c.fp_index < v.fp_index or (v.placement = 'merged' and c.fp_index = v.fp_index))
)
select
    v.visit_id,
    count(distinct a.sha)                                               as commits_since,
    count(distinct a.path)                                              as files_since,
    coalesce(list_sort(list_distinct(flatten(list(a.ticket_ids) filter (where a.sha is not null)))), [])  as tickets_since
from v
left join after a using (visit_id)
group by v.visit_id
