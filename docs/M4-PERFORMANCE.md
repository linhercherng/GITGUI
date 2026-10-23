# M4 效能與重現

原始規格第 11.1 節的目標保持不變。0.6.0 為效能改善候選版，目前仍未通過完整 M4 正式驗收；尚未發布新版 Release。既有 v0.5.0 仍為預覽版，不以功能測試通過代替效能驗收。

## 固定主機與資料

主機為 MSI 筆電、Intel Core i7-13620H（10 實體核心／16 邏輯處理器）、51,229,798,400 bytes RAM、KINGSTON SNV2S1000G NVMe SSD、Windows 11 build 26300（API 回報 Windows 10.0.26300）、x64。.NET 工作處理器固定 8，電源計畫維持 Balanced；不更改系統安全設定、程序優先權或強制 GC。

合成實體 fixture 含 100,000 筆提交、50,000 個檔案、10,000 行文字檔及 100 處修改，fingerprint 為 `997fa82c370d493f9f31971cbf922c35`。Git 2.56.0.windows.3 啟用該 fixture 的 fsmonitor、fscache，先建立 FSMN／UNTR 快取；產品唯讀查詢仍使用 no-optional-locks。這些設定只作用於專用 fixture，不修改使用者倉庫。

另保留上游 git.git 的完整 master 祖先歷史，HEAD 為 `7e34d8174dcda3435d1404c9a30bef46afe6c13e`，包含 82,563 個可達提交及 4,857 個索引檔案。此為非 shallow 的 blob:none partial clone；必要 Diff 物件事先取得，量測子程序設定 `GIT_NO_LAZY_FETCH=1`，不得在計時期間網路補取。

基準來源為 M3 標籤 v0.4.0（`f1320bf47205f497be558902153acf4a7fd68ab5`），保留原 .NET 8.0.32，只套入相同量測驅動程式。M4 使用 .NET 10.0.13。比較必須匹配 protocol、fixture、主機及 Git；原始樣本與離群值全部保留。

## 目前驗收狀態

2026-10-24 的 candidate8（來源 `7eaa6d62fe73799b6cd1b90d5696083d7c3d0a40`）五組未注入追蹤工具的原生量測如下。新二進位第一次執行包含在原始樣本中；這是候選程式，尚非最終 ZIP。離線完整判定 `artifacts/m4-v060-candidate8-desktop-verdict.json` 為 `pass=false`。

| 原始目標 | 該組觀察 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次 6,432.46ms，下一次大型模式啟動 5,928.46ms；其餘八次約 880–960ms | 未通過，首次樣本不能當暖機丟棄 |
| 大倉庫前 500 筆首屏 ≤1,000ms | 五次最大 854.23ms | 該組符合 |
| status ≤300ms | 全部 15 次最大 212.24ms | 該組符合 |
| 10k 行 Diff 顯示 ≤200ms | 全部 15 次最大 146.35ms | 該組符合 |
| 常駐 ≤300,000,000 bytes | 捲動期間全部樣本最大低於 263,000,000 bytes | 該組符合 |
| 持續捲動 60fps | 五組只有一組通過嚴格 cadence；其餘起始空檔 28.99–33.03ms，內部最長空檔 26.21–33.53ms | 未通過 |
| Git 操作期間 UI 單次阻塞 ≤16ms | 配對外部 profiler 記錄 20.1075ms render 工作與 17.4541ms Loaded 佇列工作 | 未通過，不能由平均值抵銷 |

這組已使用依文字內容重用的有界文字快取、保留的不可變繪圖筆，以及同視窗一個實體像素的 composition marker。marker 確認所等待批次確有本視窗的繪製，不能單憑成功的空批次任務宣告顯示完成。這些修正消除了既有量測盲點，但未使全部原始效能關卡通過。

首次新二進位啟動的既有 5–14 秒離群值亦保留。candidate7 的首次 EventPipe 診斷為 6,285.7ms，沒有事件遺失；大部分已觀察時間位於同步 assembly loader 區間，JIT 只占較小部分。CLR 首個事件之前約 1,569ms 為追蹤盲區。Loader 區間可能包含其他底層工作，現有證據不能判定 Windows、安全軟體、磁碟或 GPU 是根因；追蹤本身會改變耗時，不能拿來代替未注入工具的正式量測。

Read 路徑已加入設定批次查詢、保守失效快取、格式可用時的原生 refs／Diff／preview，以及獨立首批 history 發布；所有寫入仍走 CLI，重新取得完整狀態與安全前置檢查。SHA-256、reftable、特殊 attributes／encoding／Git 環境等不支援原生讀取的情況保留 CLI fallback。提交圖快取已成形的文字行，Diff 刷新保留相同文件與視窗幾何；這些功能測試結果不等於正式效能通過。

## 重現引擎與回歸比較

