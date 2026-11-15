-- One proposal a discovery's sift or recheck wrote, or a line it dropped: what it was, the run that
-- wrote it (and so its model, effort, skill version, the taste file it read), the owner's verdict
-- from the event log, what it became, and whether it landed. The grain both the Discovery marts
-- and Recon's (docs/recon-tab-plan.md, "The marts") read, so its names are theirs: Recon adds to it.
--
-- `area` is the proposal's own, as Sift wrote it. Recon (R2) derives an area from its observations'
-- URLs against the project's `discovery.areas`, which this model cannot see yet; it is meant to
-- replace this column's value in place, keeping the name.
--
-- `verdict_closed`: decided, and the verdict round it was in is over - the discovery left
-- NeedsVerdict after the proposal was written, a Complete filed something after it, or the
-- discovery is Done. Rates are over closed verdicts, so a page still being decided does not drag
-- them about.
with p as (
    select * from {{ ref('stg_board__discovery_proposals') }}
),
v as (
    select * from {{ ref('int_discovery_proposal_verdicts') }}
),
d as (
    select * from {{ ref('int_discovery_tickets') }}
),
runs as (
    select * from {{ ref('int_discovery_runs') }}
),

writer as (
    select p.proposal_id, coalesce(bl.run_id, bt.run_id) as run_id
    from p
    left join runs bl on bl.discovery_id = p.discovery_id and bl.lease_run_id = p.lease_run_id
    left join runs bt
      on bl.run_id is null and bt.discovery_id = p.discovery_id
     and bt.phase in ('Sifting', 'Rechecking')
     and p.created_at >= bt.started_at and p.created_at <= coalesce(bt.ended_at, p.created_at)
    qualify row_number() over (partition by p.proposal_id order by bl.run_id nulls last, bt.started_at desc) = 1
),

taste as (
    select p.proposal_id, r.taste_version_id, r.taste_version_number, r.lease_run_id is not null as read_by_lease
    from p
    join {{ ref('stg_board__discovery_taste_reads') }} r
      on r.discovery_id = p.discovery_id and r.round = p.round
     and (r.lease_run_id = p.lease_run_id or r.read_at <= p.created_at)
    qualify row_number() over (partition by p.proposal_id
                               order by (r.lease_run_id = p.lease_run_id) desc nulls last, r.read_at desc, r.taste_read_id desc) = 1
),

-- A goal's first check is whether it landed the first time; its latest is where it stands.
goal as (
    select proposal_id,
           arg_min(outcome, round)  as first_outcome,
           arg_max(outcome, round)  as last_outcome,
           count(*)                 as checks
    from {{ ref('stg_board__discovery_goal_checks') }} group by proposal_id
),

obs as (
    select p.proposal_id, count(o.observation_id) as observations
    from p
    left join {{ ref('stg_board__discovery_observations') }} o
      on o.discovery_id = p.discovery_id and list_contains(p.observation_ids, o.observation_id)
    group by p.proposal_id
),

closes as (
    select ticket_id as discovery_id, moved_at as closed_at
    from {{ ref('stg_board__phase_transitions') }} where from_phase = 'NeedsVerdict'
    union all
    select discovery_id, acted_at from {{ ref('stg_board__discovery_verdict_events') }} where kind = 'Filed'
),

confirmed as (
    select distinct bug_ticket_id from {{ ref('stg_board__bug_links') }} where status = 'Confirmed'
)

select
    p.proposal_id,
    p.discovery_id,
    d.project_key,
    d.track,
    p.round,
    case when p.round = 0 then 'sift' else 'recheck' end                as source,
    lower(p.recheck_kind)                                               as recheck_kind,
    p.goal_id,
    p.kind,
    p.draft_kind,
    p.rank,
    p.area,
    coalesce(nullif(p.draft_title, ''), p.title)                         as draft_title,
    p.title,
    p.created_at,
    date_trunc('week', p.created_at)::date                              as created_week,
    o.observations,
    -- What Sift did with it and what the owner did.
    v.was_dropped,
    p.drop_reason,
    v.restored,
    v.shown,
    v.verdict,
    v.verdict <> 'undecided'                                            as decided,
    v.verdict <> 'undecided' and (
        d.is_done or exists (select 1 from closes c where c.discovery_id = p.discovery_id and c.closed_at >= p.created_at)
    )                                                                   as verdict_closed,
    v.verdict = 'approved'                                              as approved,
    v.reject_reason,
    v.changed_mind,
    v.new_to_me,
    v.edited,
    v.kind_changed,
    v.edit_distance,
    v.draft_length,
    v.edit_share,
    v.first_decided_at,
    v.decided_at,
    -- What it became.
    v.filed,
    v.filed_at,
    v.filed_ticket_id,
    ft.phase                                                            as filed_ticket_phase,
    ft.phase = 'Done'                                                   as filed_ticket_done,
    coalesce(ft.is_bug, false)                                          as filed_as_bug,
    v.filed_ticket_id is not null and v.filed_ticket_id in (select bug_ticket_id from confirmed) as filed_bug_confirmed,
    -- Whether it landed: a filed Ticket or Bug is a goal for the rechecks.
    p.kind in ('Ticket', 'Bug') and v.filed                             as is_goal,
    g.checks                                                            as goal_checks,
    g.first_outcome                                                     as goal_first_outcome,
    g.last_outcome                                                      as goal_last_outcome,
    g.first_outcome = 'Met'                                             as landed_first_time,
    -- Repeats.
    rp.repeat_of_proposal_id,
    rp.repeat_of_discovery_id,
    rp.repeat_of_discovery_id = p.discovery_id                          as repeat_in_same_discovery,
    rp.repeat_of_proposal_id is not null                                as is_repeat,
    -- The run that wrote it, and what that run was.
    p.lease_run_id,
    w.run_id,
    r.phase                                                             as run_phase,
    r.model,
    r.effort,
    r.provider,
    r.skill_version,
    r.skill_version_label,
    r.experiment_arm,
    t.taste_version_id,
    t.taste_version_number,
    case when t.proposal_id is null then 'not recorded'
         when t.taste_version_number is null then 'no file'
         else 'v' || t.taste_version_number end                         as taste_version,
    -- The discovery it belongs to, and the lineage of its direction (a direction and its reruns).
    d.direction,
    d.direction_lineage_id,
    d.direction_lineage_title
from p
join d using (discovery_id)
join v using (proposal_id)
left join writer w using (proposal_id)
left join runs r on r.run_id = w.run_id
left join taste t using (proposal_id)
left join goal g on g.proposal_id = p.proposal_id
left join obs o on o.proposal_id = p.proposal_id
left join {{ ref('int_discovery_repeats') }} rp on rp.proposal_id = p.proposal_id
left join {{ ref('stg_board__tickets') }} ft on ft.ticket_id = v.filed_ticket_id
