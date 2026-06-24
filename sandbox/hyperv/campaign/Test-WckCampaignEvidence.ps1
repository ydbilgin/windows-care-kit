#requires -Version 7.0
<#
.SYNOPSIS
    FAIL-CLOSED structural evidence verifier for ONE M-UNINSTALL Persona-A campaign cell
    (F0.5a, decision C-1 / C-20). PRIMARY non-vacuity gate.

.DESCRIPTION
    Dot-source then call:

        . "$PSScriptRoot\Test-WckCampaignEvidence.ps1"
        $r = Test-WckCampaignEvidence -EvidenceDir <dir>
        # -> [pscustomobject] { Pass:bool; Reasons:string[]; Digest:<sha256> }

    Pass=$true is returned ONLY when EVERY one of the checks below holds. There is no path
    by which an exception is swallowed into Pass=$true: the function wraps its body in a
    try/catch whose catch returns Pass=$false with the exception as a reason. cx/opus
    verdicts are signed ANNOTATIONS over the returned Digest — NOT the pass mechanism.

    Expected evidence layout under -EvidenceDir (written by Invoke-WckCampaignCell.ps1):
        cell-manifest.json              run-id + VM/persona/module/checkpoint identity
                                        + runStartUtc + generatedUtc + afterSnapshotUtc
                                        + resetUtc (C-17)
        before/uninstall-registry.reg   2nd-channel BEFORE snapshot (apps present pre-action)
        before/dir-hashes.json          2nd-channel BEFORE recursive file hashes
        after/uninstall-registry.reg    2nd-channel AFTER snapshot (apps gone post-action)
        after/dir-hashes.json           2nd-channel AFTER recursive file hashes
        uninstall-e2e-evidence.json     UninstallE2E JSON verdict (Pass/Verdict + focus[])
        uninstall-e2e-result.txt        banner result string
        harness-exitcode.txt            process exit code (must agree with JSON verdict)
        vm-final-state.json             { State:'Off'; Checkpoint:'baseline-clean' }

    Checks (all must pass; numbered per spec D2; FIX-B/FIX-C structural non-vacuity):
      1. unique run-id + VM/persona/module/checkpoint identity present in cell-manifest;
         run-window timestamps (runStartUtc/generatedUtc/afterSnapshotUtc/resetUtc) parse.
      2. UNINSTALL REGISTRY WITNESS: executed apps (git, vscode) are absent from the
         independent AFTER registry export, while required non-executed apps (7zip,
         notepadpp) remain present there.
      3. required evidence files exist, are non-empty, READABLE, and every file's
         LastWriteTimeUtc lies inside the run window (no stale fixture).
      4. JSON verdict == process exit code (mismatch -> FAIL); result.txt is a valid banner.
      5. HARNESS AGREEMENT: harness pass=true, executed apps report registry-gone=True,
         branchMismatch is empty; NEGATIVE CONTROL discriminating.
      6. NO forbidden strings in evidence text or file names (secret/token patterns +
         host-username) — grep -> any hit FAILs. Unreadable file -> FAIL (fail-closed).
      7. final VM Off + expected checkpoint (reset proof); afterSnapshotUtc < resetUtc (C-17).

    Digest = ordered SHA256 over EVERY accepted artifact under -EvidenceDir (relative-path +
    hash, sorted) — NOT a hardcoded 9-file list. Any extra/changed artifact moves the digest.

    HOST SAFETY: read-only. Touches nothing but files under -EvidenceDir.
#>

Set-StrictMode -Version Latest

# Forbidden secret/token shapes (C-10). OBVIOUSLY these are PATTERNS, not real secrets.
$script:WckForbiddenPatterns = @(
    'ya29\.[0-9A-Za-z_\-]+',          # Google OAuth access token
    '1//0[0-9A-Za-z_\-]+',            # Google refresh token
    'AIza[0-9A-Za-z_\-]{10,}',        # Google API key
    'ghp_[0-9A-Za-z]{20,}',           # GitHub PAT (classic)
    'gho_[0-9A-Za-z]{20,}',           # GitHub OAuth
    'github_pat_[0-9A-Za-z_]{20,}',   # GitHub fine-grained PAT
    'sk-[0-9A-Za-z]{20,}',            # OpenAI
    'xox[baprs]-[0-9A-Za-z\-]{10,}'   # Slack
)

