-- Recon's grid (docs/recon-tab-plan.md, "Coverage"): one row per project x area x role x width.
-- Every cell of the project's areas x roles (or 'any role' when it lists none) x phone and desk is
-- here, scouted or not - an empty one is a gap - plus the cells visits fell in outside that:
-- '(unmapped)' for a URL no area matches, 'other' for a role the list does not have, 'unknown' for a
-- width that is not a number. Those are there so the config can be fixed, not as coverage
-- (`in_config` false).
--
-- `state`: gap (never scouted), fresh, changed (its area's code changed since this cell was last
-- scouted), unknown (scouted on a commit off the base). An area with no code paths is never stale:
-- fresh, with `has_code` false. Coverage is all of history, never cut by window, and carries its
-- project: Recon is one project at a time and coverage does not add up across projects.
with projects as (
    select * from {{ ref('stg_recon__projects') }}
),
areas as (
    select a.* from {{ ref('stg_recon__areas') }} a join projects using (project_key)
),
roles as (
    select project_key, role, role_ordinal from {{ ref('stg_recon__roles') }}
    where project_key in (select project_key from projects)
    union all
    select project_key, 'any role', 0 from projects where role_count = 0
),
widths as (
    select * from (values ('phone', 0), ('desk', 1)) w(width, width_ordinal)
),
visits as (
    select v.* from {{ ref('int_recon_visit_area') }} v join projects using (project_key)
),
config_cells as (
    select a.project_key, a.area, a.area_ordinal, r.role, r.role_ordinal, w.width, true as in_config
    from areas a join roles r using (project_key) cross join widths w
),
seen_cells as (
    select distinct project_key, area, area_ordinal, role, role_ordinal, width from visits
),
cells as (
    select * from config_cells
    union all
    select s.*, false from seen_cells s
    where not exists (select 1 from config_cells c
                      where c.project_key = s.project_key and c.area = s.area and c.role = s.role and c.width = s.width)
),
stats as (
    select project_key, area, role, width,
           count(*)                         as visits,
           sum(seconds)::bigint             as seconds,
           count(distinct discovery_id)     as discoveries
    from visits group by all
),
last_visit as (
    select project_key, area, role, width, visit_id, visited_at, discovery_id,
           coalesce(resolved_sha, commit_sha) as commit_sha, placement
    from visits
    qualify row_number() over (partition by project_key, area, role, width order by visited_at desc nulls last, visit_id desc) = 1
),
-- A proposal counts in each cell one of its observations was in.
cell_props as (
    select distinct o.project_key, o.area, o.role, o.width, p.proposal_id, p.shown, p.approved
    from {{ ref('fct_discovery_proposals') }} p
    join {{ ref('stg_board__discovery_proposals') }} sp using (proposal_id)
    join {{ ref('int_recon_observation_area') }} o
      on o.discovery_id = p.discovery_id and list_contains(sp.observation_ids, o.observation_id)
),
cell_yield as (
    select project_key, area, role, width,
           count(*) filter (where shown)      as shown,
           count(*) filter (where approved)   as approved
    from cell_props group by all
)
select
    c.project_key,
    c.area,
    c.area_ordinal,
    c.role,
    c.role_ordinal,
    c.width,
    {{ recon_width_ordinal('c.width') }}                    as width_ordinal,
    c.in_config,
    coalesce(st.has_code, false)                            as has_code,
    coalesce(s.visits, 0)                                   as visits,
    coalesce(s.seconds, 0)                                  as seconds,
    coalesce(s.discoveries, 0)                              as discoveries,
    lv.visited_at                                           as last_visited_at,
    lv.discovery_id                                         as last_discovery_id,
    lv.commit_sha                                           as last_commit,
    lv.placement                                            as last_placement,
    case when lv.visit_id is null then 'gap'
         when not coalesce(st.has_code, false) then 'fresh'
         when lv.placement not in ('line', 'merged') or vs.visit_id is null then 'unknown'
         when vs.commits_since > 0 then 'changed'
         else 'fresh' end                                   as state,
    coalesce(vs.commits_since, 0)                           as commits_since,
    coalesce(vs.files_since, 0)                             as files_since,
    to_json(coalesce(vs.tickets_since, []))::varchar        as tickets_since,
    coalesce(y.shown, 0)                                    as shown,
    coalesce(y.approved, 0)                                 as approved,
    st.area_head_commit,
    coalesce(st.cooling, false)                             as cooling
from cells c
left join stats s using (project_key, area, role, width)
left join last_visit lv using (project_key, area, role, width)
left join {{ ref('int_recon_visit_since') }} vs on vs.visit_id = lv.visit_id
left join cell_yield y using (project_key, area, role, width)
left join {{ ref('int_recon_area_state') }} st using (project_key, area)
