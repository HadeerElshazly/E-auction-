#!/usr/bin/env bash
# Starts a single-node Kafka broker in KRaft mode, with no Docker.
#
# Exists because the Kafka integration tests need a real broker and the
# development container has no Docker daemon. Java is enough.
#
#   ./tools/kafka/run-local-broker.sh start
#   KAFKA_BOOTSTRAP=127.0.0.1:9092 dotnet test tests/EAuction.Kafka.Tests
#   ./tools/kafka/run-local-broker.sh stop
#
# Without KAFKA_BOOTSTRAP those tests skip rather than fail, so the rest of
# the suite still runs anywhere.
set -euo pipefail

VERSION="${KAFKA_VERSION:-3.8.1}"
SCALA="2.13"
ROOT="${KAFKA_HOME:-/tmp/kafka}"
DATA="${KAFKA_DATA:-/tmp/kafka-logs}"
CONF="$DATA/kraft.properties"
LOG="${KAFKA_LOG:-/tmp/kafka.log}"

install_kafka() {
  [ -x "$ROOT/bin/kafka-server-start.sh" ] && return
  echo "==> downloading Kafka $VERSION"
  local tgz="/tmp/kafka-$VERSION.tgz"
  curl -sSL --retry 2 -o "$tgz" \
    "https://archive.apache.org/dist/kafka/$VERSION/kafka_${SCALA}-${VERSION}.tgz"
  tar xzf "$tgz" -C /tmp
  rm -rf "$ROOT"
  mv "/tmp/kafka_${SCALA}-${VERSION}" "$ROOT"
}

write_config() {
  mkdir -p "$DATA"
  cat > "$CONF" <<EOF
process.roles=broker,controller
node.id=1
controller.quorum.voters=1@127.0.0.1:9093
listeners=PLAINTEXT://127.0.0.1:9092,CONTROLLER://127.0.0.1:9093
advertised.listeners=PLAINTEXT://127.0.0.1:9092
controller.listener.names=CONTROLLER
listener.security.protocol.map=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
log.dirs=$DATA
num.partitions=1
offsets.topic.replication.factor=1
transaction.state.log.replication.factor=1
transaction.state.log.min.isr=1
# As in deployment: topics are created by the approval workflow before an
# auction is published, never implicitly (architecture section 5).
auto.create.topics.enable=false
log.initial.task.delay.ms=100
EOF
}

case "${1:-start}" in
  start)
    install_kafka
    if JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-topics.sh" \
         --bootstrap-server 127.0.0.1:9092 --list >/dev/null 2>&1; then
      echo "broker already running"
      exit 0
    fi

    rm -rf "$DATA"
    write_config
    CLUSTER_ID=$(JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-storage.sh" random-uuid)
    JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-storage.sh" format -t "$CLUSTER_ID" -c "$CONF" >/dev/null

    echo "==> starting broker (cluster $CLUSTER_ID)"
    JAVA_TOOL_OPTIONS="" KAFKA_HEAP_OPTS="-Xmx1G -Xms512M" \
      nohup "$ROOT/bin/kafka-server-start.sh" "$CONF" > "$LOG" 2>&1 &

    for _ in $(seq 1 30); do
      if JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-topics.sh" \
           --bootstrap-server 127.0.0.1:9092 --list >/dev/null 2>&1; then
        echo "broker ready on 127.0.0.1:9092"
        exit 0
      fi
      sleep 2
    done

    echo "broker did not come up; see $LOG" >&2
    exit 1
    ;;

  stop)
    JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-server-stop.sh" 2>/dev/null || true
    echo "stopped"
    ;;

  status)
    if JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-topics.sh" \
         --bootstrap-server 127.0.0.1:9092 --list >/dev/null 2>&1; then
      echo "running"
      JAVA_TOOL_OPTIONS="" "$ROOT/bin/kafka-topics.sh" \
        --bootstrap-server 127.0.0.1:9092 --list | head -20
    else
      echo "not running"
      exit 1
    fi
    ;;

  *)
    echo "usage: $0 {start|stop|status}" >&2
    exit 2
    ;;
esac
