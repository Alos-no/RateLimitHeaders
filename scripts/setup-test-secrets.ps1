# Cross-platform PowerShell script to configure user secrets for Polly.Contrib.RateLimitHeaders test project.
# Requires PowerShell Core (pwsh) to be installed.
# To run from the project root: pwsh -File ./scripts/setup-test-secrets.ps1

# --- Configuration ---
$ErrorActionPreference = "Stop"

# Define paths relative to the script's own location ($PSScriptRoot) to make it robust.
$scriptRoot = $PSScriptRoot
$CoreTestProject = Join-Path -Path $scriptRoot -ChildPath "..\tests\RateLimitHeaders.Tests\RateLimitHeaders.Tests.csproj"
$PollyTestProject = Join-Path -Path $scriptRoot -ChildPath "..\tests\RateLimitHeaders.Polly.Tests\RateLimitHeaders.Polly.Tests.csproj"

# Both projects share the same UserSecretsId, so we only need to configure one.
# But we include both for clarity and in case they diverge in the future.
$projects = @(
    $CoreTestProject,
    $PollyTestProject
)

# Define the secrets required, with user-friendly prompts.
# Using [ordered] ensures that the prompts appear in the exact order they are defined here.
$secrets = [ordered]@{
    "Cloudflare:ApiToken" = "Enter your Cloudflare API Token (used to test rate limit header parsing):";
}

# --- Script Body ---
Write-Host "--- Polly.Contrib.RateLimitHeaders Test Secret Setup ---" -ForegroundColor Yellow
Write-Host "This script will configure the necessary secrets for running integration tests"
Write-Host "against the real Cloudflare API to validate rate limit header parsing."
Write-Host ""
Write-Host "You need a Cloudflare API token. You can create one at:"
Write-Host "  https://dash.cloudflare.com/profile/api-tokens" -ForegroundColor Cyan
Write-Host ""
Write-Host "Any valid API token will work - we only need to receive rate limit headers."
Write-Host "The secrets will be stored securely using the .NET user-secrets tool."
Write-Host ""

# 1. Collect all secrets from the user first to avoid repetitive prompting.
$secretValues = @{}
foreach ($key in $secrets.Keys) {
    $prompt = $secrets[$key]
    # Determine if the secret is sensitive and should be read securely.
    $isSensitive = $key -like "*ApiToken*" -or $key -like "*Secret*"

    Write-Host $prompt -ForegroundColor Cyan

    if ($isSensitive) {
        $value = Read-Host -AsSecureString
    } else {
        $value = Read-Host -Prompt $prompt
    }

    if ($value -is [System.Security.SecureString] -and $value.Length -eq 0) {
        Write-Error "Input cannot be empty. Aborting."
        return
    }
    if ($value -isnot [System.Security.SecureString] -and [string]::IsNullOrWhiteSpace($value)) {
        Write-Error "Input cannot be empty. Aborting."
        return
    }
    $secretValues[$key] = $value
}

Write-Host ""
Write-Host "Secrets collected. Now applying to test project..." -ForegroundColor Green
Write-Host ""

# 2. Initialize and set secrets for each project.
foreach ($projectPath in $projects) {
    # Verify the project path exists before proceeding.
    if (-not (Test-Path -Path $projectPath -PathType Leaf)) {
        Write-Warning "Could not find project file at path: $projectPath. Skipping."
        continue
    }

    Write-Host "Configuring project: $projectPath" -ForegroundColor Magenta

    try {
        # Initialize user secrets for the project. This is idempotent.
        dotnet user-secrets init --project $projectPath | Out-Null
        Write-Host "  - Initialized user secrets."

        # Set each secret for the current project.
        foreach ($key in $secretValues.Keys) {
            $value = $secretValues[$key]

            # Special handling for SecureString to pass it to the command-line tool.
            if ($value -is [System.Security.SecureString]) {
                # Temporarily convert SecureString to plain text for the CLI command.
                $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($value)
                $plainTextValue = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
                [System.Runtime.InteropServices.Marshal]::FreeBSTR($bstr)

                dotnet user-secrets set "$key" "$plainTextValue" --project $projectPath | Out-Null
                # Clear the plaintext variable immediately for security.
                Clear-Variable plainTextValue
            } else {
                dotnet user-secrets set "$key" "$value" --project $projectPath | Out-Null
            }
            Write-Host "  - Set secret for '$key'."
        }
        Write-Host "Project configured successfully." -ForegroundColor Green
        Write-Host ""
    }
    catch {
        Write-Error "An error occurred while configuring project '$projectPath'."
        Write-Error $_.Exception.Message
        # Continue to the next project even if one fails.
    }
}

Write-Host "--- Setup Complete ---" -ForegroundColor Yellow
Write-Host ""
Write-Host "You can now run the Cloudflare API integration tests with:" -ForegroundColor Cyan
Write-Host "  dotnet test --filter 'FullyQualifiedName~CloudflareApiTests'"
Write-Host ""
Write-Host "Note: Tests that require the API token will be skipped if it's not configured."
