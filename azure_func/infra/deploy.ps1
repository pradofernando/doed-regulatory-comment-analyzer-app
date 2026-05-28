# ============================================================================
# Deploy DoED Regulatory Comments Azure Function Infrastructure
# ============================================================================
#
# This script deploys all Azure resources required for the regulatory
# comments processing Azure Function.
#
# Prerequisites:
# - Azure CLI installed (az --version)
# - Logged in to Azure (az login)
# - Bicep CLI installed (az bicep install)
#
# Usage:
#   .\deploy.ps1 -ResourceGroupName "rg-doed-comments" -Location "eastus" -RegulationsGovApiKey "your-api-key"
#
# ============================================================================

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-doed-comments2",
    
    # =========================================================================
    # DEFAULT REGION: East US
    # Change this value to deploy to a different Azure region.
    # Must match a region that supports Azure OpenAI (e.g., eastus, westus2, swedencentral)
    # =========================================================================
    [Parameter(Mandatory=$false)]
    [string]$Location = "eastus",  # <-- CHANGE THIS TO DEPLOY TO A DIFFERENT REGION
    
    [Parameter(Mandatory=$true)]
    [string]$RegulationsGovApiKey,
    
    [Parameter(Mandatory=$false)]
    [string]$DocumentId = "ED-2025-SCC-0481-0001",
    
    [Parameter(Mandatory=$false)]
    [int]$BatchSize = 5
)

# Check if logged in to Azure
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in to Azure. Please run 'az login' first." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "DoED Regulatory Comments - Infrastructure Deployment" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Subscription: $($account.name)" -ForegroundColor White
Write-Host "Resource Group: $ResourceGroupName" -ForegroundColor White
Write-Host "Location: $Location" -ForegroundColor White
Write-Host "Document ID: $DocumentId" -ForegroundColor White
Write-Host ""

# Create resource group if it doesn't exist
Write-Host "Checking resource group..." -ForegroundColor Yellow
$rgExists = az group exists --name $ResourceGroupName
if ($rgExists -eq "false") {
    Write-Host "Creating resource group: $ResourceGroupName" -ForegroundColor Yellow
    az group create --name $ResourceGroupName --location $Location --output none
    Write-Host "Resource group created." -ForegroundColor Green
} else {
    Write-Host "Resource group already exists." -ForegroundColor Green
}

# Deploy Bicep template
Write-Host ""
Write-Host "Deploying infrastructure (this may take 10-15 minutes)..." -ForegroundColor Yellow
Write-Host ""

$deploymentName = "doed-comments-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# Capture stdout (JSON) and stderr separately to avoid JSON parse corruption
$stderrFile = [System.IO.Path]::GetTempFileName()
$deploymentJson = az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --template-file (Join-Path $PSScriptRoot 'main.bicep') `
    --parameters baseName="doed-comments2" `
    --parameters location=$Location `
    --parameters regulationsGovApiKey=$RegulationsGovApiKey `
    --parameters documentId=$DocumentId `
    --parameters batchSize=$BatchSize `
    --output json 2>$stderrFile

$stderrContent = Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue
Remove-Item $stderrFile -ErrorAction SilentlyContinue

