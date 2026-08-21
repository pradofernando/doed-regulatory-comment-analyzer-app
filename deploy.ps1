# ============================================================================
# Deploy DoED Regulatory Comments - complete stack
# ============================================================================
#
# Runs the latest Function deployment first, then deploys and publishes the
# .NET frontend using the Foundry endpoint and prompt-agent versions produced
# by the Function deployment.
#
# Usage:
#   .\deploy.ps1 -RegulationsGovApiKey "your-key"
#   .\deploy.ps1 -ResourceGroupName "rg-doed-comments" -Location "eastus" -RegulationsGovApiKey "your-key"
#
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-doed-comments",

    [Parameter(Mandatory=$false)]
    [string]$FrontendResourceGroupName = "",

    [Parameter(Mandatory=$false)]
    [string]$Location = "eastus",

    [Parameter(Mandatory=$false)]
    [string]$RegulationsGovApiKey = $env:REGS_API_KEY,

    [Parameter(Mandatory=$false)]
    [string]$DocumentId = "ED-2025-SCC-0481-0001",

    [Parameter(Mandatory=$false)]
    [int]$BatchSize = 5,

    [Parameter(Mandatory=$false)]
    [string]$FunctionBaseName = "doed-comments",

    [Parameter(Mandatory=$false)]
    [string]$FrontendBaseName = "doedweb",

    [Parameter(Mandatory=$false)]
    [string]$FrontendSku = "B1",

    [Parameter(Mandatory=$false)]
    [ValidateSet('Sqlite', 'AzureSql', 'Cosmos')]
    [string]$PersistenceProvider = "Cosmos",

    [Parameter(Mandatory=$false)]
    [string]$AnalysisDbConnectionString = "",

    [Parameter(Mandatory=$false)]
    [string]$CosmosEndpoint = "",

    [Parameter(Mandatory=$false)]
    [string]$CosmosDatabaseName = "doed-regulatory-comments",

    [Parameter(Mandatory=$false)]
    [string]$CosmosContainerName = "analysis-runs",

    [Parameter(Mandatory=$false)]
    [string]$CosmosSummaryContainerName = "analysis-run-summaries",

    [Parameter(Mandatory=$false)]
    [string]$CosmosAccountName = "",

    [Parameter(Mandatory=$false)]
    [string]$CosmosResourceGroupName = "",

    [Parameter(Mandatory=$false)]
    [switch]$CosmosCreateIfNotExists,

    [Parameter(Mandatory=$false)]
    [switch]$ProvisionCosmosResources,

    [Parameter(Mandatory=$false)]
    [switch]$EnablePayloadStorage,

    [Parameter(Mandatory=$false)]
    [switch]$EnableAttachmentOcr,

    [Parameter(Mandatory=$false)]
    [string]$FollowUpAgentName = "",

    [Parameter(Mandatory=$false)]
    [string]$FollowUpAgentVersion = "latest",

    [Parameter(Mandatory=$false)]
    [int]$GptCapacity = 10,

    [Parameter(Mandatory=$false)]
    [int]$EmbeddingCapacity = 10,

    [Parameter(Mandatory=$false)]
    [string]$DeploymentSuffix = "",

    [Parameter(Mandatory=$false)]
    [string]$FoundryProjectEndpoint = "",

    [Parameter(Mandatory=$false)]
    [switch]$UsePremium,

    [Parameter(Mandatory=$false)]
    [switch]$SkipFunctionDeployment,

    [Parameter(Mandatory=$false)]
    [switch]$SkipFrontendPublish
)

$ErrorActionPreference = 'Continue'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$env:AZURE_CORE_ONLY_SHOW_ERRORS = 'true'

if ([string]::IsNullOrWhiteSpace($FrontendResourceGroupName)) {
    $FrontendResourceGroupName = $ResourceGroupName
}
if ([string]::IsNullOrWhiteSpace($CosmosResourceGroupName)) {
    $CosmosResourceGroupName = $FrontendResourceGroupName
}

