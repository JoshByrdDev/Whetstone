-- One run with everything known about it: why it happened, what it was told, where its wall clock
-- went, and what it left behind.
with runs as (
    select * from {{ ref('stg_board__cycle_runs') }}
),

skill_versions as (
    select * from {{ ref('stg_git__skill_versions') }}
),
prompt_versions as (
    select * from {{ ref('stg_git__prompt_versions') }}
),

-- The skill and prompt in force when the run started, by git history. Used where the run did not
-- record its own hash, and as the label for one that did.
asof_skill as (
    select r.run_id, v.content_hash, v.subject, v.valid_from
    from runs r
    asof left join skill_versions v
      on v.project_key = r.project_key and v.skill = r.skill and r.started_at >= v.valid_from
),
asof_prompt as (
    select r.run_id, v.content_hash, v.subject, v.valid_from
    from runs r
    asof left join prompt_versions v on r.started_at >= v.valid_from
),
skill_named as (
    select project_key, skill, content_hash, arg_min(subject, valid_from) as subject, min(valid_from) as valid_from
    from skill_versions group by all
),
prompt_named as (
    select content_hash, arg_min(subject, valid_from) as subject, min(valid_from) as valid_from
    from prompt_versions group by all
),

joined as (
    select
        -- The raw columns are replaced by the worked-out ones; selecting both would keep the raw.
        r.* exclude (entry_reason, pass),
        e.entry_reason, e.entry_reason_source,
        e.pass as run_pass,
        m.session_id as matched_session_id,
        m.match_method,
        a.session_span_s, a.tool_busy_s, a.tool_calls, a.tool_errors, a.tool_call_s,
        s.list_cost_usd as session_list_cost_usd,
        count(*) over (partition by m.session_id) as runs_in_session,
        s.cli_version as session_cli_version,

        coalesce(r.skill_hash, ks.content_hash)                    as skill_version,
        case when r.skill_hash is not null then 'recorded'
             when ks.content_hash is not null then 'git' end       as skill_version_source,
        coalesce(sn.subject, ks.subject)                           as skill_version_label,
        coalesce(sn.valid_from, ks.valid_from)                     as skill_version_at,

        coalesce(r.prompt_hash, kp.content_hash)                   as prompt_version,
        case when r.prompt_hash is not null then 'recorded'
             when kp.content_hash is not null then 'git' end       as prompt_version_source,
        coalesce(pn.subject, kp.subject)                           as prompt_version_label,
        coalesce(pn.valid_from, kp.valid_from)                     as prompt_version_at
    from runs r
    join {{ ref('int_run_entry') }} e using (run_id)
    left join {{ ref('int_run_sessions') }} m using (run_id)
    left join {{ ref('int_run_agent_time') }} a using (run_id)
    left join {{ ref('int_transcript_sessions') }} s on s.session_id = m.session_id
    left join asof_skill ks using (run_id)
    left join asof_prompt kp using (run_id)
    left join skill_named sn
      on sn.project_key = r.project_key and sn.skill = r.skill and sn.content_hash = r.skill_hash
    left join prompt_named pn on pn.content_hash = r.prompt_hash
),

split as (
    select
        *,
        (prep_s is not null and startup_s is not null and handoff_s is not null) as split_recorded,
        -- The agent's own time: what is left once the worker's measured stages are taken off, or,
        -- for a run from before they were measured, the span of its session's lines.
        case
            when prep_s is not null and startup_s is not null and handoff_s is not null
                then greatest(duration_s - prep_s - startup_s - handoff_s, 0)
            when session_span_s is not null
                then least(session_span_s, duration_s)
        end as agent_s
    from joined
)

