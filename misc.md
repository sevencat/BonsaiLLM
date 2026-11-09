# misc.md

杂项、选型理由、踩坑记录、二期待定。正式范围与排期见 `plan.md`。

---

## 1. 平台与部署

**只做 Windows x64。**

- 目标：Windows 10 1809+ / Windows 11，`win-x64`。
- 不处理 Linux、macOS、ARM64、WSL、商店打包。
- 发布形态：framework-dependent + `dotnet publish -r win-x64`，不做单文件自包含（原生 llama 库太大，单文件每次启动解包很慢）。
- **一期不做 NativeAOT / 裁剪**，只跑常规 JIT 发布。选型保持 AOT 友好但不为它牺牲功能：不引反射重的库，JSON 一律走 `JsonSerializerContext` 源生成。将来要上 AOT 需单独一轮，先验证 Avalonia 12 + SkiaSharp 4.153 的组合（重点：`SKTypeface` 字体发现、原生库加载、`SKBitmap` 互操作）。
- **单一发布、两个后端都带**（自用，不拆包）：`runtimes\win-x64\native\cuda12\` 与 `...\vulkan\` 同时存在，运行时用 `NativeLibraryConfig` 选。
- 官方包已按后端分目录，**不需要任何手工 Copy**：包内是 `LLamaSharpRuntimes\win-x64\native\<backend>\*.dll`，由包的 props 以 `Content` 拷到输出的同名子目录，两个包互不覆盖。
- 踩过的错：`ExcludeAssets="native"` 管不到 props 引入的 `Content`；自己再 Copy 一遍会得到 `native\cuda12\cuda12\`。按 `plan.md` 第 3 节原样引用即可。
- **`RuntimeIdentifier=win-x64` 必须显式写**：不写时 props 走 fallback，把 linux/osx/arm64 的原生库也拷进输出（`ConsoleApp1` 的 bin 里实测有 `runtimes\linux-arm64`、`osx-arm64`）。
- CUDA 包依赖 `LLamaSharp.Backend.Cpu`，所以 CPU 后端天然可用；`ggml-cuda.dll` 单个就 512MB，Vulkan 的 `ggml-vulkan.dll` 约 59MB。
- CUDA12 需要匹配的 NVIDIA 驱动；Vulkan 只需驱动支持 Vulkan 1.2+，兼容面更宽。
- 体积：单目录约 1GB（CUDA 512MB + Vulkan 60MB + CPU 四套变体 + Avalonia），自用不优化。
- 切换后端需要重启应用（原生库是进程级全局状态，且 `NativeLibraryConfig` 必须在首次调用前配置），设置页给出明确提示。

## 2. 为什么只留这些包

一期依赖：Avalonia、Material.Avalonia（浅色主题，不引 `Avalonia.Themes.Fluent`）、MarkView.Avalonia（+ Math / Mermaid 两个扩展）、LLamaSharp（+ 两个后端包）、CommunityToolkit.Mvvm。图像不额外引包，SkiaSharp 由 Avalonia 与 MarkView 扩展带入。

图像统一用 **SkiaSharp**：解码、缩放、EXIF 朝向纠正、烧框、JPEG 编码全覆盖，ImageSharp 与 ImageSharp.Drawing 都可以删。版本跟着 Math/Mermaid 走，显式写 `4.153.1`：核心包 `MarkView.Avalonia` 只依赖 `Avalonia 12.1.4+` 与 `Markdig`，不碰 Skia；是 `.Math` / `.Mermaid` 声明了 4.153.1，把 Avalonia 原本的 3.119.4 抬上去（见下条）。

被移除/暂缓的：

| 包 | 处理 | 替代 |
|---|---|---|
| Microsoft.Extensions.DependencyInjection | 不引 | `AppServices` 手写组合根，服务就十来个，构造注入足够 |
| Microsoft.Extensions.Configuration | 不引 | `System.Text.Json` 直接读写 `settings.json` |
| Serilog / NLog | 不引 | 自己写一个 500 行的 `FileLogger`（滚动、级别过滤、异步队列）+ LLamaSharp native log 回调 |
| Material.Avalonia.Dialogs | 不引 | 设置用页面/侧栏，确认用自绘遮罩层，不弹系统对话框（除文件选择用 `StorageProvider`） |
| Material.Avalonia.DataGrid | 不引 | 会话/模型列表用 `ItemsControl` + `ListBox` |
| Material.Avalonia.TreeDataGrid | 不引 | 同上 |
| MarkView.Avalonia.SyntaxHighlighting | 一期加 | TextMate 语法高亮，启动时 `MarkdownViewerDefaults.Extensions.AddTextMateHighlighting()` 全局注册；代码块带语言标签与复制按钮 |
| MarkView.Avalonia.Math | 一期加 | 公式常见；CSharpMath 纯 .NET 渲染，无 WebView。把 SkiaSharp 抬到 4.153.1 |
| MarkView.Avalonia.Mermaid | 一期加 | mermaid 围栏 → SVG → 栅格化，纯 .NET；额外带 Mermaider + `Svg.Controls.Skia.Avalonia` |
| MarkView.Avalonia.Svg | 暂缓 | 只在要显示远程/文件 SVG 图片时才需要 |
| Avalonia.Diagnostics | 仅 Debug | 由 `Avalonia.Desktop` 的 DevTools 条件引用处理 |
| 图标库（Material.Icons 等） | 不引 | 用少量 Path 几何图形 + Unicode 符号，够用且不增依赖 |

原则：能用 BCL 解决的不引包；只在"渲染 Markdown"和"跑 llama"两件事上依赖第三方。

## 3. 版本兼容笔记

- **MarkView 的版本号规则是 `12.MAJOR.MINOR`**：`12` = 对应 Avalonia 12，第二位是破坏性变更位。用 `12.4.*` 吃补丁，不要跨第二位。
- MarkView 12.4 要求 Avalonia ≥ 12.1.3；Material 3.21.0 要求 ≥ 12.1.1。项目锁 12.1.x 就同时满足，但**实际还原版本以 `dotnet list package` 为准**，别只看 csproj。
- MarkView 的控件模板来自 `avares://MarkView.Avalonia/Themes/MarkdownTheme.axaml`，漏了这条 `StyleInclude` 的表现是"控件在但没样式/不能滚动"，很容易误判成 bug。
- 顺序：`MaterialTheme` → `MarkdownTheme`，**不要 `FluentTheme`**。Material.Avalonia 3.21.0 的依赖只有 `Avalonia`（已核对 nuspec），基础控件样式由 `Material.Styles` 自带；Avalonia 模板在 `App.axaml` 里默认写好的那段 `<FluentTheme />` 要删掉，`csproj` 里的 `Avalonia.Themes.Fluent` 引用也一并删。
- Markdown 主题必须在最后，否则会被通用控件样式覆盖。
- 主题固定浅色：`<themes:MaterialTheme BaseTheme="Light" PrimaryColor="…" SecondaryColor="…" />`，同时在 `Application` 上写 `RequestedThemeVariant="Light"`，避免系统深色时控件落到未适配的分支。一期不做深浅切换。
- **公式与图表**：`MarkView.Avalonia.Math` / `.Mermaid` 从 12.2 起声明 SkiaSharp 4.x（12.4.0 = 4.153.1），核心包只依赖 `Avalonia 12.1.4+`、`Markdig`，不碰 Skia。所以项目 SkiaSharp 被抬到 4.153.1，而 Avalonia 12.1.5 原本编于 3.119.4。4.153.1 已核实发布（含 win32 原生包），但落差真实存在：M1 要同时验窗口渲染与公式/图表渲染，异常就先摘这两个包退回 3.119.4。
- `UseMath()` ≠ `AddMath()`：前者还会把公式语法加进该 viewer 的解析 pipeline，后者只注册渲染器。全局 `AddMath()` 之后 `$…$` 仍不生效，多半就是漏了语法注册。
- 公式是 opt-in：不启用时 `$…$` 原样显示；启用后单个 `$`（`$5`）不会被误判，必须成对。
- Mermaid 要改配色时，在 `MarkdownTheme.axaml` 之后追加 `avares://MarkView.Avalonia.Mermaid/Themes/MermaidTheme.axaml`；不改就用内置配色。Math 不需要任何额外样式。
- Material 3.x 的 API 与 2.x 差异大，网上老教程（`MaterialTheme` 旧命名空间、`Material.Styles` 独立包）不要照抄。
- Material 3.21.0 的目标框架标的是 net8.0，但依赖 Avalonia 12.1.1，net10.0 项目可以正常引用（computed 兼容）。
- LLamaSharp 0.30 的采样/参数 API 与 0.2x 早期版本不同，**不要照抄旧博客**；落地前先做最小 spike，把用到的类型全部包在 `BonsaiLLM.Llama` 里。

