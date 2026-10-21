# Writes a Java argument file (java @file): the arguments reach the program as given, but never appear on a command
# line, where any process on the machine (and process-creation auditing) can read them. Sign-Files.ps1 passes the
# eSigner credentials this way - CodeSignTool only takes them as arguments.
#   . build\JavaArgFile.ps1; Write-JavaArgFile -Path $file -Arguments '-jar', $jar, "-password=$env:ES_PASSWORD"
# One quoted argument per line; inside quotes the launcher reads \ as an escape, so \ and " are escaped. The launcher
# reads the file as bytes in the system code page: only ASCII is written, anything else is refused rather than garbled.
function Write-JavaArgFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $lines = foreach ($a in $Arguments) {
        if ($a -match '[^\x20-\x7E]') { throw "A Java argument file can only carry printable ASCII - one of the arguments has other characters." }
        '"' + $a.Replace('\', '\\').Replace('"', '\"') + '"'
    }
    [IO.File]::WriteAllLines($Path, [string[]]$lines, [Text.Encoding]::ASCII)
}
