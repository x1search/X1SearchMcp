# Copyright (c) 2026 X1 Discovery, Inc.
#
# Licensed under the MIT License (copyright only). See the LICENSE file in
# the repository root for the full license text.
#
# This license does not grant, and shall not be construed as granting, any
# patent rights. See the PATENTS file in the repository root.

<#
.SYNOPSIS
  Extract readable text from a cached Office file (.docx / .pptx / .xlsx). FALLBACK ONLY — try
  x1_get_content mode "content" first (see the /x1 skill).

.DESCRIPTION
  x1_get_content mode "content" extracts .pptx natively too, not just .docx (verified
  2026-07-16) — it's the default for any indexed item, so try that first. Reach for this script
  only when mode "content" comes back empty, errors, or times out: this is still the more likely
  path for large .xlsx workbooks, or an attachment whose content hasn't been extracted yet. Call
  x1_get_content with mode "preview" to have X1 cache the real file locally, then run this
  script on the returned path to pull readable text WITHOUT streaming the file through context.

  Office files are just ZIP archives of XML. This reads the text runs:
    .docx -> word/document.xml         (<w:t>, grouped by paragraph)
    .pptx -> ppt/slides/slideN.xml     (<a:t>, one block per slide)
    .xlsx -> xl/worksheets/sheetN.xml  (<row>/<c>/<v>, resolving t="s" against
                                        xl/sharedStrings.xml by <si> index, t="inlineStr"
                                        against <is>, t="str"/"b"/"e"/"n" (or no t attribute)
                                        directly; sheets are processed in filename order,
                                        which is not necessarily the visible tab order)

.PARAMETER Path
  Full path to the .docx / .pptx / .xlsx file (e.g. the "preview" path from x1_get_content).

.PARAMETER MaxChars
  Optional cap on output length. 0 (default) = unlimited.

.EXAMPLE
  powershell -File extract-office-text.ps1 -Path "C:\...\X1 Search\MSMailPreview\...\Deck.pptx"
  powershell -File extract-office-text.ps1 -Path "C:\...\report.docx" -MaxChars 6000
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$MaxChars = 0
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "File not found: $Path"
    exit 1
}

$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()

