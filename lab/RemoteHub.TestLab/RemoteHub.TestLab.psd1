@{
    RootModule        = 'RemoteHub.TestLab.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '5d0c1f3e-6a8b-4a43-9a52-1f0f6b7e2c41'
    Author            = 'Grischa Ernst'
    Description       = 'RemoteHub Hyper-V test lab: revert, guest control, console, simulated users.'
    PowerShellVersion = '5.1'
    RequiredModules   = @('Hyper-V')
    FunctionsToExport = @(
        'Get-LabConfig', 'Get-LabVm', 'Get-LabCredential',
        'Reset-LabVm', 'Stop-LabVm', 'Wait-LabVm', 'Restart-LabVm', 'Get-LabVmAddress',
        'Invoke-LabGuest', 'Copy-LabFile', 'Get-LabFile',
        'Get-LabScreenshot', 'Send-LabText', 'Send-LabKey', 'Send-LabCtrlAltDel',
        'Set-LabAutoLogon', 'Clear-LabAutoLogon', 'Wait-LabDesktop',
        'Invoke-LabInSession', 'Lock-LabSession', 'Unlock-LabSession',
        'Get-LabHostAddress', 'Install-LabAgent', 'Get-LabAgentHealth', 'Get-LabAgentState', 'Uninstall-LabAgent',
        'Save-LabAgentEvidence', 'Get-LabAgentMsi', 'Get-LabMsiVersion', 'Update-LabAgent', 'Invoke-LabAgentCommand', 'New-LabBrokenMsi',
        'Set-LabAgentPolicy', 'Get-LabAgentPolicy', 'Enable-LabShareServers', 'Invoke-LabShare',
        'Invoke-LabTool', 'Invoke-LabProbe', 'Invoke-LabSetupScript', 'Get-LabWinRmState'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
