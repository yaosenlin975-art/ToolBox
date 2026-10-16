param(
    [ValidateSet('state', 'comments', 'attachments', 'all')]
    [string]$Phase = 'all',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../bin/BoardAcceptance/debug')
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$cli = Join-Path $output 'toolbox-cli.exe'
$app = Join-Path $output 'ToolBox.exe'
$root = Join-Path ([IO.Path]::GetTempPath()) ('ToolBox Board V2 CLI Check ' + [guid]::NewGuid().ToString('N'))
$taskRoot = Join-Path $root 'host-data'
$previousTaskRoot = $env:TOOLBOX_TASK_TEST_ROOT
$env:TOOLBOX_TASK_TEST_ROOT = $taskRoot
$workspace = Join-Path $root 'workspace'
$projectName = '中文 看板 V2 项目'
$projectPath = Join-Path $workspace $projectName
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
[IO.Directory]::CreateDirectory($workspace) | Out-Null
[IO.Directory]::CreateDirectory($projectPath) | Out-Null
$script:cliPids = [Collections.Generic.List[int]]::new()
$script:checks = 0
$script:boardId = ''
$script:revision = 0L
$script:commentRevision = 0L

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
    foreach ($argument in $Arguments) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::Start($start)
    $script:cliPids.Add($process.Id)
    return $process
}

function Read-BoardCli([Diagnostics.Process]$Process, [int[]]$Expected = @(0), [string[]]$ExpectedCodes = @(), [string]$Command = '') {
    $stdout = $Process.StandardOutput.ReadToEndAsync()
    $stderr = $Process.StandardError.ReadToEndAsync()
    Assert-Board ($Process.WaitForExit(45000)) 'CLI did not finish within 45 seconds.'
    $text = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    $commandSummary = if ($Command.Length -gt 240) { $Command.Substring(0, 240) + '…' } else { $Command }
    Assert-Board ($Expected -contains $Process.ExitCode) "Expected exit $Expected, got $($Process.ExitCode) for [$commandSummary]: $text $errors"
    try { $response = $text | ConvertFrom-Json }
    catch { throw "CLI did not return one JSON response: $text $errors" }
    if ($ExpectedCodes.Count -gt 0) {
        Assert-Board ($ExpectedCodes -contains $response.error.code) "Expected error $ExpectedCodes, got $($response.error.code): $text"
    }
    if ($Process.ExitCode -eq 0) { Assert-Board ($response.ok -eq $true) "CLI returned exit 0 with a failed response: $text" }
    return $response
}

function Invoke-BoardCli([string[]]$Arguments, [int[]]$Expected = @(0), [string[]]$ExpectedCodes = @(), [string]$WorkingDirectory = $workspace) {
    $process = Start-BoardCli $Arguments $WorkingDirectory
    try { return Read-BoardCli $process $Expected $ExpectedCodes ($Arguments -join ' ') }
    finally { $process.Dispose() }
}

function Write-BoardJson([string]$Name, [object]$Value) {
    $path = Join-Path $taskRoot $Name
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
    return $path
}

function Get-BoardTestHosts {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ToolBox.exe'" | Where-Object {
        $_.ExecutablePath -eq $app -and $script:cliPids.Contains([int]$_.ParentProcessId) -and $_.CommandLine -match '(?:^|\s)--task-host(?:\s|$)'
    })
}

function Assert-RegistryIsolation {
    $assemblyPath = Join-Path $output 'ToolBox.dll'
    Assert-Board (Test-Path -LiteralPath $assemblyPath) 'Debug ToolBox.dll is missing.'
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $ipc = $assembly.GetType('ToolBox.Core.Todo.TaskIpc', $true)
    $suffix = [string]$ipc.GetProperty('TestSuffix').GetValue($null)
    $normalizedRoot = [IO.Path]::GetFullPath($taskRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
    $expectedSuffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalizedRoot))).Substring(0, 16)
    Assert-Board ($suffix -eq $expectedSuffix) 'Debug IPC isolation suffix does not match TOOLBOX_TASK_TEST_ROOT.'
    Assert-Board ([string]$ipc.GetProperty('PipeName').GetValue($null) -like "*.$suffix") 'Debug task pipe is not isolated by the test root.'

    $type = $assembly.GetType('ToolBox.Core.Boards.ProjectBoardStore', $true)
    $instance = $type.GetProperty('Instance').GetValue($null)
    $field = $type.GetField('registryPath', [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic)
    $registryPath = [string]$field.GetValue($instance)
    $expected = Join-Path ([IO.Path]::GetFullPath($taskRoot)) 'project-boards.json'
    Assert-Board ([string]::Equals([IO.Path]::GetFullPath($registryPath), [IO.Path]::GetFullPath($expected), [StringComparison]::OrdinalIgnoreCase)) "Board registry escaped the test root: $registryPath"
}

