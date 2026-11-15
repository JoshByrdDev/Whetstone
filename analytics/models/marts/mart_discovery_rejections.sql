-- Which thing is wrong, when the owner says no: rejections by reason, per scope and cut. Each reason
-- points at its own fix - generic: the prompts and the taste file; already decided: what Sift is
-- given to check against; wrong: Sift's checking; not now: nothing (timing, not quality).
--
-- One row per reason that occurred, with that reason's share of the cut's rejections and of the
-- cut's closed verdicts; `enough_verdicts` as in mart_discovery_funnel. Cut like the funnel's
-- proposal rows (macros/discovery.sql), plus `kind` (ticket, bug, slice, decide) and `source`
-- (sift or recheck).
with p as (
    select f.*, d.created_at as cohort_at, d.created_week as cohort_week,
           d.scout_model, d.scout_skill_version, d.chart_skill_version
    from {{ ref('fct_discovery_proposals') }} f
    join {{ ref('int_discovery_tickets') }} d using (discovery_id)
    where f.shown and f.verdict_closed
),
s as ({{ scoped('p', 'cohort_at') }}),
c as (
    {{ discovery_cut('s', 'proposal') }}
    {%- if discovery_dim_on('kind') %}
    union all
    select *, 'kind', lower(kind), null from s
    {%- endif %}
    {%- if discovery_dim_on('source') %}
    union all
    select *, 'source', source, null from s
    {%- endif %}
),
totals as (
    select window_days, project_scope, track_scope, dimension, dim_value,
           count(*) as closed, count(*) filter (where verdict = 'rejected') as rejected
    from c group by all
)

select
    c.window_days, c.project_scope, c.track_scope, c.dimension, c.dim_value,
    any_value(c.dim_label)                                         as dim_label,
    c.reject_reason                                                as reason,
    count(*)                                                       as rejections,
    any_value(t.rejected)                                          as rejected,
    any_value(t.closed)                                            as closed,
    any_value(t.closed) >= {{ var('discovery_min_verdicts') }}     as enough_verdicts,
    count(*) / any_value(t.rejected)::double                       as share_of_rejections,
    count(*) / any_value(t.closed)::double                         as share_of_closed,
    -- Generic and wrong teach the taste file; already decided means the register is missing a
    -- decision; not now is a snooze in all but name.
    case c.reject_reason when 'Generic' then 'taste' when 'Wrong' then 'taste'
         when 'AlreadyDecided' then 'register' when 'NotNow' then 'timing' else 'unknown' end as teaches
from c
join totals t using (window_days, project_scope, track_scope, dimension, dim_value)
where c.verdict = 'rejected'
group by c.window_days, c.project_scope, c.track_scope, c.dimension, c.dim_value, c.reject_reason
