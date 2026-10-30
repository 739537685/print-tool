# 打印助手设计文档

日期：2026-10-23
状态：已确认

## 1. 目标

网页通过 WebSocket 把 PDF 电子面单发给本机打印助手，助手静默打印到热敏标签打印机（100×150mm），并回传结果。面向自研页面、同机部署场景。

## 2. 范围（第一版）

- 接收 PDF、静默打印、结果回执
- 打印机选择、份数、纸张尺寸（默认 100×150mm）
- 连续多单串行打印，任务历史与重打
- 托盘常驻、开机自启
- 6 位配对码鉴权，长期 token
- 同时兼容 HTTP 页面（ws）与 HTTPS 页面（wss）

不在第一版：多店铺/多电脑管理、服务端中转、云端任务同步、非 Windows 平台。

## 3. 架构

```
浏览器（自研页面 + print-client.js）
        │  ws://127.0.0.1:17800  /  wss://127.0.0.1:17801
        ▼
PrintAssistant.exe（WinForms 托盘 + Kestrel）
        ├─ WebSocket 网关（鉴权、Origin 白名单、消息校验）
        ├─ 打印队列（单消费者串行）
        ├─ PDF 渲染（PDFium / PDFtoImage，203dpi 位图）
        ├─ 打印执行（System.Drawing.Printing，自定义纸型、0 边距）
        └─ 本地存储（配置、任务记录、原始 PDF）
```

- 单实例（命名 Mutex），关闭主窗口最小化到托盘。
- 开机自启写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不申请管理员权限。
- 助手仅监听 127.0.0.1，不对外暴露。

### 3.1 证书策略

- 首次启动用 `System.Security.Cryptography` 生成自签 CA + 服务器证书，SAN 包含 `localhost`、`127.0.0.1`、`::1`。
- 服务器证书导入 `CurrentUser\Root`（用户根证书库，无需管理员，首次会弹系统确认）。
- 私钥存放 `%LOCALAPPDATA%\PrintAssistant\certs`，仅当前用户可读。
- 有效期 2 年，到期前 30 天自动轮换并重新导入。
- 用户拒绝信任时，`ws://127.0.0.1:17800` 仍可用（适用于 HTTP 页面）。

## 4. 协议

传输：WebSocket 文本帧，JSON UTF-8；单条消息上限 10MB；服务端每 30s 心跳 ping。

### 4.1 握手与鉴权

| 方向 | 消息 |
| --- | --- |
| C→H | `{"type":"pair","code":"123456","clientName":"web"}` |
| H→C | `{"type":"pair_ok","token":"<64hex>"}` |
| C→H | `{"type":"hello","version":1,"token":"<64hex>"}` |
| H→C | `{"type":"hello_ok","version":1,"helperVersion":"1.0.0","printers":[...]}` |
| H→C | `{"type":"error","code":"UNAUTHORIZED","message":"..."}` |

- 未鉴权连接只接受 `pair` / `hello`，10s 内未鉴权断开。
- 配对码 6 位数字，助手指纹（机器名 + 随机 ID）显示在托盘窗口，可手动刷新；配对成功后 token 长期有效，可在助手中撤销。

### 4.2 业务消息

| 方向 | 消息 |
| --- | --- |
| C→H | `{"type":"printers","reqId":"..."}` |
| H→C | `{"type":"printers","reqId":"...","printers":[{"name":"...","isDefault":true,"status":"ready","papers":[{"name":"...","widthMm":100,"heightMm":150}]}]}` |
| C→H | `{"type":"print","id":"client-uuid","reqId":"...","printer":"...","copies":1,"paper":{"widthMm":100,"heightMm":150},"pdf":"<base64>","meta":{"orderId":"..."}}` |
| H→C | `{"type":"accepted","id":"...","reqId":"...","taskId":"..."}` |
| H→C | `{"type":"result","id":"...","taskId":"...","status":"done","pages":1,"ms":1234,"warnings":[]}` |
| H→C | `{"type":"result","id":"...","taskId":"...","status":"failed","error":"PRINTER_OFFLINE","message":"..."}` |
| C→H | `{"type":"history","reqId":"...","limit":50}` |
| H→C | `{"type":"history","reqId":"...","tasks":[...]}` |
| C→H | `{"type":"reprint","reqId":"...","taskId":"..."}` |
| C→H | `{"type":"ping"}` / H→C `{"type":"pong"}` |

错误码：`BAD_REQUEST`、`UNAUTHORIZED`、`PAYLOAD_TOO_LARGE`、`INVALID_PDF`、`PRINTER_NOT_FOUND`、`PRINTER_OFFLINE`、`PRINT_FAILED`、`RATE_LIMITED`。

