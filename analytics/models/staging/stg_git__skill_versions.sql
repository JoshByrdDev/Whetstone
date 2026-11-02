-- One version of one project's skill: from the commit that wrote it until the next one.
select
    project_key,
    skill,
    commit_sha,
    cast(timezone('UTC', try_cast(committed_at as timestamptz)) as timestamp) as valid_from,
    subject,
    content_hash
from {{ source('git', 'skill_versions') }}
where content_hash is not null
