-- One version of the worker prompt template.
select
    commit_sha,
    cast(timezone('UTC', try_cast(committed_at as timestamptz)) as timestamp) as valid_from,
    subject,
    content_hash
from {{ source('git', 'prompt_versions') }}
where content_hash is not null
