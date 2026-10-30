# .claude/hooks/format-on-save.ps1
#
# PostToolUse hook: formats the C# file Claude just edited.
# PowerShell instead of bash so it runs on Windows without jq.

$raw = [Console]::In.ReadToEnd()
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }

$path = $data.tool_input.file_path
if (-not $path -or -not (Test-Path -LiteralPath $path)) { exit 0 }

switch -Regex ($path) {
    '\.cs$' {
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            $dir  = Split-Path -Parent $path
            $leaf = Split-Path -Leaf $path
            dotnet format whitespace "$dir" --folder --include "$leaf" *> $null
        }
    }
    default {
        # No automatic formatter configured for this extension
        # (XAML included: none chosen yet).
    }
}

exit 0
