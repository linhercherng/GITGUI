# 里程碑與驗收

原規格的 M0–M4 圖表未附原圖。以下沿用初始化時的排程分組，M1 功能以 [驗收表](M1-ACCEPTANCE.md) 與 [驗證紀錄](VALIDATION.md) 為準。

| 里程碑 | 狀態／範圍 | 關卡 |
| --- | --- | --- |
| M0：專案基礎 | 已完成：七專案、唯讀 UI、CLI/Lib 路由、SQLite、測試與 CI | 三平台 build/test；唯讀不改 index |
| M1：P0 日常操作 | 已實作：11 項 P0、專業三欄 UI、三方編輯、快照 Undo/Redo | 真實 Git 操作與復原、寫入佇列、安全前置檢查、Headless 與 Windows 實機驗證 |
| M2：P1 進階歷史 | 已實作：stash、互動 rebase、cherry-pick/revert/reset、tag 管理、blame／檔案歷史、reflog、遠端管理、內建逐行終端機；見 [驗收表](M2-ACCEPTANCE.md) | 中斷／續作與隨機序列復原；擴大衝突類型；實際 Portable ZIP 測試 |
| M3：P2 與相容性 | 已實作：submodule、worktree 管理、LFS、bisect、sparse、簽章標籤、ARM 決策；見 [驗收表](M3-ACCEPTANCE.md) 與 [操作手冊](USER-GUIDE.zh-TW.md) | 精確 Git 2.40/2.56 六格 CI、SHA-256/reftable 矩陣、Windows ZIP／桌面驗收；結果以同版 Release 證據為準 |
| M4：正式版 | v0.6.0改善候選：保守原生讀取、漸進首屏、文字快取、分頁與快取、雙語／自訂快捷鍵、automation peers、診斷、.NET10 LTS、MIT及升級驗證已實作；見 [驗收表](M4-ACCEPTANCE.md) | 新二進位首次啟動、持續捲動及嚴格16ms回應目標尚未完整通過，正式關卡仍未完成；不將預覽版稱為正式1.0 |

## M1 後的技術工作

- 依 refs tip 做增量提交快取，精準分流檔案監看刷新；背景自動 fetch 與倉庫級設定。
- 依實測擴大原生讀取；一般 SHA-1/files 的 Diff／preview／refs 已有保守原生路徑，不支援的格式、attributes、encoding 或環境仍回退 CLI，優先保留 Git 語意。
- 完整語法高亮、非 UTF-8 檔案的無損文字編輯、無效位元組路徑操作。
- 一般 index 語意之外的可重建 extension／stat cache 不要求 byte-identical；持續擴大快照屬性測試與錯誤注入。
- 快照 SQLite 索引的遺失重建、使用者可調保留政策、備份匯出與磁碟壓力管理。
- 單一實例、終端機 VT/PTY 擴充、完整錯誤建議動作按鈕與可選的脫敏診斷匯出。
- 10萬commit／5萬檔案的實體基準與10%比較器已加入；引擎比較通過，完整原生效能關卡仍待完成，見 [量測結果與邊界](M4-PERFORMANCE.md)。

## 已完成的安全驗證

快照不再依賴不含 untracked/unmerged 的 stash create。已保存並測試 raw 工作檔、各 index stage、unborn/detached HEAD、分支/tag、merge 狀態與 GC 保護。還原先取得 index/ref 鎖；鎖衝突與 refs 命名衝突在替換資料前失敗。捨棄與還原包含重做快照；ignored 檔案不進行全倉庫清除。

所有網路／寫入使用 CLI。強制推送同時使用 lease/includes；保護分支文字確認、stale lease、hook 拒絕、信任 filters 及 AskPass Unicode 已有測試。M2 擴充 rebase／cherry-pick／revert、stash 堆疊與 remote 設定復原；M3 新增父倉庫 gitlink 與 sparse 設定／檔案復原，不覆寫子倉庫資料；bisect 以原生 reset 結束。
