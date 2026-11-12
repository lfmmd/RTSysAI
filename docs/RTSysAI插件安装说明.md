# RTSysAI 插件安装说明

## 安装位置

将压缩包中的 `AIPlugin` 文件夹复制到 **RTSys 安装目录下的 `Plug_in` 文件夹**，最终目录结构应为：

```text
<RTSys安装目录>\Plug_in\AIPlugin\RTSysPlugInNative.dll
<RTSys安装目录>\Plug_in\AIPlugin\RTSysPlugInAdapter.dll
<RTSys安装目录>\Plug_in\AIPlugin\RTSysPlugInWorker.exe
<RTSys安装目录>\Plug_in\AIPlugin\skills\...
```

例如 RTSys 安装在 `C:\Program Files\RTSys` 时，插件目录为：

```text
C:\Program Files\RTSys\Plug_in\AIPlugin
```

不要把插件放到桌面目录，也不要把 DLL 单独复制到 `Plug_in` 根目录。

## 安装步骤

1. 退出 RTSys。
2. 如果已有 `Plug_in\AIPlugin` 文件夹，先备份或改名保存。
3. 解压本压缩包，将其中的 `AIPlugin` 文件夹复制到 RTSys 安装目录的 `Plug_in` 文件夹中。
4. 确认 `AIPlugin` 下直接包含插件 DLL、Worker 程序、配置文件和 `skills` 文件夹。
5. 启动 RTSys，插件由宿主加载。

## 使用与注意事项

- 首次使用时，在插件面板的设置中配置 AI 服务地址和 API Key。
- 涉及程序或控制器写入的操作可能需要在插件界面确认；执行前请核对目标工程和设备状态。
- 当前版本暂时禁用了 HMI 页面读取/修改工具及 `/api/hmi` HTTP 路由。打开 `.hmi` 页面时，AI 会被告知暂不可操作 HMI。
- 本包包含插件运行所需的 RTSys 依赖库和 `skills` 资料。不要只安装单个 DLL。
- 如需卸载，退出 RTSys 后删除或移走 `Plug_in\AIPlugin` 文件夹即可。

## 故障排查

- 插件未显示：检查是否放在 RTSys 安装目录的 `Plug_in\AIPlugin` 下，并完全重启 RTSys。
- 启动或加载失败：确认压缩包解压完整，且没有把文件放进额外嵌套的一层 `AIPlugin\AIPlugin`。
- 替换升级失败：确认 RTSys 已退出后再覆盖插件目录；旧目录建议先备份，以便回退。
