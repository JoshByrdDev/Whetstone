-- Confirmed bugs, by what let them through: the stage that should have caught it (missed_by), who
-- found it, and what wrote it - the model, effort, skill and prompt version of the run that most
-- likely wrote the blamed lines, and the A/B arm it was in. Scoped by when the bug was filed.
--
-- A bug can have more than one confirmed cause, so `bugs` counts bug tickets and `links` counts
-- causes; a split by what wrote it counts a bug once under each of its causes.
with l as (
    select *, bug_created_at as started_at from {{ ref('fct_bug_links') }}
    where bug_created_at is not null
),
s as (
    {{ scoped('l') }}
),
cut as (
    {%- set dims = {
        'all': "'all'",
        'missed_by': "missed_by",
        'found_by': "found_by",
        'written_model': "coalesce(written_model, 'unknown')",
        'written_effort': "coalesce(written_effort, 'unknown')",
        'written_skill_version': "coalesce(written_skill_version, 'unknown')",
        'written_prompt_version': "coalesce(written_prompt_version, 'unknown')",
        'written_experiment_arm': "coalesce(written_experiment_arm, 'unknown')",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, dimension, dim_value,
    count(distinct bug_ticket_id) filter (where is_confirmed)                    as bugs,
    count(*) filter (where is_confirmed)                                         as links,
    count(distinct bug_ticket_id) filter (where is_confirmed and escaped)        as escaped_bugs,
    count(distinct source_ticket_id) filter (where is_confirmed)                 as source_tickets,
    count(*) filter (where is_confirmed and source_ticket_id is null)            as unmapped_links,
    count(*) filter (where is_confirmed and written_by_run_id is not null)       as links_with_run,
    median(days_to_find) filter (where is_confirmed and escaped)                 as median_days_to_find,
    coalesce(sum(attributed_fix_minutes) filter (where is_confirmed), 0)         as fix_minutes,
    -- Waiting on the owner: suggested by a trace, neither confirmed nor rejected yet.
    count(*) filter (where status = 'Suggested')                                 as pending_links,
    count(*) filter (where status = 'Rejected')                                  as rejected_links
from cut
group by all
having dimension = 'all' or count(*) filter (where is_confirmed) > 0