```powershell
$env:GITGUI_BENCH_GIT = 'C:/Tools/PortableGit/cmd/git.exe'
$env:DOTNET_PROCESSOR_COUNT = '8'
dotnet run --project tests/GitGui.Benchmarks -c Release -- --prepare C:/Bench/new-fixture 100000 50000
dotnet run --project tests/GitGui.Benchmarks -c Release -- --measure C:/Bench/new-fixture C:/Bench/new-report.json
./scripts/Compare-Performance.ps1 -Baseline C:/Bench/baseline.json -Candidate C:/Bench/new-report.json -OutputPath C:/Bench/comparison.json
./tests/Test-PerformanceComparison.ps1
```

Prepare 只接受新目錄，報告也禁止覆寫。先建置，在沒有其他 build、test 或下載的時段執行。protocol2 的手動比較器對首次 Open 計時；status／Diff 暖機一次後取五次中位數，Core 圖形佈局按其固定重複協定計算。此統計協定只用於引擎回歸比較，不用來替代原生 UI 的逐樣本上限。任何主要指標退步超過 10% 就失敗。來源 `6c714ddec508273a098df84a32da6468183d00e8` 的同主機配對比較已通過：

| 引擎指標（ms） | M3 | 候選 | 變化 |
| --- | ---: | ---: | ---: |
| 首批 500 筆發布 | 1,945.38 | 574.46 | −70.47% |
| status 中位數 | 2,834.31 | 132.55 | −95.32% |
| 10k 行 Diff 中位數 | 307.93 | 43.09 | −86.01% |
| Core layout 中位數 | 0.2625 | 0.06035 | −77.01% |

配對輸入 SHA 與逐項判定保存在 `artifacts/m4-v060-final-engine-comparison.json`；此時 UI 與原生驗收仍未通過。

BenchmarkDotNet 同時使用合成與實際倉庫：

```powershell
$env:GITGUI_BENCH_SYNTHETIC = 'C:/Bench/new-fixture'
$env:GITGUI_BENCH_REAL = 'C:/Bench/git.git'
$env:GITGUI_BENCH_SOURCE = 'exact-source-commit'
dotnet run --project tests/GitGui.Benchmarks -c Release -- --filter '*RepositoryReadBenchmarks*' --artifacts C:/Bench/new-bdn-results
```

預設 1 launch、1 warmup、5 actual iterations，每 iteration 只呼叫一次，保留所有離群值且不更改電源計畫。每次建立全新 service／native reader／暫存資料庫。`CompleteOpenFirst500` 計完整 Open task，與 progressive 首屏不同；另外測 status、合成工作檔 Diff 及實際 HEAD 的歷史 Diff。完整協定見 儲存庫的 `tests/GitGui.Benchmarks/README.md`。BDN 必須確認所有 case 與實際樣本成功；程序 exit 0 本身不足以證明成功。

## 原生視窗驗證

```powershell
./scripts/Measure-DesktopPerformance.ps1 -AppPath C:/Apps/GITGUI/GitGui.App.exe `
  -Fixture C:/Bench/new-fixture -GitExecutable C:/Apps/GITGUI/PortableGit/cmd/git.exe `
  -OutputDirectory C:/Bench/new-native-reports -Samples 5
./tests/Test-DispatcherProfiler.ps1 -AppDirectory C:/Apps/GITGUI `
  -Repository C:/Bench/new-fixture -GitPath C:/Apps/GITGUI/PortableGit/cmd/git.exe -NoCanary
./tests/Test-DesktopPerformanceAcceptance.ps1
```

Probe 只接受自有產生器的 100k／50k fixture，以新的 TEMP 設定／資料庫顯示及關閉自有視窗，不讀取一般使用者設定。請勿把任意倉庫偽裝為 fixture；停止專用 fsmonitor 時只對該 fixture 執行 `git fsmonitor--daemon stop`。

正式時間終點是實際 render scope 加上對應 `CompositionBatch.Rendered` 完成、drawing session／surface update 返回。這仍不能證明 GPU 執行完成、Windows compositor 或實體顯示器呈現；RAF callback、全域 DWM 計數與 heartbeat 不可冒充該證據。

捲動固定至少五秒，名義 60Hz 兼容 60000/1001Hz；逐一檢查完整 raw frame，內部或端點空檔達 25.025ms 就失敗，不以恢復後的平均 FPS 抵銷。記憶體每 100ms 由 worker 取樣，保留前後邊界與全部最大值；任一值超標、取樣缺口過大、render error 或身分不一致皆失敗。

獨立外部 profiler 記錄 dispatcher Execute、UI Action Send 與主視窗 WndProc 的實際進出；nested span 為 inclusive，依原始 QPC 的產品階段重疊時間判定 16ms。不以 heartbeat queue delay 代替工作耗時，不扣除猜測的 profiler 負擔。泛型 Invoke、其他 native／COM callback 不在其覆蓋範圍。正式 gate 必須核對 PID、報告 SHA-256、七個產品 DLL 的 MVID／hash 與 profiler hook；未提供或無法核對時保持未驗證。

最終正式來源、六格 CI、同主機比較、實際 ZIP 的完整原生樣本、舊版快照復原與散佈雜湊都通過後，才會發布新版並在 Release 附上 `verification.json`／`SHA256SUMS.txt`。Headless 或固定主機測試不延伸為其他桌面環境、實體螢幕閱讀器或全新 VM 已通過。