function Initialize-BoardProject {
    Assert-Board (Test-Path -LiteralPath $cli) "Debug CLI is missing: $cli"
    Assert-Board ($output -match '(?i)[\\/]Debug(?:[\\/]|$)') 'Run only against an isolated Debug output directory.'
    Assert-RegistryIsolation

    $capabilities = (Invoke-BoardCli @('capabilities', 'list')).data
    $expected = @(
        'project.register', 'project.list', 'project.get', 'project.unregister', 'board.get',
        'board.column.list', 'board.column.create', 'board.column.update', 'board.column.delete', 'board.column.move',
        'board.card.list', 'board.card.get', 'board.card.create', 'board.card.update', 'board.card.move',
        'board.card.delete', 'board.card.restore', 'board.card.add-to-todo',
        'board.card.claim', 'board.card.release', 'board.card.submit', 'board.card.close', 'board.card.reopen',
        'board.comment.add', 'board.comment.get', 'board.comment.list', 'board.comment.update', 'board.comment.delete',
        'board.comment.inbox', 'board.comment.ack', 'board.comment.bind', 'board.comment.deliver', 'board.comment.delivery',
        'board.attachment.add', 'board.attachment.list', 'board.attachment.remove'
    )
    $expectedToolsOperations = @('tools.list', 'tools.get', 'tools.call', 'tools.result', 'tools.cancel')
    $operations = @($capabilities | Where-Object { $_.name -like 'project.*' -or $_.name -like 'board.*' } | ForEach-Object { $_.name })
    $allNames = @($capabilities | ForEach-Object { $_.name })
    Assert-Board ($allNames.Count -eq 54) 'Capability list did not return 54 operations.'
    Assert-Board (@($expectedToolsOperations | Where-Object { $_ -notin $allNames }).Count -eq 0) 'Capability list is missing a tools operation.'
    Assert-Board ($operations.Count -eq $expected.Count -and @($expected | Where-Object { $_ -notin $operations }).Count -eq 0) 'Capability list does not contain all 36 board operations.'
    Assert-Board ((Get-BoardTestHosts).Count -eq 0) 'Static capability discovery started a ToolBox task host.'

    $registration = (Invoke-BoardCli @('project', 'register', '--path', $projectName, '--cwd', $workspace, '--init', '--json')).data
    Assert-Fields $registration @('boardId', 'name', 'path') 'project.register returned the wrong shape.'
    Assert-Board ([IO.Path]::GetFullPath($registration.path) -eq [IO.Path]::GetFullPath($projectPath)) 'Relative registration did not resolve inside the isolated workspace.'
    $script:boardId = [string]$registration.boardId
    $board = (Invoke-BoardCli @('board', 'get', '--project', $script:boardId, '--json')).data
    Assert-Fields $board @('schemaVersion', 'boardId', 'name', 'revision', 'columns', 'cards', 'commentRevision') 'board.get returned an incomplete v2 board.'
    $states = @($board.columns | ForEach-Object { $_.status } | Where-Object { $_ })
    $requiredStates = @('notStarted', 'inProgress', 'pendingReview', 'closed')
    Assert-Board (@($requiredStates | Where-Object { $_ -notin $states }).Count -eq 0) 'The initialized board does not map all four stable states.'
    $script:revision = [long]$board.revision
    $script:commentRevision = [long]$board.commentRevision
    return $registration
}

function Get-CurrentBoard {
    $board = (Invoke-BoardCli @('board', 'get', '--project', $script:boardId, '--json')).data
    $script:revision = [long]$board.revision
    $script:commentRevision = [long]$board.commentRevision
    return $board
}

function New-BoardCard([string]$Title, [string]$ColumnId, [string]$ParentId = '', [string]$InputPath = '') {
    $arguments = @('board', 'card', 'create', '--project', $script:boardId, '--revision', "$script:revision", '--column-id', $ColumnId, '--json')
    if ($InputPath) { $arguments += @('--input', $InputPath) } else { $arguments += @('--title', $Title) }
    if ($ParentId) { $arguments += @('--parent-id', $ParentId) }
    $data = (Invoke-BoardCli $arguments).data
    Assert-Fields $data @('card', 'revision', 'boardRevision', 'commentRevision') 'board.card.create returned an incomplete v2 response.'
    $script:revision = [long]$data.revision
    $script:commentRevision = [long]$data.commentRevision
    return $data.card
}

function Invoke-BoardWrite([string[]]$Arguments) {
    $response = Invoke-BoardCli $Arguments
    if ($response.data.PSObject.Properties.Name -contains 'revision') { $script:revision = [long]$response.data.revision }
    if ($response.data.PSObject.Properties.Name -contains 'commentRevision') { $script:commentRevision = [long]$response.data.commentRevision }
    return $response.data
}

function Assert-BoardError([string[]]$Arguments, [string[]]$Codes) {
    $exitCodes = @(2, 3, 4, 5, 6, 7)
    $response = Invoke-BoardCli $Arguments $exitCodes $Codes
    Assert-Board ($response.ok -eq $false) "Expected an error response for: $($Arguments -join ' ')"
    return $response
}

