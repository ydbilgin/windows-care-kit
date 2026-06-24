#requires -Version 7.0
<#
.SYNOPSIS
    Shared, FAIL-CLOSED host-safety precondition module for the WCK VM test campaign
    (F0.5a, decision C-16 / FIX-1).

.DESCRIPTION
    Dot-source this module before any force-op:

        . "$PSScriptRoot\Guard-WckDisposable.ps1"

    Its whole purpose is to make a destructive operation IMPOSSIBLE against the wrong
    target. Every assertion is fail-closed: on ANY ambiguity (marker unreadable, VM
    missing, path resolves outside the campaign root, suspicious symlink/`..`) it
    THROWS — it never returns "safe" on doubt.

    Used as a shared precondition by Build-WckVM (rebuild), the future campaign-clone
    script, the cell-runner, and the verifier. The contract:
      * Assert-WckDisposableVM   — a VM may be Stop-VM -TurnOff / Remove-VM ONLY if its
                                   .Notes carry the campaign-GUID marker.
      * Assert-WckPathUnderRoot  — a path may be Remove-Item'd ONLY if it canonically
                                   resolves UNDER the campaign root (after `..`/symlink
                                   resolution).
      * Assert-WckCampaignReady  — combined precondition: marker + VM present + Off-able
                                   + the expected checkpoint exists.

    HOST SAFETY: this module reads only; it performs NO Stop-VM / Remove-VM / Remove-Item
    itself. It is the gate the CALLERS must pass before they do.

.NOTES
    Marker format written into a campaign VM's .Notes field:
        WCK-CAMPAIGN:<guid>
    The GUID is any RFC-4122 GUID the campaign assigns (baseline VM gets one too).
#>

Set-StrictMode -Version Latest

# Canonical marker prefix + the well-formed-GUID shape the marker must carry.
$script:WckCampaignMarkerPrefix = 'WCK-CAMPAIGN:'
$script:WckCampaignMarkerRegex  = '(?im)^\s*WCK-CAMPAIGN:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*$'

# Default campaign root. EVERY destructive file op must contain its target under this.
$script:WckCampaignRoot = 'F:\WCK-VM\campaign'

function Get-WckCampaignMarker {
    <#
      .SYNOPSIS  Extracts the WCK-CAMPAIGN:<guid> marker from a VM's .Notes, fail-closed.
      .OUTPUTS   The GUID string if a single well-formed marker is present; otherwise $null.
                 Multiple/conflicting markers -> $null (ambiguous => refuse).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowNull()] [string] $Notes)

    if ([string]::IsNullOrWhiteSpace($Notes)) { return $null }
    $matches = [regex]::Matches($Notes, $script:WckCampaignMarkerRegex)
    if ($matches.Count -ne 1) { return $null }   # zero OR more-than-one => ambiguous => refuse
    return $matches[0].Groups[1].Value
}

function Assert-WckDisposableVM {
    <#
      .SYNOPSIS  THROWS unless VM <VMName> carries exactly one campaign-GUID marker in .Notes.
      .DESCRIPTION
        The single gate every Stop-VM -TurnOff / Remove-VM in the campaign MUST pass.
        A VM whose Notes lack the marker (a real user VM, a name-collision, a half-built
        clone) can never be reached by a force-op. Fail-closed: VM missing, Hyper-V module
        absent, Notes unreadable, or a non-unique marker all THROW.

        -VMObject lets a caller (or self-test) pass an already-fetched object/fixture so the
        guard logic can be exercised without a live Hyper-V host.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [object] $VMObject
    )

    $vm = $VMObject
    if (-not $vm) {
        if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
            throw "Assert-WckDisposableVM: Hyper-V module unavailable — refusing (fail-closed) to treat '$VMName' as disposable."
        }
        try { $vm = Get-VM -Name $VMName -ErrorAction Stop }
        catch { throw "Assert-WckDisposableVM: VM '$VMName' not found — refusing (fail-closed). $($_.Exception.Message)" }
    }
    else {
        # FIX-E: an injected fixture (test-only) must still describe the VM it claims to. A
        # name mismatch means the caller is reasoning about the wrong target — fail-closed.
        $injName = if ($vm.PSObject.Properties.Match('Name').Count -gt 0) { [string]$vm.Name } else { $null }
        if ([string]::IsNullOrWhiteSpace($injName) -or ($injName -ne $VMName)) {
            throw "Assert-WckDisposableVM: injected VM object name '$injName' != requested -VMName '$VMName' — REFUSING (fail-closed)."
        }
    }

    # Pull .Notes defensively (fixture may be a PSCustomObject without the property).
    $notes = $null
    if ($vm.PSObject.Properties.Match('Notes').Count -gt 0) { $notes = [string]$vm.Notes }

    $guid = Get-WckCampaignMarker -Notes $notes
    if (-not $guid) {
        throw "Assert-WckDisposableVM: VM '$VMName' has NO unique '$($script:WckCampaignMarkerPrefix)<guid>' marker in .Notes — REFUSING destructive op (fail-closed). Not a campaign VM."
    }
    return $guid
}

