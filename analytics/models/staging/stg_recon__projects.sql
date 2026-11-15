-- One project with a `discovery` block, as the board's config had it when it started the refresh
-- (recon-inputs.json). A project absent here is one the Recon marts know nothing about yet.
select
    project_key,
    ordinal                             as project_ordinal,
    areas                               as area_count,
    roles                               as role_count,
    try_cast(left(replace(inputs_at, 'T', ' '), 26) as timestamp) as inputs_at
from {{ source('recon', 'recon_projects') }}
