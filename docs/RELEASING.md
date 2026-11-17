# Windows 免安裝版發布

在 Windows x64、PowerShell 7、`global.json` 指定的 .NET SDK 與已登入的 GitHub CLI 環境執行。主程式採 MIT；Portable Git 與依賴另依隨附授權提供完整對應來源。本流程不攜帶使用者設定、帳號或金鑰。

## 1. 版本與來源

只修改 `Directory.Build.props` 的 `Version`。同步更新 README、操作手冊與發行說明，再執行 `scripts/Render-UserGuide.ps1`。保留既有真實標示截圖，檢查 HTML、圖片及引用文件。

提交全部來源與文件，在乾淨 checkout 建置；有既有未追蹤研究資料時，保留原目錄，另用乾淨 checkout。不要把 `artifacts/` 或個人資料提交。建置腳本拒絕未提交來源與不一致的版號。

## 2. 完整測試與六格 CI

```powershell
dotnet restore GitGui.sln --locked-mode
dotnet build GitGui.sln -c Release --no-restore
foreach ($name in @('Core','Engine','UI')) {
    dotnet test "tests/GitGui.$name.Tests/GitGui.$name.Tests.csproj" -c Release --no-build `
        --logger "trx;LogFileName=$name.trx" --results-directory artifacts/release-evidence
    if ($LASTEXITCODE -ne 0) { throw "$name tests failed" }
}
./tests/Test-VerifiedArchive.ps1
./tests/Test-ReleaseWorkflow.ps1
./tests/Test-PerformanceComparison.ps1
./tests/Test-NativePhaseIndex.ps1
./tests/Test-DesktopPerformanceAcceptance.ps1
./tests/Test-StaticJsonContracts.ps1 -OutputDirectory artifacts/release-evidence/static-json
```

推送 `codex/` 分支後，等待 `.github/workflows/ci.yml` 在同一提交的六格全部成功：Windows 2022、Ubuntu 22.04、macOS 15 Intel × Git 2.40.0／2.56.0。失敗或略過不能當作通過。下載採原 SHA-256、有限重試及官方 kernel.org 鏡站；快取也重新校驗。Engine 的整套執行上限為 30 分鐘，單一測試停滯偵測仍為 5 分鐘；產品效能門檻不變。

## 3. 建置與驗證實際 ZIP

下列路徑是範例，輸出資料夾必須尚不存在。保留原始輸出及測試報告。

```powershell
$version = ./scripts/Get-ReleaseVersion.ps1
./scripts/Build-WindowsPortable.ps1 -OutputDirectory artifacts/release-build
$archive = "$PWD/artifacts/release-build/GITGUI-$version-win-x64-portable.zip"
./tests/Test-WindowsPortable.ps1 -Archive $archive
```

將最後輸出的 `VERIFICATION:` 檔案複製成 `artifacts/release-evidence/package-verification.json`。套件測試在中文空白路徑重新解壓、隔離外部 Git／.NET，驗證共用 DLL 位元組、Git 操作、AskPass、手冊與授權。該次測試為注入測試驅動器的副本；桌面驗證必須使用建置輸出的原始程式資料夾。

```powershell
./tests/Test-WindowsDesktop.ps1 `
    -AppDirectory "artifacts/release-build/GITGUI-$version-win-x64-portable" `
    -Archive $archive -OutputDirectory artifacts/release-evidence/desktop
```

桌面驗證需要可互動 Windows 工作階段，分別測試 apphost 與一般 `wscript.exe`／`GITGUI.vbs`，核對套件位元組與內附 Git；另以兩個真實程序驗證單一實例及終止後重新啟動。只會終止測試建立的程序，使用隔離資料目錄。這些是功能／啟動檢查，正式效能量測另依 `M4-ACCEPTANCE.md`、`Measure-DesktopPerformance.ps1` 與 `Assert-DesktopPerformance.ps1` 執行，不能以啟動探針代替。

## 4. 組裝附件與發布

把實際 TRX、套件與桌面報告放入 evidence 目錄，另保留 CI run ID 及其他診斷輸出。依 `Collect-PortableGitSources.ps1` 與來源清單收集對應的 copyleft source ZIP；Git pin 不變可重用原先已核對的完整來源 ZIP。準備本版 Release 說明，列明驗證範圍及限制。

```powershell
./scripts/Prepare-ReleaseAssets.ps1 -Archive $archive `
    -EvidenceDirectory artifacts/release-evidence `
    -CopyleftSources '<對應完整來源 ZIP>' -NotesFile '<本版說明.md>' `
    -OutputDirectory '<新的附件輸出目錄>'
./scripts/Publish-GitHubRelease.ps1 -AssetsManifest '<輸出>/release-assets.json' `
    -CiRunId <本次六格全綠的 run ID> -ValidateOnly
./scripts/Publish-GitHubRelease.ps1 -AssetsManifest '<輸出>/release-assets.json' `
    -CiRunId <同一 run ID>
```

組裝腳本核對來源與版號、完整 TRX、精確 ZIP 的功能／桌面報告、GPL 來源 SHA-256，再產生獨立離線手冊、驗證證據 ZIP、`verification.json`、`release-info.json` 與 `SHA256SUMS.txt`。共七個附件。`release-assets.json` 是本機發布輸入，記錄附件路徑、大小及 SHA-256。

發布腳本重新向 GitHub 查詢指定 workflow／提交的六格結果，驗證本機附件未變動，正常 fast-forward 推送 main，建立草稿並上傳。逐項核對 GitHub 回傳的大小與 SHA-256 後才發布為 stable／latest，再核對 tag 提交。既有 tag 會停止，避免覆蓋已發布版本；若上傳中斷，先檢查保留的草稿，不要直接重跑覆蓋。

最後把七個附件複製到本機下載目錄，核對主 ZIP 雜湊與 Release 相同，向使用者提供 Release 連結、主 ZIP 與離線手冊的完整本機路徑。未簽署的執行檔與正式效能數據的受測版本範圍，須在發行說明中保留。
