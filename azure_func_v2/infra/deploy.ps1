# ============================================================================
# Deploy DoED Regulatory Comments Azure Function Infrastructure
# ============================================================================
#
# This script deploys all Azure resources required for the regulatory
# comments processing Azure Function.
# Flex Consumption is the default hosting plan. Use -UsePremium to opt into
# Elastic Premium; the script will still fall back to Flex if Premium validation fails.
#
# Prerequisites:
# - Azure CLI installed (az --version)
# - Logged in to Azure (az login)
# - Bicep CLI installed (az bicep install)
#
# Usage:
#   .\deploy.ps1 -ResourceGroupName "rg-doed-comments" -Location "eastus" -RegulationsGovApiKey "your-api-key"
#   .\deploy.ps1 -Location "eastus" -RegulationsGovApiKey "your-api-key" -UsePremium
#
# ============================================================================

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-doed-comments",

    [Parameter(Mandatory=$false)]
    [string]$BaseName = "doed-comments",
    
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
    [int]$BatchSize = 5,
    
    [Parameter(Mandatory=$false)]
    [int]$GptCapacity = 10,

    [Parameter(Mandatory=$false)]
    [string]$DeploymentSuffix = "",

    [Parameter(Mandatory=$false)]
    [int]$EmbeddingCapacity = 10,

    [Parameter(Mandatory=$false)]
    [string]$FoundryProjectEndpoint = "",

    [Parameter(Mandatory=$false)]
    [string]$AgentDeploymentOutputPath = "",

    [Parameter(Mandatory=$false)]
    [switch]$UsePremium,

    [Parameter(Mandatory=$false)]
    [switch]$IncludeTags,

    [Parameter(Mandatory=$false)]
    [string]$DeploymentTagName = "",

    [Parameter(Mandatory=$false)]
    [string]$DeploymentTagValue = "",

    [Parameter(Mandatory=$false)]
    [string]$ExistingFunctionStorageAccountName = ""
)

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$ErrorActionPreference = 'Continue'
$env:AZURE_CORE_ONLY_SHOW_ERRORS = 'true'

if ($IncludeTags) {
    if ([string]::IsNullOrWhiteSpace($DeploymentTagName)) {
        $DeploymentTagName = Read-Host "Enter Azure resource tag name"
    }
    if ([string]::IsNullOrWhiteSpace($DeploymentTagValue)) {
        $DeploymentTagValue = Read-Host "Enter Azure resource tag value"
    }
    if ([string]::IsNullOrWhiteSpace($DeploymentTagName) -or [string]::IsNullOrWhiteSpace($DeploymentTagValue)) {
        throw "Deployment tag name and value are required when -IncludeTags is passed."
    }
}

function Get-AgentPrompt {
    param(
        [Parameter(Mandatory=$true)]
        [string]$PromptsFilePath,

        [Parameter(Mandatory=$true)]
        [string]$SectionName
    )

    if (-not (Test-Path $PromptsFilePath)) {
        throw "Agent prompts file not found: $PromptsFilePath"
    }

    $promptsContent = Get-Content -Path $PromptsFilePath -Raw
    $sectionHeader = "## $SectionName"
    $promptHeader = "### Prompt"
    $fence = ([char]96).ToString() + ([char]96).ToString() + ([char]96).ToString()

    $sectionStart = $promptsContent.IndexOf($sectionHeader)
    if ($sectionStart -lt 0) {
        throw "Could not locate prompt block for section '$SectionName' in $PromptsFilePath"
    }

    $promptHeaderStart = $promptsContent.IndexOf($promptHeader, $sectionStart)
    if ($promptHeaderStart -lt 0) {
        throw "Could not locate prompt block for section '$SectionName' in $PromptsFilePath"
    }

    $openingFenceStart = $promptsContent.IndexOf($fence, $promptHeaderStart)
    if ($openingFenceStart -lt 0) {
        throw "Could not locate prompt block for section '$SectionName' in $PromptsFilePath"
    }

    $promptStart = $openingFenceStart + $fence.Length
    if ($promptStart -lt $promptsContent.Length -and $promptsContent[$promptStart] -eq "`r") {
        $promptStart++
    }
    if ($promptStart -lt $promptsContent.Length -and $promptsContent[$promptStart] -eq "`n") {
        $promptStart++
    }

    $closingFenceStart = $promptsContent.IndexOf($fence, $promptStart)
    if ($closingFenceStart -lt 0) {
        throw "Could not locate prompt block for section '$SectionName' in $PromptsFilePath"
    }

    return $promptsContent.Substring($promptStart, $closingFenceStart - $promptStart).Trim()
}

function Normalize-AgentPrompt {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Prompt
    )

    $normalized = $Prompt.Normalize([Text.NormalizationForm]::FormKC)
    $normalized = $normalized.Replace(([char]0x2011).ToString(), '-')
    $normalized = $normalized.Replace(([char]0x2013).ToString(), '-')
    $normalized = $normalized.Replace(([char]0x2014).ToString(), '-')
    $normalized = $normalized.Replace(([char]0x2018).ToString(), "'")
    $normalized = $normalized.Replace(([char]0x2019).ToString(), "'")
    $normalized = $normalized.Replace(([char]0x201C).ToString(), '"')
    $normalized = $normalized.Replace(([char]0x201D).ToString(), '"')
    $normalized = $normalized.Replace(([char]0x2022).ToString(), '-')
    $normalized = $normalized.Replace(([char]0x2192).ToString(), '->')
    $normalized = $normalized.Replace(([char]0x2260).ToString(), '!=')
    $normalized = [regex]::Replace($normalized, '[\u2500-\u257F]', '-')
    $normalized = [regex]::Replace($normalized, '[^\u0000-\u007F]', '')

    return $normalized
}

function Get-DeploymentResourceName {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceType,

        [Parameter(Mandatory=$true)]
        [string]$NamePrefix
    )

    $resourceName = az resource list `
        --resource-group $ResourceGroupName `
        --resource-type $ResourceType `
        --query "[?starts_with(name, '$NamePrefix')].name | [0]" `
        -o tsv 2>$null

    if ([string]::IsNullOrWhiteSpace($resourceName)) {
        return $null
    }

    return $resourceName.Trim()
}

function Get-DeploymentSuffixFromResourceName {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceName,

        [Parameter(Mandatory=$true)]
        [string]$Prefix
    )

    if (-not $ResourceName.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return $ResourceName.Substring($Prefix.Length)
}

function New-DeploymentSuffix {
    return ([guid]::NewGuid().ToString('N').Substring(0, 13)).ToLowerInvariant()
}

function Resolve-DeploymentSuffix {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$BaseName,

        [Parameter(Mandatory=$false)]
        [string]$RequestedSuffix
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedSuffix)) {
        return (New-Object psobject -Property @{
            Suffix = $RequestedSuffix.ToLowerInvariant()
            Source = 'provided'
        })
    }

    $resourceCandidates = @(
        @{ Type = 'Microsoft.CognitiveServices/accounts'; Prefix = "aif-$BaseName-" },
        @{ Type = 'Microsoft.CognitiveServices/accounts'; Prefix = "docint-$BaseName-" },
        @{ Type = 'Microsoft.Web/sites'; Prefix = "func-$BaseName-prem-" },
        @{ Type = 'Microsoft.Web/sites'; Prefix = "func-$BaseName-" },
        @{ Type = 'Microsoft.KeyVault/vaults'; Prefix = "kv-$BaseName-" }
    )

    foreach ($candidate in $resourceCandidates) {
        $resourceName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType $candidate.Type -NamePrefix $candidate.Prefix
        if ([string]::IsNullOrWhiteSpace($resourceName)) {
            continue
        }

        $suffix = Get-DeploymentSuffixFromResourceName -ResourceName $resourceName -Prefix $candidate.Prefix
        if (-not [string]::IsNullOrWhiteSpace($suffix)) {
            return (New-Object psobject -Property @{
                Suffix = $suffix
                Source = 'existing'
            })
        }
    }

    return (New-Object psobject -Property @{
        Suffix = (New-DeploymentSuffix)
        Source = 'generated'
    })
}

