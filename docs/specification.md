# Git GUI 用戶端架構規格

Oct 23, 2026 · @Will Lin

## 1. 目的與範圍

本產品是跨平台桌面 Git 用戶端，以 git CLI 作為唯一的寫入引擎，讓使用者不必打指令就能完成日常版本控制，且所有破壞性操作都能一鍵復原。本文件定義其架構、模組邊界、介面、功能範圍與驗收指標，供實作與審查使用。專案名稱暫以 `GitGui` 代稱。

**目標**

- 日常操作（P0、P1 功能）全部可在 UI 完成，不需開終端機。
- 任何會改寫本地歷史或捨棄變更的操作，都能透過快照還原。
- 大型倉庫（約 10 萬筆 commit、5 萬個檔案）仍維持流暢操作。
- 與 git CLI 完全相容：同一倉庫可同時用 CLI 與本產品操作，互不干擾。
- 每個 UI 動作都能顯示等效的 git 指令，兼顧透明度與教學。

**非目標（v1 不做）**

- 不重新實作 Git 物件庫、packfile 或傳輸協定。
- 不實作託管平台功能（Merge Request、Issue、CI），只提供開啟網頁的連結。
- 不發明 Git 沒有的版本控制概念（例如虛擬分支）。

**目標使用者**：熟悉 Git 概念但不想記指令的開發者，以及需要視覺化引導的 Git 新手。

## 2. 技術選型

採用 C# (.NET 8) + Avalonia：寫入與網路操作一律經 git CLI，高頻讀取用 LibGit2Sharp。GUI 的效能瓶頸在 Git 本身的操作時間與大量清單的繪製，兩者都不取決於語言；選團隊熟悉的技術，開發速度最快。

