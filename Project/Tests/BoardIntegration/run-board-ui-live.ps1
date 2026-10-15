param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$HarnessDirectory
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$harness = [IO.Path]::GetFullPath($HarnessDirectory)
$debugPath = $output.TrimEnd([char]'\', [char]'/') + [IO.Path]::DirectorySeparatorChar
if ($debugPath -notmatch '[\\/]Debug[\\/]') { throw 'The live UI harness requires an isolated Debug product build.' }
$cli = Join-Path $output 'toolbox-cli.exe'
$assembly = Join-Path $harness 'BoardsUiLive.dll'
if (!(Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Debug CLI not found: $cli" }
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Live UI harness not found: $assembly" }
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) ('ToolBox Board UI Check ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$previousRoot = $env:TOOLBOX_TASK_TEST_ROOT
$previousCli = $env:TOOLBOX_BOARD_CLI_PATH
$previousProductOutput = $env:TOOLBOX_BOARD_PRODUCT_OUTPUT
try {
    $env:TOOLBOX_TASK_TEST_ROOT = $taskRoot
    $env:TOOLBOX_BOARD_CLI_PATH = $cli
    $env:TOOLBOX_BOARD_PRODUCT_OUTPUT = $output
    & dotnet $assembly
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Live UI harness exited with code $exitCode. Evidence root: $taskRoot" }
    Write-Output "PASS: live WPF/CLI harness exit $exitCode. Evidence root: $taskRoot"
}
finally {
    $env:TOOLBOX_TASK_TEST_ROOT = $previousRoot
    $env:TOOLBOX_BOARD_CLI_PATH = $previousCli
    $env:TOOLBOX_BOARD_PRODUCT_OUTPUT = $previousProductOutput
}