function Get-FoundryProjectEndpoint {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FoundryName,

        [Parameter(Mandatory=$true)]
        [string]$ProjectName
    )

    return "https://$FoundryName.cognitiveservices.azure.com/api/projects/$ProjectName"
}

function Add-PreferredFoundryDeployment {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$AccountName,

        [Parameter(Mandatory=$true)]
        [string]$DeploymentName,

        [Parameter(Mandatory=$true)]
        [string]$ModelName,

        [Parameter(Mandatory=$true)]
        [string]$ModelVersion,

        [Parameter(Mandatory=$true)]
        [string]$SkuName,

        [Parameter(Mandatory=$true)]
        [int]$Capacity
    )

    $existingDeploymentName = az cognitiveservices account deployment list `
        --name $AccountName `
        --resource-group $ResourceGroupName `
        --query "[?name=='$DeploymentName'].name | [0]" `
        -o tsv 2>$null

    if (-not [string]::IsNullOrWhiteSpace($existingDeploymentName)) {
        Write-Host "Preferred Foundry deployment $DeploymentName already exists." -ForegroundColor Green
        return $true
    }

    Write-Host "Attempting preferred Foundry deployment $DeploymentName ($ModelName $ModelVersion)..." -ForegroundColor Yellow
    $createOutput = az cognitiveservices account deployment create `
        --name $AccountName `
        --resource-group $ResourceGroupName `
        --deployment-name $DeploymentName `
        --model-format OpenAI `
        --model-name $ModelName `
        --model-version $ModelVersion `
        --sku-name $SkuName `
        --sku-capacity $Capacity `
        --output json 2>&1 | Out-String

    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: Preferred Foundry deployment $DeploymentName failed. Continuing with the fallback model." -ForegroundColor Yellow
        Write-Host $createOutput -ForegroundColor DarkGray
        return $false
    }

    Write-Host "Preferred Foundry deployment $DeploymentName created." -ForegroundColor Green
    return $true
}

function Invoke-AgentCreationWorkflow {
    param(
        [Parameter(Mandatory=$true)]
        [string]$AiEndpoint,

        [Parameter(Mandatory=$true)]
        [string]$FunctionAppName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$ModelDeployment,

        [Parameter(Mandatory=$false)]
        [string]$DeploymentOutputPath = ""
    )

    $agentPromptsPath = Resolve-Path (Join-Path $PSScriptRoot "..\AGENT_PROMPTS.md")
    $agentDefinitions = @(
        [ordered]@{
            SectionName = 'CATEGORIZATION_AGENT'
            DisplayName = 'Categorization Agent'
            AgentName = 'RegulatoryCommentCategorizationAgent'
            Description = 'Categorizes individual regulatory comments using DoED methodology'
            EnvPrefix = 'CATEGORIZATION'
        },
        [ordered]@{
            SectionName = 'GROUPING_AGENT'
            DisplayName = 'Grouping Agent'
            AgentName = 'RegulatoryCommentGroupingAgent'
            Description = 'Groups categorized regulatory comments using DoED synthesis methodology'
            EnvPrefix = 'GROUPING'
        },
        [ordered]@{
            SectionName = 'VALIDATION_AGENT'
            DisplayName = 'Validation Agent'
            AgentName = 'RegulatoryCommentValidationAgent'
            Description = 'Validates grouped regulatory comment analysis and applies minimal corrective cleanup'
            EnvPrefix = 'VALIDATION'
        },
        [ordered]@{
            SectionName = 'FOLLOWUP_AGENT'
            DisplayName = 'Follow-up Q&A Agent'
            AgentName = 'RegulatoryCommentFollowUpAgent'
            Description = 'Answers grounded follow-up questions about a completed regulatory comment analysis'
            EnvPrefix = 'FOLLOWUP'
        }
    )

    if ([string]::IsNullOrWhiteSpace($AiEndpoint)) {
        Write-Host "Foundry project endpoint unavailable. Skipping agent creation." -ForegroundColor Yellow
        Write-Host "If you want this script to create Foundry agents too, pass -FoundryProjectEndpoint \"https://<resource-name>.cognitiveservices.azure.com/api/projects/<project-name>\"." -ForegroundColor Gray
        return
    }

    Write-Host "============================================" -ForegroundColor Yellow
    Write-Host "Creating AI Agents in Azure AI Foundry..." -ForegroundColor Yellow
    Write-Host "============================================" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Foundry Project Endpoint: $AiEndpoint" -ForegroundColor Gray

    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    $pythonExe = Join-Path $repoRoot ".venv\Scripts\python.exe"
    if (-not (Test-Path $pythonExe)) {
        $pythonExe = "python"
    }

    $agentCreationRequirements = Join-Path $PSScriptRoot "requirements-agent-creation.txt"
    & $pythonExe -c "import azure.ai.projects; import azure.identity" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installing agent-creation Python dependencies..." -ForegroundColor Yellow
        & $pythonExe -m pip install --disable-pip-version-check -r $agentCreationRequirements
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install agent-creation dependencies from $agentCreationRequirements"
        }
    }

    $agentDefinitionsPayload = @()
    foreach ($agentDefinition in $agentDefinitions) {
        $agentPrompt = Get-AgentPrompt -PromptsFilePath $agentPromptsPath -SectionName $agentDefinition.SectionName
        $agentDefinitionsPayload += [ordered]@{
            env_prefix = $agentDefinition.EnvPrefix
            display_name = $agentDefinition.DisplayName
            agent_name = $agentDefinition.AgentName
            description = $agentDefinition.Description
            model = $ModelDeployment
            instructions = (Normalize-AgentPrompt -Prompt $agentPrompt)
        }
    }

    $definitionsFile = Join-Path $env:TEMP ("foundry-agent-definitions-{0}.json" -f ([guid]::NewGuid().ToString('N')))
    $helperScript = Join-Path $PSScriptRoot "create_foundry_agents.py"
    $createdAgents = @{}

    try {
        $agentDefinitionsPayload | ConvertTo-Json -Depth 10 | Set-Content -Path $definitionsFile -Encoding UTF8

        $attemptCount = 8
        $retryDelaySeconds = 30
        $agentCreationResult = $null
        $agentCreationRaw = $null

        for ($attempt = 1; $attempt -le $attemptCount; $attempt++) {
            Write-Host "Creating agent versions with Azure AI Projects SDK and Entra auth (attempt $attempt of $attemptCount)..." -ForegroundColor Yellow
            $agentCreationRaw = & $pythonExe $helperScript `
                --project-endpoint $AiEndpoint `
                --definitions-file $definitionsFile `
                --required-env-prefix CATEGORIZATION `
                --required-env-prefix GROUPING `
                --required-env-prefix VALIDATION `
                --required-env-prefix FOLLOWUP 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                Write-Host "ERROR: Agent creation helper failed." -ForegroundColor Red
                Write-Host $agentCreationRaw
                break
            }

            try {
                $agentCreationResult = $agentCreationRaw | ConvertFrom-Json
            } catch {
                Write-Host "ERROR: Agent creation helper returned invalid JSON." -ForegroundColor Red
                Write-Host $agentCreationRaw
                break
            }

            $allErrors = @($agentCreationResult.errors.PSObject.Properties | ForEach-Object { [string]$_.Value })
            $projectNotFoundOnly = ($allErrors.Count -gt 0 -and @($allErrors | Where-Object { $_ -notmatch 'Project not found' }).Count -eq 0)
            if (-not $projectNotFoundOnly) {
                break
            }

            if ($attempt -lt $attemptCount) {
                Write-Host "Foundry project is not ready for agent creation yet. Retrying in $retryDelaySeconds seconds..." -ForegroundColor Yellow
                Start-Sleep -Seconds $retryDelaySeconds
            }
        }

        if ($agentCreationResult) {
            foreach ($agentDefinition in $agentDefinitions) {
                $createdAgent = $agentCreationResult.created.($agentDefinition.EnvPrefix)
                if ($createdAgent) {
                    $createdAgents[$agentDefinition.EnvPrefix] = @{
                        Id = $createdAgent.Id
                        Name = $createdAgent.Name
                        Version = $createdAgent.Version
                        Model = $createdAgent.Model
                    }
                    Write-Host "$($agentDefinition.DisplayName) created: $($createdAgent.Id)" -ForegroundColor Green
                } elseif ($agentCreationResult.errors.($agentDefinition.EnvPrefix)) {
                    Write-Host "ERROR creating $($agentDefinition.DisplayName): $($agentCreationResult.errors.($agentDefinition.EnvPrefix))" -ForegroundColor Red
                }
            }
        }

        if ($createdAgents.ContainsKey('CATEGORIZATION') `
            -and $createdAgents.ContainsKey('GROUPING') `
            -and $createdAgents.ContainsKey('FOLLOWUP')) {
            Write-Host ""
            Write-Host "Updating Function App settings with agent endpoint, names, and versions..." -ForegroundColor Yellow

            $allowedModelDeployments = (@('gpt-4o', $ModelDeployment) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique) -join ','

            $functionAppSettings = @(
                "FOUNDRY_PROJECT_ENDPOINT=$AiEndpoint",
                "ALLOWED_MODEL_DEPLOYMENTS=$allowedModelDeployments",
                "CATEGORIZATION_AGENT_NAME=$($createdAgents['CATEGORIZATION'].Name)",
                "CATEGORIZATION_AGENT_VERSION=$($createdAgents['CATEGORIZATION'].Version)",
                "CATEGORIZATION_AGENT_MODEL=$($createdAgents['CATEGORIZATION'].Model)",
                "GROUPING_AGENT_NAME=$($createdAgents['GROUPING'].Name)",
                "GROUPING_AGENT_VERSION=$($createdAgents['GROUPING'].Version)",
                "GROUPING_AGENT_MODEL=$($createdAgents['GROUPING'].Model)",
                "FOLLOWUP_AGENT_NAME=$($createdAgents['FOLLOWUP'].Name)",
                "FOLLOWUP_AGENT_VERSION=$($createdAgents['FOLLOWUP'].Version)",
                "FOLLOWUP_AGENT_MODEL=$($createdAgents['FOLLOWUP'].Model)"
            )

            if ($createdAgents.ContainsKey('VALIDATION')) {
                $functionAppSettings += @(
                    "VALIDATION_AGENT_NAME=$($createdAgents['VALIDATION'].Name)",
                    "VALIDATION_AGENT_VERSION=$($createdAgents['VALIDATION'].Version)",
                    "VALIDATION_AGENT_MODEL=$($createdAgents['VALIDATION'].Model)"
                )
            } else {
                $functionAppSettings += @(
                    "VALIDATION_AGENT_NAME=",
                    "VALIDATION_AGENT_VERSION=1",
                    "VALIDATION_AGENT_MODEL=$ModelDeployment"
                )
            }

            az functionapp config appsettings set `
                --name $FunctionAppName `
                --resource-group $ResourceGroupName `
                --settings $functionAppSettings `
                --output none

            az functionapp config appsettings delete `
                --name $FunctionAppName `
                --resource-group $ResourceGroupName `
                --setting-names AZURE_AI_AGENT_ENDPOINT AZURE_AI_PROJECT_ENDPOINT AZURE_AI_SEARCH_SERVICE_NAME AZURE_AI_SEARCH_ENDPOINT AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT CATEGORIZATION_AGENT_ID GROUPING_AGENT_ID VALIDATION_AGENT_ID FOLLOWUP_AGENT_ID DOCUMENTINTELLIGENCE_API_KEY AZURE_DOCUMENT_INTELLIGENCE_API_KEY `
                --output none 2>$null

            if (-not [string]::IsNullOrWhiteSpace($DeploymentOutputPath)) {
                [ordered]@{
                    followUpAgent = [ordered]@{
                        name = $createdAgents['FOLLOWUP'].Name
                        version = $createdAgents['FOLLOWUP'].Version
                        model = $createdAgents['FOLLOWUP'].Model
                    }
                } | ConvertTo-Json -Depth 4 | Set-Content -Path $DeploymentOutputPath -Encoding UTF8
            }

            Write-Host "Function App settings updated." -ForegroundColor Green
            Write-Host ""
            Write-Host "  Foundry Project Endpoint:  $AiEndpoint" -ForegroundColor Cyan
            Write-Host "  Categorization Agent Name: $($createdAgents['CATEGORIZATION'].Name)" -ForegroundColor Cyan
            Write-Host "  Categorization Agent ID:   $($createdAgents['CATEGORIZATION'].Id)" -ForegroundColor DarkGray
            Write-Host "  Grouping Agent Name:       $($createdAgents['GROUPING'].Name)" -ForegroundColor Cyan
            Write-Host "  Grouping Agent ID:         $($createdAgents['GROUPING'].Id)" -ForegroundColor DarkGray

            if ($createdAgents.ContainsKey('VALIDATION')) {
                Write-Host "  Validation Agent Name:     $($createdAgents['VALIDATION'].Name)" -ForegroundColor Cyan
                Write-Host "  Validation Agent ID:       $($createdAgents['VALIDATION'].Id)" -ForegroundColor DarkGray
            } else {
                Write-Host "  Validation Agent:          not created; app setting left blank" -ForegroundColor Yellow
            }

            Write-Host "  Follow-up Q&A Agent Name:  $($createdAgents['FOLLOWUP'].Name)" -ForegroundColor Cyan
            Write-Host "  Follow-up Q&A Agent ID:    $($createdAgents['FOLLOWUP'].Id)" -ForegroundColor DarkGray
        } else {
            Write-Host ""
            throw "One or more required Foundry agents failed to create: categorization, grouping, or follow-up Q&A."
        }
    } finally {
        if (Test-Path $definitionsFile) {
            Remove-Item $definitionsFile -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host ""
}

function Get-DeletedKeyVaultsInResourceGroup {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName
    )

    $deletedVaultsRaw = az keyvault list-deleted --query "[?contains(properties.vaultId, '/resourceGroups/$ResourceGroupName/')].{name:name,location:properties.location,purgeProtectionEnabled:properties.purgeProtectionEnabled}" -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deletedVaultsRaw)) {
        return @()
    }

    try {
        return @($deletedVaultsRaw | ConvertFrom-Json)
    } catch {
        return @()
    }
}

function Get-DeletedCognitiveAccountsInResourceGroup {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName
    )

    $deletedAccountsRaw = az cognitiveservices account list-deleted --query "[?contains(id, '/resourceGroups/$ResourceGroupName/')].{name:name,location:location}" -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deletedAccountsRaw)) {
        return @()
    }

    try {
        return @($deletedAccountsRaw | ConvertFrom-Json)
    } catch {
        return @()
    }
}

function Restore-DeletedKeyVaults {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string[]]$NamePrefixes
    )

    $deletedVaults = Get-DeletedKeyVaultsInResourceGroup -ResourceGroupName $ResourceGroupName
    foreach ($vault in $deletedVaults) {
        if (-not ($NamePrefixes | Where-Object { $vault.name.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) })) {
            continue
        }

        Write-Host "Recovering soft-deleted Key Vault $($vault.name) in $($vault.location)..." -ForegroundColor Yellow
        az keyvault recover --name $vault.name --location $vault.location --resource-group $ResourceGroupName --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to recover Key Vault $($vault.name)." -ForegroundColor Red
            exit 1
        }
    }
}

function Restore-DeletedCognitiveAccounts {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string[]]$NamePrefixes
    )

    $deletedAccounts = Get-DeletedCognitiveAccountsInResourceGroup -ResourceGroupName $ResourceGroupName
    foreach ($accountToRecover in $deletedAccounts) {
        if (-not ($NamePrefixes | Where-Object { $accountToRecover.name.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) })) {
            continue
        }

        Write-Host "Recovering soft-deleted Cognitive Services account $($accountToRecover.name) in $($accountToRecover.location)..." -ForegroundColor Yellow
        az cognitiveservices account recover --name $accountToRecover.name --location $accountToRecover.location --resource-group $ResourceGroupName --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to recover Cognitive Services account $($accountToRecover.name)." -ForegroundColor Red
            exit 1
        }
    }
}

function Get-ActiveFunctionDeployment {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FunctionAppName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName
    )

    $functionAppId = az functionapp show `
        --name $FunctionAppName `
        --resource-group $ResourceGroupName `
        --query id `
        -o tsv `
        --only-show-errors 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionAppId)) {
        return $null
    }

    $deploymentsRaw = az rest `
        --method get `
        --uri "$functionAppId/deployments?api-version=2022-03-01" `
        --only-show-errors `
        -o json 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deploymentsRaw)) {
        return $null
    }

    try {
        $deployments = $deploymentsRaw | ConvertFrom-Json
        return @($deployments.value | Where-Object {
            $_.properties.status -eq 1 -and $_.properties.complete -eq $false
        } | Select-Object -First 1)
    } catch {
        return $null
    }
}

