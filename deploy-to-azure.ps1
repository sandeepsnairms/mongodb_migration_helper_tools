# Azure Container Apps Deployment Script
# Usage: .\deploy-to-azure.ps1 -AcrName "myuniqueacrname" [-ResourceGroup "rg-name"] [-Location "eastus"]

param(
    [Parameter(Mandatory=$true, HelpMessage="Azure Container Registry name (must be globally unique)")]
    [string]$AcrName,
    
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-migration-tools",
    
    [Parameter(Mandatory=$false)]
    [ValidateSet('eastus','eastus2','westus2','westus','centralus','northcentralus','southcentralus','westcentralus','northeurope','westeurope','francecentral','germanywestcentral','uksouth','ukwest','switzerlandnorth','norwayeast','eastasia','southeastasia','japaneast','japanwest','koreacentral','australiaeast','australiasoutheast','centralindia','southindia','brazilsouth','canadacentral','canadaeast','uaenorth','southafricanorth','swedencentral')]
    [string]$Location = "eastus",
    
    [Parameter(Mandatory=$false)]
    [string]$ContainerAppName = "app-migration-tools",
    
    [Parameter(Mandatory=$false)]
    [string]$ImageName = "mongo-migration-tools",
    
    [Parameter(Mandatory=$false)]
    [string]$ImageTag = "latest",
    
    [Parameter(Mandatory=$false, HelpMessage="Application password for authentication (if not provided, will prompt)")]
    [string]$AppPassword = ""
)

$ErrorActionPreference = "Stop"

$ContainerEnvName = "$ContainerAppName-env"

Write-Host "`n=== Azure Container Apps Deployment ===" -ForegroundColor Cyan
Write-Host "ACR Name: $AcrName" -ForegroundColor White
Write-Host "Resource Group: $ResourceGroup" -ForegroundColor White
Write-Host "Location: $Location" -ForegroundColor White
Write-Host "Container App: $ContainerAppName" -ForegroundColor White
Write-Host "Environment: $ContainerEnvName" -ForegroundColor White
Write-Host ""

# Step 1: Create Resource Group
Write-Host "`nStep 1: Creating resource group..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
az group create --name $ResourceGroup --location $Location --output none 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'

Write-Host "Resource group created successfully." -ForegroundColor Green

# Step 2: Create Azure Container Registry
Write-Host "`nStep 2: Creating Azure Container Registry..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
az acr create `
  --resource-group $ResourceGroup `
  --name $AcrName `
  --sku Basic `
  --admin-enabled true `
  --output none 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'

Write-Host "Azure Container Registry created successfully." -ForegroundColor Green

# Step 3: Check if image exists, build if needed
Write-Host "`nStep 3: Checking if Docker image exists in ACR..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
$imageExists = az acr repository show-tags `
    --name $AcrName `
    --repository $ImageName `
    --query "contains(@, '$ImageTag')" `
    --output tsv 2>$null

if ($LASTEXITCODE -eq 0 -and $imageExists -eq 'true') {
    Write-Host "Image '${ImageName}:${ImageTag}' found in ACR. Skipping build." -ForegroundColor Green
    $ErrorActionPreference = 'Stop'
} else {
    Write-Host "Image '${ImageName}:${ImageTag}' not found in ACR. Building and pushing..." -ForegroundColor Yellow
    Write-Host "Note: Warnings about packing source code and excluding .git files are normal and expected." -ForegroundColor Gray
    
    az acr build `
        --registry $AcrName `
        --resource-group $ResourceGroup `
        --image "${ImageName}:${ImageTag}" `
        --file Dockerfile `
        .
    
    if ($LASTEXITCODE -ne 0) {
        $ErrorActionPreference = 'Stop'
        Write-Host "Error: Failed to build and push Docker image" -ForegroundColor Red
        exit 1
    }
    $ErrorActionPreference = 'Stop'
    
    Write-Host "Docker image built and pushed successfully." -ForegroundColor Green
}

# Step 4: Create Container Apps Environment
Write-Host "`nStep 4: Creating Container Apps environment..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
az containerapp env create `
  --name $ContainerEnvName `
  --resource-group $ResourceGroup `
  --location $Location `
  --output none 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'

Write-Host "Container Apps environment created successfully." -ForegroundColor Green

# Step 4.5: Prompt for application password if not provided
if ([string]::IsNullOrEmpty($AppPassword)) {    Write-Host "`nStep 4.5: Setting application password..." -ForegroundColor Yellow
    $securePassword = Read-Host -Prompt "Enter application password for authentication" -AsSecureString
    $AppPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    )
    
    if ([string]::IsNullOrEmpty($AppPassword)) {
        Write-Host "Error: Application password cannot be empty" -ForegroundColor Red
        exit 1
    }
    Write-Host "Application password set." -ForegroundColor Green
}

