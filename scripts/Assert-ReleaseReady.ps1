function Assert-ReleaseReady {
    param($Evidence, $Run, $Jobs, [string]$Commit, [string]$ArchiveHash)
    if ($Evidence.result -cne 'PASS' -or $Evidence.sourceCommit -cne $Commit -or $Evidence.archiveSHA256 -cne $ArchiveHash -or
        $Evidence.packageSmoke.result -cne 'PASS' -or $Evidence.packageSmoke.archiveSHA256 -cne $ArchiveHash -or $Evidence.desktop.result -cne 'PASS' -or $Evidence.desktop.archiveSHA256 -cne $ArchiveHash) {
        throw 'Source, package or desktop evidence is missing or belongs to another build.'
    }
    foreach ($name in @('Core','Engine','UI')) {
        $tests = @($Evidence.localTests | Where-Object name -CEQ $name)
        if ($tests.Count -ne 1 -or $tests[0].total -le 0 -or $tests[0].failed -ne 0 -or $tests[0].skipped -ne 0 -or $tests[0].passed -ne $tests[0].total) {
            throw "Complete local $name tests must pass without skips."
        }
    }
    if ($Run.head_sha -cne $Commit -or $Run.status -cne 'completed' -or $Run.conclusion -cne 'success') { throw 'CI must pass for the exact source commit.' }
    foreach ($os in @('windows-2022','ubuntu-22.04','macos-15-intel')) {
        foreach ($git in @('2.40.0','2.56.0')) {
            $job = @($Jobs | Where-Object name -CEQ "Git $git ($os)")
            if ($job.Count -ne 1 -or $job[0].status -cne 'completed' -or $job[0].conclusion -cne 'success') { throw "Missing or failed CI grid: Git $git ($os)" }
        }
    }
}