function Wait-Or-Exit-ForActiveFunctionDeployment {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FunctionAppName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName
    )

    $choiceMade = $false
    while ($true) {
        $activeDeployment = Get-ActiveFunctionDeployment -FunctionAppName $FunctionAppName -ResourceGroupName $ResourceGroupName
        if ($null -eq $activeDeployment) {
            return
        }

        $receivedTime = $activeDeployment.properties.received_time
        $message = $activeDeployment.properties.message
        Write-Host "A Function App deployment is already marked in progress." -ForegroundColor Yellow
        if (-not [string]::IsNullOrWhiteSpace($receivedTime)) {
            Write-Host "Started: $receivedTime" -ForegroundColor Yellow
        }
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            Write-Host "Message: $message" -ForegroundColor Yellow
        }

        if (-not $choiceMade) {
            $choice = Read-Host "Wait and monitor until it clears before publishing? [Y/n]"
            if ($choice -match '^(n|no)$') {
                Write-Host "Exiting without starting another publish. Re-run deploy.ps1 after the current Function deployment finishes." -ForegroundColor Yellow
                exit 2
            }
            $choiceMade = $true
        }

        Write-Host "Waiting 60 seconds before checking the active deployment again..." -ForegroundColor Yellow
        Start-Sleep -Seconds 60
    }
}

