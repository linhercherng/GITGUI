# M1 架構與安全決策

日期：2026-10-23。原始規格保留在 specification.md；以下記錄實作對規格細節的具體處理。

## M4 讀取與散佈決策

寫入仍重新讀取信任／狀態／操作中標記，不共用展示快取。提交圖依可見refs、detached HEAD、篩選、選定Git及object-view fingerprint建立世代；續載固定同世代，refs或shallow/grafts/replacement改變即重置。Git已有單檔／分割commit-graph時，整個世代採CLI拓撲查詢以使用其索引；其他一般SHA-1/files仍優先使用native游標，避免在頁與頁之間混用不同排序。

`RepositoryObjectView`是Core內的共用唯讀檔案指紋小工具，供兩個引擎一致辨識config、shallow、grafts及replace refs；不啟動行程、不做授權、不修改檔案。這是Core原先偏純資料責任的窄例外，避免兩個引擎的失效條件漂移。原生游標及展示快取均限制筆數與文字總量；巨大訊息可顯示但不長期保留於details快取。工作檔Diff不快取，仍由Git重新取得，避免相同status字元遮蔽外部編輯。

設定版本向後相容：原SQLite資料表不重建，UI新偏好存獨立ui.json，以同目錄暫存檔及原子替換保存。語系與快捷鍵不包含倉庫路徑或憑證。Windows發行採.NET10 LTS自含執行環境與ReadyToRun，完整PortableGit獨立散佈；主程式MIT與第三方授權分開保留。

## 模組

| 專案 | 責任 |
| --- | --- |
| GitGui.Core | Workspace／操作／確認／快照契約，純資料提交圖、patch 與衝突區段演算法 |
| GitGui.Engine.Cli | Git 行程、唯讀查詢、寫入佇列、操作前檢查與快照機制 |
| GitGui.Engine.Lib | SHA-1/files 的一般提交圖與歷史讀取 |
| GitGui.Infrastructure | SQLite 最近倉庫／信任／快照索引、同使用者 AskPass 管線、生命週期日誌 |
| GitGui.UI | MVVM、三欄視窗、AvaloniaEdit、原生確認與憑證對話框、監看與雙語資源 |
| GitGui.Controls | 自繪提交圖與可見列繪製；只依賴 Core |
| GitGui.App | DI、程式進入點、桌面與 AskPass helper 組裝 |

UI 與 Controls 不互相參照；App 在組合階段提供圖形控制項。Core 不依賴 Process、Avalonia 或 LibGit2Sharp。寫入不經 libgit2。

## 操作邊界

同一 git-common-dir 共用 SemaphoreSlim，涵蓋 linked worktree 的共享 refs。等待佇列及前置讀取可取消；開始不可逆的本地階段後不開放取消。Fetch/clone 的 Git 下載允許取消，取消會終止行程樹。CLI 使用 ArgumentList、literal pathspec 與固定機器格式；一般 stdout 有界，snapshot blob 內容另外串流。

不受信任的 status/diff 會停用 fsmonitor、clean/smudge/process filters、external diff 與 textconv；信任後保留一般 Git 設定語意。執行寫入前顯示信任設定。AskPass 只經同使用者 named pipe 與 Git 的 stdout 傳遞秘密；Windows WinExe 直接寫 UTF-8 輸出管線，不設定不存在的 console code page。

Pull 使用先 fetch 再 merge 的等價流程：精確抓取到唯一私有 ref，原子更新遠端 tracking ref，解析 OID 後 merge，最後清理私有 ref。不依賴可能被外部 fetch 改動的 FETCH_HEAD。遵守 pull.ff；配置 rebase 時需同意此次使用 merge，保留原設定。Force push 永遠同時帶 --force-with-lease 和 --force-if-includes。

## 快照的資料完整性

原規格的 stash create 只是一個起點：它不涵蓋 untracked 或 unmerged index，所以不能直接用它保證全域復原。M1 使用 Git blob/tree/commit 保存原始工作檔位元組、各 index stage、merge 標記與 JSON manifest；內容按 Git 物件去重。分支、標籤、HEAD 及 merge 相關物件另由快照 refs 保護，SQLite 只存清單索引。

擷取與還原持有 Git index.lock；還原先以 update-ref --stdin prepare 取得全部 ref 鎖，再替換檔案。完整 index 由 read-tree/update-index 在 alternate index 產生，最後依標準鎖檔替換協定安裝；不自行解析或改寫二進位 index。index 的內容、stage 與支援旗標恢復一致，stat cache 等可重建延伸資料不承諾逐位元組相同。

還原前先保存重做快照，包括將被覆蓋的特定忽略路徑。一般捨棄只 clean 選取的未追蹤檔，不使用全倉庫 clean -x。所有路徑檢查均拒絕根目錄跳脫、.git 與穿越符號連結的父目錄。忽略目錄碰撞、其他 worktree 使用的分支，以及 refs 檔案／目錄命名衝突都在資料變更前阻擋。

不能與任意外部編輯器建立跨檔案交易；執行前仍應結束其他會大量改寫同一工作目錄的工具。失敗結果保留復原快照，不會把不完整操作當成功。快照不包含 ignored 私有資料的全量備份，也不能撤回遠端推送。

## 讀取與 UI

LibGit2Sharp 的圖形查詢只從普通分支、遠端分支、tag 及 HEAD 取得可達提交，明確排除私有快照／fetch refs。SHA-256/reftable、自訂篩選及失敗降級使用 CLI；M1 Diff 也走 CLI，以保持行暫存與 Git 的 filter／模式／patch 語意一致。後續再依效能量測擴大函式庫讀取。

圖形純資料在背景計算，只繪可見固定高列，提供橫向捲動；Diff 由 AvaloniaEdit 虛擬化。每次選取查詢都綁定倉庫、路徑、模式與 commit 身分，遲到的回應不能覆蓋較新選取。衝突草稿以路徑與編輯版本保存，載入／儲存中禁止編輯與採用，完成時只清除已儲存的那一版草稿。

檔案監看涵蓋root、gitdir與linked-worktree commondir，原生執行緒先合併事件，再做300ms debounce；依事件分流status／history／config，忽略Git內fsmonitor cookie與IPC，避免查詢自我觸發。自己的操作結束後統一刷新。M4已加入提交快取與狀態獨立刷新；背景自動fetch仍未加入，完整原生效能尚未達標，見M4效能文件。
