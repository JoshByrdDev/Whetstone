-- One ticket, without its long text.
select
    "Id"                           as ticket_id,
    "ProjectKey"                   as project_key,
    coalesce("Track", 'Delivery')  as track,
    "Phase"                        as phase,
    "Title"                        as title,
    "Priority"                     as priority,
    {{ board_ts('"CreatedAt"') }}  as created_at,
    {{ board_ts('"UpdatedAt"') }}  as updated_at,
    nullif("WorktreePath", '')     as worktree_path,
    "Branch"                       as branch,
    "PlanUnit"                     as plan_unit,
    {{ board_ts('"MergedAt"') }}   as merged_at,
    coalesce("Walkthrough", false) as is_walkthrough,
    coalesce("IsBug", false)       as is_bug,
    -- Where it came from (Ticket.Origin), in the plan's spelling: owner, steward, found-in-run,
    -- regression, intake. Null on a database from before TicketOrigin reads as the owner's.
    case coalesce("Origin", 'Owner') when 'FoundInRun' then 'found-in-run'
        else lower(coalesce("Origin", 'Owner')) end as origin,
    nullif("IntakeChannel", '')    as intake_channel,
    "RegressionOf"                 as regression_of,
    "DiscoveryId"                  as discovery_id,
    nullif("ReconFrom", '')        as recon_from
from {{ source('board', 'Tickets') }}