function New-FunctionZipPackage {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FunctionAppDirectory
    )

    $packageRoot = Join-Path $env:TEMP ("doed-function-package-{0}" -f ([guid]::NewGuid().ToString('N')))
    $packageSource = Join-Path $packageRoot "src"
    $zipPath = Join-Path $packageRoot "functionapp.zip"

    New-Item -ItemType Directory -Path $packageSource -Force | Out-Null

    Get-ChildItem -Path $FunctionAppDirectory -Force | Where-Object {
        $_.Name -notin @(
            'local.settings.json',
            '.venv',
            '.venv311',
            '.python_packages',
            '__pycache__'
        )
    } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $packageSource -Recurse -Force
    }

    Get-ChildItem -Path $packageSource -Recurse -Directory -Force | Where-Object {
        $_.Name -in @('__pycache__', '.pytest_cache')
    } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    tar -a -c -f $zipPath -C $packageSource .
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create portable Function App deployment package."
    }
    return @{
        PackageRoot = $packageRoot
        ZipPath = $zipPath
    }
}

function Publish-FlexFunctionApp {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FunctionAppName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$FunctionAppDirectory
    )

    $package = New-FunctionZipPackage -FunctionAppDirectory $FunctionAppDirectory
    try {
        Write-Host "Deploying Flex Consumption package with Azure CLI remote build..." -ForegroundColor Yellow
        az functionapp deployment source config-zip `
            --name $FunctionAppName `
            --resource-group $ResourceGroupName `
            --src $package.ZipPath `
            --build-remote true `
            --only-show-errors
        $script:LastFlexPublishExitCode = $LASTEXITCODE
    } finally {
        if (Test-Path $package.PackageRoot) {
            Remove-Item $package.PackageRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Ensure-StoragePublicNetworkAccess {
    param(
        [Parameter(Mandatory=$true)]
        [string]$StorageAccountName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [int]$MaxAttempts = 6
    )

    Write-Host "Ensuring storage account public network access is enabled before Function publish..." -ForegroundColor Yellow
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        az storage account update `
            --name $StorageAccountName `
            --resource-group $ResourceGroupName `
            --public-network-access Enabled `
            --default-action Allow `
            --bypass AzureServices `
            --only-show-errors `
            --output none

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Storage network update command failed ($attempt/$MaxAttempts)." -ForegroundColor Yellow
        }

        $networkStateJson = az storage account show `
            --name $StorageAccountName `
            --resource-group $ResourceGroupName `
            --query "{publicNetworkAccess:publicNetworkAccess, defaultAction:networkRuleSet.defaultAction, bypass:networkRuleSet.bypass}" `
            -o json `
            --only-show-errors

        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($networkStateJson)) {
            $networkState = $networkStateJson | ConvertFrom-Json
            if ($networkState.publicNetworkAccess -eq 'Enabled' -and $networkState.defaultAction -eq 'Allow') {
                Write-Host "Storage public network access is enabled." -ForegroundColor Green
                return
            }

            Write-Host "Storage network state is publicNetworkAccess=$($networkState.publicNetworkAccess), defaultAction=$($networkState.defaultAction); retrying ($attempt/$MaxAttempts)..." -ForegroundColor Yellow
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds 10
        }
    }

    throw "Storage account '$StorageAccountName' did not keep public network access enabled after $MaxAttempts attempts."
}

function Wait-ForFlexAppSettingsRemoval {
    param(
        [Parameter(Mandatory=$true)]
        [string]$FunctionAppName,

        [int]$MaxAttempts = 12
    )

    $unsupportedSettings = @('SCM_DO_BUILD_DURING_DEPLOYMENT', 'ENABLE_ORYX_BUILD', 'FUNCTIONS_WORKER_RUNTIME')
    $scmSettingsUrl = "https://$FunctionAppName.scm.azurewebsites.net/api/settings"

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $accessToken = az account get-access-token `
            --resource 'https://management.azure.com/' `
            --query accessToken `
            -o tsv `
            --only-show-errors

        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($accessToken)) {
            try {
                $headers = @{ Authorization = "Bearer $($accessToken.Trim())" }
                $scmSettings = Invoke-RestMethod -Uri $scmSettingsUrl -Headers $headers -Method Get
                $remainingSettings = @($unsupportedSettings | Where-Object {
                    $_ -in $scmSettings.PSObject.Properties.Name
                })

                if ($remainingSettings.Count -eq 0) {
                    return
                }

                Write-Host "Waiting for Flex app-setting cleanup to reach the deployment endpoint ($attempt/$MaxAttempts): $($remainingSettings -join ', ')" -ForegroundColor Yellow
            } catch {
                Write-Host "Could not verify Flex app settings at the deployment endpoint yet ($attempt/$MaxAttempts): $($_.Exception.Message)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "Could not acquire a token to verify Flex app-setting cleanup ($attempt/$MaxAttempts)." -ForegroundColor Yellow
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds 5
        }
    }

    throw "Flex app settings were not removed from the deployment endpoint within $($MaxAttempts * 5) seconds."
}

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
if (-not [string]::IsNullOrWhiteSpace($FoundryProjectEndpoint)) {
    Write-Host "Foundry Project Endpoint: $FoundryProjectEndpoint" -ForegroundColor White
}
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

$deploymentSuffixResolution = Resolve-DeploymentSuffix -ResourceGroupName $ResourceGroupName -BaseName $BaseName -RequestedSuffix $DeploymentSuffix
$DeploymentSuffix = $deploymentSuffixResolution.Suffix
$suffixSource = $deploymentSuffixResolution.Source

$keyVaultNamePrefix = "kv-$BaseName-$DeploymentSuffix"
$functionAppNamePrefix = if ($UsePremium) { "func-$BaseName-prem-$DeploymentSuffix" } else { "func-$BaseName-$DeploymentSuffix" }
$aiFoundryNamePrefix = "aif-$BaseName-$DeploymentSuffix"
$documentIntelligenceNamePrefix = "docint-$BaseName-$DeploymentSuffix"
$searchServiceNamePrefix = "srch-$BaseName-$DeploymentSuffix"
$storageAccountNamePrefix = ("st{0}{1}" -f $BaseName, $DeploymentSuffix).Replace('-', '')

if ([string]::IsNullOrWhiteSpace($ExistingFunctionStorageAccountName)) {
    $detectedStorageAccountName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.Storage/storageAccounts' -NamePrefix $storageAccountNamePrefix
    if (-not [string]::IsNullOrWhiteSpace($detectedStorageAccountName)) {
        $ExistingFunctionStorageAccountName = $detectedStorageAccountName
        Write-Host "Reusing existing Function storage account: $ExistingFunctionStorageAccountName" -ForegroundColor Yellow
    }
}

Write-Host "Deployment Suffix: $DeploymentSuffix ($suffixSource)" -ForegroundColor White
Write-Host ""

Restore-DeletedKeyVaults -ResourceGroupName $ResourceGroupName -NamePrefixes @($keyVaultNamePrefix)
Restore-DeletedCognitiveAccounts -ResourceGroupName $ResourceGroupName -NamePrefixes @($documentIntelligenceNamePrefix, $aiFoundryNamePrefix)

# Deploy Bicep template
Write-Host ""
Write-Host "Deploying infrastructure (this may take 10-15 minutes)..." -ForegroundColor Yellow
Write-Host ""

$deployerPrincipalId = ""
$deployerPrincipalType = ""
$account = az account show 2>$null | ConvertFrom-Json

if ($account -and $account.user) {
    $loginType = [string]$account.user.type
    $loginName = [string]$account.user.name

    if ($loginType -eq 'user') {
        $deployerPrincipalId = az ad signed-in-user show --query id -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($deployerPrincipalId)) {
            $deployerPrincipalType = 'User'
        }
    } elseif ($loginType -eq 'servicePrincipal') {
        $deployerPrincipalId = az ad sp show --id $loginName --query id -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($deployerPrincipalId)) {
            $deployerPrincipalType = 'ServicePrincipal'
        }
    }
}

if ([string]::IsNullOrWhiteSpace($deployerPrincipalId)) {
    $deployerPrincipalId = ""
    $deployerPrincipalType = ""
}

function Invoke-InfrastructureDeployment {
    param(
        [Parameter(Mandatory=$true)]
        [string]$DeploymentName,

        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$TemplateFile,

        [Parameter(Mandatory=$true)]
        [string]$Location,

        [Parameter(Mandatory=$true)]
        [int]$GptCapacity,

        [Parameter(Mandatory=$true)]
        [int]$EmbeddingCapacity,

        [Parameter(Mandatory=$true)]
        [string]$RegulationsGovApiKey,

        [Parameter(Mandatory=$true)]
        [string]$DocumentId,

        [Parameter(Mandatory=$true)]
        [int]$BatchSize,

        [Parameter(Mandatory=$true)]
        [string]$DeployerPrincipalId,

        [Parameter(Mandatory=$true)]
        [string]$DeployerPrincipalType,

        [Parameter(Mandatory=$true)]
        [string]$BaseName,

        [Parameter(Mandatory=$true)]
        [string]$DeploymentSuffix,

        [Parameter(Mandatory=$true)]
        [string]$HostingMode,

        [Parameter(Mandatory=$false)]
        [string]$DeploymentTagName = "",

        [Parameter(Mandatory=$false)]
        [string]$DeploymentTagValue = "",

        [Parameter(Mandatory=$false)]
        [string]$ExistingFunctionStorageAccountName = ""
    )

    $output = az deployment group create `
        --name $DeploymentName `
        --resource-group $ResourceGroupName `
        --template-file $TemplateFile `
        --parameters baseName=$BaseName `
        --parameters deploymentSuffix=$DeploymentSuffix `
        --parameters location=$Location `
        --parameters gptCapacity=$GptCapacity `
        --parameters embeddingCapacity=$EmbeddingCapacity `
        --parameters regulationsGovApiKey=$RegulationsGovApiKey `
        --parameters documentId=$DocumentId `
        --parameters batchSize=$BatchSize `
        --parameters deployerPrincipalId="$DeployerPrincipalId" `
        --parameters deployerPrincipalType="$DeployerPrincipalType" `
        --parameters hostingMode=$HostingMode `
        --parameters resourceTagName="$DeploymentTagName" `
        --parameters resourceTagValue="$DeploymentTagValue" `
        --parameters existingFunctionStorageAccountName="$ExistingFunctionStorageAccountName" `
        --only-show-errors `
        --output json 2>&1

    return @{
        Output = ($output | Where-Object { $_ -is [string] } | Out-String)
        ExitCode = $LASTEXITCODE
    }
}

