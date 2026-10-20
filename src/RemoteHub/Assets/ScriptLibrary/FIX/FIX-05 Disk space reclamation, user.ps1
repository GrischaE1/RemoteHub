<#
.SYNOPSIS
    Reclaims disk space in the signed-in user's profile by clearing caches the machine-level cleanup cannot reach.
.DESCRIPTION
    Clears user temp files older than N days, browser cache folders (Edge, Chrome, Firefox), Teams caches, the
    Office file cache, the thumbnail cache and the user's recycle bin; on request it frees up OneDrive files not
    used for N days and turns on Storage Sense. Only cache folders are touched - never cookies, saved logins,
    profile data or documents. Locked files are skipped. The Office file cache is skipped while Office runs,
    because it can hold changes not yet uploaded. Cache paths change between app versions.
.NOTES
    RemoteHub: id=FIX-05; context=User; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$AgeDays = 7,
    [bool]$BrowserCache = $true,
    [bool]$TeamsCache = $true,
    [bool]$OfficeCache = $true,
    [bool]$ThumbnailCache = $true,
    [bool]$RecycleBin = $true,
    [int]$DehydrateOneDriveDays = 0,
    [int]$DehydrateMaxFiles = 500,
    [switch]$EnableStorageSense
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]

function Get-CacheFile([string]$Root, [datetime]$Before) {
    # Own walk so that junctions inside caches are never followed.
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $stack = New-Object System.Collections.Stack
    $stack.Push((Get-Item -LiteralPath $Root -Force))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        try { $items = $dir.GetFileSystemInfos() } catch { continue }
        foreach ($i in $items) {
            if ($i -is [IO.DirectoryInfo]) { $stack.Push($i) } elseif ($i.LastWriteTime -lt $Before) { $i }
        }
    }
}

function Clear-Cache([string]$Category, [string[]]$Roots, [datetime]$Before) {
    $files = @(foreach ($r in $Roots) { Get-CacheFile $r $Before })
    $bytes = [long]0; foreach ($f in $files) { $bytes += $f.Length }
    $freed = [long]0; $skipped = 0; $status = 'Nothing to remove'
    if ($files.Count -gt 0) {
        $status = 'Planned'
        if ($PSCmdlet.ShouldProcess($Category, ('Delete {0} cache file(s), {1:N0} MB' -f $files.Count, ($bytes / 1MB)))) {
            foreach ($f in $files) { try { Remove-Item -LiteralPath $f.FullName -Force; $freed += $f.Length } catch { $skipped++ } }
            $status = 'Done'
        }
    }
    $results.Add([pscustomobject]@{ Category = $Category; Files = $files.Count; BytesFound = $bytes; BytesFreed = $freed; SkippedLocked = $skipped; Status = $status })
}

