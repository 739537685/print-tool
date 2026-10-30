# 打印助手

Windows 本机打印助手：网页把 PDF 电子面单通过 WebSocket 发过来，助手静默打印到
100 × 150 mm 热敏标签机，并回传打印结果。适用于顺丰、京东、淘宝等平台导出的 PDF 面单。

- 常驻托盘，开机自启，无主窗口打扰
- 内置 PDFium 渲染，不依赖 Adobe / SumatraPDF
- 只监听 `127.0.0.1`，不暴露到局域网
- 6 位配对码 + 长期 token，`http` 页面用 `ws`，`https` 页面用 `wss`
- 打印前按纸张和偏移生成预览，支持每单覆盖偏移
- 串行打印队列、任务幂等、失败错误码、历史与重打

## 环境要求

- Windows 10 / 11（x64）
- 已安装标签打印机驱动，系统里能看到该打印机
- 运行发布版无需安装 .NET；从源码运行需 .NET 8 SDK

## 安装与启动

1. 下载或发布 `PrintAssistant.exe`，放到固定目录（例如 `C:\Tools\PrintAssistant`）。
2. 双击启动。首次运行会在 `%LOCALAPPDATA%\PrintAssistant` 下创建设置、日志、证书和 spool 目录。
3. 托盘出现图标，主面板显示 6 位配对码与打印机列表。
4. 需要开机自启时，右键托盘图标勾选「开机自启」（写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，带 `--minimized`）。

关闭主面板只是最小化到托盘；退出请用托盘菜单「退出」。

### 自签证书

助手会自动生成一张 2 年有效期的自签证书，导入当前用户的「受信任根证书」，
用于 `https` 页面连接 `wss://127.0.0.1:17801`。证书临近过期前 30 天会自动轮换。
如果浏览器仍提示证书不受信任，重启浏览器；仍不行时检查
`certmgr.msc` →「受信任的根证书颁发机构」→「证书」中是否存在 `PrintAssistant Local`。

> 只信任自己的自签证书即可，不需要关闭浏览器安全检查。http 页面不涉及证书。

## 配对

1. 打开助手主面板，复制 6 位配对码。
2. 在网页里调用 `pair(code)`，或在 `web/demo.html` 中输入配对码点击「配对」。
3. 网页拿到 token 后自行保存（SDK 默认写 `localStorage`），后续用 `hello` 鉴权。

配对码每次启动重新生成；同一来源每分钟最多尝试 5 次。要撤销全部网页授权，删除
`%LOCALAPPDATA%\PrintAssistant\tokens.json` 后重启助手。

## 网页接入

把 `web/print-client.js` 放到你的前端项目：

```html
<script src="print-client.js"></script>
<script>
  const client = PrintAssistant.createPrintClient({ name: 'my-shop' });

  async function printLabel(file, printerName) {
    await client.connect();
    if (!client.isConnected()) {
      await client.pair(prompt('请输入助手配对码'));
    }

    const preview = await client.preview({
      pdf: file,
      paper: { widthMm: 100, heightMm: 150 }
    });
    previewImg.src = preview.pages[0].dataUrl;

    const response = await client.print({
      id: 'order-' + orderId,
      pdf: file,
      printer: printerName,
      copies: 1,
      paper: { widthMm: 100, heightMm: 150 },
      meta: { orderId: orderId }
    });

    if (response.result.status !== 'done') {
      throw new Error(response.result.error + ' ' + response.result.message);
    }
  }
</script>
```

要点：

- `pdf` 支持 `File` / `Blob` / `ArrayBuffer` / base64 字符串。
- `preview()` 只生成预览，不创建打印任务；返回的每页带 `dataUrl`，可直接赋给 `<img>`。
- `offsetXMm` / `offsetYMm` 可按单传入，省略时使用 `settings.json` 中的默认偏移。
- `id` 是幂等键，重试时保持同一个值即可避免重复打印。
- `print()` 默认等到最终 `result` 才 resolve；长时间打印可传 `{ waitForResult: false }`，
  再通过 `onResult` 或 `getHistory()` 获取结果。
