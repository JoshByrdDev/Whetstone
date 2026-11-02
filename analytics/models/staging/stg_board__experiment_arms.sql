-- One arm of an experiment. A null override means "as configured".
select
    "Id"             as arm_id,
    "ExperimentId"   as experiment_id,
    "Key"            as arm_key,
    "Name"           as arm_name,
    "SkillVariant"   as skill_variant,
    "Model"          as model,
    "Effort"         as effort,
    "Provider"       as provider
from {{ source('board', 'ExperimentArms') }}
