# RemoteHub's elevated update step (UpdateService.StartInstall; docs/security-test-lab-design.md SEC-UPD-02): waits for
# RemoteHub to exit, copies the MSI into Program Files\RemoteHub Updates (administrators and SYSTEM only), checks the
# copy's SHA-256 and - when RemoteHub is signed - its signature and publisher, installs that copy and writes the outcome
# to results\<id>.txt.
#
# Started elevated by RemoteHubUpdate.exe from RemoteHub's folder with -Signer (itself): the publisher is whoever signed
# it, never a value the caller passes. Where that isn't installed, RemoteHub runs this text through Windows PowerShell
# with -Publisher (UpdateService.InstallCommand). Only cmdlets and icacls, so it also runs in Constrained Language Mode.
# Windows PowerShell 5.1 reads this file as ANSI: ASCII only.
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Sha256,
    [Parameter(Mandatory)][string]$Id,
    [int]$WaitPid,
    [string]$Publisher = '',
    [string]$PublisherKey = '',
    [string]$Signer = '',
    [string]$Stage = ''
)
$ErrorActionPreference = 'Stop'
# The id names the result and the copy: nothing else may end up in a file name.
if ($Id -notmatch '^[0-9a-fA-F]{32}$') { return }
# RemoteHub hosts PowerShell 7, which puts its own module folders into PSModulePath; inherited, Windows PowerShell would
# look for Get-FileHash there and not find it. Its own folders only, and nothing a user can write.
$env:PSModulePath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules;$env:ProgramFiles\WindowsPowerShell\Modules"
if (-not $Stage) { $Stage = Join-Path $env:ProgramFiles 'RemoteHub Updates' } # another folder: tests only
$sha = $Sha256.ToUpperInvariant()
$results = Join-Path $Stage 'results'
$target = Join-Path $Stage ($Id + '.msi')
# The publisher is a certificate's subject and its public key (hex, as .NET's GetPublicKeyString): the subject alone
# could come from any CA this PC trusts. Properties and -f only, so it runs in Constrained Language Mode.
function KeyOf($cert) { (@($cert.PublicKey.EncodedKeyValue.RawData) | ForEach-Object { '{0:X2}' -f $_ }) -join '' }
# Keys of the publisher's next certificate, shipped one release ahead of a renewal that brings a new key - the same list
# as Authenticode.NextPublisherKeys.
$NextKeys = @()
function Result([string]$text) { Set-Content -LiteralPath (Join-Path $results ($Id + '.txt')) -Value $text -Encoding UTF8 }
try {
    foreach ($dir in $Stage, $results) {
        if (Test-Path -LiteralPath $dir) { if ("$((Get-Item -LiteralPath $dir -Force).Attributes)" -match 'ReparsePoint') { throw "$dir is a link" } }
        else { $null = New-Item -ItemType Directory -Path $dir }
    }
    # Administrators and SYSTEM only; users may read the outcomes, nothing else. /reset first: a folder made where
    # nothing is inherited gets the creator's default permissions as its own entries, which /inheritance:r keeps.
    $icacls = "$env:SystemRoot\System32\icacls.exe"
    $null = & $icacls $Stage /reset
    $null = & $icacls $Stage /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
    if ($LASTEXITCODE -ne 0) { throw 'the staging folder could not be locked down' }
    $null = & $icacls $results /reset
    $null = & $icacls $results /grant '*S-1-5-32-545:(OI)(CI)RX'
    Get-ChildItem -LiteralPath $Stage -Filter *.msi -File -Force | Remove-Item -Force
    Get-ChildItem -LiteralPath $results -File -Force | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item -Force
    # The publisher is whoever signed RemoteHubUpdate.exe. Signed but not valid (changed, or a chain this PC doesn't
    # trust): nothing is installed. Not signed at all (a local build): the checksum is all there is, as for RemoteHub.
    if ($Signer) {
        $own = Get-AuthenticodeSignature -LiteralPath $Signer
        if ($own.SignerCertificate) {
            if ($own.Status -ne 'Valid') { Result 'refused signature'; return }
            $Publisher = $own.SignerCertificate.Subject
            $PublisherKey = KeyOf $own.SignerCertificate
        }
    }
    if ($WaitPid -gt 0) { Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction SilentlyContinue }
    Copy-Item -LiteralPath $Source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sha) { Result 'refused changed'; return }
    if ($Publisher) {
        $sig = Get-AuthenticodeSignature -LiteralPath $target
        if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -cne $Publisher) { Result 'refused signature'; return }
        $key = KeyOf $sig.SignerCertificate
        if (-not $key -or ($key -ne $PublisherKey -and $NextKeys -notcontains $key)) { Result 'refused signature'; return }
    }
    # msiexec's log next to the outcome (readable by users like it, gone after 7 days): an exit code like 1603
    # alone doesn't say why the install failed (lab SEC-UPD-02, run 20261021-222745)
    $log = Join-Path $results ($Id + '.log')
    $p = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList '/i', ('"' + $target + '"'), '/passive', '/norestart', '/l*v', ('"' + $log + '"') -Wait -PassThru
    Result ('installed ' + $p.ExitCode)
}
catch { try { Result ('failed ' + $_.Exception.Message) } catch { } }
finally { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue } }
