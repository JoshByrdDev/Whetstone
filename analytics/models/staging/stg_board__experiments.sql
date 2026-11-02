-- An A/B experiment on one phase's skill: what it tests, and where it stands as the owner set it.
select
    "Id"                              as experiment_id,
    "ProjectKey"                      as project_key,
    "Skill"                           as skill,
    "Name"                            as name,
    "Hypothesis"                      as hypothesis,
    "Status"                          as status,
    "MinTicketsPerArm"                as min_tickets_per_arm,
    "WaitDays"                        as wait_days,
    {{ board_ts('"StartedAt"') }}     as started_at,
    {{ board_ts('"StoppedAt"') }}     as stopped_at,
    "StopKind"                        as stop_kind,
    "StopReason"                      as stop_reason,
    {{ board_ts('"ConcludedAt"') }}   as concluded_at,
    "Winner"                          as winner
from {{ source('board', 'Experiments') }}
