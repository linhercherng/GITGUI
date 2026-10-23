# M3 功能與驗收

M3 對應原規格 P2 與 Git／倉庫格式相容性。操作方式見 [繁體中文手冊](USER-GUIDE.zh-TW.md)。最終測試與套件驗證以 v0.4.0 Release 的 `verification.json`、對應來源 commit 的 CI 與 `SHA256SUMS.txt` 為準。

| 原規格功能 | 實作與復原方式 | 主要證據 |
| --- | --- | --- |
| Submodule | 清單、新增、init、update checkout、sync、非強制 deinit；父倉庫快照，保留子倉庫資料 | `RepositoryRecoveryTests`、pending gitlink rebase continuation、ZIP child dirty undo |
| Worktree | porcelain -z 清單、建立新分支／detached、移除、鎖定／解鎖、prune；無快照 | `RepositoryToolsTests` 的中文路徑、髒／ignored 資料拒絕、共用佇列、ZIP lifecycle |
| Git LFS | 本地 install、track／untrack、status、fetch／pull；不改寫歷史、無 LFS 快照 | 真實 bare remote 的 pointer／payload 轉移與 checkout，ZIP 內建 LFS |
| Bisect | start、good、bad、skip、reset；以 bisect reset 復原 | restart 後 reset 回原分支／HEAD、ignored collision 拒絕、ZIP bisect |
| Sparse-checkout | cone／noncone set、disable；快照保存 pattern／設定／index flags／工作檔 | sparse-index、sibling worktree、ignored directory、Undo／Redo、ZIP sparse undo |
| 簽章 commit／tag | 使用既有 signer，`commit -S`／`tag -s`；同提交／標籤快照 | 臨時 SSH key 的真實 verify-commit／verify-tag，ZIP 內附 SSH signer |
| 專業 UI | 四套主題、五個工具分頁、實際指令輸出、操作／關閉互斥、快照復原 | `RepositoryToolsWindowTests`、全 UI suite、`docs/images/m3-*` |
| 精確 Git 版本與格式 | 三平台 × Git 2.40／2.56，SHA-1／SHA-256 × files／可用 reftable | [相容性矩陣](M3-COMPATIBILITY.md)，舊 Git 不支援的格式以 skip 明示 |
| ARM 決策 | Windows／Linux ARM64 暫不列正式發行平台，保留原生依賴證據與入場條件 | [ARM 決策](M3-ARM-DECISION.md) |

## 資料保護

GUI 全部寫入走 CLI，信任確認與同 Git common-directory 佇列沿用 M1／M2。新增工具也不會在寫入期間被其他 GUI 寫入、終端機或關閉視窗打斷。危險移除先確認，不提供 force。可取消的前置等待完成後，已開始的多步寫入不靠強制取消製造半成品。

父倉庫快照記錄 gitlink 的 mode／OID／index stages、父倉庫檔案與本地 submodule 設定，不遍歷或保存子倉庫 refs／index／工作檔／物件資料庫。Undo-add 留下已 clone 的子目錄，deinit Undo 不自動 checkout；子倉庫內容需獨立保存。一般父倉庫命令明確關閉 submodule.recurse，危險 gitlink／檔案型態替換先拒絕。

Sparse 復原保存目前工作樹的語意設定與 pattern bytes；保留 Git 已做的 extensions.worktreeConfig 格式升級，以保護 sibling worktree。Git 原生工作樹移除可能丟棄 ignored 檔案，因此額外掃描並拒絕。LFS 內部需要 attribute pathspec，執行其工具時不繼承全域 literal pathspec，避免命令成功卻仍留下 pointer 的錯誤。

## 明確邊界

- Windows 實體免安裝包為 x64；ARM 沒有正式套件或硬體驗收宣稱。
- macOS／Linux 的 CI 是引擎與 headless UI 驗證；真實桌面啟動驗收在 Windows 執行。
- 互動 rebase 限線性歷史；內建 shell 仍為逐行模式。大型效能、完整無障礙及 VT／PTY 擴充留在後續里程碑。
- 本機／受驗證 URL 的子模組更新不自動放寬所有巢狀來源的傳輸政策；拒絕時可個別檢查來源。所有 hooks／filters／signers 仍由已信任的 Git 設定控制。
- 手冊隨 ZIP 提供，放在 `GitGui.App.exe` 旁；相容性說明與截圖也隨包附上。
