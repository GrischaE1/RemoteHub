#Requires -Version 5.1
<#
.SYNOPSIS
    The lab's stand-in for update.remotehub.dev, inside a client VM (hosts entry -> 127.0.0.1, a certificate for the
    name trusted by the VM, an HTTPS binding on 443 - all set up by Start-LabMockServer and gone with the revert).
      GET  /latest[?channel=..]  -> <Root>\latest.json, or 404 when it doesn't exist
      POST /telemetry            -> 204, the body logged
      GET  /download/<name>      -> <Root>\download\<name>
    Every request is one JSON line in <Root>\requests.jsonl: time, method, path, query, user agent, body.
#>
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add('https://+:443/')
$listener.Start()
$log = Join-Path $Root 'requests.jsonl'
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $req = $ctx.Request; $res = $ctx.Response
    try {
        $body = ''
        if ($req.HasEntityBody) { $sr = New-Object IO.StreamReader($req.InputStream, $req.ContentEncoding); $body = $sr.ReadToEnd(); $sr.Dispose() }
        $entry = [ordered]@{ time = (Get-Date).ToString('o'); method = $req.HttpMethod; path = $req.Url.AbsolutePath; query = $req.Url.Query; agent = $req.UserAgent; body = $body }
        Add-Content -Path $log -Value ($entry | ConvertTo-Json -Compress) -Encoding UTF8
        $file = $null; $status = 404; $type = 'application/json'
        if ($req.Url.AbsolutePath -eq '/latest') { $file = Join-Path $Root 'latest.json' }
        elseif ($req.Url.AbsolutePath -eq '/telemetry') { $status = 204 }
        elseif ($req.Url.AbsolutePath -like '/download/*') { $file = Join-Path $Root ('download\' + [IO.Path]::GetFileName($req.Url.AbsolutePath)); $type = 'application/octet-stream' }
        if ($file -and (Test-Path $file)) { $status = 200 }
        $res.StatusCode = $status
        if ($status -eq 200) {
            $bytes = [IO.File]::ReadAllBytes($file)
            $res.ContentType = $type
            $res.ContentLength64 = $bytes.Length
            $res.OutputStream.Write($bytes, 0, $bytes.Length)
        }
    }
    catch { try { $res.StatusCode = 500 } catch { } }
    finally { try { $res.Close() } catch { } }
}
