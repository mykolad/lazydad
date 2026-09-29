#!/usr/bin/env bash
# shellcheck disable=SC2016  # jq programs are single-quoted on purpose: their $variables are jq's, not the shell's.
# Cloudflare's published IP ranges (https://www.cloudflare.com/ips/), behind the prod app's ingress rules and the
# app's own list (Cloudflare:IpRanges in appsettings.json). See infra/deployment-setup.md, section 10.
#
#   cloudflare-ranges.sh fetch [4|6]
#       Prints Cloudflare's current ranges (IPv4, IPv6, or both), one per line, checked for a sane format.
#   cloudflare-ranges.sh check <appsettings.json>
#       Compares the app's list with Cloudflare's. Exit 0: the same; 1: they differ (what to add and remove is
#       printed); 2: Cloudflare's list couldn't be read.
#   cloudflare-ranges.sh sync <app> <resource-group> <rule-tag> on|off
#       on (the environment's CLOUDFLARE_ONLY_INGRESS is true): the Container App admits only Cloudflare. Its
#       cloudflare-* ingress rules are made to match Cloudflare's IPv4 ranges (Container Apps' ingress is IPv4),
#       all of them created if there are none yet. Missing ranges are added (as cloudflare-<rule-tag>-<n>) and ranges
#       Cloudflare no longer lists are removed in one update of the whole list (a PATCH), so it changes all at once:
#       the app never admits only part of Cloudflare. It refuses to remove more than 3 ranges at once (it then only
#       adds). Rules that aren't cloudflare-* (staging's home, a deploy's runner) are kept.
#       off: any cloudflare-* rules are removed, so the app's own address is open again (with no other Allow rule).
set -euo pipefail

api=https://api.cloudflare.com/client/v4/ips
# The Container Apps API version for the one-update ingress PATCH (apply_rules).
api_version=2026-01-01

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

# jq's compact JSON output, without Windows carriage returns (see jq_text).
jq_json() {
  jq -c "$@" | tr -d '\r'
}

# Writes the app's whole list of ingress rules in one update: a PATCH of just that property (a JSON merge patch, so
# the rest of the app, its secrets included, stays as it is; a full PUT would need them). Azure applies the list as a
# whole, so there's no moment with only some of the new rules. The PATCH finishes asynchronously: wait until the app
# reports the new list.
apply_rules() {
  local app=$1 group=$2 id=$3 desired=$4 body state count expected
  body=$(mktemp ./ingress-rules-XXXXXX.json)
  jq -n --argjson rules "$desired" '{properties: {configuration: {ingress: {ipSecurityRestrictions: $rules}}}}' > "$body"
  az rest --method patch --url "https://management.azure.com$id?api-version=$api_version" --body "@$body" -o none
  rm -f "$body"
  expected=$(jq 'length' <<< "$desired" | tr -d '\r')
  for _ in $(seq 1 60); do
    # By name, not --ids: Git Bash would rewrite a /subscriptions/... argument as a file path.
    state=$(az containerapp show -n "$app" -g "$group" --query properties.provisioningState -o tsv | tr -d '\r')
    count=$(az containerapp show -n "$app" -g "$group" \
      --query "length(properties.configuration.ingress.ipSecurityRestrictions || \`[]\`)" -o tsv | tr -d '\r')
    if [ "$state" = Succeeded ] && [ "$count" = "$expected" ]; then
      return 0
    fi
    sleep 5
  done
  echo "The rules update didn't finish in 5 minutes (state '$state', $count rules, expected $expected)." >&2
  return 1
}

