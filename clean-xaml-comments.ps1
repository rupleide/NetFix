param([string]$Path = ".")

$files = Get-ChildItem -Recurse -Filter "*.xaml" -LiteralPath $Path |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' -and $_.Name -ne 'App.xaml' }

$totalBlocks = 0

foreach ($file in $files) {
    try {
        $text = [System.IO.File]::ReadAllText($file.FullName)
    } catch {
        Write-Host "  SKIP $($file.Name)"
        continue
    }

    $original = $text

    # Count matches before replacement
    $matches = [System.Text.RegularExpressions.Regex]::Matches($text, '<!--')
    $count = $matches.Count

    if ($count -eq 0) { continue }

    # Remove multi-line XAML comments: <!-- ... -->
    $cleaned = [System.Text.RegularExpressions.Regex]::Replace($text, '<!--.*?-->', '')

    # Remove empty comment-only lines (comment starts and ends on same line)
    $cleaned = [System.Text.RegularExpressions.Regex]::Replace($cleaned, '(?m)^\s*<!--[\s\S]*?-->\s*`r?`n', '')

    # Collapse blank lines
    $lines = $cleaned -split "`r`n|`n"
    $compacted = [System.Collections.Generic.List[string]]::new()
    $prevBlank = $false
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) {
            if (-not $prevBlank) {
                $compacted.Add($line)
                $prevBlank = $true
            }
        } else {
            $compacted.Add($line)
            $prevBlank = $false
        }
    }

    $result = $compacted -join "`r`n"
    $result = $result.TrimEnd() + "`r`n"

    if ($result -ne $original) {
        try {
            $utf8 = New-Object System.Text.UTF8Encoding $false
            [System.IO.File]::WriteAllText($file.FullName, $result, $utf8)
            $removed = $count
            $totalBlocks += $removed
            Write-Host "  cleaned: $($file.Name) ($removed blocks)"
        } catch {
            Write-Host "  FAIL $($file.Name): $_"
        }
    }
}

Write-Host "`nDone. Removed $totalBlocks comment blocks."
