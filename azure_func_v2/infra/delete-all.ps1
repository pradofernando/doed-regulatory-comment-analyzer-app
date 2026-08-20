# ============================================================================
# Delete DoED Regulatory Comments Azure Function Infrastructure
# ============================================================================
#
# This script deletes the entire deployment resource group so the companion
# deploy.ps1 script can recreate a clean environment from Bicep. It also
# purges soft-deleted resources that would otherwise block clean redeploys.
#
# Usage:
#   .\delete-all.ps1
#   .\delete-all.ps1 -ResourceGroupName "rg-doed-comments" -Force
#   .\delete-all.ps1 -ResourceGroupName "rg-doed-comments" -SkipPurge
#   .\delete-all.ps1 -ResourceGroupName "rg-doed-comments" -PurgeOnly
#
# ============================================================================

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-doed-comments",

    [Parameter(Mandatory=$false)]
    [switch]$Force,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPurge,

    [Parameter(Mandatory=$false)]
    [switch]$PurgeOnly
)

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

function Invoke-KeyVaultPurge {
    param(
        [Parameter(Mandatory=$true)]
        [psobject]$Vault
    )

    Write-Host "Purging deleted Key Vault $($Vault.name) in $($Vault.location)..." -ForegroundColor Yellow
    $purgeOutput = az keyvault purge --name $Vault.name --location $Vault.location --output none 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        return $true
    }

    if ($purgeOutput -match 'DeletedVaultPurge' -or $purgeOutput -match 'MethodNotAllowed' -or $Vault.purgeProtectionEnabled) {
        Write-Host "Key Vault $($Vault.name) is purge-protected and cannot be purged until the retention window expires." -ForegroundColor Yellow
        Write-Host "The next deploy will recover this vault automatically instead of recreating it." -ForegroundColor Yellow
        return $false
    }

    Write-Host "Failed to purge Key Vault $($Vault.name)." -ForegroundColor Red
    Write-Host $purgeOutput
    exit 1
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

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in to Azure. Please run 'az login' first." -ForegroundColor Red
    exit 1
}

Write-Host "" 
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "Delete DoED Regulatory Comments Infrastructure" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "" 
Write-Host "Subscription: $($account.name)" -ForegroundColor White
Write-Host "Resource Group: $ResourceGroupName" -ForegroundColor White
if ($PurgeOnly) {
    Write-Host "Mode: Purge soft-deleted resources only" -ForegroundColor White
} elseif ($SkipPurge) {
    Write-Host "Purge Soft-Deleted Resources: No" -ForegroundColor White
} else {
    Write-Host "Purge Soft-Deleted Resources: Yes" -ForegroundColor White
}
Write-Host "" 

if ($PurgeOnly -and $SkipPurge) {
    Write-Host "-PurgeOnly and -SkipPurge cannot be used together." -ForegroundColor Red
    exit 1
}

