param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../bin/Debug/net8.0-windows10.0.19041.0'),
    [switch]$TagsOnly
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$cli = Join-Path $output 'toolbox-cli.exe'
$app = Join-Path $output 'ToolBox.exe'
$root = Join-Path ([IO.Path]::GetTempPath()) ('ToolBox Board CLI Check ' + [guid]::NewGuid().ToString('N'))
$taskRoot = Join-Path $root 'host-data'
$previousTaskRoot = $env:TOOLBOX_TASK_TEST_ROOT
$env:TOOLBOX_TASK_TEST_ROOT = $taskRoot
$workspace = Join-Path $root 'workspace'
$projectName = '中文 看板 项目 (no git)'
$projectPath = Join-Path $workspace $projectName
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
[IO.Directory]::CreateDirectory($workspace) | Out-Null
[IO.Directory]::CreateDirectory($projectPath) | Out-Null
$script:cliPids = [Collections.Generic.List[int]]::new()
$script:checks = 0
$script:metadataMismatches = [Collections.Generic.List[string]]::new()

function Assert-Board([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}

function Assert-Fields([object]$Value, [string[]]$Fields, [string]$Message) {
    $actual = @($Value.PSObject.Properties.Name)
    $missing = @($Fields | Where-Object { $_ -notin $actual })
    Assert-Board ($missing.Count -eq 0) "$Message Missing fields: $($missing -join ', ')."
}

function Start-BoardCli([string[]]$Arguments, [string]$WorkingDirectory = $workspace) {
    $start = [Diagnostics.ProcessStartInfo]::new($cli)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.Environment['TOOLBOX_TASK_TEST_ROOT'] = $taskRoot
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $script:cliPids.Add($process.Id)
    return $process
}

function Read-BoardCli([Diagnostics.Process]$Process, [int[]]$Expected = @(0), [string[]]$ExpectedCodes = @()) {
    $stdout = $Process.StandardOutput.ReadToEndAsync()
    $stderr = $Process.StandardError.ReadToEndAsync()
    Assert-Board ($Process.WaitForExit(45000)) 'CLI did not finish within 45 seconds.'
    $text = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    Assert-Board ($Expected -contains $Process.ExitCode) "Expected exit $Expected, got $($Process.ExitCode): $text $errors"
    try { $response = $text | ConvertFrom-Json }
    catch { throw "CLI did not return one JSON response: $text $errors" }
    if ($ExpectedCodes.Count -gt 0) {
        Assert-Board ($ExpectedCodes -contains $response.error.code) "Expected error $ExpectedCodes, got $($response.error.code): $text"
    }
    return $response
}

function Invoke-BoardCli([string[]]$Arguments, [int[]]$Expected = @(0), [string[]]$ExpectedCodes = @(), [string]$WorkingDirectory = $workspace) {
    $process = Start-BoardCli $Arguments $WorkingDirectory
    try { return Read-BoardCli $process $Expected $ExpectedCodes }
    finally { $process.Dispose() }
}

function Write-BoardJson([string]$Name, [object]$Value) {
    $path = Join-Path $taskRoot $Name
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 20 -Compress), [Text.UTF8Encoding]::new($false))
    return $path
}

function Get-BoardTestHosts {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ToolBox.exe'" | Where-Object {
        $_.ExecutablePath -eq $app -and $script:cliPids.Contains([int]$_.ParentProcessId) -and $_.CommandLine -match '(?:^|\s)--task-host(?:\s|$)'
    })
}

function Get-CliHelp([string[]]$Arguments) {
    $process = Start-BoardCli $Arguments
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert-Board ($process.WaitForExit(10000)) 'CLI help did not finish.'
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Assert-Board ($process.ExitCode -eq 0) "CLI help failed with exit $($process.ExitCode): $text"
        return $text
    }
    finally { $process.Dispose() }
}

