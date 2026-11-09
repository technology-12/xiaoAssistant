# 工作助手 · 桌面设计系统

2026-10-31 / V3 / 实际 WPF 实现的设计依据。

## 产品与方向

- 普通办公用户；Win7 SP1、.NET Framework 4.8、WPF。
- 以任务输入、受控执行和文件交付为核心；不添加尚未实现的能力入口。
- 延续用户查看的“留白工作室”：暖纸背景、墨色操作、陶土点缀。
- 界面保持中文产品文案，不在常规主流程显示 API 字段名、GUID 或 SHA-256。

## 技能检索及人工判断

使用 ui-ux-pro-max 本地脚本：

1. `AI productivity desktop workspace --design-system`。
2. 首次结果的 Pattern 是营销页，因此按查询契约收窄重试 `productivity workspace minimal --design-system`。
3. 重试仍为 Product Demo + Features。没有核实到适用的原生桌面布局匹配，不采用或持久化该营销结构；本文件布局是结合现有项目和已有方向制定的设计。
4. WPF `layout typography resources` 返回 Auto/Star 自适应、资源管理、高对比度等指南；采用适用项。
5. UX `keyboard focus visible` 返回可见焦点规则；采用键盘焦点轮廓、抽屉内 Tab 循环和关闭后的焦点归还。

接受 Flat Design 的轻量层级和简单反馈；不照搬 teal/orange 配色或在线字体，避免破坏已有暖纸方向和离线字体可用性。高对比度、Win7 与多 DPI 需额外验证，不因引用指南就宣称已达标。

## 布局

- 默认 1180×780 DIP；原生系统标题栏。
- 224 DIP 侧栏；主区 Auto/*/Auto 三行。
- 首页内容最大 700 DIP，标题、输入、快捷卡共用左对齐线。
- 输入紧跟欢迎标题；会话状态下输入固定在底部，对话独立虚拟化滚动。
- 小于 1000 DIP 自动收起侧栏；可用明确按钮重新展开。
- 文件详情覆盖主区，宽 340 DIP，不再压缩对话内容到不可用。
- 设置页顶部字段标签，主体滚动、底部操作固定。

## 色彩与字体

| Token | 值 |
|---|---|
| SurfaceMain | #FDFCF9 |
| SurfaceSidebar | #F1F0EC |
| SurfaceElevated | #FFFFFF |
| TextPrimary | #252622 |
| TextSecondary | #65685F |
| Accent | #AD5232 |
| ActionPrimary | #30382E |
| ActionForeground | #FFFFFF |

字体优先 Microsoft YaHei，回退 Segoe UI。标题 34，正文 14～15，辅助 12。关键辅助文案不使用 10px。按钮、文件卡、输入框分别使用 10/12/16 圆角；不使用装饰性动画、模糊背景或大面积渐变。

## 交互

- Ctrl+Enter 发送；Enter 换行；Ctrl+N 新建；Esc 关闭文件详情。
- 未授权、空任务、取消中按钮禁用并有原因；新建任务不删除历史。
- 默认不展开技术日志；执行步骤完成后折叠。
- 文件类型使用对应图标；所有 Path 使用 Uniform 缩放，避免 24px 几何裁切进小图标。
- 抽屉出现时焦点进入关闭按钮；Tab 在抽屉中循环；关闭后归还焦点。
- 主界面不硬编码销售额等示例结果，显示真实工具产物和任务记录。

## 文件定位

- Tokens.xaml：颜色、字体与度量。
- Studio.xaml：按钮、图标和焦点样式。
- MainWindow.xaml：首页、会话、输入与文件抽屉。
- SettingsWindow.xaml：模型配置表单。
- App.xaml.cs `--render-ui`：隔离配置的 WPF 原生渲染检查；`--selftest-ui`：状态回归。
