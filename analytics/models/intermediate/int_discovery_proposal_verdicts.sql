-- Each proposal's verdict, read from the owner's event log rather than from the proposal's current
-- columns: the latest approve / reject / snooze / undo is the verdict, a restore is a restore even
-- after the proposal was decided, and the edit measured is the one at filing (what became the
-- ticket), else at the last approval. A changed mind is counted, never lost.
with p as (
    select * from {{ ref('stg_board__discovery_proposals') }}
),
e as (
    select * from {{ ref('stg_board__discovery_verdict_events') }}
),

-- The last event that decided or undecided it.
latest as (
    select proposal_id, kind as last_kind, reject_reason as last_reject_reason, acted_at as last_at
    from e
    where kind in ('Approved', 'Rejected', 'Snoozed', 'Undone')
    qualify row_number() over (partition by proposal_id order by acted_at desc, event_id desc) = 1
),
-- What the owner's edit came to, as acted on: at filing when it was filed, else at the last approval.
edits as (
    select proposal_id, edited, kind_changed, edit_distance, draft_length
    from e
    where kind in ('Filed', 'Approved')
    qualify row_number() over (partition by proposal_id
                               order by (kind = 'Filed') desc, acted_at desc, event_id desc) = 1
),
-- Every event carries the owner's new-to-me as it stood, so the latest one is the answer.
ntm as (
    select proposal_id, new_to_me
    from e
    where new_to_me is not null
    qualify row_number() over (partition by proposal_id order by acted_at desc, event_id desc) = 1
),
agg as (
    select
        proposal_id,
        count(*) filter (where kind in ('Approved', 'Rejected', 'Snoozed'))  as verdict_events,
        count(*) filter (where kind = 'Undone')                               as undos,
        bool_or(kind = 'Restored')                                            as restored,
        min(acted_at) filter (where kind = 'Restored')                              as restored_at,
        bool_or(kind = 'Filed')                                               as filed,
        min(acted_at) filter (where kind = 'Filed')                                 as filed_at,
        any_value(filed_ticket_id) filter (where kind = 'Filed')              as filed_ticket_id,
        min(acted_at) filter (where kind in ('Approved', 'Rejected', 'Snoozed'))    as first_decided_at,
        count(*)                                                              as events
    from e
    group by proposal_id
)

select
    p.proposal_id,
    p.discovery_id,
    -- Sift's dropped list keeps its reason after a restore, so a dropped line is told by its reason.
    p.drop_reason is not null                                           as was_dropped,
    coalesce(a.restored, false)                                         as restored,
    a.restored_at,
    -- Shown to the owner as a proposal: written as one, or brought back from the dropped list.
    p.drop_reason is null or coalesce(a.restored, false)                as shown,
    case
        when coalesce(a.filed, false) then 'approved'
        when l.last_kind = 'Approved' then 'approved'
        when l.last_kind = 'Rejected' then 'rejected'
        when l.last_kind = 'Snoozed'  then 'snoozed'
        else 'undecided'
    end                                                                 as verdict,
    case when l.last_kind = 'Rejected' and not coalesce(a.filed, false)
         then coalesce(l.last_reject_reason, 'Unstated') end            as reject_reason,
    -- More than one decision, or a decision taken back: the owner changed their mind.
    coalesce(a.verdict_events, 0) > 1 or coalesce(a.undos, 0) > 0       as changed_mind,
    n.new_to_me,
    coalesce(d.edited, false)                                           as edited,
    coalesce(d.kind_changed, false)                                     as kind_changed,
    d.edit_distance,
    d.draft_length,
    case when d.draft_length > 0
         then least(1.0, d.edit_distance / d.draft_length::double) end  as edit_share,
    coalesce(a.filed, false)                                            as filed,
    a.filed_at,
    coalesce(a.filed_ticket_id, p.filed_ticket_id)                      as filed_ticket_id,
    a.first_decided_at,
    l.last_at                                                           as decided_at,
    coalesce(a.events, 0)                                               as events
from p
left join latest l using (proposal_id)
left join edits d using (proposal_id)
left join ntm n using (proposal_id)
left join agg a using (proposal_id)
