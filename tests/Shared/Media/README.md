合成 H.264 fixture，不含手机画面。FFmpeg lavfi color（blue 320x240、red 240x320、blue 374x648），libx264 ultrafast/zerolatency，单帧、keyint=1、aud=1、Annex B。

用于核对单个 access unit 即刻出帧、BGRA 通道顺序、SPS 分辨率切换，以及非对齐宽度的连续帧像素转换；不会下载或保存真机屏幕。