if ([string]::IsNullOrWhiteSpace($RegulationsGovApiKey)) {
    throw "RegulationsGovApiKey is required. Pass -RegulationsGovApiKey or set REGS_API_KEY."
}

if ($ProvisionCosmosResources -and $PersistenceProvider -ne 'Cosmos') {
    throw "ProvisionCosmosResources requires -PersistenceProvider Cosmos."
}
if ($PersistenceProvider -eq 'Cosmos' -and [string]::IsNullOrWhiteSpace($CosmosEndpoint)) {
    $ProvisionCosmosResources = $true
}
if ($PersistenceProvider -eq 'Cosmos' -and -not [string]::IsNullOrWhiteSpace($CosmosEndpoint) -and [string]::IsNullOrWhiteSpace($CosmosAccountName)) {
    try {
        $CosmosAccountName = ([uri]$CosmosEndpoint).Host.Split('.')[0]
    } catch {
        throw "CosmosEndpoint must be an absolute Cosmos DB endpoint URL."
    }
}
if ($PersistenceProvider -eq 'AzureSql' -and [string]::IsNullOrWhiteSpace($AnalysisDbConnectionString)) {
    throw "AnalysisDbConnectionString is required when PersistenceProvider is AzureSql."
}

$repoRoot = $PSScriptRoot
$functionDeployScript = Join-Path $repoRoot "azure_func_v2\infra\deploy.ps1"
$frontendBicep = Join-Path $repoRoot "dotnet_frontend\infra\main.bicep"
$frontendProject = Join-Path $repoRoot "dotnet_frontend"

function Assert-CommandAvailable {
    param([Parameter(Mandatory=$true)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$FailureMessage
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-CurrentPowerShellPath {
    $currentProcess = Get-Process -Id $PID
    if ($currentProcess -and -not [string]::IsNullOrWhiteSpace($currentProcess.Path)) {
        return $currentProcess.Path
    }

    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh) { return $pwsh.Source }

    $powershell = Get-Command powershell -ErrorAction SilentlyContinue
    if ($powershell) { return $powershell.Source }

    throw "Could not locate a PowerShell executable for child deployment."
}

function Get-RequiredSetting {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Settings,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $value = $Settings[$Name]
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Function App setting '$Name' was not found or was blank. The Function deployment may not have completed agent creation."
    }

    return $value
}

function Get-OptionalSetting {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Settings,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$false)][string]$Default = ""
    )

    $value = $Settings[$Name]
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    return $value
}

Assert-CommandAvailable -Name 'az'
Assert-CommandAvailable -Name 'dotnet'
Assert-CommandAvailable -Name 'tar'

$accountJson = az account show 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accountJson)) {
    throw "Not logged in to Azure. Run 'az login' first."
}

$account = $accountJson | ConvertFrom-Json

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "DoED Regulatory Comments - Full Deployment" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Subscription:             $($account.name)" -ForegroundColor White
Write-Host "Function resource group:  $ResourceGroupName" -ForegroundColor White
Write-Host "Frontend resource group:  $FrontendResourceGroupName" -ForegroundColor White
Write-Host "Location:                 $Location" -ForegroundColor White
Write-Host "Frontend persistence:     $PersistenceProvider" -ForegroundColor White
Write-Host ""

