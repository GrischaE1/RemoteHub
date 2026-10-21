# RemoteHub - runs a script on this device (docs/scripts-view-design.md section 6.2).
# Loaded into the remote PowerShell session that RemoteHub opened (WinRM or the RemoteHub Agent), as the account that
# signed in. Defines functions only; RemoteHub calls them one after the other. Windows PowerShell 5.1 syntax, ASCII only.
#
# System and User runs: a one-off scheduled task as SYSTEM or as the signed-in user, in a run folder only SYSTEM,
# Administrators and that user can open. As me: a child process of this session, in the account's own temp folder.
# The script text is always a file in the run folder, never on a command line. Everything is removed when the run ends.

function RemoteHubRun-Root {
    Join-Path $env:ProgramData 'RemoteHub\Runs'
}

function RemoteHubRun-IsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The users signed in on the device, and which one is the console user: the user at the console, or the only signed-in
# user when nobody is at the console (a client used over RDP).
function RemoteHubRun-Users {
    $users = @()
    $seen = @{}
    foreach ($p in @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'explorer.exe'" -ErrorAction SilentlyContinue)) {
        $o = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction SilentlyContinue
        if ($null -eq $o -or $o.ReturnValue -ne 0 -or [string]::IsNullOrEmpty($o.User)) { continue }
        $name = $o.Domain + '\' + $o.User
        $key = $name.ToLowerInvariant() + '|' + $p.SessionId
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $users += [pscustomobject]@{ User = $name; SessionId = [int]$p.SessionId; IsConsole = $false }
    }
    $console = $null
    try { $console = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).UserName } catch { }
    $marked = $false
    if (-not [string]::IsNullOrEmpty($console)) {
        foreach ($u in $users) {
            if (-not $marked -and $u.User -ieq $console) { $u.IsConsole = $true; $marked = $true }
        }
    }
    $distinct = @($users | Select-Object -ExpandProperty User -Unique)
    if (-not $marked -and $distinct.Count -eq 1) { $users[0].IsConsole = $true }
    $users
}

# What the run window shows before a run: client or server Windows, and who is signed in.
function RemoteHubRun-Info {
    $os = $null
    try { $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop } catch { }
    $type = 0
    $caption = ''
    if ($null -ne $os) { $type = [int]$os.ProductType; $caption = [string]$os.Caption }
    [pscustomobject]@{ ProductType = $type; Caption = $caption; IsAdmin = (RemoteHubRun-IsAdmin); Users = @(RemoteHubRun-Users) }
}

# Refusals RemoteHub explains in its own words: the reason comes first, a colon, then details.
function RemoteHubRun-Check {
    param([string]$Context)
    if ($Context -ne 'AsMe' -and -not (RemoteHubRun-IsAdmin)) { throw 'NOT_ADMIN: running as System or User needs an administrator account' }
    foreach ($scope in 'MachinePolicy', 'UserPolicy') {
        # Where the policy can't be read, the run goes ahead: PowerShell itself still refuses what the policy blocks.
        $policy = ''
        try { $policy = [string](Get-ExecutionPolicy -Scope $scope -ErrorAction Stop) } catch { continue }
        if ($policy -eq 'AllSigned' -or $policy -eq 'Restricted') { throw ('GPO_POLICY: ' + $scope + ' is ' + $policy) }
    }
}

