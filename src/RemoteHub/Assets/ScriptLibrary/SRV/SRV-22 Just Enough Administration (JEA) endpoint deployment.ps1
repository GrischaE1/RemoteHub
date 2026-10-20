<#
.SYNOPSIS
    Builds, validates and registers a JEA endpoint that maps directory groups to a constrained role.
.DESCRIPTION
    Writes a role capability file with the allowed cmdlets into a module's RoleCapabilities folder and a
    session configuration file mapping the given groups to that role with a virtual account (or gMSA) and a
    restricted transcript folder, validates it, registers the endpoint and lists the effective commands for
    a test user. Registering restarts WinRM and can drop active sessions. A virtual account is a local admin
    on member servers, so keep roles minimal: cmdlets that run arbitrary code or write arbitrary files are
    refused. Transcripts hold command lines; restrict access and forward them to your logging system.
.NOTES
    RemoteHub: id=SRV-22; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$EndpointName = 'RemoteHub.JEA.Operator',
    [string]$RoleName = 'Operator',
    # Directory groups (DOMAIN\Group) allowed to connect. Empty = report the endpoint state only.
    [string[]]$AllowedGroup = @(),
    [string[]]$VisibleCmdlet = @('Get-Service', 'Get-Process', 'Get-HotFix', 'Get-EventLog'),
    # gMSA (DOMAIN\name$) to run as instead of a virtual account.
    [string]$GroupManagedServiceAccount = '',
    [string]$TranscriptDirectory = "$env:ProgramData\RemoteHub\JEA\Transcripts",
    # Account whose effective command list is shown after registration.
    [string]$VerifyUser = ''
)

$ErrorActionPreference = 'Stop'

$blocked = @('Invoke-Expression', 'Invoke-Command', 'Add-Type', 'New-Object', 'Start-Process', 'Enter-PSSession', 'New-PSSession',
    'Set-Content', 'Add-Content', 'Out-File', 'Copy-Item', 'Move-Item', 'New-Item', 'Set-Item', 'Remove-Item', 'Import-Module',
    'Invoke-Item', 'Start-Job', 'Register-ScheduledJob', 'Set-ExecutionPolicy', 'New-Service', 'Set-Service', 'Invoke-WebRequest', 'Invoke-RestMethod', '*')

