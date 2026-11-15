-- Recon's area drill-down (docs/recon-tab-plan.md, "Coverage"): every discovery that went to an
-- area - a visit there, or a proposal shown there - with its direction, how long it spent there and
-- what came of it there. All of history, per project, like coverage. The yield counts are only the
-- proposals in this area, so a discovery that ranged over several areas is credited in each with
-- what it found in that one.
with projects as (
    select project_key from {{ ref('stg_recon__projects') }}
),
d as (
    select * from {{ ref('int_discovery_tickets') }}
),
p as (
    select * from {{ ref('fct_discovery_proposals') }}
),
v as (
    select project_key, area, discovery_id,
           count(*)                 as visits,
           sum(seconds)::bigint     as seconds,
           max(visited_at)          as last_visited_at
    from {{ ref('int_recon_visit_area') }} group by all
),
went as (
    select project_key, area, discovery_id from v
    union
    select d.project_key, p.area, p.discovery_id from p join d using (discovery_id) where p.shown and p.area is not null
),
y as (
    select area, discovery_id, {{ recon_yield_counts() }}
    from p where area is not null group by all
)
select
    w.project_key,
    w.area,
    w.discovery_id,
    d.title,
    d.direction,
    d.phase,
    d.is_done,
    d.created_at,
    d.direction_lineage_id,
    coalesce(v.visits, 0)       as visits,
    coalesce(v.seconds, 0)      as seconds,
    v.last_visited_at,
    coalesce(y.shown, 0)        as shown,
    coalesce(y.approved, 0)     as approved,
    coalesce(y.rejected, 0)     as rejected,
    coalesce(y.new_to_me, 0)    as new_to_me,
    coalesce(y.filed, 0)        as filed
from went w
join projects using (project_key)
join d using (discovery_id)
left join v using (project_key, area, discovery_id)
left join y using (area, discovery_id)
