@{
    RootModule        = 'RemoteHub.TestLab.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '5d0c1f3e-6a8b-4a43-9a52-1f0f6b7e2c41'
    Author            = 'Grischa Ernst'
    Description       = 'RemoteHub Hyper-V test lab: revert, guest control, console, simulated users.'
    PowerShellVersion = '5.1'
    RequiredModules   = @('Hyper-V')
    FunctionsToExport = @('Invoke-LabHarness', 
        'Get-LabConfig', 'Get-LabVm', 'Get-LabCredential',
        'Reset-LabVm', 'Stop-LabVm', 'Wait-LabVm', 'Restart-LabVm', 'Get-LabVmAddress', 'Disconnect-LabNetwork', 'Connect-LabNetwork',
        'Start-LabJumpHost', 'Stop-LabJumpHost', 'Move-LabVmBehindJump', 'Restore-LabVmNetwork', 'Use-LabJump', 'Invoke-LabJump',
        'Invoke-LabGuest', 'Copy-LabFile', 'Get-LabFile',
        'Get-LabScreenshot', 'Get-LabResolution', 'Wait-LabResolution', 'Get-LabCimVm', 'Initialize-LabVm', 'Clear-LabVmState', 'Set-LabVmDirty', 'Test-LabVmState', 'Get-LabBaseCheckpoint', 'Remove-LabRunCheckpoints', 'Get-LabDevice', 'Test-LabConsoleVm', 'Test-LabVmReady', 'Send-LabText', 'Send-LabKey', 'Send-LabCtrlAltDel', 'Send-LabMouse', 'Send-LabKeyCombo', 'Send-LabScancodes',
        'Set-LabAutoLogon', 'Clear-LabAutoLogon', 'Wait-LabDesktop',
        'Invoke-LabInSession', 'Lock-LabSession', 'Unlock-LabSession',
        'Start-LabInSession', 'Stop-LabInSession', 'Start-LabChangingWindow', 'Stop-LabChangingWindow', 'Get-LabPasteCode', 'Invoke-LabPaste', 'Invoke-LabPerson', 'Invoke-LabBannerClick', 'Start-LabMarker', 'Get-LabMarker', 'Invoke-LabDesktop', 'Start-LabDesktop', 'Receive-LabDesktop', 'Wait-LabHostFile', 'Compare-LabImage', 'Get-LabOcr', 'Get-LabTextDistance', 'Get-LabColorShare', 'Find-LabPictureText', 'Find-LabSignInTile', 'Get-LabCadAnswer',
        'Get-LabHostAddress', 'Install-LabSigningCertificate', 'Install-LabAgent', 'Get-LabAgentHealth', 'Get-LabAgentState', 'Uninstall-LabAgent',
        'Save-LabAgentEvidence', 'Get-LabAgentMsi', 'Get-LabClientMsi', 'Install-LabClient', 'Start-LabClient', 'Close-LabFirstStart', 'Stop-LabClient', 'Invoke-LabUi', 'Wait-LabCredentialDialog','New-LabClientConnection', 'Start-LabMockServer', 'Set-LabMockLatest', 'Set-LabMockControl', 'Get-LabMockRequests', 'Get-LabMsiVersion', 'Update-LabAgent', 'Invoke-LabAgentCommand', 'Set-LabCredentialProvider', 'Disconnect-LabConsole', 'Invoke-LabAutoSignIn', 'New-LabBrokenMsi', 'New-LabRebuiltMsi',
        'Set-LabAgentPolicy', 'Get-LabAgentPolicy', 'Enable-LabShareServers', 'Invoke-LabShare',
        'Invoke-LabTool', 'Invoke-LabProbe', 'Invoke-LabSetupScript', 'Get-LabWinRmState',
        'Restart-LabAgent', 'Wait-LabAgent', 'Restart-LabRdpService', 'New-LabGuestUser', 'New-LabEnrollment', 'Get-LabGuestTime', 'Get-LabAgentEvent', 'Get-LabFailedLogon', 'Get-LabAgentLog'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
