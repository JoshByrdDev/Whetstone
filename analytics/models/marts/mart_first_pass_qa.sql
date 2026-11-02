-- Of the tickets QA looked at, how many it passed without sending back - by the week QA first saw
-- them, and by what built them (the implement run that first handed them to QA).
with t as (
    select *, first_qa_at as started_at
    from {{ ref('fct_tickets') }}
    where qa_attempted
),
s as (
    {{ scoped('t') }}
),
cut as (
    {%- set dims = {
        'week': "strftime(date_trunc('week', first_qa_at), '%Y-%m-%d')",
        'build_model': "coalesce(build_model, 'unknown')",
        'build_effort': "coalesce(build_effort, 'unknown')",
        'build_skill_version': "coalesce(build_skill_version, 'unknown')",
        'build_prompt_version': "coalesce(build_prompt_version, 'unknown')",
        'all': "'all'",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, dimension, dim_value,
    count(*)                                        as qa_attempted,
    count(*) filter (where first_pass_qa)           as first_pass,
    count(*) filter (where first_pass_qa) / count(*) as first_pass_rate,
    sum(qa_failures)                                as qa_failures,
    avg(qa_failures)                                as mean_qa_failures,
    sum(rework_minutes)                             as rework_minutes
from cut
group by all
