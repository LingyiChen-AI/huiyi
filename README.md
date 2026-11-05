<p align="center">
  <img src="assets/icon.svg" width="112" alt="会译">
</p>

<h1 align="center">会译 HuiYi</h1>

<p align="center">
  <b>Windows 上的 AI 会议助手：听得懂、记得住、说得出。</b><br>
  开会时它在旁边听：实时字幕和翻译、分清谁在说话、自动写纪要和待办，<br>
  还能把你说的中文实时译成对方的语言，说进会议里。
</p>

<p align="center">
  <a href="https://github.com/LingyiChen-AI/huiyi/releases/latest/download/HuiYi-Setup.exe"><b>⬇ 下载 Windows 安装包</b></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/LingyiChen-AI/huiyi/releases">所有版本</a>
  &nbsp;·&nbsp;
  Windows 10 / 11（x64）· 免费开源
</p>

<p align="center">
  <img src="docs/images/main-notes.png" alt="会议记录：AI 按项目周会模板写好的纪要，议题时间线、进展、风险、结论和待办">
</p>

## 它是做什么的

开跨语言的会、听外语分享、或者只是不想边开会边记笔记的时候，打开会译就行。它不需要接进任何会议软件：**电脑里放出来的声音它都能听**，腾讯会议、飞书、钉钉、Zoom、Teams、网页会议、直播和视频都可以。**线下开会也行**：切到「麦克风」，大家围着会议室的麦克风讨论，会译按声音分出每个人。

| | |
|---|---|
| 🎧 **实时字幕 + 翻译** | 对方一开口就出字幕，自动识别中、英、日、韩、粤语，翻成你的语言 |
| 👥 **分清谁在说话** | 按声音把每个人分开（发言人 1、2…），抢话、接话也能在换人处切开；会后再用整场录音校对一遍。可以改成真名，AI 也会从对话里猜名字 |
| 📝 **AI 会议纪要** | 边开会边整理：议题时间线、结论、待办（带负责人和截止日期），会后再按整场写一遍 |
| 🧩 **纪要模板** | 周会、站会、需求评审、客户拜访、面试、复盘……15 个内置模板，也能自己做 |
| 🗣️ **同声传译** | 按住右 Ctrl 说中文，松开后译成对方的语言，用语音说进会议 |
| 🔒 **隐私** | 语音识别、分辨说话人都在你的电脑上完成，录音不上传 |

## 看看它的样子

### 开会时：实时面板

单击任务栏右下角的会译图标，会弹出实时面板，点「开始会议」才开始收听，不会一点就录。开始前可以选听**电脑声音**（线上会议）还是**麦克风**（线下会议室），在「设置 › 字幕 › 收听」里还能指定用哪个麦克风。右键图标是常用入口的菜单。

<p align="center">
  <img src="docs/images/panel-ready.png" width="320" alt="实时面板：点「开始会议」开始收听">
  &nbsp;&nbsp;
  <img src="docs/images/tray-menu.png" width="200" alt="托盘右键菜单">
</p>

开始收听后，屏幕边上会出现一个深色小面板：上面是逐字稿，原文和译文对照，每句标出是谁说的、几点说的；切到「纪要」，能看到 AI 正在整理的要点。**点发言人的名字就能改名**，这场会里这个人的话和纪要都会换成新名字。两个人快速对话、中间不停顿时，会译会在句子结束、声音变了的地方切开；会议结束后还会用整场录音把发言人再校对一遍（录音校对完就删除，可以在设置里关掉）。面板不会抢走焦点，可以直接用系统截图键截下来；需要的话，也能在设置里让它在共享屏幕时对对方隐藏。

<p align="center">
  <img src="docs/images/panel-lines.png" width="380" alt="实时面板：逐字稿">
  &nbsp;&nbsp;
  <img src="docs/images/panel-notes.png" width="380" alt="实时面板：实时纪要">
</p>

<p align="center">
  <img src="docs/images/speaker-name.png" width="340" alt="在实时面板里点发言人的名字改名">
</p>

不想要面板的话，也可以换成屏幕底部的悬浮字幕：

<p align="center">
  <img src="docs/images/subtitle.png" width="760" alt="悬浮字幕">
</p>

### 开完会：纪要和逐字稿

每场会议自动存一份记录，按日期分组、可以搜索。纪要里的时间都是**真实时间**：议题标会上的钟点，「周五前」「下周一」会换算成具体日期；待办事项带负责人和截止日期，可以一键导出 Markdown。

<p align="center">
  <img src="docs/images/notes-weekly.png" width="560" alt="完整的会议纪要：议题、进展、计划、风险、结论、待办、术语">
</p>

逐字稿是双语对照的。外国同事说英文，你说中文，都能看懂：

<p align="center">
  <img src="docs/images/main-lines.png" alt="双语逐字稿，按发言人区分">
</p>

### 纪要模板中心

不同的会关心的东西不一样：周会看进展和风险，评审会看结论和待澄清问题，客户拜访看需求和承诺。选一个模板，AI 就按它的结构写；也可以复制一份，改成你们团队自己的格式。

<p align="center">
  <img src="docs/images/templates.png" alt="纪要模板中心">
</p>

### 用你自己的大模型

纪要和整句翻译需要一个大模型。选平台、填 API key 就能用，Base URL 会自动填好，模型列表一键获取：DeepSeek、阿里云百炼（通义千问）、火山方舟（豆包）、蚂蚁百灵、Kimi、智谱、MiniMax、腾讯混元、百度千帆、硅基流动、UCloud、OpenAI、OpenRouter、本地 Ollama，或者任何兼容 OpenAI 接口的服务。不接大模型也能用：字幕、翻译、会议记录都照常工作，只是没有 AI 纪要。