- `id` 由客户端生成幂等键：重复提交同 `id` 返回已有任务，不重复打印。
- PDF 以 base64 内联；前端 SDK 负责超过 6MB 时给出明确错误。

## 5. 打印流程

1. 收到 `print`：校验 token、字段、PDF 魔数 `%PDF-`、大小、页数上限（默认 10 页）。
2. 原始 PDF 写入 `%LOCALAPPDATA%\PrintAssistant\spool\{taskId}.pdf`，任务记录状态 `queued`。
3. 入队 `Channel<PrintTask>`，单消费者保证串行。
4. PDFtoImage + PDFium 以 203dpi 渲染每页为 `Bitmap`。
5. `PrintDocument`：`PrinterSettings.PrinterName`、`Copies`、自定义 `PaperSize(100mm,150mm)`、`Margins=0`、`OriginAtMargins=false`，`DrawImage` 铺满整页。
6. 驱动不支持指定纸型时回退到驱动默认纸型，结果附 `warnings`。
7. 每页打印完成回调中记录；结束写 `done`/`failed` 并推送 `result`。
8. 任务保留最近 500 条 / 30 天（可配置），重打复用 spool 中的 PDF 生成新任务。

### 5.1 可调参数（settings.json）

```json
{
  "wsPort": 17800,
  "wssPort": 17801,
  "defaultDpi": 203,
  "paperWidthMm": 100,
  "paperHeightMm": 150,
  "offsetXMm": 0,
  "offsetYMm": 0,
  "maxPdfBytes": 10485760,
  "maxPages": 10,
  "historyLimit": 500,
  "historyDays": 30,
  "allowedOrigins": ["*"]
}
```

## 6. 可靠性

- 队列串行，任务间互不影响；单任务失败不阻塞后续。
- 打印机离线/不存在、PDF 加密/损坏、驱动异常均转为 `failed` + 错误码。
- 打印过程中网页断线，助手继续完成任务并入库，重连后查 `history`。
- `id` 幂等，避免网页重试导致重复打印。
- 崩溃后重启恢复 `queued` 任务（标记为 `interrupted`，不自动重打，避免重复）。
- 所有异常写本地日志 `%LOCALAPPDATA%\PrintAssistant\logs`，按天滚动保留 7 天。

## 7. 前端 SDK

`web/print-client.js`（约 150 行，无依赖）：

- `createPrintClient({ token, name })`
- 自动选择 `wss`/`ws`（`location.protocol === 'https:'` 时用 wss）
- `pair(code)`、`getPrinters()`、`print({ pdf, printer, copies, paper })`、`getHistory()`、`reprint(taskId)`
- 断线指数退避重连，请求超时 30s，事件回调 `onStatus`
- 附 `web/demo.html` 可手动选 PDF 测试

## 8. 项目结构

```
src/PrintAssistant/
  Program.cs / App.cs                 应用入口、单实例、托盘
  Forms/MainForm.cs                   状态、配对码、打印机选择
  Forms/HistoryForm.cs                历史与重打
  Server/Gateway.cs                   Kestrel + WebSocket 处理
  Server/AuthService.cs               配对码、token、Origin 校验
  Server/Protocol.cs                  消息 DTO 与校验
  Server/TlsCertificateService.cs     自签证书生成/导入/轮换
  Printing/PdfRenderer.cs             PDFium 渲染
  Printing/LabelPrinter.cs            PrintDocument 打印
  Printing/PrinterCatalog.cs          打印机/纸型枚举
  Printing/PrintQueue.cs              串行队列与幂等
  Storage/AppSettings.cs
  Storage/TaskRepository.cs           tasks.jsonl + spool
  Storage/AppLog.cs
tests/PrintAssistant.Tests/           协议、纸型计算、鉴权、幂等、队列
web/print-client.js
web/demo.html
docs/protocol.md
```

技术：.NET 8（`net8.0-windows`，WinForms + ASP.NET Core Kestrel），PDFtoImage/PDFium 渲染，发布为 win-x64 自包含单文件。

## 9. 测试

- 单元：协议反序列化与校验、错误码映射、纸张毫米转像素、token 校验、幂等、队列顺序、证书 SAN 生成。
- 集成（手动）：在虚拟 PDF 打印机或真实标签机上验证 100×150 输出、份数、连续 10 单、离线失败、重打。
- 前端：demo 页面验证 ws/wss、配对、断线重连。

## 10. 风险

- 部分标签驱动对纸型/边距敏感：提供 offset、DPI、纸型回退配置。
- 浏览器信任自签证书需用户确认一次；可通过组策略预置。
- 首次打印会有系统添加打印机/驱动提示，取决于驱动实现。