function Resolve-WckRealPath {
    <#
      .SYNOPSIS  Dereferences EVERY existing reparse-point (junction/symlink) on a path.
      .DESCRIPTION
        Walks the path from its drive root down to the leaf. For each existing segment it
        asks the filesystem for the segment's real target ([System.IO.Directory]::ResolveLinkTarget
        / [System.IO.File]::ResolveLinkTarget). If the segment IS a reparse-point, the walk
        re-bases on the link's REAL target (resolved to a full, rooted path) and keeps going.
        Non-existent tail segments are appended lexically AFTER all existing links have been
        dereferenced — so an attacker cannot hide an escape behind a symlinked ancestor with
        a not-yet-created leaf.

        This is the FIX-A core: a junction under the campaign root whose target leaves the
        root is followed to its real destination, so the StartsWith test in the caller sees
        the TRUE location, not the lexical in-root path.

      .OUTPUTS   The fully reparse-resolved absolute path (string). Fail-closed: throws if a
                 link target cannot be read.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $InputPath)

    $full = [System.IO.Path]::GetFullPath($InputPath)

    # Split into the drive-root prefix + ordered segments below it.
    $rootPrefix = [System.IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrEmpty($rootPrefix)) {
        throw "Resolve-WckRealPath: '$InputPath' is not a rooted path — REFUSING (fail-closed)."
    }
    $remainder = $full.Substring($rootPrefix.Length)
    $segments  = $remainder.Split([char[]]@('\','/'), [System.StringSplitOptions]::RemoveEmptyEntries)

    # Iteratively build the real path. Re-base whenever a segment is a reparse-point whose
    # target we can read; cap iterations to defeat a link cycle (fail-closed on overrun).
    $current = [System.IO.Path]::TrimEndingDirectorySeparator($rootPrefix)
    $maxHops = 256
    $hops = 0
    foreach ($seg in $segments) {
        $candidate = Join-Path $current $seg
        if (Test-Path -LiteralPath $candidate) {
            # Resolve a reparse-point (junction OR symlink, dir OR file) to its real target.
            $linkTarget = $null
            try {
                $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
                if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                    # .Target is the link's stored target; ResolveLinkTarget(final=$true) chases chains.
                    try {
                        if ($item.PSIsContainer) {
                            $resolved = [System.IO.Directory]::ResolveLinkTarget($candidate, $true)
                        } else {
                            $resolved = [System.IO.File]::ResolveLinkTarget($candidate, $true)
                        }
                        if ($resolved) { $linkTarget = $resolved.FullName }
                    } catch {
                        throw "Resolve-WckRealPath: reparse-point '$candidate' could not be fully resolved — REFUSING (fail-closed). $($_.Exception.Message)"
                    }
                    if (-not $linkTarget) {
                        throw "Resolve-WckRealPath: reparse-point '$candidate' target unreadable — REFUSING (fail-closed)."
                    }
                }
            } catch {
                throw "Resolve-WckRealPath: cannot inspect segment '$candidate' — REFUSING (fail-closed). $($_.Exception.Message)"
            }

            if ($linkTarget) {
                $hops++
                if ($hops -gt $maxHops) {
                    throw "Resolve-WckRealPath: too many reparse hops on '$InputPath' (possible link cycle) — REFUSING (fail-closed)."
                }
                # Re-base on the link's real (absolute) target, then continue the walk.
                $current = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($linkTarget))
            } else {
                $current = $candidate
            }
        } else {
            # Non-existent tail: nothing left to dereference, append the rest lexically.
            $current = $candidate
        }
    }
    return [System.IO.Path]::GetFullPath($current)
}

