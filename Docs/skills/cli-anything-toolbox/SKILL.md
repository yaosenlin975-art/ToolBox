---
name: cli-anything-toolbox
description: Use for requests to manage ToolBox desktop app todos or folder-based project boards, OCR an explicitly named local image, or search existing screenshot tags and metadata. Do not use for Multica issues or Codex chat tasks.
---

# ToolBox CLI

Use the existing Windows `toolbox-cli.exe` to access ToolBox's local task host. This skill documents the shipped CLI; it does not grant new permissions or add commands. Use it only for the user's requested ToolBox action. Never create sample todos just to discover the CLI.

## Requirements and executable

ToolBox is a WPF app targeting `net8.0-windows10.0.19041.0`. A framework-dependent publish requires the .NET 8 Desktop Runtime. Keep the complete matching publish directory: `ToolBox.exe` and `toolbox-cli.exe` must be side by side with their runtime/dependency files. Do not copy only the CLI executable.

Find the CLI in this order:

1. Use `TOOLBOX_CLI_PATH` when set.
2. Otherwise resolve `toolbox-cli.exe` with `Get-Command`.
3. Otherwise, on this development machine only, try `D:\Workspaces\ToolBox\Project\bin\agent-cli-publish\toolbox-cli.exe`. This absolute fallback exists on the authoring machine; replace it with the actual full publish path on other machines.
4. If none resolves to a file beside `ToolBox.exe`, explain that the complete publish package is required.

```powershell
$toolboxCli = $env:TOOLBOX_CLI_PATH
if (-not $toolboxCli) {
    $cmd = Get-Command toolbox-cli.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($cmd) { $toolboxCli = $cmd.Source }
}
if (-not $toolboxCli) {
    $candidate = 'D:\Workspaces\ToolBox\Project\bin\agent-cli-publish\toolbox-cli.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $toolboxCli = $candidate }
}
if (-not $toolboxCli -or -not (Test-Path -LiteralPath $toolboxCli -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $toolboxCli) 'ToolBox.exe') -PathType Leaf)) {
    throw 'Use the full ToolBox publish directory containing both executables.'
}
$toolboxCli = (Resolve-Path -LiteralPath $toolboxCli).Path
```

The CLI and host communicate through the current Windows user's local pipe. The CLI may start the host in the background without showing a window; a normal ToolBox launch reuses that instance and shows its UI. Background mode does not start screenshot capture, clipboard monitoring, or global hotkeys. `host stop` is refused while the UI is open; close ToolBox from its exit menu instead.

## Discover before invoking

At the start of a workflow, read `capabilities list`, then `capabilities get <exact-name>` for the capability whose inputs you need. Reuse that metadata within the workflow while the CLI version is unchanged. These return static JSON metadata without starting the host or reading user data. Treat `inputSchema`, `command`, `outputFields`, `effects`, and `availability` as the source of truth. `command` gives a valid invocation form; use `--help` for the complete flag list. `runtimeChecked: false` means engine/runtime conditions are checked only when the capability runs. The CLI returns JSON by default. New `project` and `board` commands also accept `--json`; do not add it to existing `task`, `ocr`, `screenshot`, or `capabilities` commands. `--help` and `--version` are text exceptions and do not start the app.

```powershell
$stdout = & $toolboxCli capabilities list
$exitCode = $LASTEXITCODE
$allCapabilities = ($stdout -join "`n") | ConvertFrom-Json
if ($exitCode -ne 0 -or -not $allCapabilities.ok) { throw 'Could not read ToolBox capabilities.' }

$stdout = & $toolboxCli capabilities get task.create
$exitCode = $LASTEXITCODE
$capability = ($stdout -join "`n") | ConvertFrom-Json
if ($exitCode -ne 0 -or -not $capability.ok) { throw 'Could not read task.create capability.' }
$capability.data.inputSchema
```

The original command set is:

| Operation | CLI syntax | Effect |
|---|---|---|
| `capabilities.list` | `capabilities list` | Static allow-list |
| `capabilities.get` | `capabilities get <name>` | One exact capability's metadata |
| `task.create` | `task create --input <file|->` | Create one task |
| `task.list` | `task list [--status <value>]` | List tasks; default excludes recycle-bin items |
| `task.get` | `task get <id>` | Read one exact ID, including trashed tasks |
| `task.update` | `task update <id> --input <file|->` | Update supplied fields |
| `task.complete` | `task complete <id>` | Complete one task |
| `task.reopen` | `task reopen <id>` | Reopen one task |
| `task.delete` | `task delete <id>` | Soft-delete the task and its subtree |
| `task.restore` | `task restore <id>` | Restore the task and its trashed subtree |
| `host.stop` | `host stop` | Stop the background host if UI is closed |
| `ocr.file` | `ocr file --input <file|->` | OCR one specified local image |
| `screenshot.search` | `screenshot search --input <file|->` | Search existing screenshot metadata |

## PowerShell JSON I/O

Set UTF-8 for native stdin/stdout. Write JSON files as UTF-8 without a BOM. Check `$LASTEXITCODE`, parse stdout with `ConvertFrom-Json`, and inspect `ok` before using `data`.

```powershell
$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

