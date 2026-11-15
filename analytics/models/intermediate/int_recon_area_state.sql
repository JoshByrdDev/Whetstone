-- One of a project's named areas, as the suggestions read it: when and on which commit it was last
-- scouted (any role, any width), what has changed in its code since, its latest code commit (the
-- head a Not now is pinned to), whether its code changed in the last 30 days, and whether scouting
-- it has gone cold - its last three judged discoveries found nothing the owner approved there.
--
-- A discovery is judged once its verdict is in: Done, or with any closed verdict. Counting one
-- still being decided would mark an area cooling before the owner has looked.
with areas as (
    select a.* from {{ ref('stg_recon__areas') }} a
    join {{ ref('stg_recon__projects') }} p using (project_key)
),
visits as (
    select * from {{ ref('int_recon_visit_area') }}
),
last_visit as (
    select project_key, area, visit_id, visited_at, discovery_id, coalesce(resolved_sha, commit_sha) as commit_sha, placement
    from visits
    qualify row_number() over (partition by project_key, area order by visited_at desc nulls last, visit_id desc) = 1
),
commits as (
    select * from {{ ref('int_recon_area_commits') }}
),
head as (
    select project_key, area, sha as head_commit, committed_at as head_at
    from commits
    qualify row_number() over (partition by project_key, area order by committed_at desc, fp_index, repo) = 1
),
recent as (
    select project_key, area,
           count(distinct sha) filter (where committed_at >= {{ as_of() }} - interval 30 day)     as commits_30d,
           count(distinct sha) filter (where committed_at >= {{ as_of() }} - interval 90 day)     as commits_90d,
           count(distinct path) filter (where committed_at >= {{ as_of() }} - interval 90 day)    as files_90d,
           coalesce(list_sort(list_distinct(flatten(list(ticket_ids) filter (where committed_at >= {{ as_of() }} - interval 90 day)))), []) as tickets_90d
    from commits group by all
),
props as (
    select * from {{ ref('fct_discovery_proposals') }}
),
judged as (
    select discovery_id from {{ ref('int_discovery_tickets') }} where is_done
    union
    select discovery_id from props where verdict_closed
),
went as (
    select t.project_key, x.area, x.discovery_id
    from (select area, discovery_id from visits
          union
          select area, discovery_id from props where shown and area is not null) x
    join {{ ref('int_discovery_tickets') }} t using (discovery_id)
),
ranked as (
    select w.project_key, w.area, w.discovery_id,
           count(p.proposal_id) filter (where p.approved) as approved,
           row_number() over (partition by w.project_key, w.area order by any_value(t.created_at) desc, w.discovery_id desc) as rn
    from went w
    join judged j using (discovery_id)
    join {{ ref('int_discovery_tickets') }} t using (discovery_id)
    left join props p on p.discovery_id = w.discovery_id and p.area = w.area and p.shown
    group by w.project_key, w.area, w.discovery_id
),
cool as (
    select project_key, area,
           count(*) filter (where rn <= 3)                         as last3,
           coalesce(sum(approved) filter (where rn <= 3), 0)        as last3_approved
    from ranked group by all
),
went_count as (
    select project_key, area, count(distinct discovery_id) as discoveries from went group by all
)
select
    a.project_key,
    a.area,
    a.area_ordinal,
    a.has_code,
    lv.visited_at                                       as last_visited_at,
    lv.visit_id                                         as last_visit_id,
    lv.discovery_id                                     as last_discovery_id,
    lv.commit_sha                                       as last_commit,
    lv.placement                                        as last_placement,
    coalesce(s.commits_since, 0)                        as commits_since,
    coalesce(s.files_since, 0)                          as files_since,
    coalesce(s.tickets_since, [])                       as tickets_since,
    case when lv.visit_id is null then 'never'
         when not a.has_code then 'fresh'
         when lv.placement not in ('line', 'merged') or s.visit_id is null then 'unknown'
         when s.commits_since > 0 then 'changed'
         else 'fresh' end                               as state,
    coalesce(r.commits_30d, 0) > 0                      as changed_recently,
    coalesce(r.commits_90d, 0)                          as commits_90d,
    coalesce(r.files_90d, 0)                            as files_90d,
    coalesce(r.tickets_90d, [])                         as tickets_90d,
    h.head_commit                                       as area_head_commit,
    h.head_at                                           as area_head_at,
    coalesce(wc.discoveries, 0)                         as discoveries,
    coalesce(c.last3, 0) = 3 and c.last3_approved = 0   as cooling
from areas a
left join last_visit lv using (project_key, area)
left join {{ ref('int_recon_visit_since') }} s on s.visit_id = lv.visit_id
left join head h using (project_key, area)
left join recent r using (project_key, area)
left join cool c using (project_key, area)
left join went_count wc using (project_key, area)
