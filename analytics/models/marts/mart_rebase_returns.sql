-- Rebase returns: reviewed work back from review only to be rebased, by the verdict the run that
-- rebased it gave. What a verdict saves is the QA and refinement it skipped (agent minutes and hours
-- until back at review); what it risks is the rate at which the work was sent back for something
-- other than a conflict afterwards, or escaped. The two verdicts side by side are the measure of
-- whether BackToReview is being given where it should be.
with s as (
    {{ scoped(ref('int_rebase_returns'), ts_col='returned_at') }}
),
cut as (
    {%- set dims = {
        'verdict': "verdict",
        'actor': "coalesce(actor, 'unknown')",
        'week': "strftime(date_trunc('week', returned_at), '%Y-%m-%d')",
        'all': "'all'",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, dimension, dim_value,
    count(*)                                                        as returns,
    count(*) filter (where verdict <> 'unjudged')                   as judged,
    count(*) filter (where verdict = 'BackToReview')                as back_to_review,
    count(*) filter (where verdict = 'Retest')                      as retest,
    count(*) filter (where back_at_review_at is not null)           as back_at_review,
    median(agent_minutes_to_review) filter (where back_at_review_at is not null) as median_agent_minutes_to_review,
    sum(agent_minutes_to_review)                                    as total_agent_minutes,
    median(hours_to_review) filter (where back_at_review_at is not null) as median_hours_to_review,
    sum(qa_refine_runs)                                             as qa_refine_runs,
    count(*) filter (where sent_back_after)                         as sent_back_after,
    count(*) filter (where escaped_after)                           as escaped_after,
    count(*) filter (where sent_back_after) / nullif(count(*) filter (where back_at_review_at is not null), 0) as sent_back_after_rate
from cut
group by all
