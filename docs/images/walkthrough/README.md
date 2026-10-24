# 第 20、21 章操作截圖

拍攝日期：2026-10-24。16 張標註圖片對應操作手冊中的圖 20-A～20-J、21-A～21-F。

GITGUI 圖片取自真正的 Windows 原生視窗，使用來源提交 `221bad7dc54c0186050202a3f4a528301571590b` 的 MainWindow、進階視窗與 Git 引擎。獨立教學啟動器僅指定示範資料／設定目錄、視窗標題與大小，不替換後端或模擬資料。使用隨附的 Portable Git。GitHub 圖片取自既有的公開 [linhercherng/GITGUI](https://github.com/linhercherng/GITGUI)，沒有新建 GitHub 倉庫，也沒有上傳示範專案。

本機示範實際經過 GUI 初始化、全部暫存、首次提交、新增本機遠端、Push、修改 README、選取暫存、再次提交、Fetch、再次 Push。兩次提交都已在隔離的 bare remote 核對，最終工作目錄乾淨、upstream 為 `origin/main`：

- 首次提交：`57e19679e7f89e7e85edb0836233a386a29888b8`
- 第二次提交／遠端 HEAD：`3378e5d6b62bd8e3a1c83653ed82af19fc1d53d4`

`20-07-remote.png` 中的 GitHub URL 是實際輸入表單的範例，拍攝後換成本機測試遠端才執行新增與 Push。GitHub 圖中的 SHA 是既有 GITGUI 提交，不能與本機示範的 SHA 當成同一筆。教學正文已說明此邊界。作者資料使用 `GITGUI Demo / demo@example.invalid`；沒有拍攝登入、Token 或密碼畫面。

`raw/` 保存 15 張未標註原圖。`callouts.json` 記錄每張圖的來源、裁切範圍、編號框座標與 SHA-256；其中工具列局部圖沿用原始主視窗截圖，所以共產生 16 張標註 PNG。只加入向量編號／框線與裁切，沒有重繪 UI 或修改成功訊息。

如需重製標註，使用已安裝 Node.js 與 `sharp`（可透過 `NODE_PATH` 指向既有安裝），在儲存庫根目錄執行：

```text
node scripts/Render-GuideScreenshots.cjs
```

操作手冊引用的檔案都放在此目錄第一層；未標註原圖與重製資料只留在原始碼。Windows 打包腳本會複製第一層的 16 張 PNG，供 HTML／Markdown 離線閱讀。

本次檢查：16 張標註圖逐張目視確認，21 章／21 個目錄錨點保留。執行實際打包腳本的手冊複製與 HTML 產生區塊，在新的驗證目錄中檢查 63 個本機連結／錨點，全部有效；16 張打包圖片與來源 SHA-256 相同。這是手冊與資產封裝驗證，沒有重建或覆寫既有發行 ZIP，也不代表 M4 效能驗收完成。
