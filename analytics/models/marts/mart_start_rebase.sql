-- What rebasing onto the base at the start of a run costs, by what the rebase found. Only runs
-- recorded since the worker measured it; older runs are left out rather than shown as 'unknown'.
with s as (
    {{ scoped(ref('fct_runs')) }}
)

select
    window_days, project_scope, track_scope, phase, start_rebase,
    count(*)                                            as runs,
    median(prep_s)                                      as median_prep_s,
    quantile_cont(prep_s, 0.9)                          as p90_prep_s,
    coalesce(sum(prep_s), 0) / 60.0                     as total_prep_minutes,
    median(base_behind)                                 as median_base_behind,
    max(base_behind)                                    as max_base_behind,
    median(minutes)                                     as median_minutes,
    count(*) filter (where entry_reason = 'conflict')   as conflict_entries
from s
where start_rebase is not null
group by all