function Get-WckEvidenceDigest {
    <#
      Ordered SHA256 over the supplied (existing) files -> one composite hex digest.
      FIX-B: each file contributes its RELATIVE path (under -BaseDir) + its hash, so the
      digest covers WHICH files exist and WHERE — adding/removing/moving an artifact moves
      the digest. Unreadable file -> THROW (fail-closed; never a silent skip into a pass).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Files,
        [Parameter(Mandatory)] [string]   $BaseDir
    )

    $baseFull = [System.IO.Path]::GetFullPath($BaseDir)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $acc = [System.IO.MemoryStream]::new()
        # Sort by relative path for a deterministic, location-sensitive ordering.
        $entries = foreach ($f in $Files) {
            $full = [System.IO.Path]::GetFullPath($f)
            $rel  = [System.IO.Path]::GetRelativePath($baseFull, $full).Replace('\','/')
            [pscustomobject]@{ Rel = $rel; Full = $full }
        }
        foreach ($e in ($entries | Sort-Object Rel)) {
            if (-not (Test-Path -LiteralPath $e.Full)) {
                throw "Get-WckEvidenceDigest: artifact vanished mid-digest: $($e.Rel)"
            }
            $h = (Get-FileHash -LiteralPath $e.Full -Algorithm SHA256 -ErrorAction Stop).Hash
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("$($e.Rel)=$h;")
            $acc.Write($bytes, 0, $bytes.Length)
        }
        $acc.Position = 0
        return ([System.BitConverter]::ToString($sha.ComputeHash($acc)) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function ConvertTo-WckUtc {
    <# Parse an ISO-8601 / round-trip value to a UTC DateTime, or $null on failure.
       MUST accept a [datetime]/[datetimeoffset] directly: ConvertFrom-Json (PS7) auto-
       deserializes ISO-8601 manifest timestamps to [datetime], and a [string] cast of that
       renders the CURRENT-CULTURE short format — dropping sub-seconds AND the 'Z', which makes
       ConvertTo-WckUtc re-parse it as local and shift by the TZ offset (collapsing afterUtc and
       resetUtc to the same whole second → spurious C-17 ordering FAIL). #>
    param([object] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime])       { return ([datetime]$Value).ToUniversalTime() }
    if ($Value -is [datetimeoffset]) { return ([datetimeoffset]$Value).UtcDateTime }
    $s = [string]$Value
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $dt = [datetime]::MinValue
    # RoundtripKind honours the 'o' format's embedded offset/Z; convert to UTC afterward.
    $styles = [System.Globalization.DateTimeStyles]::RoundtripKind
    if ([datetime]::TryParse($s, [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$dt)) {
        return $dt.ToUniversalTime()
    }
    return $null
}

function ConvertFrom-WckRegExport {
    [CmdletBinding()]
    param([AllowNull()] [string] $Text)

    $entries = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in (([string]$Text) -split "`r?`n")) {
        if ($line -match '^\[(?<key>[^\]]+)\]$') {
            $keyPath = $matches.key
            $keyName = ($keyPath -split '\\')[-1]
            $current = [pscustomobject]@{
                KeyPath = $keyPath
                KeyName = $keyName
                Properties = [ordered]@{}
            }
            $entries.Add($current) | Out-Null
            continue
        }
        if ($null -eq $current) { continue }
        if ($line -match '^"(?<name>[^"]+)"=(?<raw>.+)$') {
            $name = $matches.name
            $raw = ([string]$matches.raw).Trim()
            $value = $raw
            if ($raw -match '^"(.*)"$') {
                $value = $matches[1] -replace '\\"','"' -replace '\\\\','\'
            }
            $current.Properties[$name] = $value
        }
    }
    return @($entries)
}

function Get-WckRegEntryStringValue {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [string] $PropertyName
    )

    if ($Entry.Properties.Contains($PropertyName)) {
        return [string]$Entry.Properties[$PropertyName]
    }
    return ''
}

