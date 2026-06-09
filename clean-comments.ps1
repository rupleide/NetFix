param([string]$Path = ".")

$files = Get-ChildItem -Recurse -Filter "*.cs" -LiteralPath $Path |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

$removedLines = 0

foreach ($file in $files) {
    try {
        $text = [System.IO.File]::ReadAllText($file.FullName)
    } catch {
        Write-Host "  SKIP $($file.Name)"
        continue
    }

    $original = $text
    $lines = $text -split "`r`n|`n"
    $newLines = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        if ($line -match '^\s*//[ \t]') {
            $removedLines++
            continue
        }

        $inStr = $false
        $inChr = $false
        $prev = ''
        $pos = -1

        for ($i = 0; $i -lt $line.Length; $i++) {
            $c = $line[$i]
            if ($c -eq '"' -and -not $inChr -and ($i -eq 0 -or $prev -ne '\')) {
                $inStr = -not $inStr
            } elseif ($c -eq "'" -and -not $inStr -and ($i -eq 0 -or $prev -ne '\')) {
                $inChr = -not $inChr
            } elseif (-not $inStr -and -not $inChr) {
                if ($i -le $line.Length - 4) {
                    $s = $line.Substring($i, 4)
                    if (($s[0] -eq ' ' -or $s[0] -eq "`t") -and $s[1] -eq '/' -and $s[2] -eq '/' -and $s[3] -eq ' ') {
                        $pos = $i
                        break
                    }
                }
            }
            $prev = $c
        }

        if ($pos -ge 0) {
            $before = $line.Substring(0, $pos).TrimEnd()
            if ($before.Length -gt 0) {
                $null = $newLines.Add($before)
            }
            $removedLines++
        } else {
            $null = $newLines.Add($line)
        }
    }

    $result = $newLines -join "`r`n"
    $result = $result -replace '[ \t]+\r?\n', "`r`n"
    $result = $result.TrimEnd() + "`r`n"

    if ($result -ne $original) {
        $diff = $lines.Count - $newLines.Count
        try {
            $utf8 = New-Object System.Text.UTF8Encoding $false
            [System.IO.File]::WriteAllText($file.FullName, $result, $utf8)
            Write-Host "  cleaned: $($file.Name) ($diff lines)"
        } catch {
            Write-Host "  FAIL $($file.Name)"
        }
    }
}

Write-Host "`nDone. Removed $removedLines comment lines."
