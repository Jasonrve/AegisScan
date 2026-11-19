#!/usr/bin/env bash
# Forks every repository listed in repos.txt (owner/name per line) into the GitHub organisation (default Workside-Security),
# named <owner>-<name> so same-named repositories from different owners never collide. Idempotent: existing forks are skipped.
# Needs `gh` logged in as an organisation owner.
set -u
ORG="${ORG:-Workside-Security}"
cd "$(dirname "$0")"
while IFS= read -r src; do
  src="${src%$'\r'}"; [ -z "$src" ] && continue
  name="${src%%/*}-${src#*/}"
  if gh api "repos/$ORG/$name" --silent 2>/dev/null; then echo "skip   $src (exists as $name)"; continue; fi
  for attempt in 1 2 3; do
    if out=$(gh api -X POST "repos/$src/forks" -f organization="$ORG" -f name="$name" -F default_branch_only=true --jq .full_name 2>&1); then echo "forked $src -> $out"; break; fi
    echo "retry  $src ($attempt): $(echo "$out" | head -c 160)"; sleep $((attempt * 30))
  done
  sleep 3   # stay under GitHub's secondary rate limit for repository creation
done < repos.txt
