-- The projects the Recon marts were built for, as the board's config had them at this refresh:
-- how many areas and roles, and each repo's base ref and tip (or why its history could not be
-- read). A project with a discovery block that is missing here was configured after the last
-- refresh: its Recon has not been built yet.
with repos as (
    select project_key,
           to_json(list({'repo': repo, 'base_ref': base_ref, 'tip': tip_sha, 'commits': commits_loaded, 'error': error}
                        order by repo))::varchar as repos,
           bool_and(error is null)              as history_ok
    from {{ ref('stg_recon__repos') }} group by project_key
)
select
    p.project_key,
    p.project_ordinal,
    p.area_count,
    p.role_count,
    p.inputs_at,
    coalesce(r.repos, '[]')         as repos,
    coalesce(r.history_ok, false)   as history_ok,
    {{ as_of() }}                   as built_at
from {{ ref('stg_recon__projects') }} p
left join repos r using (project_key)
