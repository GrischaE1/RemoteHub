<#
.SYNOPSIS
    Reports Active Directory hygiene and privileged access findings as CSV per category and a summary.
.DESCRIPTION
    Finds stale users and computers, risky account flags (password never expires, not required, reversible
    encryption, no Kerberos pre-authentication), privileged group members, SPN accounts and password age,
    delegation, krbtgt password age, orphaned AdminCount, SID history, Windows LAPS coverage and unlinked or
    empty GPOs. The CSVs hold personal and privileged data: they go to the running admin's profile and the
    console shows counts with names redacted unless -IncludeIdentity. lastLogonTimestamp replicates lazily,
    so stale thresholds under 14 days are refused.
.NOTES
    RemoteHub: id=SRV-09; context=AsMe; os=Server; risk=R0
#>
[CmdletBinding()]
param(
    [string]$Server = '',
    [ValidateRange(14, 3650)]
    [int]$StaleDays = 90,
    [int]$ServiceAccountPasswordAgeDays = 365,
    [int]$KrbtgtPasswordAgeDays = 180,
    [bool]$ExportCsv = $true,
    [string]$OutputFolder = "$env:LOCALAPPDATA\RemoteHub\ScriptLogs",
    [switch]$IncludeIdentity
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Output 'Data unavailable: the ActiveDirectory module is not installed.'
        exit 1
    }
    Import-Module ActiveDirectory -Verbose:$false
    $sp = @{}
    if ($Server) { $sp.Server = $Server }
    $domain = Get-ADDomain @sp
    $sp.Server = $domain.PDCEmulator
    $now = Get-Date
    $staleFt = $now.AddDays(-$StaleDays).ToFileTimeUtc()
    $props = @('sAMAccountName', 'distinguishedName', 'userAccountControl', 'lastLogonTimestamp', 'pwdLastSet', 'objectClass')
    $findings = [ordered]@{}

    function Select-Row($o, [string]$Detail) {
        $last = 'Never'
        if ($o.LastLogonTimestamp) { $last = [DateTime]::FromFileTime($o.LastLogonTimestamp).ToString('yyyy-MM-dd') }
        $pwdDate = 'Never'
        if ($o.PwdLastSet) { $pwdDate = [DateTime]::FromFileTime($o.PwdLastSet).ToString('yyyy-MM-dd') }
        [pscustomobject]@{ SamAccountName = $o.SamAccountName; DistinguishedName = $o.DistinguishedName; Class = $o.ObjectClass
            Enabled = -not ([int]$o.userAccountControl -band 2); LastLogon = $last; PasswordLastSet = $pwdDate; Detail = $Detail }
    }
    function Find-Ldap([string]$Category, [string]$Filter, [string]$Detail, [string[]]$Extra = @()) {
        Write-Verbose "Querying $Category"
        $script:findings[$Category] = @(Get-ADObject -LDAPFilter $Filter -Properties ($props + $Extra) -ResultPageSize 500 @sp | ForEach-Object { Select-Row $_ $Detail })
    }

    $user = '(&(objectCategory=person)(objectClass=user)'
    $enabled = '(!(userAccountControl:1.2.840.113556.1.4.803:=2))'
    Find-Ldap 'StaleUsers' "$user$enabled(|(lastLogonTimestamp<=$staleFt)(!(lastLogonTimestamp=*))))" "No logon for $StaleDays days"
    Find-Ldap 'StaleComputers' "(&(objectCategory=computer)$enabled(|(lastLogonTimestamp<=$staleFt)(!(lastLogonTimestamp=*))))" "No logon for $StaleDays days"
    Find-Ldap 'PasswordNeverExpires' "$user$enabled(userAccountControl:1.2.840.113556.1.4.803:=65536))" 'DONT_EXPIRE_PASSWORD'
    Find-Ldap 'PasswordNotRequired' "$user(userAccountControl:1.2.840.113556.1.4.803:=32))" 'PASSWD_NOTREQD'
    Find-Ldap 'ReversibleEncryption' "$user(userAccountControl:1.2.840.113556.1.4.803:=128))" 'ENCRYPTED_TEXT_PWD_ALLOWED'
    Find-Ldap 'NoKerberosPreauth' "$user(userAccountControl:1.2.840.113556.1.4.803:=4194304))" 'DONT_REQ_PREAUTH'
    Find-Ldap 'UnconstrainedDelegation' '(&(userAccountControl:1.2.840.113556.1.4.803:=524288)(!(primaryGroupID=516))(!(primaryGroupID=521)))' 'TRUSTED_FOR_DELEGATION (not a DC)'
    Find-Ldap 'ConstrainedDelegation' '(msDS-AllowedToDelegateTo=*)' 'msDS-AllowedToDelegateTo set'
    Find-Ldap 'SidHistory' '(sIDHistory=*)' 'sIDHistory present'

    $pwdCut = $now.AddDays(-$ServiceAccountPasswordAgeDays).ToFileTimeUtc()
    Find-Ldap 'ServiceAccountsOldPassword' "$user(servicePrincipalName=*)(pwdLastSet<=$pwdCut)(!(sAMAccountName=krbtgt)))" "SPN account, password older than $ServiceAccountPasswordAgeDays days"

    $krb = Get-ADUser -Identity krbtgt -Properties PasswordLastSet @sp
    $krbAge = [int]($now - $krb.PasswordLastSet).TotalDays

    $privGroups = @('Domain Admins', 'Enterprise Admins', 'Schema Admins', 'Administrators', 'Account Operators', 'Backup Operators', 'DnsAdmins', 'Protected Users')
    $privRows = @()
    $privDns = @{}
    foreach ($g in $privGroups) {
        try {
            foreach ($m in @(Get-ADGroupMember -Identity $g -Recursive @sp)) {
                if ($g -ne 'Protected Users') { $privDns[$m.distinguishedName] = $true }
                $privRows += [pscustomobject]@{ Group = $g; SamAccountName = $m.SamAccountName; DistinguishedName = $m.distinguishedName; Class = $m.objectClass }
            }
        } catch { Write-Verbose "Group $g not found or not readable in this domain." }
    }
    $findings['PrivilegedMembers'] = $privRows
    $findings['OrphanedAdminCount'] = @(Get-ADObject -LDAPFilter '(&(adminCount=1)(|(objectClass=user)(objectClass=computer)))' -Properties $props @sp |
        Where-Object { -not $privDns.ContainsKey($_.DistinguishedName) -and $_.SamAccountName -ne 'krbtgt' } | ForEach-Object { Select-Row $_ 'adminCount=1 but not in a privileged group' })

    $lapsStatus = 'OK'
    try {
        $comps = @(Get-ADComputer -Filter 'Enabled -eq $true' -Properties 'msLAPS-PasswordExpirationTime', 'PrimaryGroupID' @sp | Where-Object { $_.PrimaryGroupID -ne 516 -and $_.PrimaryGroupID -ne 521 })
        $findings['NoWindowsLaps'] = @($comps | Where-Object { -not $_.'msLAPS-PasswordExpirationTime' } | ForEach-Object {
            [pscustomobject]@{ SamAccountName = $_.SamAccountName; DistinguishedName = $_.DistinguishedName; Detail = 'No Windows LAPS password expiry' } })
    } catch { $lapsStatus = 'Data unavailable (Windows LAPS schema not extended?)' }

    $linked = @{}
    Get-ADObject -LDAPFilter '(gPLink=*)' -Properties gPLink @sp | ForEach-Object {
        foreach ($m in [regex]::Matches($_.gPLink, '\{([0-9A-Fa-f-]{36})\}')) { $linked[$m.Groups[1].Value.ToUpperInvariant()] = $true } }
    try {
        $sitesDn = "CN=Sites,$((Get-ADRootDSE @sp).configurationNamingContext)"
        Get-ADObject -SearchBase $sitesDn -LDAPFilter '(gPLink=*)' -Properties gPLink @sp | ForEach-Object {
            foreach ($m in [regex]::Matches($_.gPLink, '\{([0-9A-Fa-f-]{36})\}')) { $linked[$m.Groups[1].Value.ToUpperInvariant()] = $true } }
    } catch { Write-Verbose 'Site GPO links not readable.' }
    $gpos = @(Get-ADObject -SearchBase "CN=Policies,CN=System,$($domain.DistinguishedName)" -LDAPFilter '(objectClass=groupPolicyContainer)' -Properties displayName, versionNumber, name @sp)
    $findings['UnlinkedOrEmptyGpos'] = @($gpos | ForEach-Object {
        $guid = $_.Name.Trim('{}').ToUpperInvariant()
        $why = @()
        if (-not $linked.ContainsKey($guid)) { $why += 'Unlinked' }
        if ([int]$_.versionNumber -eq 0) { $why += 'Empty' }
        if ($why.Count -gt 0) { [pscustomobject]@{ SamAccountName = ''; DistinguishedName = $_.DistinguishedName; GpoName = $_.displayName; Detail = $why -join ', ' } } })

    $folder = $null
    if ($ExportCsv) {
        $folder = Join-Path $OutputFolder ("SRV-09-" + $now.ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        foreach ($k in $findings.Keys) { $findings[$k] | Export-Csv -LiteralPath (Join-Path $folder "$k.csv") -NoTypeInformation -Encoding UTF8 }
    }

    $summary = foreach ($k in $findings.Keys) { [pscustomobject]@{ Category = $k; Count = @($findings[$k]).Count } }
    $summary += [pscustomobject]@{ Category = "KrbtgtPasswordAgeDays (limit $KrbtgtPasswordAgeDays)"; Count = $krbAge }
    "Domain: $($domain.DNSRoot)   Windows LAPS coverage: $lapsStatus"
    $summary | Format-Table -AutoSize | Out-String -Width 200
    foreach ($k in @('PrivilegedMembers', 'UnconstrainedDelegation', 'NoKerberosPreauth', 'ReversibleEncryption')) {
        $rows = @($findings[$k])
        if ($rows.Count -eq 0) { continue }
        "$k (first 20):"
        $rows | Select-Object -First 20 | ForEach-Object {
            $n = '[REDACTED]'
            if ($IncludeIdentity) { $n = $_.SamAccountName }
            '  ' + $n + '  ' + $_.Detail + $(if ($_.PSObject.Properties['Group']) { $_.Group } else { '' })
        }
    }
    if ($folder) { "CSV files (contain identities, keep restricted): $folder" }

    $bad = @($findings['PasswordNotRequired']).Count + @($findings['ReversibleEncryption']).Count + @($findings['NoKerberosPreauth']).Count + @($findings['UnconstrainedDelegation']).Count
    if ($bad -gt 0 -or $krbAge -gt $KrbtgtPasswordAgeDays) { exit 1 }
    exit 0
} catch {
    Write-Error "AD hygiene report failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