| 層面 | 選擇 | 理由 |
| --- | --- | --- |
| 執行環境 | .NET 8 (LTS)，SDK 8.0.404 | 與開發機現有環境一致；2026-11-10 終止支援，屆時只需改目標框架即可升級到 .NET 10 |
| UI 框架 | Avalonia 11 | Windows、macOS、Linux 共用一套 XAML；寫法接近 WPF |
| MVVM | CommunityToolkit.Mvvm | 以 source generator 產生屬性與命令，減少樣板程式碼 |
| 相依注入 | Autofac（或 Microsoft.Extensions.DependencyInjection） | 與既有專案慣例一致 |
| 寫入與網路引擎 | git CLI，最低 2.40 | hooks、憑證、LFS、簽章與 CLI 行為完全一致 |
| 讀取引擎 | [LibGit2Sharp](https://nubrowse.com/packages/libgit2sharp) 0.32 | log、diff、blame 不需每次啟動行程 |
| 文字與 Diff 編輯器 | AvaloniaEdit | 語法高亮，可處理大檔案 |
| 本地資料 | SQLite (Microsoft.Data.Sqlite) | 存放倉庫清單、快照索引、設定 |
| 日誌 | Serilog | 滾動檔案、結構化欄位 |

**評估過但未採用的方案**

- Rust + Tauri 2 + Svelte（GitButler 的做法）：效能與安裝檔體積最好，但需學 Rust，且要維護兩種語言。只有在要做自有版本控制概念時才值得。
- Electron + TypeScript（GitHub Desktop、GitKraken 的做法）：UI 開發最快，但記憶體佔用大，大型倉庫的流暢度較差。

## 3. 分層架構總覽

系統分為 UI、應用、引擎介面、引擎實作四層。命令由上往下傳遞，狀態變更則以事件往上回報。

&#91;embedded content: 分層架構 · 四層加外部相依，寫入與讀取分流\]

寫入與網路操作只走左側的 CLI 後端（強調框），右側函式庫後端只負責讀取。檔案監看、SQLite、日誌等基礎設施由 `GitGui.Infrastructure` 提供，Core 透過介面使用，圖中未畫出。

## 4. 專案結構與模組切分

方案分成 7 個專案，依賴方向只能往 Core 收斂：UI 和各引擎都依賴 Core，Core 不依賴任何 UI 或外部函式庫。這樣引擎可以替換，Core 也能脫離 UI 單獨測試。

```
GitGui.sln
├─ src/
│  ├─ GitGui.App               啟動、DI 組裝、主題、單一實例控制
│  ├─ GitGui.UI                Views、ViewModels、對話框、指令面板
│  ├─ GitGui.Controls          提交圖、Diff 檢視、衝突編輯器（自繪控制項）
│  ├─ GitGui.Core              領域模型、引擎介面、操作模型、快照、風險分級
│  ├─ GitGui.Engine.Cli        GitProcessRunner、porcelain 輸出解析器
│  ├─ GitGui.Engine.Lib        LibGit2Sharp 讀取後端
│  └─ GitGui.Infrastructure    檔案監看、SQLite、Serilog、設定
└─ tests/
   ├─ GitGui.Core.Tests        單元測試（無 IO）
   ├─ GitGui.Engine.Tests      整合測試（真實 git、暫存倉庫）
   ├─ GitGui.UI.Tests          Avalonia.Headless 畫面測試
   └─ GitGui.Benchmarks        BenchmarkDotNet 效能基準
```

**依賴規則**

- `UI`、`Controls` → `Core`
- `Engine.Cli`、`Engine.Lib`、`Infrastructure` → `Core`
- `App` 參照全部專案，只負責組裝。
- 禁止 `Core` 參照 Avalonia、LibGit2Sharp 或 `System.Diagnostics.Process`；這條規則用架構測試（例如 NetArchTest）在 CI 中強制檢查。

## 5. Git 引擎層

引擎依職責拆成三個介面，由路由器決定每個呼叫交給 CLI 後端或函式庫後端。上層程式碼只看得到介面，不知道實際由誰執行。

```csharp
public interface IRepoReader
{
    Task<RepoStatus> GetStatusAsync(CancellationToken ct);
    IAsyncEnumerable<CommitInfo> GetLogAsync(LogQuery query, CancellationToken ct);
    Task<FileDiff> GetDiffAsync(DiffQuery query, CancellationToken ct);
    Task<IReadOnlyList<RefInfo>> GetRefsAsync(CancellationToken ct);
    Task<BlameResult> GetBlameAsync(string path, string rev, CancellationToken ct);
    Task<InProgressState> GetInProgressStateAsync(CancellationToken ct); // merge/rebase/cherry-pick/bisect
}

public interface IRepoWriter
{
    Task<OpResult> StageAsync(StageRequest req, CancellationToken ct);      // 檔案、hunk、行
    Task<OpResult> UnstageAsync(StageRequest req, CancellationToken ct);
    Task<OpResult> CommitAsync(CommitRequest req, CancellationToken ct);    // 含 amend、簽章
    Task<OpResult> SwitchAsync(SwitchRequest req, CancellationToken ct);
    Task<OpResult> MergeAsync(MergeRequest req, CancellationToken ct);
    Task<OpResult> RebaseAsync(RebaseRequest req, CancellationToken ct);    // 含互動式 todo
    Task<OpResult> ResetAsync(ResetRequest req, CancellationToken ct);
    Task<OpResult> StashAsync(StashRequest req, CancellationToken ct);
    Task<OpResult> UpdateRefsAsync(RefTransaction tx, CancellationToken ct); // 快照還原用
}

public interface IRemoteOps
{
    Task<OpResult> FetchAsync(FetchRequest req, IProgress<GitProgress> progress, CancellationToken ct);
    Task<OpResult> PullAsync(PullRequest req, IProgress<GitProgress> progress, CancellationToken ct);
    Task<OpResult> PushAsync(PushRequest req, IProgress<GitProgress> progress, CancellationToken ct);
    Task<OpResult> CloneAsync(CloneRequest req, IProgress<GitProgress> progress, CancellationToken ct);
}
```

### 5.1 路由規則

| 操作類型 | 後端 | 理由 |
| --- | --- | --- |
| 所有寫入（index、refs、工作目錄） | CLI | hooks、鎖定、設定語意與 CLI 一致 |
| 所有網路操作 | CLI | 憑證管理員、SSH 設定、proxy、LFS 自動正確 |
| status | CLI（`--porcelain=v2 -z`） | 支援 fsmonitor 與 untracked cache |
| log、提交圖、diff、blame | 函式庫 | 高頻呼叫，避免行程啟動成本 |
| SHA-256 或 reftable 倉庫的所有讀取 | CLI | 函式庫對這兩種格式的支援不完整 |
| 函式庫拋出未預期例外 | CLI（自動降級） | 讀取結果正確優先於速度 |

開啟倉庫時讀取 `extensions.objectFormat` 與 `extensions.refStorage`，決定這個倉庫是否停用函式庫後端。

### 5.2 CLI 執行器（GitProcessRunner）

- 參數一律用 `ProcessStartInfo.ArgumentList` 逐一傳入，不拼接字串；路徑參數前加 `--`。
- 每次呼叫固定加 `-c core.quotepath=false -c color.ui=never -c core.pager=cat`。
- 環境變數：`GIT_TERMINAL_PROMPT=0`（避免卡在終端機輸入）、`LC_ALL=C`（錯誤訊息固定英文，方便比對）、`GIT_ASKPASS` 指向本產品的輸入對話框輔助程式。
- 唯讀指令加 `GIT_OPTIONAL_LOCKS=0`，避免和使用者同時在 CLI 的操作搶 `index.lock`。
- 互動式 rebase 以 `GIT_SEQUENCE_EDITOR` 指向輔助程式，由它寫入 UI 產生的 todo 檔。
- stdout 以 UTF-8 串流讀取，邊讀邊解析，不一次載入全部輸出；stderr 另外收集，用於錯誤分類與進度。
- 取消時終止整個行程樹，接著重新讀取 `InProgressState`，確認倉庫沒有停在半完成狀態。

### 5.3 輸出解析器

每個解析器只吃機器格式，並以 golden file 測試覆蓋：

| 指令 | 格式 | 解析重點 |
| --- | --- | --- |
| `status` | `--porcelain=v2 -z --branch` | 一般、rename、unmerged、untracked 四種條目 |
| `log` | `--format` 以 `%x00` 分欄、`%x1e` 分筆 | 父節點清單、作者、日期、ref 裝飾 |
| `for-each-ref` | `--format` 以 `%00` 分欄 | 分支、上游、ahead/behind |
| `diff` | `--no-color --no-ext-diff` unified 格式 | hunk 標頭、二進位檔、模式變更 |
| `stash list`、`worktree list` | `--format` / `--porcelain` | 條目編號與路徑 |

## 6. 操作模型：佇列、進度、取消與全域復原

使用者的每個動作都封裝成一個 `IOperation`，同一倉庫的寫入操作排入單一佇列依序執行，讀取操作可以平行。中風險以上的操作在執行前自動建立快照，因此可以一鍵復原。

```csharp
public interface IOperation
{
    string Title { get; }                  // 顯示在操作面板
    RiskLevel Risk { get; }                // Low / Medium / High / RemoteIrreversible
    IReadOnlyList<string> EquivalentCommands { get; } // 「顯示等效指令」
    Task<PreflightResult> PreflightAsync(RepoState state, CancellationToken ct);
    Task<OpResult> ExecuteAsync(IGitEngine engine, IProgress<GitProgress> progress, CancellationToken ct);
}
```

### 6.1 執行流程

1. **前置檢查**：確認倉庫狀態允許這個操作，例如沒有進行中的 rebase、工作目錄乾淨，並列出這次操作會影響什麼。
2. **確認**：依風險等級決定直接執行、事後可復原提示，或先跳出確認對話框（見第 10 節）。
3. **快照**：Medium 以上的操作先建立快照（見 6.2）。
4. **執行**：在背景執行緒呼叫引擎；即時回報進度；可取消的階段才顯示取消按鈕。
5. **收尾**：重新整理受影響的狀態；成功時顯示含「復原」按鈕的提示，失敗時顯示錯誤分類與建議動作。

### 6.2 快照與復原

快照只存「指標」，不複製檔案，所以成本很低：

| 內容 | 取得方式 | 還原方式 |
| --- | --- | --- |
| HEAD（符號參照或 detached） | `git symbolic-ref` / `rev-parse HEAD` | `symbolic-ref` 或 `update-ref --no-deref` |
| 所有本地分支與標籤 | `for-each-ref refs/heads refs/tags` | `update-ref --stdin` 單一交易 |
| 暫存區 | `git write-tree` | `git read-tree` |
| 工作目錄未提交變更（僅限會捨棄變更的操作） | `git stash create`（不改動工作目錄） | `git stash apply` |

- 快照建立的物件存放在 `refs/gitgui/snapshots/<序號>` 底下，避免被 `gc` 清掉；快照的中繼資料（時間、操作名稱、ref 清單）存在 SQLite。
- 預設保留最近 50 筆或 14 天，兩者取較寬者，可在設定中調整。
- 復原本身也是一個操作，復原前同樣會先建立快照，所以可以「重做」。
- **限制**：已推送到遠端的變更無法在本地復原。UI 會改為建議 revert；若使用者堅持改寫遠端，則走 RemoteIrreversible 的確認流程。

### 6.3 進度與取消

- 解析 stderr 的進度行，例如 `Receiving objects:  45% (450/1000)`，轉成 `GitProgress`（階段、百分比、數量）。
- 能安全中斷的階段（fetch、clone 的下載）才允許取消。改寫 refs 的階段不允許取消，以免倉庫停在不一致的狀態。

## 7. 倉庫狀態、檔案監看與快取

每個開啟的倉庫有一個 `RepoState` 聚合物件，作為 UI 唯一的資料來源。檔案監看依變更位置精準決定要重新整理哪一塊，而不是全部重讀。

**RepoState 內容**：HEAD、分支與上游（含 ahead/behind）、遠端、標籤、status 結果、stash 清單、進行中的操作（merge、rebase、cherry-pick、revert、bisect），以及 submodule 與 worktree 清單。

進行中的操作由 `.git` 目錄裡的標記檔判斷，例如 `MERGE_HEAD`、`rebase-merge/`、`rebase-apply/`、`CHERRY_PICK_HEAD`、`REVERT_HEAD`、`BISECT_LOG`。

### 7.1 變更來源與對應動作

| 變更位置 | 重新整理範圍 |
| --- | --- |
| 工作目錄檔案 | status |
| `.git/index` | status |
| `.git/HEAD`、`refs/`、`packed-refs`、reftable | refs、提交圖（增量） |
| 進行中操作的標記檔 | 進行中狀態橫幅、衝突清單 |
| `.git/config` | 遠端、上游設定，並重新判斷引擎路由 |

### 7.2 監看規則

- 使用 `FileSystemWatcher` 監看工作目錄與 `.git`，事件經 300 ms debounce 後合併處理。
- 本產品自己的操作執行期間暫停處理事件，結束後統一重新整理一次，避免重複刷新。
- 視窗重新取得焦點時一律刷新 status 與 refs，補上監看可能漏掉的事件。
- 檔案數超過門檻（預設 2 萬）的倉庫，提示使用者啟用 `core.fsmonitor` 與 `core.untrackedCache`。
- 自動 fetch 預設每 10 分鐘一次，可依倉庫關閉。

### 7.3 提交圖快取

- 提交圖依「所有 ref tip 的集合」做為快取鍵，存放在記憶體中。
- 只有新增 commit（fast-forward）時，從新 tip 往下走到已知 commit 為止，只計算新增的部分。
- 偵測到 ref 被改寫（非 fast-forward，例如 rebase 或 reset）時，從受影響的分支重新計算。

## 8. UI 層

主視窗採三欄配置：左側導覽、中間提交圖或變更清單、右側詳情與 Diff。提交圖與 Diff 是效能最敏感的兩個控制項，一律自繪並虛擬化，不使用「每列一個控制項」的做法。

### 8.1 主要畫面

| 畫面 | 用途 | 關鍵互動 |
| --- | --- | --- |
| 側邊欄 | 倉庫、本地與遠端分支、標籤、stash、submodule、worktree | 雙擊切換分支；拖曳分支到另一分支觸發 merge 或 rebase |
| 變更（工作區） | 未暫存與已暫存的檔案、commit 訊息輸入 | 勾選檔案、hunk 或單行來暫存；Ctrl+Enter 提交 |
| 歷史（提交圖） | 分支圖、搜尋、篩選作者、路徑與日期 | 右鍵選單：cherry-pick、revert、reset、建立分支或標籤 |
| Commit 詳情 | 中繼資料、變更檔案清單、Diff | 切換並排或行內顯示；開啟檔案歷史或 blame |
| 衝突解決 | 三方比對（ours / base / theirs）與結果 | 逐段採用一方或兩者；標記為已解決；改用外部 mergetool |
| 互動式 rebase | commit 清單 | 拖曳排序；pick / reword / edit / squash / fixup / drop |
| 操作面板 | 進行中與歷史操作、等效指令、原始輸出 | 取消、復原、複製指令 |
| 指令面板與終端機 | 全功能搜尋與執行，以及未覆蓋操作的出口 | Ctrl+Shift+P；內建終端機預設 cwd 為目前倉庫 |

### 8.2 提交圖佈局演算法

1. 以拓撲順序取得 commit（子節點一定排在父節點前面），每批 1,000 筆在背景執行緒處理。
2. 維護一個「進行中的軌道」陣列。每條軌道記錄它正在等待出現的 commit hash。
3. 處理每個 commit 時，先找出所有等待它的軌道：取最左邊那條作為它的軌道，其餘軌道在此合流並釋放。
4. 第一個父節點沿用目前的軌道；其他父節點（merge）各自佔用一條空軌道，優先重用已釋放的位置以保持圖形緊湊。
5. 輸出每列的軌道編號與連線片段，只保留純資料，交給 UI 繪製。

控制項只繪製可見範圍的列，列高固定 24 px，以 `DrawingContext` 一次畫出圓點與連線。分支顏色依軌道編號從主題調色盤循環取用。

### 8.3 Diff 與暫存

- 以 AvaloniaEdit 顯示，支援並排與行內兩種模式，以及語法高亮。
- 選取 hunk 或行時，在記憶體中產生對應的 patch，再用 `git apply --cached` 套用。取消暫存時套用反向 patch。
- 單檔超過 1 MB 或 2 萬行時，先摺疊並顯示「仍要載入」，避免卡住 UI。
- 二進位檔只顯示大小變化；圖片檔提供前後對照預覽。

### 8.4 透明度功能

每個操作完成後，操作面板都會列出實際執行的 git 指令，可一鍵複製。這讓使用者能在 CLI 重現結果，也有助於學習。

## 9. 功能覆蓋矩陣

P0 共 11 項，涵蓋日常最常用的操作，是第一個可用版本的範圍；P1、P2 依里程碑陸續補上，其他指令由內建終端機保底。優先級欄可直接在表格中調整。

| 功能 | 對應 git 指令 | 優先級 | 引擎 | 可復原 |
| --- | --- | --- | --- | --- |
| 開啟、clone、init 倉庫 | `clone`、`init` | P0 | CLI | 不需要 |
| 檢視狀態 | `status` | P0 | CLI | 不需要 |
| 暫存與取消暫存（檔案、hunk、行） | `add`、`apply --cached`、`restore --staged` | P0 | CLI | 反向操作 |
| 捨棄工作目錄變更 | `restore`、`clean` | P0 | CLI | 快照 |
| 提交、修改上一筆 | `commit`、`commit --amend` | P0 | CLI | 快照 |
| 提交圖、搜尋、篩選 | `log`、`rev-list` | P0 | 函式庫 | 不需要 |
| 檔案與 commit 差異 | `diff`、`show` | P0 | 函式庫 | 不需要 |
| 分支建立、切換、改名、刪除 | `switch`、`branch` | P0 | CLI | 快照 |
| fetch、pull | `fetch`、`pull` | P0 | CLI | 快照（pull） |
| push | `push --force-with-lease` | P0 | CLI | 無法本地復原 |
| 合併與衝突解決 | `merge`、`mergetool` | P0 | CLI | 快照 |
| stash | `stash push`、`apply`、`pop`、`drop` | P1 | CLI | 快照 |
| 互動式 rebase | `rebase -i` | P1 | CLI | 快照 |
| cherry-pick、revert | `cherry-pick`、`revert` | P1 | CLI | 快照 |
| reset（soft、mixed、hard） | `reset` | P1 | CLI | 快照 |
| 標籤 | `tag` | P1 | CLI | 快照 |
| blame、檔案歷史 | `blame`、`log --follow` | P1 | 函式庫 | 不需要 |
| reflog 瀏覽 | `reflog` | P1 | CLI | 不需要 |
| 遠端管理 | `remote` | P1 | CLI | 記錄舊設定 |
| submodule | `submodule` | P2 | CLI | 快照（父倉庫） |
| worktree | `worktree` | P2 | CLI | 不需要 |
| Git LFS | `lfs` | P2 | CLI | 不需要 |
| bisect | `bisect` | P2 | CLI | `bisect reset` |
| sparse-checkout | `sparse-checkout` | P2 | CLI | 還原設定 |
| 簽章 commit 與標籤 | `commit -S`、`tag -s` | P2 | CLI | 同提交 |
| 其他所有指令 | 內建終端機 | 保底 | CLI | 依指令而定 |

## 10. 安全與防呆

每個操作依「失去資料的可能性」分成四級，等級決定是否確認、是否快照；能在遠端造成不可逆影響的操作要求最嚴格的確認。

| 等級 | 範例 | 行為 |
| --- | --- | --- |
| Low | stage、commit、fetch、建立分支 | 直接執行 |
| Medium | amend、merge、rebase、reset --mixed、stash drop、刪除已合併分支 | 自動快照；執行後顯示含「復原」的提示，不跳確認 |
| High | reset --hard、clean、捨棄變更、刪除未合併分支 | 自動快照；確認對話框列出會失去的檔案與 commit |
| RemoteIrreversible | 強制推送、刪除遠端分支 | 確認對話框；保護分支（`main`、`master`、`release/*`）需輸入分支名稱才能執行 |

### 10.1 強制推送

強制推送一律使用 `--force-with-lease --force-if-includes`，介面上不提供單純的 `--force`。如果遠端在上次 fetch 之後被別人更新，推送會失敗，並引導使用者先 fetch 檢視。

### 10.2 憑證

- HTTPS 交給 Git Credential Manager，SSH 交給 ssh-agent；本產品不儲存任何密碼或 token。
- 需要輸入時，git 透過 `GIT_ASKPASS` 呼叫本產品的對話框，取得輸入後直接回傳給 git，不寫入任何檔案或日誌。

### 10.3 不受信任的倉庫

倉庫的本地設定可以指定外部程式，例如 `core.fsmonitor`、`core.sshCommand`、`core.hooksPath`、`diff.external`、filter driver。開啟陌生倉庫時，連 `git status` 這類讀取都可能執行這些程式。

- 首次開啟的倉庫標記為「未信任」，所有呼叫加上 `-c core.fsmonitor=false` 等覆寫參數，並列出倉庫設定中會執行外部程式的項目。
- 使用者確認信任後才移除覆寫，信任清單存在 SQLite。
- 遵守 git 的 `safe.directory` 機制；擁有者不符時顯示說明，不自動加入例外。

### 10.4 Hooks

- hooks 照常執行，輸出即時顯示在操作面板。
- 「略過 hooks」（`--no-verify`）預設不勾選，也不記住上次的選擇。

## 11. 非功能需求

下表的數值是初始目標，M0 完成後以基準測試校正。基準環境為一般開發用筆電（SSD、8 核心），大型倉庫的代表規模為 10 萬筆 commit、5 萬個檔案。

### 11.1 效能目標

| 指標 | 目標 | 量測條件 |
| --- | --- | --- |
| 冷啟動到主視窗可操作 | ≤ 1.5 s | 不開啟倉庫 |
| 開啟大型倉庫，提交圖首屏 | ≤ 1 s | 先顯示前 500 筆，其餘背景載入 |
| 提交圖捲動 | 60 fps | 大型倉庫，持續捲動 |
| status 刷新 | ≤ 300 ms | 大型倉庫，已啟用 fsmonitor |
| 單檔 Diff 顯示 | ≤ 200 ms | 1 萬行的文字檔 |
| 常駐記憶體 | ≤ 300 MB | 開啟一個大型倉庫 |
| UI 執行緒單次阻塞 | ≤ 16 ms | 任何 git 操作期間 |

### 11.2 相容性

- 作業系統：Windows 10 / 11、macOS 13 以上、Ubuntu 22.04 以上。
- git 2.40 以上；啟動時偵測版本，低於最低版本時提示升級。
- 支援 SHA-1 與 SHA-256 倉庫，以及 files 與 reftable 兩種 ref 後端。
- 可與使用者同時在 CLI 操作同一倉庫，透過 git 本身的鎖定機制避免衝突。

### 11.3 國際化與中文處理

- 介面語系先做繁體中文與英文，以資源檔管理。
- 中文檔名正常顯示（`core.quotepath=false`）；解析時以位元組處理路徑，不假設任何編碼。
- commit 訊息優先依 commit 物件的 `encoding` 標頭解碼。沒有標頭又不是合法 UTF-8 時，嘗試以 Big5 解碼，並在詳情中標示偵測結果。
- Windows 預設啟用 `core.longpaths`；顯示 CRLF 轉換警告，並說明 `core.autocrlf` 的目前設定。
- 字型預設帶 CJK 後備字型（Windows：Microsoft JhengHei；macOS：PingFang TC；Linux：Noto Sans TC）。

### 11.4 無障礙

- 所有功能都能只用鍵盤操作，並可自訂快捷鍵。
- 自繪控制項提供 automation peer，讓螢幕閱讀器能讀出 commit 與檔案名稱。

## 12. 錯誤處理、日誌與遙測

git 錯誤先依結束碼與 stderr 內容分類，每一類都對應一句白話說明和一個建議動作按鈕；原始輸出永遠可以展開查看。stderr 比對之所以可靠，是因為執行器固定設定 `LC_ALL=C`，錯誤訊息一律是英文。

| 錯誤類型 | 判斷依據（stderr 片段） | 建議動作 |
| --- | --- | --- |
| NonFastForward | `rejected`、`non-fast-forward` | 先 Pull，或改用 rebase 同步 |
| StaleLease | `stale info` | 先 fetch，檢視遠端的新 commit |
| Conflict | `CONFLICT`、結束碼 1 且存在 unmerged 條目 | 開啟衝突解決畫面 |
| AuthFailed | `Authentication failed`、`Permission denied (publickey)` | 重新登入，或開啟 SSH 金鑰設定說明 |
| LockExists | `index.lock': File exists` | 確認沒有其他 git 行程後，提供移除鎖定檔 |
| HookRejected | hook 名稱加上非零結束碼 | 顯示 hook 輸出；提供「略過 hooks 重試」 |
| Network | `Could not resolve host`、`timed out` | 重試，或檢查 proxy 設定 |
| DirtyWorktree | `would be overwritten` | 提供 stash 後再試 |
| Unknown | 以上皆不符 | 顯示原始輸出，提供複製與回報 |

### 12.1 日誌

- Serilog 寫入滾動檔案，保留 14 天。
- 每次 git 呼叫都記錄參數、cwd、耗時、結束碼與 stderr 摘要。
- URL 中的帳號密碼，以及 `Authorization` 標頭類的內容，在寫入前一律遮蔽。

### 12.2 遙測與當機回報

- 預設關閉，需使用者明確同意才啟用。
- 只收集功能使用次數、操作耗時分佈、錯誤類型與當機堆疊；不收集路徑、倉庫名稱、分支名稱或檔案內容。

## 13. 測試策略

測試重點放在兩處：解析器的正確性，以及「任何操作都能復原」這個承諾。這兩處出錯會直接造成資料遺失或畫面誤導，其餘部分以常規測試覆蓋。

| 層級 | 對象 | 方法 |
| --- | --- | --- |
| 單元 | 輸出解析器 | golden file：保存真實 git 輸出，逐欄比對解析結果 |
| 單元 | 提交圖佈局 | 以小型 DAG 驗證軌道編號與連線；涵蓋 octopus merge |
| 單元 | 風險分級、前置檢查 | 表格驅動測試 |
| 模糊測試 | 解析器 | 檔名含換行、引號、emoji、無效 UTF-8 位元組 |
| 整合 | 引擎實作 | 在暫存目錄建立真實倉庫，用腳本重現衝突、rebase、submodule 等情境 |
| 屬性測試 | 快照與復原 | 隨機產生操作序列，執行後復原，驗證 refs、index、工作目錄與快照時完全一致 |
| 相容性 | 倉庫格式 | SHA-256 與 reftable 倉庫跑同一組整合測試 |
| 架構 | 依賴規則 | NetArchTest 檢查第 4 節的依賴方向 |
| UI | 主要畫面 | Avalonia.Headless 畫面截圖比對 |
| 效能 | 第 11.1 節指標 | BenchmarkDotNet；以 git.git 倉庫及合成大型倉庫作為測試資料 |

**CI 矩陣**：Windows、macOS、Linux 三平台，各搭配 git 2.40 與最新穩定版。效能基準只在固定規格的機器上執行，結果退步超過 10% 時讓建置失敗。

## 14. 開發里程碑

開發分為 M0 到 M4 五個階段，M1 完成後即可在日常工作中取代 CLI。各階段時程待人力確定後再排定（見第 15 節）。

&#91;embedded content: 開發里程碑 · M0 到 M4，各有一個關卡\]

每個關卡都是可驗證的條件，未通過就不進入下一階段，避免在不穩定的基礎上繼續疊加功能。

## 15. 風險與待決事項

最大的技術風險是 LibGit2Sharp 的格式支援與 Native AOT 相容性；兩者都有退路，因為讀取可以全部改走 CLI。

| 風險 | 影響 | 因應 |
| --- | --- | --- |
| LibGit2Sharp 不支援 SHA-256 或 reftable | 這類倉庫無法使用函式庫讀取 | 依倉庫設定自動改走 CLI（第 5.1 節） |
| LibGit2Sharp 與 Native AOT、裁剪不相容 | 啟動時間無法進一步縮短 | v1 先用 ReadyToRun；M0 實測 AOT 可行性 |
| Windows 上 git 行程啟動較慢 | 頻繁呼叫 CLI 時累積延遲 | 讀取走函式庫；批次合併呼叫；視窗閒置時預先讀取 |
| Avalonia 大量清單效能 | 提交圖捲動卡頓 | 提交圖用自繪控制項，不用 ItemsControl |
| 解析 stderr 依賴錯誤訊息文字 | git 改版後錯誤分類失準 | 固定 `LC_ALL=C`；CI 測試多個 git 版本；比對失敗時歸為 Unknown，不誤判 |
| 使用者同時用 CLI 修改倉庫 | UI 顯示過期狀態 | 檔案監看加上取得焦點時刷新 |

**待決事項**

- [ ] 產品正式名稱與授權方式（開源、商用或內部使用）
- [ ] 是否需要支援 Windows ARM64 與 Linux ARM64
- [ ] 託管平台整合（GitLab、GitHub 的 MR / PR 檢視）要排在 v1 之後的哪個版本
- [ ] 開發人力與各里程碑時程
- [ ] 是否提供可攜版（免安裝）
