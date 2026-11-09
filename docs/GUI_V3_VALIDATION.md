# GUI V3 实施与验证记录

2026-10-31。使用 ui-ux-pro-max 做方案检索、WPF 指南和可访问性检查；依据见 design-system/work-assistant/MASTER.md。

## 已实现

- 欢迎页标题与输入整合、双列快捷任务、重排侧栏与顶栏。
- 中文字体优先、正文层级、统一按钮状态及可见焦点。
- 文件卡横向换行、按文件类型显示图标，修复小图标几何裁切。
- 执行步骤去掉系统灰底；展开箭头跟随实际展开状态。
- 详情以覆盖式抽屉呈现，支持 Tab 循环、Esc、焦点归还。
- 窄窗口自动收起侧栏，窗口初始尺寸适配工作区域。
- 设置页重新排版，固定底部操作；演示模式允许缺省模型配置，在线模式保存错误后聚焦对应字段。
- Headless WPF 原生渲染入口，隔离配置与工作区，不访问模型，不污染用户任务记录。

## 验证结果

- Release 构建：成功，0 警告、0 错误。
- 已有回归测试：33/33 通过（正常用户权限环境）。沙箱环境对临时文件移动/联接/HTTP 初始化的限制已排除后重新验证。
- UI 状态自测：27/27 通过，涵盖授权、欢迎与任务状态、步骤结束、成果存在、历史回看、新建重置。
- 1180×760：实际 WPF 渲染首页、完成页、文件抽屉。
- 820×620：实际 WPF 渲染首页与完成页；侧栏收起，首页可纵向滚动。
- 540×680：设置页原生渲染，表单滚动、操作按钮可见。
- 初轮原生窗口检查已执行；后续图片由 WPF RenderTargetBitmap 从实际视图生成，非网页概念稿，无系统标题栏。

图片位于 docs/previews/studio-*.png。状态自测结果位于 artifacts/studio-v3-ui-selftest.txt。

## 验证边界

- 尚未使用 Win7 实机；不宣称 Win7 兼容性验收通过。
- 多显示器、高 DPI、系统高对比度、屏幕阅读器尚未完整验证；当前只有浅色主题。
- 未使用真实模型服务；任务交付使用 Fake 模型和样例 CSV，已有 HTTP 测试为 Mock。
- 自动渲染不替代全部指针/键盘交互验收；焦点、窗口变换等仍应在目标环境复核。

## 运行与复核

启动 release/WorkAssistant.Desktop.exe 查看新版。
更新前版本保留在 artifacts/release-before-studio-v3/。
构建时若旧开发程序正在运行，应关闭对应程序或输出到独立目录，避免 exe 文件锁冲突。

```powershell
& C:/Users/YUAN/.dotnet/dotnet.exe build src/WorkAssistant.Desktop/WorkAssistant.Desktop.csproj -c Release -m:1 -p:UseSharedCompilation=false -o artifacts/studio-v3
artifacts/studio-v3/WorkAssistant.Desktop.exe --render-ui --scene home --output C:/files/generate/gui_cli/docs/previews/studio-home.png
artifacts/studio-v3/WorkAssistant.Desktop.exe --render-ui --scene completed --output C:/files/generate/gui_cli/docs/previews/studio-completed.png
artifacts/studio-v3/WorkAssistant.Desktop.exe --selftest-ui --result C:/files/generate/gui_cli/artifacts/studio-v3-ui-selftest.txt
```
