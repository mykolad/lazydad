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

foreach ($target in $APPS) {
    # As Deploy Environment does: what runs now stays tagged (previous-<target>), and the new digest is tagged
    # deployed-<target> before the app moves to it, so the purge never deletes a manifest a revision runs.
    $serving = az containerapp show --name $target.App --resource-group $RG --query "properties.template.containers[0].image" -o tsv
    docker pull -q $serving
    docker tag $serving "$ACR_SERVER/lazydad:previous-$($target.Target)"
    docker push -q "$ACR_SERVER/lazydad:previous-$($target.Target)"
    docker tag "$ACR_SERVER/$TAG" "$ACR_SERVER/lazydad:deployed-$($target.Target)"
    docker push -q "$ACR_SERVER/lazydad:deployed-$($target.Target)"
    az containerapp update --name $target.App --resource-group $RG --image "$ACR_SERVER/lazydad@$DIGEST" --remove-env-vars App__Version
}

Write-Host ""
Write-Host "Deployed $TAG ($DIGEST)"
foreach ($target in $APPS) {
    Write-Host "URL: https://$(az containerapp show --name $target.App --resource-group $RG --query properties.configuration.ingress.fqdn -o tsv)"
}
