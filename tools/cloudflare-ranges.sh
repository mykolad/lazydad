#!/usr/bin/env bash
# Cloudflare's published IP ranges (https://www.cloudflare.com/ips/), behind the prod app's ingress rules and the
# app's own list (Cloudflare:IpRanges in appsettings.json). See infra/deployment-setup.md, section 12.
#
#   cloudflare-ranges.sh fetch [4|6]
#       Prints Cloudflare's current ranges (IPv4, IPv6, or both), one per line, checked for a sane format.
#   cloudflare-ranges.sh check <appsettings.json>
#       Compares the app's list with Cloudflare's. Exit 0: the same; 1: they differ (what to add and remove is
#       printed); 2: Cloudflare's list couldn't be read.
#   cloudflare-ranges.sh sync <app> <resource-group> <rule-tag> on|off
#       on (the environment's CLOUDFLARE_ONLY_INGRESS is true): the Container App admits only Cloudflare. Its
#       cloudflare-* ingress rules are made to match Cloudflare's IPv4 ranges (Container Apps' ingress is IPv4),
#       all of them created if there are none yet: missing ranges are added first, as cloudflare-<rule-tag>-<n>,
#       and only then are ranges Cloudflare no longer lists removed, so the app never admits less of Cloudflare
#       than it should. It refuses to remove more than 3 ranges at once.
#       off: any cloudflare-* rules are removed, so the app's own address is open again (with no other Allow rule).
set -euo pipefail

api=https://api.cloudflare.com/client/v4/ips

# jq's text output, without the carriage returns jq adds on Windows (Git Bash); a no-op on Linux runners.
jq_text() {
  jq -r "$@" | tr -d '\r'
}

# Cloudflare's ranges of one family (4 or 6), validated: a failed or odd answer must never remove rules.
ranges() {
  local family=$1 json list minimum pattern
  json=$(curl -fsS --retry 3 --max-time 30 "$api")
  if [ "$(jq_text '.success' <<< "$json")" != true ]; then
    echo "Cloudflare's API didn't report success: $json" >&2
    return 1
  fi
  # Duplicates removed before counting: the same range ten times must not pass as ten ranges.
  if [ "$family" = 4 ]; then
    list=$(jq_text '.result.ipv4_cidrs[]' <<< "$json" | sort -u)
    pattern='^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$'
    minimum=10
  else
    list=$(jq_text '.result.ipv6_cidrs[]' <<< "$json" | sort -u)
    pattern='^[0-9a-f:]+/[0-9]{1,3}$'
    minimum=3
  fi
  if [ "$(grep -c . <<< "$list")" -lt "$minimum" ] || grep -vqE "$pattern" <<< "$list"; then
    echo "Cloudflare's IPv$family list looks wrong, not using it:" >&2
    echo "$list" >&2
    return 1
  fi
  echo "$list"
}

fetch() {
  local v4 v6
  case "${1:-both}" in
    4 | 6) ranges "$1" ;;
    both)
      # Each family on its own: a failure of either must fail the whole fetch, not leave a partial list.
      v4=$(ranges 4) || return 1
      v6=$(ranges 6) || return 1
      printf '%s\n%s\n' "$v4" "$v6" | sort -u
      ;;
    *) echo "fetch: 4, 6 or nothing" >&2; return 2 ;;
  esac
}

check() {
  local settings=$1 cloudflare app
  if ! cloudflare=$(fetch both); then
    return 2
  fi
  app=$(jq_text '.Cloudflare.IpRanges[]' "$settings" | sort -u)
  if [ "$cloudflare" = "$app" ]; then
    echo "The app's list matches Cloudflare's ($(grep -c . <<< "$app") ranges)."
    return 0
  fi
  echo "The app's list ($settings, Cloudflare:IpRanges) differs from Cloudflare's published ranges."
  echo "Missing (Cloudflare lists them, the app doesn't trust them yet):"
  comm -23 <(echo "$cloudflare") <(echo "$app") | sed 's/^/  + /'
  echo "No longer listed by Cloudflare:"
  comm -13 <(echo "$cloudflare") <(echo "$app") | sed 's/^/  - /'
  return 1
}