function Test-InfrastructureDeployment {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory=$true)]
        [string]$TemplateFile,

        [Parameter(Mandatory=$true)]
        [string]$Location,

        [Parameter(Mandatory=$true)]
        [int]$GptCapacity,

        [Parameter(Mandatory=$true)]
        [int]$EmbeddingCapacity,

        [Parameter(Mandatory=$true)]
        [string]$RegulationsGovApiKey,

        [Parameter(Mandatory=$true)]
        [string]$DocumentId,

        [Parameter(Mandatory=$true)]
        [int]$BatchSize,

        [Parameter(Mandatory=$true)]
        [string]$DeployerPrincipalId,

        [Parameter(Mandatory=$true)]
        [string]$DeployerPrincipalType,

        [Parameter(Mandatory=$true)]
        [string]$BaseName,

        [Parameter(Mandatory=$true)]
        [string]$DeploymentSuffix,

        [Parameter(Mandatory=$true)]
        [string]$HostingMode,

        [Parameter(Mandatory=$false)]
        [string]$DeploymentTagName = "",

        [Parameter(Mandatory=$false)]
        [string]$DeploymentTagValue = "",

        [Parameter(Mandatory=$false)]
        [string]$ExistingFunctionStorageAccountName = ""
    )

    $output = az deployment group validate `
        --resource-group $ResourceGroupName `
        --template-file $TemplateFile `
        --parameters baseName=$BaseName `
        --parameters deploymentSuffix=$DeploymentSuffix `
        --parameters location=$Location `
        --parameters gptCapacity=$GptCapacity `
        --parameters embeddingCapacity=$EmbeddingCapacity `
        --parameters regulationsGovApiKey=$RegulationsGovApiKey `
        --parameters documentId=$DocumentId `
        --parameters batchSize=$BatchSize `
        --parameters deployerPrincipalId="$DeployerPrincipalId" `
        --parameters deployerPrincipalType="$DeployerPrincipalType" `
        --parameters hostingMode=$HostingMode `
        --parameters resourceTagName="$DeploymentTagName" `
        --parameters resourceTagValue="$DeploymentTagValue" `
        --parameters existingFunctionStorageAccountName="$ExistingFunctionStorageAccountName" `
        --only-show-errors `
        --output json 2>&1

    return @{
        Output = ($output | Out-String)
        ExitCode = $LASTEXITCODE
    }
}

