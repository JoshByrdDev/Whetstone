-- How often QA's weaker calls hold up. A QA run can pass an item three ways: drive it, argue it from
-- the code (by inspection), or - after a rework - carry an earlier pass over because the rework's
-- diff cannot reach it. Refinement audits the last two and expires the ones that do not hold; the
-- item is then driven again, and what that re-drive finds is the verdict on the call. A Failed
-- re-drive is the number that matters most: a carry-over that was hiding a real defect.
--
-- Every call is credited to the QA run that made it, so the cut is by that run's model, effort,
-- provider, skill and prompt version and experiment arm - what to tune when a rate is high. Only
-- QA runs that logged a ruling count: runs from before the ruling log existed say nothing either way.
with events as (
    select * from {{ ref('stg_board__qa_ruling_events') }}
),
runs as (
    select * from {{ ref('fct_runs') }}
),

-- The run behind each worker ruling: by the ticket lease it recorded, or, for a run that did not
-- report its lease, the run on that ticket in that phase whose span holds the ruling.
ev as (
    select e.*, coalesce(by_lease.run_id, by_time.run_id) as run_id
    from events e
    left join runs by_lease
      on by_lease.lease_run_id = e.lease_run_id and by_lease.ticket_id = e.ticket_id
    left join runs by_time
      on by_lease.run_id is null and by_time.ticket_id = e.ticket_id and by_time.phase = e.phase
     and e.ruled_at >= by_time.started_at and e.ruled_at <= by_time.ended_at
    where e.actor = 'Worker'
    qualify row_number() over (partition by e.event_id order by by_lease.run_id nulls last, by_time.started_at desc) = 1
),

-- The first status recorded on an item after each expiry: what the re-drive found.
redrives as (
    select x.event_id as expiry_id, n.kind as redrive_kind
    from events x
    join events n
      on n.qa_item_id = x.qa_item_id and n.event_id > x.event_id
     and n.kind in ('Driven', 'ByInspection', 'Failed', 'Blocked')
    where x.kind = 'Expired'
    qualify row_number() over (partition by x.event_id order by n.event_id) = 1
),

-- Each pass QA made, with whether refinement overturned it and what the item's next status was.
passes as (
    select
        p.event_id, p.run_id, p.kind,
        x.event_id is not null as overturned,
        d.redrive_kind
    from ev p
    left join events x on x.kind = 'Expired' and x.overturns_event_id = p.event_id
    left join redrives d on d.expiry_id = x.event_id
    where p.kind in ('Driven', 'ByInspection', 'CarriedOver') and p.phase = 'QaTesting' and p.run_id is not null
),

per_run as (
    select
        run_id,
        count(*)                                                        as passes,
        count(*) filter (where kind = 'Driven')                         as passes_driven,
        count(*) filter (where kind = 'ByInspection')                   as by_inspection,
        count(*) filter (where kind = 'CarriedOver')                    as carry_overs,
        count(*) filter (where kind = 'CarriedOver' and overturned)     as carry_overturned,
        count(*) filter (where kind = 'ByInspection' and overturned)    as inspection_overturned,
        count(*) filter (where kind = 'Driven' and overturned)          as driven_overturned,
        count(*) filter (where kind <> 'Driven' and overturned)         as overturned,
        count(*) filter (where kind <> 'Driven' and overturned and redrive_kind is not null) as overturned_redriven,
        count(*) filter (where kind <> 'Driven' and overturned and redrive_kind = 'Failed')  as overturned_failed
    from passes
    group by run_id
),

-- QA runs that logged a ruling - any status counts - with what their passes came to.
qa_runs as (
    select
        r.run_id, r.project_key, r.track, r.started_at,
        r.model, r.effort, r.provider, r.skill_version, r.prompt_version, r.experiment_arm,
        coalesce(r.pass, 1) > 1 as is_rework,
        coalesce(p.passes, 0) as passes, coalesce(p.passes_driven, 0) as passes_driven,
        coalesce(p.by_inspection, 0) as by_inspection, coalesce(p.carry_overs, 0) as carry_overs,
        coalesce(p.carry_overturned, 0) as carry_overturned,
        coalesce(p.inspection_overturned, 0) as inspection_overturned,
        coalesce(p.driven_overturned, 0) as driven_overturned,
        coalesce(p.overturned, 0) as overturned,
        coalesce(p.overturned_redriven, 0) as overturned_redriven,
        coalesce(p.overturned_failed, 0) as overturned_failed
    from runs r
    left join per_run p using (run_id)
    where r.phase = 'QaTesting' and r.run_id in (select run_id from ev where run_id is not null)
),

s as (
    {{ scoped('qa_runs') }}
),
cut as (
    {%- set dims = {
        'model': "coalesce(model, 'unknown')",
        'effort': "coalesce(effort, 'unknown')",
        'provider': "coalesce(provider, 'unknown')",
        'skill_version': "coalesce(skill_version, 'unknown')",
        'prompt_version': "coalesce(prompt_version, 'unknown')",
        'experiment_arm': "coalesce(experiment_arm, 'none')",
        'all': "'all'",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, dimension, dim_value,
    count(*)                                              as qa_runs,
    count(*) filter (where is_rework)                     as rework_runs,
    count(*) filter (where is_rework and overturned > 0)  as rework_runs_overturned,
    count(*) filter (where is_rework and overturned > 0)
        / nullif(count(*) filter (where is_rework), 0)    as rework_overturn_share,
    sum(passes)                                           as passes,
    sum(passes_driven)                                    as passes_driven,
    sum(by_inspection)                                    as by_inspection,
    sum(carry_overs)                                      as carry_overs,
    sum(carry_overs) / nullif(sum(passes), 0)             as carry_over_rate,
    sum(by_inspection) / nullif(sum(passes), 0)           as inspection_rate,
    sum(carry_overturned)                                 as carry_overturned,
    sum(carry_overturned) / nullif(sum(carry_overs), 0)   as carry_overturn_rate,
    sum(inspection_overturned)                            as inspection_overturned,
    sum(inspection_overturned) / nullif(sum(by_inspection), 0) as inspection_overturn_rate,
    sum(driven_overturned)                                as driven_overturned,
    sum(overturned)                                       as overturned,
    sum(overturned) / nullif(sum(carry_overs) + sum(by_inspection), 0) as overturn_rate,
    sum(overturned_redriven)                              as overturned_redriven,
    sum(overturned_failed)                                as overturned_failed,
    -- Of the overturned calls QA has since driven again, the share that failed: real defects the
    -- weaker call was hiding.
    sum(overturned_failed) / nullif(sum(overturned_redriven), 0) as overturned_failed_rate
from cut
group by all
