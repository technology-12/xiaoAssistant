# 1.0.5 PDF 读取失败诊断

检查日期：2026-11-08。结论：此次输入是没有文字层的扫描图片 PDF；1.0.5 的两个 PDF 读取入口均只提取已有文字，未实现 OCR，因此返回 `UNSUPPORTED_FORMAT`。

后续状态：已在 [1.0.6 本地 OCR 更新](OCR_1.0.6.md) 中修复并打包。下文保留原版故障定位记录。

## 实际任务证据

只读检查了便携版用户数据中的任务 `5a22d0b95de04b89956f50a317130add`，没有修改配置、历史记录或原文件。

| 北京时间 | 实际工具 | 结果 |
|---|---|---|
| 2026-11-08 11:01:04—11:01:05 | extract_pdf_text | UNSUPPORTED_FORMAT；共 10 页，检测到图像，无可提取文字，未提供 OCR |
| 2026-11-08 11:03:07 | read_document_text | UNSUPPORTED_FORMAT；无可提取文字，未启用 OCR |

用户报告的 `read.text` 对应这次任务中的文档读取失败；日志内保存的实际工具名是 `read_document_text`。这两次调用都读取了同一份 PDF，改用另一入口不能增加 OCR 能力。

## 文件与解析复现

使用任务快照中的原始输入，文件大小 7,378,653 字节（约 7.04 MiB），SHA-256：`358b8c19c01cc6caf04fd803bbfb98657103ed4a695d36f48be8e00812957f9a`。

1. 独立使用 pypdf 检查：未加密，10 页；每页可提取字符数 0、字体资源数 0、一个图像对象。解析结构成功，支持扫描件判断。
2. 用 **1.0.5 已发行 DLL** 调用 `PdfText.Extract` 复现：`page_count=10`、`total_chars=0`、`likely_scanned=true`。未用后续 XP 改造中的源码构建来代替版本复现。
3. 验证上述探针加载的 Tools、Core、PdfPig、PdfPig.Core 四个 DLL 与原始 1.0.5 ZIP 内文件 SHA-256 一致。

该文件未触发当前 10 MiB 大小限制。本次也没有触发单页 2 万字符、总计 20 万字符的提取限制。解析已成功返回 10 页，没有发现本次失败由加密或解析异常造成的证据。

开发诊断产物位于 `artifacts/diagnostics/pdf-read-1.0.5/`：

- `independent-inspection.json`：逐页文字、图像、字体计数及输入散列。
- `PdfReadProbe.cs` / `PdfReadProbe.exe`：只读探针，参数为发行程序集目录和输入 PDF 路径。
- `shipped-parser-result.json`：发行版解析结果。
- `binary-verification.json`：四个发行程序集的 ZIP 一致性检查。

这些产物不包含原文、模型凭据或完整聊天内容；探针不初始化产品 App 或状态存储。

## 代码原因

- `src/WorkAssistant.Tools/PdfText.cs` 的 `Extract` 使用 PdfPig 的 `GetPage(...).Text`，仅返回已有文字；检测图像只用于扫描件提示。
- `src/WorkAssistant.Tools/DocumentTextTools.cs` 的 PDF 分支调用同一 `PdfText.Extract`，当 `TotalChars==0` 时抛出“PDF 没有可提取的文字；未启用 OCR”。
- `src/WorkAssistant.Tools/ExportXlsxTool.cs` 的 `ExtractPdfTextTool` 同样在零字符时返回错误，并明确提示 OCR 属于后续能力。

因此，这是当前产品的扫描件读取能力缺口；只更改模型提示词、重试或切换这两个工具无法读出图片中文字。模型 API 计算能力也不会自动替代本地没有实现的页面渲染与 OCR 流程。

## 修复方向（本轮未改产品代码）

保留文本型 PDF 的快速提取；无文字或图片页自动转入本地“逐页渲染 → 中文 OCR → 带页码正文 → 统一分页阅读”流程。混合型 PDF 按页判断，不能只在整份文档总字符为零时才触发。应提供进度、取消、内存与并发限制、来源散列缓存、低置信度提示及失败页范围，避免静默漏读。OCR 结果不得无提示地替代原文事实。

结合已确立的 XP x86/x64 目标，OCR 引擎、PDF 渲染器和语言数据包需要逐平台验证，不预先认定最新依赖支持 XP。所有文档处理仍在本机完成。临时处理办法是先在本地 OCR 软件将 PDF 转成带文字层的 PDF 或 DOCX，再交给 1.0.5 读取。

界面还应区分“扫描件需要文字识别”和普通工具执行异常，给出可操作的说明与选项，而不是仅显示 `read.text 失败`。本轮仅完成原因定位和复现，没有修改正式源码或重新打包。
