<#
.SYNOPSIS
  PowerShell tab completion for wbrew (A18), built on System.CommandLine's
  completion support: the CLI answers the `[suggest:<position>]` directive
  with one candidate per line.

.DESCRIPTION
  Dot-source this file from your PowerShell profile:
      . "$env:LOCALAPPDATA\WinBrew\app\<version>\wbrew-completion.ps1"
  or copy it anywhere and dot-source it. It registers a native argument
  completer for `wbrew` (and `wbrew.exe`). Completion never changes state:
  suggestions come from the command tree only (no network, no lock).
#>

$wbrewCompleter = {
    param($wordToComplete, $commandAst, $cursorPosition)

    $line = $commandAst.ToString()
    # The directive needs the cursor position inside the command line text.
    $position = [Math]::Min($cursorPosition - $commandAst.Extent.StartOffset, $line.Length)
    $suggestions = & wbrew "[suggest:$position]" "$line" 2>$null
    foreach ($suggestion in $suggestions) {
        if ($suggestion -like "$wordToComplete*") {
            [System.Management.Automation.CompletionResult]::new($suggestion, $suggestion, 'ParameterValue', $suggestion)
        }
    }
}

Register-ArgumentCompleter -Native -CommandName 'wbrew', 'wbrew.exe' -ScriptBlock $wbrewCompleter
