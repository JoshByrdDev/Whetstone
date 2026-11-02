-- A ticket enrolled in an experiment, on one arm for good.
select
    "Id"                            as assignment_id,
    "ExperimentId"                  as experiment_id,
    "TicketId"                      as ticket_id,
    "ArmKey"                        as arm_key,
    "Ordinal"                       as ordinal,
    {{ board_ts('"AssignedAt"') }}  as assigned_at
from {{ source('board', 'ExperimentAssignments') }}
