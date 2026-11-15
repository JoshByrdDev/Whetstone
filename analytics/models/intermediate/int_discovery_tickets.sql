-- One discovery with everything measured about it as a whole: what its direction descends from,
-- which runs did its charting, scouting and sifting (and so the model, effort, skill version and
-- arm every measure is cut by), what its runs cost, how far its scout went, and the owner's time
-- on its verdicts. Proposal-level counts are added in mart_discovery_funnel.
with recursive d as (
    select * from {{ ref('stg_board__discovery_tickets') }}
),
runs as (
    select * from {{ ref('int_discovery_runs') }}
),

-- A rerun names what it reran (Ticket.ReconFrom = 'rerun:<id>', docs/recon-tab-plan.md); a
-- direction's lineage is the discovery at the root of that chain, so its record carries across
-- every rerun of it. Until Recon files reruns, every discovery is its own lineage.
edges as (
    select discovery_id as child, try_cast(regexp_extract(recon_from, '^rerun:(\d+)$', 1) as integer) as parent
    from d where recon_from like 'rerun:%'
),
anc(discovery_id, ancestor, depth) as (
    select discovery_id, discovery_id, 0 from d
    union all
    select a.discovery_id, e.parent, a.depth + 1
    from anc a join edges e on e.child = a.ancestor
    where e.parent is not null and a.depth < 50
),
chain as (
    select discovery_id, arg_max(ancestor, depth) as lineage_id from anc group by discovery_id
),

-- The run behind a phase of the discovery: the one whose lease most of that phase's rows carry,
-- else the last run in that phase.
by_lease as (
    select discovery_id, 'Scouting' as phase, lease_run_id, count(*) as n
    from {{ ref('stg_board__discovery_observations') }} where round = 0 and lease_run_id is not null group by all
    union all
    select discovery_id, 'Sifting', lease_run_id, count(*)
    from {{ ref('stg_board__discovery_proposals') }} where round = 0 and lease_run_id is not null group by all
    union all
    select discovery_id, 'Charting', lease_run_id, 1
    from {{ ref('stg_board__discovery_routes') }} where lease_run_id is not null
),
picked as (
    select r.discovery_id, r.phase, r.run_id
    from runs r
    left join by_lease b
      on b.discovery_id = r.discovery_id and b.phase = r.phase and b.lease_run_id = r.lease_run_id
    where r.phase in ('Charting', 'Scouting', 'Sifting')
    qualify row_number() over (partition by r.discovery_id, r.phase
                               order by coalesce(b.n, 0) desc, r.started_at desc, r.run_id desc) = 1
),
chart as (select p.discovery_id, r.* exclude (discovery_id) from picked p join runs r using (run_id) where p.phase = 'Charting'),
scout as (select p.discovery_id, r.* exclude (discovery_id) from picked p join runs r using (run_id) where p.phase = 'Scouting'),
sift  as (select p.discovery_id, r.* exclude (discovery_id) from picked p join runs r using (run_id) where p.phase = 'Sifting'),

-- The taste file the first sift read (null number: there was no file yet).
taste as (
    select discovery_id, taste_version_number, true as has_read
    from {{ ref('stg_board__discovery_taste_reads') }}
    where round = 0
    qualify row_number() over (partition by discovery_id order by read_at desc, taste_read_id desc) = 1
),

cost as (
    select discovery_id,
           count(*)                                         as runs,
           count(list_cost_usd)                             as runs_priced,
           sum(list_cost_usd)                               as cost_usd,
           sum(minutes)                                     as run_minutes,
           sum(tokens)                                      as run_tokens,
           any_value(experiment_arm) filter (where experiment_arm not in ('none', 'not applied')) as any_arm
    from runs group by discovery_id
),

obs as (
    select discovery_id, count(*) as observations, count(*) filter (where round = 0) as scout_observations
    from {{ ref('stg_board__discovery_observations') }} group by discovery_id
),