## 4. 后端选择与显存

后端由用户手动选（`cuda12` / `vulkan` / `cpu`），不做"CUDA → Vulkan → CPU"的自动排序。选完立即尝试初始化，成功即用；失败**直接报错并让用户重选**，不静默降级到 CPU——降级会让人误以为还在用 GPU，显存与速度预期全错。两套库都在发布目录里，探测只判断驱动与初始化是否成功，不存在"这个后端没打包"的分支。

- 只允许初始化一个后端；一个进程里切换后端需要重启应用（写清楚提示，别做成"静默失败"）。
- 后端始终由用户选择；设置里显示"当前实际使用：CUDA12 / Vulkan / CPU"。初始化失败时展示失败原因并让用户换一个，不写"已回退"，也不替用户决定。
- `gpuLayers = 99` 表示全部卸载，llama.cpp 会自己截断到实际层数，不用精确算。
- **显卡与显存自动探测**：优先 `nvidia-smi` 拿型号、总显存、已用显存，失败则退化到 Vulkan 查询，再失败按保守值（8 GB）假设。设置页直接把推荐的 `gpuLayers` / `contextSize` 填进去，用户可改；加载前仍按 5.4 估算提示，但不再改写用户手动设过的值。
- 估算公式见 `plan.md` 5.4；GGUF 缺字段时退化为"文件大小 × 1.15 + 2GB"。
- 多模态模型额外加 mmproj 文件大小（2B 的 f16 投影就有 0.76 GB），并给 mmproj 一个"按需加载"开关（纯文本聊天不加载）。
- 上下文越长，KV cache 增长是线性的；`q8_0` KV 通常能省一半且质量损失可接受，可作为"显存紧张"预设。
- 多卡/SLI 一期不做 split-mode，按单卡处理。
- 集成显卡（Intel/AMD iGPU）走 Vulkan 能跑，但内存与显存共享，估算会偏乐观 → 提示里加一句。
- 上下文长度不要默认拉满模型原生值（例如 128k），默认 8192，避免首次加载就爆显存。

