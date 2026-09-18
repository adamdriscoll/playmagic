#!/usr/bin/env bash
set -euo pipefail
umask 0077

release_id=${1:-}
if [[ ! $release_id =~ ^[0-9a-f]{40}-[0-9]+-[0-9]+$ ]]; then
  echo "Invalid release ID" >&2
  exit 1
fi

base=/opt/playmagic
release="$base/releases/$release_id"
database=/var/lib/playmagic/playmagic.db
backup_dir="$base/backups"
[[ -f "$release/PlayMagic.dll" ]] || { echo "Release is incomplete" >&2; exit 1; }
chgrp -R playmagic "$release"
chmod -R g+rX "$release"

if [[ -f $database ]]; then
  backup="$backup_dir/playmagic-$(date -u +%Y%m%dT%H%M%SZ)-$release_id.db"
  sqlite3 -cmd '.timeout 30000' "$database" ".backup '$backup'"
  echo "SQLite backup: $backup"
fi

previous=$(readlink -f "$base/current" || true)
ln -sfn "$release" "$base/current.next"
mv -Tf "$base/current.next" "$base/current"

if sudo -n /usr/bin/systemctl restart playmagic.service; then
  for attempt in {1..30}; do
    if curl --fail --silent --show-error --max-time 2 http://127.0.0.1:5000/healthz >/dev/null 2>&1; then
      echo "Release $release_id is healthy"
      exit 0
    fi
    sleep 2
  done
fi

echo "Release $release_id failed its local health check" >&2
if [[ -n $previous && -f $previous/PlayMagic.dll ]]; then
  ln -sfn "$previous" "$base/current.next"
  mv -Tf "$base/current.next" "$base/current"
  sudo -n /usr/bin/systemctl restart playmagic.service || true
  echo "Previous binary restored. If a migration changed SQLite, restore the backup above." >&2
fi
exit 1
