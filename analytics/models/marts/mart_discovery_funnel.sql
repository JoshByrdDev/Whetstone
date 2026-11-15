-- Is Discovery worth running, and did the last change to it help? Observations -> proposals ->
-- verdicts -> tickets -> landed, with what it cost and what it took of the owner, per scope and cut
-- (docs/discovery-track-plan.md, "What is measured"; macros/discovery.sql for the cuts).
--
-- Windowed by when the discovery was filed: a discovery and everything that came of it - its
-- rechecks, their verdicts, the tickets it filed - stay in one cohort, so a rate is never the
-- verdicts of one month over the proposals of another.
--
-- Rates are over closed verdicts (fct_discovery_proposals.verdict_closed) and are null below
-- `discovery_min_verdicts` of them, with `enough_verdicts` saying so: the page then says "not
-- enough verdicts yet" instead of a rate from two proposals.
with d as (
    select *, created_at as cohort_at, created_week as cohort_week
    from {{ ref('int_discovery_tickets') }}
),
p as (
    select f.*, d.created_at as cohort_at, d.created_week as cohort_week,
           d.scout_model, d.scout_skill_version, d.chart_skill_version
    from {{ ref('fct_discovery_proposals') }} f
    join d using (discovery_id)
),

-- What each discovery's own proposals came to, for the per-discovery ratios (per dollar, per minute).
per_d as (
    select discovery_id,
           count(*) filter (where approved and verdict_closed)                 as approved_closed,
           count(*) filter (where round = 0 and not was_dropped)               as sift_out,
           count(*) filter (where round = 0 and was_dropped)                   as sift_dropped
    from p group by discovery_id
),
dd as (
    select d.*, coalesce(x.approved_closed, 0) as approved_closed,
           coalesce(x.sift_out, 0) as sift_out, coalesce(x.sift_dropped, 0) as sift_dropped
    from d left join per_d x using (discovery_id)
),
r as (
    select v.*, d.project_key, d.track, d.cohort_at, d.cohort_week, d.sift_model, d.sift_effort, d.sift_provider,
           d.sift_skill_version, d.scout_model, d.scout_skill_version, d.chart_skill_version,
           d.sift_taste_version, d.experiment_arm, d.direction_lineage_id, d.direction_lineage_title
    from {{ ref('int_discovery_verdict_rounds') }} v
    join d using (discovery_id)
),

sp as ({{ scoped('p', 'cohort_at') }}),
sd as ({{ scoped('dd', 'cohort_at') }}),
sr as ({{ scoped('r', 'cohort_at') }}),
cp as ({{ discovery_cut('sp', 'proposal') }}),
cd as ({{ discovery_cut('sd', 'discovery') }}),
cr as ({{ discovery_cut('sr', 'discovery') }}),

props as (
    select
        window_days, project_scope, track_scope, dimension, dim_value,
        any_value(dim_label)                                                        as p_label,
        count(*) filter (where not was_dropped)                                     as proposals_written,
        count(*) filter (where shown)                                               as shown,
        count(*) filter (where was_dropped)                                         as dropped,
        count(*) filter (where restored)                                            as restored,
        count(*) filter (where restored and approved and verdict_closed)            as restored_approved,
        count(*) filter (where shown and decided)                                   as decided,
        count(*) filter (where shown and verdict_closed)                            as closed,
        count(*) filter (where shown and verdict_closed and approved)               as approved,
        count(*) filter (where shown and verdict_closed and verdict = 'rejected')   as rejected,
        count(*) filter (where shown and verdict_closed and reject_reason = 'Generic')        as rejected_generic,
        count(*) filter (where shown and verdict_closed and reject_reason = 'AlreadyDecided') as rejected_already_decided,
        count(*) filter (where shown and verdict_closed and reject_reason = 'NotNow')         as rejected_not_now,
        count(*) filter (where shown and verdict_closed and reject_reason = 'Wrong')          as rejected_wrong,
        count(*) filter (where shown and verdict_closed and verdict = 'snoozed')    as snoozed,
        count(*) filter (where shown and verdict_closed and changed_mind)           as changed_mind,
        count(*) filter (where approved and verdict_closed and new_to_me)           as new_to_me,
        count(*) filter (where approved and verdict_closed and new_to_me is not null) as new_to_me_asked,
        count(*) filter (where approved and verdict_closed and edited)              as edited,
        median(edit_share) filter (where approved and verdict_closed)               as median_edit_share,
        count(*) filter (where filed)                                               as filed,
        count(*) filter (where filed_ticket_id is not null)                         as filed_tickets,
        count(*) filter (where filed_ticket_done)                                   as filed_tickets_done,
        count(*) filter (where is_goal)                                             as goals,
        count(*) filter (where is_goal and goal_checks > 0)                         as goals_checked,
        count(*) filter (where is_goal and landed_first_time)                       as landed_first_time,
        count(*) filter (where source = 'recheck' and shown)                        as recheck_shown,
        count(*) filter (where source = 'recheck' and shown and verdict_closed)     as recheck_closed,
        count(*) filter (where source = 'recheck' and verdict_closed and approved)  as recheck_approved,
        count(*) filter (where recheck_kind = 'fixup' and verdict_closed)           as fixups_closed,
        count(*) filter (where recheck_kind = 'fixup' and verdict_closed and approved) as fixups_approved,
        count(*) filter (where recheck_kind = 'new' and verdict_closed)             as recheck_new_closed,
        count(*) filter (where recheck_kind = 'new' and verdict_closed and approved) as recheck_new_approved,
        count(*) filter (where shown and is_repeat)                                 as repeats,
        count(*) filter (where shown and is_repeat and not repeat_in_same_discovery) as repeats_across,
        count(*) filter (where filed and kind = 'Bug')                              as bugs_filed,
        count(*) filter (where filed and kind = 'Bug' and filed_bug_confirmed)      as bugs_confirmed,
        count(*) filter (where filed and kind = 'Bug' and filed_ticket_done)        as bugs_fixed
    from cp group by all
),