## 5. 上下文模式的语义（重要）

界面上的开关是「携带历史 / 每次全新」，**只影响发给模型的内容，不影响界面显示**。

- `carry`：system + 全部（裁剪后的）历史 + 当前问题。
- `fresh`：system + 当前问题。界面上仍然看得到之前聊过什么，只是模型不知道。
- 切换模式不清空会话，也不重建进程内的 `ChatHistory` 之外的状态；`fresh` 模式下每轮都相当于一次独立请求。
- `fresh` 更适合"翻译/改写/单次问答"，也方便对比同一问题的不同回答。
- 裁剪策略：按 token 预算从最旧的 user+assistant 对开始丢，system 永远保留，最后一条问答永远保留。
- token 计数优先用模型的 tokenizer；不可用时 `字符数 / 1.6`（中英混合的经验值），并在 UI 标注"估算"。
- 一期不做"摘要压缩历史"，但接口留好（`IContextPolicy`），二期可加。

## 6. 特化：坐标标注（WeVisOne-VL，已实测）

### 6.1 结论

早上用 `F:\test\ConsoleApp1` 跑通，事实如下，不用再猜：

- 模型：`WeVisOne-VL-2B/4B`，GGUF 架构 `qwen3vl`，配 `mmproj-f16`（`clip` + `qwen3vl_merger`）。**真的吃图**，不是只吃 OCR 文本。
- **特化入口按模型名匹配**：设置里维护一份「需要特化的模型列表」（默认 `WeVisOne`），命中才挂检测适配器；不看 `architecture`——`qwen3vl` 也会被别的 VL 模型占用，按架构启用会误伤。
- 输出：标签 + 坐标 token，坐标是 **0–1000 归一化**的 `[x1,y1,x2,y2]`，多框用逗号分隔：

```text
<ref>person</ref><box><98><49><121><169>,<122><223><260><1000></box>
```

- 多目标：连续多组 `<ref>…</ref><box>…</box>`（可能用不同说法重复描述同一类，如 `helmet` / `hard hat` / `safety helmet`）。
- 无目标：输出 `None`。
- 没有置信度分数；标签来自 `<ref>`。
- 任务模板（官方 prompts.py）：`Locate the instances that match the following description: {phrase}.`，system 用 `You are a helpful assistant.`。
- 采样必须确定性（`Temperature=0.01, TopK=1`），否则坐标会飘。

### 6.2 解析踩坑（已验证的补偿）

- 主解析：`<(\d{1,4})>` 每 4 个数字一框，除以 1000；`<box>` 可能只有一个框，也可能多个。
- 兜底：部分量化/服务端会把尖括号吃掉，输出 `100,200,300,400`，需要第二个正则。
- 过滤：宽或高 < 0.002 的退化框直接丢；IoU > 0.92 去重（同一目标常被重复输出）。
- **mtmd 执行器会吞掉生成序列的第一个 token**：常见结果是只剩 `</ref>` 而没有 `<ref>`。补偿：把 `</ref>` 之前的内容反推成标签，同时存档原始输出便于复核。
- 坐标可能恰好是 1000（贴边），clamp 后再画，别当越界丢掉。
- `None` 与空串都当"未找到"，不要当解析失败弹错误。

