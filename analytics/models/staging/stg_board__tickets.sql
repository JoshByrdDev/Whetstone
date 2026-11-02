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
    "PlanKey"                      as plan_key,
    "PlanUnit"                     as plan_unit,
    {{ board_ts('"MergedAt"') }}   as merged_at,
    coalesce("Walkthrough", false) as is_walkthrough,
    coalesce("IsBug", false)       as is_bug
from {{ source('board', 'Tickets') }}
