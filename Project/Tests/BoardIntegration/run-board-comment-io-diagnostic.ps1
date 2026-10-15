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
if ($debugPath -notmatch '[\\/]Debug[\\/]') { throw 'The comment I/O diagnostic requires an isolated Debug product build.' }
$cli = Join-Path $output 'toolbox-cli.exe'
$assembly = Join-Path $harness 'BoardsUiLive.dll'
if (!(Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Debug CLI not found: $cli" }
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Live harness not found: $assembly" }
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) ('ToolBox Board Comment I-O Diagnostic ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$previous = @{
    TaskRoot = $env:TOOLBOX_TASK_TEST_ROOT
    CliPath = $env:TOOLBOX_BOARD_CLI_PATH
    ProductOutput = $env:TOOLBOX_BOARD_PRODUCT_OUTPUT
    HarnessMode = $env:TOOLBOX_BOARD_HARNESS_MODE
}
try {
    $env:TOOLBOX_TASK_TEST_ROOT = $taskRoot
    $env:TOOLBOX_BOARD_CLI_PATH = $cli
    $env:TOOLBOX_BOARD_PRODUCT_OUTPUT = $output
    $env:TOOLBOX_BOARD_HARNESS_MODE = 'comment-io-diagnostic'
    & dotnet $assembly
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Comment I/O diagnostic harness exited with code $exitCode. Evidence root: $taskRoot" }
}
finally {
    $env:TOOLBOX_TASK_TEST_ROOT = $previous.TaskRoot
    $env:TOOLBOX_BOARD_CLI_PATH = $previous.CliPath
    $env:TOOLBOX_BOARD_PRODUCT_OUTPUT = $previous.ProductOutput
    $env:TOOLBOX_BOARD_HARNESS_MODE = $previous.HarnessMode
}
