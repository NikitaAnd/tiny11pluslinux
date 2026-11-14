# Linux-style commands for Command Prompt, installed by Tiny11 Builder.
# Each <command>.cmd next to this file runs: powershell -File tiny11linux.ps1 <command> <arguments>
$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$command = [string]$args[0]
$rest = @()
if ($args.Count -gt 1) { $rest = @($args[1..($args.Count - 1)] | ForEach-Object { [string]$_ }) }
$script:code = 0

function Fail([string]$Message, [int]$Code = 1) {
    [Console]::Error.WriteLine("${command}: $Message")
    $script:code = $Code
}

# Splits GNU-style options. Letters in $WithValue take a value (-n 5, -n5); -5 is read as -n 5.
function Split-Options([string[]]$Items, [string[]]$WithValue = @()) {
    $flags = New-Object System.Collections.Hashtable
    $values = New-Object System.Collections.Hashtable
    $operands = New-Object System.Collections.Generic.List[string]
    $end = $false
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $item = [string]$Items[$i]
        if ($end -or $item -eq '-' -or -not $item.StartsWith('-')) { $operands.Add($item); continue }
        if ($item -eq '--') { $end = $true; continue }
        if ($item.StartsWith('--')) { $flags[$item.Substring(2)] = $true; continue }
        $letters = $item.Substring(1)
        if ($letters -match '^\d+$') { $values['n'] = $letters; continue }
        for ($j = 0; $j -lt $letters.Length; $j++) {
            $letter = [string]$letters[$j]
            if ($WithValue -ccontains $letter) {
                $value = $letters.Substring($j + 1)
                if ($value -eq '' -and $i + 1 -lt $Items.Count) { $i++; $value = [string]$Items[$i] }
                $values[$letter] = $value
                break
            }
            $flags[$letter] = $true
        }
    }
    [pscustomobject]@{ Flags = $flags; Values = $values; Operands = [string[]]$operands.ToArray() }
}

function Format-Size([double]$Bytes, [bool]$Human) {
    if (-not $Human) { return ([long]$Bytes).ToString($invariant) }
    $units = 'B', 'K', 'M', 'G', 'T', 'P'
    $index = 0
    while ($Bytes -ge 1024 -and $index -lt $units.Count - 1) { $Bytes /= 1024; $index++ }
    if ($index -eq 0) { return ([long]$Bytes).ToString($invariant) }
    return $Bytes.ToString('0.#', $invariant) + $units[$index]
}

