# Azure Container Apps - Application Update Script
# Updates only the application image without resetting environment variables and secrets
# Usage: .\update-aca-app.ps1 -ResourceGroup "rg-name" -ContainerAppName "app-name" -AcrName "acrname" [-ImageTag "latest"]

param(
    [Parameter(Mandatory=$true, HelpMessage="Resource group name")]
    [string]$ResourceGroup,
    
    [Parameter(Mandatory=$true, HelpMessage="Container App name")]
    [string]$ContainerAppName,
    
    [Parameter(Mandatory=$true, HelpMessage="Azure Container Registry name")]
    [string]$AcrName,
    
    [Parameter(Mandatory=$false)]
    [string]$ImageName = "mongo-migration-tools",
    
    [Parameter(Mandatory=$false)]
    [string]$ImageTag = "latest",
    
    [Parameter(Mandatory=$false, HelpMessage="Update application password (leave empty to keep existing)")]
    [string]$AppPassword = ""
)

$ErrorActionPreference = "Stop"

Write-Host "`n=== Azure Container App - Image Update ===" -ForegroundColor Cyan
Write-Host "Resource Group: $ResourceGroup" -ForegroundColor White
Write-Host "Container App: $ContainerAppName" -ForegroundColor White
Write-Host "ACR: $AcrName" -ForegroundColor White
Write-Host "Image: ${ImageName}:${ImageTag}" -ForegroundColor White
Write-Host ""

# Step 1: Build and push new image to ACR
Write-Host "Step 1: Checking if Docker image exists in ACR..." -ForegroundColor Yellow

# Check if the image exists in ACR
$ErrorActionPreference = 'Continue'
$imageExists = az acr repository show-tags `
    --name $AcrName `
    --repository $ImageName `
    --query "contains(@, '$ImageTag')" `
    --output tsv 2>$null
$ErrorActionPreference = 'Stop'

if ($imageExists -eq 'true') {
    Write-Host "Image '${ImageName}:${ImageTag}' found in ACR. Skipping build." -ForegroundColor Green
    Write-Host "To force a rebuild, delete the tag or use a different tag." -ForegroundColor Gray
} else {
    Write-Host "Image '${ImageName}:${ImageTag}' not found in ACR. Building and pushing..." -ForegroundColor Yellow
    Write-Host "Note: Warnings about packing source code and excluding .git files are normal and expected." -ForegroundColor Gray

    $ErrorActionPreference = 'Continue'
    az acr build `
        --registry $AcrName `
        --resource-group $ResourceGroup `
        --image "${ImageName}:${ImageTag}" `
        --file Dockerfile `
        . `
        2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nError: Failed to build and push Docker image" -ForegroundColor Red
        exit 1
    }
    $ErrorActionPreference = 'Stop'

    Write-Host "`nDocker image built and pushed successfully." -ForegroundColor Green
}

# Step 2: Update Container App with new image
Write-Host "`nStep 2: Updating Container App with new image..." -ForegroundColor Yellow
Write-Host "Note: Warnings about cryptography or UserWarnings are normal and can be ignored." -ForegroundColor Gray

$fullImageName = "$AcrName.azurecr.io/${ImageName}:${ImageTag}"

$updateCommand = @(
    "containerapp", "update",
    "--name", $ContainerAppName,
    "--resource-group", $ResourceGroup,
    "--image", $fullImageName
)

# Add password environment variable if provided
if (-not [string]::IsNullOrEmpty($AppPassword)) {
    Write-Host "Updating APP_PASSWORD environment variable..." -ForegroundColor Cyan
    $updateCommand += @("--set-env-vars", "APP_PASSWORD=$AppPassword")
}

$ErrorActionPreference = 'Continue'
az @updateCommand 2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' }

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nError: Failed to update Container App" -ForegroundColor Red
    exit 1
}
$ErrorActionPreference = 'Stop'

Write-Host "`n=== Update Complete ===" -ForegroundColor Cyan
Write-Host "The Container App '$ContainerAppName' has been updated with image: $fullImageName" -ForegroundColor Green
Write-Host "Environment variables and secrets remain unchanged." -ForegroundColor Green
Write-Host ""

# Step 3: Verify the new image becomes active
Write-Host "Step 3: Verifying new image deployment..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'

# Get the expected replica count from scaling configuration
$scaleConfig = az containerapp show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query "properties.template.scale" `
    --output json `
    2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' } | ConvertFrom-Json

$expectedReplicaCount = 1
if ($scaleConfig.minReplicas) {
    $expectedReplicaCount = $scaleConfig.minReplicas
}

Write-Host "Expected replica count: $expectedReplicaCount (minReplicas: $($scaleConfig.minReplicas), maxReplicas: $($scaleConfig.maxReplicas))" -ForegroundColor Cyan

