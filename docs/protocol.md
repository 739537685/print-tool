# 打印助手 WebSocket 协议

版本：1  
传输：WebSocket 文本帧，UTF-8 编码 JSON  
地址：

- `ws://127.0.0.1:17800/ws`（http 页面）
- `wss://127.0.0.1:17801/ws`（https 页面，使用助手自签证书）

仅监听回环地址，不对外网开放。消息字段使用 camelCase。

## 1. 连接与鉴权

1. 客户端建立 WebSocket 连接。
2. 若本地已有 token，发送 `hello`；助手返回 `hello_ok`，连接进入已授权状态。
3. 若没有 token，发送 `pair`（6 位配对码）；助手返回 `pair_ok` 与长期 token。
4. 未授权的连接只能发送 `pair` / `hello`，其它请求返回 `UNAUTHORIZED`。
5. 连接建立后 10 秒内未完成鉴权，助手会返回 `UNAUTHORIZED` 并断开。

token 保存在客户端（网页通常用 `localStorage`），助手侧以 SHA-256 摘要形式存储在
`%LOCALAPPDATA%\PrintAssistant\tokens.json`。配对码在助手面板中显示，每次启动重新生成；
同一 IP 每分钟最多尝试 5 次。

## 2. 消息格式

所有请求可带 `reqId`（字符串）。助手在响应中原样返回 `reqId`，客户端据此关联 Promise。
`pair_ok`、`hello_ok`、`pong`、`result` 是不带 `reqId` 的通知型消息。

### 2.1 配对

```json
{ "type": "pair", "code": "123456", "clientName": "web", "reqId": "p1" }
```

成功：

```json
{ "type": "pair_ok", "token": "…", "helperVersion": "1.0.0" }
```

失败：`error`，`code` 为 `UNAUTHORIZED` 或 `RATE_LIMITED`。

### 2.2 鉴权

```json
{ "type": "hello", "version": 1, "token": "…", "reqId": "h1" }
```

成功：

```json
{
  "type": "hello_ok",
  "version": 1,
  "helperVersion": "1.0.0",
  "printers": [
    {
      "name": "Xprinter XP-365B",
      "isDefault": true,
      "status": "ready",
      "papers": [{ "name": "100 x 150 mm", "widthMm": 100, "heightMm": 150 }]
    }
  ]
}
```

`status` 取值：`ready`、`unavailable`。

### 2.3 打印机列表

```json
{ "type": "printers", "reqId": "p2" }
```

响应：

```json
{ "type": "printers", "reqId": "p2", "printers": [ … ] }
```

### 2.4 打印