function Test-WckRegEntryMatchesTarget {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] $Target
    )

    foreach ($key in @($Target.KeyNames)) {
        if ([string]::Equals([string]$Entry.KeyName, [string]$key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    foreach ($rx in @($Target.KeyRegexes)) {
        if ([string]$Entry.KeyName -match [string]$rx) { return $true }
    }

    $displayName = Get-WckRegEntryStringValue -Entry $Entry -PropertyName 'DisplayName'
    foreach ($name in @($Target.DisplayNames)) {
        if ([string]::Equals($displayName, [string]$name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    foreach ($rx in @($Target.DisplayNameRegexes)) {
        if ($displayName -match [string]$rx) { return $true }
    }
    return $false
}

function Find-WckRegEntriesForTarget {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Entries,
        [Parameter(Mandatory)] $Target
    )

    return @($Entries | Where-Object { Test-WckRegEntryMatchesTarget -Entry $_ -Target $Target })
}

function Get-WckUninstallRegistryTargets {
    param([Parameter(Mandatory)] [ValidateSet('A','B')] [string] $Persona)

    if ($Persona -eq 'B') {
        return @(
            [pscustomobject]@{
                Id = 'qbittorrent'
                Role = 'required-not-executed'
                KeyNames = @('qBittorrent')
                KeyRegexes = @('^qBittorrent')
                DisplayNames = @('qBittorrent')
                DisplayNameRegexes = @('^qBittorrent( .*)?$')
            }
            [pscustomobject]@{
                Id = 'chrome-enterprise'
                Role = 'required-not-executed'
                KeyNames = @()
                KeyRegexes = @()
                DisplayNames = @('Google Chrome')
                DisplayNameRegexes = @('^Google Chrome( Enterprise)?$')
            }
        )
    }

    return @(
        [pscustomobject]@{
            Id = 'git'
            Role = 'executed'
            KeyNames = @('Git_is1')
            KeyRegexes = @()
            DisplayNames = @('Git')
            DisplayNameRegexes = @()
        }
        [pscustomobject]@{
            Id = 'vscode'
            Role = 'executed'
            KeyNames = @('{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1')
            KeyRegexes = @()
            DisplayNames = @('Microsoft Visual Studio Code', 'Microsoft Visual Studio Code (User)')
            DisplayNameRegexes = @()
        }
        [pscustomobject]@{
            Id = '7zip'
            Role = 'required-not-executed'
            KeyNames = @()
            KeyRegexes = @('^\{23170F69-40C1-2702-24[0-9]{2}-000001000000\}$')
            DisplayNames = @()
            DisplayNameRegexes = @('^7-Zip [0-9][0-9.]* \(x64( edition)?\)$')
        }
        [pscustomobject]@{
            Id = 'notepadpp'
            Role = 'required-not-executed'
            KeyNames = @('Notepad++')
            KeyRegexes = @()
            DisplayNames = @('Notepad++ (64-bit x64)')
            DisplayNameRegexes = @()
        }
    )
}

function Test-WckPersonaBUninstallDisposition {
    param(
        [Parameter(Mandatory)] $Disposition,
        [AllowNull()] $SeedManifest,
        [Parameter(Mandatory)] $Evidence,
        [Parameter(Mandatory)] [object[]] $Focus,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Executions,
        [System.Collections.Generic.List[string]] $Reasons
    )

    if ([string](Get-WckJsonValue $Disposition 'persona') -ne 'B') {
        $Reasons.Add("Persona-B disposition persona mismatch")
    }
    $apps = @((Get-WckJsonValue $Disposition 'apps') | Where-Object { $_ })
    foreach ($id in 'qbittorrent','chrome-enterprise','steam','discord','spotify') {
        $hit = $apps | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'appId'), $id, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $hit) { $Reasons.Add("Persona-B disposition missing app '$id'"); continue }
        $source = [string](Get-WckJsonValue $hit 'source')
        if ($id -in @('steam','discord','spotify')) {
            if ($source -ne 'synthetic-seed') {
                $Reasons.Add("Persona-B synthetic app '$id' claimed source '$source' (expected synthetic-seed)")
            }
        } elseif ($source -ne 'real-installed') {
            $Reasons.Add("Persona-B real app '$id' claimed source '$source' (expected real-installed)")
        }
    }

    if ($SeedManifest) {
        $seedApps = @((Get-WckJsonValue $SeedManifest 'apps') | Where-Object { $_ })
        foreach ($id in 'steam','discord','spotify') {
            $seedHit = $seedApps | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'id'), $id, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if (-not $seedHit -or [string](Get-WckJsonValue $seedHit 'action') -ne 'synthetic-seed') {
                $Reasons.Add("Persona-B seed manifest does not mark '$id' as synthetic-seed")
            }
        }
    }

    $qbFocus = $Focus | Where-Object { [string]::Equals([string]$_.targetId, 'qbittorrent', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if (-not $qbFocus -or -not (Get-StrictJsonBool $qbFocus 'found' 'focus[qbittorrent]')) {
        $Reasons.Add("Persona-B qBittorrent focus missing/not found")
    }
    $chromeFocus = $Focus | Where-Object { [string]::Equals([string]$_.targetId, 'chrome-enterprise', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if (-not $chromeFocus -or -not (Get-StrictJsonBool $chromeFocus 'found' 'focus[chrome-enterprise]')) {
        $Reasons.Add("Persona-B Chrome focus missing/not found")
    }
    if ($qbFocus -and [string](Get-WckJsonValue $qbFocus 'classification') -ne 'MANUAL') {
        $Reasons.Add("Persona-B qBittorrent must classify as MANUAL")
    }
    if ($chromeFocus -and [string](Get-WckJsonValue $chromeFocus 'classification') -ne 'ALLOW') {
        $Reasons.Add("Persona-B Chrome must classify as ALLOW")
    }
    if ($chromeFocus) {
        $silentCapable = Get-WckJsonValue $chromeFocus 'silentCapable'
        if ($silentCapable -isnot [bool] -or $silentCapable) {
            $Reasons.Add("Persona-B Chrome must be an unattended decline witness (silentCapable=false)")
        }
    }
    $qbExec = $Executions | Where-Object { [string]::Equals([string]$_.targetId, 'qbittorrent', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($qbExec) {
        $Reasons.Add("Persona-B qBittorrent must be manual witness and not executed")
    }
    $chromeExec = $Executions | Where-Object { [string]::Equals([string]$_.targetId, 'chrome-enterprise', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($chromeExec) {
        $chromeSkipped = Get-StrictJsonBool $chromeExec 'skipped' 'executions[chrome-enterprise]'
        $chromeRemoved = Get-StrictJsonBool $chromeExec 'removedFromRegistry' 'executions[chrome-enterprise]'
        $detail = [string](Get-WckJsonValue $chromeExec 'detail')
        if (-not $chromeSkipped -or $chromeRemoved -or $detail -notmatch 'no silent switch|would block unattended') {
            $Reasons.Add("Persona-B Chrome execution entry must be skipped for no silent switch and not removed")
        }
    } else {
        $executeSet = @((Get-WckJsonValue $Evidence 'executeSet') | Where-Object { $_ })
        if ($executeSet.Count -gt 0) {
            $Reasons.Add("Persona-B executeSet must be empty unless Chrome is explicitly skipped")
        }
    }

    $unproven = @((Get-WckJsonValue $Evidence 'unprovenExecutions') | Where-Object { $_ })
    foreach ($u in $unproven) {
        $s = [string]$u
        if ($s -notmatch 'chrome-enterprise' -or $s -notmatch 'no silent switch|would block unattended') {
            $Reasons.Add("Persona-B unproven execution is not the expected Chrome no-silent-switch decline: $s")
        }
    }
}

function Test-WckCampaignEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $EvidenceDir,
        # Host-username pattern that must NOT leak into any artifact (C-10). Default = current user.
        [string] $HostUsername = $env:USERNAME,
        # Tolerance (minutes) for file-write-time vs run-window staleness (clock skew / copy lag).
        [int] $FreshnessSlackMinutes = 1440,
        # Small backward tolerance from runStartUtc for timestamp granularity/copy latency only.
        [int] $FreshnessBackSlackMinutes = 2
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $digest = $null

    try {
        if (-not (Test-Path -LiteralPath $EvidenceDir -PathType Container)) {
            $reasons.Add("evidence dir not found: $EvidenceDir")
            return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $null }
        }

        $manifestPath = Join-Path $EvidenceDir 'cell-manifest.json'
        $beforeHash   = Join-Path $EvidenceDir 'before/dir-hashes.json'
        $afterHash    = Join-Path $EvidenceDir 'after/dir-hashes.json'
        $finalState   = Join-Path $EvidenceDir 'vm-final-state.json'

        # --- parse the JSON artifacts (fail-closed on malformed JSON) -----------------------
        function Read-Json([string]$p, [string]$label) {
            try { return (Get-Content -LiteralPath $p -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop) }
            catch { $reasons.Add("malformed/unreadable JSON in $label ($(Split-Path $p -Leaf)): $($_.Exception.Message)"); return $null }
        }
        function Get-StrictJsonBool($obj, [string]$property, [string]$label) {
            if ($null -eq $obj -or -not $obj.PSObject.Properties.Match($property).Count) {
                $reasons.Add("$label missing boolean field: $property")
                return $false
            }
            $value = $obj.$property
            if ($value -isnot [bool]) {
                $typeName = if ($null -eq $value) { '<null>' } else { $value.GetType().FullName }
                $reasons.Add("$label boolean field '$property' is $typeName, expected System.Boolean (strict JSON bool)")
                return $false
            }
            return [bool]$value
        }

        function Test-RequiredEvidenceFiles([string[]]$Files) {
            foreach ($f in $Files) {
                if (-not (Test-Path -LiteralPath $f)) { $reasons.Add("missing evidence file: $(Split-Path $f -Leaf) ($f)") ; continue }
                if ((Get-Item -LiteralPath $f).Length -le 0) { $reasons.Add("empty evidence file: $(Split-Path $f -Leaf)") ; continue }
                try { [System.IO.File]::OpenRead($f).Dispose() }
                catch { $reasons.Add("unreadable evidence file (fail-closed): $(Split-Path $f -Leaf) — $($_.Exception.Message)") }
            }
        }

        Test-RequiredEvidenceFiles @($manifestPath)
        if ($reasons.Count -gt 0) {
            return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $null }
        }
        $manifest    = Read-Json $manifestPath 'cell-manifest'
        if ($null -eq $manifest) {
            return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $null }
        }

        $module = if ($manifest.PSObject.Properties.Match('module').Count) { [string]$manifest.module } else { '' }
        $persona = if ($manifest.PSObject.Properties.Match('persona').Count) { [string]$manifest.persona } else { '' }
        $beforeReg = $null
        $afterReg = $null
        $ueJson = $null
        $ueResult = $null
        $exitPath = $null
        $personaSeedJson = $null
        $personaDispositionJson = $null
        $migJson = $null
        $migSummary = $null
        $migZip = $null
        $cleanJson = $null
        $cleanSummary = $null

        $requiredFiles = @($manifestPath, $beforeHash, $afterHash, $finalState)
        switch ($module) {
            'Uninstall' {
                $beforeReg = Join-Path $EvidenceDir 'before/uninstall-registry.reg'
                $afterReg  = Join-Path $EvidenceDir 'after/uninstall-registry.reg'
                $ueJson    = Join-Path $EvidenceDir 'uninstall-e2e-evidence.json'
                $ueResult  = Join-Path $EvidenceDir 'uninstall-e2e-result.txt'
                $exitPath  = Join-Path $EvidenceDir 'harness-exitcode.txt'
                $requiredFiles += @($beforeReg, $afterReg, $ueJson, $ueResult, $exitPath)
                if ($persona -eq 'B') {
                    $personaSeedJson = Join-Path $EvidenceDir 'persona-seed-manifest.json'
                    $personaDispositionJson = Join-Path $EvidenceDir 'persona-b-disposition.json'
                    $requiredFiles += @($personaSeedJson, $personaDispositionJson)
                }
            }
            'Migration' {
                $migJson    = Join-Path $EvidenceDir 'migration-e2e-evidence.json'
                $migSummary = Join-Path $EvidenceDir 'migration-e2e-summary.txt'
                $migZip     = Join-Path $EvidenceDir 'migration-export.zip'
                $requiredFiles += @($migJson, $migSummary, $migZip)
            }
            'Clean' {
                $beforeReg = Join-Path $EvidenceDir 'before/run-registry.reg'
                $afterReg  = Join-Path $EvidenceDir 'after/run-registry.reg'
                $cleanJson = Join-Path $EvidenceDir 'clean-e2e-evidence.json'
                $cleanSummary = Join-Path $EvidenceDir 'clean-e2e-summary.txt'
                $requiredFiles += @($beforeReg, $afterReg, $cleanJson, $cleanSummary)
            }
            default {
                $reasons.Add("cell-manifest module expected one of [Uninstall, Migration, Clean], got '$module'")
            }
        }

        Test-RequiredEvidenceFiles $requiredFiles
        if ($reasons.Count -gt 0) {
            return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $null }
        }

        $ue = $null
        $mig = $null
        $clean = $null
        $personaSeed = $null
        $personaDisposition = $null
        $final = Read-Json $finalState 'vm-final-state'
        $beforeHashes = Read-Json $beforeHash 'before/dir-hashes'
        $afterHashes  = Read-Json $afterHash  'after/dir-hashes'
        if ($module -eq 'Uninstall') { $ue = Read-Json $ueJson 'uninstall-e2e-evidence' }
        if ($module -eq 'Uninstall' -and $persona -eq 'B') {
            $personaSeed = Read-Json $personaSeedJson 'persona-seed-manifest'
            $personaDisposition = Read-Json $personaDispositionJson 'persona-b-disposition'
        }
        if ($module -eq 'Migration') { $mig = Read-Json $migJson 'migration-e2e-evidence' }
        if ($module -eq 'Clean')     { $clean = Read-Json $cleanJson 'clean-e2e-evidence' }

        if ($null -eq $final -or
            ($module -eq 'Uninstall' -and $null -eq $ue) -or
            ($module -eq 'Uninstall' -and $persona -eq 'B' -and ($null -eq $personaSeed -or $null -eq $personaDisposition)) -or
            ($module -eq 'Migration' -and $null -eq $mig) -or
            ($module -eq 'Clean' -and $null -eq $clean)) {
            # Without these we cannot run the structural checks; fail now.
            return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $null }
        }

        # --- CHECK 1: unique run-id + identity present in manifest --------------------------
        foreach ($field in 'runId','vmName','persona','module','checkpoint') {
            if (-not $manifest.PSObject.Properties.Match($field).Count -or
                [string]::IsNullOrWhiteSpace([string]$manifest.$field)) {
                $reasons.Add("cell-manifest missing identity field: $field")
            }
        }
        if ($manifest.PSObject.Properties.Match('runId').Count) {
            if ([string]$manifest.runId -notmatch '^[0-9A-Za-z][0-9A-Za-z._\-]{7,}$') {
                $reasons.Add("cell-manifest runId is not a well-formed unique id: '$($manifest.runId)'")
            }
        }
        if ($manifest.PSObject.Properties.Match('persona').Count -and (@('A','B') -notcontains [string]$manifest.persona)) {
            $reasons.Add("cell-manifest persona expected one of [A, B], got '$($manifest.persona)'")
        }
        if ($manifest.PSObject.Properties.Match('module').Count -and (@('Uninstall','Migration','Clean') -notcontains [string]$manifest.module)) {
            $reasons.Add("cell-manifest module expected one of [Uninstall, Migration, Clean], got '$($manifest.module)'")
        }

        # --- run-window timestamps (FIX-B freshness anchor + FIX-C ordering) ----------------
        $startUtc = if ($manifest.PSObject.Properties.Match('runStartUtc').Count)    { ConvertTo-WckUtc ($manifest.runStartUtc) }    else { $null }
        $genUtc   = if ($manifest.PSObject.Properties.Match('generatedUtc').Count)    { ConvertTo-WckUtc ($manifest.generatedUtc) }    else { $null }
        $afterUtc = if ($manifest.PSObject.Properties.Match('afterSnapshotUtc').Count){ ConvertTo-WckUtc ($manifest.afterSnapshotUtc) } else { $null }
        $resetUtc = if ($manifest.PSObject.Properties.Match('resetUtc').Count)        { ConvertTo-WckUtc ($manifest.resetUtc) }        else { $null }
        if ($null -eq $startUtc) { $reasons.Add("cell-manifest runStartUtc missing/unparseable (cannot anchor freshness) — fail-closed") }
        if ($null -eq $genUtc)   { $reasons.Add("cell-manifest generatedUtc missing/unparseable (cannot prove run window) — fail-closed") }
        if ($null -eq $afterUtc) { $reasons.Add("cell-manifest afterSnapshotUtc missing/unparseable (cannot prove ordering) — fail-closed") }
        if ($null -eq $resetUtc) { $reasons.Add("cell-manifest resetUtc missing/unparseable (cannot prove ordering) — fail-closed") }

        # --- CHECK 7 (FIX-C): AFTER snapshot must precede the reset --------------------------
        if (($null -ne $afterUtc) -and ($null -ne $resetUtc)) {
            if (-not ($afterUtc -lt $resetUtc)) {
                $reasons.Add("ordering FAIL: afterSnapshotUtc ($($afterUtc.ToString('o'))) is NOT before resetUtc ($($resetUtc.ToString('o'))) — AFTER witness may be post-reset (vacuous)")
            }
        }

        # --- CHECK 3 (part B / FIX-B freshness): every file's write-time in the run window ---
        # The run window starts at runStartUtc with only a small backward tolerance for
        # timestamp granularity/copy lag; stale pre-run artifacts still fail.
        if ($null -ne $startUtc) {
            $slack = [timespan]::FromMinutes([math]::Max(1, $FreshnessSlackMinutes))
            $backSlack = [timespan]::FromMinutes([math]::Max(0, $FreshnessBackSlackMinutes))
            $windowEnd = $startUtc
            foreach ($t in @($genUtc, $afterUtc, $resetUtc)) { if (($null -ne $t) -and ($t -gt $windowEnd)) { $windowEnd = $t } }
            $lo = $startUtc.Subtract($backSlack)
            $hi = $windowEnd.Add($slack)
            foreach ($f in $requiredFiles) {
                $w = (Get-Item -LiteralPath $f).LastWriteTimeUtc
                if ($w -lt $lo -or $w -gt $hi) {
                    $reasons.Add("stale/mistimed artifact: $(Split-Path $f -Leaf) written $($w.ToString('o')) is OUTSIDE run window [$($lo.ToString('o')) .. $($hi.ToString('o'))]")
                }
            }
        }

        # --- CHECK 2/5: dir-hashes are structural evidence, not Uninstall ground truth -------
        # Both hash snapshots must be valid JSON arrays of {Path,Sha256}. Empty arrays are
        # legal: the Uninstall cell installs and removes apps inside one harness step, so the
        # independent uninstall witness is the AFTER registry export below.
        function Test-HashShape($obj, [string]$label) {
            # ConvertFrom-Json yields a single object (not array) for a 1-element array, and
            # $null for '[]'. Normalize to an array of records.
            $arr = @($obj)
            foreach ($rec in $arr) {
                if ($null -eq $rec) { continue }
                if (-not ($rec.PSObject.Properties.Match('Path').Count) -or
                    -not ($rec.PSObject.Properties.Match('Sha256').Count)) {
                    $reasons.Add("$label dir-hashes record malformed (missing Path/Sha256)")
                    return $false
                }
                if ([string]$rec.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
                    $reasons.Add("$label dir-hashes Sha256 not a 64-hex digest: '$($rec.Sha256)'")
                    return $false
                }
            }
            return $true
        }
        # $null (from '[]') is a legal EMPTY snapshot for either phase; this is a shape check.
        $beforeArr = @($beforeHashes | Where-Object { $_ })
        $afterArr  = @($afterHashes  | Where-Object { $_ })
        $beforeShapeOk = Test-HashShape $beforeHashes 'before'
        $afterShapeOk  = Test-HashShape $afterHashes  'after'

        # Keep variables live so malformed JSON/shape checks above are not optimized away by
        # future edits while making the intended "shape only" contract obvious.
        $null = $beforeShapeOk, $afterShapeOk, $beforeArr, $afterArr

        function Get-WckJsonValue($obj, [string]$name) {
            if ($null -ne $obj -and $obj.PSObject.Properties.Match($name).Count) { return $obj.$name }
            return $null
        }

        function Test-WckHashSnapshotContains([object[]]$Hashes, [string]$Path, [string]$Sha256) {
            if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Sha256)) { return $false }
            foreach ($h in @($Hashes)) {
                if ($null -eq $h) { continue }
                if ([string]::Equals([string](Get-WckJsonValue $h 'Path'), $Path, [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals([string](Get-WckJsonValue $h 'Sha256'), $Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            return $false
        }

        if ($module -eq 'Uninstall') {
            # --- CHECK 2 (ground truth): AFTER registry witness for Uninstall -------------------
            $afterRegText = Get-Content -LiteralPath $afterReg -Raw
            $afterRegEntries = @((ConvertFrom-WckRegExport -Text $afterRegText) | Where-Object { $_ })
            $registryTargets = Get-WckUninstallRegistryTargets -Persona ($(if ($persona -eq 'B') { 'B' } else { 'A' }))
        $executeTargets = @($registryTargets | Where-Object { $_.Role -eq 'executed' })
        foreach ($t in $executeTargets) {
            $hits = @(Find-WckRegEntriesForTarget -Entries $afterRegEntries -Target $t)
            if ($hits.Count -gt 0) {
                $hitList = (($hits | ForEach-Object { $_.KeyName }) -join ', ')
                $reasons.Add("registry witness FAIL: executed target '$($t.Id)' still present in AFTER registry snapshot ($hitList)")
            }
        }
        foreach ($t in @($registryTargets | Where-Object { $_.Role -eq 'required-not-executed' })) {
            $hits = @(Find-WckRegEntriesForTarget -Entries $afterRegEntries -Target $t)
            if ($hits.Count -eq 0) {
                $reasons.Add("registry witness FAIL: required non-executed target '$($t.Id)' missing from AFTER registry snapshot (vacuous/no-op)")
            }
        }
        $focus = @($ue.focus)
        $execs = @($ue.executions)
        $jsonPass = Get-StrictJsonBool $ue 'pass' 'uninstall-e2e-evidence'
        foreach ($f in $focus) {
            if ($f.PSObject.Properties.Match('found').Count) {
                $null = Get-StrictJsonBool $f 'found' "focus[$($f.targetId)]"
            }
        }
        foreach ($e0 in $execs) {
            if ($e0.PSObject.Properties.Match('skipped').Count) {
                $null = Get-StrictJsonBool $e0 'skipped' "executions[$($e0.targetId)]"
            }
            $null = Get-StrictJsonBool $e0 'removedFromRegistry' "executions[$($e0.targetId)]"
        }
        foreach ($t in $executeTargets) {
            $e = $execs | Where-Object { $_.targetId -eq $t.Id } | Select-Object -First 1
            if (-not $e) { $reasons.Add("harness JSON has no execution entry for '$($t.Id)'") ; continue }
            $removedFromRegistry = Get-StrictJsonBool $e 'removedFromRegistry' "executions[$($t.Id)]"
            if (-not $removedFromRegistry) { $reasons.Add("harness JSON registry-gone=False for '$($t.Id)'") }
        }
        if (-not $ue.PSObject.Properties.Match('branchMismatch').Count) {
            $reasons.Add("uninstall-e2e-evidence missing branchMismatch field")
        } elseif (@($ue.branchMismatch).Count -gt 0) {
            $reasons.Add("harness JSON branchMismatch not empty: $((@($ue.branchMismatch) | ConvertTo-Json -Compress -Depth 4))")
        }

        if ($persona -eq 'A') {
            # --- CHECK 5 (negative control): a protected/absent app must NOT be green --------
            $negControls = @(
                @{ Id = '7zip';      Allowed = @('BLOCK') },
                @{ Id = 'notepadpp'; Allowed = @('MANUAL') }
            )
            $sawNonGreen = $false
            foreach ($n in $negControls) {
                $f = $focus | Where-Object { $_.targetId -eq $n.Id } | Select-Object -First 1
                if (-not $f) { $reasons.Add("negative-control focus entry missing for '$($n.Id)'"); continue }
                if ($n.Allowed -notcontains [string]$f.classification) {
                    $reasons.Add("negative control NOT discriminating: '$($n.Id)' classification '$($f.classification)' expected one of [$($n.Allowed -join ',')]")
                } else {
                    $sawNonGreen = $true
                }
            }
            if (-not $sawNonGreen) {
                $reasons.Add("suite is all-green (no discriminating negative control) — refusing to pass")
            }
        } elseif ($persona -eq 'B') {
            Test-WckPersonaBUninstallDisposition -Disposition $personaDisposition -SeedManifest $personaSeed -Evidence $ue -Focus $focus -Executions $execs -Reasons $reasons
        }

        # --- CHECK 4: JSON verdict == process exit code -------------------------------------
        $exitRaw = (Get-Content -LiteralPath $exitPath -Raw).Trim()
        $exitCode = 0
        if (-not [int]::TryParse($exitRaw, [ref]$exitCode)) {
            $reasons.Add("harness-exitcode.txt is not an integer: '$exitRaw'")
        }
        $jsonVerdict = [string]$ue.verdict
        if (($exitCode -eq 0) -ne $jsonPass) {
            $reasons.Add("JSON/exit mismatch: exitCode=$exitCode but json.pass=$jsonPass")
        }
        if ($exitCode -eq 0 -and $jsonVerdict -ne 'PASS') {
            $reasons.Add("JSON/exit mismatch: exitCode=0 but verdict='$jsonVerdict' (expected PASS)")
        }
        if ($exitCode -ne 0 -and $jsonVerdict -eq 'PASS') {
            $reasons.Add("JSON/exit mismatch: verdict=PASS but exitCode=$exitCode")
        }

        # --- CHECK 4b (FIX-B): result.txt is a valid banner, not random text ----------------
        $resultRaw = (Get-Content -LiteralPath $ueResult -Raw).Trim()
        $validBanners = @('PASS','FAIL')
            if ($validBanners -notcontains $resultRaw.ToUpperInvariant()) {
                $reasons.Add("uninstall-e2e-result.txt is not a valid banner ('PASS'/'FAIL'): '$resultRaw'")
            } elseif (($exitCode -eq 0) -and ($resultRaw.ToUpperInvariant() -ne 'PASS')) {
                $reasons.Add("result.txt banner '$resultRaw' disagrees with exit 0 (expected PASS)")
            }
        }
        elseif ($module -eq 'Migration') {
            $jsonPass = Get-StrictJsonBool $mig 'pass' 'migration-e2e-evidence'
            if (-not $jsonPass) { $reasons.Add("migration-e2e-evidence pass is not true") }
            if ([string](Get-WckJsonValue $mig 'failReason')) {
                $reasons.Add("migration-e2e-evidence failReason is populated: $([string](Get-WckJsonValue $mig 'failReason'))")
            }
            foreach ($mismatchField in 'branchMismatch','mismatches') {
                $mv = Get-WckJsonValue $mig $mismatchField
                if ($null -ne $mv -and @($mv).Count -gt 0) {
                    $reasons.Add("migration-e2e-evidence $mismatchField not empty: $((@($mv) | ConvertTo-Json -Compress -Depth 6))")
                }
            }

            $verifications = @((Get-WckJsonValue $mig 'verifications') | Where-Object { $_ })
            $matched = @($verifications | Where-Object {
                $skipped = Get-WckJsonValue $_ 'skipped'
                $shaMatch = Get-WckJsonValue $_ 'shaMatch'
                ($skipped -is [bool]) -and (-not $skipped) -and ($shaMatch -is [bool]) -and $shaMatch
            })

            $restoreSkips = @((Get-WckJsonValue $mig 'restorePlanSkips') | Where-Object { $_ })
            $discordSkip = $restoreSkips | Where-Object {
                [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), 'discord', [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string](Get-WckJsonValue $_ 'reason'), 'NotAllowListed', [System.StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            if (-not $discordSkip) {
                $reasons.Add("migration honest-defer gate FAIL: Discord restore skip NotAllowListed not proven")
            }
            $discordRestored = $matched | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), 'discord', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if ($discordRestored) {
                $reasons.Add("migration honest-defer gate FAIL: Discord appears as restored SHA-match")
            }

            if ($persona -eq 'B') {
                $backupProofs = @((Get-WckJsonValue $mig 'backupProofs') | Where-Object { $_ })
                foreach ($recipe in 'discord','launcher') {
                    $proof = $backupProofs | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), $recipe, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                    if (-not $proof) { $reasons.Add("Persona-B migration backup proof missing: $recipe"); continue }
                    $bytes = Get-WckJsonValue $proof 'bytes'
                    if ($bytes -isnot [long] -and $bytes -isnot [int]) { $reasons.Add("Persona-B migration backup proof '$recipe' bytes is not numeric"); continue }
                    if ([int64]$bytes -lt 1) { $reasons.Add("Persona-B migration backup proof '$recipe' has no real bytes") }
                }

                $honest = @((Get-WckJsonValue $mig 'honestDispositions') | Where-Object { $_ })
                $launcher = $honest | Where-Object {
                    [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), 'launcher', [System.StringComparison]::OrdinalIgnoreCase) -and
                    ([string](Get-WckJsonValue $_ 'warning') -match 'sadece re-add')
                } | Select-Object -First 1
                if (-not $launcher) { $reasons.Add("Persona-B migration honesty FAIL: launcher re-add warning missing") }
                $chrome = $honest | Where-Object {
                    [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), 'chrome-abe', [System.StringComparison]::OrdinalIgnoreCase) -and
                    ([string](Get-WckJsonValue $_ 'warning') -match 'restore-edilemez|sync')
                } | Select-Object -First 1
                if (-not $chrome) { $reasons.Add("Persona-B migration honesty FAIL: Chrome ABE sync/restore warning missing") }
            } else {
                if ($matched.Count -lt 2) {
                    $reasons.Add("migration SHA-match gate FAIL: restoredShaMatches=$($matched.Count), expected >= 2")
                }
                foreach ($recipe in 'git.config','anthropic.claude-code') {
                    $hit = $matched | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'recipeId'), $recipe, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                    if (-not $hit) { $reasons.Add("migration SHA-match gate FAIL: required restored recipe missing: $recipe") }
                }

                $corroborated = 0
                foreach ($m in $matched) {
                    $dest = [string](Get-WckJsonValue $m 'destPath')
                    $sha = [string](Get-WckJsonValue $m 'restoredSha')
                    if ([string]::IsNullOrWhiteSpace($sha)) { $sha = [string](Get-WckJsonValue $m 'manifestSha') }
                    $afterHas = Test-WckHashSnapshotContains -Hashes $afterArr -Path $dest -Sha256 $sha
                    $beforeHas = Test-WckHashSnapshotContains -Hashes $beforeArr -Path $dest -Sha256 $sha
                    if ($afterHas -and -not $beforeHas) { $corroborated++ }
                }
                if ($corroborated -lt 2) {
                    $reasons.Add("migration second-channel dir-hash FAIL: corroborated restore targets=$corroborated, expected >= 2 with before-absent/after-present")
                }
            }

            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
            $zip = $null
            try {
                $zip = [System.IO.Compression.ZipFile]::OpenRead($migZip)
                $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
                if ($persona -eq 'B') {
                    if (-not ($entryNames | Where-Object { $_ -eq 'discord/Local State' } | Select-Object -First 1)) {
                        $reasons.Add("Persona-B migration zip gate FAIL: discord/Local State backup missing")
                    }
                    if (-not ($entryNames | Where-Object { $_ -eq 'launcher/libraryfolders.vdf' } | Select-Object -First 1)) {
                        $reasons.Add("Persona-B migration zip gate FAIL: launcher/libraryfolders.vdf backup missing")
                    }
                } else {
                    if (-not ($entryNames | Where-Object { $_ -match '(^|/)SKILL\.md$' } | Select-Object -First 1)) {
                        $reasons.Add("migration zip gate FAIL: required positive entry **/SKILL.md missing")
                    }
                    if (-not ($entryNames | Where-Object { $_ -match '(^|/)note\.md$' } | Select-Object -First 1)) {
                        $reasons.Add("migration zip gate FAIL: required positive entry **/note.md missing")
                    }
                }
                $blockedLeafNames = @('id_rsa','app.secret','blob.dat','temp.dat','2026-06-21.snap','todo.txt','f_000001','data_0')
                foreach ($entry in $zip.Entries) {
                    $name = $entry.FullName.Replace('\','/')
                    $leaf = ($name -split '/')[-1]
                    if ($blockedLeafNames -contains $leaf) {
                        $reasons.Add("migration zip prune FAIL: forbidden seeded entry present: $name")
                    }
                    foreach ($pat in $script:WckForbiddenPatterns) {
                        if ($name -match $pat) { $reasons.Add("forbidden token pattern in ZIP ENTRY NAME '$name' (/$pat/)") }
                    }
                    if ($entry.Length -gt 0 -and $entry.Length -le 1048576) {
                        try {
                            $sr = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8, $true)
                            try { $entryText = $sr.ReadToEnd() } finally { $sr.Dispose() }
                            foreach ($pat in $script:WckForbiddenPatterns) {
                                if ($entryText -match $pat) { $reasons.Add("forbidden token pattern in ZIP ENTRY '$name' (/$pat/)") }
                            }
                        }
                        catch { $reasons.Add("unreadable ZIP entry during secret-scan '$name' — $($_.Exception.Message)") }
                    }
                }
            }
            catch { $reasons.Add("migration zip gate FAIL: cannot inspect migration-export.zip — $($_.Exception.Message)") }
            finally { if ($zip) { $zip.Dispose() } }
        }
        elseif ($module -eq 'Clean') {
            $jsonPass = Get-StrictJsonBool $clean 'pass' 'clean-e2e-evidence'
            if (-not $jsonPass) { $reasons.Add("clean-e2e-evidence pass is not true") }
            $verdict = [string](Get-WckJsonValue $clean 'verdict')
            if ($verdict -ne 'PASS') { $reasons.Add("clean-e2e-evidence verdict expected PASS, got '$verdict'") }

            $decisions = @((Get-WckJsonValue $clean 'gateDecisions') | Where-Object { $_ })
            $requiredDecisionNames = @(
                'P1-junk-in-plan',
                'P2-protected-refused',
                'P3-value-delete-allowed',
                'P3-key-delete-refused',
                'P1-ground-truth',
                'P2-ground-truth',
                'P3-ground-truth'
            )
            foreach ($name in $requiredDecisionNames) {
                $d = $decisions | Where-Object { [string]::Equals([string](Get-WckJsonValue $_ 'name'), $name, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                if (-not $d) { $reasons.Add("clean ground-truth gate FAIL: missing decision '$name'"); continue }
                $expected = Get-WckJsonValue $d 'expectedAllowed'
                $actual = Get-WckJsonValue $d 'actualAllowed'
                if ($expected -isnot [bool] -or $actual -isnot [bool]) {
                    $reasons.Add("clean ground-truth gate FAIL: decision '$name' has non-bool expected/actual")
                    continue
                }
                if ($expected -ne $actual) {
                    $reasons.Add("clean ground-truth gate FAIL: decision '$name' expectedAllowed=$expected actualAllowed=$actual")
                }
            }

            $beforeRegText = Get-Content -LiteralPath $beforeReg -Raw
            $afterRegText = Get-Content -LiteralPath $afterReg -Raw
            if ($beforeRegText -notmatch 'WCK-CleanE2E-Witness') {
                $reasons.Add("clean registry second-channel FAIL: witness Run value missing from BEFORE run-registry.reg")
            }
            if ($afterRegText -match 'WCK-CleanE2E-Witness') {
                $reasons.Add("clean registry second-channel FAIL: witness Run value still present in AFTER run-registry.reg")
            }
            $junkBefore = @($beforeArr | Where-Object { [string](Get-WckJsonValue $_ 'Path') -match '\\WCK-CleanE2E-Witness\\junk\.txt$' })
            $junkAfter = @($afterArr | Where-Object { [string](Get-WckJsonValue $_ 'Path') -match '\\WCK-CleanE2E-Witness\\junk\.txt$' })
            if ($junkBefore.Count -eq 0) {
                $reasons.Add("clean dir-hash second-channel FAIL: witness junk file missing from BEFORE dir-hashes")
            }
            if ($junkAfter.Count -gt 0) {
                $reasons.Add("clean dir-hash second-channel FAIL: witness junk file still present in AFTER dir-hashes")
            }
        }

        # --- CHECK 7: final VM Off + expected checkpoint ------------------------------------
        $finalStateVal = if ($final.PSObject.Properties.Match('state').Count) { [string]$final.state } else { '' }
        $finalCp        = if ($final.PSObject.Properties.Match('checkpoint').Count) { [string]$final.checkpoint } else { '' }
        if ($finalStateVal -ne 'Off') {
            $reasons.Add("final VM state is '$finalStateVal' (expected 'Off') — dirty/running VM left behind")
        }
        $expectedCp = if ($manifest.PSObject.Properties.Match('checkpoint').Count) { [string]$manifest.checkpoint } else { 'baseline-clean' }
        if ($finalCp -ne $expectedCp) {
            $reasons.Add("final checkpoint '$finalCp' != expected '$expectedCp' (reset not proven)")
        }

        # --- CHECK 6: forbidden secret/token patterns + host-username in any artifact -------
        # Scan BOTH file CONTENTS and file NAMES across the whole evidence tree. An unreadable
        # file is treated as a FAIL (fail-closed) — a planted secret must not hide behind a lock.
        $allFiles = Get-ChildItem -LiteralPath $EvidenceDir -Recurse -File -ErrorAction SilentlyContinue
        foreach ($file in $allFiles) {
            foreach ($pat in $script:WckForbiddenPatterns) {
                if ($file.Name -match $pat) { $reasons.Add("forbidden token pattern in FILE NAME '$($file.Name)' (/$pat/)") }
            }
            $content = $null
            try { $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop }
            catch { $reasons.Add("unreadable artifact during secret-scan (fail-closed): '$($file.Name)' — $($_.Exception.Message)"); continue }
            if ($null -eq $content) { $content = '' }
            foreach ($pat in $script:WckForbiddenPatterns) {
                if ($content -match $pat) { $reasons.Add("forbidden token pattern in '$($file.Name)' (/$pat/)") }
            }
            if (-not [string]::IsNullOrWhiteSpace($HostUsername)) {
                $unameRe = '\b' + [regex]::Escape($HostUsername) + '\b'
                if ($file.Name -match $unameRe) { $reasons.Add("host-username leaked in FILE NAME '$($file.Name)'") }
                if ($content -match $unameRe)   { $reasons.Add("host-username leaked in '$($file.Name)'") }
            }
        }

        # --- digest over EVERY accepted artifact under the evidence dir (FIX-B) -------------
        # Not a hardcoded list: digest covers the whole tree (relative-path + hash), so an
        # extra or changed file moves the digest. Computed even on FAIL (audit fingerprint).
        $digestFiles = @(Get-ChildItem -LiteralPath $EvidenceDir -Recurse -File -ErrorAction Stop |
                         Where-Object { $_.Name -ne 'campaign-verdict.json' } |   # exclude our own future output
                         ForEach-Object { $_.FullName })
        $digest = Get-WckEvidenceDigest -Files $digestFiles -BaseDir $EvidenceDir

        $pass = ($reasons.Count -eq 0)
        return [pscustomobject]@{ Pass = $pass; Reasons = $reasons.ToArray(); Digest = $digest }
    }
    catch {
        # FAIL-CLOSED: any exception => Pass=$false, never swallowed into a pass.
        $reasons.Add("verifier exception (fail-closed): $($_.Exception.Message)")
        return [pscustomobject]@{ Pass = $false; Reasons = $reasons.ToArray(); Digest = $digest }
    }
}
