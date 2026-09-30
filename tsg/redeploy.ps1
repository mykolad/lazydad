# Fail fast: stop on the first error, including a non-zero exit from az/docker
# (PowerShell 7.3+), so a failed login, build or push never reaches the app update.
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$ACR_NAME   = "lazydadacr"
$RG         = "lazydad-rg"
$APP_NAMES  = "lazydad-app", "lazydad-app-swedencentral"   # production runs in two regions
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
foreach ($APP_NAME in $APP_NAMES) {
    az containerapp update --name $APP_NAME --resource-group $RG --image "$ACR_SERVER/$TAG" --remove-env-vars App__Version
}

Write-Host ""
Write-Host "Deployed $TAG"
foreach ($APP_NAME in $APP_NAMES) {
    Write-Host "URL: https://$(az containerapp show --name $APP_NAME --resource-group $RG --query properties.configuration.ingress.fqdn -o tsv)"
}
