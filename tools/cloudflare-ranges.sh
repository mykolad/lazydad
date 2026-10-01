#!/usr/bin/env bash
# shellcheck disable=SC2016  # jq programs are single-quoted on purpose: their $variables are jq's, not the shell's.
# Cloudflare's published IP ranges (https://www.cloudflare.com/ips/), behind the prod apps' ingress rules and the
# app's own list (Cloudflare:IpRanges in appsettings.json), and the addresses Traffic Manager's health checks come from.
# See infra/deployment-setup.md, section 10.
#
#   cloudflare-ranges.sh fetch [4|6]
#       Prints Cloudflare's current ranges (IPv4, IPv6, or both), one per line, checked for a sane format.
#   cloudflare-ranges.sh check <appsettings.json>
#       Compares the app's list with Cloudflare's. Exit 0: the same; 1: they differ (what to add and remove is
#       printed); 2: Cloudflare's list couldn't be read.
#   cloudflare-ranges.sh sync <app> <resource-group> <rule-tag> on|off
#       on (the environment's CLOUDFLARE_ONLY_INGRESS is true): the Container App admits only Cloudflare, and Traffic
#       Manager's health checks. Two sets of ingress rules are made to match two IPv4 lists (Container Apps' ingress is
#       IPv4), all of them created if there are none yet: cloudflare-* Cloudflare's ranges, trafficmanager-* the
#       AzureTrafficManager service tag (reading it needs an Azure login). Missing ranges are added (as
#       <prefix>-<rule-tag>-<n>, at most 32 characters: a long tag becomes a short hash) and ranges no longer listed
#       are removed in one update of the whole list (a PATCH), so it changes all at once: the app never admits only
#       part of Cloudflare. It refuses to remove more than 3 of Cloudflare's or 50 of Traffic Manager's ranges at once
#       (it then only adds). Other rules (staging's home, a deploy's runner) are kept. If either list can't be read,
#       nothing changes.
#       off: any cloudflare-* and trafficmanager-* rules are removed, so the app's own address is open again (with no
#       other Allow rule).
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
  jq '{properties: {configuration: {ingress: {ipSecurityRestrictions: .}}}}' <<< "$desired" > "$body"
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

# Traffic Manager's health checks come from these addresses: the AzureTrafficManager service tag, one list for every
# region (reading it needs an Azure login). Validated like Cloudflare's: a failed or odd answer must never remove rules.
traffic_manager_ranges() {
  local list
  if ! list=$(az network list-service-tags --location westeurope \
    --query "values[?name=='AzureTrafficManager'].properties.addressPrefixes[]" -o tsv | tr -d '\r' | grep -v ':' | sort -u); then
    echo "Couldn't read Traffic Manager's addresses (the AzureTrafficManager service tag)." >&2
    return 1
  fi
  if [ "$(grep -c . <<< "$list")" -lt 100 ] || grep -vqE '^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$' <<< "$list"; then
    echo "Traffic Manager's IPv4 list looks wrong, not using it:" >&2
    echo "$list" >&2
    return 1
  fi
  echo "$list"
}

# What to change in one list's rules. $1: its rule prefix, $2: its name, $3: the most ranges it may remove at once,
# $4: the rule tag, $5: its ranges (one per line), $6: the app's current rules (JSON). Prints a JSON object: keep (rules
# to leave), add (new rules), remove (rules whose range is no longer listed) and held (removals refused: more than $3 at
# once means something is off, so they stay until a person has looked; the additions still go in, they only admit more).
# The lists go through stdin, not arguments: Windows limits a command line to 32K characters.
plan() {
  { echo "$6"; jq -R . <<< "$5" | jq_json -s 'map(select(length > 0))'; } |
  jq_json -s --arg prefix "$1-" --arg label "$2" --argjson max "$3" --arg tag "$4" '
    .[0] as $current | .[1] as $wanted
    | [$current[] | select(.name | startswith($prefix))] as $mine
    | [$mine[] | select(.ipAddressRange as $r | any($wanted[]; . == $r))] as $listed
    | [$mine[] | select(.ipAddressRange as $r | any($wanted[]; . == $r) | not)] as $obsolete
    | ($obsolete | length > $max) as $hold
    | {
        label: $label,
        max: $max,
        count: ($wanted | length),
        existing: ($mine | length),
        keep: (if $hold then $mine else $listed end),
        add: [$wanted - ($mine | map(.ipAddressRange)) | to_entries[]
              | {name: "\($prefix)\($tag)-\(.key + 1)", ipAddressRange: .value, action: "Allow", description: "\($label) \(.value)"}
              # Container Apps refuses the whole update if any name is longer (see name_tag); never send one.
              | if (.name | length) > 32 then error("rule name \(.name) is longer than 32 characters") else . end],
        remove: (if $hold then [] else $obsolete end),
        held: (if $hold then $obsolete else [] end)
      }'
}

