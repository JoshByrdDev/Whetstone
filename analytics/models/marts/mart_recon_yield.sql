-- Where scouting has paid (docs/recon-tab-plan.md, "Yield"), per window, for one project at a time,
-- cut three ways: by `area` (the discoveries that went there - a visit or a shown proposal - and the
-- proposals in it) and by `direction` lineage (a direction and every rerun of it, ReconFrom =
-- 'rerun:<id>', counted together so a direction's record carries across its reruns), and by
-- `source`: where each discovery's direction came from - the owner's own words, an agent's draft
-- taken as it was or edited, or a rerun (Ticket.DirectionSource). That last cut is the experiment
-- drafting is (docs/recon-tab-plan.md, "Drafted directions"): whether a drafted direction pays.
--
-- A window is a cohort, as in the Discovery marts: a row counts by when its discovery was filed.
-- Cost is the discoveries' runs (fct_runs' list price, via int_discovery_tickets), so an area's cost
-- is that of the discoveries that went there and areas do not add up to the project; it is shown
-- only where every one of those discoveries is priced (`cost_complete`). Only the project's own
-- rows and the Discovery track are kept: Recon never shows all projects.
with d as (
    select * from {{ ref('int_discovery_tickets') }}
),
p as (
    select * from {{ ref('fct_discovery_proposals') }}
),
went as (
    select area, discovery_id from {{ ref('int_recon_visit_area') }}
    union
    select area, discovery_id from p where shown and area is not null
),
rows as (
    select d.project_key, d.track, 'area' as cut, w.area as cut_key, w.area as label,
           d.discovery_id, d.created_at as cohort_at, d.cost_usd, d.cost_complete, d.direction
    from went w join d using (discovery_id)
    union all
    select d.project_key, d.track, 'direction', cast(d.direction_lineage_id as varchar), d.direction_lineage_title,
           d.discovery_id, d.created_at, d.cost_usd, d.cost_complete, d.direction
    from d
    union all
    select d.project_key, d.track, 'source', d.direction_source, d.direction_source,
           d.discovery_id, d.created_at, d.cost_usd, d.cost_complete, d.direction
    from d
),
s as ({{ scoped('rows', 'cohort_at') }}),
-- Each discovery's proposals, counted once per cut it is in.
pc as (
    select 'area' as cut, area as cut_key, discovery_id, {{ recon_yield_counts() }}
    from p where area is not null group by all
    union all
    select 'direction', cast(d.direction_lineage_id as varchar), p.discovery_id, {{ recon_yield_counts() }}
    from p join d using (discovery_id) group by all
    union all
    select 'source', d.direction_source, p.discovery_id, {{ recon_yield_counts() }}
    from p join d using (discovery_id) group by all
)
select
    s.window_days,
    s.project_key,
    s.cut,
    s.cut_key,
    any_value(s.label)                                          as label,
    count(distinct s.discovery_id)                              as discoveries,
    coalesce(sum(pc.shown), 0)::bigint                          as shown,
    coalesce(sum(pc.approved), 0)::bigint                       as approved,
    coalesce(sum(pc.rejected), 0)::bigint                       as rejected,
    coalesce(sum(pc.rejected_generic), 0)::bigint               as rejected_generic,
    coalesce(sum(pc.rejected_already_decided), 0)::bigint       as rejected_already_decided,
    coalesce(sum(pc.rejected_not_now), 0)::bigint               as rejected_not_now,
    coalesce(sum(pc.rejected_wrong), 0)::bigint                 as rejected_wrong,
    coalesce(sum(pc.snoozed), 0)::bigint                        as snoozed,
    coalesce(sum(pc.new_to_me), 0)::bigint                      as new_to_me,
    coalesce(sum(pc.filed), 0)::bigint                          as filed,
    case when bool_and(s.cost_complete) then sum(s.cost_usd) end as cost_usd,
    bool_and(s.cost_complete)                                   as cost_complete,
    arg_max(s.discovery_id, s.cohort_at)                        as latest_discovery_id,
    arg_max(s.direction, s.cohort_at)                           as latest_direction,
    max(s.cohort_at)                                            as latest_at
from s
left join pc on pc.cut = s.cut and pc.cut_key = s.cut_key and pc.discovery_id = s.discovery_id
where s.project_scope = s.project_key and s.track_scope = 'Discovery'
group by s.window_days, s.project_key, s.cut, s.cut_key