if (-not $SkipFunctionDeployment) {
    if (-not (Test-Path $functionDeployScript)) {
        throw "Function deployment script not found: $functionDeployScript"
    }

    Write-Host "============================================" -ForegroundColor Yellow
    Write-Host "Step 1/4: Deploying Azure Function v2 stack" -ForegroundColor Yellow
    Write-Host "============================================" -ForegroundColor Yellow

    $agentDeploymentOutputPath = Join-Path $env:TEMP ("doed-agent-deployment-{0}.json" -f ([guid]::NewGuid().ToString('N')))
    $functionArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $functionDeployScript,
        '-ResourceGroupName', $ResourceGroupName,
        '-BaseName', $FunctionBaseName,
        '-Location', $Location,
        '-RegulationsGovApiKey', $RegulationsGovApiKey,
        '-DocumentId', $DocumentId,
        '-BatchSize', [string]$BatchSize,
        '-GptCapacity', [string]$GptCapacity,
        '-EmbeddingCapacity', [string]$EmbeddingCapacity,
        '-AgentDeploymentOutputPath', $agentDeploymentOutputPath
    )

    if (-not [string]::IsNullOrWhiteSpace($DeploymentSuffix)) {
        $functionArgs += @('-DeploymentSuffix', $DeploymentSuffix)
    }
    if (-not [string]::IsNullOrWhiteSpace($FoundryProjectEndpoint)) {
        $functionArgs += @('-FoundryProjectEndpoint', $FoundryProjectEndpoint)
    }
    if ($UsePremium) {
        $functionArgs += '-UsePremium'
    }

    $powerShellExe = Get-CurrentPowerShellPath
    & $powerShellExe @functionArgs
    if ($LASTEXITCODE -eq 2) {
        Remove-Item $agentDeploymentOutputPath -Force -ErrorAction SilentlyContinue
        Write-Host "Function deployment publish was already in progress, and you chose to exit. No frontend deployment was started." -ForegroundColor Yellow
        exit 0
    }
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $agentDeploymentOutputPath -Force -ErrorAction SilentlyContinue
        throw "Azure Function v2 deployment failed."
    }

    if (-not (Test-Path $agentDeploymentOutputPath)) {
        throw "Function deployment completed without the Foundry agent deployment manifest."
    }
    try {
        $agentDeployment = Get-Content -Path $agentDeploymentOutputPath -Raw | ConvertFrom-Json
        $deployedFollowUpAgentName = [string]$agentDeployment.followUpAgent.name
        $deployedFollowUpAgentVersion = [string]$agentDeployment.followUpAgent.version
        if ([string]::IsNullOrWhiteSpace($deployedFollowUpAgentName) -or [string]::IsNullOrWhiteSpace($deployedFollowUpAgentVersion)) {
            throw "Foundry agent deployment manifest did not contain the follow-up agent name and version."
        }
    } finally {
        Remove-Item $agentDeploymentOutputPath -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "Skipping Azure Function v2 deployment by request." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Step 2/4: Reading Function deployment settings" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow

$functionNamePrefix = if ($UsePremium) { "func-$FunctionBaseName-prem-" } else { "func-$FunctionBaseName-" }
$functionAppName = az resource list `
    --resource-group $ResourceGroupName `
    --resource-type 'Microsoft.Web/sites' `
    --query "[?starts_with(name, '$functionNamePrefix') && (!starts_with(name, 'func-$FunctionBaseName-prem-') || '$($UsePremium.IsPresent)' == 'True')].name | [0]" `
    -o tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionAppName)) {
    throw "Could not locate Function App with prefix '$functionNamePrefix' in resource group '$ResourceGroupName'."
}

$functionSettingsJson = az functionapp config appsettings list `
    --name $functionAppName `
    --resource-group $ResourceGroupName `
    -o json

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionSettingsJson)) {
    throw "Could not read settings from Function App '$functionAppName'."
}

$functionSettings = @{}
foreach ($setting in ($functionSettingsJson | ConvertFrom-Json)) {
    $functionSettings[[string]$setting.name] = [string]$setting.value
}

$foundryEndpoint = Get-RequiredSetting -Settings $functionSettings -Name 'FOUNDRY_PROJECT_ENDPOINT'
$categorizationAgentName = Get-RequiredSetting -Settings $functionSettings -Name 'CATEGORIZATION_AGENT_NAME'
$categorizationAgentVersion = Get-RequiredSetting -Settings $functionSettings -Name 'CATEGORIZATION_AGENT_VERSION'
$groupingAgentName = Get-RequiredSetting -Settings $functionSettings -Name 'GROUPING_AGENT_NAME'
$groupingAgentVersion = Get-RequiredSetting -Settings $functionSettings -Name 'GROUPING_AGENT_VERSION'
$validationAgentName = Get-OptionalSetting -Settings $functionSettings -Name 'VALIDATION_AGENT_NAME'
$validationAgentVersion = Get-OptionalSetting -Settings $functionSettings -Name 'VALIDATION_AGENT_VERSION' -Default 'latest'
$modelDeploymentName = Get-OptionalSetting -Settings $functionSettings -Name 'CATEGORIZATION_AGENT_MODEL' -Default 'gpt-4o'
$functionStorageAccountName = Get-RequiredSetting -Settings $functionSettings -Name 'AZURE_STORAGE_ACCOUNT_NAME'
$functionAppUrl = "https://$functionAppName.azurewebsites.net"
$useFunctionAnalysisBackend = $PersistenceProvider -eq 'Cosmos'
$functionKey = ''
if ($useFunctionAnalysisBackend) {
    $functionKey = az functionapp keys list `
        --name $functionAppName `
        --resource-group $ResourceGroupName `
        --query 'functionKeys.default' `
        -o tsv `
        --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionKey)) {
        throw "Could not retrieve the default Function key for '$functionAppName'."
    }
}
$analysisPayloadContainerName = 'analysis-run-payloads'
$analysisPayloadContainerUri = "https://$functionStorageAccountName.blob.core.windows.net/$analysisPayloadContainerName"

