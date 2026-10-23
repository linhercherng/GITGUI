# M2 P1 驗收

依據原始 specification.md 第 9 節 P1 與 ROADMAP 的 M2 分組實作；原始里程碑圖片未附，不推測 P2 功能。

| 功能 | GUI / 行為 | 驗證 |
| --- | --- | --- |
| Stash | push（含 untracked）、apply、pop、drop；OID + selector 驗證防止選錯堆疊 | 真實中文檔案、堆疊、GC 復原 |
| 互動式 Rebase | 拖曳／上下排序；pick/reword/edit/squash/fixup/drop；UI 訊息 | 真實歷史、混合 action、過期計畫拒絕；Headless 拖曳 |
| Cherry-pick / Revert | 提交 SHA 輸入、衝突編輯與繼續／略過／中止 | 真實分歧提交與衝突 |
| Reset | soft/mixed/hard；hard 列檔案與提交並確認；快照 | 各模式 index/worktree 語意與復原 |
| 標籤 | 輕量／附註新增、刪除 | 真實 tags 與訊息 |
| Blame / File history | 選檔、逐行作者／SHA；跟隨 rename | libgit2 與 SHA-256 CLI fallback |
| Reflog | HEAD 最近 500 筆，選取作為目標 SHA | 真實 reflog、UI 選取 |
| 遠端 | 新增、改名、fetch/push URL、移除 | 設定與 tracking refs 復原 |
| 終端機 | 倉庫 cwd、持續 shell、選定 Git PATH/helpers、關閉終止子程序 | 真實 shell、信任、Git shadow、防洩漏、取消與資源釋放 |
| 中斷復原 | 識別磁碟上的 sequencer/rebase 狀態；無需記憶體工作階段 | 新 service 續作；快照還原與隨機序列 |

所有 GUI 寫入經同一 common Git directory 佇列；終端機工作階段取得同一佇列租約。
所有新操作沿用信任檢查及 CLI；風險操作前建立快照，高風險另確認。快照保存原始檔案、各 index stage、refs、stash 完整 reflog、remote 設定、sequencer/rebase/helper 狀態及可執行模式；內部 refs 保護物件可達性。

Hard reset 會將目的樹覆寫的 ignored 檔案與阻擋父路徑的檔案納入快照。若目的檔案被既有目錄擋住，先拒絕並請使用者移開目錄，避免清除其中的 ignored 資料。快照復原的檔案／目錄轉換會保存重做所需的碰撞檔案，並先拒絕未知的 ignored 子檔案。進階工作樹操作也會在修改前拒絕引入 submodule，維持 P2 邊界與可復原性。

Rebase、cherry-pick、revert 與 stash apply/pop 也會額外保存目的樹／補丁觸及的 ignored 檔案。繼續、略過與中止會檢查原始 HEAD、onto、進行中提交及待辦清單，避免操作中新增的碰撞資料被當作一般 ignored 檔案略過。這是針對 Git 內建工作樹操作的保護；hooks 或任意終端機程式的外部副作用不屬於自動復原保證。

檔案歷史的原生查詢明確指定 Topological | Time 排序，避免相同或倒退的提交時間使 rename 路徑追蹤先走訪父提交。此選項依據目前套件的 [FileHistory 實作](https://github.com/libgit2/libgit2sharp/blob/eaa698d078941fd5e3cc82b59b885cd35d8cc0f8/LibGit2Sharp/Core/FileHistory.cs#L114)；仍保留原生讀取失敗時的 CLI fallback。

邊界：rebase 僅線性範圍，不隱含攤平 merge commit；bisect/sparse/submodule 等 P2 模式維持拒絕快照保證。終端機是逐行 redirected shell，無 VT/fullscreen/Ctrl+C forwarding；任意指令不宣稱自動可復原。檔案歷史／reflog 各顯示最近 500 筆。跨平台 CI 不等同 Linux/macOS 原生桌面人工驗收。

執行紀錄：本機與 CI 結果、精確封裝 SHA、解壓後的命令／原生啟動驗證隨 Release 的 verification.json 一起發布。