function Invoke-ToolBoxCli([string]$Exe, [string[]]$CliArgs) {
    $stdout = & $Exe @CliArgs
    $exitCode = $LASTEXITCODE
    $response = ($stdout -join "`n") | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $response.ok) {
        throw "ToolBox CLI failed (exit $exitCode): $($response.error.code) $($response.error.message)"
    }
    return $response
}
```

For file input, create a UTF-8 JSON file and pass its path:

```powershell
$createPath = Join-Path $env:TEMP ('toolbox-create-' + [guid]::NewGuid().ToString('N') + '.json')
[System.IO.File]::WriteAllText($createPath, '{"title":"按用户要求填写"}', $utf8)
$created = Invoke-ToolBoxCli -Exe $toolboxCli -CliArgs @('task', 'create', '--input', $createPath)
```

For stdin, provide the user's actual image path; do not guess a file or search for one:

```powershell
$imagePath = 'C:\path\to\user-selected.png' # replace with the user's explicitly named absolute path
$payload = @{ path = $imagePath; language = 'eng' } | ConvertTo-Json -Compress
$stdout = $payload | & $toolboxCli ocr file --input -
$exitCode = $LASTEXITCODE
$response = ($stdout -join "`n") | ConvertFrom-Json
if ($exitCode -ne 0 -or -not $response.ok) {
    throw "ToolBox CLI failed (exit $exitCode): $($response.error.code) $($response.error.message)"
}
```

## Task behavior

For a requested create/change workflow, create only when requested, capture the exact returned 12-hex-character `data.id`, then use it as the positional ID for update/complete/get. `task.update` takes the entire input object as the update body; `id` belongs in the command line and must not appear in that JSON. Priority is `0` unset, `1` medium, `2` high. Omitted fields stay unchanged; `description: ""` and `tags: []` clear those values; `dueDate: null` clears the deadline. Dates require an ISO 8601 timezone. Do not infer dates from a title. Parent progress is calculated from children and cannot be set directly. A parent cannot complete before its active children; reopening keeps progress. Restore trashed ancestors before restoring a child.

Example workflow, only when the user asked for these changes:

```powershell
# $created came from the requested task.create above.
$id = [string]$created.data.id
if ($id -notmatch '^[A-Fa-f0-9]{12}$') { throw 'CLI returned an invalid task ID.' }