- https 页面无法连接 `ws://`（混合内容限制），SDK 会自动改用 `wss://`。
- 完整协议见 [docs/protocol.md](docs/protocol.md)，可直接打开 `web/demo.html` 做联调。

## 配置

配置文件：`%LOCALAPPDATA%\PrintAssistant\settings.json`。修改后重启助手生效。

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
  "maxCopies": 20,
  "historyLimit": 500,
  "historyDays": 30,
  "allowedOrigins": ["*"]
}
```

| 字段 | 说明 |
| --- | --- |
| `wsPort` / `wssPort` | 本地监听端口 |
| `defaultDpi` | PDF 渲染分辨率，203 对应大多数标签机 |
| `paperWidthMm` / `paperHeightMm` | 默认纸张尺寸 |
| `offsetXMm` / `offsetYMm` | 默认打印偏移，用于校准边距，范围 -50–50；单次请求可覆盖 |
| `maxPdfBytes` | 单个 PDF 上限，默认 10 MB |
| `maxPages` | 单个 PDF 最多打印页数 |
| `maxCopies` | 单任务最大份数 |
| `historyLimit` / `historyDays` | 历史保留条数与天数 |
| `allowedOrigins` | 允许的网页来源；`["*"]` 表示不限制 |

数据目录：

```
%LOCALAPPDATA%\PrintAssistant\
  settings.json     配置
  tokens.json       网页 token（SHA-256 摘要）
  tasks.jsonl       任务历史
  spool\            待打印/可重打的 PDF
  logs\             按天滚动的日志，保留 7 天
  certs\            wss 自签证书
```

## 从源码构建

```powershell
dotnet build PrintAssistant.sln
dotnet test PrintAssistant.sln
```

运行：

```powershell
dotnet run --project src/PrintAssistant
```

发布自包含单文件：

```powershell
scripts\publish.ps1
# 或手动执行
dotnet publish src/PrintAssistant -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物默认输出到 `dist\`。单文件首次启动会把 PDFium/SkiaSharp 原生库解压到临时目录，属正常现象。

## 常见问题

**打印机列表是空的**
确认驱动已安装且在「设置 → 蓝牙和设备 → 打印机和扫描仪」中可见；以当前用户身份运行助手。

**打印位置偏移 / 内容被裁切**
先在驱动里把纸型设为 100 × 150 mm，再调整 `offsetXMm`、`offsetYMm`，或在单次打印请求中传偏移。
可先用 `web/demo.html` 的预览确认纸张比例、白边和偏移效果。
若驱动没有该纸型，助手会先尝试自定义纸型，失败后回退驱动默认纸型，并在 `result.warnings`
中返回 `CUSTOM_PAPER` 或 `PAPER_SIZE_FALLBACK`。

**网页提示连接失败**
确认助手正在运行、托盘图标存在，且端口未被占用：

```powershell
Test-NetConnection 127.0.0.1 -Port 17800
```

**https 页面连不上**
https 页面必须用 `wss://127.0.0.1:17801`。确认证书已导入受信任根证书并重启浏览器；
也可先用 `web/demo.html` 在 http 下验证打印链路。

**打印成功但没出纸**
检查打印机是否脱机、缺纸、暂停，或驱动是否弹出了保存文件对话框（应关闭「打印到文件」）。

**需要重置**
退出助手后删除 `%LOCALAPPDATA%\PrintAssistant`，重新启动即可。

## 目录结构

```
src/PrintAssistant/
  Program.cs                入口、单实例
  TrayAppContext.cs         托盘、生命周期
  Forms/                    主面板、历史
  Server/                   Kestrel、WebSocket、协议、鉴权、证书
  Printing/                 PDF 渲染、预览合成、纸张计算、打印、队列
  Storage/                  配置、任务持久化、日志、开机自启
tests/PrintAssistant.Tests/ 单元与集成测试
web/print-client.js         前端 SDK
web/demo.html               联调页面
docs/protocol.md            协议文档
scripts/publish.ps1         发布脚本
```