function RemoteHubRun-Cleanup {
    $root = RemoteHubRun-Root
    $limit = (Get-Date).AddHours(-24)
    try {
        foreach ($t in @(Get-ScheduledTask -TaskPath '\RemoteHub\' -ErrorAction SilentlyContinue)) {
            if ($t.TaskName -notlike 'RemoteHub run *') { continue }
            $id = $t.TaskName.Substring(14)
            $dir = Join-Path $root $id
            if (-not (Test-Path -LiteralPath $dir) -or (Get-Item -LiteralPath $dir).CreationTime -lt $limit) {
                Stop-ScheduledTask -InputObject $t -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -InputObject $t -Confirm:$false -ErrorAction SilentlyContinue
            }
        }
    } catch { }
    if (Test-Path -LiteralPath $root) {
        foreach ($d in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            if ($d.CreationTime -lt $limit) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    $mine = Join-Path ([IO.Path]::GetTempPath()) 'RemoteHub-Runs'
    if (Test-Path -LiteralPath $mine) {
        foreach ($d in @(Get-ChildItem -LiteralPath $mine -Directory -ErrorAction SilentlyContinue)) {
            if ($d.CreationTime -lt $limit) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

# A folder only SYSTEM and Administrators can open (inheritance off), plus the given account with modify rights.
function RemoteHubRun-SecureFolder {
    param([string]$Path, [string]$Account)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $who = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, 'FullControl', $inherit, $none, $allow)))
    }
    if (-not [string]::IsNullOrEmpty($Account)) {
        $who = (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier])
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, 'Modify', $inherit, $none, $allow)))
    }
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# The wrapper the task or process runs: it runs script.ps1 with the parameters, writes its output to output.txt (at
# most 2 MB, UTF-8) as it comes, and the exit code to result.json at the end.
function RemoteHubRun-Wrapper {
@'
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'output.txt'
$utf8 = New-Object Text.UTF8Encoding($false)
$script:written = 0
$script:cut = $false
function Write-RunLine([string]$line) {
    if ($script:cut) { return }
    $bytes = $utf8.GetByteCount($line) + 2
    if ($script:written + $bytes -gt 2097152) {
        [IO.File]::AppendAllText($out, "[RemoteHub: output cut off]`r`n", $utf8)
        $script:cut = $true
        return
    }
    [IO.File]::AppendAllText($out, $line + "`r`n", $utf8)
    $script:written += $bytes
}
$params = @{}
$paramFile = Join-Path $here 'params.json'
if (Test-Path -LiteralPath $paramFile) {
    $json = [IO.File]::ReadAllText($paramFile, $utf8) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) {
        $v = $p.Value
        if ($v -is [object[]]) { $v = [string[]]$v }
        $params[$p.Name] = $v
    }
}
$code = 0
$global:LASTEXITCODE = 0
try {
    & (Join-Path $here 'script.ps1') @params *>&1 | Out-String -Stream -Width 220 | ForEach-Object { Write-RunLine $_ }
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { $code = [int]$LASTEXITCODE }
    elseif (-not $?) { $code = 1 }
} catch {
    Write-RunLine ('ERROR: ' + $_.Exception.Message)
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) { Write-RunLine $_.InvocationInfo.PositionMessage }
    $code = 1
}
[IO.File]::WriteAllText((Join-Path $here 'result.json'), ('{"ExitCode":' + $code + '}'), $utf8)
exit $code
'@
}

function RemoteHubRun-Start {
    param(
        [Parameter(Mandatory)] [string]$RunId,
        [Parameter(Mandatory)] [ValidateSet('System', 'User', 'AsMe')] [string]$Context,
        [string]$RunUser,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$ScriptText,
        [string]$ParamsJson,
        [int]$TimeoutSec = 600,
        [switch]$PowerShell7
    )
    $ErrorActionPreference = 'Stop'
    if ($RunId -notmatch '^[0-9a-f]{32}$') { throw 'BAD_REQUEST: run id' }
    RemoteHubRun-Check -Context $Context
    RemoteHubRun-Cleanup

    if ($Context -eq 'AsMe') {
        $root = Join-Path ([IO.Path]::GetTempPath()) 'RemoteHub-Runs'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        $dir = Join-Path $root $RunId
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    } else {
        if ($Context -eq 'User' -and [string]::IsNullOrEmpty($RunUser)) { throw 'NO_USER: no user is signed in on the device' }
        $root = RemoteHubRun-Root
        if (-not (Test-Path -LiteralPath $root)) {
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            RemoteHubRun-SecureFolder -Path $root -Account $null
        }
        $dir = Join-Path $root $RunId
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $account = $null
        if ($Context -eq 'User') { $account = $RunUser }
        RemoteHubRun-SecureFolder -Path $dir -Account $account
    }

    $bom = New-Object Text.UTF8Encoding($true)
    [IO.File]::WriteAllText((Join-Path $dir 'script.ps1'), $ScriptText, $bom)
    [IO.File]::WriteAllText((Join-Path $dir 'wrapper.ps1'), (RemoteHubRun-Wrapper), $bom)
    if (-not [string]::IsNullOrEmpty($ParamsJson)) { [IO.File]::WriteAllText((Join-Path $dir 'params.json'), $ParamsJson, (New-Object Text.UTF8Encoding($false))) }
    [IO.File]::WriteAllText((Join-Path $dir 'output.txt'), '', (New-Object Text.UTF8Encoding($false)))

    $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if ($PowerShell7) {
        $pwsh = Get-Command -Name 'pwsh.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $pwsh) {
            $candidate = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
            if (Test-Path -LiteralPath $candidate) { $exe = $candidate } else { throw 'NO_PWSH: PowerShell 7 is not installed' }
        } else { $exe = $pwsh.Source }
    }
    $wrapper = Join-Path $dir 'wrapper.ps1'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $wrapper + '"'

    if ($Context -eq 'AsMe') {
        $proc = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $dir -WindowStyle Hidden -PassThru
        return [pscustomobject]@{ Folder = $dir; Task = $null; ProcessId = $proc.Id }
    }

    $name = 'RemoteHub run ' + $RunId
    $execute = $exe
    $taskArgs = $arguments
    # In the user's session a console window would show; conhost's headless mode (Windows 10 1809 and later) hides it.
    $conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
    if ($Context -eq 'User' -and [Environment]::OSVersion.Version.Build -ge 17763 -and (Test-Path -LiteralPath $conhost)) {
        $execute = $conhost
        $taskArgs = '--headless "' + $exe + '" ' + $arguments
    }
    $action = New-ScheduledTaskAction -Execute $execute -Argument $taskArgs -WorkingDirectory $dir
    if ($Context -eq 'System') {
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    } else {
        $principal = New-ScheduledTaskPrincipal -UserId $RunUser -LogonType Interactive -RunLevel Limited
    }
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Seconds ($TimeoutSec + 300))
    $task = Register-ScheduledTask -TaskName $name -TaskPath '\RemoteHub\' -Action $action -Principal $principal -Settings $settings -Force
    Start-ScheduledTask -InputObject $task
    [pscustomobject]@{ Folder = $dir; Task = $name; ProcessId = 0 }
}

