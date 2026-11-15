-- Recon's area drill-down (docs/recon-tab-plan.md): within an area, each route pattern visited, by
-- role and width - how many visits, how long, how many discoveries, and when last. The pattern is
-- the area's page pattern the URL matched, else the scout's own, else the path itself.
select
    v.project_key,
    v.area,
    v.route_pattern,
    v.role,
    v.width,
    count(*)                        as visits,
    sum(v.seconds)::bigint          as seconds,
    count(distinct v.discovery_id)  as discoveries,
    max(v.visited_at)               as last_visited_at
from {{ ref('int_recon_visit_area') }} v
join {{ ref('stg_recon__projects') }} using (project_key)
group by all