### 6.3 渲染

- 统一内部表示：`NormalizedBox { float X1,Y1,X2,Y2; string? Label; }`，全部 0–1；置信度字段先留空。
- 界面叠加（默认）：`Viewbox` + `Grid` + `Canvas`，Canvas 尺寸对齐**图片实际显示尺寸**；处理 `Stretch=Uniform` 的 letterbox，不要用控件尺寸直接算。
- 缩放用 `ScaleTransform`，不重算坐标。
- 导出烧图（可选）：SkiaSharp 画框与标签（`SKCanvas.DrawRect` / `DrawText`），输出 JPEG 92，便于直接发出去。文字要用 `SKFont` + `SKTextAlign`，3.x 已把字号/字形从 `SKPaint` 移到 `SKFont`，按老写法会编译不过。
- 颜色按类别固定色板；hover 高亮；图例与框双向联动。
- 解析失败：原样显示文本 + "原始输出"折叠区，绝不吞内容。

### 6.4 路线结论

| 路线 | 状态 |
|---|---|
| A. LLamaSharp mtmd（`MtmdWeights` + `InteractiveExecutor`） | **已验证可用**，一期主路线 |
| B. 外部 OpenAI 兼容服务（LM Studio / llama-server） | 已验证可用（`LmStudioClient.cs`），留作备选/对比 |
| C. 只吃外部检测结果 | 不需要 |

后续换更强模型时 A 仍优先；遇到 llama.cpp 尚未支持的架构直接切 B，UI 与解析层不动。

### 6.5 图像预处理

- 最长边 1600、JPEG 92（验证参数）；归一化坐标与缩放无关。
- 2B 对小目标召回一般，必要时上 4B；"分块检测 + 坐标映射回原图"留作二期优化。
- 2B + mmproj-f16 合计约 1.9 GB 权重，显存估算要把 mmproj 算进去。

## 7. 流式渲染与性能

- token 到 UI 要**合并刷新**：攒 8 个字符或 40ms 再 append 一次；实测比逐 token 快很多。
- `BeginStreaming()` 的会话要 `using`/显式 Dispose，异常路径也要释放，否则长会话会累积。
- 只让最后一条助手消息处于 streaming 状态；历史消息一律静态 `Markdown`。
- 消息列表超过 200 条后考虑虚拟化（`ItemsRepeater`/自定义面板）；Avalonia 12 的 `VirtualizingStackPanel` 在消息高度不定时表现一般，先测再定。
- 自动滚动不要用 `ScrollToEnd()` 每帧调用；用 `ScrollViewer.Offset` 判断是否贴底，贴底才跟随。
- Markdown 重渲染时避免整段替换字符串拼接（O(n²)），用 `StringBuilder` 或直接用 streaming session。
- 大代码块（> 2000 行）高亮会卡，先限制或延迟渲染。
- 推理线程优先级不要设最高，否则 UI 掉帧；`ThreadPool` 里跑即可。
- tok/s 统计：从首 token 到结束的 token 数 / 耗时，分别显示首 token 延迟与生成速度。

## 8. 存储

