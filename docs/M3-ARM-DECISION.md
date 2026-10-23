# M3 決策：Windows ARM64／Linux ARM64 暫不列正式發行平台

決策日期：**2026-10-23**。v0.4.0 繼續提供 Windows x64 portable ZIP。Windows ARM64 與 Linux ARM64 保留為可評估的來源建置目標；目前不提供正式套件，也不宣稱做過 ARM 硬體或桌面驗收。

這是驗收與支援範圍的決策。已鎖定的原生套件具有 ARM64 資產，並非只因為 C# 可編譯就推定可發行。

## 已查核的套件資產

來源是目前 `packages.lock.json` 與 locked restore 的原始 NuGet 套件目錄；在查核日讀取實際 binary header，Windows ARM64 的 PE machine 應為 `0xAA64`，Linux ARM64 的 ELF `e_machine` 應為 `183`（AArch64）。存在資產只證明套件提供此架構，不證明應用程式已正常載入或運作。

| 原生相依／版本 | Windows ARM64 資產 | Linux ARM64 資產 |
| --- | --- | --- |
| LibGit2Sharp.NativeBinaries 2.0.324 | `runtimes/win-arm64/native/` | `runtimes/linux-arm64/native/` |
| SkiaSharp 2.88.9 native assets | `SkiaSharp.NativeAssets.Win32/runtimes/win-arm64/native/libSkiaSharp.dll` | `SkiaSharp.NativeAssets.Linux/runtimes/linux-arm64/native/libSkiaSharp.so` |
| SQLitePCLRaw.lib.e_sqlite3 2.1.13 | `runtimes/win-arm64/native/e_sqlite3.dll` | `runtimes/linux-arm64/native/libe_sqlite3.so` |
| Avalonia.Angle.Windows.Natives 2.1.25547.20250602 | `runtimes/win-arm64/native/av_libglesv2.dll` | 不適用；Linux 使用平台顯示／繪製相依 |

NuGet 版本與內容雜湊均由鎖定檔固定。[LibGit2Sharp.NativeBinaries 官方原始碼](https://github.com/libgit2/libgit2sharp.nativebinaries/tree/2b95030f15e55dfe70e9fdbb2b3d304a5c95ad23)、[SkiaSharp 官方專案](https://github.com/mono/SkiaSharp)、[SQLitePCL.raw 官方專案](https://github.com/ericsink/SQLitePCL.raw) 可查來源；沒有以目前快取存在資產取代實機測試。

Git for Windows 目前也提供 [2.56.0(3) ARM64 Portable Git](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.3)。其系統需求與 x64 不同：[官方需求](https://github.com/git-for-windows/git-for-windows.github.io/blob/main/content/requirements.md) 說明 Windows ARM64 需 Windows 11，shell／POSIX 元件的模擬鏈也需考慮。因此不可把 x64 的 Windows 10 支援直接延伸到 ARM64。

## 正式支援前的必要證據

1. 在實際 Windows ARM64 與 Ubuntu ARM64 主機以對應 RID 做 locked self-contained publish，確認沒有混入 x64 native DLL／SO；保存 binary 清單與雜湊。
2. 在限制系統 PATH 與 .NET 搜尋的環境，驗證 packaged Git、helpers、SSH／AskPass、持續 shell、原生 libgit2、SQLite、Avalonia／Skia 的實際載入。
3. 跑相同 SHA-1／SHA-256、files／reftable 與寫入／快照復原矩陣，外加真實桌面啟動、CJK、Diff、衝突編輯、顯示／輸入及視窗正常關閉。
4. 另外驗證 Windows Job Object 的子程序清理，及 Linux ARM64 的 glibc／圖形／字型相依；確認 ARM Git distribution 的 helpers 路徑與 shell 架構。
5. 定義 ARM 的 CI runner、封裝／安裝、最低 OS 與支援人力，再以獨立變更核准發行。

目前的六格 CI 明確使用 Windows／Ubuntu／macOS Intel x64。它不提供 Windows ARM64／Linux ARM64 的成功證據；未以跨編譯產出或 NuGet 資產存在宣稱 ARM 可用。
