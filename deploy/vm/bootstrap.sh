#!/usr/bin/env bash
#
# Prepares a fresh Ubuntu VM to run the demo. Idempotent: safe to re-run.
#
#   deploy/vm/bootstrap.sh
#
# Installs Docker from Docker's own apt repository rather than Ubuntu's
# docker.io package, because the distribution package ships an older engine
# without the `docker compose` plugin, and this stack is a compose file.
set -euo pipefail

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

if docker compose version >/dev/null 2>&1; then
  say "Docker and the compose plugin are already here"
  docker --version
  docker compose version
else
  say "Installing Docker"
  sudo apt-get update -qq
  sudo apt-get install -y -qq ca-certificates curl gnupg
  sudo install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  sudo chmod a+r /etc/apt/keyrings/docker.gpg
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -qq
  sudo apt-get install -y -qq docker-ce docker-ce-cli containerd.io \
                              docker-buildx-plugin docker-compose-plugin
  sudo usermod -aG docker "$USER"
  say "Added $USER to the docker group — a new login is needed for that to apply"
fi

# Kafka and Elasticsearch-style services need more map counts than the default,
# and Keycloak plus eleven .NET services on 16 GiB is comfortable but not if
# the kernel is stingy about file handles.
say "Raising a couple of kernel limits"
sudo tee /etc/sysctl.d/99-eauction.conf >/dev/null <<'SYSCTL'
# One broker, eleven services and two portals on one host: the defaults are
# tuned for a desktop, and the first symptom of hitting them is a container
# that restarts with no useful log line.
vm.max_map_count = 262144
fs.file-max = 131072
SYSCTL
sudo sysctl --system >/dev/null
echo "  vm.max_map_count=$(cat /proc/sys/vm/max_map_count)"

# Docker's default log driver keeps growing. Eleven services on a demo box that
# nobody prunes is how a 30 GiB disk fills up in a fortnight.
say "Capping container logs"
sudo mkdir -p /etc/docker
if [ -f /etc/docker/daemon.json ] && grep -q log-opts /etc/docker/daemon.json; then
  echo "  already configured"
else
  sudo tee /etc/docker/daemon.json >/dev/null <<'DAEMON'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "20m", "max-file": "3" }
}
DAEMON
  sudo systemctl restart docker
  echo "  20m x 3 per container"
fi

say "Ready"
echo "  next: deploy/vm/deploy.sh"