function Run-StatePhase {
    $board = Get-CurrentBoard
    $columns = @{}; foreach ($column in $board.columns) { if ($column.status) { $columns[$column.status] = [string]$column.id } }

    $rootCard = New-BoardCard '状态树根' $columns.notStarted
    Assert-Board ($rootCard.status -eq 'notStarted' -and $null -eq $rootCard.parentId) 'A new root card did not start in notStarted.'
    $child = New-BoardCard '状态树子项' $columns.notStarted ([string]$rootCard.id)
    $grandchild = New-BoardCard '状态树孙项' $columns.notStarted ([string]$child.id)
    $allCards = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId)).data.cards
    $roots = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId, '--roots-only')).data.cards
    $children = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId, '--parent-id', [string]$rootCard.id)).data.cards
    Assert-Board (@($allCards | Where-Object id -in @($rootCard.id, $child.id, $grandchild.id)).Count -eq 3) 'The three-level card tree was not returned by card.list.'
    Assert-Board (@($roots | Where-Object id -in @($rootCard.id, $child.id, $grandchild.id)).Count -eq 1 -and [string]$roots[0].id -eq [string]$rootCard.id) '--roots-only returned child cards.'
    Assert-Board (@($children).Count -eq 1 -and [string]$children[0].id -eq [string]$child.id) '--parent-id did not return only direct children.'
    Assert-Board ([string]$grandchild.parentId -eq [string]$child.id) 'The grandchild parent relationship was lost.'

    $jsonInput = Write-BoardJson 'card-create.json' ([ordered]@{ title = 'JSON 合并卡'; description = 'from JSON'; tags = @('alpha', '两个词') })
    $jsonCard = New-BoardCard '' $columns.notStarted '' $jsonInput
    Assert-Board ($jsonCard.title -eq 'JSON 合并卡' -and $jsonCard.description -eq 'from JSON' -and @($jsonCard.tags).Count -eq 2) 'Card JSON input did not retain title, description, and tags.'
    $updateInput = Write-BoardJson 'card-update.json' ([ordered]@{ description = 'merged description' })
    $updated = Invoke-BoardWrite @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$jsonCard.id, '--input', $updateInput, '--priority', '2', '--revision', "$script:revision", '--json')
    Assert-Board ($updated.card.description -eq 'merged description' -and $updated.card.priority -eq 2) 'Card JSON input did not merge with a non-conflicting flag.'
    $updated = Invoke-BoardWrite @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$jsonCard.id, '--tags', '--revision', "$script:revision", '--json')
    Assert-Board (@($updated.card.tags).Count -eq 0) 'A zero-value --tags option did not clear tags.'
    $updated = Invoke-BoardWrite @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$child.id, '--clear-parent-id', '--revision', "$script:revision", '--json')
    Assert-Board ($null -eq $updated.card.parentId) '--clear-parent-id did not detach the card.'

    $unmapped = Invoke-BoardWrite @('board', 'column', 'create', '--project', $script:boardId, '--title', '无状态列', '--order', '4', '--revision', "$script:revision", '--json')
    $unmappedId = [string]$unmapped.column.id
    Assert-Board ($null -eq $unmapped.column.status) 'A new column without --status received a state mapping.'
    Assert-BoardError @('board', 'card', 'move', '--project', $script:boardId, '--id', [string]$rootCard.id, '--target-column-id', $columns.pendingReview, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    Assert-BoardError @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$rootCard.id, '--column-id', $columns.closed, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    $unchanged = (Invoke-BoardCli @('board', 'card', 'get', '--project', $script:boardId, '--id', [string]$rootCard.id)).data.card
    Assert-Board ($unchanged.status -eq 'notStarted' -and [string]$unchanged.columnId -eq $columns.notStarted) 'A generic column move changed the stable card state.'

    $actorA = 'actor-' + [guid]::NewGuid().ToString('N')
    $actorB = 'actor-' + [guid]::NewGuid().ToString('N')
    $hostA = 'host-' + [guid]::NewGuid().ToString('N')
    $hostB = 'host-' + [guid]::NewGuid().ToString('N')
    $sessionA = 'session-' + [guid]::NewGuid().ToString('N')
    $sessionB = 'session-' + [guid]::NewGuid().ToString('N')
    $claimed = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorA, '--host', $hostA, '--session', $sessionA, '--ttl-minutes', '10', '--revision', "$script:revision", '--json')
    Assert-Board ($claimed.card.status -eq 'inProgress' -and $claimed.card.claim.actorId -eq $actorA -and $claimed.card.claim.hostId -eq $hostA -and $claimed.card.claim.sessionId -eq $sessionA) 'Claim did not preserve the full host/actor/session identity.'
    $leaseBefore = [DateTimeOffset]::Parse([string]$claimed.card.claim.expiresAt)
    $competing = Assert-BoardError @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json') @('CONFLICT')
    Assert-Board ($competing.data.conflictType -eq 'CLAIMED_BY_OTHER') 'A competing claim did not identify the active owner conflict.'
    $sessionConflict = Assert-BoardError @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorA, '--host', $hostA, '--session', ($sessionA + '-other'), '--revision', "$script:revision", '--json') @('CONFLICT')
    Assert-Board ($sessionConflict.data.conflictType -eq 'CLAIMED_BY_OTHER') 'A different session on the same host and actor bypassed the active claim.'
    Assert-BoardError @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$rootCard.id, '--title', 'other update', '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    Assert-BoardError @('board', 'card', 'move', '--project', $script:boardId, '--id', [string]$rootCard.id, '--target-column-id', $unmappedId, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    Assert-BoardError @('board', 'card', 'delete', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    Assert-BoardError @('board', 'card', 'reopen', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    $mineA = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId, '--mine', '--actor', $actorA, '--host', $hostA, '--session', $sessionA)).data.cards
    $mineB = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId, '--mine', '--actor', $actorB, '--host', $hostB, '--session', $sessionB)).data.cards
    Assert-Board (@($mineA | Where-Object id -eq $rootCard.id).Count -eq 1 -and @($mineB | Where-Object id -eq $rootCard.id).Count -eq 0) 'The mine filter did not compare the complete execution identity.'
    $renewed = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorA, '--host', $hostA, '--session', $sessionA, '--ttl-minutes', '15', '--revision', "$script:revision", '--json')
    Assert-Board ([DateTimeOffset]::Parse([string]$renewed.card.claim.expiresAt) -gt $leaseBefore) 'The owner could not renew its active claim.'
    $ownerUpdate = Invoke-BoardWrite @('board', 'card', 'update', '--project', $script:boardId, '--id', [string]$rootCard.id, '--title', '本人修改成功', '--actor', $actorA, '--host', $hostA, '--session', $sessionA, '--revision', "$script:revision", '--json')
    Assert-Board ($ownerUpdate.card.title -eq '本人修改成功') 'The current claim owner could not update the card.'
    $submitted = Invoke-BoardWrite @('board', 'card', 'submit', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorA, '--host', $hostA, '--session', $sessionA, '--revision', "$script:revision", '--json')
    Assert-Board ($submitted.card.status -eq 'pendingReview' -and $null -eq $submitted.card.claim -and [string]$submitted.card.columnId -eq $columns.pendingReview) 'The owner could not submit the card to pendingReview.'
    $closed = Invoke-BoardWrite @('board', 'card', 'close', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', 'local-user', '--host', 'toolbox-qa', '--revision', "$script:revision", '--json')
    Assert-Board ($closed.card.status -eq 'closed' -and [string]$closed.card.columnId -eq $columns.closed) 'Local acceptance could not close a pending-review card.'
    $reopened = Invoke-BoardWrite @('board', 'card', 'reopen', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', 'local-user', '--host', 'toolbox-qa', '--revision', "$script:revision", '--json')
    Assert-Board ($reopened.card.status -eq 'notStarted' -and $null -eq $reopened.card.claim) 'Local acceptance could not reopen a closed card.'
    $reclaimed = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json')
    Assert-Board ($reclaimed.card.status -eq 'inProgress' -and $reclaimed.card.claim.actorId -eq $actorB) 'A reopened card could not be claimed by another execution identity.'
    $released = Invoke-BoardWrite @('board', 'card', 'release', '--project', $script:boardId, '--id', [string]$rootCard.id, '--actor', $actorB, '--host', $hostB, '--session', $sessionB, '--revision', "$script:revision", '--json')
    Assert-Board ($released.card.status -eq 'notStarted' -and $null -eq $released.card.claim) 'The claim owner could not release the card.'

    $workflow = New-BoardCard '父关闭验收' $columns.notStarted
    $workflowChild = New-BoardCard '仍打开的子项' $columns.notStarted ([string]$workflow.id)
    $worker = 'actor-' + [guid]::NewGuid().ToString('N')
    $workerHost = 'host-' + [guid]::NewGuid().ToString('N')
    $workerSession = 'session-' + [guid]::NewGuid().ToString('N')
    $null = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$workflow.id, '--actor', $worker, '--host', $workerHost, '--session', $workerSession, '--revision', "$script:revision", '--json')
    $null = Invoke-BoardWrite @('board', 'card', 'submit', '--project', $script:boardId, '--id', [string]$workflow.id, '--actor', $worker, '--host', $workerHost, '--session', $workerSession, '--revision', "$script:revision", '--json')
    Assert-BoardError @('board', 'card', 'close', '--project', $script:boardId, '--id', [string]$workflow.id, '--actor', 'local-user', '--host', 'toolbox-qa', '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    $childWorker = 'actor-' + [guid]::NewGuid().ToString('N')
    $childHost = 'host-' + [guid]::NewGuid().ToString('N')
    $childSession = 'session-' + [guid]::NewGuid().ToString('N')
    $null = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$workflowChild.id, '--actor', $childWorker, '--host', $childHost, '--session', $childSession, '--revision', "$script:revision", '--json')
    $null = Invoke-BoardWrite @('board', 'card', 'submit', '--project', $script:boardId, '--id', [string]$workflowChild.id, '--actor', $childWorker, '--host', $childHost, '--session', $childSession, '--revision', "$script:revision", '--json')
    $null = Invoke-BoardWrite @('board', 'card', 'close', '--project', $script:boardId, '--id', [string]$workflowChild.id, '--actor', 'local-user', '--host', 'toolbox-qa', '--revision', "$script:revision", '--json')
    $closedParent = Invoke-BoardWrite @('board', 'card', 'close', '--project', $script:boardId, '--id', [string]$workflow.id, '--actor', 'local-user', '--host', 'toolbox-qa', '--revision', "$script:revision", '--json')
    Assert-Board ($closedParent.card.status -eq 'closed') 'A parent could not close after every active descendant was closed.'
    Assert-BoardError @('board', 'card', 'create', '--project', $script:boardId, '--title', 'closed parent child', '--column-id', $columns.notStarted, '--parent-id', [string]$workflow.id, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null

    $deleteRoot = New-BoardCard '子树删除根' $columns.notStarted
    $deleteChild = New-BoardCard '被保护的删除子项' $columns.notStarted ([string]$deleteRoot.id)
    $deleteActor = 'actor-' + [guid]::NewGuid().ToString('N')
    $deleteHost = 'host-' + [guid]::NewGuid().ToString('N')
    $deleteSession = 'session-' + [guid]::NewGuid().ToString('N')
    $null = Invoke-BoardWrite @('board', 'card', 'claim', '--project', $script:boardId, '--id', [string]$deleteChild.id, '--actor', $deleteActor, '--host', $deleteHost, '--session', $deleteSession, '--revision', "$script:revision", '--json')
    Assert-BoardError @('board', 'card', 'delete', '--project', $script:boardId, '--id', [string]$deleteRoot.id, '--revision', "$script:revision", '--json') @('CONFLICT') | Out-Null
    $null = Invoke-BoardWrite @('board', 'card', 'release', '--project', $script:boardId, '--id', [string]$deleteChild.id, '--actor', $deleteActor, '--host', $deleteHost, '--session', $deleteSession, '--revision', "$script:revision", '--json')
    $deletedTree = Invoke-BoardWrite @('board', 'card', 'delete', '--project', $script:boardId, '--id', [string]$deleteRoot.id, '--revision', "$script:revision", '--json')
    Assert-Board ($deletedTree.affectedCount -eq 2) 'Soft delete did not cover the full card subtree.'
    $deletedCards = (Invoke-BoardCli @('board', 'card', 'list', '--project', $script:boardId, '--include-deleted')).data.cards
    Assert-Board (@($deletedCards | Where-Object id -in @($deleteRoot.id, $deleteChild.id)).Count -eq 2) 'Soft-deleted descendants were not retained in the recycle bin.'
    $restoredTree = Invoke-BoardWrite @('board', 'card', 'restore', '--project', $script:boardId, '--id', [string]$deleteRoot.id, '--revision', "$script:revision", '--json')
    Assert-Board ($restoredTree.affectedCount -eq 2 -and $null -eq $restoredTree.card.deletedAt) 'Restore did not recover the full card subtree.'
    $restoredChild = (Invoke-BoardCli @('board', 'card', 'get', '--project', $script:boardId, '--id', [string]$deleteChild.id)).data.card
    Assert-Board ($null -eq $restoredChild.deletedAt) 'Restoring the root did not restore its child.'
}

function New-BoardComment([string]$CardId, [string]$Body, [string]$Actor, [string]$SourceHost, [string]$Session = '', [string]$ParentId = '') {
    $arguments = @('board', 'comment', 'add', '--project', $script:boardId, '--id', $CardId, '--body', $Body, '--actor', $Actor, '--host', $SourceHost, '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    if ($Session) { $arguments += @('--session', $Session) }
    if ($ParentId) { $arguments += @('--parent-id', $ParentId) }
    $data = (Invoke-BoardCli $arguments).data
    Assert-Fields $data @('comment', 'cardId', 'revision', 'boardRevision', 'commentRevision') 'board.comment.add returned the wrong v2 shape.'
    $script:revision = [long]$data.revision
    $script:commentRevision = [long]$data.commentRevision
    return $data.comment
}

function Run-CommentsPhase {
    $board = Get-CurrentBoard
    $notStarted = [string](@($board.columns | Where-Object status -eq 'notStarted')[0].id)
    $card = New-BoardCard '评论与投递验收卡' $notStarted
    $author = 'comment-author-' + [guid]::NewGuid().ToString('N')
    $authorHost = 'origin-host-' + [guid]::NewGuid().ToString('N')
    $authorSession = 'origin-session-' + [guid]::NewGuid().ToString('N')
    $rootComment = New-BoardComment ([string]$card.id) 'root comment' $author $authorHost $authorSession
    $context = (Invoke-BoardCli @('board', 'comment', 'get', '--project', $script:boardId, '--comment-id', [string]$rootComment.id)).data
    Assert-Fields $context @('registration', 'cardId', 'comment', 'revision', 'boardRevision', 'commentRevision') 'board.comment.get did not return the typed comment context.'
    Assert-Board ([string]$context.cardId -eq [string]$card.id -and [string]$context.comment.id -eq [string]$rootComment.id) 'board.comment.get returned the wrong card or comment.'

    $local = New-BoardComment ([string]$card.id) 'local edit target' 'local-user' 'toolbox'
    $otherEdit = Assert-BoardError @('board', 'comment', 'update', '--project', $script:boardId, '--comment-id', [string]$local.id, '--body', 'unauthorized', '--actor', 'other', '--host', 'other-host', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json') @('ACCESS_DENIED')
    $edited = Invoke-BoardWrite @('board', 'comment', 'update', '--project', $script:boardId, '--comment-id', [string]$local.id, '--body', 'updated by author', '--actor', 'local-user', '--host', 'toolbox', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($edited.comment.body -eq 'updated by author') 'The comment author could not update the comment.'
    $deleted = Invoke-BoardWrite @('board', 'comment', 'delete', '--project', $script:boardId, '--comment-id', [string]$local.id, '--actor', 'local-user', '--host', 'toolbox', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($null -ne $deleted.comment.deletedAt) 'local-user could not soft-delete a comment.'
    $includingDeleted = (Invoke-BoardCli @('board', 'comment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--include-deleted')).data
    Assert-Fields $includingDeleted @('comments', 'cursor', 'revision', 'boardRevision', 'commentRevision') 'board.comment.list returned the wrong v2 shape.'
    Assert-Board (@($includingDeleted.comments | Where-Object id -eq $local.id).Count -eq 1) 'Deleted comments were not available through --include-deleted.'

    $bound = Invoke-BoardWrite @('board', 'comment', 'bind', '--project', $script:boardId, '--comment-id', [string]$rootComment.id, '--target-host', 'inbox-host', '--actor', $author, '--host', $authorHost, '--session', $authorSession, '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($bound.comment.origin.host -eq 'inbox-host') 'comment.bind did not update the future reply source.'
    $replyOne = New-BoardComment ([string]$card.id) 'first routed reply' 'reply-one' 'reply-host' '' ([string]$rootComment.id)
    Assert-Board ($replyOne.routing.host -eq 'inbox-host' -and $null -eq $replyOne.routing.sessionId) 'The reply did not capture the bound host-only route.'
    $boundAgain = Invoke-BoardWrite @('board', 'comment', 'bind', '--project', $script:boardId, '--comment-id', [string]$rootComment.id, '--target-host', 'second-inbox-host', '--target-session', 'synthetic-session', '--actor', $author, '--host', $authorHost, '--session', $authorSession, '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($boundAgain.comment.origin.host -eq 'second-inbox-host' -and $boundAgain.comment.origin.sessionId -eq 'synthetic-session') 'comment.bind did not store the optional session.'
    $replyTwo = New-BoardComment ([string]$card.id) 'second routed reply' 'reply-two' 'reply-host' '' ([string]$rootComment.id)
    Assert-Board ($replyOne.routing.host -eq 'inbox-host' -and $replyTwo.routing.host -eq 'second-inbox-host' -and $replyTwo.routing.sessionId -eq 'synthetic-session') 'Changing a source binding redirected an existing reply or failed to route a new reply.'

    $inboxActor = [guid]::NewGuid().ToString('D')
    $otherInboxActor = [guid]::NewGuid().ToString('D')
    $inbox = (Invoke-BoardCli @('board', 'comment', 'inbox', '--project', $script:boardId, '--host', 'inbox-host', '--actor', $inboxActor, '--limit', '10', '--json')).data
    Assert-Fields $inbox @('comments', 'cursor', 'revision', 'boardRevision', 'commentRevision') 'board.comment.inbox returned the wrong v2 shape.'
    $script:revision = [long]$inbox.revision
    $script:commentRevision = [long]$inbox.commentRevision
    $reserved = @($inbox.comments | Where-Object id -eq $replyOne.id | Select-Object -First 1)
    Assert-Board ($reserved.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace([string]$reserved[0].inboxClaim.claimId)) 'The routed reply was not reserved in the host inbox.'
    $claimId = [string]$reserved[0].inboxClaim.claimId
    Assert-Board ($reserved[0].inboxClaim.actorId -eq $inboxActor -and $null -ne $reserved[0].inboxClaim.expiresAt) 'The inbox reply does not carry its independent lease identity.'
    $secondInbox = (Invoke-BoardCli @('board', 'comment', 'inbox', '--project', $script:boardId, '--host', 'inbox-host', '--actor', $otherInboxActor, '--limit', '10', '--json')).data
    $script:revision = [long]$secondInbox.revision
    $script:commentRevision = [long]$secondInbox.commentRevision
    Assert-Board (@($secondInbox.comments | Where-Object id -eq $replyOne.id).Count -eq 0) 'A second actor received a reply already held by another inbox lease.'
    Assert-BoardError @('board', 'comment', 'ack', '--project', $script:boardId, '--comment-id', [string]$replyOne.id, '--claim-id', $claimId, '--actor', $otherInboxActor, '--host', 'inbox-host', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json') @('CONFLICT') | Out-Null
    $ack = Invoke-BoardWrite @('board', 'comment', 'ack', '--project', $script:boardId, '--comment-id', [string]$replyOne.id, '--claim-id', $claimId, '--actor', $inboxActor, '--host', 'inbox-host', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($null -ne $ack.comment.acknowledgedAt -and -not [string]::IsNullOrWhiteSpace([string]$ack.comment.acknowledgedBy)) 'The current inbox lease could not acknowledge its reply.'
    $afterAck = (Invoke-BoardCli @('board', 'comment', 'inbox', '--project', $script:boardId, '--host', 'inbox-host', '--actor', $otherInboxActor, '--json')).data
    Assert-Board (@($afterAck.comments | Where-Object id -eq $replyOne.id).Count -eq 0) 'An acknowledged reply was offered again.'

    $pageOne = (Invoke-BoardCli @('board', 'comment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--limit', '2', '--json')).data
    Assert-Board (-not [string]::IsNullOrWhiteSpace([string]$pageOne.cursor)) 'The first comment page did not return a continuation cursor.'
    $cursorRevision = [long]$pageOne.commentRevision
    $pageTwo = (Invoke-BoardCli @('board', 'comment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--limit', '2', '--cursor', [string]$pageOne.cursor, '--comment-revision', "$cursorRevision", '--json')).data
    Assert-Board (@($pageOne.comments).Count -gt 0 -and @($pageTwo.comments).Count -gt 0 -and [string]$pageOne.comments[0].id -ne [string]$pageTwo.comments[0].id) 'Comment pagination did not advance to a distinct page.'
    $staleCursor = [string]$pageOne.cursor
    $null = New-BoardComment ([string]$card.id) 'pagination invalidation' 'page-writer' 'page-host'
    Assert-BoardError @('board', 'comment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--limit', '2', '--cursor', $staleCursor, '--comment-revision', "$cursorRevision", '--json') @('CONFLICT') | Out-Null

    $unrouted = New-BoardComment ([string]$card.id) 'delivery safety check' 'pull-author' 'pull-host'
    $noRouteResponse = Invoke-BoardCli @('board', 'comment', 'delivery', '--project', $script:boardId, '--comment-id', [string]$unrouted.id, '--json')
    $noRoute = $noRouteResponse.data
    Assert-Fields $noRoute @('delivery', 'revision', 'boardRevision', 'commentRevision') 'Unrouted board.comment.delivery returned the wrong v2 shape.'
    if ($null -eq $noRoute.delivery) {
        Assert-Board ([string]($noRouteResponse.warnings -join "`n") -match 'manualPullRequired') 'An unrouted comment without a delivery record did not explain manual pull.'
    } else {
        Assert-Board (@('manualPullRequired', 'needsConfirmation') -contains $noRoute.delivery.status) 'Unrouted delivery entered a sendable state.'
    }
    $confirmState = (Invoke-BoardCli @('board', 'comment', 'delivery', '--project', $script:boardId, '--comment-id', [string]$replyTwo.id, '--json')).data
    Assert-Fields $confirmState @('delivery', 'revision', 'boardRevision', 'commentRevision') 'board.comment.delivery returned the wrong v2 shape.'
    Assert-Board (@('manualPullRequired', 'needsConfirmation') -contains $confirmState.delivery.status) 'A routed comment was sent or entered a sendable state without confirmation.'

    $externalCard = New-BoardCard '评论外置验收卡' $notStarted
    $body = '界' * 5400
    Assert-Board ([Text.Encoding]::UTF8.GetByteCount($body) -le 16KB) 'The externalization body exceeds the CLI comment limit.'
    $externalCount = 0
    while (!(Test-Path -LiteralPath (Join-Path $projectPath '.toolbox/comments.json')) -and $externalCount -lt 160) {
        try { $external = New-BoardComment ([string]$externalCard.id) $body 'external-writer' 'external-host' }
        catch { throw "Sidecar externalization write $($externalCount + 1) failed: $($_.Exception.Message)" }
        $externalCount++
    }
    $commentsPath = Join-Path $projectPath '.toolbox/comments.json'
    Assert-Board (Test-Path -LiteralPath $commentsPath) "Comments did not externalize after $externalCount CLI writes."
    $board = Get-CurrentBoard
    Assert-Board ($board.commentsExternal -eq $true) 'board.get did not report that comments are external.'
    $externalList = (Invoke-BoardCli @('board', 'comment', 'list', '--project', $script:boardId, '--id', [string]$externalCard.id, '--limit', '1', '--json')).data
    Assert-Board (@($externalList.comments).Count -eq 1) 'CLI comment reads failed after externalization.'
    $externalComment = $externalList.comments[0]
    $externalUpdate = Invoke-BoardWrite @('board', 'comment', 'update', '--project', $script:boardId, '--comment-id', [string]$externalComment.id, '--body', 'external sidecar write verified', '--actor', 'external-writer', '--host', 'external-host', '--revision', "$script:revision", '--comment-revision', "$script:commentRevision", '--json')
    Assert-Board ($externalUpdate.comment.body -eq 'external sidecar write verified') 'CLI comment writes failed after externalization.'
}

function Write-Png([string]$Path, [string]$Bytes) {
    [IO.File]::WriteAllBytes($Path, [Convert]::FromBase64String($Bytes))
}

function Run-AttachmentsPhase {
    $board = Get-CurrentBoard
    $notStarted = [string](@($board.columns | Where-Object status -eq 'notStarted')[0].id)
    $card = New-BoardCard '图片附件验收卡' $notStarted
    $pngA = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC'
    $pngB = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8DwHwAFgAI/ScL+WQAAAABJRU5ErkJggg=='
    $source = Join-Path $taskRoot 'one-pixel.png'
    Write-Png $source $pngA
    $added = Invoke-BoardWrite @('board', 'attachment', 'add', '--project', $script:boardId, '--id', [string]$card.id, '--file', $source, '--actor', 'image-author', '--host', 'test-host', '--revision', "$script:revision", '--json')
    Assert-Fields $added @('attachment', 'revision', 'boardRevision', 'commentRevision') 'board.attachment.add returned the wrong v2 shape.'
    $attachment = $added.attachment
    Assert-Board ($attachment.id -and $attachment.sha256 -and $attachment.relativePath -and $attachment.size -gt 0) 'The added image metadata is incomplete.'
    $listed = (Invoke-BoardCli @('board', 'attachment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--json')).data
    Assert-Fields $listed @('attachments', 'revision', 'boardRevision', 'commentRevision') 'board.attachment.list returned the wrong v2 shape.'
    Assert-Board (@($listed.attachments | Where-Object id -eq $attachment.id).Count -eq 1) 'The new image was not listed.'
    $storedPath = [IO.Path]::GetFullPath((Join-Path $projectPath ([string]$attachment.relativePath)))
    $assetsRoot = [IO.Path]::GetFullPath((Join-Path $projectPath '.toolbox')) + [IO.Path]::DirectorySeparatorChar
    Assert-Board ($storedPath.StartsWith($assetsRoot, [StringComparison]::OrdinalIgnoreCase)) 'Stored image path escaped the project .toolbox directory.'
    Assert-Board ([IO.File]::Exists($storedPath)) 'The listed image file is missing.'

    [IO.File]::Delete($storedPath)
    $missing = (Invoke-BoardCli @('board', 'attachment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--json')).data.attachments | Where-Object id -eq $attachment.id | Select-Object -First 1
    Assert-Board ($missing.exists -eq $false) 'A missing image was not marked as missing.'
    Assert-Board (@('false', '') -contains ([string]$missing.hashMatch)) 'A missing image returned an unexpected hash-match marker.'

    Write-Png $source $pngB
    $tamperSource = Join-Path $taskRoot 'different-pixel.png'
    Write-Png $tamperSource $pngA
    $tampered = Invoke-BoardWrite @('board', 'attachment', 'add', '--project', $script:boardId, '--id', [string]$card.id, '--file', $tamperSource, '--actor', 'image-author', '--host', 'test-host', '--revision', "$script:revision", '--json')
    $tamperedPath = [IO.Path]::GetFullPath((Join-Path $projectPath ([string]$tampered.attachment.relativePath)))
    Write-Png $tamperedPath $pngB
    $mismatch = (Invoke-BoardCli @('board', 'attachment', 'list', '--project', $script:boardId, '--id', [string]$card.id, '--json')).data.attachments | Where-Object id -eq $tampered.attachment.id | Select-Object -First 1
    Assert-Board ($mismatch.exists -eq $true -and $mismatch.hashMatch -eq $false) 'A changed image was not reported as a hash mismatch.'
    $removed = Invoke-BoardWrite @('board', 'attachment', 'remove', '--project', $script:boardId, '--attachment-id', [string]$tampered.attachment.id, '--actor', 'image-author', '--host', 'test-host', '--revision', "$script:revision", '--json')
    Assert-Board ($removed.attachment.id -eq $tampered.attachment.id) 'The changed image could not be removed by its attachment ID.'
    Assert-Board (!(Test-Path -LiteralPath $tamperedPath)) 'Removing an attachment left its stored image behind.'

    $oversized = Join-Path $taskRoot 'oversized-image.png'
    $pngBytes = [Convert]::FromBase64String($pngA)
    $oversizedBytes = [byte[]]::new((10 * 1024 * 1024) + 1)
    [Array]::Copy($pngBytes, $oversizedBytes, $pngBytes.Length)
    [IO.File]::WriteAllBytes($oversized, $oversizedBytes)
    $sizeFailure = Invoke-BoardCli @('board', 'attachment', 'add', '--project', $script:boardId, '--id', [string]$card.id, '--file', $oversized, '--actor', 'image-author', '--host', 'test-host', '--revision', "$script:revision", '--json') @(2, 4, 6, 7) @('INVALID_ARGUMENT', 'ATTACHMENT_TOO_LARGE', 'FILE_TOO_LARGE')
    Assert-Board ($sizeFailure.ok -eq $false) 'The 10 MiB image limit did not reject an oversized input.'
}

try {
    Initialize-BoardProject | Out-Null
    $phases = if ($Phase -eq 'all') { @('state', 'comments', 'attachments') } else { @($Phase) }
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($currentPhase in $phases) {
        try {
            switch ($currentPhase) {
                'state' { Run-StatePhase }
                'comments' { Run-CommentsPhase }
                'attachments' { Run-AttachmentsPhase }
            }
            Write-Output "PASS: $currentPhase phase."
        }
        catch {
            $failures.Add("${currentPhase}: $($_.Exception.Message)")
            Write-Output "FAIL: $currentPhase phase after $script:checks checks: $($_.Exception.Message)"
        }
    }
    $null = Invoke-BoardCli @('host', 'stop')
    Assert-Board ((Get-BoardTestHosts).Count -eq 0) 'The isolated background host did not stop.'
    if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
    Write-Output "PASS: $script:checks board v2 $Phase CLI checks. Isolated evidence: $root"
}
finally {
    foreach ($entry in (Get-BoardTestHosts)) {
        Stop-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
    }
    $env:TOOLBOX_TASK_TEST_ROOT = $previousTaskRoot
}