sync() {
  local app=$1 group=$2 tag=$3 mode=${4:-} max_removals=3
  local id current cloudflare others wanted wanted_json have missing added kept obsolete desired i=0 range
  case "$mode" in
    on | off) ;;
    *) echo "sync: the mode must be on or off, not '$mode'" >&2; return 2 ;;
  esac
  id=$(az containerapp show -n "$app" -g "$group" --query id -o tsv | tr -d '\r')
  current=$(az containerapp show -n "$app" -g "$group" \
    --query "properties.configuration.ingress.ipSecurityRestrictions || \`[]\`" -o json | jq_json .)
  # Only cloudflare-* rules are this script's; any other rule (staging's home, a deploy's runner) is kept as it is.
  cloudflare=$(jq_json '[.[] | select(.name | startswith("cloudflare-"))]' <<< "$current")
  others=$(jq_json '[.[] | select(.name | startswith("cloudflare-") | not)]' <<< "$current")

  if [ "$mode" = off ]; then
    if [ "$(jq 'length' <<< "$cloudflare" | tr -d '\r')" = 0 ]; then
      echo "$app isn't locked to Cloudflare (CLOUDFLARE_ONLY_INGRESS isn't true), and has no cloudflare-* rules."
      return 0
    fi
    # Turning the lock off: every Cloudflare rule goes, and with no Allow rule left the app admits every address.
    echo "::warning::CLOUDFLARE_ONLY_INGRESS isn't true: removing $app's cloudflare-* rules, so its own address is open again."
    apply_rules "$app" "$group" "$id" "$others"
    jq_text '.[] | "Removed \(.ipAddressRange) (\(.name))."' <<< "$cloudflare"
    return 0
  fi

  # On: from here, the app admits only Cloudflare.
  wanted=$(ranges 4)
  wanted_json=$(jq -R . <<< "$wanted" | jq_json -s .)
  have=$(jq_text '.[].ipAddressRange' <<< "$cloudflare" | sort -u)
  missing=$(comm -23 <(echo "$wanted") <(echo "$have") | grep . || true)
  added=$(for range in $missing; do
    i=$((i + 1))
    jq -n --arg name "cloudflare-$tag-$i" --arg range "$range" \
      '{name: $name, ipAddressRange: $range, action: "Allow", description: ("Cloudflare " + $range)}'
  done | jq_json -s .)
  kept=$(jq_json --argjson wanted "$wanted_json" '[.[] | select(.ipAddressRange as $r | $wanted | index($r))]' <<< "$cloudflare")
  obsolete=$(jq_json --argjson wanted "$wanted_json" '[.[] | select(.ipAddressRange as $r | $wanted | index($r) | not)]' <<< "$cloudflare")

  # Cloudflare's changes are small; removing more than a few ranges at once means something is off. The additions
  # still go in (they only admit more), but nothing is removed until a person has looked.
  local too_many=0
  if [ "$(jq 'length' <<< "$obsolete" | tr -d '\r')" -gt "$max_removals" ]; then
    too_many=1
    kept=$cloudflare
    obsolete='[]'
  fi
  if [ "$(jq 'length' <<< "$added" | tr -d '\r')" = 0 ] && [ "$(jq 'length' <<< "$obsolete" | tr -d '\r')" = 0 ]; then
    echo "$app's Cloudflare rules match Cloudflare's $(grep -c . <<< "$wanted") IPv4 ranges."
  else
    if [ "$(jq 'length' <<< "$cloudflare" | tr -d '\r')" = 0 ]; then
      echo "Locking $app to Cloudflare: adding a rule per IPv4 range, in one update."
    fi
    desired=$(jq_json -n --argjson a "$others" --argjson b "$kept" --argjson c "$added" '$a + $b + $c')
    apply_rules "$app" "$group" "$id" "$desired"
    jq_text '.[] | "Added \(.ipAddressRange) (\(.name))."' <<< "$added"
    jq_text '.[] | "Removed \(.ipAddressRange) (\(.name)): Cloudflare no longer lists it."' <<< "$obsolete"
  fi
  if [ "$too_many" = 1 ]; then
    echo "Refusing to remove more than $max_removals ranges at once; remove these by hand if Cloudflare really dropped them:" >&2
    jq_text --argjson wanted "$wanted_json" '.[] | select(.ipAddressRange as $r | $wanted | index($r) | not) | "  \(.name)\t\(.ipAddressRange)"' <<< "$cloudflare" >&2
    return 1
  fi
}

command=${1:-}
shift || true
case "$command" in
  fetch) fetch "$@" ;;
  check) check "$@" ;;
  sync) sync "$@" ;;
  *) sed -n '3,18p' "$0" >&2; exit 2 ;;
esac