function Assert-WckPathUnderRoot {
    <#
      .SYNOPSIS  THROWS unless <Path> REALLY resolves UNDER <Root> (campaign root).
      .DESCRIPTION
        Mandatory before any Remove-Item in the campaign. FIX-A: dereferences EVERY existing
        reparse-point (junction/symlink) on BOTH the root and the target — re-basing on each
        link's real filesystem destination — BEFORE the StartsWith descendant test. A junction
        that lives under the campaign root but points OUTSIDE it is followed to its true target
        and REJECTED.

        Fail-closed: the campaign root MUST exist (no lexical fallback — a missing root THROWS);
        a path equal to the root, outside the root, on another drive, with an unreadable link
        target, or one whose canonical form cannot be computed all THROW. A non-existent leaf is
        allowed (the campaign creates/clears paths that may not exist yet) PROVIDED every existing
        ancestor really stays under the root.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Root = $script:WckCampaignRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Assert-WckPathUnderRoot: empty path — REFUSING (fail-closed)."
    }

    # Canonicalize the ROOT: it MUST exist (no lexical fail-open fallback — FIX-A). Resolve
    # any reparse-point in the root too, so a symlinked root is compared by its real target.
    if (-not (Test-Path -LiteralPath $Root)) {
        throw "Assert-WckPathUnderRoot: campaign root '$Root' does not exist — REFUSING (fail-closed); a real, deliberate root is required (no lexical fallback)."
    }
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $rootReal  = Resolve-WckRealPath -InputPath $Root
    $rootExpected = [System.IO.Path]::TrimEndingDirectorySeparator($rootFull)
    $rootResolved = [System.IO.Path]::TrimEndingDirectorySeparator($rootReal)
    if (-not $rootResolved.Equals($rootExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Assert-WckPathUnderRoot: campaign root '$Root' resolves to '$rootReal' instead of itself — REFUSING linked campaign root (fail-closed)."
    }
    $rootCanon = (([System.IO.Path]::TrimEndingDirectorySeparator($rootReal)) + [System.IO.Path]::DirectorySeparatorChar)

    # Canonicalize the TARGET with full reparse-point dereferencing of every existing segment.
    $targetReal = Resolve-WckRealPath -InputPath $Path
    if ([string]::IsNullOrWhiteSpace($targetReal)) {
        throw "Assert-WckPathUnderRoot: could not canonicalize '$Path' — REFUSING (fail-closed)."
    }

    $targetCompare = [System.IO.Path]::TrimEndingDirectorySeparator($targetReal) + [System.IO.Path]::DirectorySeparatorChar

    # STRICT descendant: must START WITH root + separator, and must NOT equal the root itself.
    if ($targetCompare.Equals($rootCanon, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Assert-WckPathUnderRoot: path '$Path' resolves to the campaign root itself — REFUSING (fail-closed); only descendants may be removed."
    }
    if (-not $targetCompare.StartsWith($rootCanon, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Assert-WckPathUnderRoot: path '$Path' (real target '$targetReal') is OUTSIDE campaign root '$rootCanon' — REFUSING destructive op (fail-closed)."
    }
    return $targetReal
}

function Assert-WckCampaignReady {
    <#
      .SYNOPSIS  Combined precondition: marker + VM present + Off-able + expected checkpoint.
      .DESCRIPTION
        Called once at cell-runner start (and before any reset). Throws on the first failed
        invariant. -VMObject / -Checkpoints let the self-test inject fixtures.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [string] $Checkpoint = 'baseline-clean',
        [object] $VMObject,
        [object[]] $Checkpoints
    )

    # 1. marker gate (also throws if VM missing / Hyper-V absent)
    $guid = Assert-WckDisposableVM -VMName $VMName -VMObject $VMObject

    # 2. expected checkpoint must exist
    $cps = $Checkpoints
    if ($null -eq $cps) {
        if (-not (Get-Command Get-VMCheckpoint -ErrorAction SilentlyContinue)) {
            throw "Assert-WckCampaignReady: Hyper-V checkpoint cmdlets unavailable — refusing (fail-closed)."
        }
        $cps = @(Get-VMCheckpoint -VMName $VMName -ErrorAction SilentlyContinue)
    }
    $hasCp = @($cps | Where-Object { $_ -and ($_.Name -eq $Checkpoint) }).Count -gt 0
    if (-not $hasCp) {
        throw "Assert-WckCampaignReady: expected checkpoint '$Checkpoint' not found on '$VMName' — REFUSING (fail-closed)."
    }

    return [pscustomobject]@{
        VMName        = $VMName
        CampaignGuid  = $guid
        Checkpoint    = $Checkpoint
        Ready         = $true
    }
}
