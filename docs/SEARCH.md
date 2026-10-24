# 应用搜索

- 应用名称/包名支持中文全拼、首字母、混合拼写、部分音节和多音字；本地 `search-pinyin.json` 保存 402 个音节、8843 个字符的读音表。
- 手机屏幕、文件、终端按设备能力加入结果；搜索终端优先激活运行中或启动中的窗口，设备页可新建独立终端。
- 域名或 http(s) 链接显示“在 Android 中打开”：省略协议补 `https://`；即使同时匹配应用也保留链接动作，顺序在应用之后；路径、查询和 fragment 原样交给 Android。tel/mailto/geo 分别发拨号、邮件、地图 Intent。
- 其它无匹配文本显示“复制到 Android 剪贴板”，trim 后只在点击/回车时发送；自定义 scheme、`intent:`、`file:` 都当普通文本，不尝试启动。IME 输入中不提交；自定义 URI 回退为文本。
- 裸域名规则不是 IANA 一级域名名单：合法标签、末级 2–63 个字母或 `xn--`、有效端口；裸 IP、localhost、单字母/数字末级不算。输入最多 8192 UTF-16 code units，内部空格、控制字符和 DEL 不作链接。
- 协议：`[4,1]+{c:"open-window",intentUrl,w,h,dpr}`，不能混入 pkg/userId/kind；只允许 http/https/tel/mailto/geo，失败返回无包名 `window-state.failed`；拨号与浏览分别使用 ACTION_DIAL 或 VIEW+BROWSABLE，由系统 `resolveActivity` 决定应用。
- 复制用类型化 `SetClipboardTextAsync` → `[3,2]+{text}`，不依赖自动剪贴板开关，不请求桌面剪贴板，无服务端完成 ACK；不记录正文、搜索历史或完整链接。
