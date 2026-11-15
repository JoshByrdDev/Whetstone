-- One file of one commit on a project's base, first-parent line only, newest first: fp_index 0 is
-- the tip, so "after a visit" is a smaller fp_index - ancestry, not clock time. A commit that
-- changed no file has one row with a null path.
select
    project_key,
    coalesce(repo, '')                  as repo,
    sha,
    fp_index,
    try_cast(committed_at as timestamptz) at time zone 'UTC' as committed_at,
    subject,
    coalesce(is_merge, false)           as is_merge,
    path
from {{ source('recon', 'recon_commits') }}
