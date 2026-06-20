#requires -Version 5.1
<#
.SYNOPSIS
    Windows Care Kit (WCK) · Step 4 Tier B AUTHORITATIVE pass-gate.

.DESCRIPTION
    Closes the vacuous-pass hole (council critic FIX-2, 2026-06-20): a `dotnet test`
    exit code of 0 is NOT sufficient proof that the Tier B destructive tests ran.
    A [DisposableFact] that is statically SKIPPED (because the disposable-machine
    opt-in did not take effect) still yields exit 0 — a green that proves nothing.

    This gate parses the TRX and FAILS LOUDLY unless ALL of the required Tier B
    tests (B1 scheduled-task, B2 service, B3 HKLM registry) are present AND have
    outcome=Passed. It is invoked twice with the SAME logic:
      * inside the sandbox by step4-run.cmd (so the in-VM banner cannot lie), and
      * on the host by Claude when reading C:\WCK-SandboxOutput\step4.trx.

.PARAMETER Trx
    Path to step4.trx produced by `dotnet test --logger trx`.

.OUTPUTS
    Exit 0  = B1/B2/B3 each ran and Passed (Tier B genuinely exercised).
    Exit 3  = TRX missing or empty (tests never ran / no results returned).
    Exit 4  = one or more Tier B tests MISSING / NotExecuted / Failed (vacuous or real fail).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Trx,
    [string[]]$Required = @(
        'B1_ScheduledTask_disable_then_delete_through_the_real_gate_and_adapter',
        'B2_Service_disable_then_delete_through_the_real_gate_and_adapter',
        'B3_HKLM_registry_value_then_key_delete_through_the_real_gate_writes_a_reg_backup'
    )
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg, [int]$code) {
    Write-Host "[step4-gate] FAIL: $msg" -ForegroundColor Red
    exit $code
}

if (-not (Test-Path -LiteralPath $Trx)) {
    Fail "TRX not found at '$Trx' (tests did not run / no results came back)." 3
}

[xml]$doc = Get-Content -LiteralPath $Trx -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

$results = @($doc.SelectNodes('//t:UnitTestResult', $ns))
if ($results.Count -eq 0) {
    Fail "TRX has no UnitTestResult entries." 3
}

$passed = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
$failed = @($results | Where-Object { $_.outcome -eq 'Failed' }).Count
$other  = @($results | Where-Object { $_.outcome -ne 'Passed' -and $_.outcome -ne 'Failed' }).Count
Write-Host ("[step4-gate] results={0}  passed={1}  failed={2}  notExecuted/other={3}" -f `
    $results.Count, $passed, $failed, $other)

$bad = @()
foreach ($name in $Required) {
    $match = @($results | Where-Object { $_.testName -like "*$name*" })
    if ($match.Count -eq 0) {
        $bad += "${name}: MISSING (never ran -> statically skipped off a disposable machine?)"
    } elseif ($match[0].outcome -ne 'Passed') {
        $bad += ("{0}: {1}" -f $name, $match[0].outcome)
    }
}

if ($bad.Count -gt 0) {
    Fail ("Tier B destructive tests did NOT run+pass:`n  - " + ($bad -join "`n  - ")) 4
}

Write-Host "[step4-gate] PASS: B1/B2/B3 all present and Passed (Tier B genuinely ran)." -ForegroundColor Green
exit 0
