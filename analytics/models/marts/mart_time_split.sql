-- Where a phase's wall clock goes: getting the worktree ready (prep), the agent starting up, the
-- model thinking, tools running, handing the branch back. Totals and per-run medians.
--
-- Runs from before the worker measured its own stages have a transcript but no prep/startup/
-- hand-off figures; their non-agent time is `overhead`, unsplit. Runs with neither are `unmeasured`.
-- The parts sum to total_minutes.
with s as (
    {{ scoped(ref('fct_runs')) }}
)

select
    window_days, project_scope, track_scope, phase,
    count(*)                                                    as runs,
    count(*) filter (where split_source = 'recorded')           as runs_recorded,
    count(*) filter (where split_source = 'transcript')         as runs_transcript,
    count(*) filter (where split_source = 'none')               as runs_unmeasured,
    sum(minutes)                                                as total_minutes,
    coalesce(sum(prep_s), 0) / 60.0                             as prep_minutes,
    coalesce(sum(startup_s), 0) / 60.0                          as startup_minutes,
    coalesce(sum(model_s), 0) / 60.0                            as model_minutes,
    coalesce(sum(tool_s), 0) / 60.0                             as tool_minutes,
    coalesce(sum(agent_unsplit_s), 0) / 60.0                    as agent_unsplit_minutes,
    coalesce(sum(handoff_s), 0) / 60.0                          as handoff_minutes,
    coalesce(sum(overhead_s), 0) / 60.0                         as overhead_minutes,
    coalesce(sum(unmeasured_s), 0) / 60.0                       as unmeasured_minutes,
    median(prep_s)                                              as median_prep_s,
    median(startup_s)                                           as median_startup_s,
    median(model_s)                                             as median_model_s,
    median(tool_s)                                              as median_tool_s,
    median(handoff_s)                                           as median_handoff_s,
    median(overhead_s)                                          as median_overhead_s
from s
group by all
