-- Recon's area drill-down: the commits on the project's base that touched an area's code since it
-- was last scouted, one row per commit, newest first by `fp_index` (0 is the tip), with the files it
-- touched there and the tickets it belongs to - the "what changed" behind a changed cell.
--
-- `since` says why the commit is listed: `after` the area's last visit on the first-parent line (the
-- same rule as int_recon_visit_since, so these are the commits the coverage counts); `recent`, in an
-- area never scouted, from the last 90 days (what a never-scouted gap is ranked by); `by_date`, when
-- the last visit is on a commit off the base, so ancestry cannot place it and the clock is the best
-- guess - shown as such, never as the count. Commits before the last visit are not here.
with st as (
    select project_key, area, last_visit_id, last_visited_at from {{ ref('int_recon_area_state') }}
),
lv as (
    select visit_id, commit_repo, fp_index, placement from {{ ref('int_recon_visit_area') }}
),
c as (
    select project_key, area, repo, sha,
           any_value(fp_index)              as fp_index,
           any_value(committed_at)          as committed_at,
           any_value(subject)               as subject,
           count(distinct path)             as files,
           to_json(any_value(ticket_ids))::varchar as tickets
    from {{ ref('int_recon_area_commits') }} group by project_key, area, repo, sha
),
placed as (
    select c.*,
           case when st.last_visit_id is null then
                    case when c.committed_at >= {{ as_of() }} - interval 90 day then 'recent' end
                when lv.fp_index is null or lv.placement not in ('line', 'merged') then
                    case when c.committed_at > st.last_visited_at then 'by_date' end
                when c.repo = lv.commit_repo
                     and (c.fp_index < lv.fp_index or (lv.placement = 'merged' and c.fp_index = lv.fp_index)) then 'after'
           end as since
    from c
    join st using (project_key, area)
    left join lv on lv.visit_id = st.last_visit_id
)
select project_key, area, repo, sha, fp_index, committed_at, subject, files, tickets, since
from placed
where since is not null