sync() {
  local app=$1 group=$2 tag=$3 mode=${4:-} rules wanted have obsolete range name i=0 changed=0 max_removals=3
  case "$mode" in
    on | off) ;;
    *) echo "sync: the mode must be on or off, not '$mode'" >&2; return 2 ;;
  esac
  rules=$(az containerapp ingress access-restriction list -n "$app" -g "$group" \
    --query "[?starts_with(name, 'cloudflare-')].[name, ipAddressRange]" -o tsv | tr -d '\r')
  if [ "$mode" = off ]; then
    if [ -z "$rules" ]; then
      echo "$app isn't locked to Cloudflare (CLOUDFLARE_ONLY_INGRESS isn't true), and has no cloudflare-* rules."
      return 0
    fi
    # Turning the lock off: every Cloudflare rule goes, and with no Allow rule left the app admits every address.
    echo "::warning::CLOUDFLARE_ONLY_INGRESS isn't true: removing $app's cloudflare-* rules, so its own address is open again."
    while IFS=$'\t' read -r name range; do
      az containerapp ingress access-restriction remove -n "$app" -g "$group" --rule-name "$name" -o none
      echo "Removed $range ($name)."
    done <<< "$rules"
    return 0
  fi
  # On: from here, the app admits only Cloudflare. Without rules yet, all of Cloudflare's ranges are added (the
  # first Allow rule denies everyone else, so for the seconds this takes, not all of Cloudflare gets through).
  wanted=$(ranges 4)
  if [ -z "$rules" ]; then
    echo "Locking $app to Cloudflare: adding a rule per IPv4 range."
  fi
  have=$(cut -f2 <<< "$rules" | sort -u)
  # Add first: while this runs, every range Cloudflare uses stays admitted.
  for range in $wanted; do
    if ! grep -qxF "$range" <<< "$have"; then
      i=$((i + 1))
      az containerapp ingress access-restriction set -n "$app" -g "$group" --rule-name "cloudflare-$tag-$i" \
        --ip-address "$range" --action Allow --description "Cloudflare $range" -o none
      echo "Added $range (cloudflare-$tag-$i)."
      changed=1
    fi
  done
  # Cloudflare's changes are small; removing more than a few ranges at once means something is off. The additions
  # above stand (they only admit more), but nothing is removed until a person has looked.
  obsolete=""
  if [ -n "$rules" ]; then
    obsolete=$(while IFS=$'\t' read -r name range; do
      grep -qxF "$range" <<< "$wanted" || printf '%s\t%s\n' "$name" "$range"
    done <<< "$rules")
  fi
  if [ "$(grep -c . <<< "$obsolete")" -gt "$max_removals" ]; then
    echo "Refusing to remove $(grep -c . <<< "$obsolete") ranges at once (at most $max_removals); remove them by hand if Cloudflare really dropped them:" >&2
    echo "$obsolete" >&2
    return 1
  fi
  while IFS=$'\t' read -r name range; do
    [ -n "$name" ] || continue
    az containerapp ingress access-restriction remove -n "$app" -g "$group" --rule-name "$name" -o none
    echo "Removed $range ($name): Cloudflare no longer lists it."
    changed=1
  done <<< "$obsolete"
  if [ "$changed" = 0 ]; then
    echo "$app's Cloudflare rules match Cloudflare's $(grep -c . <<< "$wanted") IPv4 ranges."
  fi
}

command=${1:-}
shift || true
case "$command" in
  fetch) fetch "$@" ;;
  check) check "$@" ;;
  sync) sync "$@" ;;
  *) sed -n '2,16p' "$0" >&2; exit 2 ;;
esac
