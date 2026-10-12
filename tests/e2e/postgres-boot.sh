#!/usr/bin/env bash
# Boots the published Librarr binary against a real PostgreSQL server and
# checks that it actually works there.
#
# The DbTest fixtures prove individual queries; this proves the thing users
# run: the real host applying every migration to empty main/log/cache
# databases, then serving its API and running housekeeping on top of them.
#
# Expects the Readarr__Postgres__{Host,Port,User,Password} variables to be
# set (the app reads the same ones), and the three databases
# readarr-main / readarr-log / readarr-cache to exist already: the app
# migrates databases, it does not create them.
#
# Usage: tests/e2e/postgres-boot.sh
#   READARR_BIN   binary to launch (default: the linux-x64 publish output)
#   LIBRARR_PORT  port to probe     (default: 8787)

set -euo pipefail

: "${Readarr__Postgres__Host:?Readarr__Postgres__Host must be set, or this would silently test SQLite}"

READARR_BIN="${READARR_BIN:-_output/net10.0/linux-x64/publish/Readarr}"
LIBRARR_PORT="${LIBRARR_PORT:-8787}"
HOST="http://localhost:${LIBRARR_PORT}"
DATA="$(mktemp -d -t librarr-postgres.XXXXXX)"
LOG="${DATA}/console.log"

# The newest migration in the tree is what a fully migrated database must
# report. Derived rather than hard-coded so adding a migration cannot leave
# this check asserting a stale number.
EXPECTED_MIGRATION=$(find src/NzbDrone.Core/Datastore/Migration -maxdepth 1 -name '[0-9]*_*.cs' \
  | sed -n 's|.*/0*\([0-9][0-9]*\)_.*|\1|p' | sort -n | tail -1)
[[ -n "${EXPECTED_MIGRATION}" ]] || { echo "could not determine the latest migration number" >&2; exit 1; }

echo "Data dir: ${DATA}; expecting migration ${EXPECTED_MIGRATION}"
"${READARR_BIN}" -data="${DATA}" -nobrowser >"${LOG}" 2>&1 &
PID=$!

cleanup() {
  kill "${PID}" 2>/dev/null || true
  wait "${PID}" 2>/dev/null || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  echo "----- last 200 lines of console log -----" >&2
  tail -200 "${LOG}" >&2 || true
  exit 1
}

for _ in $(seq 1 120); do
  curl -fsS "${HOST}/ping" >/dev/null 2>&1 && break
  kill -0 "${PID}" 2>/dev/null || fail "the process exited before answering /ping"
  sleep 1
done
curl -fsS "${HOST}/ping" >/dev/null 2>&1 || fail "never answered ${HOST}/ping"

API_KEY=$(sed -n 's/.*<ApiKey>\(.*\)<\/ApiKey>.*/\1/p' "${DATA}/config.xml")
[[ -n "${API_KEY}" ]] || fail "no ApiKey in ${DATA}/config.xml"

api() {
  curl -sS -H "X-Api-Key: ${API_KEY}" -H 'Content-Type: application/json' "$@"
}

STATUS=$(api -f "${HOST}/api/v1/system/status") || fail "system/status did not return 2xx"
DB_TYPE=$(jq -r '.databaseType' <<<"${STATUS}")
DB_VERSION=$(jq -r '.databaseVersion' <<<"${STATUS}")
MIGRATION=$(jq -r '.migrationVersion' <<<"${STATUS}")
echo "databaseType=${DB_TYPE} databaseVersion=${DB_VERSION} migrationVersion=${MIGRATION}"

shopt -s nocasematch
[[ "${DB_TYPE}" == postgres* ]] || fail "expected a postgres database, the app reports '${DB_TYPE}'"
shopt -u nocasematch
[[ "${MIGRATION}" == "${EXPECTED_MIGRATION}" ]] || fail "expected migration ${EXPECTED_MIGRATION}, the app reports ${MIGRATION}"

# No SQLite file may exist: if one does, some code path ignored the
# PostgreSQL configuration and quietly went its own way.
if compgen -G "${DATA}/*.db" >/dev/null; then
  fail "SQLite database files were created in ${DATA}: $(ls "${DATA}"/*.db)"
fi

# Read endpoints. The tables are empty, but each of these makes the server
# parse and plan the query, which is where a quoting or type mistake that
# SQLite tolerates shows up.
ENDPOINTS=(
  "author"
  "book"
  "wanted/missing?page=1&pageSize=10&sortKey=releaseDate&sortDirection=descending"
  "wanted/cutoff?page=1&pageSize=10&sortKey=releaseDate&sortDirection=descending"
  "history?page=1&pageSize=10&sortKey=date&sortDirection=descending"
  "blocklist?page=1&pageSize=10&sortKey=date&sortDirection=descending"
  "queue?page=1&pageSize=10"
  "calendar"
  "qualityprofile"
  "metadataprofile"
  "rootfolder"
  "tag"
  "indexer"
  "downloadclient"
  "notification"
  "importlist"
  "customformat"
  "system/task"
  "health"
  "log?page=1&pageSize=10&sortKey=time&sortDirection=descending"
)
BAD=0
for ep in "${ENDPOINTS[@]}"; do
  code=$(api -o "${DATA}/body.json" -w '%{http_code}' "${HOST}/api/v1/${ep}")
  if [[ "${code}" != 2* ]]; then
    echo "  ${code}  GET /api/v1/${ep}" >&2
    head -c 2000 "${DATA}/body.json" >&2 || true
    echo >&2
    BAD=1
  else
    echo "  ${code}  GET /api/v1/${ep}"
  fi
done
[[ "${BAD}" == 0 ]] || fail "one or more read endpoints failed on PostgreSQL"

# A write and a delete through the real repository layer.
TAG_ID=$(api -f -X POST -d '{"label":"postgres-smoke"}' "${HOST}/api/v1/tag" | jq -r '.id') \
  || fail "could not create a tag"
[[ "${TAG_ID}" =~ ^[0-9]+$ ]] || fail "tag creation returned no id"
api -f -X DELETE "${HOST}/api/v1/tag/${TAG_ID}" >/dev/null || fail "could not delete tag ${TAG_ID}"

# Housekeeping runs every housekeeper's hand-written SQL, which is where the
# engine-specific branches live. A housekeeper that throws is caught and
# logged rather than failing the command, so the log is checked as well.
CMD_ID=$(api -f -X POST -d '{"name":"Housekeeping"}' "${HOST}/api/v1/command" | jq -r '.id') \
  || fail "could not queue the Housekeeping command"
CMD_STATUS=""
for _ in $(seq 1 120); do
  CMD_STATUS=$(api -f "${HOST}/api/v1/command/${CMD_ID}" | jq -r '.status')
  case "${CMD_STATUS}" in
    completed|failed|aborted|cancelled|orphaned) break ;;
  esac
  sleep 1
done
echo "Housekeeping command: ${CMD_STATUS}"
[[ "${CMD_STATUS}" == "completed" ]] || fail "Housekeeping ended as '${CMD_STATUS}'"

echo "----- warnings and errors logged during the run -----"
grep -E '^\[(Warn|Error|Fatal)\]' "${LOG}" || echo "(none)"

# Deliberately narrower than "any error": the app also talks to the network
# at startup (update check, OpenLibrary health), and a third party being
# down must not redden a database job.
if grep -qE 'Error running housekeeping task' "${LOG}"; then
  fail "a housekeeping task threw on PostgreSQL"
fi
if grep -qE 'Npgsql|PostgresException' "${LOG}"; then
  fail "the app logged a database error while running on PostgreSQL"
fi

echo "PostgreSQL boot check passed."