# Wait for the new container to become ready
Write-Host "`nWaiting for new image to become active and healthy..." -ForegroundColor Yellow
$maxAttempts = 60  # 10 minutes (60 * 10 seconds)
$attemptCount = 0
$isReady = $false

while ($attemptCount -lt $maxAttempts -and -not $isReady) {
    $attemptCount++
    Write-Host "Checking deployment status (attempt $attemptCount/$maxAttempts)..." -ForegroundColor Gray
    
    # Get the active revision
    $activeRevision = az containerapp revision list `
        --name $ContainerAppName `
        --resource-group $ResourceGroup `
        --query "[?properties.active==``true``].name" `
        --output tsv `
        2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' }
    
    if ($activeRevision -and $LASTEXITCODE -eq 0) {
        # Get comprehensive revision details
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
                $activeReplicaCount = $revisionInfo.properties.replicas
                
                # Check if the new image is actually running
                $currentImage = $revisionInfo.properties.template.containers[0].image
                
                Write-Host "  Running State: $runningState | Provisioning: $provisioningState | Health: $healthState | Replicas: $activeReplicaCount" -ForegroundColor Gray
                Write-Host "  Current Image: $currentImage" -ForegroundColor Gray
                
                # Verify all conditions are met
                $imageMatches = $currentImage -eq $fullImageName
                $statesOk = ($runningState -eq "Running") -and ($provisioningState -eq "Provisioned") -and ($healthState -eq "Healthy")
                $correctReplicaCount = $activeReplicaCount -eq $expectedReplicaCount
                
                if ($imageMatches -and $statesOk -and $correctReplicaCount) {
                    $isReady = $true
                    Write-Host "`nNew image is fully active and healthy!" -ForegroundColor Green
                    Write-Host "  Running state: $runningState" -ForegroundColor Green
                    Write-Host "  Provisioning state: $provisioningState" -ForegroundColor Green
                    Write-Host "  Health state: $healthState" -ForegroundColor Green
                    Write-Host "  Active replicas: $activeReplicaCount (expected: $expectedReplicaCount)" -ForegroundColor Green
                    Write-Host "  Image verified: $currentImage" -ForegroundColor Green
                } else {
                    if (-not $imageMatches) {
                        Write-Host "  Waiting for new image to be deployed..." -ForegroundColor Yellow
                    }
                    if (-not $statesOk) {
                        Write-Host "  Waiting for container to reach healthy state..." -ForegroundColor Yellow
                    }
                    if (-not $correctReplicaCount) {
                        if ($activeReplicaCount -gt $expectedReplicaCount) {
                            Write-Host "  Waiting for old replica to terminate ($activeReplicaCount -> $expectedReplicaCount)..." -ForegroundColor Yellow
                        } else {
                            Write-Host "  Waiting for replicas to start ($activeReplicaCount -> $expectedReplicaCount)..." -ForegroundColor Yellow
                        }
                    }
                    Write-Host "  Checking again in 10 seconds..." -ForegroundColor Gray
                    Start-Sleep -Seconds 10
                }
            }
            catch {
                Write-Host "  Error parsing revision info. Retrying in 10 seconds..." -ForegroundColor Yellow
                Start-Sleep -Seconds 10
            }
        } else {
            Write-Host "  Revision info not available yet. Waiting..." -ForegroundColor Yellow
            Start-Sleep -Seconds 10
        }
    } else {
        Write-Host "  Waiting for active revision..." -ForegroundColor Yellow
        Start-Sleep -Seconds 10
    }
}

if (-not $isReady) {
    Write-Host "`nWarning: New image did not become fully active within expected time." -ForegroundColor Yellow
    Write-Host "The deployment may still be in progress. Please check the Azure Portal for more details." -ForegroundColor Yellow
}

$ErrorActionPreference = 'Stop'
Write-Host ""

# Retrieve and display the application URL
Write-Host "Retrieving application URL..." -ForegroundColor Yellow
$ErrorActionPreference = 'Continue'
$appUrl = az containerapp show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query "properties.configuration.ingress.fqdn" `
    --output tsv `
    2>&1 | Where-Object { $_ -notmatch 'cryptography' -and $_ -notmatch 'UserWarning' -and $_ -notmatch 'WARNING:' }
$ErrorActionPreference = 'Stop'

if ($appUrl) {
    Write-Host ""
    Write-Host "===========================================" -ForegroundColor Green
    Write-Host "  Application updated successfully!" -ForegroundColor Green
    Write-Host "===========================================" -ForegroundColor Green
    Write-Host "  Launch URL: https://$appUrl" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "Unable to retrieve application URL. Please check the Azure Portal." -ForegroundColor Yellow
}
