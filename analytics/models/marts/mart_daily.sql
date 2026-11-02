-- Each day's runs per phase: the trend line under every other mart.
with s as (
    {{ scoped(ref('fct_runs')) }}
)

select
    window_days, project_scope, track_scope, phase,
    cast(started_at as date)                                         as day,
    count(*)                                                         as runs,
    sum(minutes)                                                     as minutes,
    median(minutes)                                                  as median_minutes,
    sum(tokens)                                                      as tokens,
    coalesce(sum(minutes) filter (where rework_class = 'rework'), 0) as rework_minutes,
    count(*) filter (where outcome <> 'Completed')                   as unfinished
from s
group by all