if ($SkipFunctionDeployment -and [string]::IsNullOrWhiteSpace($FollowUpAgentName)) {
    $existingFrontendAppName = az resource list `
        --resource-group $FrontendResourceGroupName `
        --resource-type 'Microsoft.Web/sites' `
        --query "[?starts_with(name, '$FrontendBaseName-app-')].name | [0]" `
        -o tsv
    if (-not [string]::IsNullOrWhiteSpace($existingFrontendAppName)) {
        $deployedFollowUpAgentName = az webapp config appsettings list `
            --name $existingFrontendAppName `
            --resource-group $FrontendResourceGroupName `
            --query "[?name=='Api__FollowUpAgentName'].value | [0]" `
            -o tsv
        $deployedFollowUpAgentVersion = az webapp config appsettings list `
            --name $existingFrontendAppName `
            --resource-group $FrontendResourceGroupName `
            --query "[?name=='Api__FollowUpAgentVersion'].value | [0]" `
            -o tsv
    }
}

$effectiveFollowUpAgentName = if ([string]::IsNullOrWhiteSpace($FollowUpAgentName)) { $deployedFollowUpAgentName } else { $FollowUpAgentName }
$effectiveFollowUpAgentVersion = if ([string]::IsNullOrWhiteSpace($FollowUpAgentName)) { $deployedFollowUpAgentVersion } else { $FollowUpAgentVersion }
if (-not [string]::IsNullOrWhiteSpace($effectiveFollowUpAgentName) -and [string]::IsNullOrWhiteSpace($effectiveFollowUpAgentVersion)) {
    $effectiveFollowUpAgentVersion = 'latest'
}

$foundryProjectResourceId = az resource list `
    --resource-group $ResourceGroupName `
    --resource-type 'Microsoft.CognitiveServices/accounts/projects' `
    --query '[0].id' `
    -o tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($foundryProjectResourceId)) {
    throw "Could not locate the Foundry project ARM resource in '$ResourceGroupName'."
}

