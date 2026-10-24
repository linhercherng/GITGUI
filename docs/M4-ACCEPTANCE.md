# M4 正式版驗收

M4 沿用 [ROADMAP](ROADMAP.md) 與原始 [規格](specification.md) 第11–13節。v0.7.0正式效能驗收已通過，原始規格及所有門檻保持不變。實際二進位來源為 `f923f11e18286b78b5b7358212bce5736032d5be`；後續驗收器及CI調整沒有修改產品程式碼。

Windows／macOS／Linux × Git2.40／2.56的 [六格CI](https://github.com/linhercherng/GITGUI/actions/runs/40081899175) 全部通過，包含Core33、Engine288、UI170項測試及Windows ZIP驗證。實際ZIP另完成2,224次Git操作、24筆原版M3快照Undo/Redo與原狀復原、AskPass及桌面啟閉。

首次新二進位啟動1,319.8566ms，五組大型倉庫操作及捲動全部通過。獨立profiler保留462,924筆完整紀錄，其中174,902筆涉及產品操作、40筆跨階段，最大產品重疊13.9479ms。原版與加速後驗收器的68項判定相同；51個驗收情境、5,992個時間索引對照及17個實際native負向案例均通過。BDN六個case／30個actual及M3的10%比較亦完成，詳見 [效能報告](M4-PERFORMANCE.md)。

預設啟動路徑為 `GITGUI.vbs`。手冊更新採已測試映像的文件封裝，所有程式、Runtime、Portable Git及截圖逐檔SHA保持相同。`build-info.json`保留二進位來源，`release-info.json`記錄文件／驗收來源；Release的 `verification.json`、`SHA256SUMS.txt` 與證據ZIP提供完整核對。

## 歷史驗收紀錄

以下保留當時的失敗與限制，不代表v0.7.0最終判定。

2026-10-24續作的編輯器延遲建立、資料庫背景排程與收合操作面板改善，及仍未達標的原生數據，見 [本輪效能結果](M4-PERFORMANCE.md)。測試程式已驗證資料庫鎖定不阻塞呼叫端、切換Diff保留文件與語系、收合期間保留紀錄並於展開更新；這些功能性質不能取代正式效能驗收。

最新實際免安裝候選包來源 `4e3741b` 的 [六格CI](https://github.com/linhercherng/GITGUI/actions/runs/40048922703) 與Windows459項測試、2,224次Git操作及原版24筆快照功能驗證均通過。原生五組的首屏、status、Diff、記憶體及本次配對16ms工作範圍達標；首次啟動7,734.22ms與捲動cadence 0/5仍未過關，正式判定保持失敗，因此沒有發布0.7.0 Release。

前輪候選包來源為 `b34dc8a4fe6c8053cf854ace95a8180ec12d949f`，已完成五組原生量測與配對 dispatcher profiler，正式判定仍為失敗：首次啟動4,074.56ms、捲動cadence僅1/5通過、單次UI工作18.9497ms；首批歷史、status、Diff及記憶體該組符合目標。該來源的真實git.git與合成倉庫BDN六個case／30個actual樣本全部完成，但不取代最新來源的原生關卡。詳見 [逐項結果](M4-PERFORMANCE.md)。

該來源的 [CI run39993166764](https://github.com/linhercherng/GITGUI/actions/runs/39993166764) 為五格成功、一格macOS／Git2.56的UI測試同步失敗；Core33、Engine284均通過。失敗已在隔離測試受控重現：衝突編輯測試未等待Activated啟動的實際刷新，儲存因忙碌被正確拒絕。修正只等待正確的工作完成，不放寬儲存與復原斷言。修正來源 `d5b5e08a63873ab00336554ac967c4c0cfc56d46` 的 [六格 CI run39995975192](https://github.com/linhercherng/GITGUI/actions/runs/39995975192) 已全部成功，包含Windows套件驗證；其相較候選包來源只有測試同步與文件變更，產品原始碼相同。本文件後續僅同步此驗證結果，不將CI成功延伸成原生效能通過。

| 範圍 | 實作 | 驗證方式 |
| --- | --- | --- |
| 大型倉庫 | 首批500筆、續載1000筆、依 refs 世代的有界提交快取、原生游標、狀態獨立刷新 | 100k提交／50k檔案實體fixture、SHA-1/SHA-256分頁與外部改refs、取消後重試、寫入重新檢查 |
| UI 回應 | 批次清單通知、監看事件先合併再送UI、非同步Diff投影、延遲載入大型Diff | 50k事件/檔案壓力、過期選取與關閉切換測試；原生量測見效能文件 |
| 鍵盤與偏好 | 共用指令描述、自訂／解除／重設快捷鍵、視窗作用域、JSON原子保存 | 真正鍵盤stage/commit、衝突拒絕、交換快捷鍵後重啟、文字輸入焦點保護 |
| 雙語 | 繁中／英文資源、語系持久保存、開啟中視窗即時更新 | 資源鍵完整性、指令面板／設定／工具／終端機／確認文字與live切換 |
| 無障礙 | 提交圖虛擬列Selection/Scroll automation peers、檔案完整名稱、Diff唯讀Value provider、可見焦點 | Headless automation與Tab/ShiftTab；不將其稱為實體Narrator驗收 |
| 文字與路徑 | 依encoding標頭／嚴格UTF-8／Big5解碼；顯示偵測與EOL設定；不支援路徑顯示escaped bytes並禁止模糊寫入 | 真實Big5 commit/reword、混合訊息批次、Linux無效位元組檔名、Windows UTF-16別名保護 |
| 寫入編碼 | 遵守已設定的i18n.commitEncoding，未設定為UTF-8；未知／無法表示的文字在快照或寫入前拒絕 | emoji對Big5、空／未知編碼、rebase續作時編碼改變；不偷偷更改使用者Git設定 |
| 錯誤處理 | 保守分類、白話說明與建議動作、原始輸出仍可展開 | 認證／鎖／hook／網路／lease等案例；retry沿用原安全確認，鎖定說明不盲刪檔案 |
| 診斷 | 結構化參數/cwd/耗時/exit/錯誤分類，14日滾動保留，憑證遮罩 | attached/split/quoted秘密參數、URL/header、取消、ILogger與observer故障；不持久化stdout/stdin/AskPass |
| 執行環境 | .NET10 LTS、自含10.0.13、ReadyToRun、無trimming | 鎖定依賴、實際ZIP共享DLL逐一hash核對，隔離已安裝.NET/Git後啟動 |
| 散佈與升級 | Windows x64 ZIP含完整PortableGit2.56.0.3、離線手冊、保留舊設定與快照索引 | 解壓到中文空白路徑；AskPass UTF-8、桌面啟動關閉、資料庫／偏好重新開啟；不覆寫舊套件 |
| 授權 | 主程式MIT，第三方依原授權 | 根目錄LICENSE、NuGet版本/hash/license inventory、PortableGit原始來源包與notice |

## 執行環境與相容性決策

SDK固定10.0.402，Runtime固定10.0.13，依 [Microsoft官方發行metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json) 選定LTS。SDK下載SHA-512：

```text
ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd
```

上游 [支援OS表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) 截至2026-10-23列出Windows11、指定Windows10 Enterprise/IoT/LTSC版本、macOS15+與Ubuntu22.04+。原規格的macOS13已離開上游支援週期；本次macOS CI使用15 Intel，不宣稱驗證13，也不把Windows11本機測試延伸為所有Windows10版本驗收。Windows套件為x64；ARM決策仍見 [M3 ARM](M3-ARM-DECISION.md)。

Git固定2.40.0與2.56.0，各於Windows2022、Ubuntu22.04、macOS15 Intel執行；舊Git不具備的reftable案例明確skip。非UTF-8實體檔名字節測試僅能在Linux建立相應fixture，不能將Windows無法建立的案例當成跨平台通過。

## 驗證邊界

效能的每一項原始目標、基準比較與原生桌面量測邊界見 [M4效能](M4-PERFORMANCE.md)。Headless測試不能證明實體螢幕閱讀器讀音、顯示器實際幀率或所有桌面環境；Windows免安裝驗證也不是全新VM驗收。Git的使用者設定、credential helper、hooks與外部工具仍依個人環境執行。

逐行文字終端機沒有新增PTY/VT；託管平台PR/MR整合與可選遙測仍不在本次範圍。主程式不啟用遙測。
