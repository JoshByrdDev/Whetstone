-- Did that change help? Each version of the chart, scout, sift and recheck skills and of the
-- project's taste file, in the order they came, with what the proposals under it came to - the
-- before and after of every fold and every prompt edit (docs/discovery-track-plan.md, step 4's
-- taste versions and the skill history fct_runs already carries).
--
-- Which proposals a version is credited with:
--   sift_skill, recheck_skill  the proposals its own runs wrote (round 0, and round 1 and later);
--   scout_skill, chart_skill   every shown proposal of round 0 of the discoveries it scouted or charted;
--   taste                      the proposals of every run that read it ('no file' before the first).
-- Taste versions are ordered by number, skills by when the version was first in force.
with p as (
    select f.*, d.created_at as cohort_at, d.scout_skill_version, d.chart_skill_version
    from {{ ref('fct_discovery_proposals') }} f
    join {{ ref('int_discovery_tickets') }} d using (discovery_id)
    where f.shown
),
runs as (
    select * from {{ ref('int_discovery_runs') }}
),
-- When each skill version was first in force on a run, and its label.
skill_meta as (
    select phase, skill_version as version, min(coalesce(skill_version_at, started_at)) as version_at,
           any_value(skill_version_label) as label
    from runs where skill_version is not null group by all
),
-- A taste version is a project's own number, so across all projects 'v2' is every project's second.
taste_meta as (
    select project_key as scope, 'v' || number as version, min(written_at) as version_at,
           any_value(source) as label, min(number) as number
    from {{ ref('stg_board__discovery_taste_versions') }} group by all
    union all
    select '*', 'v' || number, min(written_at), any_value(source), min(number)
    from {{ ref('stg_board__discovery_taste_versions') }} group by all
),

credited as (
    select * exclude (kind), 'sift_skill' as kind, skill_version as version from p where source = 'sift' and skill_version is not null
    union all
    select * exclude (kind), 'recheck_skill', skill_version from p where source = 'recheck' and skill_version is not null
    union all
    select * exclude (kind), 'scout_skill', scout_skill_version from p where round = 0 and scout_skill_version is not null
    union all
    select * exclude (kind), 'chart_skill', chart_skill_version from p where round = 0 and chart_skill_version is not null
    union all
    select * exclude (kind), 'taste', taste_version from p where taste_version <> 'not recorded'
),
s as ({{ scoped('credited', 'cohort_at') }}),

agg as (
    select
        window_days, project_scope, track_scope, kind, version,
        min(created_at)                                                       as first_at,
        max(created_at)                                                       as last_at,
        count(distinct discovery_id)                                          as discoveries,
        count(*)                                                              as shown,
        count(*) filter (where verdict_closed)                                as closed,
        count(*) filter (where verdict_closed and approved)                   as approved,
        count(*) filter (where verdict_closed and verdict = 'rejected')       as rejected,
        count(*) filter (where verdict_closed and reject_reason in ('Generic', 'Wrong')) as rejected_taste,
        count(*) filter (where verdict_closed and approved and new_to_me)     as new_to_me,
        count(*) filter (where verdict_closed and approved and edited)        as edited
    from s group by all
)

select
    a.*,
    case a.kind when 'taste' then tm.label else sm.label end                  as label,
    case a.kind when 'taste' then tm.version_at else sm.version_at end        as version_at,
    a.closed >= {{ var('discovery_min_verdicts') }}                           as enough_verdicts,
    case when a.closed >= {{ var('discovery_min_verdicts') }} then a.approved / a.closed::double end as approval_rate,
    case when a.closed >= {{ var('discovery_min_verdicts') }} then a.rejected_taste / a.closed::double end as taste_rejection_rate,
    case when a.closed >= {{ var('discovery_min_verdicts') }} then a.new_to_me / nullif(a.approved, 0)::double end as new_to_me_share,
    row_number() over (partition by a.window_days, a.project_scope, a.track_scope, a.kind
                       order by case when a.version = 'no file' then -1 end nulls last,
                                case a.kind when 'taste' then tm.number end,
                                coalesce(case a.kind when 'taste' then tm.version_at else sm.version_at end, a.first_at),
                                a.first_at, a.version) as version_seq
from agg a
left join skill_meta sm
  on a.kind <> 'taste' and sm.version = a.version
 and sm.phase = case a.kind when 'sift_skill' then 'Sifting' when 'recheck_skill' then 'Rechecking'
                            when 'scout_skill' then 'Scouting' else 'Charting' end
left join taste_meta tm
  on a.kind = 'taste' and tm.version = a.version and tm.scope = a.project_scope
