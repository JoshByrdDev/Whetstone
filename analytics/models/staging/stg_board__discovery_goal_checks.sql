-- One recheck's finding on one approved goal (a filed Ticket or Bug proposal): Met, NotMet or
-- Unclear. One row per goal per round; a goal's earliest row is whether it landed the first time.
select
    "Id"                                as goal_check_id,
    "TicketId"                          as discovery_id,
    "ProposalId"                        as proposal_id,
    "FiledTicketId"                     as filed_ticket_id,
    "Round"                             as round,
    "Outcome"                           as outcome,
    "RunId"                             as lease_run_id,
    nullif(trim("Commit"), '')          as commit_sha,
    {{ board_ts('"At"') }}              as checked_at
from {{ source('board', 'DiscoveryGoalChecks') }}