$exitCode = 0
try {
    $existing = Get-PSSessionConfiguration -Name $EndpointName -ErrorAction SilentlyContinue
    if ($AllowedGroup.Count -eq 0) {
        if ($existing) { $existing | Select-Object Name, RunAsVirtualAccount, RoleDefinitions, TranscriptDirectory, Permission | Format-List | Out-String -Width 250 }
        else { "Endpoint $EndpointName is not registered." }
        'Nothing to deploy: give -AllowedGroup to build the endpoint.'
        exit 0
    }
    $bad = @($VisibleCmdlet | Where-Object { $blocked -contains $_ -or $_ -match '\*' })
    if ($bad.Count -gt 0) { throw "Refused: these commands run arbitrary code or write arbitrary files: $($bad -join ', ')" }

    $moduleRoot = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules\RemoteHubJEA'
    $psrc = Join-Path $moduleRoot "RoleCapabilities\$RoleName.psrc"
    $pssc = Join-Path $env:ProgramData "RemoteHub\JEA\$EndpointName.pssc"
    $stage = Join-Path $env:TEMP ("SRV-22-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force -WhatIf:$false | Out-Null

    try {
        $newPsrc = Join-Path $stage "$RoleName.psrc"
        New-PSRoleCapabilityFile -Path $newPsrc -Description "RemoteHub JEA role $RoleName" -VisibleCmdlets $VisibleCmdlet
        $roles = @{}
        foreach ($g in $AllowedGroup) { $roles[$g] = @{ RoleCapabilities = $RoleName } }
        $newPssc = Join-Path $stage "$EndpointName.pssc"
        $cfg = @{ Path = $newPssc; SessionType = 'RestrictedRemoteServer'; TranscriptDirectory = $TranscriptDirectory; RoleDefinitions = $roles }
        if ($GroupManagedServiceAccount) { $cfg.GroupManagedServiceAccount = $GroupManagedServiceAccount } else { $cfg.RunAsVirtualAccount = $true }
        New-PSSessionConfigurationFile @cfg
        if (-not (Test-PSSessionConfigurationFile -Path $newPssc)) { throw 'The generated session configuration file did not validate.' }

        # GUID and date lines differ on every generation, so they are ignored when deciding whether anything changed.
        $norm = { param($p) if (Test-Path -LiteralPath $p) { (Get-Content -LiteralPath $p | Where-Object { $_ -notmatch '^\s*(GUID|# ID|# Created on|Copyright|Author)' }) -join "`n" } else { '' } }
        $psrcChanged = (& $norm $psrc) -ne (& $norm $newPsrc)
        $psscChanged = (& $norm $pssc) -ne (& $norm $newPssc)

        if (-not (Test-Path -LiteralPath $TranscriptDirectory)) {
            if ($PSCmdlet.ShouldProcess($TranscriptDirectory, 'Create transcript folder for SYSTEM and Administrators only')) {
                New-Item -ItemType Directory -Path $TranscriptDirectory -Force | Out-Null
                $acl = New-Object System.Security.AccessControl.DirectorySecurity
                $acl.SetAccessRuleProtection($true, $false)
                foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
                    $id = New-Object System.Security.Principal.SecurityIdentifier $sid
                    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule $id, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
                }
                Set-Acl -LiteralPath $TranscriptDirectory -AclObject $acl
            }
        }

        if ($psrcChanged) {
            if ($PSCmdlet.ShouldProcess($psrc, 'Write role capability file')) {
                New-Item -ItemType Directory -Path (Split-Path $psrc) -Force | Out-Null
                if (-not (Test-Path -LiteralPath (Join-Path $moduleRoot 'RemoteHubJEA.psd1'))) {
                    New-ModuleManifest -Path (Join-Path $moduleRoot 'RemoteHubJEA.psd1') -Description 'RemoteHub JEA role capabilities'
                }
                Copy-Item -LiteralPath $newPsrc -Destination $psrc -Force
            }
        }
        if ($psscChanged -or $psrcChanged -or -not $existing) {
            Write-Warning 'Registering the endpoint restarts WinRM and can drop active remote sessions.'
            if ($PSCmdlet.ShouldProcess($EndpointName, 'Register-PSSessionConfiguration')) {
                New-Item -ItemType Directory -Path (Split-Path $pssc) -Force | Out-Null
                Copy-Item -LiteralPath $newPssc -Destination $pssc -Force
                if ($existing) { Unregister-PSSessionConfiguration -Name $EndpointName -Force -NoServiceRestart }
                Register-PSSessionConfiguration -Name $EndpointName -Path $pssc -Force | Out-Null
                "Registered endpoint $EndpointName for $($AllowedGroup.Count) group(s)."
            } else { "Planned: register endpoint $EndpointName with cmdlets $($VisibleCmdlet -join ', ')." }
        } else { "No change: endpoint $EndpointName is registered with the same role and configuration." }
    } finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -WhatIf:$false -ErrorAction SilentlyContinue
    }

    if ($VerifyUser -and -not $WhatIfPreference) {
        try {
            $caps = @(Get-PSSessionCapability -ConfigurationName $EndpointName -Username $VerifyUser)
            "Effective commands for the verification account ($($caps.Count)):"
            ($caps | Sort-Object Name | ForEach-Object { "  $($_.CommandType) $($_.Name)" })
        } catch { "Capability check failed: $($_.Exception.Message)"; $exitCode = 1 }
    }
} catch {
    Write-Error "JEA deployment failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