discs as (
    select
        window_days, project_scope, track_scope, dimension, dim_value,
        any_value(dim_label)                                                as d_label,
        count(*)                                                            as discoveries,
        count(*) filter (where is_done)                                     as discoveries_done,
        sum(runs)                                                           as runs,
        sum(runs_priced)                                                    as runs_priced,
        sum(run_minutes)                                                    as run_minutes,
        sum(run_tokens)                                                     as run_tokens,
        count(*) filter (where cost_complete)                               as discoveries_priced,
        sum(cost_usd) filter (where cost_complete)                          as cost_usd,
        sum(approved_closed) filter (where cost_complete)                   as approved_priced,
        sum(owner_minutes)                                                  as owner_minutes,
        sum(approved_closed) filter (where owner_minutes > 0)               as approved_timed,
        sum(owner_minutes) filter (where owner_minutes > 0)                 as owner_minutes_timed,
        sum(observations)                                                   as observations,
        sum(scout_observations)                                             as scout_observations,
        sum(sift_out)                                                       as sift_out,
        sum(sift_dropped)                                                   as sift_dropped,
        sum(visits)                                                         as visits,
        count(*) filter (where visits > 0)                                  as discoveries_with_visits,
        median(pages) filter (where visits > 0)                             as median_pages,
        median(areas_reached) filter (where visits > 0)                     as median_areas,
        median(roles_reached) filter (where visits > 0)                     as median_roles,
        median(widths_reached) filter (where visits > 0)                    as median_widths,
        sum(visit_seconds) / 60.0                                           as visit_minutes,
        sum(off_route_visits)                                               as off_route_visits,
        sum(route_judged_visits)                                            as route_judged_visits
    from cd group by all
),

rounds as (
    select
        window_days, project_scope, track_scope, dimension, dim_value,
        count(*)                                                as verdict_rounds,
        count(*) filter (where completed)                       as verdict_rounds_completed,
        median(hours_to_verdict) filter (where completed)       as median_hours_to_verdict,
        quantile_cont(hours_to_verdict, 0.9) filter (where completed) as p90_hours_to_verdict,
        median(owner_minutes) filter (where completed and owner_events > 0) as median_owner_minutes_per_round
    from cr group by all
),

joined as (
    select
        coalesce(d.window_days, p.window_days)      as window_days,
        coalesce(d.project_scope, p.project_scope)  as project_scope,
        coalesce(d.track_scope, p.track_scope)      as track_scope,
        coalesce(d.dimension, p.dimension)          as dimension,
        coalesce(d.dim_value, p.dim_value)          as dim_value,
        coalesce(d.d_label, p.p_label)              as dim_label,
        p.* exclude (window_days, project_scope, track_scope, dimension, dim_value, p_label),
        d.* exclude (window_days, project_scope, track_scope, dimension, dim_value, d_label)
    from discs d
    full join props p using (window_days, project_scope, track_scope, dimension, dim_value)
)

select
    j.*,
    coalesce(j.closed, 0) >= {{ var('discovery_min_verdicts') }}                     as enough_verdicts,
    {{ var('discovery_min_verdicts') }}                                         as min_verdicts,
    {%- set gate = "coalesce(j.closed, 0) >= " ~ var('discovery_min_verdicts') %}
    case when {{ gate }} then j.approved / nullif(j.closed, 0) end                 as approval_rate,
    case when {{ gate }} then j.rejected / nullif(j.closed, 0) end                 as rejection_rate,
    case when {{ gate }} then j.new_to_me / nullif(j.approved, 0) end              as new_to_me_share,
    case when {{ gate }} then j.edited / nullif(j.approved, 0) end                 as edited_share,
    case when {{ gate }} then j.repeats / nullif(j.shown, 0) end                   as repeat_share,
    case when {{ gate }} then j.approved_priced / nullif(j.cost_usd, 0) end        as approved_per_dollar,
    case when {{ gate }} then j.approved_timed / nullif(j.owner_minutes_timed, 0) end as approved_per_owner_minute,
    -- Neither is a verdict rate, so neither waits for verdicts: observations in, proposals out.
    j.sift_out / nullif(j.scout_observations, 0)::double                           as sift_yield,
    j.sift_dropped / nullif(j.sift_out + j.sift_dropped, 0)::double                as sift_cut_share,
    j.off_route_visits / nullif(j.route_judged_visits, 0)::double                  as off_route_share,
    -- Landed and recheck yield have their own, smaller, denominators.
    j.landed_first_time / nullif(j.goals_checked, 0)::double                       as landed_rate,
    j.recheck_approved / nullif(j.recheck_closed, 0)::double                       as recheck_yield,
    r.verdict_rounds, r.verdict_rounds_completed, r.median_hours_to_verdict, r.p90_hours_to_verdict,
    r.median_owner_minutes_per_round
from joined j
left join rounds r using (window_days, project_scope, track_scope, dimension, dim_value)
