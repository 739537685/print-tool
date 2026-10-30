# 打印助手实现计划

> 依据 `docs/plans/2026-10-23-print-assistant-design.md`

**目标：** 交付 Windows 托盘打印助手 + 前端 SDK，网页通过 WebSocket 发送 PDF 面单并静默打印 100×150mm 热敏标签。

**架构：** WinForms 托盘程序内嵌 Kestrel，提供 ws/wss 本机 WebSocket；消息校验后进入串行队列，PDFium 渲染为 203dpi 位图，由 PrintDocument 静默打印；任务落盘可查历史与重打。

**技术栈：** .NET 8（net8.0-windows）、WinForms、ASP.NET Core Kestrel、PDFtoImage/PDFium、System.Text.Json、xUnit。

---

## 任务 1：解决方案骨架

**文件：** `PrintAssistant.sln`、`src/PrintAssistant/PrintAssistant.csproj`、`src/PrintAssistant/app.manifest`、`tests/PrintAssistant.Tests/PrintAssistant.Tests.csproj`

- `dotnet new sln`、`dotnet new winforms`、`dotnet new xunit`
- 主项目加 `FrameworkReference Microsoft.AspNetCore.App`、`PackageReference PDFtoImage 5.4.0`
- 启用 nullable、implicit usings、win-x64 发布配置

**验证：** `dotnet build` 通过。

## 任务 2：配置、日志、任务存储

**文件：** `Storage/AppSettings.cs`、`Storage/AppLog.cs`、`Storage/TaskRepository.cs`

- `AppSettings`：端口、DPI、纸型、偏移、上限、历史保留、Origin 白名单；读写 `%LOCALAPPDATA%\PrintAssistant\settings.json`
- `AppLog`：按天滚动、保留 7 天
- `TaskRepository`：`tasks.jsonl` 追加写；启动加载；按 clientId 查幂等；按 taskId 查重打；启动时将 `queued/printing` 标为 `interrupted`；清理超出条数/天数的记录与 spool 文件

**测试：** 新建临时目录验证追加、幂等查找、启动中断标记、清理。

## 任务 3：协议 DTO 与校验

**文件：** `Server/Protocol.cs`

- 请求/响应 record，`JsonSerializerOptions` camelCase + enum string
- `ValidatePrintRequest`：id 非空、printer 非空、copies 1–20、paper 默认 100×150、base64 合法、PDF 魔数、字节上限、页数上限由渲染阶段校验
- 错误码常量 `ProtocolErrors`

**测试：** 合法消息通过；缺字段、非法 base64、非 PDF、超限、份数越界返回对应错误码。

## 任务 4：鉴权与配对

**文件：** `Server/AuthService.cs`

- 启动生成 6 位配对码，可刷新
- `pair(code, clientName)` 验证码后生成 32 字节 token，SHA256 存 `tokens.json`
- `ValidateToken` 常数时间比较
- 配对尝试限流（5 次/分钟）

**测试：** 正确/错误配对码、token 签发与校验、限流、刷新后旧码失效。

## 任务 5：TLS 证书

**文件：** `Server/TlsCertificateService.cs`

- 生成自签证书：CN=PrintAssistant Local，SAN localhost/127.0.0.1/::1，EKU serverAuth，有效期 2 年
- 公钥导入 `CurrentUser\Root`；PFX 存 `certs` 并限制 ACL
- 已存在且可信则复用；到期前 30 天轮换
- 失败不阻塞，仅禁用 wss

**测试：** SAN 与有效期断言；重复调用幂等（不重复导入）。

## 任务 6：打印能力

**文件：** `Printing/PrinterCatalog.cs`、`Printing/PdfRenderer.cs`、`Printing/LabelPrinter.cs`

- 枚举 `PrinterSettings.InstalledPrinters`、默认打印机、纸型
- `PdfRenderer.Render(pdfPath, dpi)` 用 PDFtoImage 渲染并转换为 `Bitmap`；页数超限抛 `INVALID_PDF`
- `LabelPrinter.Print(bitmap列表, printer, copies, paper)`：自定义纸型、0 边距、`StandardPrintController` 静默；不支持纸型时回退并返回 warning；逐份打印保证份数
- 毫米→百分之一英寸换算函数

**测试：** 纸型换算、份数循环参数、无打印机时报 `PRINTER_NOT_FOUND`（不实际出纸）。手工集成测试真实标签机。

## 任务 7：串行打印队列与幂等

**文件：** `Printing/PrintQueue.cs`

- `Channel<PrintTask>` 单消费者；任务状态流转 queued→printing→done/failed
- 相同 clientId 返回已有任务
- 单任务异常隔离；结果通过回调/事件推送

**测试：** 顺序执行、失败不阻塞、幂等、状态流转。

## 任务 8：WebSocket 网关

**文件：** `Server/Gateway.cs`、`Program.cs`、`App.cs`

- Kestrel 监听 127.0.0.1:17800/17801，`/ws` 路由，消息上限 10MB
- 未鉴权仅允许 pair/hello，10s 超时；Origin 白名单
- 处理 printers/print/history/reprint/ping，reqId 原样回传
- `accepted`、`result` 推送；断线清理
- WinForms 单实例 Mutex、退出时优雅关闭 Kestrel

**测试：** 集成测试用 `WebApplicationFactory` 或本地启动 + `ClientWebSocket` 覆盖鉴权失败、配对、打印非法 PDF、历史查询。

## 任务 9：托盘 UI

**文件：** `Forms/MainForm.cs`、`Forms/HistoryForm.cs`、`App.cs`

- 托盘菜单：打开面板、历史、复制配对码、开机自启、退出
- 主面板：运行状态、配对码、刷新、打印机列表、端口、证书状态、最近日志
- 历史：列表、状态、失败原因、重打
- 关闭窗口最小化到托盘；退出确认
- 开机自启写 HKCU Run

**验证：** 手工运行，托盘/窗口/配对码/自启切换正常。

## 任务 10：前端 SDK 与 demo

**文件：** `web/print-client.js`、`web/demo.html`、`docs/protocol.md`

- `createPrintClient`：自动 ws/wss、token 持久化、配对、请求 Promise 化、30s 超时、指数退避重连、状态回调
- demo：选择本地 PDF、打印机、份数，显示结果与历史
- 协议文档与错误码

**验证：** demo 页面 `file://` 与本地 http 下连接 ws；可用 wss 时自动切换。

## 任务 11：打包与文档

**文件：** `README.md`、`scripts/publish.ps1`

- 发布命令：`dotnet publish src/PrintAssistant -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- README：安装、配对、前端接入、配置项、常见问题（纸张偏移、证书信任、防火墙）
- 冒烟测试：发布产物启动、端口监听、demo 完成一次打印

---

## 验收

- [ ] 网页（http 与 https）都能连接并配对
- [ ] 100×150mm PDF 静默打印，份数正确，连续 10 单不串单
- [ ] 失败任务有错误码，可重打，历史持久化
- [ ] 托盘常驻与开机自启可用
- [ ] 单元测试与集成测试通过
- [ ] 自包含单文件发布可运行