function Decode([string]$s) {
    return ($s -replace '&amp;', '&' -replace '&lt;', '<' -replace '&gt;', '>' `
                -replace '&#39;', "'" -replace '&apos;', "'" -replace '&quot;', '"')
}

function Read-Entry($zip, [string]$name) {
    $e = $zip.GetEntry($name)
    if (-not $e) { return $null }
    $sr = New-Object System.IO.StreamReader($e.Open())
    try { return $sr.ReadToEnd() } finally { $sr.Close() }
}

# Resolves the text of an OOXML CT_Rst container (shared-string <si> or inline <is>):
# either a bare <t> child, or a sequence of <r> (run) children each with their own <t>.
# Direct children only - never recurses into <rPh> (phonetic hint) siblings of <r>, which
# also contain <t> but must not be concatenated into the value or (for <si>) miscounted
# as extra shared-string entries, which would misalign every later index lookup.
function Get-RstText([System.Xml.XmlElement]$container) {
    if ($null -eq $container) { return '' }
    $directT = @($container.ChildNodes | Where-Object { $_.LocalName -eq 't' })
    if ($directT.Count -gt 0) { return $directT[0].InnerText }
    $runs = @($container.ChildNodes | Where-Object { $_.LocalName -eq 'r' })
    $parts = foreach ($r in $runs) {
        $rt = @($r.ChildNodes | Where-Object { $_.LocalName -eq 't' })
        if ($rt.Count -gt 0) { $rt[0].InnerText }
    }
    return ($parts -join '')
}

$sb = New-Object System.Text.StringBuilder
$zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
try {
    switch ($ext) {
        '.docx' {
            $xml = Read-Entry $zip 'word/document.xml'
            if ($xml) {
                foreach ($p in [regex]::Matches($xml, '<w:p[ >].*?</w:p>', 'Singleline')) {
                    $runs = [regex]::Matches($p.Value, '<w:t[^>]*>(.*?)</w:t>', 'Singleline') |
                            ForEach-Object { $_.Groups[1].Value }
                    $line = (Decode (($runs -join ''))).Trim()
                    if ($line) { [void]$sb.AppendLine($line) }
                }
            }
        }
        '.pptx' {
            $slides = $zip.Entries |
                Where-Object { $_.FullName -match '^ppt/slides/slide\d+\.xml$' } |
                Sort-Object { [int]([regex]::Match($_.Name, '\d+').Value) }
            $n = 0
            foreach ($s in $slides) {
                $n++
                $sr = New-Object System.IO.StreamReader($s.Open())
                $xml = $sr.ReadToEnd(); $sr.Close()
                $runs = [regex]::Matches($xml, '<a:t>(.*?)</a:t>', 'Singleline') |
                        ForEach-Object { $_.Groups[1].Value }
                $text = (Decode (($runs -join ' '))).Trim()
                [void]$sb.AppendLine("----- Slide $n -----")
                [void]$sb.AppendLine($(if ($text) { $text } else { '(no text)' }))
                [void]$sb.AppendLine('')
            }
        }
        '.xlsx' {
            # Shared-string table, indexed by <si> position (used by cells with t="s").
            # Uses [xml] rather than regex here: resolving indices through nested runs/rPh
            # correctly needs real element structure, not text scraping. GetElementsByTagName /
            # ChildNodes filtered by LocalName work fine under SpreadsheetML's default xmlns
            # (unprefixed elements keep their local name) without needing a namespace manager.
            $sharedStrings = @()
            $ssXml = Read-Entry $zip 'xl/sharedStrings.xml'
            if ($ssXml) {
                $ssDoc = [xml]$ssXml
                $siNodes = @($ssDoc.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'si' })
                $sharedStrings = @($siNodes | ForEach-Object { Get-RstText $_ })
            }

            $sheets = $zip.Entries |
                Where-Object { $_.FullName -match '^xl/worksheets/sheet\d+\.xml$' } |
                Sort-Object { [int]([regex]::Match($_.Name, '\d+').Value) }

            if ($sheets.Count -eq 0) {
                [void]$sb.AppendLine('(no worksheet XML found in this workbook)')
            }

            $n = 0
            foreach ($sheetEntry in $sheets) {
                $n++
                [void]$sb.AppendLine("----- Sheet $n -----")
                try {
                    $sr = New-Object System.IO.StreamReader($sheetEntry.Open())
                    $sheetXml = $sr.ReadToEnd(); $sr.Close()
                    $sheetDoc = [xml]$sheetXml
                    $sheetData = @($sheetDoc.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'sheetData' })
                    $rows = if ($sheetData.Count -gt 0) { @($sheetData[0].ChildNodes | Where-Object { $_.LocalName -eq 'row' }) } else { @() }

                    $any = $false
                    foreach ($row in $rows) {
                        $cells = @($row.ChildNodes | Where-Object { $_.LocalName -eq 'c' })
                        $vals = foreach ($c in $cells) {
                            $t = $c.GetAttribute('t')
                            $vNode  = @($c.ChildNodes | Where-Object { $_.LocalName -eq 'v' })
                            $isNode = @($c.ChildNodes | Where-Object { $_.LocalName -eq 'is' })
                            switch ($t) {
                                's' {
                                    if ($vNode.Count -gt 0) {
                                        $idx = 0
                                        if ([int]::TryParse($vNode[0].InnerText, [ref]$idx) -and $idx -ge 0 -and $idx -lt $sharedStrings.Count) {
                                            $sharedStrings[$idx]
                                        } else { "(shared string #$($vNode[0].InnerText) out of range)" }
                                    }
                                }
                                'inlineStr' { if ($isNode.Count -gt 0) { Get-RstText $isNode[0] } }
                                'str'  { if ($vNode.Count -gt 0) { $vNode[0].InnerText } }
                                'b'    { if ($vNode.Count -gt 0) { if ($vNode[0].InnerText -eq '1') { 'TRUE' } else { 'FALSE' } } }
                                'e'    { if ($vNode.Count -gt 0) { $vNode[0].InnerText } }
                                default {
                                    # '' (no t attribute) or 'n' -> raw numeric literal; no date formatting.
                                    if ($vNode.Count -gt 0) { $vNode[0].InnerText }
                                }
                            }
                        }
                        $vals = @($vals | Where-Object { $_ -ne $null -and $_ -ne '' })
                        if ($vals.Count -gt 0) { [void]$sb.AppendLine(($vals -join "`t")); $any = $true }
                    }
                    if (-not $any) { [void]$sb.AppendLine('(no data rows found in this sheet)') }
                }
                catch {
                    [void]$sb.AppendLine("(could not parse Sheet $n : $($_.Exception.Message))")
                }
                [void]$sb.AppendLine('')
            }
        }
        default {
            Write-Error "Unsupported type '$ext'. This script handles .docx, .pptx, and .xlsx."
            exit 2
        }
    }
}
finally {
    $zip.Dispose()
}

$out = $sb.ToString()
if ($MaxChars -gt 0 -and $out.Length -gt $MaxChars) {
    $out = $out.Substring(0, $MaxChars) + "`n... (truncated at $MaxChars characters)"
}
Write-Output $out
