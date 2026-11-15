-- Each repo of a project with Recon inputs: the project's base ref as the board resolved it, its tip,
-- how many first-parent commits were loaded, and why none were when that is so.
select project_key, coalesce(repo, '') as repo, base_ref, tip_sha, coalesce(commits, 0) as commits_loaded, error
from {{ source('recon', 'recon_repos') }}
