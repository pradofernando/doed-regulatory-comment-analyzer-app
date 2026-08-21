$results = @()

Write-Host "--- 1) bicep build ---"
try {
    $bicepError = $null
    az bicep build --file azure_func_v2/infra/main.bicep 2>&1 | Out-String -OutVariable bicepError
    if ($LASTEXITCODE -eq 0) {
        $results += [PSCustomObject]@{ Check = "Bicep Build"; Status = "Pass"; Details = "main.bicep successfully compiled with az bicep build." }
    } else {
        $results += [PSCustomObject]@{ Check = "Bicep Build"; Status = "Fail"; Details = $bicepError }
    }
} catch {
    $results += [PSCustomObject]@{ Check = "Bicep Build"; Status = "FAIL_EXCEPTION"; Details = $_.Exception.Message }
}

Write-Host "--- 2) parse deploy.ps1 ---"
try {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile("azure_func_v2\infra\deploy.ps1", [ref]$null, [ref]$errors)
    if ($errors) {
        $errMsgs = $errors | ForEach-Object { "- " + $_.Message + " at line " + $_.Extent.StartLineNumber + ", col " + $_.Extent.StartColumnNumber } | Out-String
        $results += [PSCustomObject]@{ Check = "Parse deploy.ps1"; Status = "Fail"; Details = "Syntax errors found:\
$errMsgs" }
    } else {
        $results += [PSCustomObject]@{ Check = "Parse deploy.ps1"; Status = "Pass"; Details = "No syntax errors found." }
    }
} catch {
    $results += [PSCustomObject]@{ Check = "Parse deploy.ps1"; Status = "FAIL_EXCEPTION"; Details = $_.Exception.Message }
}

Write-Host "--- 3) parse local.settings.json.example ---"
try {
    $jsonPath = "azure_func_v2\doed_regulatory_comments_func\local.settings.json.example"
    $jsonContent = Get-Content -Raw -Path $jsonPath
    $parsedJson = ConvertFrom-Json $jsonContent -ErrorAction Stop
    $results += [PSCustomObject]@{ Check = "Parse JSON"; Status = "Pass"; Details = "Successfully parsed local.settings.json.example as valid JSON." }
} catch {
    $results += [PSCustomObject]@{ Check = "Parse JSON"; Status = "Fail"; Details = $_.Exception.Message }
}

Write-Host "--- 4) run unit tests ---"
try {
    Push-Location "azure_func_v2\doed_regulatory_comments_func"
    $testOut = & "c:\src\doed-regulatory-comment-analyzer-app\.venv\Scripts\python.exe" -m unittest discover 2>&1 | Out-String
    Pop-Location
    if ($LASTEXITCODE -eq 0) {
        $results += [PSCustomObject]@{ Check = "Python Unittests"; Status = "Pass"; Details = $testOut }
    } else {
        $results += [PSCustomObject]@{ Check = "Python Unittests"; Status = "Fail"; Details = $testOut }
    }
} catch {
    if ($PWD.Path -like "*doed_regulatory_comments_func*") { Pop-Location }
    $results += [PSCustomObject]@{ Check = "Python Unittests"; Status = "FAIL_EXCEPTION"; Details = $_.Exception.Message }
}

Write-Host "--- 5) run git diff --check ---"
try {
    $gitOut = git diff --check 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
         $results += [PSCustomObject]@{ Check = "Git Diff Check"; Status = "Pass"; Details = "No whitespace/conflict errors found by git diff --check." }
    } else {
         $results += [PSCustomObject]@{ Check = "Git Diff Check"; Status = "Fail"; Details = $gitOut }
    }
} catch {
    $results += [PSCustomObject]@{ Check = "Git Diff Check"; Status = "FAIL_EXCEPTION"; Details = $_.Exception.Message }
}

$results | Format-List