- 配置 `%AppData%\BonsaiLLM\settings.json`；会话 `%AppData%\BonsaiLLM\sessions\{guid}.json`；日志 `%AppData%\BonsaiLLM\logs\`。
- `lastModelPath` 只存绝对路径：启动时文件还在就自动加载；被删/移动则清空该字段并提示，不随便挑一个模型顶上。
- 会话 JSON 单文件，字段：id、标题（首条消息截断生成）、模型路径、上下文模式、消息数组（role/text/tokens/时间/耗时/特化元数据）。
- 写入用"临时文件 + 替换"，避免中途退出把会话写坏。
- 附件（图片）一期只存路径引用，不复制进配置目录；路径失效时显示占位图。
- 迁移：`settings.json` 带 `version` 字段，后续加字段一律给默认值，不做破坏性重命名。
- 不引 SQLite：会话量级小（几百条），JSON 够用，还方便用户直接看/改。

## 9. 日志

- 自建 `FileLogger`：按天滚动、保留 7 天、单文件上限 10MB、写失败不影响主流程。
- LLamaSharp 的 native log 回调 → 转成统一格式，默认只落 `Warning` 以上；调试开关打开后全量。
- 记录关键事件：所选后端与初始化结果、模型加载参数与耗时、prompt token 数、每轮推理耗时、异常堆栈。
- **不要记录完整 prompt 内容到默认日志**（可能含用户隐私），调试模式下才写。
- UI 里放一个"日志"抽屉，展示最近 500 行，方便用户反馈问题。

## 10. 打包与分发

- `dotnet publish -c Release -r win-x64 --self-contained false`，单目录输出，两套后端都在里面（`native\cuda12\`、`native\vulkan\`）。
- 输出体积预估：Avalonia 约 20–30MB + `ggml-cuda.dll` 512MB + `ggml-vulkan.dll` 59MB + CPU 四套变体，接近 1GB。
- 原生库由各后端包的 props 自动拷入，不需要额外的 MSBuild Target；体积大是唯一的副作用。
- 不加 `PublishTrimmed` / `PublishAot`：SkiaSharp 4.153 与 Avalonia 12 的裁剪标注不全，裁了大概率运行期缺类型；真要减体积就接受启动慢换单文件，也不在一期。
- 可选压缩成 zip；不引安装器（MSIX/Inno），一期绿色版。
- 版本号写进标题栏与日志首行，便于对问题。
- 不写注册表，不装服务；卸载 = 删目录（配置可选保留）。

## 11. 测试

- `BonsaiLLM.Core.Tests`（xUnit）：上下文裁剪、token 预算、配置合并、坐标归一化换算、Markdown 输出后处理容错。全部纯函数，快。
- 解析器用 `F:\test\ConsoleApp1\outputs\*_raw.txt` 的真实输出做回归：多 ref、`<ref>` 被吞、`None`、无尖括号、单框/多框五种。
- 推理层不做自动化（依赖模型与环境），用固定小模型（如 0.5B Q4）做手动冒烟脚本：加载 → 生成 50 token → 停止 → 卸载。
- UI 手测清单：
  - 空模型状态、加载中、加载失败
  - 生成中切换会话、生成中关窗口
  - 超长消息、超长代码块、连续快速点击发送
  - 高 DPI（125%/150%/200%）、窗口最小宽度
  - 浅色主题下 Markdown 正文、行内代码、代码块、表格边框的对比度
  - 坐标图层在窗口缩放、图片长宽比极端时的对齐

## 12. 其他想法（不进一期）

- 分块检测（tiling）：小目标召回不足时切图推理再映射回原图
- 模型对比（左右并排同问）
- Prompt 模板库与变量（`{{char}}` 之类）
- 导出会话为 Markdown / JSONL
- 生成中途编辑上一条消息并重发
- 简单的 tokenizer 预览（看 prompt 实际喂了什么）
- GPU 占用/温度的轻量监控
- 系统托盘常驻 + 全局快捷键呼出
- 多语言（一期中文硬编码，字符串集中放一个静态类，为将来抽资源文件留路）
- 会话搜索
- 流式语音朗读（本地 TTS）

## 13. M1–M2 实现记录（与计划的偏差）

- **后端初始化时机**：计划写 `Program.Main` 最前，但后端是用户在设置里选的，首次启动还没有值。实际做法是在第一次加载模型时调 `LlamaBackend.Configure`；`NativeLibraryConfig` 进程内只能配一次，之后再改后端会提示重启。失败抛 `BackendUnavailableException`，UI 打开设置页要求重选。
- **Vulkan 查询没做**：非 NVIDIA 机器直接按 8 GB 保守估算并标注「（估算）」。Vulkan 枚举显存要额外 P/Invoke，一期不值得，nvidia-smi 覆盖主力场景。
- **GGUF 内嵌 chat template 不执行**：固定 ChatML（`<|im_start|>` / `<|im_end|>`），Qwen3-VL、SmolLM2、WeVisOne 实测同款标记。执行 Jinja 需要额外模板引擎，等真遇到不兼容模型再补。
- **`--selfcheck` 保留**：以假数据引擎跑一遍 Markdown 渲染链路，跳过设置读取与扫描，用于无模型时的 UI 冒烟。
- **扫描性能实测**：200 个 GGUF 头部解析 163 ms（本机 SSD），扫描在后台线程，UI 不冻结。

## 14. M3–M7 实现记录

- **会话持久化**：`ChatSession` / `SessionStore` 放 `BonsaiLLM.Core.Sessions`，一会话一文件 `%AppData%\BonsaiLLM\sessions\{id}.json`，写入走"临时文件 + `File.Replace`"。标题取首条 user 消息前 24 字。
- **空状态遮罩会吃掉消息**：`IsEmptyVisible` 原先只看 `Models.Count`，模型列表为空时那层「添加模型目录」遮罩盖住整个消息区，表现为发完消息看不到回复。改成 `Models.Count == 0 && Messages.Count == 0`，并在 `Messages.CollectionChanged` 里调 `UpdateEmptyState()`。排查时容易误判成"消息没上屏"。
- **上下文占用要区分两种值**：`UsageInfo` 里的 prompt/completion 是**本轮**用量；顶栏「上下文 已用/总量」用 `IChatEngine.ContextTokensUsed`，即 KV cache 里的累计值（引擎在 `StreamAsync` 的 finally 里累加，重开上下文时清零）。切换会话后引擎还没重建，此时按当前会话消息重新估算（`EstimateContextTokens`），否则会显示上一个会话的数字。
- **裁剪**：`BuildRequestHistory` 从最新一条往前累加，超出 `contextSize - maxTokens(缺省 1024) - 128` 就丢最旧的，并保证请求以 user 开头；`StatsText` 追加「已裁剪 N 条」。实测把 profile 的 `contextSize` 临时改 1024 后，12 条历史被裁掉 4 条，界面仍显示完整历史。
- **生成中切换会话**：`StreamAssistantAsync` 开头固定住发起时的 `_session`，收尾写盘、追加消息都用这个局部变量，避免取消后把旧回复追加到新会话。
- **流式刷新走 VM 事件而非 `BeginStreaming()`**：消息体是 `MarkdownViewer.Markdown` 字符串绑定，每轮开始先清空，token 累积到 32 字符触发一次 `StreamingContentUpdated`，`finally` 再兜底触发一次；窗口侧订阅该事件统一 `ScrollToEnd()`。没有 `Append`/`Dispose` 生命周期要管，异常路径也不会漏释放。
- **设置面板（M5）**：后端、显卡型号、系统提示词、当前模型四项参数、字号 Slider（12–20，绑 `Window.FontSize`）、模型目录列表。参数落在 `settings.json` 的 `modelProfiles[模型绝对路径]`，`SaveProfileCommand` 写盘后给 `ProfileNotice` 提示，下一轮请求即生效（`ChatOptions` 每轮从 profile 读，不需要重新加载模型）。
- **消息操作**：助手消息下方「复制 / 重新生成 / 删除」。Avalonia 12 的剪贴板是 `IClipboard.SetDataAsync(IAsyncDataTransfer)`：`new DataTransfer()` + `DataTransferItem.CreateText(text)`；`DataTransfer` 在 `Avalonia.Input`。旧的 `SetTextAsync` 只在 `ClipboardExtensions` 里，容易误判成 API 被删。`OnCopyMessage` 用 try/catch 把失败写进状态栏，不再静默返回。
- **重新生成只允许最后一轮**：删中间那条再追加到末尾会把历史顺序打乱，命中非最后一条时只提示「只能重新生成最后一条回复」。
- **滚动跟随要看 `OffsetDelta` 而不是 `Extent`**：`ScrollChanged` 里内容变长也会触发，此时 offset 没动、离底距离瞬间变大，若直接按距离判断会把「跟着流式输出」误判成「用户上滚了」。判据是 `e.OffsetDelta.Y == 0` 就当作非用户滚动直接返回。注意 `OffsetDelta` 是 `Vector`（用 `.Y`），`Extent`/`Viewport` 是 `Size`（用 `.Height`）。跟随本身靠 `ScrollToTail(force)`：非强制时离底 > 32px 就跳过；自己发的新 user 消息强制回底。按钮用 `IsVisible` 切换而不是绑 VM 属性，纯视图状态，`IsVisible=false` 时控件会从 UIA 树里消失，测试时按名字找不到是正常的。
- **应用图标**：根目录 `Logo.png`（512×512、24bpp 无 alpha）复制到 `src/BonsaiLLM.App/Assets/logo.png`，csproj 加 `<AvaloniaResource Include="Assets\logo.png" />`，`MainWindow` 上 `Icon="/Assets/logo.png"`（`WindowIcon` 由字符串路径的 TypeConverter 转，不用手动 `Bitmap`）。exe/任务栏图标靠 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`；`app.ico` 是用 PowerShell 现搓的：ICONDIR + 16 字节目录项（宽高字节、planes=1、bitCount=32、数据长、偏移）+ 各尺寸 PNG 数据，尺寸 16/24/32/48/64/128/256，256 的宽高字节写 0。只设 `ApplicationIcon` 不换窗口图标，只设 `Window.Icon` 不换 exe 图标，两处都要。
- **会话级操作收进 `⋮` 菜单**：侧栏标题行「会话 + 新建 + ⋮」，`Button.Flyout` 挂 `MenuFlyout`（重命名 / 清空消息 / 删除会话）。侧栏底部空出来留给内存、CPU、显存等运行时指标。重命名改成就地编辑：`IsRenaming` 控制输入框显隐，VM 触发 `RenameRequested` 事件让窗口 `Dispatcher.UIThread.Post` 去 `Focus()` + `SelectAll()`（展开后才存在控件，直接 Focus 拿不到），回车提交、Esc 走 `CancelRenameCommand` 还原标题。
- **清空/删除会话前必须等生成任务收尾**：`StreamAssistantAsync` 的 `finally` 会往发起时的会话追加回复并 `PersistSession`，如果只是 `_cts.Cancel()` 就立刻清空/删除，回复会被写回刚清空的会话、删掉的文件也会被重新写出来。做法是把生成任务存进 `_generation`，`StopGenerationAsync()` 里 cancel 后 `await` 它，`ClearSessionAsync` / `DeleteCurrentSessionAsync` 先等再动数据。
- **语法高亮**：加 `MarkView.Avalonia.SyntaxHighlighting 12.4.*`，在 `App.axaml.cs` 建窗口前 `MarkdownViewerDefaults.Extensions.AddTextMateHighlighting()`（全局默认，Light+ 主题）。自检截图里 C# 代码块的 `var`/`new`/注释已按 token 着色。
- **快捷键**：`Ctrl+Enter` 发送、`Esc` 停止生成（无生成时关闭设置）、`Ctrl+N` 新会话、`Ctrl+,` 设置、`Ctrl+R` 重新扫描。
- **截图验证脚本**（临时，已清理，需要时按此重写）：PowerShell + Win32 P/Invoke，`PrintWindow` 抓窗口、`SetCursorPos`+`mouse_event` 点击、`WScript.Shell.SendKeys` 输入，`-ClickX/-ClickY` 按窗口比例定位。测按钮坐标别靠肉眼估：把图放大后用像素扫描（找主色按钮块）求行范围，之前 y 差 20px 就整轮点空。`PrintWindow(hwnd, hdc, 2)` 抓被遮挡的窗口也正常，不必抢前台；用户在用机器时优先走 UIA（`InvokePattern` / `ValuePattern` / `ScrollPattern`），只有必须的滚轮才 `SetForegroundWindow` + `SetCursorPos` + `mouse_event`。
- **日志抽屉**：`BonsaiLLM.Core/Diagnostics/AppLog.cs` 是进程内环形缓冲（静态、锁保护、上限 500），提供 `Info/Warn/Error` 和 `Appended` 事件；核心层不引 Avalonia，VM 收到事件后 `Dispatcher.UIThread.Post` 转成 `LogEntryViewModel`（级别→颜色）。事件分发包了 try/catch，日志自身出错不能影响加载模型的主流程。埋点位置：目录扫描（含不存在/不可读目录、读取失败文件名 Top5）、后端配置、模型加载开始/完成/失败、每轮「发送请求（新增几条、预填充 token、KV 复用）」「本轮结束（token、耗时、tok/s）」、推理异常、会话/设置保存失败。抽屉是右侧主区第 3 行（`RowDefinitions="Auto,*,Auto,Auto"`），`IsVisible` 绑 `IsLogOpen`，新条目由窗口侧 `Logs.CollectionChanged` 触发 `ScrollToEnd`。复制走窗口注入的 `ClipboardWriter`——Avalonia 12 的剪贴板不是 `SetTextAsync`，要 `DataTransfer` + `DataTransferItem.CreateText` 再 `SetDataAsync`。
- **消息虚拟化**：`ItemsControl` 加 `ItemsPanelTemplate` 里的 `VirtualizingStackPanel` 就能在 `ScrollViewer` 里虚拟化，不用换 `ListBox`（省掉选中态/容器样式）。验证手段：UIA 数 `ControlType.Text` 元素——126 条消息时约 28 个，全量实例化会有上千个。
- **虚拟化下的滚动跟随三个坑**：①`ScrollToEnd()` 只推到「已实现部分」的末端，Extent 是估算值，强制回底要连推 3 帧（`PushToTail(retries)`，每帧 `DispatcherPriority.Background` 重投）；②离底判断在虚拟化下会瞬时失真，所以跟随状态改成由用户输入驱动（`_followTail`）：滚轮上滚/拖滚动条置 false，滚回底部或点按钮恢复，另加 600ms 抑制窗口防止刚上滚那几帧被误判成「已到底」；③`ScrollViewer` 自己处理滚轮并置 `Handled=true`，XAML 上挂 `PointerWheelChanged` 收不到事件，必须在代码里 `AddHandler(... RoutingStrategies.Tunnel, handledEventsToo: true)`。
- **上下文兜底**：VM 侧按预算裁剪后仍可能撞满窗（预留量和实际生成量对不上，`MaxTokens` 为 -1 时尤其明显），引擎在预填充后按 `contextSize - used - promptTokens - 32` 压 `MaxTokens`，剩余 ≤64 时只生成 8 token 并写警告日志。否则 LLamaSharp 直接抛 `context window is full`，界面上就是一段红字错误。
- **图片只走 `ChatOptions.ImageBytes`，不塞进 `ChatMessage`**：`_fed` 与请求历史靠 `ChatMessage` 的 record 相等做前缀比对，`byte[]` 字段会让每轮重建的历史永远"不相等"，KV 复用全废。图片按轮携带，历史文本保持稳定。
- **mtmd 通道**：`<__media__>` 必须出现在最后一条 user 内容前（`ChatPromptBuilder.BuildPrompt(messages, withMedia)`），`MtmdWeights.LoadMedia(bytes)` 要在 `InferAsync` 之前调用，生成结束后 `ClearMedia()` + `Dispose()`。图片 token 数没法精确算，记账按 1024 保守预留，否则长会话加图依旧会撞满上下文。
- **发图前预处理**：EXIF `EncodedOrigin` 的 8 种情况用 `SKMatrix` 直接映射（源坐标 → 目标坐标），`5/6/7/8` 交换宽高；随后最长边缩到 1600、JPEG 92。实测 4.1 MB 手机图 → 442 KB / 1568×1068。
- **模型输出格式不稳定**：mtmd 执行器会吞掉生成序列的第一个 token，正常应为 `<ref>person</ref><box>...` 时常变成 `person</ref><box>...`，只能从 `</ref>` 前缀反推标签；偶尔还会输出 `<0><980> The following people are located:` 这类前缀，反推时必须拒绝含 `<`/`>` 或超过 40 字的"标签"，否则界面上会出现一串坐标当标签。`<box>` 内坐标是 0~1000 整数，按 4 个一组切；近重复框用 IoU>0.92 去重。
- **叠加层对齐**：`Viewbox` 包一个固定成图片像素尺寸的 `Grid`（`Image` + `Canvas`），框坐标 = 归一化值 × 像素尺寸，缩放交给 Viewbox，窗口改大小后仍贴合。几十个同类框逐框挂标签会把图糊住，改成全图只有**一个** tooltip：`Canvas.PointerMoved` 里按指针位置命中框再显示，框本身 `IsHitTestVisible=false`（否则 Canvas 收不到移动事件）。导出烧图同理，只画框 + 左下角「person x N」图例。
- **检测结果的持久化**：图片文件写 `%AppData%\BonsaiLLM\images\{guid}.jpg`，`StoredMessage` 只存 `ImageFile` 文件名；助手消息自己不带图，加载时往前找最近一条带图消息当源图。`IsDetection` 落盘，恢复时重新 `WeVisOneParser.Parse` 即可还原叠加。删除消息/清空会话/删除会话都要顺手删图片文件。
- **UIA 验证图片链路**：Windows 的文件对话框（`IFileDialog`）在 UIA 里挂在主窗口的子树下，不是 `RootElement` 的顶层子节点；文件名框是 ComboBox 里的 Edit，`AutomationId=1148`，`ValuePattern` 常不可用，`SetFocus` 也可能抛异常——用 `SendKeys` 输入完整路径再 `{ENTER}` 最省事。`TextBlock` 的 UIA `Name` 会截断长文本（几百字就只留开头），别拿它验证长输出。
- **负样本**：`None`/空输出在解析器单测里已覆盖；但 WeVisOne-VL-2B 对纯色图仍会幻觉出一个全图框，确认「无目标显示未找到」还需要更难的负样本（复杂背景、遮挡）。

## 15. 许可证

- Avalonia / Material.Avalonia / MarkView.Avalonia / LLamaSharp：均为 MIT，可商用。
- llama.cpp 原生库：MIT。
- SkiaSharp：MIT（含原生库），随 Avalonia 一起二进制约 10MB。
- Math/Mermaid 带进来的 `CSharpMath.SkiaSharp`、`Mermaider`、`Svg.Controls.Skia.Avalonia` 均为 MIT。
- GGUF 模型权重有各自许可（如 Qwen 系列、Llama 系列的自定义条款），分发应用时不打包模型，只让用户自备。