<p align="center">
  <img src="docs/images/settings-ai.png" width="560" alt="AI 服务设置">
</p>

### 同声传译：让对方听懂你

按住右 Ctrl 说中文，松开后会译把它译成对方的语言，用语音通过虚拟麦克风说进会议，对方在会议软件里直接就能听到。虚拟麦克风可以在设置里一键安装，打开同声传译时会自动切换系统麦克风，关掉时再切回来。没翻译成功的话，这句绝不会说出去。

<p align="center">
  <img src="docs/images/settings-speak.png" width="560" alt="同声传译设置">
</p>

## 安装和上手

1. 下载 [**HuiYi-Setup.exe**](https://github.com/LingyiChen-AI/huiyi/releases/latest/download/HuiYi-Setup.exe)，双击安装，可以自己选安装位置。不需要管理员权限，也不用另装 .NET。
2. 第一次打开会下载约 260 MB 的本机识别模型，下载完会自动校验。
3. 开会时单击任务栏右下角的会译图标，在弹出的面板上点「开始会议」（或者点主界面的「开始收听」），字幕就出来了。想单击图标就直接开始，在「设置 › 通用」里打开对应开关。
4. 想要 AI 纪要：在「设置 › AI 服务」里选一个平台，填上 API key。
5. 想要同声传译：在「设置 › 同声传译」里一键安装虚拟麦克风，然后打开开关。

会译会自己检查新版本：每 6 小时去 GitHub 看一次，发现新版本时弹窗告诉你更新了什么，点「立即更新」才下载并重启，点「下次提醒」就先不管；也可以在「设置 › 通用」里点「检查更新」。

<p align="center">
  <img src="docs/images/update.png" width="420" alt="发现新版本弹窗">
</p>

> 也有免安装版 `HuiYi.App-win-Portable.zip`，解压到任意文件夹就能用，同样会自动更新。

## 隐私：什么会离开你的电脑

| 内容 | 去哪里 |
|---|---|
| 会议声音、录音 | **不离开电脑**。识别和分辨说话人都在本机完成 |
| 识别出的文字 | 打开翻译时发给翻译服务；接了大模型时发给你自己选的大模型，用来写纪要 |
| 同声传译时你说的话 | 先在本机识别成文字，再把文字送去翻译；用蚂蚁百灵时，译文会发给它合成语音 |
| API key | 只存在本机，用 Windows 加密保护，只有你的 Windows 账号能读取 |

## 数据放在哪

| 内容 | 位置 |
|---|---|
| 设置、会议记录（`Sessions\<id>\session.json`，格式与 Cicada 相同） | `%APPDATA%\HuiYi` |
| 各 AI 服务的 API key（每个服务一个文件，DPAPI 加密，只有当前 Windows 用户能解开） | `%APPDATA%\HuiYi\keys\` |
| 识别模型、调试日志（日志里没有对话内容和 key） | `%LOCALAPPDATA%\HuiYi` |

## 开发

需要 .NET 8 SDK。README 里的截图用演示数据生成：`HuiYi.exe --snapshot <目录> --demo`。

```powershell
dotnet build HuiYi.sln
dotnet test tests/HuiYi.Tests                      # 单测（含从 Cicada 移植的用例）
dotnet run --project src/HuiYi -- --show main      # 运行；--listen 启动即收听，--show main|settings|setup
                                                        # --feed <音频> 用文件代替系统声音；--snapshot <目录> 把各窗口渲染成 PNG
powershell -File scripts/package.ps1                    # 本地打包：artifacts\HuiYi-Setup.exe 安装向导 + 免安装 zip
powershell -File scripts/release.ps1 0.2.0              # 发版：改版本号、测试、打 tag 推送；GitHub Actions 打包并发布 Release
```

不带界面检查引擎：`tools/HuiYi.Cli`

```powershell
huiyi-cli models                        # 下载 / 校验模型
huiyi-cli transcribe <音频>             # 整段识别一次
huiyi-cli listen <音频> [目标语言]       # 在文件上跑完整的听写 + 翻译链路
huiyi-cli loopback <音频> [目标语言]     # 在扬声器上播放，同时采集系统声音识别（真实路径）
huiyi-cli mic <音频> [目标语言]          # 播进 VB-CABLE，再从 CABLE Output 当麦克风采集识别（测麦克风模式）
huiyi-cli translate <文本> <源> <目标>
huiyi-cli dialog <输出.wav>             # 用两个 Windows 语音合成一段两人对话（测发言人区分）
huiyi-cli voices | say <文本> <语言> | micusers
```

目录：

```
src/HuiYi.Core   引擎，不含界面：采集、识别、发言人区分、翻译、AI 服务（OpenAI 兼容）、纪要、会议记录、同声传译
src/HuiYi        WPF 界面：托盘、实时面板、悬浮字幕、会议记录、设置、首次引导向导
tests/                单测
tools/                命令行检查工具
scripts/package.ps1   打包
```

## 致谢与许可

Apache-2.0，见 `LICENSE`。部分处理逻辑（大模型调用约定、纪要提示词、断句合并、会议结束判断、发言人聚类规则等）移植自开源 macOS 应用 [Cicada](https://github.com/wquguru/cicada)（Apache-2.0），界面与产品形态为本项目自行设计，与该项目无隶属关系。移植内容和第三方组件的完整清单见 `NOTICE`。
