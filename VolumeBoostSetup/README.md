# VolumeBoost 单文件安装器

`VolumeBoost-Setup.exe` 是自包含的 Windows x64 安装包，内含 `VolumeBoost.exe` 和 `ApplicationLoopback.dll`，不需要另装 .NET、Equalizer APO 或虚拟音频驱动。

安装是用户级的：程序和卸载器放在 `%LOCALAPPDATA%\Programs\VolumeBoost`，并创建桌面/开始菜单快捷方式。卸载器通过注册表的用户级卸载项提供，也可以直接双击安装目录中的 `VolumeBoost-卸载.exe`。

构建顺序：

```powershell
dotnet publish ..\PerAppVolume\PerAppVolume.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\PerAppVolume\publish
Copy-Item ..\PerAppVolume\publish\PerAppVolume.exe .\payload\PerAppVolume.exe -Force
dotnet publish .\VolumeBoostSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\release
```
