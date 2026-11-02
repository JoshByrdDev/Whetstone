-- How much of each phase's time is doing work again, and why: QA failing it, refinement sending it
-- back, a merge conflict, a reviewer's changes - beside the first-time work and continuations.
with s as (
    {{ scoped(ref('fct_runs')) }}
),
cut as (
    select window_days, project_scope, track_scope, phase, entry_reason, rework_class,
           count(*) as runs,
           count(distinct ticket_id) as tickets,
           sum(minutes) as minutes,
           sum(tokens) as tokens,
           median(minutes) as median_minutes,
           avg(pass) as mean_pass
    from s
    group by all
)

select
    *,
    runs    / sum(runs)    over (partition by window_days, project_scope, track_scope, phase) as share_of_runs,
    minutes / nullif(sum(minutes) over (partition by window_days, project_scope, track_scope, phase), 0) as share_of_minutes
from cut
