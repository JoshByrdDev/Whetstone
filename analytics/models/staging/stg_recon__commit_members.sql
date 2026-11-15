-- The commits each first-parent commit stands for: itself, and for a merge the commits it brought
-- in. What a Delivery ticket's recorded commits are matched against.
select project_key, coalesce(repo, '') as repo, sha, member_sha
from {{ source('recon', 'recon_commit_members') }}
