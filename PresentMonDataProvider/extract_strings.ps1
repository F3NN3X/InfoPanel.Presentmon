$path = Join-Path $PSScriptRoot 'PresentMonDataProvider.exe'
$out = Join-Path $PSScriptRoot 'PresentMonDataProvider.strings.txt'
$bytes = [System.IO.File]::ReadAllBytes($path)
$minLen = 4
$sb = [System.Text.StringBuilder]::new()
$list = New-Object System.Collections.Generic.List[string]
foreach ($b in $bytes) {
    if ($b -ge 32 -and $b -le 126) {
        [void]$sb.Append([char]$b)
    }
    else {
        if ($sb.Length -ge $minLen) {
            $list.Add($sb.ToString())
        }
        $sb.Clear()
    }
}
if ($sb.Length -ge $minLen) {
    $list.Add($sb.ToString())
}
[System.IO.File]::WriteAllLines($out, $list)
