#!/usr/bin/env bash
# Regenerate the compendium (if needed) and deploy: main + gh-pages.
set -euo pipefail
cd "$(dirname "$0")/.."

MSG="${1:-Compendium update}"
WT=$(mktemp -d)

git add -A
git diff --cached --quiet || git commit -m "$MSG"
git push origin main

git worktree add -q "$WT" gh-pages
cp bible/DungeonLurkerBible.html "$WT/index.html"
cd "$WT"
git add index.html
git diff --cached --quiet || git commit -m "$MSG"
git push origin gh-pages
cd - >/dev/null
git worktree remove "$WT"
echo "Deployed: https://doubtfulhermit.github.io/dungeon-lurker-coop/"
