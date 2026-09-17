import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


REPO_ROOT = Path(__file__).resolve().parents[2]
FUNCTION_SCRIPT = REPO_ROOT / "azure_func_v2" / "infra" / "deploy.ps1"


class DeploymentContractTests(unittest.TestCase):
    def invoke_powershell(self, script, **environment):
        executable = shutil.which("pwsh")
        if not executable:
            self.fail("PowerShell 7 is required to verify the deployment script contracts.")
        result = subprocess.run(
            [executable, "-NoProfile", "-NonInteractive", "-Command", script],
            env={**os.environ, **environment},
            capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result.stdout.strip()

    def test_both_deployment_scripts_parse_and_require_all_four_agent_roles(self):
        output = self.invoke_powershell(
            """
            $ErrorActionPreference = 'Stop'
            foreach ($file in @($env:ROOT_SCRIPT, $env:FUNCTION_SCRIPT)) {
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors)
                if ($errors.Count) { throw ($errors.Message -join '; ') }
                if ($ast.ParamBlock.Parameters.Name.VariablePath.UserPath -notcontains 'AgentPythonExecutable') {
                    throw 'Deployment must accept an explicitly validated Python interpreter.'
                }
            }
            $agentWorkflow = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Invoke-AgentCreationWorkflow'
            }, $true)
            $required = $agentWorkflow.Body.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Clauses[0].Item1.Extent.Text.Contains("ContainsKey('CATEGORIZATION')")
            }, $true)
            if ($required.Count -ne 1) { throw 'Required-agent gate is missing or ambiguous.' }
            $required[0].Clauses[0].Item1.Extent.Text
            """,
            ROOT_SCRIPT=str(REPO_ROOT / "deploy.ps1"), FUNCTION_SCRIPT=str(FUNCTION_SCRIPT),
        )
        for role in ("CATEGORIZATION", "GROUPING", "VALIDATION", "FOLLOWUP"):
            self.assertIn(f"ContainsKey('{role}')", output)

    def test_function_package_contains_only_required_runtime_files(self):
        required = {
            "host.json", "requirements.txt", "function_app.py", "analysis_requests.py",
            "cosmos_runs.py", "structured_responses.py", "source_evidence.py",
        }
        with tempfile.TemporaryDirectory(prefix="doed-package-test-") as directory:
            source = Path(directory) / "source"
            source.mkdir()
            for name in required | {"local.settings.json", ".env", "test_live.py", "output_comments.json", "README.md"}:
                (source / name).write_text("synthetic test fixture", encoding="utf-8")
            environment_folder = source / ".venv_x64"
            environment_folder.mkdir()
            (environment_folder / "not-for-deployment.txt").write_text("fixture", encoding="utf-8")
            result = self.invoke_powershell(
                """
                $ErrorActionPreference = 'Stop'
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $env:FUNCTION_SCRIPT, [ref]$null, [ref]$errors)
                if ($errors.Count) { throw ($errors.Message -join '; ') }
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'New-FunctionZipPackage'
                }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
                New-FunctionZipPackage -FunctionAppDirectory $env:TEST_SOURCE | ConvertTo-Json -Compress
                """,
                FUNCTION_SCRIPT=str(FUNCTION_SCRIPT), TEST_SOURCE=str(source),
                TEMP=directory, TMP=directory, TMPDIR=directory,
            )
            package = json.loads(result)
            with zipfile.ZipFile(package["ZipPath"]) as archive:
                files = {info.filename.removeprefix("./") for info in archive.infolist() if not info.is_dir()}
            self.assertEqual(required, files)

    def test_personal_defaults_do_not_downgrade_or_enable_billable_automation(self):
        template = (REPO_ROOT / "azure_func_v2" / "infra" / "main.bicep").read_text(encoding="utf-8-sig")
        parameter_file = (REPO_ROOT / "dotnet_frontend" / "infra" / "main.bicepparam").read_text(encoding="utf-8-sig")
        self.assertIn("param agentModelName string = 'gpt-5.5'", template)
        self.assertIn("param agentModelVersion string = '2026-04-24'", template)
        self.assertIn("param enableMethodologySearch bool = false", template)
        self.assertIn("param enableScheduledAnalysis bool = false", template)
        self.assertIn("readEnvironmentVariable('FOUNDRY_MODEL_DEPLOYMENT', 'gpt-5.5')", parameter_file)
        self.assertIn("ALLOWED_CLIENT_CIDR", parameter_file)
        local_settings = json.loads(
            (REPO_ROOT / "azure_func_v2" / "doed_regulatory_comments_func" / "local.settings.json.example")
            .read_text(encoding="utf-8-sig")
        )["Values"]
        self.assertEqual("gpt-5.5", local_settings["ALLOWED_MODEL_DEPLOYMENTS"])
        self.assertEqual("true", local_settings["AzureWebJobs.regulatory_comments_daily.Disabled"])
        project = (REPO_ROOT / "dotnet_frontend" / "DoedRegulatoryComments.Web.csproj").read_text(encoding="utf-8-sig")
        self.assertIn('Content Remove="App_Data\\**;infra\\**;.azure\\**;appsettings.Development.json"', project)

    def test_optional_ocr_has_analyze_permission_not_just_data_read(self):
        template = (REPO_ROOT / "dotnet_frontend" / "infra" / "main.bicep").read_text(encoding="utf-8-sig")
        self.assertIn("var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'", template)
        self.assertIn("roleDefinitions', cognitiveServicesUserRoleId)", template)
        self.assertNotIn("b59867f0-fa02-499b-be73-45a86b5b3e1c", template)

    def test_upstream_search_and_embedding_options_preserve_explicit_model_and_safety_flags(self):
        result = self.invoke_powershell(
            """
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:FUNCTION_SCRIPT, [ref]$null, [ref]$null)
            foreach ($name in @('Test-InfrastructureDeployment', 'Invoke-InfrastructureDeployment')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
            }
            function az { $script:CapturedArguments = @($args); $global:LASTEXITCODE = 0; '{}' }
            $AgentModelName = 'gpt-5.5'
            $AgentModelVersion = '2026-04-24'
            $AgentModelSku = 'GlobalStandard'
            [switch]$EnableMethodologySearch = $false
            [switch]$EnableScheduledAnalysis = $false
            $parameters = @{
                ResourceGroupName = 'synthetic'
                TemplateFile = 'synthetic.bicep'
                Location = 'eastus'
                SearchLocation = 'centralus'
                GptCapacity = 10
                EmbeddingCapacity = 5
                EmbeddingSkuName = 'Standard'
                RegulationsGovApiKey = 'synthetic-not-a-secret'
                DocumentId = 'ED-SYNTHETIC'
                BatchSize = 5
                DeployerPrincipalId = ''
                DeployerPrincipalType = ''
                BaseName = 'synthetic'
                DeploymentSuffix = 'synthetic'
                HostingMode = 'FlexConsumption'
            }
            $validation = Test-InfrastructureDeployment @parameters
            $validatedArguments = $script:CapturedArguments
            $deployment = Invoke-InfrastructureDeployment @parameters -DeploymentName 'synthetic'
            if ($validation.ExitCode -ne 0 -or $deployment.ExitCode -ne 0) { throw 'Mocked commands failed.' }
            @{ validation = $validatedArguments; deployment = $script:CapturedArguments } | ConvertTo-Json -Compress
            """,
            FUNCTION_SCRIPT=str(FUNCTION_SCRIPT),
        )
        for arguments in json.loads(result).values():
            for expected in ("searchLocation=centralus", "embeddingSkuName=Standard",
                             "agentModelName=gpt-5.5", "agentModelVersion=2026-04-24",
                             "enableMethodologySearch=false", "enableScheduledAnalysis=false"):
                self.assertIn(expected, arguments)

    def test_retry_classifier_identifies_not_ready_resources_without_retrying_other_failures(self):
        result = self.invoke_powershell(
            """
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:FUNCTION_SCRIPT, [ref]$null, [ref]$null)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Test-FunctionDeploymentNotReady'
            }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            @(
                (Test-FunctionDeploymentNotReady -Output 'ResourceNotFound')
                (Test-FunctionDeploymentNotReady -Output 'Microsoft.Web/sites/example was not found')
                (Test-FunctionDeploymentNotReady -Output 'AuthorizationFailed')
                (Test-FunctionDeploymentNotReady -Output 'Package build failed')
                (Test-FunctionDeploymentNotReady -Output '')
            ) | ConvertTo-Json -Compress
            """,
            FUNCTION_SCRIPT=str(FUNCTION_SCRIPT),
        )
        self.assertEqual([True, True, False, False, False], json.loads(result))

    def test_search_resource_recovery_selects_the_exact_region_specific_service(self):
        result = self.invoke_powershell(
            """
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:FUNCTION_SCRIPT, [ref]$null, [ref]$null)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Get-DeploymentResourceName'
            }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            function az {
                $queryIndex = [array]::IndexOf($args, '--query')
                $script:Query = $args[$queryIndex + 1]
                'srch-synthetic-centralus'
            }
            $null = Get-DeploymentResourceName -ResourceGroupName 'synthetic' `
                -ResourceType 'Microsoft.Search/searchServices' -NamePrefix 'srch-synthetic-centralus' -ExactMatch
            $script:Query
            """,
            FUNCTION_SCRIPT=str(FUNCTION_SCRIPT),
        )
        self.assertEqual("[?name=='srch-synthetic-centralus'].name | [0]", result)

    def test_merged_premium_default_is_consistent_and_still_supports_basic(self):
        template = (REPO_ROOT / "dotnet_frontend" / "infra" / "main.bicep").read_text(encoding="utf-8-sig")
        parameters = (REPO_ROOT / "dotnet_frontend" / "infra" / "main.bicepparam").read_text(encoding="utf-8-sig")
        script = (REPO_ROOT / "deploy.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("param appServicePlanSku string = 'P0v3'", template)
        self.assertIn("param appServicePlanSku = 'P0v3'", parameters)
        self.assertIn('[string]$FrontendSku = "P0v3"', script)
        self.assertIn("@allowed([ 'B1'", template)

    def test_storage_preflight_never_reopens_policy_restricted_networks(self):
        output = self.invoke_powershell(
            """
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $env:FUNCTION_SCRIPT, [ref]$null, [ref]$null)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Ensure-StoragePublicNetworkAccess'
            }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            function az {
                if (($args -join ' ') -notmatch '^storage account show ') { throw 'A non-read-only Azure command was attempted.' }
                $global:LASTEXITCODE = 0
                '{"publicNetworkAccess":"Disabled","defaultAction":"Deny"}'
            }
            try {
                Ensure-StoragePublicNetworkAccess -StorageAccountName 'synthetic' -ResourceGroupName 'synthetic'
                throw 'Restricted storage was incorrectly accepted.'
            } catch {
                if ($_.Exception.Message -notmatch 'will not reopen storage or override policy') { throw }
                'Restricted storage correctly blocks publishing.'
            }
            """,
            FUNCTION_SCRIPT=str(FUNCTION_SCRIPT),
        )
        self.assertIn("correctly blocks publishing", output)


if __name__ == "__main__":
    unittest.main()
