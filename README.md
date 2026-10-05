# Dual Audio

**把 Windows 系统声音同步输出到两台设备。** 分别控制每台设备的音量，为播放较快的设备增加延迟，并在同步播放时显示 macOS 风格的音量浮层。

![Dual Audio 音量浮层预览](Assets/volume-overlay-preview.png)

## 主要功能

- **双设备同步播放**：将一个输出设备上的系统声音镜像到另一台设备。
- **独立音量控制**：分别调整设备音量，并用总音量统一控制两台设备。
- **延迟调整与麦克风校准**：按实际播放和录音结果估算设备差异；校准失败或测量不可靠时保留现有延迟。
- **macOS 风格音量浮层**：同步播放期间按音量或静音键时显示；停止同步后隐藏并恢复系统默认按键行为。
- **托盘与快捷键**：关闭窗口后可留在系统托盘；使用 `Alt + Insert` 开始或停止同步。

## 使用方法

1. 在 Windows 声音设置里确认两台输出设备已连接并启用。
2. 打开 `Dual Audio.exe`，选择声音来源设备和第二台输出设备。
3. 点击“开始同步播放”，然后按需调整独立音量、总音量和延迟。
4. 要运行麦克风校准，请先持续播放有人声或节奏变化的音乐，并让麦克风能听到两台设备。

校准依赖当前房间、麦克风和输出设备的实际声音。没有可靠测量时会保留现有延迟。蓝牙编解码和扬声器响应也会影响最终听感。

## 从源码构建

需要 Windows 10/11 和 .NET 8 SDK：

```powershell
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## English

Dual Audio is a Windows utility that mirrors system audio to two output devices. It provides per-device volume, adjustable delay, microphone-based calibration, and a macOS-inspired volume overlay that appears only while synchronized playback is active.
