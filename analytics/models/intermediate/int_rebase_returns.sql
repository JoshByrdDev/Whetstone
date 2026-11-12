-- One rebase return, with what happened after it: how long and how much agent time until the ticket
-- was back at review, and whether anything then sent it back for something other than another
-- conflict - QA, refinement, the steward or a person - before it was done or returned again. That
-- is how a verdict is checked: a BackToReview that is sent back afterwards is one that may not
-- have held; a Retest that never is may have cost a QA pass for nothing.
with returns as (
    select * from {{ ref('stg_board__rebase_returns') }}
),
moves as (
    select * from {{ ref('stg_board__phase_transitions') }}
),
escapes as (
    select * from {{ ref('stg_board__escape_events') }}
),
runs as (
    select * from {{ ref('int_runs') }}
),

-- Where this return's story ends: the ticket's next return, or Done, whichever comes first.
bounded as (
    select
        r.*,
        least(
            coalesce((select min(n.returned_at) from returns n
                      where n.ticket_id = r.ticket_id and n.returned_at > r.returned_at), 'infinity'::timestamp),
            coalesce((select min(m.moved_at) from moves m
                      where m.ticket_id = r.ticket_id and m.to_phase = 'Done' and m.moved_at > r.returned_at), 'infinity'::timestamp)
        ) as ends_at,
        (select min(m.moved_at) from moves m
         where m.ticket_id = r.ticket_id and m.to_phase = 'NeedsReview' and m.moved_at > r.returned_at) as back_at_review_at
    from returns r
)

select
    b.return_id, b.ticket_id, b.project_key, b.track, b.actor, b.returned_at,
    coalesce(b.verdict, 'unjudged') as verdict,
    b.handed_off_to, b.back_at_review_at,
    date_diff('second', b.returned_at, b.back_at_review_at) / 3600.0 as hours_to_review,
    (select coalesce(sum(x.minutes), 0) from runs x
     where x.ticket_id = b.ticket_id and x.started_at >= b.returned_at
       and x.started_at < coalesce(b.back_at_review_at, b.ends_at)) as agent_minutes_to_review,
    (select count(*) from runs x
     where x.ticket_id = b.ticket_id and x.phase in ('QaTesting', 'Refining') and x.started_at >= b.returned_at
       and x.started_at < coalesce(b.back_at_review_at, b.ends_at)) as qa_refine_runs,
    exists (select 1 from moves m
            where m.ticket_id = b.ticket_id and m.backward and m.moved_at > b.returned_at and m.moved_at < b.ends_at
              and m.to_phase not in ('Blocked', 'Backlog')
              and coalesce(m.reason, '') not ilike '%conflicts with%') as sent_back_after,
    exists (select 1 from escapes e
            where e.ticket_id = b.ticket_id and e.escaped_at > b.returned_at and e.escaped_at < b.ends_at) as escaped_after
from bounded b