select
    run_id, ticket_id, project_key, track, phase, skill, resumed, model,
    coalesce(provider, case
        when model ilike 'claude%' then 'claude'
        when model ilike 'gemini%' then 'gemini'
        when model ilike 'gpt%' or model ilike 'o1%' or model ilike 'o3%' or model ilike 'o4%' or model ilike '%codex%' then 'codex'
    end, 'unknown')                                                    as provider,
    coalesce(effort, 'not recorded')                                   as effort,
    coalesce(cli_version, session_cli_version)                         as cli_version,
    started_at, ended_at, outcome, phase_after,
    entry_reason, entry_reason_source,
    case
        when entry_reason = 'forward' then 'first-time'
        when entry_reason in ('qa-failed', 'refine-sent-back', 'review-changes', 'conflict') then 'rework'
        when entry_reason in ('resumed', 'unblocked') then 'continuation'
        else 'unknown'
    end                                                                as rework_class,
    run_pass                                                           as pass,
    case when run_pass is null then 'unknown' when run_pass >= 3 then '3+' else cast(run_pass as varchar) end as pass_bucket,
    {{ phase_order('phase_after') }} > {{ phase_order('phase') }}     as advanced,
    {{ phase_order('phase_after') }} < {{ phase_order('phase') }}     as went_backward,
    coalesce(phase_after = 'Blocked', false)                           as blocked_after,

    duration_s,
    duration_s / 60.0                                                  as minutes,
    api_duration_s,
    num_turns,
    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
    -- What a run spent against the rate limit: everything but cache reads, which are a different,
    -- far cheaper kind of token and two orders of magnitude more numerous.
    coalesce(input_tokens, 0) + coalesce(output_tokens, 0) + coalesce(cache_creation_tokens, 0) as tokens,
    peak_context_tokens, context_window_tokens, compactions, tool_result_chars, mutations,
    case when runs_in_session = 1 then session_list_cost_usd end       as list_cost_usd,

    -- Where the wall clock went. The parts add up to duration_s; unmeasured_s is the remainder.
    case when split_recorded then 'recorded'
         when agent_s is not null then 'transcript'
         else 'none' end                                               as split_source,
    case when split_recorded then prep_s end                           as prep_s,
    case when split_recorded then startup_s end                        as startup_s,
    case when matched_session_id is not null and agent_s is not null
         then agent_s - least(tool_busy_s, agent_s) end                as model_s,
    case when matched_session_id is not null and agent_s is not null
         then least(tool_busy_s, agent_s) end                          as tool_s,
    case when matched_session_id is null and split_recorded then agent_s end as agent_unsplit_s,
    case when split_recorded then handoff_s end                        as handoff_s,
    case when not split_recorded and agent_s is not null
         then duration_s - agent_s end                                 as overhead_s,
    duration_s
      - case when split_recorded then prep_s + startup_s + handoff_s + agent_s
             when agent_s is not null then duration_s
             else 0 end                                                as unmeasured_s,

    matched_session_id                                                 as session_id,
    match_method,
    session_span_s,
    tool_calls, tool_errors, tool_call_s,

    skill_version, skill_version_source, skill_version_label, skill_version_at,
    prompt_version, prompt_version_source, prompt_version_label, prompt_version_at,

    start_rebase, base_behind,
    files_changed, lines_added, lines_removed,
    coalesce(lines_added, 0) + coalesce(lines_removed, 0)              as lines_changed,
    case
        when lines_added is null and lines_removed is null then 'not recorded'
        when coalesce(lines_added, 0) + coalesce(lines_removed, 0) = 0 then '0'
        when coalesce(lines_added, 0) + coalesce(lines_removed, 0) <= 50 then '1-50'
        when coalesce(lines_added, 0) + coalesce(lines_removed, 0) <= 200 then '51-200'
        when coalesce(lines_added, 0) + coalesce(lines_removed, 0) <= 800 then '201-800'
        else '800+'
    end                                                                as size_bucket,
    run_commits,
    changed_files_json,
    run_files_json,
    -- The A/B arm the run was in, named with its experiment because every experiment has a
    -- control. A run that named an arm but ran as configured (the experiment was stopped, or an
    -- override could not be honoured) is in no arm: counting it would credit one arm with the
    -- other's settings (docs/bugs-and-experiments-plan.md, 2.6).
    experiment_id,
    case when experiment_applied then experiment_arm end               as experiment_arm_key,
    coalesce(experiment_applied, false)                                as experiment_applied,
    case
        when experiment_id is null then 'none'
        when not coalesce(experiment_applied, false) then 'not applied'
        else '#' || experiment_id || ' ' || experiment_arm
    end                                                                as experiment_arm
from split