-- Where the scout and the rechecks went. A visit is off the route when it is in an area the route
-- did not name; a visit with no area, or a route that named none, says nothing either way.
visits as (
    select v.*, rt.areas as route_areas,
           case when v.area is null or len(coalesce(rt.areas, [])) = 0 then null
                else not list_contains(list_transform(rt.areas, x -> lower(x)), lower(v.area)) end as off_route
    from {{ ref('stg_board__discovery_visits') }} v
    left join {{ ref('stg_board__discovery_routes') }} rt using (discovery_id)
),
reach as (
    select discovery_id,
           count(*)                                                  as visits,
           count(distinct coalesce(route_pattern, url_path))         as pages,
           count(distinct lower(area))                               as areas_reached,
           count(distinct lower(signed_in_as))                       as roles_reached,
           count(distinct lower(width))                              as widths_reached,
           count(distinct lease_run_id)                              as runs_with_visits,
           sum(seconds)                                              as visit_seconds,
           count(*) filter (where off_route)                         as off_route_visits,
           count(off_route)                                          as route_judged_visits
    from visits group by discovery_id
),

verdicts as (
    select discovery_id,
           count(*)                                    as verdict_rounds,
           count(*) filter (where completed)           as verdict_rounds_completed,
           sum(owner_minutes)                          as owner_minutes
    from {{ ref('int_discovery_verdict_rounds') }} group by discovery_id
)

select
    d.discovery_id,
    d.project_key,
    d.track,
    d.phase,
    d.phase = 'Done'                                        as is_done,
    d.title,
    d.direction,
    d.created_at,
    date_trunc('week', d.created_at)::date                  as created_week,
    c.lineage_id                                            as direction_lineage_id,
    l.title                                                 as direction_lineage_title,
    ch.run_id as chart_run_id, ch.model as chart_model, ch.skill_version as chart_skill_version,
    sc.run_id as scout_run_id, sc.model as scout_model, sc.effort as scout_effort,
    sc.skill_version as scout_skill_version,
    si.run_id as sift_run_id, si.model as sift_model, si.effort as sift_effort, si.provider as sift_provider,
    si.skill_version as sift_skill_version,
    t.taste_version_number,
    case when t.has_read is null then 'not recorded'
         when t.taste_version_number is null then 'no file'
         else 'v' || t.taste_version_number end            as sift_taste_version,
    coalesce(nullif(si.experiment_arm, 'none'), k.any_arm, 'none') as experiment_arm,
    coalesce(k.runs, 0)                                     as runs,
    coalesce(k.runs_priced, 0)                              as runs_priced,
    -- Claude Code's own list price for runs whose session served only that run: a relative weight,
    -- not a bill. Complete only when every run of the discovery has one.
    k.cost_usd,
    coalesce(k.runs, 0) > 0 and k.runs = k.runs_priced     as cost_complete,
    coalesce(k.run_minutes, 0)                              as run_minutes,
    coalesce(k.run_tokens, 0)                               as run_tokens,
    coalesce(o.observations, 0)                             as observations,
    coalesce(o.scout_observations, 0)                       as scout_observations,
    coalesce(rc.visits, 0)                                  as visits,
    coalesce(rc.pages, 0)                                   as pages,
    coalesce(rc.areas_reached, 0)                           as areas_reached,
    coalesce(rc.roles_reached, 0)                           as roles_reached,
    coalesce(rc.widths_reached, 0)                          as widths_reached,
    coalesce(rc.runs_with_visits, 0)                        as runs_with_visits,
    rc.visit_seconds,
    coalesce(rc.off_route_visits, 0)                        as off_route_visits,
    coalesce(rc.route_judged_visits, 0)                     as route_judged_visits,
    coalesce(v.verdict_rounds, 0)                           as verdict_rounds,
    coalesce(v.verdict_rounds_completed, 0)                 as verdict_rounds_completed,
    coalesce(v.owner_minutes, 0)                            as owner_minutes
from d
left join chain c using (discovery_id)
left join d l on l.discovery_id = c.lineage_id
left join chart ch using (discovery_id)
left join scout sc using (discovery_id)
left join sift si using (discovery_id)
left join taste t using (discovery_id)
left join cost k using (discovery_id)
left join obs o using (discovery_id)
left join reach rc using (discovery_id)
left join verdicts v using (discovery_id)