Write-Host "Function App:              $functionAppName" -ForegroundColor White
Write-Host "Foundry endpoint:          $foundryEndpoint" -ForegroundColor White
Write-Host "Categorization agent:      $categorizationAgentName v$categorizationAgentVersion" -ForegroundColor White
Write-Host "Grouping agent:            $groupingAgentName v$groupingAgentVersion" -ForegroundColor White
if (-not [string]::IsNullOrWhiteSpace($validationAgentName)) {
    Write-Host "Validation agent:          $validationAgentName v$validationAgentVersion" -ForegroundColor White
} else {
    Write-Host "Validation agent:          not configured" -ForegroundColor Yellow
}
if (-not [string]::IsNullOrWhiteSpace($effectiveFollowUpAgentName)) {
    Write-Host "Follow-up Q&A agent:       $effectiveFollowUpAgentName v$effectiveFollowUpAgentVersion" -ForegroundColor White
} else {
    Write-Host "Follow-up Q&A agent:       not configured" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Step 3/4: Deploying frontend infrastructure" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow

$rgExists = az group exists --name $FrontendResourceGroupName
if ($rgExists -eq 'false') {
    Invoke-NativeChecked -Command 'az' -Arguments @('group', 'create', '--name', $FrontendResourceGroupName, '--location', $Location, '--output', 'none') -FailureMessage "Failed to create frontend resource group."
}

$frontendParametersFile = Join-Path ([System.IO.Path]::GetTempPath()) ("doed-web-parameters-{0}.json" -f ([guid]::NewGuid().ToString('N')))
try {
    $frontendParameters = [ordered]@{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = [ordered]@{
            baseName = @{ value = $FrontendBaseName }
            location = @{ value = $Location }
            appServicePlanSku = @{ value = $FrontendSku }
            regulationsGovApiKey = @{ value = $RegulationsGovApiKey }
            foundryProjectEndpoint = @{ value = $foundryEndpoint }
            categorizationAgentName = @{ value = $categorizationAgentName }
            categorizationAgentVersion = @{ value = $categorizationAgentVersion }
            groupingAgentName = @{ value = $groupingAgentName }
            groupingAgentVersion = @{ value = $groupingAgentVersion }
            validationAgentName = @{ value = $validationAgentName }
            validationAgentVersion = @{ value = $validationAgentVersion }
            followUpAgentName = @{ value = $effectiveFollowUpAgentName }
            followUpAgentVersion = @{ value = $effectiveFollowUpAgentVersion }
            modelDeploymentName = @{ value = $modelDeploymentName }
            defaultDocumentId = @{ value = $DocumentId }
            batchSize = @{ value = $BatchSize }
            persistenceProvider = @{ value = $PersistenceProvider }
            analysisDbConnectionString = @{ value = $AnalysisDbConnectionString }
            cosmosEndpoint = @{ value = $CosmosEndpoint }
            cosmosDatabaseName = @{ value = $CosmosDatabaseName }
            cosmosContainerName = @{ value = $CosmosContainerName }
            cosmosSummaryContainerName = @{ value = $CosmosSummaryContainerName }
            cosmosAccountName = @{ value = $CosmosAccountName }
            cosmosCreateIfNotExists = @{ value = $CosmosCreateIfNotExists.IsPresent }
            provisionCosmosResources = @{ value = $ProvisionCosmosResources.IsPresent }
            enablePayloadStorage = @{ value = $EnablePayloadStorage.IsPresent }
            analysisPayloadBlobContainerUri = @{ value = $analysisPayloadContainerUri }
            enableAttachmentOcr = @{ value = $EnableAttachmentOcr.IsPresent }
            useFunctionAnalysisBackend = @{ value = $useFunctionAnalysisBackend }
            analysisFunctionBaseUrl = @{ value = $functionAppUrl }
            analysisFunctionKey = @{ value = $functionKey }
        }
    }
    $frontendParameters | ConvertTo-Json -Depth 10 | Set-Content -Path $frontendParametersFile -Encoding UTF8

    Write-Host "Previewing frontend Bicep changes..." -ForegroundColor Yellow
    $whatIfArgs = @(
        'deployment', 'group', 'what-if',
        '--resource-group', $FrontendResourceGroupName,
        '--template-file', $frontendBicep,
        '--parameters', "@$frontendParametersFile",
        '--only-show-errors',
        '--no-pretty-print'
    )
    Invoke-NativeChecked -Command 'az' -Arguments $whatIfArgs -FailureMessage "Frontend Bicep what-if failed."

    $frontendDeploymentName = "doed-web-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $frontendDeployArgs = @(
        'deployment', 'group', 'create',
        '--name', $frontendDeploymentName,
        '--resource-group', $FrontendResourceGroupName,
        '--template-file', $frontendBicep,
        '--parameters', "@$frontendParametersFile",
        '--only-show-errors',
        '--query', 'properties.outputs',
        '-o', 'json'
    )

    $frontendOutputsJson = az @frontendDeployArgs
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($frontendOutputsJson)) {
        throw "Frontend Bicep deployment failed."
    }

    $frontendOutputs = $frontendOutputsJson | ConvertFrom-Json
    $webAppName = $frontendOutputs.webAppName.value
    $webAppUrl = $frontendOutputs.webAppUrl.value
    $webAppPrincipalId = $frontendOutputs.webAppPrincipalId.value
    $CosmosEndpoint = $frontendOutputs.effectiveCosmosEndpoint.value
    $CosmosAccountName = $frontendOutputs.effectiveCosmosAccountName.value
} finally {
    if (Test-Path $frontendParametersFile) {
        Remove-Item $frontendParametersFile -Force -ErrorAction SilentlyContinue
    }
}