# Step 5: Deploy Container App
$imageName = "${AcrName}.azurecr.io/${ImageName}:${ImageTag}"
Write-Host "`nStep 5: Deploying Container App..." -ForegroundColor Yellow
Write-Host "Using image: $imageName" -ForegroundColor Gray
$ErrorActionPreference = 'Continue'

$deployParams = @(
    "containerapp", "create",
    "--name", $ContainerAppName,
    "--resource-group", $ResourceGroup,
    "--environment", $ContainerEnvName,
    "--image", $imageName,
    "--target-port", "8080",
    "--ingress", "external",
    "--registry-server", "${AcrName}.azurecr.io",
    "--cpu", "0.5",
    "--memory", "1.0Gi",
    "--min-replicas", "1",
    "--max-replicas", "3",
    "--env-vars", "APP_PASSWORD=$AppPassword",
    "--output", "none"
)

az @deployParams 2>&1 | Out-Null

$ErrorActionPreference = 'Stop'

Write-Host "`n=== Deployment Complete ===" -ForegroundColor Cyan

# Step 5.5: Enable sticky sessions (required for Blazor Server SignalR)
Write-Host "`nStep 5.5: Enabling sticky sessions for Blazor Server..." -ForegroundColor Yellow
az containerapp ingress sticky-sessions set `
  --name $ContainerAppName `
  --resource-group $ResourceGroup `
  --affinity sticky `
  --output none 2>&1 | Out-Null
Write-Host "Sticky sessions enabled." -ForegroundColor Green

# Step 6: Verify deployment
Write-Host "`nStep 6: Verifying deployment..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'

$maxAttempts = 60
$attemptCount = 0
$isReady = $false

while ($attemptCount -lt $maxAttempts -and -not $isReady) {
    $attemptCount++
    Write-Host "Checking deployment status (attempt $attemptCount/$maxAttempts)..." -ForegroundColor Gray
    
    $activeRevision = az containerapp revision list `
        --name $ContainerAppName `
        --resource-group $ResourceGroup `
        --query "[?properties.active==``true``].name" `
        --output tsv `
        2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' }
    
    if ($activeRevision -and $LASTEXITCODE -eq 0) {
        $revisionOutput = az containerapp revision show `
            --name $ContainerAppName `
            --resource-group $ResourceGroup `
            --revision $activeRevision `
            --output json `
            2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' -and $_ -notmatch 'ERROR' }
        
        if ($LASTEXITCODE -eq 0 -and $revisionOutput) {
            try {
                $revisionInfo = $revisionOutput | ConvertFrom-Json
                
                $runningState = $revisionInfo.properties.runningState
                $provisioningState = $revisionInfo.properties.provisioningState
                $healthState = $revisionInfo.properties.healthState
                
                Write-Host "  Running: $runningState | Provisioning: $provisioningState | Health: $healthState" -ForegroundColor Gray
                
                if ($runningState -eq "Running" -and $provisioningState -eq "Provisioned" -and $healthState -eq "Healthy") {
                    $isReady = $true
                    Write-Host "`nContainer is fully active and healthy!" -ForegroundColor Green
                } else {
                    Start-Sleep -Seconds 10
                }
            }
            catch {
                Start-Sleep -Seconds 10
            }
        } else {
            Start-Sleep -Seconds 10
        }
    } else {
        Start-Sleep -Seconds 10
    }
}

$ErrorActionPreference = 'Stop'

# Step 8: Get the URL
Write-Host "`nStep 8: Retrieving application URL..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
$appUrl = az containerapp show `
  --name $ContainerAppName `
  --resource-group $ResourceGroup `
  --query properties.configuration.ingress.fqdn `
  --output tsv `
  2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' }
$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "  Deployment completed successfully!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
if ($appUrl) {
    Write-Host "  Application URL: https://$appUrl" -ForegroundColor Cyan
} else {
    Write-Host "  Unable to retrieve URL. Check Azure Portal." -ForegroundColor Yellow
}
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