function Write-Columns([string[]]$Names) {
    if ($Names.Count -eq 0) { return }
    if ([Console]::IsOutputRedirected) { $Names; return }
    $width = 80
    try { $width = [Math]::Max(20, [Console]::WindowWidth - 1) } catch { }
    $column = ($Names | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum + 2
    $perRow = [Math]::Max(1, [Math]::Floor($width / $column))
    $rows = [Math]::Ceiling($Names.Count / $perRow)
    for ($row = 0; $row -lt $rows; $row++) {
        $line = ''
        for ($col = 0; $col -lt $perRow; $col++) {
            $index = $col * $rows + $row
            if ($index -lt $Names.Count) { $line += $Names[$index].PadRight($column) }
        }
        $line.TrimEnd()
    }
}

function Read-StandardInput {
    while ($null -ne ($line = [Console]::In.ReadLine())) { $line }
}

function Resolve-Files([string[]]$Paths) {
    foreach ($path in $Paths) {
        if (-not (Test-Path -Path $path)) { Fail "${path}: No such file or directory" 2; continue }
        foreach ($item in @(Get-Item -Path $path -Force)) {
            if ($item.PSIsContainer) { Fail "$($item.Name): Is a directory" 2; continue }
            $item.FullName
        }
    }
}

function Get-Lines([string]$File) {
    if ($File -eq '-') { Read-StandardInput } else { [IO.File]::ReadLines($File) }
}

function Invoke-Ls([string[]]$Items) {
    $o = Split-Options $Items
    $all = [bool]($o.Flags['a'] -or $o.Flags['A'] -or $o.Flags['all'])
    $long = [bool]$o.Flags['l']
    $human = [bool]$o.Flags['h']
    $classify = [bool]$o.Flags['F']
    $single = [bool]$o.Flags['1']
    $directoryOnly = [bool]$o.Flags['d']
    $paths = if ($o.Operands.Count -gt 0) { $o.Operands } else { @('.') }
    $show = {
        param($entries)
        $entries = @($entries | Where-Object { $null -ne $_ -and ($all -or -not $_.Name.StartsWith('.')) } | Sort-Object Name)
        if ($long) {
            foreach ($entry in $entries) {
                $size = if ($entry.PSIsContainer) { '<DIR>' } else { Format-Size $entry.Length $human }
                $time = $entry.LastWriteTime.ToString('yyyy-MM-dd HH:mm', $invariant)
                '{0} {1,10} {2} {3}' -f $entry.Mode, $size, $time, $entry.Name
            }
        }
        else {
            $names = @($entries | ForEach-Object { if ($classify -and $_.PSIsContainer) { $_.Name + '\' } else { $_.Name } })
            if ($single) { $names } else { Write-Columns $names }
        }
    }
    $files = @()
    $directories = @()
    foreach ($path in $paths) {
        if (-not (Test-Path -Path $path)) { Fail "cannot access '$path': No such file or directory" 2; continue }
        foreach ($item in @(Get-Item -Path $path -Force)) {
            if ($item.PSIsContainer -and -not $directoryOnly) { $directories += $item } else { $files += $item }
        }
    }
    if ($files.Count -gt 0) { & $show $files }
    $headers = ($files.Count + $directories.Count) -gt 1
    $first = $files.Count -eq 0
    foreach ($directory in $directories) {
        if ($headers) {
            if (-not $first) { '' }
            "$($directory.FullName):"
        }
        $first = $false
        & $show (Get-ChildItem -LiteralPath $directory.FullName -Force:$all)
    }
}

function Invoke-CopyOrMove([string[]]$Items, [bool]$Move) {
    $o = Split-Options $Items
    $recurse = $o.Flags['r'] -or $o.Flags['R'] -or $o.Flags['recursive'] -or $o.Flags['a']
    if ($o.Operands.Count -lt 2) { Fail 'missing destination file operand'; return }
    $destination = $o.Operands[-1]
    $sources = $o.Operands[0..($o.Operands.Count - 2)]
    if ($sources.Count -gt 1 -and -not (Test-Path -LiteralPath $destination -PathType Container)) {
        Fail "target '$destination' is not a directory"; return
    }
    foreach ($source in $sources) {
        if (-not (Test-Path -Path $source)) { Fail "cannot stat '$source': No such file or directory"; continue }
        if ($Move) { Move-Item -Path $source -Destination $destination -Force; continue }
        if (-not $recurse -and (Test-Path -Path $source -PathType Container)) { Fail "-r not specified; omitting directory '$source'"; continue }
        Copy-Item -Path $source -Destination $destination -Recurse:$recurse -Force
    }
}

function Invoke-Rm([string[]]$Items) {
    $o = Split-Options $Items
    $recurse = $o.Flags['r'] -or $o.Flags['R'] -or $o.Flags['recursive']
    $force = $o.Flags['f'] -or $o.Flags['force']
    if ($o.Operands.Count -eq 0 -and -not $force) { Fail 'missing operand'; return }
    foreach ($path in $o.Operands) {
        if (-not (Test-Path -Path $path)) {
            if (-not $force) { Fail "cannot remove '$path': No such file or directory" }
            continue
        }
        if (-not $recurse -and (Test-Path -Path $path -PathType Container)) { Fail "cannot remove '$path': Is a directory"; continue }
        Remove-Item -Path $path -Recurse:$recurse -Force
    }
}

function Invoke-Touch([string[]]$Items) {
    $o = Split-Options $Items
    if ($o.Operands.Count -eq 0) { Fail 'missing file operand'; return }
    foreach ($path in $o.Operands) {
        if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path -Force).LastWriteTime = Get-Date }
        elseif (-not $o.Flags['c']) { New-Item -ItemType File -Path $path | Out-Null }
    }
}

function Invoke-Ln([string[]]$Items) {
    $o = Split-Options $Items
    if ($o.Operands.Count -ne 2) { Fail 'usage: ln [-s] TARGET LINK_NAME'; return }
    $target = $o.Operands[0]
    $link = $o.Operands[1]
    if ($o.Flags['f'] -and (Test-Path -LiteralPath $link)) { Remove-Item -LiteralPath $link -Force }
    $type = if ($o.Flags['s']) { 'SymbolicLink' } else { 'HardLink' }
    New-Item -ItemType $type -Path $link -Value $target | Out-Null
}

function Invoke-HeadOrTail([string[]]$Items, [bool]$Tail) {
    $o = Split-Options $Items @('n')
    $count = 10
    if ($o.Values['n']) { $count = [int]$o.Values['n'] }
    $files = @($o.Operands)
    if ($files.Count -eq 0) { $files = @('-') }
    $headers = $files.Count -gt 1
    $first = $true
    foreach ($file in $files) {
        $path = $file
        if ($file -ne '-') {
            $path = @(Resolve-Files @($file))
            if ($path.Count -ne 1) { continue }
            $path = $path[0]
        }
        if ($headers) {
            if (-not $first) { '' }
            "==> $(if ($file -eq '-') { 'standard input' } else { $file }) <=="
        }
        $first = $false
        if ($Tail -and $o.Flags['f'] -and $file -ne '-') { Get-Content -LiteralPath $path -Tail $count -Wait; continue }
        if ($Tail) {
            $queue = New-Object System.Collections.Generic.Queue[string]
            foreach ($line in (Get-Lines $path)) {
                $queue.Enqueue($line)
                if ($queue.Count -gt $count) { [void]$queue.Dequeue() }
            }
            $queue.ToArray()
        }
        elseif ($file -eq '-') {
            for ($i = 0; $i -lt $count -and $null -ne ($line = [Console]::In.ReadLine()); $i++) { $line }
        }
        else { Get-Content -LiteralPath $path -TotalCount $count }
    }
}

function Invoke-Grep([string[]]$Items) {
    $o = Split-Options $Items @('e', 'm')
    $operands = @($o.Operands)
    $pattern = $o.Values['e']
    if ($null -eq $pattern) {
        if ($operands.Count -eq 0) { Fail 'usage: grep [-ivnclrwFq] PATTERN [FILE...]' 2; return }
        $pattern = $operands[0]
        $operands = @($operands | Select-Object -Skip 1)
    }
    $recurse = $o.Flags['r'] -or $o.Flags['R']
    if ($o.Flags['F']) { $pattern = [regex]::Escape($pattern) }
    if ($o.Flags['w']) { $pattern = '\b(?:' + $pattern + ')\b' }
    if ($o.Flags['x']) { $pattern = '^(?:' + $pattern + ')$' }
    $options = [Text.RegularExpressions.RegexOptions]::None
    if ($o.Flags['i']) { $options = [Text.RegularExpressions.RegexOptions]::IgnoreCase }
    $regex = New-Object Text.RegularExpressions.Regex($pattern, $options)
    $invert = [bool]$o.Flags['v']
    $limit = if ($o.Values['m']) { [int]$o.Values['m'] } else { [int]::MaxValue }
    if ($operands.Count -eq 0) { $operands = if ($recurse) { @('.') } else { @('-') } }
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($operand in $operands) {
        if ($operand -eq '-') { $files.Add('-'); continue }
        if (-not (Test-Path -Path $operand)) { Fail "${operand}: No such file or directory" 2; continue }
        foreach ($item in @(Get-Item -Path $operand -Force)) {
            if (-not $item.PSIsContainer) { $files.Add($item.FullName); continue }
            if (-not $recurse) { Fail "$($item.Name): Is a directory" 2; continue }
            Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force | ForEach-Object { $files.Add($_.FullName) }
        }
    }
    $names = ($files.Count -gt 1 -or $recurse) -and -not $o.Flags['h']
    if ($o.Flags['H']) { $names = $true }
    $separator = [IO.Path]::DirectorySeparatorChar
    $base = (Get-Location).ProviderPath.TrimEnd($separator) + $separator
    $matched = $false
    foreach ($file in $files) {
        $label = if ($file -eq '-') { '(standard input)' } elseif ($file.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { $file.Substring($base.Length) } else { $file }
        $hits = 0
        $number = 0
        try { $lines = Get-Lines $file } catch { Fail "${label}: $($_.Exception.Message)" 2; continue }
        foreach ($line in $lines) {
            $number++
            if ($regex.IsMatch($line) -eq $invert) { continue }
            $hits++
            $matched = $true
            if ($o.Flags['q']) { exit 0 }
            if ($o.Flags['l']) { break }
            if (-not $o.Flags['c']) {
                $prefix = ''
                if ($names) { $prefix += $label + ':' }
                if ($o.Flags['n']) { $prefix += [string]$number + ':' }
                if ($o.Flags['o'] -and -not $invert) { foreach ($m in $regex.Matches($line)) { $prefix + $m.Value } }
                else { $prefix + $line }
            }
            if ($hits -ge $limit) { break }
        }
        if ($o.Flags['l'] -and $hits -gt 0) { $label }
        elseif ($o.Flags['c']) { if ($names) { "${label}:$hits" } else { [string]$hits } }
    }
    if ($script:code -eq 0 -and -not $matched) { $script:code = 1 }
}

function Invoke-Wc([string[]]$Items) {
    $o = Split-Options $Items
    $lines = $o.Flags['l']; $words = $o.Flags['w']; $bytes = $o.Flags['c'] -or $o.Flags['m']
    if (-not ($lines -or $words -or $bytes)) { $lines = $words = $bytes = $true }
    $files = @($o.Operands)
    if ($files.Count -eq 0) { $files = @('-') }
    $total = @(0, 0, 0)
    $format = {
        param($counts, $name)
        $parts = @()
        if ($lines) { $parts += '{0,7}' -f $counts[0] }
        if ($words) { $parts += '{0,7}' -f $counts[1] }
        if ($bytes) { $parts += '{0,7}' -f $counts[2] }
        ($parts -join ' ') + $(if ($name) { ' ' + $name } else { '' })
    }
    foreach ($file in $files) {
        if ($file -eq '-') {
            $text = [Console]::In.ReadToEnd()
            $size = [Text.Encoding]::UTF8.GetByteCount($text)
            $name = ''
        }
        else {
            $path = @(Resolve-Files @($file))
            if ($path.Count -ne 1) { continue }
            $text = [IO.File]::ReadAllText($path[0])
            $size = (Get-Item -LiteralPath $path[0]).Length
            $name = $file
        }
        $counts = @(($text.Split("`n").Count - 1), @($text -split '\s+' | Where-Object { $_ }).Count, $size)
        for ($i = 0; $i -lt 3; $i++) { $total[$i] += $counts[$i] }
        & $format $counts $name
    }
    if ($files.Count -gt 1) { & $format $total 'total' }
}

function Get-OsInfo { Get-CimInstance -ClassName Win32_OperatingSystem }

function Invoke-Uname([string[]]$Items) {
    $o = Split-Options $Items
    $version = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $machine = switch ($env:PROCESSOR_ARCHITECTURE) { 'AMD64' { 'x86_64' } 'ARM64' { 'aarch64' } 'x86' { 'i686' } default { $env:PROCESSOR_ARCHITECTURE } }
    $values = [ordered]@{
        s = 'Windows_NT'; n = $env:COMPUTERNAME
        r = "10.0.$($version.CurrentBuild).$($version.UBR)"; v = [string]$version.DisplayVersion
        m = $machine; o = 'Windows'
    }
    $selected = @($values.Keys | Where-Object { $o.Flags['a'] -or $o.Flags[$_] })
    if ($selected.Count -eq 0) { $selected = @('s') }
    ($selected | ForEach-Object { $values[$_] }) -join ' '
}

function Invoke-Df([string[]]$Items) {
    $o = Split-Options $Items
    $human = [bool]$o.Flags['h']
    '{0,-12} {1,10} {2,10} {3,10} {4,5} {5}' -f 'Filesystem', $(if ($human) { 'Size' } else { '1K-blocks' }), 'Used', 'Available', 'Use%', 'Mounted on'
    foreach ($disk in @(Get-CimInstance -ClassName Win32_LogicalDisk | Where-Object { $_.Size -gt 0 })) {
        $used = $disk.Size - $disk.FreeSpace
        $scale = if ($human) { 1 } else { 1024 }
        '{0,-12} {1,10} {2,10} {3,10} {4,4}% {5}' -f $(if ($disk.FileSystem) { $disk.FileSystem } else { 'unknown' }),
            (Format-Size ($disk.Size / $scale) $human), (Format-Size ($used / $scale) $human),
            (Format-Size ($disk.FreeSpace / $scale) $human), [Math]::Round(100 * $used / $disk.Size), ($disk.DeviceID + '\')
    }
}

function Invoke-Free([string[]]$Items) {
    $o = Split-Options $Items
    $os = Get-OsInfo
    $human = [bool]$o.Flags['h']
    $divisor = if ($o.Flags['g']) { 1MB } elseif ($o.Flags['m']) { 1KB } else { 1 }
    $show = { param($kib) if ($human) { Format-Size ($kib * 1024) $true } else { [long]($kib / $divisor) } }
    $total = [double]$os.TotalVisibleMemorySize
    $free = [double]$os.FreePhysicalMemory
    $swapTotal = [Math]::Max(0, [double]$os.TotalVirtualMemorySize - $total)
    $swapFree = [Math]::Max(0, [double]$os.FreeVirtualMemory - $free)
    '{0,-6} {1,12} {2,12} {3,12}' -f '', 'total', 'used', 'free'
    '{0,-6} {1,12} {2,12} {3,12}' -f 'Mem:', (& $show $total), (& $show ($total - $free)), (& $show $free)
    '{0,-6} {1,12} {2,12} {3,12}' -f 'Swap:', (& $show $swapTotal), (& $show ([Math]::Max(0, $swapTotal - $swapFree))), (& $show $swapFree)
}

function Invoke-Uptime([string[]]$Items) {
    $o = Split-Options $Items
    $boot = (Get-OsInfo).LastBootUpTime
    $span = (Get-Date) - $boot
    if ($o.Flags['s']) { return $boot.ToString('yyyy-MM-dd HH:mm:ss', $invariant) }
    $text = if ($span.Days -gt 0) { '{0} days, {1:hh\:mm}' -f $span.Days, $span } else { '{0:hh\:mm}' -f $span }
    if ($o.Flags['p']) { return "up $($span.Days) days, $($span.Hours) hours, $($span.Minutes) minutes" }
    '{0} up {1}' -f (Get-Date).ToString('HH:mm:ss', $invariant), $text
}

function Invoke-Ps([string[]]$Items) {
    '{0,7} {1,10} {2,10} {3}' -f 'PID', 'CPU(s)', 'MEM(MB)', 'COMMAND'
    foreach ($process in @(Get-Process | Sort-Object Id)) {
        $cpu = if ($null -ne $process.CPU) { ([double]$process.CPU).ToString('0.00', $invariant) } else { '-' }
        '{0,7} {1,10} {2,10} {3}' -f $process.Id, $cpu, ([double]$process.WorkingSet64 / 1MB).ToString('0.0', $invariant), $process.ProcessName
    }
}

function Invoke-Kill([string[]]$Items, [bool]$ByName) {
    $o = Split-Options $Items @('s')
    if ($o.Operands.Count -eq 0) { Fail $(if ($ByName) { 'usage: killall NAME...' } else { 'usage: kill [-9] PID...' }); return }
    foreach ($target in $o.Operands) {
        try {
            if ($ByName) { Stop-Process -Name ($target -replace '\.exe$', '') -Force }
            else { Stop-Process -Id ([int]$target) -Force }
        }
        catch { Fail "(${target}): $($_.Exception.Message)" }
    }
}

function Invoke-Sleep([string[]]$Items) {
    $milliseconds = 0.0
    foreach ($item in $Items) {
        if ($item -notmatch '^(\d+(?:\.\d+)?)([smhd]?)$') { Fail "invalid time interval '$item'"; return }
        $factor = switch ($Matches[2]) { 'm' { 60000 } 'h' { 3600000 } 'd' { 86400000 } default { 1000 } }
        $milliseconds += [double]::Parse($Matches[1], $invariant) * $factor
    }
    if ($Items.Count -eq 0) { Fail 'missing operand'; return }
    Start-Sleep -Milliseconds ([int][Math]::Min([int]::MaxValue, $milliseconds))
}

# Fallback for Windows builds without the built-in sudo.exe: opens an elevated Command Prompt
# in the current directory that runs the command. The command line arrives in TINY11_SUDO.
function Invoke-Sudo {
    $line = $env:TINY11_SUDO
    if ([string]::IsNullOrWhiteSpace($line)) { Fail 'usage: sudo COMMAND [ARGUMENTS]'; return }
    $directory = (Get-Location).ProviderPath
    $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
    try {
        Start-Process -FilePath $cmd -Verb RunAs -ArgumentList '/s', '/k', ('"cd /d "' + $directory + '" && ' + $line + '"')
    }
    catch { Fail 'elevation was cancelled or is not available' }
}

try {
    switch -CaseSensitive ($command) {
        'ls' { Invoke-Ls $rest }
        'cp' { Invoke-CopyOrMove $rest $false }
        'mv' { Invoke-CopyOrMove $rest $true }
        'rm' { Invoke-Rm $rest }
        'touch' { Invoke-Touch $rest }
        'ln' { Invoke-Ln $rest }
        'head' { Invoke-HeadOrTail $rest $false }
        'tail' { Invoke-HeadOrTail $rest $true }
        'grep' { Invoke-Grep $rest }
        'wc' { Invoke-Wc $rest }
        'uname' { Invoke-Uname $rest }
        'df' { Invoke-Df $rest }
        'free' { Invoke-Free $rest }
        'uptime' { Invoke-Uptime $rest }
        'ps' { Invoke-Ps $rest }
        'kill' { Invoke-Kill $rest $false }
        'killall' { Invoke-Kill $rest $true }
        'sleep' { Invoke-Sleep $rest }
        'sudo' { Invoke-Sudo }
        default { Fail "unknown command '$command'" 127 }
    }
}
catch {
    Fail $_.Exception.Message
}
exit $script:code
