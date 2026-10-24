# 音频转发

- 协议：控制 `[2,3]+{on,sessionId}` 订阅；手机发 `[2,1]+{sampleRate,channels,format:"pcm_s16le"}` 和 `[2,2]+uint64 大端时间戳+S16LE 交织采样`。没有音频 ACK，正文最多 1 MiB。
- sessionId 是音频所有者；所有镜像共用一条设备音频流，不按每个 `/media` 重复订阅。采集的是设备音频，不是应用独立混音。
- 手机声明 audio 和 device-audio 且存在可控镜像时订阅；聚焦切换只换所有者；没有镜像、设置关闭或断开即取消。
- Protocol 校验格式/通道/截断/对齐；错误通过 AudioFailed 报告，不中断视频、文件、终端或剪贴板。
- AudioSharing reader 只写容量 8 的 DropOldest channel；UI tick 每次最多 8 包；SDL 初始化、重配、播放和释放在 UI。保留约 120ms 预缓冲、200ms 最大水位，过量丢旧样本，欠载重新预缓冲。
- 设置默认开启，重连保留；关闭音频、最后一个镜像或断开时释放本地播放器。音频失败只报告并退订，不停止视频；macOS 需自行提供 SDL 原生库。
