function Get-VerifiedArchive {
    param([string[]]$Urls, [string]$ExpectedHash, [string]$Archive)
    if (-not (Test-Path -LiteralPath $Archive)) {
        $partial = "$Archive.partial-$([Guid]::NewGuid().ToString('N'))"
        try {
            $downloaded = $false
            foreach ($url in $Urls) {
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    try {
                        Invoke-WebRequest -Uri $url -OutFile $partial -TimeoutSec 120
                        $downloaded = $true
                        break
                    } catch {
                        Write-Warning "Download attempt $attempt failed: $url ($($_.Exception.Message))"
                        if ($attempt -lt 3) { Start-Sleep -Seconds $attempt }
                    }
                }
                if ($downloaded) { break }
            }
            if (-not $downloaded) { throw "All verified download sources failed: $($Urls -join ', ')" }
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedHash) { throw "Archive SHA-256 mismatch: $url" }
            Move-Item -LiteralPath $partial -Destination $Archive
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    }
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedHash) { throw "Cached archive SHA-256 mismatch: $Archive" }
    return $Archive
}
