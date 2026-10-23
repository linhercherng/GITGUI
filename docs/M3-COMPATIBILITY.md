# M3 Git 與倉庫格式相容性

版本基準查核日：**2026-10-23**。最低版本維持 **Git 2.40**，不因 reftable 測試而提高最低需求。

## 可重現的六格 CI

`.github/workflows/ci.yml` 對下列每格執行相同的 locked restore、Release build 與完整 solution tests：

| 作業系統／架構 | 最低版本 | 本次固定的最新穩定版 |
| --- | --- | --- |
| Windows Server 2022 x64 | Git for Windows `2.40.0.windows.1` | `2.56.0.windows.3` |
| Ubuntu 22.04 x64 | Git `2.40.0` | `2.56.0` |
| macOS 15 Intel | Git `2.40.0` | `2.56.0` |

Windows 最新版格另保留 AskPass apphost 與實際 Windows portable ZIP smoke；舊版格不會被新 Portable Git 偷換。每格分別輸出含 Git 版本的 TRX artifact 名稱。工作排程與結果須以對應 commit 的六格 CI 為準，設定矩陣本身不代表已通過。

「最新」是本次查核後的固定值，不是在每次 CI 動態下載最新版本。Git 官方來源目錄列出 [Git 2.56.0](https://www.kernel.org/pub/software/scm/git/)；[Git for Windows 官方最新發行](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.3) 為 2.56.0(3)，沿用 v0.3.0 封裝所附版本。更新版本時必須一起更新 manifest、雜湊、矩陣與驗證紀錄。

安裝腳本：[Install-CompatibilityGit.ps1](https://github.com/linhercherng/GITGUI/blob/main/scripts/Install-CompatibilityGit.ps1)。唯一版本清單：[compatibility-git.json](https://github.com/linhercherng/GITGUI/blob/main/scripts/compatibility-git.json)。Windows 使用官方完整 Portable Git；Unix 從 kernel.org 官方 release tar.xz 編譯，保留 shell／Perl／remote helpers，以作業系統與架構及 manifest 雜湊分開快取。下載先核對 SHA-256；版本或 PATH 斷言不符就失敗。

| 原始輸入 | SHA-256 |
| --- | --- |
| Git 2.40.0 官方 tar.xz | `b17a598fbf58729ef13b577465eb93b2d484df1201518b708b5044ff623bf46d` |
| Git 2.56.0 官方 tar.xz | `26c56c296b38c0695b26fa95f475f1d01704d2d38e73465ca30b0b2f5dc789d3` |
| PortableGit 2.40.0 x64 | `9031c171fccbf30113b8a9b93b9580b4aca8502dcf2b22c5cd3205212e895b13` |
| PortableGit 2.56.0.3 x64 | `4fc27b779829a36df77911e3f4dea09c66c790dd6c5f7dfa3b6a5479f1fe354f` |

Windows 雜湊取自 [2.40.0 官方發行頁](https://github.com/git-for-windows/git/releases/tag/v2.40.0.windows.1) 與上述最新發行頁；Unix 雜湊為查核日經 HTTPS 下載官方 tar.xz 後計算並固定的內容雜湊，不冒稱另做過 GPG 簽章驗證。

## 相同流程的格式矩陣

`CompatibilityMatrixTests` 在每個可用組合跑相同真實 Git 流程：

| 格式 | Git 2.40.0 | Git 2.56.0 |
| --- | --- | --- |
| SHA-1 + files | 必須通過 | 必須通過 |
| SHA-256 + files | 必須通過 | 必須通過 |
| SHA-1 + reftable | 明確 skip：Git 本身不支援 | 必須通過 |
| SHA-256 + reftable | 明確 skip：Git 本身不支援 | 必須通過 |

流程包含 init、中文／空白檔名與二進位資料、stage、commit、status、提交圖、blame／歷史、staged Diff、混合 staged/worktree/untracked 的 raw 快照、分支與 tag 復原、`git gc --prune=now`、Undo／Redo，以及相同格式 bare remote 的 push／fetch／clone。檢查選定 Git 確實符合 CI 的完整版本字串；一般 SHA-1/files 可用 libgit2，其他格式必須經 CLI 降級。

每個可用格式也執行相同的 M3／P2 代表操作：

| 工具 | 格式矩陣中的實際驗證 |
| --- | --- |
| Worktree | 建立 detached worktree、CLI 清單確認、以非 force 操作移除乾淨 worktree；Windows legacy reftable 的明確部分失敗與手動清理路徑見下節 |
| Sparse checkout | cone 選取 included 目錄、excluded 檔案消失、快照 Undo 還原檔案、index flags 與 sparse 設定 |
| Bisect | 以額外空提交形成範圍、start 產生 BISECT_START、reset 回到原 HEAD 與 main 分支 |
| LFS | repository-local install、驗證本地 filter 設定、track／untrack pattern 與 .gitattributes |

這些代表操作不等同所有 P2 動作都做過四格式驗收。真正子模組的 clone／initialize／update／deinitialize 生命週期，以及 LFS 物件的 fetch／pull／實體內容往返，仍由獨立 SHA-1/files 測試涵蓋；不延伸宣稱已對 SHA-256／reftable 完成這兩類資料傳輸測試。

### Windows legacy 刪除語意與 reftable 工作樹的已知邊界

固定的 Git for Windows `2.56.0.windows.3` 在 Windows Server 2022 CI 的 SHA-1／SHA-256 reftable 倉庫，可能已移除工作目錄，卻因管理目錄的 reftable 映射檔仍開啟而回報 `failed to delete '.git/worktrees/<id>': Invalid argument`。這是純 Git CLI 也可重現的非原子部分失敗；再次執行非 force remove 會回報該路徑不是 worktree，不能當成可靠重試。

本機 Windows 11 同版 Git 的一般刪除可成功；以 Git 官方的 `GIT_TEST_LEGACY_DELETE=1` 測試開關可確定性重現較舊 Windows 的行為。原因對照官方 [Windows 刪除實作](https://github.com/git-for-windows/git/blob/v2.56.0.windows.3/compat/mingw.c#L557)、[worktree remove](https://github.com/git-for-windows/git/blob/v2.56.0.windows.3/builtin/worktree.c#L1425) 與 [reftable 檔案關閉需求](https://github.com/git-for-windows/git/blob/v2.56.0.windows.3/refs/reftable-backend.c#L515)。測試開關只用於隔離驗證，不是建議使用者改動的設定。

GITGUI 對符合條件的原生失敗回傳 `Success=false`、`WorktreeCleanupIncomplete`，顯示工作目錄已移除、管理資料清理未完成及後續操作，不假裝完整成功，也不自動 prune 或 force。先檢查 `git worktree prune --dry-run --verbose --expire=now`，確認列出的過期管理資料，再由使用者明確執行「Prune」。Prune 會處理倉庫中所有符合條件的過期項目，應先檢查清單。

格式矩陣只接受此結構化錯誤，且必須同時滿足 Windows／reftable、目標工作目錄已不存在、移除前捕獲的管理目錄仍存在、其 gitdir 檔已不存在、dry-run 列出該精確管理項目。測試在隔離 fixture 中另外明確確認並呼叫 Prune，再驗證管理資料已清除、HEAD 與全部 refs 完全相同，繼續測試其他 P2 操作。這些斷言通過表示部分失敗與使用者主動復原處理正確，不代表原生 remove 在 legacy Windows 已成功；其他錯誤或格式不會因此略過。

新矩陣用 `git init -h` 探測 `--ref-format`，以兩項獨立的 xUnit Fact Skip 呈現不可用的兩格；不把不支援格式算成成功。舊有 reftable 專項測試也只在 Git 回報不支援初始化選項時停止，並非略過可用格式的失敗。files 初始化不傳較新才加入的 `--ref-format=files`，因此最低版本仍可執行完整套件。

## 本機單項驗證紀錄

2026-10-23 在 Windows x64，以已核對雜湊的官方 Portable Git 執行 `CompatibilityMatrixTests`：

| 實際 `git --version` | 通過 | 明確 skip | 失敗 |
| --- | --- | --- | --- |
| `git version 2.40.0.windows.1` | 3 | 2（reftable） | 0 |
| `git version 2.56.0.windows.3` | 5 | 0 | 0 |

兩者均另驗證 PATH 與應用程式 runner 選到固定版本。這是相容性專項結果，不能代替最終來源的完整 solution tests、六格 CI 或桌面驗收；Unix 安裝流程與另外兩個 OS 的成功證據仍以對應 commit 的 CI 為準。

## 本機重現

Windows PowerShell 7（安裝腳本只調整目前 shell 與 CI 後續步驟，不修改系統 PATH）：

```powershell
./scripts/Install-CompatibilityGit.ps1 -Version 2.40.0 -Destination "$env:TEMP/gitgui-compat-240"
dotnet test tests/GitGui.Engine.Tests/GitGui.Engine.Tests.csproj `
  --filter FullyQualifiedName~CompatibilityMatrixTests --artifacts-path "$env:TEMP/gitgui-compat-build-240"
```

測試最新版改為 `-Version 2.56.0` 與不同的目的／輸出目錄。在 Ubuntu／macOS 也以 PowerShell 7 執行同一腳本；需 C compiler、make、tar/xz、zlib、curl、expat、Perl 與 OS 開發標頭。Unix 安裝前綴不可含空白；這項限制只屬於上游 source build，測試倉庫仍涵蓋中文／空白路徑。

Git 2.40 是最低相容性回歸基準，不建議把舊版作為日常下載；正式 Windows ZIP 保持附最新已驗證的 2.56.0.3。測試不操作真實遠端帳號，bare remote 均在隔離暫存目錄。

## 證據邊界

最低 OS 規格仍為 Windows 10/11、macOS 13+、Ubuntu 22.04+；六格 CI 不等於在每個最低 OS 做過桌面驗收。此矩陣固定為 x64，macOS 使用明確的 `macos-15-intel` 標籤，避免把現在代表 Apple Silicon 的一般標籤誤報為 Intel。[GitHub runner 清單](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) 可查架構與映像。Windows ARM64／Linux ARM64 決策與原生依賴證據另見 [ARM 決策](M3-ARM-DECISION.md)。
