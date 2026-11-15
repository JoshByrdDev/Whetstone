-- Where a commit a visit or observation recorded sits on the base's first-parent line: 'line',
-- 'merged' (brought in by the first-parent commit at fp_index) or 'unknown' (not an ancestor of the
-- base, or not in that repo). One row per project, repo and recorded commit.
select project_key, coalesce(repo, '') as repo, commit_sha, resolved_sha, fp_index, placement
from {{ source('recon', 'recon_visit_commits') }}