function Assert-RegistryIsolation {
    $assemblyPath = Join-Path $output 'ToolBox.dll'
    Assert-Board (Test-Path -LiteralPath $assemblyPath) 'Debug ToolBox.dll is missing.'
    $previous = $env:TOOLBOX_TASK_TEST_ROOT
    try {
        $env:TOOLBOX_TASK_TEST_ROOT = $taskRoot
        $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
        $type = $assembly.GetType('ToolBox.Core.Boards.ProjectBoardStore', $true)
        $instance = $type.GetProperty('Instance').GetValue($null)
        $field = $type.GetField('registryPath', [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic)
        $registryPath = [string]$field.GetValue($instance)
        $expected = Join-Path ([IO.Path]::GetFullPath($taskRoot)) 'project-boards.json'
        Assert-Board ([string]::Equals([IO.Path]::GetFullPath($registryPath), [IO.Path]::GetFullPath($expected), [StringComparison]::OrdinalIgnoreCase)) "Board registry escaped the test root: $registryPath"
        Assert-Board ([string]::IsNullOrEmpty([string]$assembly.GetType('ToolBox.Core.Todo.TaskIpc').GetProperty('TestSuffix').GetValue($null)) -eq $false) 'Debug TaskIpc isolation suffix is missing.'
    }
    finally { $env:TOOLBOX_TASK_TEST_ROOT = $previous }
}

try {
    Assert-Board (Test-Path -LiteralPath $cli) 'Build the Debug output first.'
    $debugPath = $output.TrimEnd([char]'\', [char]'/') + [IO.Path]::DirectorySeparatorChar
    Assert-Board ($debugPath -match '[\\/]Debug[\\/]') 'Run only against Debug output; Release ignores test isolation.'
    Assert-RegistryIsolation

    $capabilities = Invoke-BoardCli @('capabilities', 'list')
    $expectedLegacyOperations = @('task.create', 'task.list', 'task.get', 'task.update', 'task.complete', 'task.reopen', 'task.delete', 'task.restore', 'host.stop', 'capabilities.list', 'capabilities.get', 'ocr.file', 'screenshot.search')
    $expectedToolsOperations = @('tools.list', 'tools.get', 'tools.call', 'tools.result', 'tools.cancel')
    $expectedOperations = @(
        'project.register', 'project.list', 'project.get', 'project.unregister', 'board.get',
        'board.column.list', 'board.column.create', 'board.column.update', 'board.column.delete', 'board.column.move',
        'board.card.list', 'board.card.get', 'board.card.create', 'board.card.update', 'board.card.move',
        'board.card.delete', 'board.card.restore', 'board.card.add-to-todo',
        'board.card.claim', 'board.card.release', 'board.card.submit', 'board.card.close', 'board.card.reopen',
        'board.comment.add', 'board.comment.get', 'board.comment.list', 'board.comment.update', 'board.comment.delete',
        'board.comment.inbox', 'board.comment.ack', 'board.comment.bind', 'board.comment.deliver', 'board.comment.delivery',
        'board.attachment.add', 'board.attachment.list', 'board.attachment.remove'
    )
    $actualOperations = @($capabilities.data | Where-Object { $_.name -like 'project.*' -or $_.name -like 'board.*' } | ForEach-Object { $_.name })
    $allCapabilityNames = @($capabilities.data | ForEach-Object { $_.name })
    Assert-Board ($capabilities.ok -and $allCapabilityNames.Count -eq 54) 'Capability list did not return the expected 54 entries.'
    $expectedExistingAndTools = $expectedLegacyOperations + $expectedToolsOperations
    Assert-Board (@($expectedExistingAndTools | Where-Object { $_ -notin $allCapabilityNames }).Count -eq 0) 'A legacy capability or tools operation is missing.'
    Assert-Board ($capabilities.ok -and $expectedOperations.Count -eq $actualOperations.Count -and @($expectedOperations | Where-Object { $_ -notin $actualOperations }).Count -eq 0) 'Capability list does not contain the 36 project-board operations.'
    $expectedOutputFields = [ordered]@{
        'project.register' = @('boardId', 'name', 'path')
        'project.list' = @('[].boardId', '[].name', '[].path')
        'project.get' = @('boardId', 'name', 'path')
        'project.unregister' = @('boardId', 'name', 'path')
        'board.get' = @('schemaVersion', 'boardId', 'name', 'revision', 'boardRevision', 'commentRevision', 'columns[]', 'cards[]')
        'board.column.list' = @('columns', 'revision', 'boardRevision', 'commentRevision')
        'board.column.create' = @('column', 'revision', 'boardRevision', 'commentRevision')
        'board.column.update' = @('column', 'revision', 'boardRevision', 'commentRevision')
        'board.column.delete' = @('column', 'revision', 'boardRevision', 'commentRevision')
        'board.column.move' = @('column', 'revision', 'boardRevision', 'commentRevision')
        'board.card.list' = @('cards', 'revision', 'boardRevision', 'commentRevision')
        'board.card.get' = @('card', 'revision', 'boardRevision', 'commentRevision', 'card.claim', 'commentCount', 'attachmentCount', 'childCount', 'childClosedCount')
        'board.card.create' = @('card', 'revision', 'boardRevision', 'commentRevision')
        'board.card.update' = @('card', 'revision', 'boardRevision', 'commentRevision')
        'board.card.move' = @('card', 'revision', 'boardRevision', 'commentRevision')
        'board.card.delete' = @('card', 'revision', 'boardRevision', 'commentRevision')
        'board.card.restore' = @('card', 'revision', 'boardRevision', 'commentRevision')
        'board.card.add-to-todo' = @('todoId')
    }
    foreach ($operation in $expectedOutputFields.Keys) {
        $capability = (Invoke-BoardCli @('capabilities', 'get', $operation)).data
        $actualFields = @($capability.outputFields)
        if (($actualFields -join "`n") -cne ($expectedOutputFields[$operation] -join "`n")) {
            $script:metadataMismatches.Add("$operation outputFields expected [$($expectedOutputFields[$operation] -join ', ')], got [$($actualFields -join ', ')]")
        }
        $script:checks++
    }
    $moveCapability = (Invoke-BoardCli @('capabilities', 'get', 'board.card.move')).data
    Assert-Board ($moveCapability.inputSchema.properties.PSObject.Properties.Name -contains 'targetColumnId') 'board.card.move schema omits targetColumnId.'
    Assert-Board ($moveCapability.inputSchema.properties.PSObject.Properties.Name -notcontains 'columnId') 'board.card.move schema exposes the wrong destination field.'
    $updateCapability = (Invoke-BoardCli @('capabilities', 'get', 'board.card.update')).data
    $updateFields = @($updateCapability.inputSchema.anyOf | ForEach-Object { $_.required[0] })
    Assert-Board (@(@('columnId', 'order') | Where-Object { $_ -notin $updateFields }).Count -eq 0) 'board.card.update schema omits CLI-supported columnId/order updates.'
    Assert-Board ($updateCapability.command.Contains('--input')) 'board.card.update capability command template is not executable by the CLI.'
    $boardGetCapability = (Invoke-BoardCli @('capabilities', 'get', 'board.get')).data
    Assert-Board (@(@('boardId', 'name', 'revision', 'columns[]', 'cards[]') | Where-Object { $_ -notin $boardGetCapability.outputFields }).Count -eq 0) 'board.get capability outputFields do not describe its returned board shape.'
    $columnDeleteCapability = (Invoke-BoardCli @('capabilities', 'get', 'board.column.delete')).data
    Assert-Board (@(@('column', 'revision') | Where-Object { $_ -notin $columnDeleteCapability.outputFields }).Count -eq 0) 'board.column.delete capability outputFields omit the returned column/revision.'
    $moveHelp = Get-CliHelp @('board', 'card', 'move', '--help')
    Assert-Board ($moveHelp.Contains('--target-column-id')) 'board card move help omits --target-column-id.'
    $updateHelp = Get-CliHelp @('board', 'card', 'update', '--help')
    Assert-Board (@(@('--column-id', '--order', '--clear-due-date') | Where-Object { !$updateHelp.Contains($_) }).Count -eq 0) 'board card update help omits a supported update flag.'
    Assert-Board ((Get-BoardTestHosts).Count -eq 0) 'Static capability and help commands started a ToolBox host.'

    $registration = (Invoke-BoardCli @('project', 'register', '--path', $projectName, '--cwd', $workspace, '--init', '--json')).data
    Assert-Fields $registration @('boardId', 'name', 'path') 'project.register did not return a direct registration object.'
    Assert-Board ($registration.BoardId -and $registration.Path -eq [IO.Path]::GetFullPath($projectPath)) 'Relative project.register path did not resolve against --cwd.'
    $startedHosts = Get-BoardTestHosts
    Assert-Board ($startedHosts.Count -eq 1) 'The isolated CLI did not start exactly one background task host.'
    $hostProcess = Get-Process -Id $startedHosts[0].ProcessId -ErrorAction Stop
    Assert-Board ($hostProcess.MainWindowHandle -eq [IntPtr]::Zero) 'A CLI board command opened a visible ToolBox window.'
    Assert-Board (!(Test-Path -LiteralPath (Join-Path $projectPath '.git'))) 'The test project unexpectedly contains a Git directory.'
    $boardFile = Join-Path $projectPath '.toolbox/board.json'
    Assert-Board (Test-Path -LiteralPath $boardFile) 'project.register --init did not create .toolbox/board.json.'
    $boardId = [string]$registration.BoardId
    $projectList = (Invoke-BoardCli @('project', 'list')).data
    Assert-Board (@($projectList).Count -eq 1 -and [string]$projectList[0].BoardId -eq $boardId) 'project.list returned the wrong registrations.'
    Assert-Fields $projectList[0] @('boardId', 'name', 'path') 'project.list item did not return a registration object.'
    $currentProject = (Invoke-BoardCli @('project', 'get', '--cwd', $projectPath)).data
    Assert-Fields $currentProject @('boardId', 'name', 'path') 'project.get did not return a direct registration object.'
    Assert-Board ([string]$currentProject.BoardId -eq $boardId) 'project.get did not resolve the registered project from --cwd.'
    $board = (Invoke-BoardCli @('board', 'get', '--project', $projectName, '--cwd', $workspace, '--json')).data
    Assert-Fields $board @('schemaVersion', 'boardId', 'name', 'revision', 'boardRevision', 'commentRevision', 'columns', 'cards') 'board.get did not return the expected direct board object shape.'
    Assert-Board ($board.Columns.Count -eq 4) 'board.get did not return the four initialized columns.'
    Assert-Board (@($board.Columns | Where-Object { $_.status -in @('notStarted', 'inProgress', 'pendingReview', 'closed') }).Count -eq 4) 'Initialized columns do not map the four stable states.'
    $revision = [long]$board.Revision
    $todoColumn = $board.Columns | Where-Object Title -eq '待办' | Select-Object -First 1
    if ($TagsOnly) {
        $tagCard = (Invoke-BoardCli @('board', 'card', 'create', '--project', $boardId, '--column-id', [string]$todoColumn.id, '--title', '标签值验收卡', '--revision', "$revision")).data.card
        $revision = [long](Invoke-BoardCli @('board', 'get', '--project', $boardId)).data.revision
        $singleTagOption = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$tagCard.id, '--tags', '标签甲', 'two words', '--revision', "$revision")).data
        $revision = [long]$singleTagOption.revision
        Assert-Board (@($singleTagOption.card.tags).Count -eq 2 -and $singleTagOption.card.tags[0] -eq '标签甲' -and $singleTagOption.card.tags[1] -eq 'two words') 'One --tags option did not accept multiple values including a spaced value.'
        $repeatedTagOption = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$tagCard.id, '--tags', '标签乙', '--tags', 'two words', '--revision', "$revision")).data
        $revision = [long]$repeatedTagOption.revision
        Assert-Board (@($repeatedTagOption.card.tags).Count -eq 2 -and $repeatedTagOption.card.tags[0] -eq '标签乙' -and $repeatedTagOption.card.tags[1] -eq 'two words') 'Repeated --tags options did not preserve both values.'
        $clearedTags = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$tagCard.id, '--tags', '--revision', "$revision")).data
        Assert-Board (@($clearedTags.card.tags).Count -eq 0) 'A zero-value --tags option did not clear the card tags.'
        $null = Invoke-BoardCli @('host', 'stop')
        Assert-Board ((Get-BoardTestHosts).Count -eq 0) 'The isolated background host did not stop after the tags test.'
        Write-Output "PASS: $script:checks isolated CLI tag-value checks. Evidence: $root"
        return
    }
    $review = (Invoke-BoardCli @('board', 'column', 'create', '--project', $boardId, '--title', '验收列', '--order', '1', '--revision', "$revision")).data
    Assert-Fields $review @('column', 'revision', 'boardRevision', 'commentRevision') 'board.column.create returned the wrong shape.'
    $reviewId = [string]$review.column.id
    $revision = [long]$review.revision
    $columns = (Invoke-BoardCli @('board', 'column', 'move', '--project', $boardId, '--id', $reviewId, '--order', '0', '--revision', "$revision")).data
    Assert-Fields $columns @('column', 'revision', 'boardRevision', 'commentRevision') 'board.column.move returned the wrong shape.'
    $revision = [long]$columns.revision
    $columnList = (Invoke-BoardCli @('board', 'column', 'list', '--cwd', $projectPath)).data
    Assert-Fields $columnList @('columns', 'revision', 'boardRevision', 'commentRevision') 'board.column.list returned the wrong shape.'
    Assert-Board ([string]$columnList.columns[0].id -eq $reviewId) 'board.column.move did not reorder the column through CLI.'
    $columnUpdateInput = Write-BoardJson 'column-update.json' ([ordered]@{ title = '验收列更新' })
    $renamedColumn = (Invoke-BoardCli @('board', 'column', 'update', '--project', $boardId, '--id', $reviewId, '--input', $columnUpdateInput, '--revision', "$revision")).data
    Assert-Fields $renamedColumn @('column', 'revision', 'boardRevision', 'commentRevision') 'board.column.update returned the wrong shape.'
    $revision = [long]$renamedColumn.revision
    Assert-Board ([string]$renamedColumn.column.title -eq '验收列更新') 'board.column.update did not rename the column.'

    $cardInput = Write-BoardJson 'card-input.json' ([ordered]@{
        project = $boardId
        title = '中文 CLI 卡片'
        description = 'first line'
        tags = @('验收', 'CLI')
        dueDate = '2026-10-15T09:00:00+08:00'
    })
    $created = (Invoke-BoardCli @('board', 'card', 'create', '--input', $cardInput, '--priority', '2', '--column-id', $reviewId, '--revision', "$revision")).data
    Assert-Fields $created @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.create returned the wrong shape.'
    $card = $created.card
    $revision = [long]$created.revision
    Assert-Board ($card.title -eq '中文 CLI 卡片' -and $card.priority -eq 2 -and $card.tags.Count -eq 2 -and $card.columnId -eq $reviewId -and $card.status -eq 'notStarted') 'JSON input and non-conflicting flags did not merge into a not-started card.'
    $duplicateTitle = Invoke-BoardCli @('board', 'card', 'create', '--input', $cardInput, '--title', 'duplicate') @(2) @('INVALID_ARGUMENT')
    $invalidCardInput = Write-BoardJson 'invalid-card.json' ([ordered]@{ title = 'must not persist'; unknown = $true })
    $invalidPayload = Invoke-BoardCli @('board', 'card', 'create', '--input', $invalidCardInput) @(2) @('INVALID_ARGUMENT')
    $invalidStatusInput = Write-BoardJson 'invalid-card-status.json' ([ordered]@{ title = 'must not set state'; status = 'closed' })
    $invalidStatus = Invoke-BoardCli @('board', 'card', 'create', '--input', $invalidStatusInput) @(2) @('INVALID_ARGUMENT')
    $dueDateConflict = Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$card.id, '--due-date', '2026-10-16T09:00:00+08:00', '--clear-due-date') @(2) @('INVALID_ARGUMENT')
    $latest = (Invoke-BoardCli @('board', 'get', '--project', $boardId)).data
    Assert-Board (@($latest.Cards).Count -eq 1) 'A duplicate JSON/flag field created a card.'
    $revision = [long]$latest.Revision

    $cardUpdateInput = Write-BoardJson 'card-update.json' ([ordered]@{ description = 'merged from JSON input' })
    $inputUpdated = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$card.id, '--input', $cardUpdateInput, '--priority', '1', '--revision', "$revision")).data
    Assert-Fields $inputUpdated @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.update returned the wrong shape.'
    $card = $inputUpdated.card
    $revision = [long]$inputUpdated.revision
    Assert-Board ($card.description -eq 'merged from JSON input' -and $card.priority -eq 1) 'board.card.update --input did not merge with a non-conflicting explicit flag.'

    $updated = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$card.id, '--clear-due-date', '--tags', '--column-id', [string]$todoColumn.id, '--order', '0', '--revision', "$revision")).data
    Assert-Fields $updated @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.update with multiple flags returned the wrong shape.'
    $card = $updated.card
    $revision = [long]$updated.revision
    Assert-Board ($null -eq $card.dueDate -and @($card.tags).Count -eq 0 -and [string]$card.columnId -eq [string]$todoColumn.id -and $card.order -eq 0) 'Card update did not clear due date/tags or apply column/order flags.'
    $tagged = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$card.id, '--tags', '标签甲', 'two words', '--revision', "$revision")).data
    $card = $tagged.card
    $revision = [long]$tagged.revision
    Assert-Board (@($card.tags).Count -eq 2 -and $card.tags[1] -eq 'two words') 'One --tags option did not preserve multiple values.'
    $repeatedTags = (Invoke-BoardCli @('board', 'card', 'update', '--project', $boardId, '--id', [string]$card.id, '--tags', '标签乙', '--tags', 'two words', '--revision', "$revision")).data
    $card = $repeatedTags.card
    $revision = [long]$repeatedTags.revision
    Assert-Board (@($card.tags).Count -eq 2 -and $card.tags[0] -eq '标签乙' -and $card.tags[1] -eq 'two words') 'Repeated --tags options did not preserve both values.'
    $moved = (Invoke-BoardCli @('board', 'card', 'move', '--project', $boardId, '--id', [string]$card.id, '--target-column-id', $reviewId, '--order', '0', '--revision', "$revision")).data
    Assert-Fields $moved @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.move returned the wrong shape.'
    $card = $moved.card
    $revision = [long]$moved.revision
    Assert-Board ([string]$card.columnId -eq $reviewId) 'board.card.move did not use targetColumnId.'

    $deleted = (Invoke-BoardCli @('board', 'card', 'delete', '--project', $boardId, '--id', [string]$card.id, '--revision', "$revision")).data
    Assert-Fields $deleted @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.delete returned the wrong shape.'
    $revision = [long]$deleted.revision
    Assert-Board ([string]$deleted.card.id -eq [string]$card.id -and $deleted.card.deletedAt) 'board.card.delete did not soft-delete the card.'
    $visible = (Invoke-BoardCli @('board', 'card', 'list', '--project', $boardId)).data
    $withDeleted = (Invoke-BoardCli @('board', 'card', 'list', '--project', $boardId, '--include-deleted')).data
    $deletedInColumn = (Invoke-BoardCli @('board', 'card', 'list', '--project', $boardId, '--column-id', $reviewId, '--include-deleted')).data
    Assert-Fields $visible @('cards', 'revision') 'board.card.list returned the wrong shape.'
    Assert-Fields $withDeleted @('cards', 'revision') 'board.card.list --include-deleted returned the wrong shape.'
    Assert-Fields $deletedInColumn @('cards', 'revision', 'boardRevision', 'commentRevision') 'board.card.list filters returned the wrong shape.'
    Assert-Board (@($visible.cards).Count -eq 0 -and @($withDeleted.cards).Count -eq 1 -and @($deletedInColumn.cards).Count -eq 1) 'board.card.list columnId/includeDeleted behavior is incorrect.'
    $columnDeleted = (Invoke-BoardCli @('board', 'column', 'delete', '--project', $boardId, '--id', $reviewId, '--target-column-id', [string]$todoColumn.id, '--revision', "$revision")).data
    Assert-Fields $columnDeleted @('column', 'revision') 'board.column.delete returned the wrong shape.'
    $revision = [long]$columnDeleted.revision
    $boardAfterColumnDelete = (Invoke-BoardCli @('board', 'get', '--project', $boardId)).data
    $getCardResponse = (Invoke-BoardCli @('board', 'card', 'get', '--project', $boardId, '--id', [string]$card.id)).data
    Assert-Fields $getCardResponse @('card', 'revision') 'board.card.get returned the wrong shape.'
    $transferredDeletedCard = $getCardResponse.card
    Assert-Board ([string]$columnDeleted.column.id -eq $reviewId -and @($boardAfterColumnDelete.Columns).Count -eq 4 -and [string]$transferredDeletedCard.columnId -eq [string]$todoColumn.id -and $transferredDeletedCard.deletedAt) 'Deleting a column did not transfer its soft-deleted cards.'
    $restored = (Invoke-BoardCli @('board', 'card', 'restore', '--project', $boardId, '--id', [string]$card.id, '--revision', "$revision")).data
    Assert-Fields $restored @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.restore returned the wrong shape.'
    $revision = [long]$restored.revision
    Assert-Board ($null -eq $restored.card.deletedAt) 'board.card.restore did not clear deletedAt.'

    $todoOne = (Invoke-BoardCli @('board', 'card', 'add-to-todo', '--project', $boardId, '--id', [string]$card.id, '--revision', "$revision")).data
    $todoTwo = (Invoke-BoardCli @('board', 'card', 'add-to-todo', '--project', $boardId, '--id', [string]$card.id, '--revision', "$revision")).data
    Assert-Fields $todoOne @('todoId') 'board.card.add-to-todo returned the wrong shape.'
    $todoList = (Invoke-BoardCli @('task', 'list')).data
    Assert-Board ([string]$todoOne.todoId -eq [string]$todoTwo.todoId -and @($todoList).Count -eq 1 -and [string]$todoList[0].id -eq [string]$todoOne.todoId -and $todoList[0].title -eq $card.title -and $todoList[0].description -eq $card.description) 'Repeated add-to-todo did not preserve the v1 task snapshot or created more than one personal task.'

    $oldTaskInput = Write-BoardJson 'old-task.json' ([ordered]@{ title = '旧 task 回归'; description = 'before' })
    $oldTask = (Invoke-BoardCli @('task', 'create', '--input', $oldTaskInput)).data
    $taskId = [string]$oldTask.id
    Assert-Board ((Invoke-BoardCli @('task', 'get', $taskId)).data.title -eq '旧 task 回归') 'Existing task.get failed.'
    $updateInput = Write-BoardJson 'old-task-update.json' ([ordered]@{ description = 'after'; priority = 2 })
    Assert-Board ((Invoke-BoardCli @('task', 'update', $taskId, '--input', $updateInput)).data.description -eq 'after') 'Existing task.update failed.'
    Assert-Board ((Invoke-BoardCli @('task', 'complete', $taskId)).data.isCompleted) 'Existing task.complete failed.'
    Assert-Board (!(Invoke-BoardCli @('task', 'reopen', $taskId)).data.isCompleted) 'Existing task.reopen failed.'
    Assert-Board ((Invoke-BoardCli @('task', 'delete', $taskId)).data.isTrashed) 'Existing task.delete failed.'
    Assert-Board (!(Invoke-BoardCli @('task', 'restore', $taskId)).data.isTrashed) 'Existing task.restore failed.'

    $copyPath = Join-Path $workspace '复制的中文项目'
    [IO.Directory]::CreateDirectory((Join-Path $copyPath '.toolbox')) | Out-Null
    Copy-Item -LiteralPath $boardFile -Destination (Join-Path $copyPath '.toolbox/board.json')
    $copyConflict = Invoke-BoardCli @('project', 'register', '--path', $copyPath) @(4) @('CONFLICT')
    $replaced = (Invoke-BoardCli @('project', 'register', '--path', $copyPath, '--replace-path')).data
    Assert-Fields $replaced @('boardId', 'name', 'path') 'project.register --replace-path did not return a direct registration object.'
    Assert-Board ([string]$replaced.BoardId -eq $boardId -and $replaced.Path -eq [IO.Path]::GetFullPath($copyPath)) 'replacePath did not retain BoardId and update the local path.'
    $oldCwd = Invoke-BoardCli @('project', 'get', '--cwd', $projectPath) @(3) @('NOT_FOUND')
    Assert-Board ($oldCwd.error.code -eq 'NOT_FOUND') 'The previous path remained registered after replacePath.'

    $copyBoardFile = Join-Path $copyPath '.toolbox/board.json'
    $beforeExternalEdit = [IO.File]::ReadAllText($copyBoardFile)
    $external = $beforeExternalEdit | ConvertFrom-Json
    $staleRevision = [long]$external.revision
    $external.name = 'external edit'
    [IO.File]::WriteAllText($copyBoardFile, ($external | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $stale = Invoke-BoardCli @('board', 'card', 'create', '--project', $boardId, '--title', 'must not overwrite', '--revision', "$staleRevision") @(4) @('CONFLICT')
    Assert-Board (([IO.File]::ReadAllText($copyBoardFile)).Contains('external edit')) 'A stale CLI write overwrote an external board change.'
    $corrupted = '{"schemaVersion":1,"boardId":"bad","name":"broken","revision":1,"columns":[],"cards":[],"unknown":true}'
    [IO.File]::WriteAllText($copyBoardFile, $corrupted, [Text.UTF8Encoding]::new($false))
    $readCorrupt = Invoke-BoardCli @('board', 'get', '--project', $boardId) @(6) @('DATA_CORRUPT')
    Assert-Board ([IO.File]::ReadAllText($copyBoardFile) -eq $corrupted) 'A corrupt CLI board read modified the board file.'
    $unregistered = (Invoke-BoardCli @('project', 'unregister', '--cwd', $copyPath)).data
    Assert-Fields $unregistered @('boardId', 'name', 'path') 'project.unregister did not return the removed registration object.'
    Assert-Board ((Test-Path -LiteralPath $copyBoardFile) -and (Test-Path -LiteralPath $boardFile)) 'Unregister removed project board files.'
    Assert-Board (@((Invoke-BoardCli @('project', 'list')).data).Count -eq 0) 'project.unregister did not clear the local registration.'

    $null = Invoke-BoardCli @('host', 'stop')
    Assert-Board ((Get-BoardTestHosts).Count -eq 0) 'The isolated background host did not stop.'
    if ($script:metadataMismatches.Count -gt 0) { throw "Capability output shape metadata mismatches: $($script:metadataMismatches -join '; ')" }
    Write-Output "PASS: $script:checks board CLI/capability checks. Isolated evidence: $root"
}
finally {
    foreach ($entry in (Get-BoardTestHosts)) {
        Stop-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
    }
    $env:TOOLBOX_TASK_TEST_ROOT = $previousTaskRoot
}
