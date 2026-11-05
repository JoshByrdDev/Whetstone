-- What got past the whole pipeline: finished work a person sent back after it reached review, by
-- the kind of miss the implementation run that picked it up said it was (corrected by a person
-- where they disagreed - counted as overridden). The rate is per hand-off to review, so it falls
-- when the pipeline gets better, not only when less work is done. A requirement change is counted
-- apart - the owner changing their mind is not a miss - and an escape nobody classified (one from
-- before runs classified their own) is counted apart too, never guessed into a kind.
--
-- Cut by the week of the hand-off (the trend) and by the settings of the implementing run that
-- built what was handed off, so a kind that keeps escaping can be traced to a skill or a model.
with s as (
    {{ scoped(ref('int_escape_handoffs'), ts_col='handed_off_at') }}
),
cut as (
    {%- set dims = {
        'week': "strftime(date_trunc('week', handed_off_at), '%Y-%m-%d')",
        'model': "coalesce(model, 'unknown')",
        'effort': "coalesce(effort, 'unknown')",
        'provider': "coalesce(provider, 'unknown')",
        'skill_version': "coalesce(skill_version, 'unknown')",
        'all': "'all'",
    } %}
    {%- for name, expr in dims.items() %}
    select *, '{{ name }}' as dimension, {{ expr }} as dim_value from s
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
)

select
    window_days, project_scope, track_scope, dimension, dim_value,
    count(*) filter (where is_handoff)                              as handoffs,
    count(distinct ticket_id) filter (where is_handoff)             as tickets_reviewed,
    count(*) filter (where is_escape)                               as escapes,
    count(*) filter (where is_miss)                                 as misses,
    count(*) filter (where is_requirement_change)                   as requirement_changes,
    count(*) filter (where is_unclassified)                         as unclassified,
    count(*) filter (where is_overridden)                           as overridden,
    count(*) filter (where is_escape and kind = 'OutsideContract')    as outside_contract,
    count(*) filter (where is_escape and kind = 'InheritedBehaviour') as inherited_behaviour,
    count(*) filter (where is_escape and kind = 'UiPolish')           as ui_polish,
    count(*) filter (where is_escape and kind = 'Logic')              as logic,
    count(*) filter (where is_escape and kind = 'Other')              as other,
    count(*) filter (where is_escape and source = 'Owner')            as found_by_owner,
    count(*) filter (where is_escape and source = 'PrReview')         as found_in_pr_review,
    count(*) filter (where is_miss) / nullif(count(*) filter (where is_handoff), 0) as misses_per_handoff,
    count(*) filter (where is_miss) / nullif(count(distinct ticket_id) filter (where is_handoff), 0) as misses_per_ticket
from cut
group by all
