$ACR_NAME   = "lazydadacr"
$RG         = "lazydad-rg"
$APP_NAME   = "lazydad-app"
$ACR_SERVER = az acr show --name $ACR_NAME --query loginServer -o tsv

az acr login --name $ACR_NAME

$TAG = "lazydad:$(git rev-parse --short HEAD)"
docker build -t "$ACR_SERVER/$TAG" .
docker push "$ACR_SERVER/$TAG"
az containerapp update --name $APP_NAME --resource-group $RG --image "$ACR_SERVER/$TAG"

Write-Host ""
Write-Host "Deployed $TAG"
Write-Host "URL: https://$(az containerapp show --name $APP_NAME --resource-group $RG --query properties.configuration.ingress.fqdn -o tsv)"
