[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'bin/RelayPane.exe')
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$output = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null
$markdownAssemblies = @(& (Join-Path $PSScriptRoot 'scripts/RestoreMarkdown.ps1') -Cache (Join-Path $PSScriptRoot 'bin/markdown'))

$arguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/out:$output",
    ("/reference:" + (Join-Path $wpf 'WindowsBase.dll')),
    ("/reference:" + (Join-Path $wpf 'PresentationCore.dll')),
    ("/reference:" + (Join-Path $wpf 'PresentationFramework.dll')),
    ("/reference:" + (Join-Path $framework 'System.Xaml.dll')),
    ("/reference:" + (Join-Path $framework 'System.Runtime.Serialization.dll')),
    ("/reference:" + (Join-Path $framework 'System.Web.Extensions.dll')),
    ("/reference:" + (Join-Path $framework 'System.Windows.Forms.dll')),
    ("/reference:" + (Join-Path $framework 'System.Drawing.dll')),
    ("/win32manifest:" + (Join-Path $PSScriptRoot 'src/Preview.manifest')),
    ("/win32icon:" + (Join-Path $PSScriptRoot 'assets/RelayPane.ico')),
    ("/resource:" + (Join-Path $PSScriptRoot 'assets/RelayPane.ico') + ',RelayPane.ico'),
    ("/resource:" + (Join-Path $PSScriptRoot 'src/Preview.xaml') + ',Preview.xaml'),
    ("/resource:" + (Join-Path $PSScriptRoot 'src/Pane.xaml') + ',Pane.xaml'),
    ("/resource:" + (Join-Path $PSScriptRoot 'src/AgentBridge.cjs') + ',AgentBridge.cjs'),
    ("/resource:" + (Join-Path $PSScriptRoot 'docs/MarkdownNotices.txt') + ',Markdown.Notices.txt'),
    (Join-Path $PSScriptRoot 'src/Preview.cs'),
    (Join-Path $PSScriptRoot 'src/PaneView.cs'),
    (Join-Path $PSScriptRoot 'src/WorkspaceLayout.cs'),
    (Join-Path $PSScriptRoot 'src/LiveBridge.cs'),
    (Join-Path $PSScriptRoot 'src/Connections.cs'),
    (Join-Path $PSScriptRoot 'src/ChatLive.cs'),
    (Join-Path $PSScriptRoot 'src/AgentSettings.cs'),
    (Join-Path $PSScriptRoot 'src/AgentCatalog.cs'),
    (Join-Path $PSScriptRoot 'src/AgentInventory.cs'),
    (Join-Path $PSScriptRoot 'src/EmbeddedLibraries.cs'),
    (Join-Path $PSScriptRoot 'src/MarkdownFlow.cs'),
    (Join-Path $PSScriptRoot 'src/MessageBubble.cs'),
    (Join-Path $PSScriptRoot 'src/ChatClipboard.cs'),
    (Join-Path $PSScriptRoot 'src/SurfaceMenus.cs'),
    (Join-Path $PSScriptRoot 'src/SurfaceDialog.cs'),
    (Join-Path $PSScriptRoot 'src/NativeAgentPrograms.cs'),
    (Join-Path $PSScriptRoot 'src/AgentLauncher.cs'),
    (Join-Path $PSScriptRoot 'src/LaunchPathEditor.cs'),
    (Join-Path $PSScriptRoot 'src/StateStore.cs')
    (Join-Path $PSScriptRoot 'src/AssemblyInfo.cs')
)
foreach ($assembly in $markdownAssemblies) {
    $arguments += "/reference:$assembly"
    $arguments += "/resource:$assembly,Markdown.$([IO.Path]::GetFileName($assembly))"
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Compiler exited with code $LASTEXITCODE" }
Get-Item -LiteralPath $output
