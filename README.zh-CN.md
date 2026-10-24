# Tiny11 构建器

使用原生 WinUI 应用构建精简版 Windows 11 ISO。可下载官方 Windows 映像或使用自有映像，选择要移除的内容并生成 ISO。

## 下载

[**获取最新版本**](https://github.com/ntdevlabs/tiny11builder/releases/latest)

Intel/AMD 电脑选择 **x64**，Windows on ARM 选择 **ARM64**。解压整个 ZIP 后运行 `Tiny11Builder.exe`，需要管理员批准。

## 构建模式

| 模式 | 说明 |
| --- | --- |
| Tiny11 | 移除捆绑应用与不需要的功能，保留更新、Defender 与恢复。 |
| Tiny11 Core | 进一步精简，移除维护与恢复支持并禁用 Defender。 |
| 自定义 | 单独选择要移除或保留的应用、组件、功能与服务。 |

Turbo 模式可加快映像处理。映像提交或压缩后，中断的构建可从已保存进度继续。Core 与组件存储移除会自动使用旧版 Windows 安装程序。

**Core 无法更新或维护。受保护的自定义移除可能破坏 Windows。安装前请在虚拟机中测试 ISO。**

## 构建（Windows）

在已安装 .NET 8 SDK 与 Windows App SDK 工作负载的 Windows 上：

```powershell
cd <仓库根目录>
dotnet publish src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-x64 --self-contained
```

或使用仓库脚本：

```powershell
.\scripts\package.ps1 -Platform x64
```

输出为自包含的 `Tiny11Builder.exe`（具体路径见脚本结束时的提示）。

## 上游

本仓库为 [OuRunJu/tiny11builder](https://github.com/OuRunJu/tiny11builder) 的简体中文界面分支；上游项目见 [ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder)。
