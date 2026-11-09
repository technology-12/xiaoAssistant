# 第三方组件清单

来源：当前 NuGet 还原产生的 project.assets.json 与各包内 nuspec。开发框架引用包不随应用分发，未列入。

| 组件 | 版本 | 许可证声明 | 项目/许可证来源 |
|---|---|---|---|
| CsvHelper | 33.0.1 | MS-PL OR Apache-2.0 | https://joshclose.github.io/CsvHelper/ |
| HarfBuzzSharp.NativeAssets.macOS | 7.3.0.2 | 见组件随附声明及来源链接 | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp.NativeAssets.Win32 | 7.3.0.2 | 见组件随附声明及来源链接 | https://go.microsoft.com/fwlink/?linkid=868515 |
| HarfBuzzSharp | 7.3.0.2 | 见组件随附声明及来源链接 | https://go.microsoft.com/fwlink/?linkid=868515 |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | https://dot.net/ |
| Microsoft.Bcl.HashCode | 1.1.1 | MIT | https://github.com/dotnet/corefx |
| Microsoft.CSharp | 4.7.0 | MIT | https://github.com/dotnet/corefx |
| Newtonsoft.Json | 13.0.3 | MIT | https://www.newtonsoft.com/json |
| PdfPig.Rendering.Skia | 0.1.9.1 | Apache-2.0 | https://github.com/BobLd/PdfPig.Rendering.Skia |
| PdfPig | 0.1.9 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| SkiaSharp.HarfBuzz | 2.88.8 | 见组件随附声明及来源链接 | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SkiaSharp | 2.88.9 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| Stub.System.Data.SQLite.Core.NetFramework | 1.0.119 | 见组件随附声明及来源链接 | https://system.data.sqlite.org/ |
| System.Buffers | 4.5.1 | 见组件随附声明及来源链接 | https://dot.net/ |
| System.Data.SQLite.Core | 1.0.119 | 见组件随附声明及来源链接 | https://system.data.sqlite.org/ |
| System.Memory | 4.5.5 | 见组件随附声明及来源链接 | https://dot.net/ |
| System.Numerics.Vectors | 4.5.0 | 见组件随附声明及来源链接 | https://dot.net/ |
| System.Runtime.CompilerServices.Unsafe | 4.5.3 | 见组件随附声明及来源链接 | https://dot.net/ |
| System.Threading.Tasks.Extensions | 4.5.4 | 见组件随附声明及来源链接 | https://dot.net/ |
| Tesseract | 5.2.0 | Apache-2.0 | https://github.com/charlesw/tesseract/ |

完整的包内许可证与 nuspec 副本见 third-party-licenses/。SPDX 许可证正文另附在该目录根部。

SQLite、SkiaSharp/HarfBuzz 和 Tesseract/Leptonica 随主线包提供 x86/x64 本机组件；OCR 语言资源来自 tesseract-ocr/tessdata_fast 4.1.0（Apache-2.0），来源与散列见 ocr/sources.json，许可证见 ocr/tessdata/LICENSE。

1.0.7 主线的 VC++ 2019 14.29.30157.0 文件从微软签名的可再分发包提取；UCRT 10.0.10240.16384 文件来自 Windows SDK 的 Redist/ucrt/DLLs 可再分发目录。二者采用应用目录内部署，不运行 VC++ 系统安装器，不从 System32 复制运行库。来源和逐文件 SHA-256 见 native-runtime.json，VC++ 安装器随附许可见 third-party-licenses/MICROSOFT-VC-LICENSE.rtf。部署依据：[Microsoft C++ 部署方式](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170)、[Universal CRT 部署](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-160)。实际 Win7 加载验收尚未完成。

XP 包仅分发静态 CRT 的 SQLite 本机组件，不分发上述 OCR/VC++ 动态运行库；其真实 XP 验收状态见包内说明。