if ([string]::IsNullOrWhiteSpace($webAppName) -or [string]::IsNullOrWhiteSpace($webAppPrincipalId)) {
    throw "Frontend deployment completed without expected webAppName/webAppPrincipalId outputs."
}
if ($useFunctionAnalysisBackend) {
    if ([string]::IsNullOrWhiteSpace($CosmosEndpoint) -or [string]::IsNullOrWhiteSpace($CosmosAccountName)) {
        throw "Frontend deployment completed without a usable Cosmos endpoint and account name."
    }

    Write-Host "Wiring shared Cosmos and payload storage access..." -ForegroundColor Yellow
    $functionPrincipalId = az functionapp identity show `
    --name $functionAppName `
    --resource-group $ResourceGroupName `
    --query principalId `
    -o tsv `
    --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionPrincipalId)) {
        throw "Could not resolve the Function App managed identity."
    }

    $cosmosAccountId = az cosmosdb show `
    --name $CosmosAccountName `
    --resource-group $CosmosResourceGroupName `
    --query id `
    -o tsv `
    --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($cosmosAccountId)) {
        throw "Could not resolve Cosmos account '$CosmosAccountName' in '$CosmosResourceGroupName'."
    }
    $cosmosContributorRoleId = "$cosmosAccountId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
    foreach ($principalId in @($functionPrincipalId, $webAppPrincipalId)) {
        $existingCosmosRole = az cosmosdb sql role assignment list `
        --account-name $CosmosAccountName `
        --resource-group $CosmosResourceGroupName `
        --query "[?principalId=='$principalId'].id | [0]" `
        -o tsv `
        --only-show-errors
        if ([string]::IsNullOrWhiteSpace($existingCosmosRole)) {
            Invoke-NativeChecked -Command 'az' -Arguments @(
                'cosmosdb', 'sql', 'role', 'assignment', 'create',
                '--account-name', $CosmosAccountName,
                '--resource-group', $CosmosResourceGroupName,
                '--scope', '/',
                '--principal-id', $principalId,
                '--role-definition-id', $cosmosContributorRoleId,
                '--output', 'none'
            ) -FailureMessage "Failed to grant Cosmos data access to principal '$principalId'."
        }
    }

    Invoke-NativeChecked -Command 'az' -Arguments @(
    'functionapp', 'config', 'appsettings', 'set',
    '--name', $functionAppName,
    '--resource-group', $ResourceGroupName,
    '--settings',
    "COSMOS_ENDPOINT=$CosmosEndpoint",
    "COSMOS_DATABASE_NAME=$CosmosDatabaseName",
    "COSMOS_RUNS_CONTAINER_NAME=$CosmosContainerName",
    "COSMOS_SUMMARIES_CONTAINER_NAME=$CosmosSummaryContainerName",
    "ANALYSIS_PAYLOAD_CONTAINER_NAME=$analysisPayloadContainerName",
    '--output', 'none'
    ) -FailureMessage "Failed to configure the Function App for shared Cosmos persistence."

    Invoke-NativeChecked -Command 'az' -Arguments @(
    'storage', 'container', 'create',
    '--account-name', $functionStorageAccountName,
    '--name', $analysisPayloadContainerName,
    '--auth-mode', 'login',
    '--output', 'none'
    ) -FailureMessage "Failed to create the analysis payload container."

    $functionStorageId = az storage account show `
    --name $functionStorageAccountName `
    --resource-group $ResourceGroupName `
    --query id `
    -o tsv `
    --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionStorageId)) {
        throw "Could not resolve Function storage account '$functionStorageAccountName'."
    }
    $existingPayloadRole = az role assignment list `
    --assignee $webAppPrincipalId `
    --role 'Storage Blob Data Contributor' `
    --scope $functionStorageId `
    --query '[0].id' `
    -o tsv `
    --only-show-errors
    if ([string]::IsNullOrWhiteSpace($existingPayloadRole)) {
        Invoke-NativeChecked -Command 'az' -Arguments @(
        'role', 'assignment', 'create',
        '--assignee-object-id', $webAppPrincipalId,
        '--assignee-principal-type', 'ServicePrincipal',
        '--role', 'Storage Blob Data Contributor',
        '--scope', $functionStorageId,
        '--output', 'none',
        '--only-show-errors'
        ) -FailureMessage "Failed to grant the frontend access to Function payload blobs."
    }
}