if ($PurgeOnly) {
    if (-not $Force) {
        Write-Host "This will purge previously soft-deleted Key Vault and Foundry/Cognitive Services resources for this resource group." -ForegroundColor Red
        $confirmation = Read-Host "Type PURGE to continue"
        if ($confirmation -cne "PURGE") {
            Write-Host "Purge cancelled." -ForegroundColor Yellow
            exit 0
        }
    }

    Write-Host "Purging soft-deleted resources for $ResourceGroupName..." -ForegroundColor Yellow

    $deletedVaults = Get-DeletedKeyVaultsInResourceGroup -ResourceGroupName $ResourceGroupName
    $recoveryRequiredVaults = @()
    foreach ($vault in $deletedVaults) {
        if (-not (Invoke-KeyVaultPurge -Vault $vault)) {
            $recoveryRequiredVaults += $vault.name
        }
    }

    $deletedAccounts = Get-DeletedCognitiveAccountsInResourceGroup -ResourceGroupName $ResourceGroupName
    foreach ($accountToPurge in $deletedAccounts) {
        Write-Host "Purging deleted Cognitive Services account $($accountToPurge.name) in $($accountToPurge.location)..." -ForegroundColor Yellow
        az cognitiveservices account purge --name $accountToPurge.name --resource-group $ResourceGroupName --location $accountToPurge.location --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to purge Cognitive Services account $($accountToPurge.name)." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host "" 
    if ($recoveryRequiredVaults.Count -gt 0) {
        Write-Host "All purgeable soft-deleted resources were purged." -ForegroundColor Green
        Write-Host "These Key Vaults remain soft-deleted due to purge protection and will need recovery on the next deploy:" -ForegroundColor Yellow
        Write-Host ($recoveryRequiredVaults -join ', ') -ForegroundColor Yellow
    } else {
        Write-Host "Soft-deleted resources purged." -ForegroundColor Green
    }
    Write-Host "" 
    exit 0
}

$rgExists = az group exists --name $ResourceGroupName
if ($rgExists -eq "false") {
    Write-Host "Resource group does not exist. Nothing to delete." -ForegroundColor Green
    exit 0
}

if (-not $Force) {
    Write-Host "This will delete the entire resource group and all deployed resources." -ForegroundColor Red
    if (-not $SkipPurge) {
        Write-Host "It will also purge soft-deleted Key Vault and Foundry/Cognitive Services resources in this resource group." -ForegroundColor Red
    }
    $confirmation = Read-Host "Type DELETE to continue"
    if ($confirmation -cne "DELETE") {
        Write-Host "Deletion cancelled." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Deleting resource group $ResourceGroupName..." -ForegroundColor Yellow
az group delete --name $ResourceGroupName --yes --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "Resource group deletion command failed." -ForegroundColor Red
    exit 1
}

Write-Host "Waiting for resource group deletion to complete..." -ForegroundColor Yellow
az group wait --deleted --name $ResourceGroupName
if ($LASTEXITCODE -ne 0) {
    Write-Host "Timed out or failed while waiting for resource group deletion." -ForegroundColor Red
    exit 1
}

if (-not $SkipPurge) {
    Write-Host "Purging soft-deleted resources for $ResourceGroupName..." -ForegroundColor Yellow

    $deletedVaults = Get-DeletedKeyVaultsInResourceGroup -ResourceGroupName $ResourceGroupName
    $recoveryRequiredVaults = @()
    foreach ($vault in $deletedVaults) {
        if (-not (Invoke-KeyVaultPurge -Vault $vault)) {
            $recoveryRequiredVaults += $vault.name
        }
    }

    $deletedAccounts = Get-DeletedCognitiveAccountsInResourceGroup -ResourceGroupName $ResourceGroupName
    foreach ($accountToPurge in $deletedAccounts) {
        Write-Host "Purging deleted Cognitive Services account $($accountToPurge.name) in $($accountToPurge.location)..." -ForegroundColor Yellow
        az cognitiveservices account purge --name $accountToPurge.name --resource-group $ResourceGroupName --location $accountToPurge.location --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to purge Cognitive Services account $($accountToPurge.name)." -ForegroundColor Red
            exit 1
        }
    }
}

Write-Host "" 
Write-Host "Resource group deleted." -ForegroundColor Green
if (-not $SkipPurge) {
    if ($recoveryRequiredVaults.Count -gt 0) {
        Write-Host "All purgeable soft-deleted resources were purged." -ForegroundColor Green
        Write-Host "These Key Vaults remain soft-deleted due to purge protection and will be recovered on the next deploy:" -ForegroundColor Yellow
        Write-Host ($recoveryRequiredVaults -join ', ') -ForegroundColor Yellow
    } else {
        Write-Host "Soft-deleted resources purged." -ForegroundColor Green
    }
}
Write-Host "Run .\deploy.ps1 to recreate the infrastructure from Bicep." -ForegroundColor Green
Write-Host "" 