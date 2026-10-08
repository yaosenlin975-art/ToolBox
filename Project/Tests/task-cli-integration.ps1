param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../bin/Debug/net8.0-windows10.0.19041.0')
)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskCli = Join-Path $taskOutput 'toolbox-cli.exe'
$taskApp = Join-Path $taskOutput 'ToolBox.exe'
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) ('ToolBox-task-check-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$script:cliPids = [Collections.Generic.List[int]]::new()

function Assert-Task([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Start-TaskCli([string[]]$CliArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($taskCli)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.Environment['TOOLBOX_TASK_TEST_ROOT'] = $taskRoot
    foreach ($argument in $CliArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $script:cliPids.Add($process.Id)
    return $process
}
function Read-TaskCli([Diagnostics.Process]$Process, [int[]]$Expected = @(0)) {
    $stdout = $Process.StandardOutput.ReadToEndAsync()
    $stderr = $Process.StandardError.ReadToEndAsync()
    Assert-Task ($Process.WaitForExit(45000)) 'CLI did not finish within 45 seconds.'
    $text = $stdout.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    $errors = $stderr.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    Assert-Task ($Expected -contains $Process.ExitCode) "Expected exit $Expected, got $($Process.ExitCode): $text $errors"
    return ($text | ConvertFrom-Json)
}
function Invoke-TaskCli([string[]]$CliArguments, [int[]]$Expected = @(0)) {
    $process = Start-TaskCli $CliArguments
    try { return Read-TaskCli $process $Expected }
    finally { $process.Dispose() }
}
function Write-TaskPayload([string]$Name, [string]$Json) {
    $path = Join-Path $taskRoot $Name
    [IO.File]::WriteAllText($path, $Json, [Text.UTF8Encoding]::new($false))
    return $path
}
function Get-TestHosts {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ToolBox.exe'" | Where-Object {
        $_.ExecutablePath -eq $taskApp -and $script:cliPids.Contains([int]$_.ParentProcessId)
    })
}

try {
    Assert-Task (Test-Path -LiteralPath $taskCli) 'Build Debug output with the bundled CLI first.'
    # This script deliberately requires the Debug-only isolation hook.
    Assert-Task ($taskOutput -match '[\\/]Debug[\\/]') 'Run only against Debug output; Release ignores test isolation.'
    $help = Start-TaskCli @('--help')
    Assert-Task ($help.WaitForExit(10000)) 'Help did not finish.'
    Assert-Task ($help.ExitCode -eq 0) 'Help failed.'
    $help.Dispose()
    Assert-Task ((Get-TestHosts).Count -eq 0) 'Help started a host.'
    $capabilities = Invoke-TaskCli @('capabilities', 'list')
    Assert-Task ($capabilities.ok) 'Capability discovery failed.'
    $ocrCapability = Invoke-TaskCli @('capabilities', 'get', 'ocr.file')
    Assert-Task ($ocrCapability.ok) 'OCR capability is missing.'
    $null = Invoke-TaskCli @('capabilities', 'get', 'system.repair') 3
    Assert-Task ((Get-TestHosts).Count -eq 0) 'Capability discovery started a host.'
    $null = Invoke-TaskCli @('host', 'stop')
    Assert-Task ((Get-TestHosts).Count -eq 0) 'Stopping a missing host started it.'
    $badFile = Write-TaskPayload 'bad.json' '{"title":"  "}'
    $bad = Invoke-TaskCli @('task', 'create', '--input', $badFile) 2
    Assert-Task (!$bad.ok) 'Invalid input returned success.'
    Assert-Task ((Get-TestHosts).Count -eq 0) 'Invalid input started a host.'

    $parallel = @()
    1..4 | ForEach-Object {
        $file = Write-TaskPayload "parallel-$_.json" ('{"title":"concurrent-' + $_ + '"}')
        $parallel += Start-TaskCli @('task', 'create', '--input', $file)
    }
    foreach ($process in $parallel) {
        try { $null = Read-TaskCli $process } finally { $process.Dispose() }
    }
    $hostEntries = Get-TestHosts
    Assert-Task ($hostEntries.Count -eq 1) 'Concurrent cold start did not leave exactly one host.'
    $hostProcess = Get-Process -Id $hostEntries[0].ProcessId
    Assert-Task ($hostProcess.MainWindowHandle -eq [IntPtr]::Zero) 'Background host has a main window.'
    $list = Invoke-TaskCli @('task', 'list')
    Assert-Task ($list.data.Count -eq 4) 'Concurrent creates lost tasks.'
    Assert-Task (Test-Path -LiteralPath (Join-Path $taskRoot 'todos.json')) 'Debug data isolation did not take effect.'

    $tagPath = Join-Path $taskRoot 'screenshot_tags.json'
    [IO.File]::WriteAllText($tagPath, '[{"CacheKey":"C:\\private\\20261008000000001","Tags":["agent"],"Rating":5,"Notes":"approved metadata","DominantColors":[-65536]},{"CacheKey":"20261008000000002","Tags":["agent"],"Rating":4,"Notes":"second"}]')
    $searchFile = Write-TaskPayload 'search.json' '{"tag":"agent","minRating":4,"limit":1}'
    $search = Invoke-TaskCli @('screenshot', 'search', '--input', $searchFile)
    Assert-Task ($search.data.items.Count -eq 1 -and $search.data.items[0].cacheKey -eq '20261008000000001') 'Tag search first page failed.'
    Assert-Task (($search | ConvertTo-Json -Depth 15) -notmatch 'private') 'Tag search leaked a stored absolute path.'
    $nextFile = Write-TaskPayload 'next.json' ('{"tag":"agent","minRating":4,"limit":1,"cursor":"' + $search.data.nextCursor + '"}')
    $next = Invoke-TaskCli @('screenshot', 'search', '--input', $nextFile)
    Assert-Task ($next.data.items[0].cacheKey -eq '20261008000000002' -and $null -eq $next.data.nextCursor) 'Tag pagination failed.'
    $missingImage = Write-TaskPayload 'missing-image.json' ('{"path":' + ((Join-Path $taskRoot 'absent.png') | ConvertTo-Json -Compress) + '}')
    $null = Invoke-TaskCli @('ocr', 'file', '--input', $missingImage) 3
    Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.DrawRectangle([Windows.Media.Brushes]::White, $null, [Windows.Rect]::new(0, 0, 640, 160))
    $caption = [Windows.Media.FormattedText]::new('AGENT 123', [Globalization.CultureInfo]::InvariantCulture,
        [Windows.FlowDirection]::LeftToRight, [Windows.Media.Typeface]::new('Segoe UI'), 48, [Windows.Media.Brushes]::Black, 1)
    $drawing.DrawText($caption, [Windows.Point]::new(20, 40))
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(640, 160, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $imagePath = Join-Path $taskRoot 'fixed-ocr.png'
    $imageStream = [IO.File]::Create($imagePath)
    try { $encoder.Save($imageStream) } finally { $imageStream.Dispose() }
    $ocrFile = Write-TaskPayload 'ocr.json' ('{"path":' + ($imagePath | ConvertTo-Json -Compress) + ',"language":"eng"}')
    $ocr = Invoke-TaskCli @('ocr', 'file', '--input', $ocrFile)
    Assert-Task ($ocr.data.text -match 'AGENT\s+123') 'Fixed local OCR image text was not recognized.'
    Write-Output ('OCR evidence: ' + $ocr.data.engine + ' / ' + $ocr.data.text)
    $missingLanguage = 'toolbox_cli_missing_language'
    $languageAsset = Join-Path $env:LOCALAPPDATA ('ToolBox/ocr/tessdata/' + $missingLanguage + '.traineddata')
    Assert-Task (!(Test-Path -LiteralPath $languageAsset)) 'Choose a genuinely missing test language.'
    $offlineFile = Write-TaskPayload 'offline.json' ('{"path":' + ($imagePath | ConvertTo-Json -Compress) + ',"language":"' + $missingLanguage + '"}')
    $offline = Invoke-TaskCli @('ocr', 'file', '--input', $offlineFile) @(0, 4)
    Assert-Task (!(Test-Path -LiteralPath $languageAsset)) 'Offline OCR downloaded a missing language.'
    Assert-Task ($offline.ok -or $offline.error.code -eq 'ENGINE_UNAVAILABLE') 'Missing offline language returned an unexpected error.'
    $invalidImage = Write-TaskPayload 'not-image.txt' 'not an image'
    $invalidImageInput = Write-TaskPayload 'invalid-image.json' ('{"path":' + ($invalidImage | ConvertTo-Json -Compress) + '}')
    $null = Invoke-TaskCli @('ocr', 'file', '--input', $invalidImageInput) 2
    $largeImage = Join-Path $taskRoot 'large.png'
    $largeStream = [IO.File]::Create($largeImage)
    try { $largeStream.SetLength(16 * 1024 * 1024 + 1) } finally { $largeStream.Dispose() }
    $largeInput = Write-TaskPayload 'large-image.json' ('{"path":' + ($largeImage | ConvertTo-Json -Compress) + '}')
    $null = Invoke-TaskCli @('ocr', 'file', '--input', $largeInput) 2

    $createFile = Write-TaskPayload 'create.json' '{"title":"中文 CLI 检查","description":"line1\nline2","tags":["test"],"dueDate":"2026-10-09T09:00:00+08:00"}'
    $created = Invoke-TaskCli @('task', 'create', '--input', $createFile)
    $taskId = $created.data.id
    Assert-Task ($created.data.title -eq '中文 CLI 检查') 'Unicode title changed.'
    $updateFile = Write-TaskPayload 'update.json' '{"description":"","tags":[],"dueDate":null,"priority":2}'
    $updated = Invoke-TaskCli @('task', 'update', $taskId, '--input', $updateFile)
    Assert-Task ($null -eq $updated.data.dueDate -and $updated.data.description -eq '' -and $updated.data.tags.Count -eq 0) 'Clearing fields failed.'
    $completed = Invoke-TaskCli @('task', 'complete', $taskId)
    Assert-Task ($completed.data.isCompleted -and $completed.data.progress -eq 100) 'Complete failed.'
    $reopened = Invoke-TaskCli @('task', 'reopen', $taskId)
    Assert-Task (!$reopened.data.isCompleted -and $reopened.data.progress -eq 100) 'Reopen did not preserve progress.'
    $deleted = Invoke-TaskCli @('task', 'delete', $taskId)
    Assert-Task ($deleted.data.isTrashed) 'Delete was not a soft delete.'
    $null = Invoke-TaskCli @('task', 'delete', $taskId)
    $restored = Invoke-TaskCli @('task', 'restore', $taskId)
    Assert-Task (!$restored.data.isTrashed) 'Restore failed.'
    $null = Invoke-TaskCli @('task', 'get', 'ffffffffffff') 3
    $null = Invoke-TaskCli @('host', 'stop')
    Assert-Task ($hostProcess.WaitForExit(10000)) 'Background host did not stop.'
    $hostProcess.Dispose()
    $readBack = Invoke-TaskCli @('task', 'get', $taskId)
    Assert-Task ($readBack.data.priority -eq 2 -and $readBack.data.title -eq '中文 CLI 检查') 'Restart lost persisted changes.'
    $null = Invoke-TaskCli @('host', 'stop')
    Write-Output "PASS: CLI discovery, tag pagination/privacy, OCR missing-file, cold-start concurrency, headless host, CRUD, validation, exit codes, persistence. Evidence: $taskRoot"
}
finally {
    foreach ($entry in (Get-TestHosts)) {
        # Only processes launched by this script's recorded CLI PIDs and matching the exact build path.
        Stop-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
    }
}
