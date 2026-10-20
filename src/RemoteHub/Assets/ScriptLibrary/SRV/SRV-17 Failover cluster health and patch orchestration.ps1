<#
.SYNOPSIS
    Reports failover cluster health and, with -Apply, drains or resumes a node or runs Cluster-Aware Updating.
.DESCRIPTION
    Health covers the cluster, nodes, groups, resources, networks, quorum and cluster shared volumes,
    including redirected mode. Drain pauses a node with drain only after confirming quorum and that the other
    nodes are up; Resume brings it back with failback. CauScan lists the updates CAU would apply; CauRun
    starts a run only with -ScanReviewed and -Apply. Full validation (Test-Cluster) takes storage offline in
    its storage tests, so it is not run here; schedule it in a maintenance window.
.NOTES
    RemoteHub: id=SRV-17; context=AsMe; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Health', 'Drain', 'Resume', 'CauScan', 'CauRun')]
    [string]$Mode = 'Health',
    [string]$ClusterName = '',
    [string]$NodeName = '',
    [int]$MaxFailedNodes = 0,
    # Confirms that a CauScan result was reviewed before a CauRun.
    [switch]$ScanReviewed,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

$exitCode = 0
try {
    if (-not (Get-Module -ListAvailable -Name FailoverClusters)) { Write-Output 'Data unavailable: the FailoverClusters module is not installed.'; exit 1 }
    Import-Module FailoverClusters -Verbose:$false
    $cp = @{}
    if ($ClusterName) { $cp.Cluster = $ClusterName }
    $cluster = Get-Cluster @cp
    $cp.Cluster = $cluster.Name
    $nodes = @(Get-ClusterNode @cp)
    $groups = @(Get-ClusterGroup @cp)
    $resources = @(Get-ClusterResource @cp)
    $networks = @(Get-ClusterNetwork @cp)
    $quorum = Get-ClusterQuorum @cp
    $csv = @()
    try { $csv = @(Get-ClusterSharedVolumeState @cp) } catch { Write-Verbose 'No cluster shared volumes.' }

    $up = @($nodes | Where-Object { [string]$_.State -eq 'Up' })
    $issues = @()
    foreach ($n in @($nodes | Where-Object { [string]$_.State -ne 'Up' })) { $issues += "Node $($n.Name) is $($n.State)" }
    foreach ($g in @($groups | Where-Object { [string]$_.State -notin @('Online', 'PartialOnline') })) { $issues += "Group $($g.Name) is $($g.State) on $($g.OwnerNode)" }
    foreach ($r in @($resources | Where-Object { [string]$_.State -eq 'Failed' })) { $issues += "Resource $($r.Name) failed" }
    foreach ($nw in @($networks | Where-Object { [string]$_.State -ne 'Up' })) { $issues += "Network $($nw.Name) is $($nw.State)" }
    foreach ($c in @($csv | Where-Object { [string]$_.StateInfo -ne 'Direct' })) { $issues += "CSV $($c.Name) on $($c.Node) is $($c.StateInfo) ($($c.FileSystemRedirectedIOReason))" }

    [pscustomobject]@{
        Cluster = $cluster.Name; Quorum = "$($quorum.QuorumType) witness=$($quorum.QuorumResource)"
        NodesUp = "$($up.Count) of $($nodes.Count)"; Groups = $groups.Count; FailedResources = @($resources | Where-Object { [string]$_.State -eq 'Failed' }).Count
        Networks = (@($networks | ForEach-Object { "$($_.Name)=$($_.State)" }) -join ', ')
    } | Format-List | Out-String -Width 200
    $nodes | Select-Object Name, State, NodeWeight, DynamicWeight | Format-Table -AutoSize | Out-String -Width 200
    $groups | Select-Object Name, OwnerNode, State | Format-Table -AutoSize | Out-String -Width 200
    if ($csv.Count -gt 0) { $csv | Select-Object Name, Node, StateInfo, FileSystemRedirectedIOReason | Format-Table -AutoSize | Out-String -Width 200 }
    if ($issues.Count -gt 0) { 'Issues:'; $issues | ForEach-Object { "  - $_" } }
    if (@($nodes | Where-Object { [string]$_.State -eq 'Down' }).Count -gt $MaxFailedNodes -or @($resources | Where-Object { [string]$_.State -eq 'Failed' }).Count -gt 0) { $exitCode = 1 }

    switch ($Mode) {
        'Drain' {
            if (-not $NodeName) { throw '-NodeName is required to drain.' }
            $node = $nodes | Where-Object { $_.Name -eq $NodeName }
            if (-not $node) { throw "Node $NodeName is not in cluster $($cluster.Name)." }
            if ([string]$node.State -eq 'Paused') { Write-Output "No change: $NodeName is already paused."; break }
            # Dynamic quorum adjusts votes, but draining while another node is down leaves no headroom for a second failure.
            $others = @($up | Where-Object { $_.Name -ne $NodeName })
            if ($others.Count -lt 1 -or $up.Count -ne $nodes.Count) { throw 'Not all other nodes are up; draining now risks quorum and capacity.' }
            if (@($resources | Where-Object { [string]$_.State -eq 'Failed' }).Count -gt 0) { throw 'Failed resources exist; fix them before maintenance.' }
            if ($PSCmdlet.ShouldProcess($NodeName, 'Suspend-ClusterNode -Drain')) {
                Suspend-ClusterNode -Name $NodeName -Drain -Wait @cp | Out-Null
                Write-Output "$NodeName is paused and drained: $((Get-ClusterNode -Name $NodeName @cp).DrainStatus)"
            }
        }
        'Resume' {
            if (-not $NodeName) { throw '-NodeName is required to resume.' }
            $node = $nodes | Where-Object { $_.Name -eq $NodeName }
            if (-not $node) { throw "Node $NodeName is not in cluster $($cluster.Name)." }
            if ([string]$node.State -ne 'Paused') { Write-Output "No change: $NodeName is $($node.State)."; break }
            if ($PSCmdlet.ShouldProcess($NodeName, 'Resume-ClusterNode -Failback Immediate')) {
                Resume-ClusterNode -Name $NodeName -Failback Immediate @cp | Out-Null
                Write-Output "$NodeName resumed."
            }
        }
        'CauScan' {
            if (-not (Get-Module -ListAvailable -Name ClusterAwareUpdating)) { throw 'Data unavailable: the ClusterAwareUpdating module is not installed.' }
            $role = $null
            try { $role = Get-CauClusterRole -ClusterName $cluster.Name } catch { Write-Verbose 'No CAU clustered role (remote-updating mode only).' }
            $scan = @(Invoke-CauScan -ClusterName $cluster.Name -CauPluginName Microsoft.WindowsUpdatePlugin)
            "CAU self-updating role: $(if ($role) { 'configured' } else { 'not configured' })"
            $scan | Select-Object NodeName, @{ n = 'Updates'; e = { @($_.UpdateTitle).Count } }, UpdateTitle | Format-Table -AutoSize -Wrap | Out-String -Width 250
        }
        'CauRun' {
            if (-not (Get-Module -ListAvailable -Name ClusterAwareUpdating)) { throw 'Data unavailable: the ClusterAwareUpdating module is not installed.' }
            if (-not $ScanReviewed) { throw 'Run -Mode CauScan, review the result and repeat with -ScanReviewed.' }
            if ($issues.Count -gt 0) { throw 'The cluster has health issues; CAU run refused.' }
            if ($PSCmdlet.ShouldProcess($cluster.Name, 'Invoke-CauRun (drains, patches and restarts nodes one at a time)')) {
                $run = Invoke-CauRun -ClusterName $cluster.Name -CauPluginName Microsoft.WindowsUpdatePlugin -MaxFailedNodes $MaxFailedNodes -RequireAllNodesOnline -Force
                $run | Select-Object ClusterName, RunId, @{ n = 'Nodes'; e = { @($_.NodeResults).Count } } | Format-List | Out-String
                if (@($run.NodeResults | Where-Object { [string]$_.Status -eq 'Failed' }).Count -gt 0) { throw 'CAU reported failed nodes.' }
            }
        }
    }
} catch {
    Write-Error "Cluster task failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