$hostingMode = if ($UsePremium) { 'Premium' } else { 'FlexConsumption' }

$validationAttempt = Test-InfrastructureDeployment `
    -ResourceGroupName $ResourceGroupName `
    -TemplateFile "$PSScriptRoot\main.bicep" `
    -Location $Location `
    -GptCapacity $GptCapacity `
    -EmbeddingCapacity $EmbeddingCapacity `
    -RegulationsGovApiKey $RegulationsGovApiKey `
    -DocumentId $DocumentId `
    -BatchSize $BatchSize `
    -DeployerPrincipalId $deployerPrincipalId `
    -DeployerPrincipalType $deployerPrincipalType `
    -BaseName $BaseName `
    -DeploymentSuffix $DeploymentSuffix `
    -HostingMode $hostingMode `
    -DeploymentTagName $(if ($IncludeTags) { $DeploymentTagName } else { "" }) `
    -DeploymentTagValue $(if ($IncludeTags) { $DeploymentTagValue } else { "" }) `
    -ExistingFunctionStorageAccountName $ExistingFunctionStorageAccountName

if ($validationAttempt.ExitCode -ne 0) {
    $premiumValidationOutput = $validationAttempt.Output

    if ($hostingMode -eq 'Premium') {
        Write-Host ""
        Write-Host "Premium hosting validation failed. Falling back to Flex Consumption..." -ForegroundColor Yellow
        $hostingMode = 'FlexConsumption'
        $validationAttempt = Test-InfrastructureDeployment `
            -ResourceGroupName $ResourceGroupName `
            -TemplateFile "$PSScriptRoot\main.bicep" `
            -Location $Location `
            -GptCapacity $GptCapacity `
            -EmbeddingCapacity $EmbeddingCapacity `
            -RegulationsGovApiKey $RegulationsGovApiKey `
            -DocumentId $DocumentId `
            -BatchSize $BatchSize `
            -DeployerPrincipalId $deployerPrincipalId `
            -DeployerPrincipalType $deployerPrincipalType `
            -BaseName $BaseName `
            -DeploymentSuffix $DeploymentSuffix `
            -HostingMode $hostingMode `
            -DeploymentTagName $(if ($IncludeTags) { $DeploymentTagName } else { "" }) `
            -DeploymentTagValue $(if ($IncludeTags) { $DeploymentTagValue } else { "" }) `
            -ExistingFunctionStorageAccountName $ExistingFunctionStorageAccountName
    }

    Write-Host "" 
    if ($validationAttempt.ExitCode -ne 0) {
        Write-Host "Deployment validation failed before resource creation." -ForegroundColor Red
        if (-not [string]::IsNullOrWhiteSpace($premiumValidationOutput)) {
            Write-Host "Premium validation output:" -ForegroundColor Yellow
            Write-Host $premiumValidationOutput
            Write-Host ""
        }
        if ($hostingMode -eq 'FlexConsumption') {
            Write-Host "Flex Consumption validation output:" -ForegroundColor Yellow
        }
        Write-Host $validationAttempt.Output
        exit 1
    }
}

Write-Host "Hosting mode selected: $hostingMode" -ForegroundColor Cyan
Write-Host "Creating or updating Azure resources with ARM deployment..." -ForegroundColor Yellow

$deploymentName = "doed-comments-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$deploymentAttempt = Invoke-InfrastructureDeployment `
    -DeploymentName $deploymentName `
    -ResourceGroupName $ResourceGroupName `
    -TemplateFile "$PSScriptRoot\main.bicep" `
    -Location $Location `
    -GptCapacity $GptCapacity `
    -EmbeddingCapacity $EmbeddingCapacity `
    -RegulationsGovApiKey $RegulationsGovApiKey `
    -DocumentId $DocumentId `
    -BatchSize $BatchSize `
    -DeployerPrincipalId $deployerPrincipalId `
    -DeployerPrincipalType $deployerPrincipalType `
    -BaseName $BaseName `
    -DeploymentSuffix $DeploymentSuffix `
    -HostingMode $hostingMode `
    -DeploymentTagName $(if ($IncludeTags) { $DeploymentTagName } else { "" }) `
    -DeploymentTagValue $(if ($IncludeTags) { $DeploymentTagValue } else { "" }) `
    -ExistingFunctionStorageAccountName $ExistingFunctionStorageAccountName

$deploymentOutput = $deploymentAttempt.Output
$roleAssignmentOnlyCreateFailure = $false

Write-Host "ARM deployment command completed with exit code $($deploymentAttempt.ExitCode)." -ForegroundColor Gray