if ($LASTEXITCODE -ne 0) {
    # RoleAssignmentExists is non-fatal — permission already exists from a prior run
    # Check both stderr AND the deployment JSON output (error can appear in either)
    $combinedOutput = "$stderrContent $deploymentJson"
    $isOnlyRoleConflict = ($combinedOutput -match 'RoleAssignmentExists') -and
                          ($combinedOutput -notmatch '"code"\s*:\s*"(?!RoleAssignmentExists)(?!DeploymentFailed)[A-Za-z][A-Za-z]')
    if ($isOnlyRoleConflict) {
        Write-Host ""
        Write-Host "Note: Some role assignments already existed (non-fatal - permissions are in place)." -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "Deployment failed!" -ForegroundColor Red
        Write-Host ""
        # Extract all inner error messages from the nested JSON
        $allMessages = [System.Collections.Generic.List[string]]::new()
        $pattern = '"message"\s*:\s*"([^"]{10,})"'
        $matches2 = [regex]::Matches($stderrContent, $pattern)
        foreach ($m in $matches2) { 
            $msg = $m.Groups[1].Value
            if ($msg -notmatch 'tracking id|inner error|See https') {
                $allMessages.Add($msg)
            }
        }
        if ($allMessages.Count -gt 0) {
            Write-Host "Errors:" -ForegroundColor Red
            $allMessages | Select-Object -Unique | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        } else {
            Write-Host "Raw error:" -ForegroundColor Red
            Write-Host $stderrContent -ForegroundColor Red
        }
        Write-Host ""
        Write-Host "Run this to see full inner error details:" -ForegroundColor Yellow
        Write-Host "  az deployment group show --resource-group $ResourceGroupName --name $deploymentName --query `"properties.error`" -o json" -ForegroundColor Cyan
        exit 1
    }
}

# Use the deployment JSON already captured above (avoids a second az call that fails on DeploymentNotFound)
if ([string]::IsNullOrWhiteSpace($deploymentJson)) {
    Write-Host ""
    Write-Host "ERROR: Deployment produced no output. This usually means the deployment failed before it could register." -ForegroundColor Red
    Write-Host "Check what models are available in your region:" -ForegroundColor Yellow
    Write-Host "  az cognitiveservices model list --location $Location --query `"[?model.name=='gpt-4.1']`" -o table" -ForegroundColor Cyan
    exit 1
}

$result = $deploymentJson | ConvertFrom-Json

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "Deployment Succeeded!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

# Extract outputs
$functionAppName   = $result.properties.outputs.functionAppName.value
$functionAppUrl    = $result.properties.outputs.functionAppUrl.value
$storageAccountOut = $result.properties.outputs.storageAccountName.value
$aiHubNameOut      = if ($result.properties.outputs.aiHubName)     { $result.properties.outputs.aiHubName.value }     else { "" }
$aiProjectNameOut  = if ($result.properties.outputs.aiProjectName) { $result.properties.outputs.aiProjectName.value } else { "" }
$aiProjectEndpoint = $result.properties.outputs.aiProjectEndpoint.value
$openAiEndpoint    = $result.properties.outputs.openAiEndpoint.value
$modelDeployment   = "gpt-4.1"

# Validate critical outputs are not empty
$missingOutputs = @()
if ([string]::IsNullOrWhiteSpace($functionAppName))   { $missingOutputs += 'functionAppName' }
if ([string]::IsNullOrWhiteSpace($aiHubNameOut))       { $missingOutputs += 'aiHubName' }
if ([string]::IsNullOrWhiteSpace($aiProjectNameOut))   { $missingOutputs += 'aiProjectName' }
if ([string]::IsNullOrWhiteSpace($aiProjectEndpoint))  { $missingOutputs += 'aiProjectEndpoint' }
if ([string]::IsNullOrWhiteSpace($modelDeployment))    { $missingOutputs += 'modelDeploymentName' }

if ($missingOutputs.Count -gt 0) {
    Write-Host "ERROR: Missing deployment outputs: $($missingOutputs -join ', ')" -ForegroundColor Red
    Write-Host "This usually means one or more resources failed to provision." -ForegroundColor Yellow
    Write-Host "Check the error with:" -ForegroundColor Yellow
    Write-Host "  az deployment group show --resource-group $ResourceGroupName --name $deploymentName --query properties.error -o json" -ForegroundColor Cyan
    exit 1
}

# Display outputs
Write-Host "Deployment Outputs:" -ForegroundColor Cyan
Write-Host ""
Write-Host "Function App Name:        $functionAppName" -ForegroundColor White
Write-Host "Function App URL:         $functionAppUrl" -ForegroundColor White
Write-Host "Storage Account:          $storageAccountOut" -ForegroundColor White
Write-Host "AI Hub Name:              $aiHubNameOut" -ForegroundColor White
Write-Host "AI Project Name:          $aiProjectNameOut" -ForegroundColor White
Write-Host "AI Project Endpoint:      $aiProjectEndpoint" -ForegroundColor White
Write-Host "OpenAI Endpoint:          $openAiEndpoint" -ForegroundColor White
Write-Host "Model Deployment:         $modelDeployment" -ForegroundColor White
Write-Host ""

# -------------------------------------------------------------------------
# Create AI Agents via REST API (agents can't be created via Bicep)
# -------------------------------------------------------------------------
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Creating AI Agents in Azure AI Foundry..." -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""

$aiEndpoint = $aiProjectEndpoint
$agentsUrl  = "$aiEndpoint/assistants?api-version=2024-12-01-preview"

Write-Host "AI Endpoint: $aiEndpoint" -ForegroundColor Gray

# Wait for AI Foundry to fully provision before making REST calls
Write-Host "Waiting 3 minutes for AI Foundry to finish provisioning..." -ForegroundColor Yellow
Start-Sleep -Seconds 180

# Get access token for the Azure AI Foundry API
Write-Host "Obtaining access token for Azure AI Foundry..." -ForegroundColor Yellow
$token = (az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv)
if (-not $token) {
    Write-Host "ERROR: Could not obtain access token. Skipping agent creation." -ForegroundColor Red
    Write-Host "You can create agents manually in the Azure AI Foundry portal." -ForegroundColor Gray
} elseif ([string]::IsNullOrWhiteSpace($aiEndpoint) -or $aiEndpoint -notmatch '^https://[^.]+\.') {
    Write-Host "ERROR: AI endpoint is invalid or empty: '$aiEndpoint'" -ForegroundColor Red
    Write-Host "Skipping agent creation. Set CATEGORIZATION_AGENT_ID and GROUPING_AGENT_ID manually." -ForegroundColor Yellow
} else {
    # Verify the endpoint is reachable (AI Foundry may still need a moment)
    Write-Host "Verifying AI Foundry endpoint is reachable..." -ForegroundColor Yellow
    try {
        $headers = @{ 'Authorization' = "Bearer $token"; 'Content-Type' = 'application/json' }
        Invoke-RestMethod -Method Get -Uri $agentsUrl -Headers $headers -ErrorAction Stop | Out-Null
        Write-Host "Endpoint reachable." -ForegroundColor Green
    } catch {
        Write-Host "Endpoint not yet ready, waiting 2 more minutes..." -ForegroundColor Yellow
        Start-Sleep -Seconds 120
        $token = (az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv)
    }

    # Refresh headers with potentially new token
    $headers = @{ 'Authorization' = "Bearer $token"; 'Content-Type' = 'application/json' }
    Write-Host "" -ForegroundColor Gray
    if ($true) {
    $headers = @{
        'Authorization' = "Bearer $token"
        'Content-Type'  = 'application/json'
    }

    # --- Create Categorization Agent ---
    Write-Host "Creating Categorization Agent..." -ForegroundColor Yellow
    $catBody = @{
        model = $modelDeployment
        name  = "RegulatoryCommentCategorizationAgent"
        description = "Categorizes individual regulatory comments by topic and sentiment"
        instructions = "You are an expert regulatory analyst. Analyze each individual public comment submitted in response to U.S. Department of Education regulatory proposals. For each comment: 1) Identify the primary topic/category (e.g. student loans, financial aid, accreditation, civil rights, campus safety, special education). 2) Assess the overall sentiment: supportive, opposed, neutral, or mixed. 3) Extract the key concerns or arguments raised. 4) Note whether the commenter is an individual or an organization. Return your analysis as structured JSON."
    } | ConvertTo-Json -Depth 5

    try {
        $catResponse = Invoke-RestMethod -Method Post -Uri $agentsUrl -Headers $headers -Body $catBody
        $categorizationAgentId = $catResponse.id
        Write-Host "Categorization Agent created: $categorizationAgentId" -ForegroundColor Green
    } catch {
        Write-Host "ERROR creating Categorization Agent: $_" -ForegroundColor Red
        $categorizationAgentId = $null
    }

    # --- Create Grouping Agent ---
    Write-Host "Creating Grouping Agent..." -ForegroundColor Yellow
    $grpBody = @{
        model = $modelDeployment
        name  = "RegulatoryCommentGroupingAgent"
        description = "Groups and summarizes batches of categorized regulatory comments"
        instructions = "You are an expert regulatory policy analyst. You receive batches of pre-categorized public comments on U.S. Department of Education regulatory proposals. Your job is to: 1) Identify common themes and patterns across the batch. 2) Group similar comments together. 3) Synthesize the key arguments made by each group. 4) Quantify the distribution of opinions (e.g. 60% opposed, 30% supportive, 10% neutral). 5) Highlight any unique or outlier perspectives. 6) Produce a structured summary suitable for policy review. Return your analysis as structured JSON."
    } | ConvertTo-Json -Depth 5

    try {
        $grpResponse = Invoke-RestMethod -Method Post -Uri $agentsUrl -Headers $headers -Body $grpBody
        $groupingAgentId = $grpResponse.id
        Write-Host "Grouping Agent created: $groupingAgentId" -ForegroundColor Green
    } catch {
        Write-Host "ERROR creating Grouping Agent: $_" -ForegroundColor Red
        $groupingAgentId = $null
    }

    # --- Update Function App settings with real agent IDs ---
    if ($categorizationAgentId -and $groupingAgentId) {
        Write-Host ""
        Write-Host "Updating Function App settings with agent IDs..." -ForegroundColor Yellow
        az functionapp config appsettings set `
            --name $functionAppName `
            --resource-group $ResourceGroupName `
            --settings "CATEGORIZATION_AGENT_ID=$categorizationAgentId" "GROUPING_AGENT_ID=$groupingAgentId" `
            --output none

        Write-Host "Function App settings updated." -ForegroundColor Green
        Write-Host ""
        Write-Host "  Categorization Agent ID: $categorizationAgentId" -ForegroundColor Cyan
        Write-Host "  Grouping Agent ID:        $groupingAgentId" -ForegroundColor Cyan
    } else {
        Write-Host ""
        Write-Host "WARNING: One or both agents failed to create." -ForegroundColor Yellow
        Write-Host "Update CATEGORIZATION_AGENT_ID and GROUPING_AGENT_ID in the Function App settings manually." -ForegroundColor Yellow
    }
  } # end if ($true)
} # end else (token + endpoint valid)
Write-Host ""

# -------------------------------------------------------------------------
# Publish the Function App code
# -------------------------------------------------------------------------
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Publishing Function App code..." -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""

$funcAppDir = Join-Path $PSScriptRoot ".." "doed_regulatory_comments_func"
$funcAppDir = Resolve-Path $funcAppDir

# Check if Azure Functions Core Tools is installed
if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
    Write-Host "Azure Functions Core Tools not found. Installing..." -ForegroundColor Yellow
    winget install --id Microsoft.AzureFunctionsCoreTools --accept-source-agreements --accept-package-agreements
    # Refresh PATH
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PATH', 'User')
}

if ([string]::IsNullOrWhiteSpace($functionAppName)) {
    Write-Host "ERROR: Cannot publish - function app name is empty." -ForegroundColor Red
    Write-Host "Retrieve it with: az functionapp list --resource-group $ResourceGroupName --query '[0].name' -o tsv" -ForegroundColor Cyan
    exit 1
}

Push-Location $funcAppDir
try {
    Write-Host "Publishing to $functionAppName..." -ForegroundColor Yellow
    func azure functionapp publish $functionAppName --python
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "Function App published successfully!" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Function App publish failed. You can retry manually:" -ForegroundColor Red
        Write-Host "  cd $funcAppDir" -ForegroundColor Gray
        Write-Host "  func azure functionapp publish $functionAppName --python" -ForegroundColor Gray
    }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "All done! Your app is fully deployed." -ForegroundColor Cyan
Write-Host "The function runs daily at 3AM EST (8AM UTC)." -ForegroundColor Cyan
Write-Host "Monitor it at: https://portal.azure.com" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