$updatePath = Join-Path $env:TEMP ('toolbox-update-' + [guid]::NewGuid().ToString('N') + '.json')
[System.IO.File]::WriteAllText($updatePath, '{"description":"已按请求更新"}', $utf8)
$updated = Invoke-ToolBoxCli -Exe $toolboxCli -CliArgs @('task', 'update', $id, '--input', $updatePath)
$completed = Invoke-ToolBoxCli -Exe $toolboxCli -CliArgs @('task', 'complete', $id)
$verified = Invoke-ToolBoxCli -Exe $toolboxCli -CliArgs @('task', 'get', $id)
```

`task.delete` is a soft delete of the entire subtree, not permanent deletion. `task.restore` restores the task and its trashed subtree; restore ancestors first. Repeating configuration is readable but not writable through this CLI. Moving tasks and permanent deletion are unsupported.

## OCR and screenshot search

`ocr.file` accepts only one explicitly specified absolute local image path; it does not download, translate, change clipboard, or write history. Limits are 16 MiB, 16,384 pixels per edge, and 32 million pixels total. Default language is `chi_sim+eng`. OCR uses local Tesseract or Windows OCR; missing language data is not downloaded. Windows OCR may fall back to installed Windows languages and may not match every requested language combination. Windows OCR confidence is `-1`.

`screenshot.search` searches tags/rating/color metadata only. It does not OCR-search text or return the original image. Each item exposes a 17-digit `cacheKey`, tags, rating, notes, and tagged time, not the internal image path. Pass `nextCursor` as the next page's cursor and keep the same filters; `null` means no next page. Results use ordinal key order and do not provide a snapshot if data changes between pages.

## Project folder boards

Use `project` and `board` for project cards; use `task` for personal todos. Read the requested operation with `capabilities get <exact-name>` before calling it.

| Operation | CLI syntax |
|---|---|
| `project.register` | `project register --path <folder> [--init] [--replace-path]` |
| `project.list` | `project list` |
| `project.get` / `project.unregister` | `project get|unregister [--project <board-id-or-path>]` |
| `board.get` | `board get [--project <board-id-or-path>]` |
| `board.column.list` | `board column list` |
| `board.column.create` | `board column create --title <title> [--order <index>]` |
| `board.column.update` | `board column update --id <uuid> [--title <title>] [--order <index>]` |
| `board.column.move` | `board column move --id <uuid> --order <index>` |
| `board.column.delete` | `board column delete --id <uuid> [--target-column-id <uuid>]` |
| `board.card.list` | `board card list [--column-id <uuid>] [--include-deleted]` |
| `board.card.get` | `board card get --id <uuid>` |
| `board.card.create` | `board card create --title <title> [--column-id <uuid>]` |
| `board.card.update` | `board card update --id <uuid> [update fields]` |
| `board.card.move` | `board card move --id <uuid> --target-column-id <uuid> [--order <index>]` |
| `board.card.delete` / `board.card.restore` | `board card delete|restore --id <uuid>` |
| `board.card.add-to-todo` | `board card add-to-todo --id <uuid>` |

Board commands accept `--project <board-id-or-path>`; otherwise they resolve the nearest registered project containing `--cwd <folder>` or the CLI working directory. Use an explicit project when the agent's current directory is not the intended project. `project register` resolves relative paths on the CLI side.

New commands accept `--input <json-file|->` and `--json`. JSON fields and command flags can be combined only when field names do not overlap. Board mutations accept `--revision <number>`; read `board get` and send its revision for edits based on that snapshot. A CONFLICT requires re-reading the board before deciding the next edit.

Card create/update fields: `title`, `description`, `priority` (0–2), `tags` (string array), `dueDate` (ISO date or null), `columnId`, `order`. Flags use `--due-date` or `--clear-due-date`; `--tags` accepts multiple values and no values clears tags. IDs are stable UUIDs. Card list/get results and mutations include the board revision. Boards must fit the existing 4 MiB protocol response limit; oversized writes fail before persistence.

Initialize `.toolbox/board.json` only when the user requests a new project board. Register an existing board without `--init`. Do not initialize sample projects for discovery. `--replace-path` changes a duplicate board ID's local registration only when the user intends to move/rebind it.

`project unregister` removes local registration only. `board card delete` soft-deletes a card; `restore` recovers it. Deleting a populated column requires a target column and moves all cards, including deleted cards. The final column cannot be deleted.

`add-to-todo` copies a snapshot into personal todos and returns `todoId`; status changes are independent. Repeated successful calls return the local association. If Todo creation succeeded but association persistence failed, do not replay: inspect the returned Todo ID and the reported pending/conflict state.

The host caches only its most recent 256 request IDs. After a timeout, cache eviction, or host restart, query the result before deciding any new write. Stop board writes before Git checkout or direct external editing of board.json, then reload; external programs do not share the host write transaction.

All mutations go through the existing local host. Do not edit `.toolbox/board.json` or the local project registry directly, scan source files, run project scripts, or register unrelated folders.

## Errors, retries, and boundaries

Business commands return one JSON object on stdout: success has `ok: true` and `data`; failure has `ok: false` and `error.code/message` (sometimes `field`). Diagnostics go to stderr. Exit codes: `0` success, `2` invalid command/input, `3` missing task/capability/file, `4` conflict/oversized result/OCR engine unavailable, `5` host unavailable/timeout/access denied, `6` I/O/data corruption/persistence failure, `7` protocol/internal error.

Startup waits up to 10 seconds; a request waits up to 30 seconds. A JSON request or response is limited to 4 MiB. If a write times out or disconnects after sending, first query for its result; never automatically replay an unknown write or retry `create`, which could duplicate a task. The CLI does not replay write requests.

Use the CLI for task, registered project-board, and screenshot metadata access. Do not edit ToolBox JSON stores or file-access allow-lists, bypass the host/pipe, elevate privileges, or expand file, screen, clipboard, or network access beyond the user's request. Respect current-user boundaries. This skill does not authorize broader access or repair actions.