if ($deploymentAttempt.ExitCode -ne 0) {
    $errorCodes = @([regex]::Matches([string]$deploymentOutput, '"code"\s*:\s*"([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $nonRoleAssignmentCodes = @($errorCodes | Where-Object { $_ -ne 'RoleAssignmentExists' })

    # RoleAssignmentExists can happen immediately after a full RG recreate while
    # Azure is still reconciling deleted role assignments. If that is the only
    # failure, treat it as retryable instead of fatal.
    if ($nonRoleAssignmentCodes.Count -gt 0) {
        Write-Host ""
        Write-Host "Deployment failed!" -ForegroundColor Red
        Write-Host $deploymentOutput
        exit 1
    }

    $otherErrorOutput = [string]$deploymentOutput
    $otherErrorOutput = [regex]::Replace($otherErrorOutput, '"code"\s*:\s*"RoleAssignmentExists"', '', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $hasOtherErrors = [regex]::IsMatch($otherErrorOutput, '"code"\s*:\s*"[A-Za-z]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($hasOtherErrors) {
        Write-Host ""
        Write-Host "Deployment failed!" -ForegroundColor Red
        Write-Host $deploymentOutput
        exit 1
    }
    $roleAssignmentOnlyCreateFailure = $true
    Write-Host ""
    Write-Host "Note: Some role assignments already existed (non-fatal - permissions are in place)." -ForegroundColor Yellow
}

# Retrieve outputs (query directly in case of partial failure)
$deploymentShowRaw = az deployment group show `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --output json `
    --only-show-errors 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    if ($roleAssignmentOnlyCreateFailure -and $deploymentShowRaw -match 'DeploymentNotFound') {
        Write-Host "" 
        Write-Host "Deployment record was not available after role assignment conflicts. Retrying once..." -ForegroundColor Yellow
        $deploymentName = "doed-comments-$(Get-Date -Format 'yyyyMMdd-HHmmss')-retry"
        $deploymentAttempt = Invoke-InfrastructureDeployment `
            -DeploymentName $deploymentName `
            -ResourceGroupName $ResourceGroupName `
            -TemplateFile "$PSScriptRoot\main.bicep" `
            -Location $Location `
            -GptCapacity $GptCapacity `
            -EmbeddingCapacity $EmbeddingCapacity `
            -RegulationsGovApiKey $RegulationsGovApiKey `
            -DocumentId $DocumentId `
            -BatchSize $BatchSize `
            -DeployerPrincipalId $deployerPrincipalId `
            -DeployerPrincipalType $deployerPrincipalType `
            -BaseName $BaseName `
            -DeploymentSuffix $DeploymentSuffix `
            -HostingMode $hostingMode `
            -DeploymentTagName $(if ($IncludeTags) { $DeploymentTagName } else { "" }) `
            -DeploymentTagValue $(if ($IncludeTags) { $DeploymentTagValue } else { "" }) `
            -ExistingFunctionStorageAccountName $ExistingFunctionStorageAccountName

        $deploymentOutput = $deploymentAttempt.Output
        if ($deploymentAttempt.ExitCode -ne 0) {
            Write-Host "" 
            Write-Host "Deployment retry failed!" -ForegroundColor Red
            Write-Host $deploymentOutput
            exit 1
        }

        $deploymentShowRaw = az deployment group show `
            --name $deploymentName `
            --resource-group $ResourceGroupName `
            --output json `
            --only-show-errors 2>&1 | Out-String
    }
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "" 
    Write-Host "Failed to retrieve deployment outputs." -ForegroundColor Red
    Write-Host $deploymentShowRaw
    exit 1
}

try {
    $result = $deploymentShowRaw | ConvertFrom-Json
} catch {
    Write-Host ""
    Write-Host "Deployment output was not valid JSON." -ForegroundColor Red
    Write-Host $deploymentShowRaw
    exit 1
}

$functionAppName = $result.properties.outputs.functionAppName.value
if ([string]::IsNullOrWhiteSpace($functionAppName)) {
    $roleAssignmentOnlyFailure = $false
    if ($result.properties.error.code -eq 'DeploymentFailed' -and $result.properties.error.details) {
        $nonRoleAssignmentErrors = @($result.properties.error.details | Where-Object { $_.code -ne 'RoleAssignmentExists' })
        $roleAssignmentOnlyFailure = ($nonRoleAssignmentErrors.Count -eq 0)
    }

    if (-not $roleAssignmentOnlyFailure) {
        Write-Host ""
        Write-Host "Deployment completed without the expected outputs. Check the Azure deployment details for the underlying failure." -ForegroundColor Red
        Write-Host $deploymentShowRaw
        exit 1
    }

    Write-Host ""
    Write-Host "Deployment completed, but ARM did not emit outputs because some role assignments already existed." -ForegroundColor Yellow
    Write-Host "Recovering resource names directly from the resource group..." -ForegroundColor Yellow

    $functionAppName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.Web/sites' -NamePrefix $functionAppNamePrefix
    $storageAccountName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.Storage/storageAccounts' -NamePrefix $storageAccountNamePrefix
    $aiFoundryName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.CognitiveServices/accounts' -NamePrefix $aiFoundryNamePrefix
    $documentIntelligenceName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.CognitiveServices/accounts' -NamePrefix $documentIntelligenceNamePrefix
    $searchServiceName = Get-DeploymentResourceName -ResourceGroupName $ResourceGroupName -ResourceType 'Microsoft.Search/searchServices' -NamePrefix $searchServiceNamePrefix

    $aiProjectName = "aiproj-$BaseName"

    if ([string]::IsNullOrWhiteSpace($functionAppName) -or [string]::IsNullOrWhiteSpace($storageAccountName) -or [string]::IsNullOrWhiteSpace($aiFoundryName)) {
        Write-Host ""
        Write-Host "Could not recover the required deployment outputs from existing resources." -ForegroundColor Red
        Write-Host $deploymentShowRaw
        exit 1
    }

    $functionAppUrl = "https://$functionAppName.azurewebsites.net"
    $aiProjectEndpoint = Get-FoundryProjectEndpoint -FoundryName $aiFoundryName -ProjectName $aiProjectName
    $foundryResourceEndpoint = if ($aiFoundryName) { "https://$aiFoundryName.services.ai.azure.com/" } else { '' }
    $documentIntelligenceEndpoint = if ($documentIntelligenceName) { "https://$documentIntelligenceName.cognitiveservices.azure.com/" } else { '' }
    $searchServiceEndpoint = if ($searchServiceName) { "https://$searchServiceName.search.windows.net" } else { '' }
    $modelDeployment = 'gpt-4o'
    $embeddingModelDeployment = 'text-embedding-3-large'
} else {
    $functionAppUrl = $result.properties.outputs.functionAppUrl.value
    $storageAccountName = $result.properties.outputs.storageAccountName.value
    $aiFoundryName = $result.properties.outputs.aiFoundryName.value
    $aiProjectName = $result.properties.outputs.aiProjectName.value
    $aiProjectEndpoint = $result.properties.outputs.aiProjectEndpoint.value
    $foundryResourceEndpoint = $result.properties.outputs.foundryResourceEndpoint.value
    $documentIntelligenceEndpoint = $result.properties.outputs.documentIntelligenceEndpoint.value
    $searchServiceName = $result.properties.outputs.searchServiceName.value
    $searchServiceEndpoint = $result.properties.outputs.searchServiceEndpoint.value
    $modelDeployment = $result.properties.outputs.modelDeploymentName.value
    $embeddingModelDeployment = $result.properties.outputs.embeddingModelDeploymentName.value
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "Deployment Succeeded!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

# Display outputs
Write-Host "Deployment Outputs:" -ForegroundColor Cyan
Write-Host ""
Write-Host "Function App Name:        $functionAppName" -ForegroundColor White
Write-Host "Function App URL:         $functionAppUrl" -ForegroundColor White
Write-Host "Storage Account:          $storageAccountName" -ForegroundColor White
Write-Host "AI Foundry Resource:      $aiFoundryName" -ForegroundColor White
Write-Host "AI Project Name:          $aiProjectName" -ForegroundColor White
Write-Host "AI Project Endpoint:      $aiProjectEndpoint" -ForegroundColor White
if (-not [string]::IsNullOrWhiteSpace($FoundryProjectEndpoint)) {
    Write-Host "Foundry Endpoint Override: $FoundryProjectEndpoint" -ForegroundColor White
}
Write-Host "Foundry Resource Endpoint:$foundryResourceEndpoint" -ForegroundColor White
Write-Host "Doc Intelligence Endpoint:$documentIntelligenceEndpoint" -ForegroundColor White
Write-Host "AI Search Service:        $searchServiceName" -ForegroundColor White
Write-Host "AI Search Endpoint:       $searchServiceEndpoint" -ForegroundColor White
Write-Host "Embedding Model:          $embeddingModelDeployment" -ForegroundColor White
Write-Host ""

$preferredModelDeploymentAvailable = Add-PreferredFoundryDeployment `
    -ResourceGroupName $ResourceGroupName `
    -AccountName $aiFoundryName `
    -DeploymentName 'gpt-5.4' `
    -ModelName 'gpt-5.4' `
    -ModelVersion '2026-03-05' `
    -SkuName 'GlobalStandard' `
    -Capacity $GptCapacity

if ($preferredModelDeploymentAvailable) {
    $modelDeployment = 'gpt-5.4'
    Write-Host "Preferred GPT-5.4 Model:  available" -ForegroundColor White
} else {
    Write-Host "Preferred GPT-5.4 Model:  unavailable; using gpt-4o fallback" -ForegroundColor Yellow
}
Write-Host "Default Agent Model:      $modelDeployment" -ForegroundColor White
Write-Host ""

$allowedModelDeployments = (@('gpt-4o', $modelDeployment) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique) -join ','

az functionapp config appsettings set `
    --name $functionAppName `
    --resource-group $ResourceGroupName `
    --settings `
        "ALLOWED_MODEL_DEPLOYMENTS=$allowedModelDeployments" `
        "CATEGORIZATION_AGENT_MODEL=$modelDeployment" `
        "GROUPING_AGENT_MODEL=$modelDeployment" `
        "VALIDATION_AGENT_MODEL=$modelDeployment" `
    --output none

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to configure Function App model settings." -ForegroundColor Red
    exit 1
}

$aiEndpoint = if ([string]::IsNullOrWhiteSpace($FoundryProjectEndpoint)) { $aiProjectEndpoint.TrimEnd('/') } else { $FoundryProjectEndpoint.TrimEnd('/') }

$functionPrincipalId = az functionapp show `
    --name $functionAppName `
    --resource-group $ResourceGroupName `
    --query identity.principalId `
    -o tsv `
    --only-show-errors

$storageAccountId = az storage account show `
    --name $storageAccountName `
    --resource-group $ResourceGroupName `
    --query id `
    -o tsv `
    --only-show-errors

if ([string]::IsNullOrWhiteSpace($functionPrincipalId) -or [string]::IsNullOrWhiteSpace($storageAccountId)) {
    Write-Host "Could not verify Function App managed identity or storage account before publish." -ForegroundColor Red
    exit 1
}

Write-Host "Verifying Function App managed identity has Storage Blob Data Owner on deployment storage..." -ForegroundColor Yellow
$blobOwnerAssignment = ''
for ($attempt = 1; $attempt -le 18; $attempt++) {
    $blobOwnerAssignment = az role assignment list `
        --assignee $functionPrincipalId `
        --scope $storageAccountId `
        --role 'Storage Blob Data Owner' `
        --query '[0].id' `
        -o tsv `
        --only-show-errors

    if (-not [string]::IsNullOrWhiteSpace($blobOwnerAssignment)) {
        break
    }

    if ($attempt -lt 18) {
        Write-Host "Storage Blob Data Owner role is not visible yet; waiting for RBAC propagation ($attempt/18)..." -ForegroundColor Yellow
        Start-Sleep -Seconds 10
    }
}

if ([string]::IsNullOrWhiteSpace($blobOwnerAssignment)) {
    Write-Host "Function App managed identity does not have Storage Blob Data Owner on $storageAccountName. Re-run deployment after RBAC propagation or check role assignment policy." -ForegroundColor Red
    exit 1
}

if ($IncludeTags) {
    Write-Host "Ensuring Function storage account has deployment tag $DeploymentTagName=$DeploymentTagValue..." -ForegroundColor Yellow
    az tag update `
        --resource-id $storageAccountId `
        --operation Merge `
        --tags "$DeploymentTagName=$DeploymentTagValue" `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to merge deployment tag onto Function storage account '$storageAccountName'."
    }
}

if ($hostingMode -eq 'FlexConsumption') {
    Write-Host "Removing app settings unsupported by Flex Consumption before publish..." -ForegroundColor Yellow
    az functionapp config appsettings delete `
        --name $functionAppName `
        --resource-group $ResourceGroupName `
        --setting-names SCM_DO_BUILD_DURING_DEPLOYMENT ENABLE_ORYX_BUILD FUNCTIONS_WORKER_RUNTIME `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove app settings unsupported by Flex Consumption."
    }

    Wait-ForFlexAppSettingsRemoval -FunctionAppName $functionAppName
}

# -------------------------------------------------------------------------
# Publish the Function App code
# -------------------------------------------------------------------------
Wait-Or-Exit-ForActiveFunctionDeployment -FunctionAppName $functionAppName -ResourceGroupName $ResourceGroupName

Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Publishing Function App code..." -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""

$funcAppDir = Join-Path $PSScriptRoot "..\doed_regulatory_comments_func"
$funcAppDir = Resolve-Path $funcAppDir

# Check if Azure Functions Core Tools is installed
if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
    Write-Host "Azure Functions Core Tools not found. Installing..." -ForegroundColor Yellow
    winget install --id Microsoft.AzureFunctionsCoreTools --accept-source-agreements --accept-package-agreements
    # Refresh PATH
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PATH', 'User')
}