# The output written since Offset (at most 64 KB, base64) and whether the run has ended.
function RemoteHubRun-Poll {
    param([Parameter(Mandatory)] [string]$Folder, [string]$Task, [int]$ProcessId, [long]$Offset)
    $chunk = ''
    $next = $Offset
    $out = Join-Path $Folder 'output.txt'
    if (Test-Path -LiteralPath $out) {
        $fs = [IO.File]::Open($out, 'Open', 'Read', 'ReadWrite, Delete')
        try {
            if ($fs.Length -gt $Offset) {
                [void]$fs.Seek($Offset, 'Begin')
                $buffer = New-Object byte[] ([Math]::Min(65536, $fs.Length - $Offset))
                $read = $fs.Read($buffer, 0, $buffer.Length)
                $chunk = [Convert]::ToBase64String($buffer, 0, $read)
                $next = $Offset + $read
            }
        } finally { $fs.Dispose() }
    }
    $done = $false
    $exit = $null
    $state = 'Running'
    $result = Join-Path $Folder 'result.json'
    if (Test-Path -LiteralPath $result) {
        $done = $true
        try { $exit = [int]((Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).ExitCode) } catch { $exit = 1 }
        $state = 'Ended'
    } elseif ($ProcessId -gt 0) {
        if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { $done = $true; $state = 'Vanished' }
    } elseif (-not [string]::IsNullOrEmpty($Task)) {
        $t = Get-ScheduledTask -TaskName $Task -TaskPath '\RemoteHub\' -ErrorAction SilentlyContinue
        if ($null -eq $t) { $done = $true; $state = 'Vanished' }
        elseif ([string]$t.State -ne 'Running' -and [string]$t.State -ne 'Queued') {
            $info = Get-ScheduledTaskInfo -InputObject $t -ErrorAction SilentlyContinue
            # Ready again without a result: it ended without the wrapper finishing, or never started (0x41303).
            if ($null -ne $info -and $info.LastTaskResult -ne 267011 -and $info.LastTaskResult -ne 267009) {
                $done = $true
                $state = 'NoResult'
                $exit = [int]$info.LastTaskResult
            }
        }
    }
    # A short run can finish between the look for result.json above and the task or process check (Get-ScheduledTask
    # is slow): gone without a result is only so if there is still none now.
    if ($state -eq 'NoResult' -or $state -eq 'Vanished') {
        if (Test-Path -LiteralPath $result) {
            try { $exit = [int]((Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).ExitCode) } catch { $exit = 1 }
            $state = 'Ended'
        }
    }
    [pscustomobject]@{ Chunk = $chunk; Offset = $next; Done = $done; ExitCode = $exit; State = $state }
}

function RemoteHubRun-Stop {
    param([string]$Task, [int]$ProcessId)
    if (-not [string]::IsNullOrEmpty($Task)) { Stop-ScheduledTask -TaskName $Task -TaskPath '\RemoteHub\' -ErrorAction SilentlyContinue }
    if ($ProcessId -gt 0) {
        # The wrapper and the script it started.
        foreach ($c in @(Get-CimInstance -ClassName Win32_Process -Filter ("ParentProcessId = " + $ProcessId) -ErrorAction SilentlyContinue)) {
            Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function RemoteHubRun-Finish {
    param([string]$Folder, [string]$Task, [int]$ProcessId)
    RemoteHubRun-Stop -Task $Task -ProcessId $ProcessId
    if (-not [string]::IsNullOrEmpty($Task)) { Unregister-ScheduledTask -TaskName $Task -TaskPath '\RemoteHub\' -Confirm:$false -ErrorAction SilentlyContinue }
    if (-not [string]::IsNullOrEmpty($Folder) -and (Test-Path -LiteralPath $Folder)) {
        for ($i = 0; $i -lt 10; $i++) {
            Remove-Item -LiteralPath $Folder -Recurse -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $Folder)) { break }
            Start-Sleep -Milliseconds 300
        }
    }
}