```json
{
  "type": "print",
  "id": "order-20261023-0001",
  "printer": "Xprinter XP-365B",
  "copies": 1,
  "paper": { "widthMm": 100, "heightMm": 150 },
  "offsetXMm": 0,
  "offsetYMm": 0,
  "pdf": "JVBERi0xLjQK…",
  "meta": { "orderId": "SO123", "carrier": "SF" },
  "reqId": "p3"
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 客户端任务幂等键，≤128 字符。相同 `id` 不会重复打印 |
| `pdf` | 是 | PDF 的 base64，≤ `maxPdfBytes`（默认 10 MB） |
| `printer` | 否 | 打印机名（不区分大小写）。省略时使用系统默认打印机 |
| `copies` | 否 | 份数，1–`maxCopies`（默认 20），缺省 1 |
| `paper` | 否 | 纸张毫米尺寸，缺省 `100 × 150`；每边 11–300 mm |
| `offsetXMm` / `offsetYMm` | 否 | 本单打印偏移，单位 mm，范围 -50–50；缺省使用助手配置 |
| `meta` | 否 | 字符串键值对，原样保存并在历史中返回 |

受理：

```json
{ "type": "accepted", "id": "order-20261023-0001", "taskId": "8f3c…", "reqId": "p3", "duplicate": false }
```

`duplicate` 为 `true` 表示该 `id` 已存在，助手不会再次入队。

打印结束（主动推送，可能晚于 `accepted`）：

```json
{
  "type": "result",
  "id": "order-20261023-0001",
  "taskId": "8f3c…",
  "status": "done",
  "pages": 1,
  "ms": 1834,
  "warnings": [],
  "error": null,
  "message": null
}
```

`status` 取值：`queued`、`printing`、`done`、`failed`、`interrupted`。

### 2.5 预览

预览不会创建打印任务，也不要求 `id`。助手复用打印渲染链路，把 PDF 按所选纸张和偏移合成到白底画布，
返回 PNG（base64，不带 data URL 前缀）。预览 DPI 由助手按纸张尺寸自动选择，最大 150 dpi、最长边约 1000 px。

```json
{
  "type": "preview",
  "paper": { "widthMm": 100, "heightMm": 150 },
  "offsetXMm": 0,
  "offsetYMm": 0,
  "pdf": "JVBERi0xLjQK…",
  "reqId": "p4"
}
```

响应：

```json
{
  "type": "preview",
  "reqId": "p4",
  "dpi": 150,
  "paperWidthMm": 100,
  "paperHeightMm": 150,
  "offsetXMm": 0,
  "offsetYMm": 0,
  "pages": [
    {
      "widthPx": 591,
      "heightPx": 886,
      "png": "iVBORw0KGgo…"
    }
  ]
}
```

`pages` 最多为 `maxPages` 页。PDF 会先缩放到纸张大小，再按偏移绘制；偏移导致的超出部分会被裁切。
`offsetXMm` / `offsetYMm` 省略时使用助手配置，同样限制在 -50–50 mm。

### 2.6 历史

```json
{ "type": "history", "limit": 50, "reqId": "p4" }
```

响应：

```json
{
  "type": "history",
  "reqId": "p4",
  "tasks": [
    {
      "taskId": "8f3c…",
      "clientId": "order-20261023-0001",
      "status": "done",
      "printer": "Xprinter XP-365B",
      "copies": 1,
      "pages": 1,
      "createdAt": "2026-10-23T10:21:03.118+08:00",
      "finishedAt": "2026-10-23T10:21:04.952+08:00",
      "error": null,
      "message": null,
      "warnings": [],
      "meta": { "orderId": "SO123" }
    }
  ]
}
```

### 2.7 重打

```json
{ "type": "reprint", "taskId": "8f3c…", "reqId": "p5" }
```

响应为 `accepted`（`id` 为 `null`，`taskId` 为新的任务 ID），随后推送 `result`。
原任务的 spool PDF 必须仍在保留期内，否则返回 `BAD_REQUEST`。

### 2.8 心跳

```json
{ "type": "ping" }
```

响应：

```json
{ "type": "pong" }
```

服务端每 30 秒发送 WebSocket ping 帧；客户端也可用应用层 `ping` 检测链路。

## 3. 错误

```json
{ "type": "error", "reqId": "p3", "code": "INVALID_PDF", "message": "pdf 文件头不正确" }
```

| 错误码 | 含义 |
| --- | --- |
| `BAD_REQUEST` | 字段缺失、JSON 非法、尺寸/份数越界 |
| `UNAUTHORIZED` | 未配对、token 无效或配对码错误 |
| `PAYLOAD_TOO_LARGE` | PDF 或单条消息超过限制 |
| `INVALID_PDF` | base64 合法但文件头不是 `%PDF-` 或内容损坏 |
| `PDF_PASSWORD_PROTECTED` | PDF 受密码保护或加密，无法直接打印 |
| `PRINTER_NOT_FOUND` | 打印机不存在或未找到默认打印机 |
| `PRINTER_OFFLINE` | 驱动报告打印机不可用 |
| `PRINT_FAILED` | 驱动/渲染阶段失败 |
| `RATE_LIMITED` | 单连接 10 秒内超过 60 条消息，或配对尝试过多 |
| `INTERNAL_ERROR` | 未预期的服务端异常 |

`result.status = "failed"` 时的 `error` 字段同样使用上述错误码。

## 4. 限制与幂等

- 单条消息上限约为 `maxPdfBytes × 4/3 + 256 KB`，超限后连接被关闭。
- 单连接每 10 秒最多 60 条消息。
- PDF 最多渲染 `maxPages` 页（默认 10 页），超出部分丢弃并写入 `warnings`。
- 预览同样受 `maxPages` 限制；预览 DPI 最高 150，最长边约 1000 px，超长纸张会自动降低 DPI。
- 相同 `id` 重复提交返回 `duplicate: true` 与原有 `taskId`；若原任务已结束，会立即补发 `result`。
- 打印任务串行执行；单个任务失败不影响后续任务。
- 网页断线不会中断已受理的打印；重连后可查 `history`，或按原 `id` 重发以获取结果。

## 5. 前端 SDK

`web/print-client.js` 封装了上述协议：

```html
<script src="print-client.js"></script>
<script>
  const client = PrintAssistant.createPrintClient({
    name: 'my-shop',
    onStatus(status, detail) {
      console.log(status, detail);
    },
    onResult(result) {
      console.log('打印完成', result.taskId, result.status);
    }
  });

  await client.connect();

  if (!client.isConnected()) {
    await client.pair('123456');
  }

  const printers = await client.getPrinters();
  const previewImage = document.querySelector('#preview');
  const preview = await client.preview({
    pdf: fileOrBase64,
    paper: { widthMm: 100, heightMm: 150 },
    offsetXMm: 0,
    offsetYMm: 0
  });
  previewImage.src = preview.pages[0].dataUrl;

  const response = await client.print({
    id: 'order-0001',
    pdf: fileOrBase64,
    printer: printers[0]?.name,
    copies: 1,
    paper: { widthMm: 100, heightMm: 150 },
    meta: { orderId: 'SO123' }
  });

  console.log(response.taskId, response.result.status);
</script>
```

接口：

| 方法 | 说明 |
| --- | --- |
| `connect()` | 建立连接；有 token 时自动 `hello` |
| `pair(code)` | 配对并持久化 token |
| `setToken(token)` / `getToken()` | 手动设置或读取 token |
| `getPrinters()` | 返回 `PrinterInfo[]` |
| `getHistory(limit)` | 返回 `TaskView[]` |
| `preview(payload, options)` | 生成打印预览，返回 `{ dpi, paperWidthMm, paperHeightMm, offsetXMm, offsetYMm, pages }`；每页含 `dataUrl` |
| `print(payload, options)` | 提交打印，默认等待最终 `result`；`waitForResult: false` 时仅返回 `accepted` |
| `reprint(taskId, options)` | 重打历史任务 |
| `ping()` | 应用层心跳 |
| `reconnect()` / `close()` | 手动重连 / 关闭（关闭后不再自动重连） |
| `isConnected()` | 已连接且已鉴权 |

选项：`token`、`name`、`url`、`wsPort`、`wssPort`、`secure`、`requestTimeout`（默认 30s）、
`resultTimeout`（默认 180s）、`previewTimeout`（默认 60s）、`autoReconnect`（默认 `true`）、
`maxReconnectDelay`（默认 30s）、`storageKey`、`storage`、`onStatus`、`onResult`。

`onStatus` 的状态值：`connecting`、`connected`、`unpaired`、`disconnected`、`reconnecting`、
`result`、`server-error`、`token`、`closed`。

断线后按指数退避重连（0.5s 起，加入抖动，最长 30s）；重连成功且 token 有效时会自动重新鉴权。
