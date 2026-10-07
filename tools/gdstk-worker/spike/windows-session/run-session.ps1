# circuitRF -- gdstk worker, real-Windows confirmation session (brief-oasis-gdstk.md: G0's Q1 and Q3, re-run for G1).
#
#   powershell -ExecutionPolicy Bypass -File run-session.ps1
#
# Runs, for each RID this machine can execute (win-x64 and win-x86 on x64 Windows; win-arm64, win-x64 and win-x86 on
# ARM64 Windows):
#   0. (G1) each RID's worker compiled HERE with tools\gdstk-worker\build.cmd, replacing the Mac cross-build;
#   1. --version, hello and selftest, for every worker variant in bin\<rid> (G0: plain and utf8; G1: the one
#      built here, and the Mac cross-build);
#   2. every recorded job: the corpus read whole and written back as GDSII and OASIS. Every reply and every
#      written file is compared, by SHA-256, with what the macOS build produced for the same request;
#   3. the path cases: a space, e-acute, Omega, CJK, and a path longer than 260 characters (plain and with the
#      \\?\ prefix), read and write, for both variants;
#   4. the 525 malformed files of Q3 and the 5 originals, each in a fresh worker with a 10 s limit,
#      compared with macOS.
# Writes results\summary.txt and results\results.json. Nothing is installed, and nothing outside this folder
# is written except step 0's dependency cache. THIS FILE IS PURE ASCII (packaging/'s .ps1 rule): every
# non-ASCII name is built from [char].

