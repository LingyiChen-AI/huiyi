# 会译（HuiYi）

Windows 上的会议实时字幕与纪要工具：把电脑里播放的会议声音（Zoom、Teams、腾讯会议、飞书、网页会议、视频……）变成实时双语字幕，每场会议留一份双语记录和纪要；按住一个键说中文，松开后用对方的语言说进会议。

## 功能

| 功能 | 说明 |
|---|---|
| 实时字幕 | 采集系统声音（WASAPI loopback），本机识别（SenseVoice：中、英、日、韩、粤），说话过程中约 0.6 秒刷新一次 |
| 翻译 | 实时字幕走 Google；整句在接入 AI 服务后由大模型翻译（带纪要里的人名术语表），否则 Google |
| AI 服务 | 供应商模式：可添加多个、随时切换。预设蚂蚁百灵、阿里云百炼、DeepSeek、火山方舟（豆包）、UCloud（聚合）、Kimi、智谱、MiniMax、腾讯混元、百度千帆、硅基流动、OpenAI、OpenRouter、本地 Ollama，选平台自动填 Base URL；也可自定义任何 OpenAI 兼容服务；「获取模型列表」直接读取平台当前的模型 |
| 实时面板 / 悬浮字幕 | 两种显示方式：深色的实时面板（逐字稿 + 纪要），或屏幕底部的悬浮字幕；都不抢焦点，默认不出现在屏幕共享和录屏里，可在设置里打开（会显示「共享中可见」） |
| 区分发言人 | 默认打开：按声音把对方的每个人分开，显示为「发言人 1、发言人 2…」，可以改成真名；接入 AI 服务后会根据对话猜名字 |
| 会议纪要 | 接入 AI 服务后每 45 秒左右更新一次，会议结束后根据整场再写一遍；所有时间都是真实时间：议题和要点标会上的钟点，截止日期、里程碑换算成具体日期 |
| 纪要模板中心 | 15 个内置模板：通用会议、决策会、头脑风暴、项目周会、每日站会、项目启动会、需求评审、技术方案评审、复盘会、客户拜访、经营分析会、一对一沟通、面试记录、培训笔记、访谈 / 播客；可设默认、每场会议单独换；可复制后自定义各部分（标题、写作要求、负责人、截止日期） |
| 会议记录 | 按日期分组，每场会议一份纪要和双语逐字稿，可改名、搜索、给发言人命名、重新整理纪要、导出 Markdown |
| 自动结束 | 会议软件挂断（放开麦克风）后自动结束；长时间没人说话时提醒或结束 |
| 同传发言 | 按住右 Ctrl 说话 → 本机识别 → 翻译 → 语音（使用蚂蚁百灵时用 Ming，否则 Windows 自带语音）→ 虚拟麦克风 VB-CABLE；没翻译成功的话绝不会说出去 |

## 使用

1. 下载 `HuiYi-<版本>-win-x64.zip`，解压后运行 `HuiYi.exe`（自带运行时，不用装 .NET）。
2. 首次启动会下载约 260 MB 的本机模型（语音识别、断句、发言人声纹；Hugging Face / GitHub，失败自动换镜像，下载后校验 SHA-256）。
3. 单击任务栏右下角通知区域里的图标开始 / 停止收听；右键菜单里有会议记录和设置。
4. 可选：在「设置 › AI 服务」里接入一个大模型（选平台、填 API key），打开纪要和更准的翻译。
5. 可选：装 [VB-CABLE](https://vb-audio.com/Cable/)，在会议软件里把麦克风选成「CABLE Output」，再在「设置 › 同传发言」里打开。

系统要求：Windows 10 2004（19041）或更新，x64。

## 数据放在哪

| 内容 | 位置 |
|---|---|
| 设置、会议记录（`Sessions\<id>\session.json`，格式与 Cicada 相同） | `%APPDATA%\HuiYi` |
| 各 AI 服务的 API key（每个服务一个文件，DPAPI 加密，只有当前 Windows 用户能解开） | `%APPDATA%\HuiYi\keys\` |
| 识别模型、调试日志（日志里没有对话内容和 key） | `%LOCALAPPDATA%\HuiYi` |

## 开发

需要 .NET 8 SDK。

```powershell
dotnet build HuiYi.sln
dotnet test tests/HuiYi.Tests                      # 单测（含从 Cicada 移植的用例）
dotnet run --project src/HuiYi -- --show main      # 运行；--listen 启动即收听，--show main|settings|setup
                                                        # --feed <音频> 用文件代替系统声音；--snapshot <目录> 把各窗口渲染成 PNG
powershell -File scripts/package.ps1                    # 打包：artifacts\HuiYi-<版本>-win-x64.zip
```

不带界面检查引擎：`tools/HuiYi.Cli`

```powershell
huiyi-cli models                        # 下载 / 校验模型
huiyi-cli transcribe <音频>             # 整段识别一次
huiyi-cli listen <音频> [目标语言]       # 在文件上跑完整的听写 + 翻译链路
huiyi-cli loopback <音频> [目标语言]     # 在扬声器上播放，同时采集系统声音识别（真实路径）
huiyi-cli translate <文本> <源> <目标>
huiyi-cli dialog <输出.wav>             # 用两个 Windows 语音合成一段两人对话（测发言人区分）
huiyi-cli voices | say <文本> <语言> | micusers
```

目录：

```
src/HuiYi.Core   引擎，不含界面：采集、识别、发言人区分、翻译、AI 服务（OpenAI 兼容）、纪要、会议记录、同传发言
src/HuiYi        WPF 界面：托盘、实时面板、悬浮字幕、会议记录、设置、首次引导向导
tests/                单测
tools/                命令行检查工具
scripts/package.ps1   打包
```

## 致谢与许可

Apache-2.0，见 `LICENSE`。部分处理逻辑（大模型调用约定、纪要提示词、断句合并、会议结束判断、发言人聚类规则等）移植自开源 macOS 应用 [Cicada](https://github.com/wquguru/cicada)（Apache-2.0），界面与产品形态为本项目自行设计，与该项目无隶属关系。移植内容和第三方组件的完整清单见 `NOTICE`。
