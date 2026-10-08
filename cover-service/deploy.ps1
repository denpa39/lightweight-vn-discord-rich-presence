$ErrorActionPreference = 'Stop'
$env:WRANGLER_SEND_METRICS = 'false'
$taskServiceRoot = $PSScriptRoot
Push-Location -LiteralPath $taskServiceRoot
try {
    # Only run after checking Workers plans -> Free ($0). Never enables billing or upgrades.
    $taskConfig = Get-Content -LiteralPath 'wrangler.jsonc' -Raw | ConvertFrom-Json
    $taskExistingText = npx --yes wrangler@4.119.0 d1 list --json
    if ($LASTEXITCODE -ne 0) { throw 'Could not check existing databases' }
    $taskExisting = @($taskExistingText | ConvertFrom-Json)
    $taskNeeded = @($taskConfig.d1_databases | Where-Object { $_.database_name -notin $taskExisting.name })
    if ($taskExisting.Count + $taskNeeded.Count -gt 10) { throw 'Not enough Free plan database slots' }
    New-Item -ItemType Directory -Path '.wrangler' -Force | Out-Null
    foreach ($taskBinding in $taskConfig.d1_databases) {
        $taskDatabase = $taskExisting | Where-Object { $_.name -eq $taskBinding.database_name }
        if ($null -eq $taskDatabase) {
            npx --yes wrangler@4.119.0 d1 create $taskBinding.database_name
            if ($LASTEXITCODE -ne 0) { throw 'Database creation failed' }
            $taskRefreshed = npx --yes wrangler@4.119.0 d1 list --json
            if ($LASTEXITCODE -ne 0) { throw 'Could not read the new database ID' }
            $taskDatabase = @($taskRefreshed | ConvertFrom-Json) | Where-Object { $_.name -eq $taskBinding.database_name }
        }
        $taskBinding.database_id = if ($taskDatabase.uuid) { $taskDatabase.uuid } else { $taskDatabase.database_id }
        if ([string]::IsNullOrWhiteSpace($taskBinding.database_id)) { throw 'Database ID missing' }
    }
    # Account-specific IDs stay in ignored local deployment config.
    $taskConfig.main = '../entry.mjs'
    $taskConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath '.wrangler/deploy.json'
    ((Get-Content -LiteralPath 'migrate-png.sql' -Raw) + "`n" + (Get-Content -LiteralPath 'migrate-usage.sql' -Raw) + "`n" + (Get-Content -LiteralPath 'images.sql' -Raw)) |
        Set-Content -LiteralPath '.wrangler/png-upgrade.sql'
    ((Get-Content -LiteralPath 'migrate-usage.sql' -Raw) + "`n" + (Get-Content -LiteralPath 'images.sql' -Raw)) |
        Set-Content -LiteralPath '.wrangler/usage-upgrade.sql'
    foreach ($taskBinding in $taskConfig.d1_databases) {
        $taskSchema = if ($taskBinding.binding -eq 'BUDGET') { 'schema.sql' } else { 'images.sql' }
        if ($taskBinding.binding -like 'IMAGES*') {
            $taskSchemaJson = npx --yes wrangler@4.119.0 d1 execute $taskBinding.database_name --remote --command "SELECT sql FROM sqlite_master WHERE name='covers'" --json --config .wrangler/deploy.json
            if ($LASTEXITCODE -ne 0) { throw 'Could not check image schema' }
            $taskSchemaRows = @($taskSchemaJson | ConvertFrom-Json)[0].results
            $taskOldSchema = if ($taskSchemaRows.Count -gt 0) { $taskSchemaRows[0].sql } else { '' }
            if ($taskOldSchema -match '262144') {
                # One import keeps table replacement and trigger recreation together.
                $taskSchema = '.wrangler/png-upgrade.sql'
            } elseif ($taskOldSchema -and $taskOldSchema -notmatch 'last_used') {
                $taskSchema = '.wrangler/usage-upgrade.sql'
            }
        }
        npx --yes wrangler@4.119.0 d1 execute $taskBinding.database_name --remote --file $taskSchema --config .wrangler/deploy.json
        if ($LASTEXITCODE -ne 0) { throw 'Database setup failed' }
    }
    npx --yes wrangler@4.119.0 deploy --config .wrangler/deploy.json
    if ($LASTEXITCODE -ne 0) { throw 'Worker deployment failed' }
} finally { Pop-Location }
