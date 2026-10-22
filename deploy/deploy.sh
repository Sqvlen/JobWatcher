#!/usr/bin/env bash
# Runs ON THE DROPLET. GitHub Actions calls it over SSH as the only command the CI key may run (see README,
# "Автоматичний деплой"): the arguments arrive in $SSH_ORIGINAL_COMMAND as "<image> <registry user>", and the registry
# token arrives on stdin. Install it once as /home/deploy/deploy.sh (mode 700); the CI cannot overwrite it.
set -euo pipefail

readonly CONTAINER=jobwatcher
readonly ENV_FILE=/home/deploy/jobwatcher.env
readonly DATA_VOLUME=jobwatcher-data
readonly START_TIMEOUT_SECONDS=40
# The only package this script will run: the one cd.yml pushes (lower-case owner/repository). Change it if the repository moves.
readonly IMAGE_REPOSITORY=ghcr.io/sqvlen/jobwatcher
readonly TAG_PATTERN='^[A-Za-z0-9._-]+$'
readonly USER_PATTERN='^[][A-Za-z0-9._-]+$'

read -r IMAGE REGISTRY_USER EXTRA <<< "${SSH_ORIGINAL_COMMAND:-}"

if [[ -z "${IMAGE:-}" || -z "${REGISTRY_USER:-}" || -n "${EXTRA:-}" ]]; then
    echo "usage (as SSH command): <image> <registry user>, with the registry token on stdin" >&2
    exit 2
fi
TAG=${IMAGE#"$IMAGE_REPOSITORY":}
if [[ $IMAGE != "$IMAGE_REPOSITORY":* || ! $TAG =~ $TAG_PATTERN || ! $REGISTRY_USER =~ $USER_PATTERN ]]; then
    echo "refusing: the image must be $IMAGE_REPOSITORY:<tag> and the registry user must be a plain name" >&2
    exit 2
fi

start_container() {
    docker rm -f "$CONTAINER" > /dev/null 2>&1 || true
    docker run -d --name "$CONTAINER" --restart unless-stopped \
        --env-file "$ENV_FILE" -v "$DATA_VOLUME":/app/data "$1" > /dev/null
}

# "Application started" is logged only after the configuration (including Key Vault) has been read, so a wrong
# secret or an unreachable vault shows up here as an exit or a restart loop, not as a silent success.
wait_until_started() {
    local deadline=$((SECONDS + START_TIMEOUT_SECONDS)) running restarts logs
    while ((SECONDS < deadline)); do
        sleep 2
        read -r running restarts < <(docker inspect --format '{{.State.Running}} {{.RestartCount}}' "$CONTAINER")
        if [[ $running != true || $restarts != 0 ]]; then
            return 1
        fi
        logs=$(docker logs "$CONTAINER" 2>&1 || true)
        if [[ $logs == *"Application started"* ]]; then
            return 0
        fi
    done
    return 1
}

trap 'docker logout ghcr.io > /dev/null 2>&1 || true' EXIT

docker login ghcr.io --username "$REGISTRY_USER" --password-stdin
docker pull --quiet "$IMAGE"

PREVIOUS_IMAGE=$(docker inspect --format '{{.Config.Image}}' "$CONTAINER" 2> /dev/null || true)
echo "Deploying $IMAGE (previous: ${PREVIOUS_IMAGE:-none})"

start_container "$IMAGE"
if wait_until_started; then
    echo "The new container is up."
    docker image prune -af --filter "until=168h" > /dev/null
    exit 0
fi

echo "The new container did not start properly. Last log lines:" >&2
docker logs --tail 30 "$CONTAINER" >&2 || true

if [[ -n $PREVIOUS_IMAGE && $PREVIOUS_IMAGE != "$IMAGE" ]]; then
    echo "Rolling back to $PREVIOUS_IMAGE" >&2
    start_container "$PREVIOUS_IMAGE"
fi
exit 1