param([string[]]$Rids, [int]$TimeoutSeconds = 10, [switch]$SkipFuzz, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Results = Join-Path $Root 'results'
if (Test-Path $Results) { Remove-Item -Recurse -Force $Results }
New-Item -ItemType Directory -Path $Results | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Results 'replies') | Out-Null
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$Summary = New-Object System.Collections.Generic.List[string]
# Every line is written to results\summary.txt the moment it is said, and the whole console goes to
# results\transcript.txt, so a run that stops part-way still leaves everything it learned.
$SummaryPath = Join-Path $Results 'summary.txt'
function Say([string]$s) { Write-Host $s; $Summary.Add($s); Add-Content -LiteralPath $SummaryPath -Value $s -Encoding Ascii }
function Where-Failed($e) { return ("{0} (script line {1})" -f $e.Exception.Message, $e.InvocationInfo.ScriptLineNumber) }
try { Start-Transcript -LiteralPath (Join-Path $Results 'transcript.txt') | Out-Null } catch { }
# A long-path form of any path: \\?\C:\... for a drive, \\?\UNC\server\share\... for a network share.
function LongPath([string]$p) {
  if ($p.StartsWith('\\?\')) { return $p }
  if ($p.StartsWith('\\')) { return '\\?\UNC\' + $p.Substring(2) }
  return '\\?\' + $p
}

# ---- the machine ----------------------------------------------------------------------------------------
$osArch = 'unknown'
try { $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() } catch { $osArch = $env:PROCESSOR_ARCHITECTURE }
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$lpe = $null
try { $lpe = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').LongPathsEnabled } catch { }
$acp = $null
try { $acp = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').ACP } catch { }
# Smart App Control (Windows 11) blocks unsigned programs: 0 off, 1 on, 2 evaluation.
$sac = 'not present'
try { $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction Stop).VerifiedAndReputablePolicyState } catch { }
$machine = [ordered]@{
  product = $cv.ProductName; display_version = $cv.DisplayVersion; build = "$($cv.CurrentBuild).$($cv.UBR)"
  os_architecture = $osArch; long_paths_enabled = $lpe; ansi_code_page = $acp; powershell = $PSVersionTable.PSVersion.ToString()
  smart_app_control = $sac; session_folder = $Root
}
Say ("machine: {0} {1} build {2}, {3}, LongPathsEnabled={4}, ACP={5}, PowerShell {6}, SmartAppControl={7}" -f $machine.product, $machine.display_version, $machine.build, $machine.os_architecture, $machine.long_paths_enabled, $machine.ansi_code_page, $machine.powershell, $machine.smart_app_control)
Say ("session folder: {0}" -f $Root)

if (-not $Rids) {
  # ARM64 Windows runs all three: x64 and x86 under its own emulation.
  if ($osArch -eq 'Arm64') { $Rids = @('win-arm64', 'win-x64', 'win-x86') }
  elseif ($osArch -eq 'X64') { $Rids = @('win-x64', 'win-x86') }
  else { $Rids = @('win-x86') }
}

# ---- 0. the workers, compiled on THIS machine ------------------------------------------------------------
# The installers are built on Windows, so every RID's worker must compile here: on ARM64 Windows that means
# win-x64 and win-x86 cross-built as well as win-arm64. src\ holds the worker's source and its Windows build
# scripts exactly as the repository has them, and this runs the same build.cmd --strict the packaging script
# runs. build.cmd fetches gdstk, qhull and zlib into %LOCALAPPDATA%\circuitRF-build\gdstk (CRF_GDSTK_CACHE
# overrides it) -- the one place outside this folder the session writes, and the cache packaging uses -- and
# needs llvm-mingw, CMake and Ninja. A worker built here becomes bin\<rid>\gdstk-worker.exe, which every step
# below tests; the Mac cross-build stays beside it as gdstk-worker-mac.exe (steps 1 and 3 test both).
# -NoBuild skips this and tests whatever bin\ holds.
$Built = [ordered]@{}
if (-not $NoBuild) {
  $buildCmd = Join-Path $Root 'src\tools\gdstk-worker\build.cmd'
  foreach ($rid in $Rids) {
    Say ""
    Say "==== build $rid (tools\gdstk-worker\build.cmd --strict --rid $rid)"
    $t0 = Get-Date
    # Continue, not Stop: Windows PowerShell 5.1 turns a native program's first stderr line into a
    # terminating error under Stop, and the build's progress is not an error.
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $lines = @(& $buildCmd --strict --rid $rid 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    Set-Content -LiteralPath (Join-Path $Results "build-$rid.txt") -Value $lines -Encoding UTF8
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    $native = Join-Path $Root "src\tools\gdstk-worker\build\$rid\gdstk-kernel\gdstk-worker.exe"
    $bin = Join-Path $Root "bin\$rid"
    if ($code -eq 0 -and (Test-Path -LiteralPath $native)) {
      $mac = Join-Path $bin 'gdstk-worker-mac.exe'
      if (-not (Test-Path -LiteralPath $mac)) { Move-Item -LiteralPath (Join-Path $bin 'gdstk-worker.exe') -Destination $mac }
      Copy-Item -LiteralPath $native -Destination (Join-Path $bin 'gdstk-worker.exe') -Force
      $size = (Get-Item -LiteralPath $native).Length
      $same = if ((Get-FileHash -LiteralPath $native).Hash -eq (Get-FileHash -LiteralPath $mac).Hash) { 'byte-identical to' } else { 'different bytes from' }
      Say ("  ok in {0} s: {1} bytes, {2} the Mac cross-build" -f $secs, $size, $same)
      # Anything the build printed that is not its own progress, and the import check's verdict.
      $lines | Where-Object { $_ -notmatch '^gdstk-worker: ' -or $_ -match 'imports checked' } | ForEach-Object { Say "    $_" }
      $Built[$rid] = "ok, $secs s, $size bytes, $same the Mac cross-build"
    } else {
      Say ("  FAILED (exit {0}) after {1} s; the Mac cross-build is tested instead. Its last lines:" -f $code, $secs)
      $lines | Select-Object -Last 12 | ForEach-Object { Say "    $_" }
      $Built[$rid] = "FAILED, exit $code"
    }
  }
}

# ---- the worker, spoken to in frames --------------------------------------------------------------------
# Windows PowerShell 5.1 (.NET Framework) builds a child's stdin writer on [Console]::InputEncoding, and when
# that is UTF-8 with a preamble (code page 65001) it writes a byte-order mark, EF BB BF, into the pipe as soon
# as the process starts. The worker reads those three bytes as the start of its first frame header, sees a
# length of 297,778,159 and waits for it: every request times out. An encoding with no preamble stops it.
$PrevInputEncoding = $null
try { $PrevInputEncoding = [Console]::InputEncoding; [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$StdinPreambleReported = $false
function Start-Worker([string]$exe) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  # The worker's test switch: lets the malformed-file step read an OASIS file with the guards off, as the
  # macOS run it is compared with did. It changes nothing else the session asks.
  $psi.EnvironmentVariables['CRF_GDSTK_WORKER_TEST'] = '1'
  $p = [System.Diagnostics.Process]::Start($psi)
  if (-not $script:StdinPreambleReported) {
    $script:StdinPreambleReported = $true
    $pre = $p.StandardInput.Encoding.GetPreamble().Length
    Say ("worker stdin: {0}, {1} preamble byte(s){2}" -f $p.StandardInput.Encoding.WebName, $pre, $(if ($pre -gt 0) { ' -- FRAMES WILL NOT GET THROUGH' } else { '' }))
  }
  $p.BeginErrorReadLine()   # drains gdstk's stderr so it can never fill the pipe and stall the worker
  return @{ Proc = $p; Sha = [System.Security.Cryptography.SHA256]::Create() }
}

# --version through Process.Start, as the worker is started everywhere else: a program Windows refuses to
# run is an answer to record, never a reason for the whole session to stop.
function Run-Version([string]$exe) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe
  $psi.Arguments = '--version'
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $err = $p.StandardError.ReadToEndAsync()
  $out = $p.StandardOutput.ReadToEnd()
  if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { }; return @{ Code = 'timeout'; Text = $out } }
  return @{ Code = $p.ExitCode; Text = ((($out + $err.Result).Trim()) -replace "`r?`n", ' | ') }
}

function Stop-Worker($w) {
  try { if (-not $w.Proc.HasExited) { $w.Proc.Kill() } } catch { }
  try { $w.Proc.WaitForExit(5000) | Out-Null } catch { }
}

function Read-Exact($w, [int]$n, [datetime]$deadline) {
  $buf = New-Object byte[] $n
  $off = 0
  $s = $w.Proc.StandardOutput.BaseStream
  while ($off -lt $n) {
    $remain = ($deadline - [datetime]::UtcNow).TotalMilliseconds
    if ($remain -le 0) { throw 'TIMEOUT' }
    $t = $s.ReadAsync($buf, $off, $n - $off)
    if (-not $t.Wait([int][Math]::Ceiling($remain))) { throw 'TIMEOUT' }
    $k = $t.Result
    if ($k -le 0) { throw 'EOF' }
    $off += $k
  }
  return ,$buf
}

# Sends one frame and returns @{ Bytes = the whole reply frame; Reply = its JSON, parsed }.
function Send-Frame($w, [byte[]]$json, [byte[]]$bin) {
  if ($null -eq $bin) { $bin = New-Object byte[] 0 }
  $s = $w.Proc.StandardInput.BaseStream
  $s.Write([BitConverter]::GetBytes([uint32]$json.Length), 0, 4)
  $s.Write([BitConverter]::GetBytes([uint32]$bin.Length), 0, 4)
  $s.Write($json, 0, $json.Length)
  if ($bin.Length -gt 0) { $s.Write($bin, 0, $bin.Length) }
  $s.Flush()
  $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
  $h = Read-Exact $w 8 $deadline
  $jl = [BitConverter]::ToUInt32($h, 0)
  $bl = [BitConverter]::ToUInt32($h, 4)
  $j = Read-Exact $w ([int]$jl) $deadline
  $b = if ($bl -gt 0) { Read-Exact $w ([int]$bl) $deadline } else { New-Object byte[] 0 }
  $all = New-Object byte[] (8 + $jl + $bl)
  [Array]::Copy($h, 0, $all, 0, 8)
  [Array]::Copy($j, 0, $all, 8, $jl)
  if ($bl -gt 0) { [Array]::Copy($b, 0, $all, 8 + $jl, $bl) }
  $w.Sha.TransformBlock($all, 0, $all.Length, $null, 0) | Out-Null
  return @{ Bytes = $all; Reply = ($Utf8.GetString($j) | ConvertFrom-Json) }
}

function Send-Json($w, [string]$text) { return Send-Frame $w ($Utf8.GetBytes($text)) $null }

function Hex([byte[]]$b) { return ([BitConverter]::ToString($b) -replace '-', '').ToLowerInvariant() }
function Sha256Bytes([byte[]]$b) { $s = [System.Security.Cryptography.SHA256]::Create(); return Hex ($s.ComputeHash($b)) }
function Final-Sha($w) { $w.Sha.TransformFinalBlock((New-Object byte[] 0), 0, 0) | Out-Null; return Hex $w.Sha.Hash }
function JsonStr([string]$s) { return '"' + ($s.Replace('\', '\\').Replace('"', '\"')) + '"' }

$RootJson = $Root.Replace('\', '\\').Replace('"', '\"')
$Report = [ordered]@{ machine = $machine; build = $Built; rids = [ordered]@{} }

foreach ($rid in $Rids) {
  $bin = Join-Path $Root "bin\$rid"
  $Variants = @(Get-ChildItem -LiteralPath $bin -Filter '*.exe' | Sort-Object Name | ForEach-Object { $_.BaseName })
  # NOT $R: PowerShell names are case-insensitive, and every reply below is $r.
  $RidResult = [ordered]@{}
  Say ""
  Say "==== $rid"

  # ---- 1. version and selftest ----
  foreach ($variant in $Variants) {
    $exe = Join-Path $bin "$variant.exe"
    $ver = ''; $code = 'not run'
    try { $v = Run-Version $exe; $ver = $v.Text; $code = $v.Code } catch { $ver = "COULD NOT START: " + (Where-Failed $_) }
    $st = 'not run'; $hello = 'not run'
    try {
      $w = Start-Worker $exe
      $h = Send-Json $w '{"op":"hello","protocol":1}'
      $hello = "worker $($h.Reply.worker), rid $($h.Reply.rid), code page $($h.Reply.code_page)"
      $r = Send-Json $w '{"op":"selftest"}'
      $st = if ($r.Reply.ok) { "ok (gds $($r.Reply.gds_elements), oas $($r.Reply.oas_elements), valid $($r.Reply.oas_valid))" } else { "REFUSED: $($r.Reply.code) $($r.Reply.detail)" }
      Stop-Worker $w
    } catch { $st = "FAILED: $_" }
    Say ("  {0} --version (exit {1}): {2}" -f $variant, $code, $ver)
    Say ("  {0} hello: {1}" -f $variant, $hello)
    Say ("  {0} selftest: {1}" -f $variant, $st)
    $RidResult["$variant version"] = $ver
    $RidResult["$variant hello"] = $hello
    $RidResult["$variant selftest"] = $st
  }
  $exe = Join-Path $bin 'gdstk-worker.exe'
  if (-not ("$($RidResult['gdstk-worker selftest'])".StartsWith('ok'))) {
    Say "  the plain worker's selftest did not pass, so the jobs, paths and malformed files are skipped for $rid"
    $Report.rids[$rid] = $RidResult
    $Report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 (Join-Path $Results 'results.json')
    continue
  }

  # ---- 2. recorded jobs ----
  try {
  $expected = Get-Content -Raw (Join-Path $Root 'jobs\expected.json') | ConvertFrom-Json
  $jobsSame = 0; $jobsDiff = New-Object System.Collections.Generic.List[string]
  foreach ($job in $expected) {
    $data = [IO.File]::ReadAllBytes((Join-Path $Root "jobs\$($job.job).job"))
    $w = Start-Worker $exe
    $ok = $true; $i = 0; $at = 0
    try {
      while ($at -lt $data.Length) {
        $jl = [BitConverter]::ToUInt32($data, $at); $bl = [BitConverter]::ToUInt32($data, $at + 4)
        $text = $Utf8.GetString($data, $at + 8, $jl).Replace('@ROOT@', $RootJson)
        $binb = New-Object byte[] $bl
        if ($bl -gt 0) { [Array]::Copy($data, $at + 8 + $jl, $binb, 0, $bl) }
        $at += 8 + $jl + $bl
        $r = Send-Frame $w ($Utf8.GetBytes($text)) $binb
        if ((Sha256Bytes $r.Bytes) -ne $job.replies[$i]) {
          $ok = $false
          [IO.File]::WriteAllBytes((Join-Path $Results "replies\$rid-$($job.job)-$i.bin"), $r.Bytes)
        }
        $i++
      }
      foreach ($p in $job.outputs.PSObject.Properties) {
        $f = Join-Path $Root ($p.Name.Replace('/', '\'))
        $got = if (Test-Path -LiteralPath $f) { (Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLowerInvariant() } else { 'missing' }
        if ($got -ne $p.Value) { $ok = $false; Copy-Item -LiteralPath $f (Join-Path $Results "replies\$rid-$($job.job).out") -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue
      }
    } catch { $ok = $false; $jobsDiff.Add("$($job.job): $_") }
    Stop-Worker $w
    if ($ok) { $jobsSame++ } elseif (-not ($jobsDiff | Where-Object { $_.StartsWith($job.job) })) { $jobsDiff.Add($job.job) }
  }
  Say ("  jobs: {0} of {1} identical to macOS" -f $jobsSame, $expected.Count)
  foreach ($d in $jobsDiff) { Say "    DIFFERENT: $d" }
  $RidResult['jobs identical'] = $jobsSame; $RidResult['jobs total'] = $expected.Count; $RidResult['jobs different'] = @($jobsDiff)
  } catch { Say ("  jobs: STOPPED: " + (Where-Failed $_)); $RidResult['jobs stopped'] = (Where-Failed $_) }

  # ---- 3. paths ----
  try {
  $names = @(
    @{ Key = 'space'; Name = 'with space' },
    @{ Key = 'e-acute'; Name = ('caf' + [char]0x00E9) },
    @{ Key = 'omega'; Name = ([string][char]0x03A9 + '-ohm') },
    @{ Key = 'cjk'; Name = ([string][char]0x56DE + [string][char]0x8DEF) },
    @{ Key = 'long'; Name = (((('d' * 50) + '\') * 5).TrimEnd('\')) }
  )
  $xy = New-Object byte[] 64
  $k = 0
  foreach ($v in @(0, 0, 1, 0, 1, 0.5, 0, 0.5)) { [Array]::Copy([BitConverter]::GetBytes([double]($v * 1000)), 0, $xy, $k, 8); $k += 8 }
  $pathRows = New-Object System.Collections.Generic.List[object]
  foreach ($variant in $Variants) {
    foreach ($n in $names) {
      $dir = Join-Path $Results ("paths-$rid-$variant\" + $n.Name)
      [IO.Directory]::CreateDirectory((LongPath $dir)) | Out-Null
      $forms = @(@{ Form = 'plain'; Prefix = '' })
      if ($n.Key -eq 'long') { $forms += @{ Form = 'prefixed'; Prefix = '\\?\' } }
      foreach ($form in $forms) {
        foreach ($fmt in @('gds', 'oas')) {
          $src = Join-Path $Root "corpus\plain.$fmt"
          $target = Join-Path $dir "layout $($n.Key).$fmt"
          [IO.File]::Copy((LongPath $src), (LongPath $target), $true)
          $row = [ordered]@{ rid = $rid; variant = $variant; case = $n.Key; form = $form.Form; format = $fmt; path_chars = ($form.Prefix + $target).Length; read = $null; write = $null }
          $w = Start-Worker (Join-Path $bin "$variant.exe")
          try {
            $r = Send-Json $w ('{"op":"open","path":' + (JsonStr ($form.Prefix + $target)) + ',"format":"' + $fmt + '"}')
            $row.read = if ($r.Reply.ok) { 'ok' } else { "refused: $($r.Reply.code)" }
            $out = Join-Path $dir "written $($n.Key).$fmt"
            $r = Send-Json $w ('{"op":"begin-write","format":"' + $fmt + '","unit_m":1e-6,"precision_m":1e-9}')
            $h = $r.Reply.handle
            $add = '{"op":"add-cell","handle":' + $h + ',"name":"top","polygons":[{"layer":1,"datatype":0,"n":4}],"paths":[],"labels":[],"refs":[],"blobs":[{"name":"xy","type":"f64","count":8},{"name":"path_xy","type":"f64","count":0}]}'
            Send-Frame $w ($Utf8.GetBytes($add)) $xy | Out-Null
            $r = Send-Json $w ('{"op":"finish-write","handle":' + $h + ',"path":' + (JsonStr ($form.Prefix + $out)) + '}')
            $exists = [IO.File]::Exists((LongPath $out))
            $row.write = if ($r.Reply.ok -and $exists) { 'ok' } elseif ($r.Reply.ok) { 'answered ok, but no file' } else { "refused: $($r.Reply.code) $($r.Reply.detail)" }
          } catch { if (-not $row.read) { $row.read = "FAILED: $_" } else { $row.write = "FAILED: $_" } }
          Stop-Worker $w
          $pathRows.Add($row)
          Say ("  path {0,-18} {1,-8} {2,-8} {3} ({4} chars): read {5}; write {6}" -f $variant, $n.Key, $form.Form, $fmt, $row.path_chars, $row.read, $row.write)
        }
      }
    }
  }
  $RidResult['paths'] = $pathRows.ToArray()
  # The test folders go: every outcome is in the rows above, and a 370-character folder left in results\ is
  # one Explorer cannot copy or zip (G1 session 2). Deleted through the \\?\ form, which removes it.
  foreach ($variant in $Variants) {
    $pdir = Join-Path $Results "paths-$rid-$variant"
    try { if ([IO.Directory]::Exists((LongPath $pdir))) { [IO.Directory]::Delete((LongPath $pdir), $true) } }
    catch { Say ("  could not remove {0}: {1} -- delete it before copying results\" -f $pdir, $_.Exception.Message) }
  }
  } catch { Say ("  paths: STOPPED: " + (Where-Failed $_)); $RidResult['paths stopped'] = (Where-Failed $_) }

  # ---- 4. malformed files ----
  if (-not $SkipFuzz) {
   try {
    $cases = Get-Content -Raw (Join-Path $Root 'fuzz\expected.json') | ConvertFrom-Json
    $same = 0; $diff = New-Object System.Collections.Generic.List[object]; $classes = @{}
    foreach ($c in $cases) {
      $path = Join-Path $Root "fuzz\$($c.file)"
      $w = Start-Worker $exe
      $cls = 'read'; $detail = $null; $digest = $null
      try {
        $open = '{"op":"open","path":' + (JsonStr $path) + ',"format":"' + $c.format + '","tolerance_dbu":0.5' + $(if ($c.validate) { '' } else { ',"validate":false' }) + '}'
        $r = Send-Json $w $open
        if (-not $r.Reply.ok) { $cls = 'refused'; $detail = $r.Reply.code }
        else {
          foreach ($cell in $r.Reply.cells) {
            $cr = Send-Json $w ('{"op":"cell","handle":' + $r.Reply.handle + ',"name":' + (JsonStr $cell.name) + '}')
            if (-not $cr.Reply.ok) { $cls = 'refused'; $detail = $cr.Reply.code; break }
          }
          if ($cls -eq 'read') { Send-Json $w ('{"op":"close","handle":' + $r.Reply.handle + '}') | Out-Null }
        }
        $digest = Final-Sha $w
      } catch {
        if ("$_" -match 'TIMEOUT') { $cls = 'hang' }
        else { try { $w.Proc.WaitForExit(5000) | Out-Null } catch { }; $cls = 'crash'; $detail = ('0x{0:X8}' -f $w.Proc.ExitCode) }
      }
      Stop-Worker $w
      $classes["$cls"] = 1 + [int]$classes["$cls"]
      $match = ($cls -eq $c.mac_class) -and (($cls -ne 'read' -and $cls -ne 'refused') -or ($digest -eq $c.mac_reply_sha256))
      if ($match) { $same++ } else { $diff.Add([ordered]@{ file = $c.file; fixture = $c.fixture; windows = $cls; detail = $detail; macos = $c.mac_class }) }
    }
    Say ("  malformed files: {0} of {1} answered exactly as on macOS; classes here: {2}" -f $same, $cases.Count, (($classes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '))
    foreach ($d in $diff) { Say ("    DIFFERENT: {0} [{1}] windows={2} {3} macos={4}" -f $d.file, $d.fixture, $d.windows, $d.detail, $d.macos) }
    $RidResult['fuzz same as macos'] = $same; $RidResult['fuzz total'] = $cases.Count; $RidResult['fuzz different'] = $diff.ToArray()
   } catch { Say ("  malformed files: STOPPED: " + (Where-Failed $_)); $RidResult['fuzz stopped'] = (Where-Failed $_) }
  }
  $Report.rids[$rid] = $RidResult
  # after every RID, so a later stop still leaves this one's results
  $Report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 (Join-Path $Results 'results.json')
}

$Report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 (Join-Path $Results 'results.json')
Say "==== finished"
if ($null -ne $PrevInputEncoding) { try { [Console]::InputEncoding = $PrevInputEncoding } catch { } }
try { Stop-Transcript | Out-Null } catch { }
Write-Host ""
Write-Host "Done. Please zip the 'results' folder ($Results) and send it back, with RESULTS-TEMPLATE.md filled in."
