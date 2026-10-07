param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$word = $null
$document = $null
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $document = $word.Documents.Open((Join-Path $OutputDirectory 'word-120-structural.docx'), $false, $true)
    $document.Repaginate()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $document.ExportAsFixedFormat((Join-Path $OutputDirectory 'word-120-headings.pdf'),17,$false,0,0,1,1,0,$true,$true,1,$true,$true,$false)
    $timer.Stop()
    [pscustomobject]@{Version=$word.Version; Build=$word.Build; Pages=$document.ComputeStatistics(2); ExportSeconds=$timer.Elapsed.TotalSeconds} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'word-export.json')
} finally {
    if ($null -ne $document) { $document.Close(0); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document) }
    if ($null -ne $word) { $word.Quit(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word) }
}