# The tag in new rules' names. Container Apps allows rule names of at most 32 characters, and the longest name is
# trafficmanager-<tag>-<n> with n up to 3 digits, so a tag longer than 13 characters (a deploy's "r<run ID>-<attempt>"
# is 14) becomes the first 10 hex digits of its SHA-1: still different for every sync.
name_tag() {
  if [ "${#1}" -le 13 ]; then
    echo "$1"
  else
    printf '%s' "$1" | sha1sum | cut -c1-10
  fi
}

sync() {
  local app=$1 group=$2 tag=$3 mode=${4:-}
  local id current mine others cloudflare traffic_manager plans desired
  case "$mode" in
    on | off) ;;
    *) echo "sync: the mode must be on or off, not '$mode'" >&2; return 2 ;;
  esac
  id=$(az containerapp show -n "$app" -g "$group" --query id -o tsv | tr -d '\r')
  current=$(az containerapp show -n "$app" -g "$group" \
    --query "properties.configuration.ingress.ipSecurityRestrictions || \`[]\`" -o json | jq_json .)
  # Only cloudflare-* and trafficmanager-* rules are this script's; any other rule (staging's home, a deploy's runner)
  # is kept as it is.
  mine=$(jq_json '[.[] | select(.name | startswith("cloudflare-") or startswith("trafficmanager-"))]' <<< "$current")
  others=$(jq_json '[.[] | select(.name | startswith("cloudflare-") or startswith("trafficmanager-") | not)]' <<< "$current")

  if [ "$mode" = off ]; then
    if [ "$(jq 'length' <<< "$mine" | tr -d '\r')" = 0 ]; then
      echo "$app isn't locked to Cloudflare (CLOUDFLARE_ONLY_INGRESS isn't true), and has no cloudflare-* or trafficmanager-* rules."
      return 0
    fi
    # Turning the lock off: all of these rules go, and with no Allow rule left the app admits every address.
    echo "::warning::CLOUDFLARE_ONLY_INGRESS isn't true: removing $app's cloudflare-* and trafficmanager-* rules, so its own address is open again."
    apply_rules "$app" "$group" "$id" "$others"
    jq_text '.[] | "Removed \(.ipAddressRange) (\(.name))."' <<< "$mine"
    return 0
  fi

  # On: from here, the app admits only Cloudflare, and Traffic Manager's health checks (runbook section 10). Traffic
  # Manager's list is long (about 200 addresses) and changes without notice, so more removals at once are normal there.
  cloudflare=$(ranges 4)
  traffic_manager=$(traffic_manager_ranges)
  plans=$({
    plan cloudflare Cloudflare 3 "$(name_tag "$tag")" "$cloudflare" "$current"
    plan trafficmanager "Traffic Manager" 50 "$(name_tag "$tag")" "$traffic_manager" "$current"
  } | jq_json -s .)
  if [ "$(jq '[.[] | (.add + .remove) | length] | add' <<< "$plans" | tr -d '\r')" = 0 ]; then
    jq_text --arg app "$app" '"\($app)'"'"'s rules match " + ([.[] | "\(.label) (\(.count) IPv4 ranges)"] | join(" and ")) + "."' <<< "$plans"
  else
    if [ "$(jq 'length' <<< "$mine" | tr -d '\r')" = 0 ]; then
      echo "Locking $app to Cloudflare (and Traffic Manager's health checks): adding a rule per IPv4 range, in one update."
    fi
    desired=$(printf '%s\n%s\n' "$others" "$plans" | jq_json -s '.[0] + [.[1][] | .keep[], .add[]]')
    apply_rules "$app" "$group" "$id" "$desired"
    jq_text '.[] | .label as $label
      | (.add[] | "Added \(.ipAddressRange) (\(.name))."),
        (.remove[] | "Removed \(.ipAddressRange) (\(.name)): \($label) no longer lists it.")' <<< "$plans"
  fi
  if [ "$(jq '[.[] | .held | length] | add' <<< "$plans" | tr -d '\r')" != 0 ]; then
    jq_text '.[] | select(.held | length > 0)
      | "Refusing to remove more than \(.max) of the \(.label) ranges at once; remove these by hand if \(.label) really dropped them:",
        (.held[] | "  \(.name)\t\(.ipAddressRange)")' <<< "$plans" >&2
    return 1
  fi
}

command=${1:-}
shift || true
case "$command" in
  fetch) fetch "$@" ;;
  check) check "$@" ;;
  sync) sync "$@" ;;
  *) sed -n '3,23p' "$0" >&2; exit 2 ;;
esac
