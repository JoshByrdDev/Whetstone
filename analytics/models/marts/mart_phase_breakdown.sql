-- What a phase costs, cut by whatever might explain it: the provider, model, effort, skill version,
-- prompt version, why the run happened, which time round it was, the CLI version, an A/B arm, the
-- machine it ran on and how busy that machine was.
--
-- One row per scope, phase, dimension and value. `dimension = 'all'` is the phase as a whole.
-- Medians rather than means, as on the board: one timed-out run would drag a mean somewhere useless.
with s as (
    {{ scoped(ref('fct_runs')) }}
),

cut as (
    {%- set dims = {
        'all': "'all'",
        'provider': "provider",
        'model': "coalesce(model, 'unknown')",
        'effort': "effort",
        'skill_version': "coalesce(skill_version, 'unknown')",
        'prompt_version': "coalesce(prompt_version, 'unknown')",
        'entry_reason': "entry_reason",
        'pass': "pass_bucket",
        'cli_version': "coalesce(cli_version, 'unknown')",
        'experiment_arm': "experiment_arm",
        'machine': "machine",
        'machine_load': "machine_load",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, phase, dimension, dim_value,
    count(*)                                                        as runs,
    count(distinct ticket_id)                                       as tickets,
    median(minutes)                                                 as median_minutes,
    quantile_cont(minutes, 0.9)                                     as p90_minutes,
    sum(minutes)                                                    as total_minutes,
    median(tokens)                                                  as median_tokens,
    sum(tokens)                                                     as total_tokens,
    sum(cache_read_tokens)                                          as total_cache_read_tokens,
    median(num_turns)                                               as median_turns,
    median(tool_calls) filter (where session_id is not null)        as median_tool_calls,
    count(*) filter (where outcome = 'Completed')                   as completed,
    count(*) filter (where outcome = 'Timeout')                     as timeouts,
    count(*) filter (where outcome = 'Error')                       as errors,
    count(*) filter (where advanced)                                as advanced,
    count(*) filter (where went_backward)                           as went_backward,
    count(*) filter (where rework_class = 'rework')                 as rework_runs,
    coalesce(sum(minutes) filter (where rework_class = 'rework'), 0) as rework_minutes,
    coalesce(sum(minutes) filter (where rework_class = 'rework'), 0) / nullif(sum(minutes), 0) as rework_share,
    -- What a value is worth beside the others: its first and last run, so a version reads as a span.
    min(started_at)                                                 as first_at,
    max(started_at)                                                 as last_at,
    any_value(case when dimension = 'skill_version' then skill_version_label
                   when dimension = 'prompt_version' then prompt_version_label end) as dim_label
from cut
group by all
