<#
.SYNOPSIS
    Audits local user accounts and the members of privileged local groups against an approved list.
.DESCRIPTION
    Lists local users (enabled, password last set, last logon, password required, expiry) and the
    members of Administrators, Remote Desktop Users, Remote Management Users and Backup Operators,
    looked up by well-known SID so localised group names work. Flags unexpected members, orphaned
    SIDs and the built-in administrator (RID 500, not by name). Get-LocalGroupMember fails on
    unresolved SIDs, so the ADSI WinNT provider is the fallback. Names are redacted unless
    -IncludeIdentity is set; Entra members appear as S-1-12-1-... SIDs.
.NOTES
    RemoteHub: id=INV-11; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$ApprovedAdministrators = @(),
    [switch]$IncludeIdentity
)

$ErrorActionPreference = 'Stop'

$groups = [ordered]@{
    'Administrators'           = 'S-1-5-32-544'
    'Remote Desktop Users'     = 'S-1-5-32-555'
    'Remote Management Users'  = 'S-1-5-32-580'
    'Backup Operators'         = 'S-1-5-32-551'
}

function Format-Name([string]$Name, [string]$Sid) {
    if ($IncludeIdentity) { return $Name }
    if ($Sid -match '-500$') { return 'Built-in administrator' }
    return '[REDACTED]'
}

function Get-MembersAdsi([string]$GroupName) {
    # WinNT returns raw SIDs for members it cannot resolve instead of failing the whole call.
    $g = [ADSI]"WinNT://./$GroupName,group"
    foreach ($m in @($g.psbase.Invoke('Members'))) {
        $path = [string]$m.GetType().InvokeMember('ADsPath', 'GetProperty', $null, $m, $null)
        $sidBytes = $m.GetType().InvokeMember('objectSid', 'GetProperty', $null, $m, $null)
        $sid = (New-Object Security.Principal.SecurityIdentifier($sidBytes, 0)).Value
        [PSCustomObject]@{ Name = ($path -replace '^WinNT://', '' -replace '/', '\'); Sid = $sid; Source = 'ADSI' }
    }
}

try {
    $users = @(Get-LocalUser | ForEach-Object {
        [PSCustomObject]@{
            Name             = Format-Name $_.Name $_.SID.Value
            Rid500           = ($_.SID.Value -match '-500$')
            Enabled          = $_.Enabled
            PasswordLastSet  = if ($_.PasswordLastSet) { $_.PasswordLastSet.ToString('yyyy-MM-dd') } else { 'Never' }
            LastLogon        = if ($_.LastLogon) { $_.LastLogon.ToString('yyyy-MM-dd') } else { 'Never' }
            PasswordRequired = $_.PasswordRequired
            PasswordExpires  = if ($_.PasswordExpires) { $_.PasswordExpires.ToString('yyyy-MM-dd') } else { 'Never' }
        }
    })

    $memberships = New-Object System.Collections.Generic.List[object]
    $unexpectedAdmins = 0
    foreach ($label in $groups.Keys) {
        $grp = Get-LocalGroup -SID $groups[$label] -ErrorAction SilentlyContinue
        if (-not $grp) { continue }
        $members = $null
        try {
            $members = @(Get-LocalGroupMember -Group $grp -ErrorAction Stop | ForEach-Object {
                [PSCustomObject]@{ Name = $_.Name; Sid = $_.SID.Value; Source = [string]$_.PrincipalSource }
            })
        } catch {
            Write-Verbose "Get-LocalGroupMember failed for $label, using ADSI: $($_.Exception.Message)"
            $members = @(Get-MembersAdsi $grp.Name)
        }
        foreach ($m in $members) {
            $finding = 'OK'
            $orphan = ($m.Name -match '^S-1-' -or $m.Name -match '\\S-1-')
            if ($orphan) { $finding = 'Orphaned SID' }
            elseif ($m.Sid -match '-500$') { $finding = 'Built-in administrator' }
            # Without an approved list every member would be "unexpected", which says nothing.
            if ($ApprovedAdministrators.Count -gt 0 -and $label -eq 'Administrators' -and -not $orphan -and $m.Sid -notmatch '-500$') {
                $approved = ($ApprovedAdministrators -contains $m.Name) -or ($ApprovedAdministrators -contains $m.Sid)
                if (-not $approved) { $finding = 'Unexpected administrator'; $unexpectedAdmins++ }
            }
            $memberships.Add([PSCustomObject]@{
                Group   = $label
                Member  = if ($orphan) { $m.Sid } else { Format-Name $m.Name $m.Sid }
                Sid     = $m.Sid
                Source  = $m.Source
                Finding = $finding
            })
        }
    }

    [PSCustomObject]@{
        UnexpectedAdministratorCount = $unexpectedAdmins
        ApprovedListSupplied         = ($ApprovedAdministrators.Count -gt 0)
        LocalUsers                   = $users
        GroupMembers                 = $memberships
    } | ConvertTo-Json -Depth 5

    if ($unexpectedAdmins -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Local account audit failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
