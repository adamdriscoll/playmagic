#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 || $# -ne 1 || ! -s $1 ]]; then
  echo "Usage: sudo bash bootstrap.sh /path/to/playmagic-deploy.pub" >&2
  exit 1
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
apt-get update
apt-get install -y aspnetcore-runtime-10.0 rsync sqlite3 curl

if ! id -u playmagic >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/playmagic --shell /usr/sbin/nologin playmagic
fi
if ! id -u playmagic-deploy >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash playmagic-deploy
fi
usermod -aG playmagic playmagic-deploy

install -d -m 0700 -o playmagic-deploy -g playmagic-deploy /home/playmagic-deploy/.ssh
install -m 0600 -o playmagic-deploy -g playmagic-deploy "$1" /home/playmagic-deploy/.ssh/authorized_keys
install -d -m 0750 -o playmagic-deploy -g playmagic /opt/playmagic
install -d -m 2750 -o playmagic-deploy -g playmagic /opt/playmagic/releases
install -d -m 0750 -o playmagic-deploy -g playmagic /opt/playmagic/backups
install -d -m 0770 -o playmagic -g playmagic /var/lib/playmagic
install -m 0644 "$script_dir/playmagic.service" /etc/systemd/system/playmagic.service
printf 'playmagic-deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart playmagic.service\n' \
  > /etc/sudoers.d/playmagic-deploy
chmod 0440 /etc/sudoers.d/playmagic-deploy
visudo -cf /etc/sudoers.d/playmagic-deploy
systemctl daemon-reload
systemctl enable playmagic.service

echo "Bootstrap complete. Configure Cloudflare Tunnel before running Publish to production."
