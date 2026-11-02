-- What a phase's agents wait on, tool by tool - and for a shell call, command by command.
--
-- Seconds here are each call's own duration, summed: calls made in parallel each count in full,
-- so a tool's total can add up past the agent's tool time (fct_runs.tool_s, which is their union).
-- Only runs whose transcript was found contribute.
with calls as (
    select c.*, r.phase, r.project_key, r.track, r.started_at
    from {{ ref('fct_tool_calls') }} c
    join {{ ref('fct_runs') }} r using (run_id)
    where not c.is_sidechain
),
s as (
    {{ scoped('calls') }}
),
agg as (
    select
        window_days, project_scope, track_scope, phase,
        tool_family, tool_name, coalesce(tool_detail, '') as tool_detail,
        any_value(tool_short)                       as tool_short,
        count(*)                                    as calls,
        count(distinct run_id)                      as runs,
        coalesce(sum(seconds), 0)                   as total_seconds,
        median(seconds)                             as median_seconds,
        quantile_cont(seconds, 0.9)                 as p90_seconds,
        max(seconds)                                as max_seconds,
        count(*) filter (where is_error)            as error_calls,
        count(*) filter (where seconds is null)     as unanswered_calls,
        sum(result_chars)                           as result_chars
    from s
    group by all
)

select
    *,
    total_seconds / nullif(sum(total_seconds) over (partition by window_days, project_scope, track_scope, phase), 0) as share_of_tool_seconds
from agg