function Resolve-Wildcard([string]$Pattern) {
    @(Resolve-Path -Path $Pattern -ErrorAction SilentlyContinue | ForEach-Object { $_.ProviderPath })
}

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { Write-Output 'Data unavailable: run this script in the signed-in user context.'; exit 1 }
    $local = $env:LOCALAPPDATA; $roaming = $env:APPDATA
    $now = Get-Date

    Clear-Cache 'User temp' @($env:TEMP) ($now.AddDays(-$AgeDays))
    if ($BrowserCache) {
        $roots = @()
        foreach ($b in 'Microsoft\Edge', 'Google\Chrome') {
            foreach ($sub in 'Cache', 'Code Cache', 'GPUCache') { $roots += Resolve-Wildcard (Join-Path $local "$b\User Data\*\$sub") }
        }
        $roots += Resolve-Wildcard (Join-Path $local 'Mozilla\Firefox\Profiles\*\cache2')
        Clear-Cache 'Browser caches' $roots $now
    }
    if ($TeamsCache) {
        $roots = @()
        foreach ($sub in 'Cache', 'Code Cache', 'GPUCache', 'tmp') { $roots += Join-Path $roaming "Microsoft\Teams\$sub" }
        foreach ($sub in 'Cache', 'Code Cache', 'GPUCache') { $roots += Resolve-Wildcard (Join-Path $local "Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\$sub") }
        Clear-Cache 'Teams caches' $roots $now
    }
    if ($OfficeCache) {
        if (Get-Process -Name WINWORD, EXCEL, POWERPNT, OUTLOOK, ONENOTE -ErrorAction SilentlyContinue) {
            $results.Add([pscustomobject]@{ Category = 'Office file cache'; Files = 0; BytesFound = 0; BytesFreed = 0; SkippedLocked = 0; Status = 'Skipped: Office is running' })
        }
        else { Clear-Cache 'Office file cache' (Resolve-Wildcard (Join-Path $local 'Microsoft\Office\1*.0\OfficeFileCache')) $now }
    }
    if ($ThumbnailCache) {
        $thumbs = @(Get-ChildItem -LiteralPath (Join-Path $local 'Microsoft\Windows\Explorer') -Filter 'thumbcache_*.db' -File -Force -ErrorAction SilentlyContinue)
        $bytes = [long]0; foreach ($f in $thumbs) { $bytes += $f.Length }
        $freed = [long]0; $skipped = 0; $status = 'Nothing to remove'
        if ($thumbs.Count -gt 0 -and $PSCmdlet.ShouldProcess('Thumbnail cache', 'Delete thumbcache files (files in use by Explorer are skipped)')) {
            foreach ($f in $thumbs) { try { Remove-Item -LiteralPath $f.FullName -Force; $freed += $f.Length } catch { $skipped++ } }
            $status = 'Done'
        }
        elseif ($thumbs.Count -gt 0) { $status = 'Planned' }
        $results.Add([pscustomobject]@{ Category = 'Thumbnail cache'; Files = $thumbs.Count; BytesFound = $bytes; BytesFreed = $freed; SkippedLocked = $skipped; Status = $status })
    }
    if ($RecycleBin) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $bytes = [long]0
        foreach ($d in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3')) {
            foreach ($f in @(Get-CacheFile (Join-Path ($d.DeviceID + '\') "`$Recycle.Bin\$sid") $now)) { if ($f.Name -ne 'desktop.ini') { $bytes += $f.Length } }
        }
        $status = 'Nothing to remove'
        if ($bytes -gt 0) {
            $status = 'Planned'
            if ($PSCmdlet.ShouldProcess('Recycle bin', 'Empty the recycle bin')) { Clear-RecycleBin -Force; $status = 'Done' }
        }
        $freed = [long]0; if ($status -eq 'Done') { $freed = $bytes }
        $results.Add([pscustomobject]@{ Category = 'Recycle bin'; Files = '-'; BytesFound = $bytes; BytesFreed = $freed; SkippedLocked = 0; Status = $status })
    }

    if ($DehydrateOneDriveDays -gt 0) {
        $odRoots = @($env:OneDriveCommercial, $env:OneDriveConsumer) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
        if (-not (Get-Process -Name OneDrive -ErrorAction SilentlyContinue) -or -not $odRoots) {
            $results.Add([pscustomobject]@{ Category = 'OneDrive dehydration'; Files = 0; BytesFound = 0; BytesFreed = 0; SkippedLocked = 0; Status = 'Skipped: OneDrive is not running, sync health cannot be confirmed' })
        }
        else {
            # 0x400000 = recall on data access (already online-only); 0x80000 = pinned "always keep on this device".
            $old = $now.AddDays(-$DehydrateOneDriveDays)
            $candidates = @(foreach ($r in $odRoots) { Get-CacheFile $r $old } ) | Where-Object { $_.LastAccessTime -lt $old -and -not ([int]$_.Attributes -band 0x400000) -and -not ([int]$_.Attributes -band 0x80000) } | Select-Object -First $DehydrateMaxFiles
            $candidates = @($candidates)
            $bytes = [long]0; foreach ($f in $candidates) { $bytes += $f.Length }
            $done = 0; $status = 'Nothing to free up'
            if ($candidates.Count -gt 0) {
                $status = 'Planned'
                if ($PSCmdlet.ShouldProcess('OneDrive', "Free up space for $($candidates.Count) file(s) (cloud copies are kept)")) {
                    foreach ($f in $candidates) {
                        $p = Start-Process -FilePath (Join-Path $env:windir 'System32\attrib.exe') -ArgumentList ('-P +U "{0}"' -f $f.FullName) -PassThru -Wait -WindowStyle Hidden
                        if ($p.ExitCode -eq 0) { $done++ }
                    }
                    $status = "Done ($done marked online-only; space is freed by OneDrive in the background)"
                }
            }
            $results.Add([pscustomobject]@{ Category = 'OneDrive dehydration'; Files = $candidates.Count; BytesFound = $bytes; BytesFreed = '-'; SkippedLocked = $candidates.Count - $done; Status = $status })
        }
    }

    if ($EnableStorageSense) {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy'
        $cur = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        $status = 'Already on'
        if (-not ($cur -and $cur.PSObject.Properties['01'] -and $cur.'01' -eq 1)) {
            $status = 'Planned'
            if ($PSCmdlet.ShouldProcess('Storage Sense', 'Turn on for this user')) {
                $logDir = Join-Path $env:LOCALAPPDATA 'RemoteHub\ScriptLogs'
                if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
                $prior = 'absent'; if ($cur -and $cur.PSObject.Properties['01']) { $prior = $cur.'01' }
                Set-Content -LiteralPath (Join-Path $logDir 'FIX-05-storagesense-rollback.txt') -Value "StoragePolicy\01 prior value: $prior" -Encoding UTF8
                if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
                Set-ItemProperty -LiteralPath $key -Name '01' -Value 1 -Type DWord
                $status = 'Turned on'
            }
        }
        $results.Add([pscustomobject]@{ Category = 'Storage Sense'; Files = '-'; BytesFound = '-'; BytesFreed = '-'; SkippedLocked = 0; Status = $status })
    }

    $results | Select-Object Category, Files, @{ n = 'FoundMB'; e = { if ($_.BytesFound -is [long]) { [math]::Round($_.BytesFound / 1MB, 1) } else { '-' } } },
        @{ n = 'FreedMB'; e = { if ($_.BytesFreed -is [long]) { [math]::Round($_.BytesFreed / 1MB, 1) } else { '-' } } }, SkippedLocked, Status
    exit 0
}
catch {
    Write-Output "FIX-05 failed: $($_.Exception.Message)"
    exit 1
}
