-- One of a project's roles (discovery.roles), in config order. Visits match it without case.
select project_key, role, lower(trim(role)) as role_match, ordinal as role_ordinal
from {{ source('recon', 'recon_roles') }}
