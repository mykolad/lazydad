# Fail fast: stop on the first error, including a non-zero exit from az/docker
# (PowerShell 7.3+), so a failed login, build or push never reaches the app update.
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$ACR_NAME   = "lazydadacr"
$RG         = "lazydad-rg"
# Production runs in two regions; each app's protection tags are deployed-/previous-<target> (see Deploy Environment).
$APPS       = @{ App = "lazydad-app"; Target = "production" },
              @{ App = "lazydad-app-swedencentral"; Target = "production-swedencentral" }
$ACR_SERVER = az acr show --name $ACR_NAME --query loginServer -o tsv

az acr login --name $ACR_NAME

$TAG = "lazydad:$(git rev-parse --short HEAD)"
# Same version metadata Deploy Master bakes in (see Dockerfile).
docker build -t "$ACR_SERVER/$TAG" `
    --build-arg VERSION=$(git rev-parse --short HEAD) `
    --build-arg REVISION=$(git rev-parse HEAD) `
    --build-arg COMMIT_DATE=$(git show -s --format=%cI HEAD) `
    --build-arg SOURCE_URL=https://github.com/mykolad/lazydad `
    .
docker push "$ACR_SERVER/$TAG"
# The revisions are pinned to the digest, not the commit tag: Container Apps pulls the image again on every replica
# start, and the weekly purge deletes old commit tags (runbook section 2).
$DIGEST = az acr manifest show-metadata -r $ACR_NAME -n $TAG --query digest -o tsv

function Set-Tag([string] $Image, [string] $Tag) {
    docker tag $Image "$ACR_SERVER/lazydad:$Tag"
    docker push -q "$ACR_SERVER/lazydad:$Tag"
}

foreach ($target in $APPS) {
    # Two phases, as Deploy Environment does, so the purge never deletes a manifest a revision runs. First: the image
    # of the revision serving traffic (not the app's desired image, which after a failed rollout names the failed one)
    # is tagged deployed- and previous-<target>, and the new digest deploying-<target>.
    $serving = @(az containerapp revision list --name $target.App --resource-group $RG -o json | ConvertFrom-Json |
        Where-Object { $_.properties.trafficWeight -gt 0 } | ForEach-Object { $_.properties.template.containers[0].image })
    if ($serving.Count -ne 1) {
        throw "Expected exactly one revision of $($target.App) serving traffic, found $($serving.Count); not moving any tags."
    }
    docker pull -q $serving[0]
    Set-Tag $serving[0] "deployed-$($target.Target)"
    Set-Tag $serving[0] "previous-$($target.Target)"
    Set-Tag "$ACR_SERVER/$TAG" "deploying-$($target.Target)"
    az containerapp update --name $target.App --resource-group $RG --image "$ACR_SERVER/lazydad@$DIGEST" --remove-env-vars App__Version
    # Second: the new digest now runs, so it becomes deployed-<target>.
    Set-Tag "$ACR_SERVER/$TAG" "deployed-$($target.Target)"
}

Write-Host ""
Write-Host "Deployed $TAG ($DIGEST)"
foreach ($target in $APPS) {
    Write-Host "URL: https://$(az containerapp show --name $target.App --resource-group $RG --query properties.configuration.ingress.fqdn -o tsv)"
}
