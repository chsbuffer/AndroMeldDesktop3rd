# 本地 native assets

`build/NativeAssets.targets` 把本地预编译文件接入 SDK 的 Content/AssignTargetPaths 管线，复用 build、项目引用传递、publish、增量复制和 clean。通过 `IsRidAgnostic=false` 让 RID 传递到类库。

调用方提供包含 RID 子目录的根目录：

```xml
<ItemGroup>
  <NativeAssetDirectory Include="$(MSBuildProjectDirectory)/../../artifacts/prebuilt/runtimes" />
</ItemGroup>
<Import Project="../../build/NativeAssets.targets" />
```

源文件按 NuGet 布局：`runtimes/win-x64/native/tool.exe`、`runtimes/win-x64/native/nested/data.bin`、`runtimes/linux-x64/native/tool`。

| 构建方式 | 输出 |
| --- | --- |
| 未指定 RID | `runtimes/<rid>/native/...`，保留嵌套目录 |
| `-r win-x64` | 平铺到根目录，嵌套目录压平 |
| `-r linux-x64` | 平铺到根目录 |

只选取 `native/` 内文件；复制/发布使用 `PreserveNewest`，`ExcludeFromSingleFile` 保留在单文件外。文件放在 Git 忽略的 `artifacts/`；ADB 在 `BeforeTargets="IncludeNativeAssets"` 中准备，非 Linux 的 FFmpeg 由用户预放。该接入层不生成 NuGet RID 兼容图和 `.deps.json` 原生探测记录；ADB 和非 Linux 的 FFmpeg 查找程序目录和 `runtimes/<当前 RID>/native/`。Linux 构建/发布跳过本地 FFmpeg assets，运行时由系统动态加载器查找安装的 FFmpeg 9 共享库。