Push-Location $funcAppDir
try {
    $publishSucceeded = $false
    $lastPublishOutput = ''
    $maxPublishAttempts = 6
    $deploymentBusyChoiceMade = $false

    for ($publishAttempt = 1; $publishAttempt -le $maxPublishAttempts; $publishAttempt++) {
        Write-Host "Publishing to $functionAppName (attempt $publishAttempt of $maxPublishAttempts)..." -ForegroundColor Yellow
        $publishLogPath = Join-Path $env:TEMP ("func-publish-{0}.log" -f ([guid]::NewGuid().ToString('N')))
        try {
            if ($hostingMode -eq 'FlexConsumption') {
                $script:LastFlexPublishExitCode = 1
                Ensure-StoragePublicNetworkAccess -StorageAccountName $storageAccountName -ResourceGroupName $ResourceGroupName
                Publish-FlexFunctionApp -FunctionAppName $functionAppName -ResourceGroupName $ResourceGroupName -FunctionAppDirectory $funcAppDir 2>&1 | Tee-Object -FilePath $publishLogPath
                $publishExitCode = $script:LastFlexPublishExitCode
            } else {
                func azure functionapp publish $functionAppName --python 2>&1 | Tee-Object -FilePath $publishLogPath
                $publishExitCode = $LASTEXITCODE
            }
            $lastPublishOutput = if (Test-Path $publishLogPath) { Get-Content -Path $publishLogPath -Raw } else { '' }
        } finally {
            if (Test-Path $publishLogPath) {
                Remove-Item $publishLogPath -Force -ErrorAction SilentlyContinue
            }
        }

        if ($publishExitCode -eq 0) {
            $publishSucceeded = $true
            break
        }

        $deploymentBusy = $lastPublishOutput -match 'another deployment in progress' `
            -or $lastPublishOutput -match 'Deployment was cancelled' `
            -or $lastPublishOutput -match 'SCM site is currently busy'

        if ($deploymentBusy -and $publishAttempt -lt $maxPublishAttempts) {
            if (-not $deploymentBusyChoiceMade) {
                Write-Host "Function App deployment endpoint is busy because another deployment is already in progress." -ForegroundColor Yellow
                $choice = Read-Host "Wait and retry until it clears? [Y/n]"
                if ($choice -match '^(n|no)$') {
                    Write-Host "Exiting without starting another publish. Re-run deploy.ps1 after the current Function deployment finishes." -ForegroundColor Yellow
                    exit 2
                }
                $deploymentBusyChoiceMade = $true
            }

            Write-Host "Waiting 60 seconds before checking the deployment endpoint again..." -ForegroundColor Yellow
            Start-Sleep -Seconds 60
            continue
        }

        Write-Host "Function publish exited with code $publishExitCode." -ForegroundColor Red
        if (-not [string]::IsNullOrWhiteSpace($lastPublishOutput)) {
            Write-Host "Last publish log lines:" -ForegroundColor Red
            $lastPublishOutput -split "`r?`n" | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
        }
        break
    }

    if ($publishSucceeded) {
        Write-Host ""
        Write-Host "Function App published successfully!" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Function App publish failed. You can retry manually:" -ForegroundColor Red
        if ($hostingMode -eq 'FlexConsumption') {
            Write-Host "  cd $funcAppDir" -ForegroundColor Gray
            Write-Host "  Remove unsupported Flex app settings, wait for SCM propagation, then run az functionapp deployment source config-zip --build-remote true" -ForegroundColor Gray
        } else {
            Write-Host "  cd $funcAppDir" -ForegroundColor Gray
            Write-Host "  func azure functionapp publish $functionAppName --python" -ForegroundColor Gray
        }
        exit 1
    }
} finally {
    Pop-Location
}

Invoke-AgentCreationWorkflow `
    -AiEndpoint $aiEndpoint `
    -FunctionAppName $functionAppName `
    -ResourceGroupName $ResourceGroupName `
    -ModelDeployment $modelDeployment `
    -DeploymentOutputPath $AgentDeploymentOutputPath

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "All done! Your app is fully deployed." -ForegroundColor Cyan
Write-Host "The function runs daily at 3AM EST (8AM UTC)." -ForegroundColor Cyan
Write-Host "Monitor it at: https://portal.azure.com" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
exit 0