Write-Host "Granting web app managed identity access to Foundry agents..." -ForegroundColor Yellow
$foundryRoles = @(
    @{ Name = 'Foundry Project Runtime User'; Scope = $foundryProjectResourceId },
    @{ Name = 'Foundry Agent Consumer'; Scope = $foundryProjectResourceId }
)

foreach ($role in $foundryRoles) {
    $existingFoundryRole = az role assignment list `
        --assignee $webAppPrincipalId `
        --role $role.Name `
        --scope $role.Scope `
        --query '[0].id' `
        -o tsv `
        --only-show-errors

    if ([string]::IsNullOrWhiteSpace($existingFoundryRole)) {
        Invoke-NativeChecked -Command 'az' -Arguments @(
            'role', 'assignment', 'create',
            '--assignee-object-id', $webAppPrincipalId,
            '--assignee-principal-type', 'ServicePrincipal',
            '--role', $role.Name,
            '--scope', $role.Scope,
            '--only-show-errors',
            '--output', 'none'
        ) -FailureMessage "Failed to assign $($role.Name) role to the web app managed identity."
        Write-Host "$($role.Name) role assigned." -ForegroundColor Green
    } else {
        Write-Host "$($role.Name) role assignment already exists." -ForegroundColor Green
    }
}

if (-not $SkipFrontendPublish) {
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Yellow
    Write-Host "Step 4/4: Publishing frontend app" -ForegroundColor Yellow
    Write-Host "============================================" -ForegroundColor Yellow

    $publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("doed-web-publish-{0}" -f ([guid]::NewGuid().ToString('N')))
    $publishDir = Join-Path $publishRoot "publish"
    $zipPath = Join-Path $publishRoot "webapp.zip"

    try {
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        Invoke-NativeChecked -Command 'dotnet' -Arguments @('publish', $frontendProject, '-c', 'Release', '-o', $publishDir) -FailureMessage "dotnet publish failed."

        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }
        Invoke-NativeChecked -Command 'tar' -Arguments @('-a', '-c', '-f', $zipPath, '-C', $publishDir, '.') -FailureMessage "Failed to create frontend deployment package."

        Invoke-NativeChecked -Command 'az' -Arguments @(
            'webapp', 'deploy',
            '--resource-group', $FrontendResourceGroupName,
            '--name', $webAppName,
            '--src-path', $zipPath,
            '--type', 'zip',
            '--clean', 'true',
            '--restart', 'true',
            '--output', 'none'
        ) -FailureMessage "Frontend web app publish failed."
    } finally {
        if (Test-Path $publishRoot) {
            Remove-Item $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} else {
    Write-Host "Skipping frontend publish by request." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "Full deployment complete" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host "Function App: $functionAppName" -ForegroundColor White
Write-Host "Frontend App: $webAppName" -ForegroundColor White
Write-Host "Frontend URL: $webAppUrl" -ForegroundColor Cyan
Write-Host ""