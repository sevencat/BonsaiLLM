# BonsaiLLM 一期开发计划

> v0.1 草案 / 2026-11-09
> 定位：**Windows x64 本地 GGUF 聊天客户端**，形态对标 LM Studio / Chatbox，推理走 LLamaSharp（CUDA12 / Vulkan），界面 Avalonia + Material，回复用 MarkView 渲染 Markdown。
> 杂项、备选方案、踩坑记录见 `misc.md`。

---

## 1. 项目定位

小而可控的本地模型工作台（Bonsai = 盆景：小、可修剪）。

- 完全本地：不联网、不上传、不需要账号。
- 配置极简：模型放哪个目录、多少层进显存，其余用默认值就能跑。
- 回复按 Markdown 富文本流式呈现（代码块、表格、公式；图表走 mermaid 围栏）。
- 为"某些模型有特殊输出"预留扩展点：一期直接跑通 **WeVisOne-VL 真实图片 → 目标框 → 界面叠加**。

## 2. 一期范围

### 2.1 做什么

| 编号 | 能力 | 要点 |
|---|---|---|
| F1 | 模型目录扫描 | 用户添加一个或多个目录，递归找 `*.gguf`；**不预置默认路径**，首次启动空状态引导选目录；`*.mmproj*.gguf` 不单独成条目，按同名前缀归属主模型；显示名称、量化、大小、原生上下文 |
| F2 | 模型加载/卸载 | 同时只驻留一个模型；有配对 mmproj 时一并加载并启用视觉；加载中可见状态；**记住上次模型，启动时自动加载**；显存不足/文件损坏给出可读错误 |
| F3 | GPU 卸载配置 | 全局默认 + 单模型覆盖：GPU 层数、上下文长度、batch、KV cache 类型、线程数 |
| F4 | 流式问答 | token 逐字上屏、随时停止、重新生成、异常可恢复 |
| F5 | Markdown 渲染 | 标题/列表/表格/代码块；流式中未闭合语法不炸；浅色主题下代码块与表格边框可读 |
| F6 | 会话管理 | 多会话、新建/切换/重命名/删除、本地持久化、应用重启后恢复 |
| F7 | 上下文模式 | 每会话可选「携带历史」/「每次全新」，UI 始终展示完整历史 |
| F8 | 采样参数 | temperature / top-p / top-k / repeat penalty / max tokens，全局 + 单模型覆盖 |
| F9 | 特化框架 | 模型适配器 + 输出后处理扩展点；接通 WeVisOne-VL（`qwen3vl` + mmproj）的坐标输出 |
| F10 | 基础设置 | 模型目录、后端、GPU/显存推荐值（自动探测，可覆盖）、字号、默认上下文模式 |

### 2.2 不做什么（一期）

- 训练/微调/量化转换/模型下载
- RAG、知识库、向量检索、文件问答
- Agent、工具调用、联网
- 多模型并行、多会话同时生成
- 预置固定模型目录、后台全盘搜索（目录一律由用户显式添加）
- 语音、移动端、Web、Linux/macOS、ARM64
- 插件市场、脚本沙箱、账号同步

## 3. 技术基线（仅 win-x64）

| 项 | 选择 | 版本 |
|---|---|---|
| 目标框架 | .NET | **net10.0** |
| 运行时标识 | RID | **win-x64**（仅此一个） |
| UI | Avalonia + Material.Avalonia | **12.1.\***（≥12.1.3）；主题固定 Material 浅色，不用 Fluent |
| 样式 | Material.Avalonia | **3.21.0**（依赖 Avalonia ≥12.1.1） |
| Markdown | MarkView.Avalonia | **12.4.\*** |
| 公式 | MarkView.Avalonia.Math | **12.4.\***（LaTeX `$…$` / `$$…$$`，CSharpMath 渲染） |
| 图表 | MarkView.Avalonia.Mermaid | **12.4.\***（`mermaid` 围栏 → SVG → 栅格化，纯 .NET） |
| 代码高亮 | MarkView.Avalonia.SyntaxHighlighting | **12.4.\***（TextMate；启动时全局 `AddTextMateHighlighting()`） |
| 推理 | LLamaSharp | **0.30.0** |
| 原生后端 | LLamaSharp.Backend.Cuda12.Windows + .Vulkan.Windows | 0.30.0，**两个都带**，运行时选择 |
| MVVM | CommunityToolkit.Mvvm | 8.x |
| 图像 | SkiaSharp（`Avalonia.Skia` 传递引入） | **4.153.1**；由 Math/Mermaid 子包抬高，Avalonia 12.1.5 自身引 3.119.4 |

依赖清单（一期只用这些，**不额外引入** DI 容器、配置框架、日志库、对话框库、图标库）：

单发布、自用，不做多产物。

**不需要手工搬 dll**：0.30 的后端包本身按后端分目录，两个包互不冲突。已核对本机缓存与 `ConsoleApp1` 的实际输出：

```
runtimes\win-x64\native\cuda12\   llama.dll  ggml.dll  ggml-base.dll  ggml-cuda.dll   mtmd.dll
runtimes\win-x64\native\vulkan\   llama.dll  ggml.dll  ggml-base.dll  ggml-vulkan.dll mtmd.dll
runtimes\win-x64\native\avx2\     CPU 变体（avx / avx2 / avx512 / noavx 各一套）
```

每个后端包里的 `build/*.props` 把 `LLamaSharpRuntimes\<rid>\native\<backend>\**` 以 `Content` 形式拷到输出的 `runtimes\<rid>\native\<backend>\`，同名文件落在不同子目录，不会覆盖。

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <Platforms>x64</Platforms>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.*" />
  <PackageReference Include="Avalonia.Desktop" Version="12.1.*" />
  <PackageReference Include="Material.Avalonia" Version="3.21.0" />
  <PackageReference Include="MarkView.Avalonia" Version="12.4.*" />
  <PackageReference Include="MarkView.Avalonia.Math" Version="12.4.*" />
  <PackageReference Include="MarkView.Avalonia.Mermaid" Version="12.4.*" />
  <PackageReference Include="MarkView.Avalonia.SyntaxHighlighting" Version="12.4.*" />
  <PackageReference Include="LLamaSharp" Version="0.30.0" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
  <PackageReference Include="SkiaSharp" Version="4.153.1" />
  <PackageReference Include="LLamaSharp.Backend.Cuda12.Windows" Version="0.30.0" />
  <PackageReference Include="LLamaSharp.Backend.Vulkan.Windows" Version="0.30.0" />
</ItemGroup>
```

不需要额外的 `<Target>` 拷贝，也不要加 `ExcludeAssets="native"`：这些 dll 是由 props 的 `Content` 引进来的，不受 NuGet native 资产管理；再手工拷一遍只会叠成 `native\cuda12\cuda12\`。

**`RuntimeIdentifier` 必须显式写**：不写时 props 走 fallback 分支，会把 linux、osx、arm64 的原生库一起拷进输出（`ConsoleApp1` 的 bin 里就有一堆 `runtimes\linux-arm64`、`osx-arm64`）。

CUDA 包依赖 `LLamaSharp.Backend.Cpu`，所以 CPU（avx 系列）天然可用，选 CPU 时不用额外装包。

两点待编译时确认：`WithCuda()` / `WithVulkan()` 在 0.30 的 `LLamaSharp.dll` 里已确认存在（同族还有 `WithSearchDirectory`、`WithLogCallback`），参数形式以编译为准；Vulkan 0.30 包本机尚未还原（缓存里只有 0.27），但 0.27 已是 `native\vulkan` 布局，restore 后核对一眼目录名即可。

暂不引用、需要时再加：`MarkView.Avalonia.Svg`（SVG 图片）、`Material.Avalonia.Dialogs`。

**SkiaSharp 被抬到 4.153.1 的原因**：核心包 `MarkView.Avalonia 12.4.0` 只依赖 `Avalonia 12.1.4+` 与 `Markdig`，nuspec 与程序集里都没有 SkiaSharp；声明 `SkiaSharp 4.153.1` 的是 `.Math` / `.Mermaid` 两个子包（12.2 起）。NuGet 会统一到 4.153.1，Avalonia 12.1.5 与我们的图像代码也随 4.x 编译。该版本已核实发布（4.153.0–4.153.3 都有 win32 原生包），但 Avalonia 12.1.5 是按 3.119.4 编的，M1 必须亲手验一次窗口渲染 + 公式 + mermaid（见 9 节风险表）。

主题装配：固定 **Material 浅色**，不引 Fluent。

```xml
<Application xmlns:themes="clr-namespace:Material.Styles.Themes;assembly=Material.Styles"
             RequestedThemeVariant="Light">
  <Application.Styles>
    <themes:MaterialTheme BaseTheme="Light" PrimaryColor="Indigo" SecondaryColor="Teal" />
    <StyleInclude Source="avares://MarkView.Avalonia/Themes/MarkdownTheme.axaml" />
  </Application.Styles>
</Application>
```

- 顺序不能反：Markdown 主题放最后，否则被通用控件样式覆盖。
- `FluentTheme` 与 `Avalonia.Themes.Fluent` 包都去掉。Material.Avalonia 3.21.0 只依赖 `Avalonia`（nuspec 已核对），基础控件样式由 `Material.Styles` 自带。
- Avalonia 模板生成的 `App.axaml` 默认带 `<FluentTheme />`，要删，否则 Fluent 样式与 Material 叠加，表现为部分控件还是旧外观。
- 若某控件报缺资源，多半是模板里引用了 Fluent 资源键（如 `SystemControlBackgroundAltHighBrush`），改用 Material 的 palette 键，别把 Fluent 加回来。
- 一期不做浅/深切换，`requestedThemeVariant` 钉死 `Light`，避免系统深色时落到未适配分支。

约束：

1. **一个进程只注册一个 llama 原生后端**：在**任何 LLamaSharp 调用之前**完成配置（`NativeLibraryConfig.All.WithCuda()` / `.WithVulkan()` / 不配走 CPU），选定后不可更改；切换后端需重启应用。后端由用户显式指定，加载失败不自动回退，直接报错并让用户重新选择。
2. **不要手工搬原生库**：0.30 已按 `native\<backend>` 分好目录，再加 Copy 或 `ExcludeAssets` 只会叠层、漏拷；后端选择只通过 `NativeLibraryConfig` 做。
3. **Avalonia 12 是大版本**：Material 必须 ≥3.21.0，MarkView 必须 12.x，三者不要跨线混用。
4. 只考虑 Windows 10 1809+ / Windows 11 x64；不使用 Windows 专属 API 之外的多平台分支。

### 3.1 已就绪的开发模型（本机）

| 用途 | 路径 | 大小 | 说明 |
|---|---|---|---|
| 主开发模型 | `F:\ai\models\llama\mradermacher\WeVisOne-VL-2B-GGUF\WeVisOne-VL-2B.Q4_K_S.gguf` | 1.15 GB | `qwen3vl` 架构，GGUF 内嵌 chat template |
| 视觉投影 | 同目录 `WeVisOne-VL-2B.mmproj-f16.gguf` | 0.76 GB | `clip` + `qwen3vl_merger`，必须与主模型配对 |
| 质量对照 | `...\WeVisOne-VL-4B-GGUF\WeVisOne-VL-4B.Q4_K_S.gguf` + 同目录 mmproj | 4B（另有 Q8_0） | 只做效果对照，不做日常 |
| 快速冒烟 | `F:\ai\models\llama\unsloth\SmolLM2-135M-Instruct-GGUF\SmolLM2-135M-Instruct-Q8_0.gguf` | 138 MB | `llama` 架构、Q8_0、原生 8k 上下文、内嵌 ChatML 模板；纯文本，加载近乎瞬时 |

- **已验证**：`LLamaSharp 0.30.0` + CUDA12 后端可加载 2B/4B + mmproj，单图 referring 检测能出框；验证代码在 `F:\test\ConsoleApp1`（MTMD 用法见 5.3）。
- 一期开发与验收以 **2B** 为准（加载快）；4B 用于对照模型能力。
- 2B 的 mmproj 占 0.76 GB，**显存估算要计入**；纯文本问答可不加载 mmproj。
- **SmolLM2 用来跑纯文本链路**：加载/流式输出/取消/上下文裁剪/会话两种模式，都先在它上面过一遍，秒级迭代，不为几 GB 的权重等加载。它与 WeVisOne 同属 ChatML 模板，模板层只测一次即可。
- 扫描只认 `.gguf`，`.part` 天然被排除；目录里存在未完成下载不报错。

## 4. 架构

### 4.1 项目划分（3 + 1）

```
BonsaiLLM.App        UI：View / ViewModel / 特化可视化实现 / 组合根
BonsaiLLM.Core       领域模型、配置模型、扩展点接口、上下文裁剪策略（无 UI、无 llama）
BonsaiLLM.Llama      LLamaSharp 实现：GGUF 元数据、模型加载、推理、token 流
tests/BonsaiLLM.Core.Tests   纯逻辑单测
```

依赖单向：`App → Core ← Llama`。App 不直接 `using LLamaSharp`，保留将来换成内置 `llama-server` 的余地。

### 4.2 接口草案

```csharp
public sealed record ModelDescriptor(
    string Path, string Name, string Architecture, string Quantization,
    long FileSizeBytes, int NativeContextLength, int LayerCount,
    int HeadCount, int HeadCountKv, int EmbeddingLength);

public interface IModelCatalog
{
    Task<IReadOnlyList<ModelDescriptor>> ScanAsync(IReadOnlyList<string> directories, CancellationToken ct);
}

public interface IChatEngine : IAsyncDisposable
{
    ModelDescriptor? Loaded { get; }
    Task LoadAsync(ModelDescriptor model, ModelLoadOptions options, IProgress<float>? progress, CancellationToken ct);
    Task UnloadAsync();
    IAsyncEnumerable<ChatDelta> CompleteAsync(ChatRequest request, CancellationToken ct);
}

public readonly record struct ChatDelta(string Text, bool IsDone, UsageInfo? Usage);

public interface IModelAdapter                  // 特化：识别模型、改写请求、申明后处理
{
    bool CanHandle(ModelDescriptor model);
    IOutputPostProcessor? CreatePostProcessor(ChatRequest request);
}
```

`IOutputPostProcessor` 负责把完整回复 + 附件解析成可视化元数据（如坐标框），UI 侧再决定怎么画。

### 4.3 数据流

```
输入框 → ChatViewModel.Send
   → 组装 ChatRequest（system + 历史或仅当前问题 + 采样参数）
   → IChatEngine.CompleteAsync (后台线程，IAsyncEnumerable)
   → 缓冲/节流（约 30–60ms 或每 N 字符）
   → Dispatcher.UIThread → 更新消息 VM 的 Markdown 字符串（32 字符/次）
   → 完成：落盘会话 JSON，触发后处理器，渲染特化图层
```

要点：

- 推理在 `Task.Run`/独立线程，UI 线程只做渲染，禁止在 UI 线程 `await` 原生推理。
- 停止 = 取消 `CancellationToken`；已生成内容保留。
- 同一时刻只允许一个推理任务；发送按钮在生成中变成「停止」。
- 长会话只给最后一条消息挂 streaming 会话，其余消息用静态 Markdown，避免 N 个控件重复重排。

## 5. 核心设计

### 5.1 模型扫描与元数据

- **没有默认扫描根**：`modelDirectories` 初始为空，首次启动显示空状态「添加模型目录」；不探测 `F:\ai\models` 之类的固定路径。
- **启动自动加载上次模型**：成功加载后写 `lastModelPath`；启动时若文件仍存在就自动加载（加载失败只提示，留在模型列表，不影响启动）。
- 递归枚举目录（跳过符号链接环、忽略隐藏目录），扩展名 `.gguf`。
- `*.mmproj*.gguf` 或元数据 `general.type = mmproj` 的文件**不单独成条目**；配对规则：同目录 + 去掉量化后缀后同名前缀的主模型。
- 配对不到主模型的 mmproj 收进「未配对视觉文件」列表，不报错（允许先下 mmproj 后下主模型）。
- 元数据优先从 GGUF 头部读（层数、head 数、KV head 数、embedding、原生 context、`tokenizer.chat_template`）；读失败则只显示文件名 + 大小，并标记「元数据不可用」。
- 列表项显示：文件名、参数量（由层数/embedding 粗估）、量化、大小、原生上下文。
- 加载前做一次**显存估算**（见 5.4），超预算时提示而不是直接 OOM。

### 5.2 配置文件

位置：**exe 同目录** `settings.json`（便携模式；目录不可写时回退 `%AppData%\BonsaiLLM`，UTF-8，缩进输出，读写失败要提示而不是静默重置）。首次启动会把旧 `%AppData%\BonsaiLLM` 的 settings/sessions/images 复制过来。

```jsonc
{
  "modelDirectories": [],            // 初始为空，首次启动引导用户添加，不预置路径
  "lastModelPath": "",               // 上次成功加载的模型，启动时自动加载
  "backend": "cuda12",               // cuda12 | vulkan | cpu；启动时确认，探测失败报错重选
  "theme": "light",                  // 固定浅色，一期不提供切换
  "fontSize": 14,
  "defaultContextMode": "fresh",     // carry | fresh；缺省 fresh（顶栏「携带历史」默认不勾）
  "defaults": {
    "gpuLayers": 99,                 // 首次按 GPU 探测推荐填入，99 = 全部
    "contextSize": 8192,             // 首次按显存与模型原生上下文推荐填入
    "batchSize": 512,
    "kvCacheType": "f16",            // f16 | q8_0 | q4_0
    "threads": 8,
    "temperature": 0.7,
    "topP": 0.9,
    "topK": 40,
    "repeatPenalty": 1.1,
    "maxTokens": -1
  },
  "modelProfiles": {                 // key = 模型绝对路径
    "F:\\ai\\models\\llama\\mradermacher\\WeVisOne-VL-2B-GGUF\\WeVisOne-VL-2B.Q4_K_S.gguf": { "gpuLayers": -1, "contextSize": 8192, "systemPrompt": "..." }
  }
}
```

- `modelProfiles` 只存**与 defaults 不同的字段**，读取时合并。
- 会话文件独立存放：exe 同目录 `sessions\{id}.json`（图片在同目录 `images\`），包含消息、所用模型、上下文模式、采样参数快照。
- 无 DI 容器，`AppServices` 手写组合根；配置用 `System.Text.Json` 手工读写。

### 5.3 推理封装（已验证的 MTMD 用法）

以下形状来自 `F:\test\ConsoleApp1\LlamaSharpClient.cs`，0.30.0 + CUDA12 实测通过：

```csharp
var modelParams = new ModelParams(modelPath)
{
    ContextSize = 8192,
    GpuLayerCount = -1,          // -1 = 全部层进显存
    BatchSize = 1024,
    UBatchSize = 512
};
_weights  = LLamaWeights.LoadFromFile(modelParams);
_mtmd     = MtmdWeights.LoadFromFile(mmprojPath, _weights, MtmdContextParams.Default());
_context  = _weights.CreateContext(modelParams);   // 不是 new LLamaContext
_executor = new InteractiveExecutor(_context, _mtmd);

_mtmd.SupportsVision;              // 校验 mmproj 是否为视觉投影
_mtmd.LoadMedia(imageBytes);       // 挂起，供下一次 tokenize 使用
_executor.InferAsync(prompt, inferenceParams, ct);
_mtmd.ClearMedia();                // 每轮结束必须清理
```

要点：

- **提示词里必须有 `<__media__>` 占位符**，否则图片不会被编码进上下文。
- 图片直接交给 `LoadMedia` 字节数组，不需要 data URI / HTTP。
- `InferenceParams`：`MaxTokens`、`AntiPrompts = ["<|im_end|>", "<|endoftext|>"]`、`DecodeSpecialTokens = true`、`SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.01f, TopK = 1, TopP = 1f, RepeatPenalty = 1f }`。坐标任务是确定性输出，**不要用聊天默认温度**。
- 原生日志：`NativeLogConfig.llama_log_set((level, msg) => ...)`，默认只放行 `Error`，否则 CUDA graph 日志刷屏。
- 已知缺陷：llama.cpp 的 mtmd 执行器会吞掉生成序列的**第一个 token**（常表现为只剩 `</ref>`），解析层要补偿（见 5.7）。
- 纯文本模型（SmolLM2）走同一套封装但不创建 `MtmdWeights`；`ChatRequest` 带图时才要求 mtmd，纯文本模型收到图要给出明确提示而不是静默丢图。
- 会话与模板：
  - 坐标/检测请求是**单轮无状态**（system + 当前指令 + 图），不进 ChatHistory；
  - 普通聊天「携带历史」用 `InteractiveExecutor` + `ChatHistory`，「每次全新」每轮重建 prompt；
  - WeVisOne 实测可用 ChatML（`<|im_start|>system ...<|im_end|>`）；聊天模型仍优先读 GGUF 的 `tokenizer.chat_template`，缺失再按 architecture 映射。
- 后端初始化放在 `Program.Main` 最前（早于 Avalonia，也早于任何 LLamaSharp 调用）：按配置里的 `backend` 调 `NativeLibraryConfig.All.WithCuda()` / `.WithVulkan()`，并挂 `WithLogCallback` 记录搜索过程。没有自动探测链：初始化失败就报错，弹窗让用户重新选（换一个后端要重启，提示写清楚）。`WithSearchDirectory` 只在自定义目录时用，正常发布不需要。
- 错误分档：文件缺失 / mmproj 不匹配 / 不支持架构 / 显存不足 / 上下文溢出 / 用户取消。

### 5.4 显存估算

```
weights ≈ 文件大小
kv      ≈ 2 * layerCount * contextSize * headCountKv * (embeddingLength / headCount) * bytesPerElem
总占用   ≈ weights + kv + 计算缓冲（约 weights * 0.05~0.15）
```

- 多模态模型：`weights` 加上 mmproj 文件大小（2B 的 f16 投影就是 0.76 GB）。
- KV 每元素字节：f16=2、q8_0≈1、q4_0≈0.5。
- **探测**：优先 `nvidia-smi`（型号、总显存、已用显存），失败退化 Vulkan 查询，再失败按 8 GB 保守假设；结果进程内缓存，设置页与加载前共用。
- **推荐值**：`gpuLayers` 按「放得下就多放」，`contextSize` 取 `min(模型原生上限, 显存剩余能撑的值, 32768)`；设置页直接填好并标注「自动推荐」，用户改了就不再覆盖。
- 加载前仍按估算提示，允许「强制加载」；不静默改写用户手动设过的值。

### 5.5 上下文裁剪

- 统计：每条消息 token 数（优先用 llama tokenizer，退化时按字符数/1.6 估算）。
- 预算：`contextSize - 预留输出(maxTokens 或 1024) - 模板开销`。
- 超预算从**最旧的非 system 消息**开始成对丢弃（user+assistant 一起丢），保留最后一条完整问答。
- 一期不做摘要压缩，只在 UI 提示「已裁剪 N 条历史」。
- 引擎侧兜底（已实现）：预填充之后按 `剩余窗口 = contextSize - 已用 - promptTokens` 压 `MaxTokens`（留 32 token 余量）；剩余 ≤ 64 时只生成 8 token 并写警告日志。否则 `MaxTokens` 照搬 512/不限会撞 `context window is full` 硬报错。
- 模式为 `fresh` 时不做裁剪，历史和请求无关。

### 5.6 流式渲染

- **实际做法**：不使用 `BeginStreaming()` / `Append()`（有生命周期要管、异常路径易漏释放），改为 `MarkdownViewer.Markdown` 字符串绑定：每轮先把 `Content` 置空，token 累积到 32 字符才更新一次 `Content`，`finally` 再兜底刷新一次。
- 滚动由 VM 的 `StreamingContentUpdated` 事件驱动，窗口侧统一走 `ScrollToTail()`；贴底判断与「回到底部」已实现（见下一条）。
- 思考块：若模型输出 `思考...` 段落，折进可折叠区块（用 MarkView 的 chat 预设能力，具体属性以 12.4 文档为准）。
- 自动滚动（已实现）：窗口侧维护 `_followTail` 标志，滚轮上滚/拖滚动条即置 false，同时在对话区底部居中浮出「回到底部 ↓」；滚回底部、滚轮向下到底或点按钮恢复跟随。强制回底会连推 3 帧——虚拟化面板的 Extent 是估算值，一次 `ScrollToEnd()` 只能推到「已实现部分」的末端。自己发的新消息强制回底。
- 消息虚拟化（已实现）：消息列表用 `ItemsControl` + `VirtualizingStackPanel`，126 条消息时 UIA 暴露的文本元素仍只有 ~28 个，长会话不再全量实例化。
- 代码块：MarkView 内置样式 + `MarkView.Avalonia.SyntaxHighlighting`（启动时全局 `AddTextMateHighlighting()`），带语言标签与复制按钮。
- 公式与图表：启动时 `MarkdownViewerDefaults.Extensions.AddMath(); AddMermaid();`，或每个 viewer 用 `UseMath().UseMermaid()`。两者不等价：`AddMath()` 只注册渲染器，`UseMath()` 还会把公式语法加进该 viewer 的 pipeline，图省事就用 `Use…`。公式是 opt-in，不开时 `$…$` 原样显示，开了也不会误判 `$5`。
- 流式中的半截 `$$` 与未闭合 ```` ```mermaid ```` 交给 MarkView 的流式补齐处理；若出现闪烁，就把这两类块在流式阶段降级为源码文本，结束后统一渲染。Mermaid 要跟浅色主题配色时，在 `MarkdownTheme.axaml` 之后追加 `avares://MarkView.Avalonia.Mermaid/Themes/MermaidTheme.axaml`。

### 5.7 特化扩展（WeVisOne 坐标标注，已打通）

实测输出格式（0–1000 坐标，多框用逗号分隔）：

```text
<ref>person</ref><box><98><49><121><169>,<122><223><260><1000></box>
```

多目标时连续输出多组 `<ref>…</ref><box>…</box>`；无目标输出 `None`。

- 入口：按模型名匹配，不按架构猜。设置里维护一份「需要特化的模型列表」（默认 `WeVisOne`），模型名/文件名命中才启用特化渲染；否则即便 architecture 是 `qwen3vl` 也按普通聊天处理，避免误伤其它 VL 模型。设置页可关闭特化，退回普通 Markdown 显示。
- 解析（纯逻辑，放 `BonsaiLLM.Core`，可单测）：
  - 主正则 `<(\d{1,4})>` 取坐标，每 4 个一组；兜底正则解析 `100,200,300,400`（部分量化/服务端会丢尖括号）；
  - 除以 1000 得 0–1、clamp、过滤宽高 < 0.002 的退化框、IoU > 0.92 去重；
  - `<ref>…</ref>` 取标签，`None` 视为无目标；
  - 首 token 被吞的补偿：`</ref>` 前的整段内容反推为 ref；原文另存「原始输出」折叠区，绝不丢。
- 请求：`<__media__>` + 官方 referring 模板 `Locate the instances that match the following description: {phrase}.`，system 用 `You are a helpful assistant.`。
- 展示侧：
  - 图片区用 `Viewbox`/`Canvas` 叠加矩形与标签，坐标统一归一化 0–1；
  - hover 高亮、图例与框双向联动，缩放跟随 `ScaleTransform`；
  - 两条路都有：界面矢量叠加（默认）+ SkiaSharp 烧图导出（复制/保存）。
- 图片预处理：最长边缩到 1600、JPEG 92（验证代码参数）；归一化坐标与缩放无关。SkiaSharp 侧固定四步：`SKCodec.Create` 读 `EncodedOrigin` 先纠 EXIF 旋转（否则手机竖拍图模型看到的是横的）→ `SKBitmap.Resize(SKImageInfo, SKSamplingOptions)` 缩放 → 烧框/编码 → `SKImage.FromBitmap(...).Encode(SKEncodedImageFormat.Jpeg, 92)` 出字节交给 `LoadMedia`。
- 一期样例：2B 真实推理跑通即可，不追求检测精度。

## 6. UI 布局（单窗口，LM Studio 风格）

```
┌──────────────────────────────────────────────────────────────┐
│ 植 BonsaiLLM  状态文字                          —  □  ✕      │  自绘标题栏
├───────────┬──────────────────────────────────┬───────────────┤
│ 会话  新建 ⋮│  消息流（Markdown 流式）        │ 参数面板(可折叠)│
│ 今天…      │  - 用户 / 助手气泡               │ - GGUF 元数据  │
│            │  - 代码块 / 表格 / 坐标图层      │ - GPU 层数     │
│            │                                  │ - 上下文长度    │
│            │                                  │ - 采样参数      │
├────────────┴──────────────────────────────────┴───────────────┤
│ [模型 ▾]  ☐ 携带历史                                   [🗑]  │  顶栏（一行）
├──────────────────────────────────────────────────────────────┤
│ 消息流 / 坐标叠加                                             │
├──────────────────────────────────────────────────────────────┤
│ ▢ 待发送附件：图片预览条 / 文件 chip（可逐个移除）            │
│ ╭──────────────────────────────────────────────────────────╮ │
│ │ [＋] 多行输入框（Enter 发送 / Shift+Enter 换行）   [↑]   │ │  输入卡片
│ │                              上下文 43/4096 · 20.7 tok/s │ │
│ ╰──────────────────────────────────────────────────────────╯ │
├──────────────────────────────────────────────────────────────┤
│ ⚙ 设置   ☰ 日志                                               │  侧栏底部
└──────────────────────────────────────────────────────────────┘
```

窗口用 `WindowDecorations="None"` 自绘标题栏（Avalonia 12 已移除 `ExtendClientAreaChromeHints`）：左侧图标 + 标题 + 状态文字，右侧最小化 / 最大化 / 关闭；标题栏 `PointerPressed` 走 `BeginMoveDrag`，双击切换最大化，最大化按 `Screen.WorkingArea` 贴边（避免盖住侧边任务栏），四周补 8 向 `BeginResizeDrag` 热区。

会话侧栏：

- 标题行「会话 + 新建 + ⋮」，`⋮` 是 `MenuFlyout`：**重命名 / 清空消息 / 删除会话**；会话级操作一律收进这个菜单，不再占侧栏底部。
- 重命名是**就地编辑**：点菜单后标题行下方展开一个输入框并自动聚焦，回车提交、Esc 取消（不改标题），提交或取消后收起。
- 侧栏底部放「⚙ 设置」「☰ 日志」两个入口（带 `AutomationProperties.Name` 便于读屏/UIA）。

- 对话区：贴底才跟随流式输出，上滚后底部居中浮出「回到底部 ↓」；消息操作按钮挂在助手消息下方。
- 运行日志是**独立窗口**（非模态、`CenterOwner`、不占任务栏）：标题行「运行日志 + 共 N 条 + 复制 / 清空 / 关闭」，正文按级别上色（信息灰、警告橙、错误红），新条目自动滚到底，Esc 关闭。侧栏「日志」只负责打开/激活唯一的实例。
- 特化消息的展示顺序：**先「模型原始输出」**（等宽小字 + 淡底，可选中复制），**再**是坐标叠加图与「检测到 N 个目标」摘要。
- token 统计（已用/总量 · tok/s）落在输入卡片右下角，生成结束后保留；顶栏只留模型、携带历史与清空按钮。
- 输入卡片与发送按钮同一行：左侧 `＋` 是 `MenuFlyout`（**插入图片 / 插入文件**），右侧 `↑` 发送、生成中变 `■` 停止。
- 插入文件只接收文本：读成 UTF-8 文本后按「【附件：名字】+ 代码块」拼进消息正文（本地模型没有文件通道，这样历史也能原样重放）；单文件上限 256 KB，二进制与空文件会被拒绝。
- 设置面板与运行日志都是**独立组件**：设置是 `Views/SettingsPanel.axaml`（`UserControl`，`DataContext` 继承自 `MainWindowViewModel`），日志是独立 `Window`，主窗口只负责摆放与显隐。
- 消息操作：复制、重新生成、删除该轮、编辑后重发（一期至少「复制 + 重新生成 + 删除」）。前三项已实现，重新生成只对最后一轮生效（删中间那条再追加会把历史顺序打乱），编辑后重发留二期。
- 快捷键：`Enter` 发送、`Shift+Enter` 换行、`Esc` 停止生成（无生成任务时关闭设置）、`Ctrl+N` 新会话、`Ctrl+,` 设置、`Ctrl+R` 重新扫描。
- 全局固定 Material 浅色：背景用 Material 的 Surface 色阶，不要写死 `#FFF`，否则卡片/侧栏层次全糊；MarkView 主题与 palette 同色系。
- 图标：源图就是仓库根目录的 `Logo.png`（512×512），拷进 `src/BonsaiLLM.App/Assets/` 作 `AvaloniaResource`；窗口用 `MainWindow.Icon="/Assets/logo.png"`，exe/任务栏用 `Assets/app.ico`（16/24/32/48/64/128/256 多尺寸）+ `ApplicationIcon`。两者要分别设，只配 `ApplicationIcon` 不会换掉窗口左上角和任务栏的图标。

## 7. 里程碑

| 阶段 | 内容 | 产出 | 预估 |
|---|---|---|---|
| M1 骨架 | sln/项目、组合根、主题装配（Material Light → Markdown，无 Fluent）、假数据流式 demo | 能跑起来、能渲染流式 Markdown 的空壳 | 0.5 人日 |
| M2 模型 | 目录添加与扫描（含 mmproj 配对）、GGUF 元数据、列表 UI、加载/卸载、启动自动加载上次模型、MTMD 初始化、后端选择与初始化探测、GPU 探测与推荐值 | 能选模型并成功加载，重启后自动恢复上次模型，视觉能力探测正确 | 1 人日 |
| M3 推理 | `IChatEngine` 实现、token 流、停止、错误分档、tok/s、首 token 延迟 | 能完成一轮真实问答 | 1.5 人日 |
| M4 会话 | 会话 CRUD 与持久化、上下文模式开关、token 预算与裁剪、system prompt | 多轮对话行为符合预期 | 1 人日 |
| M5 设置 | 设置页、模型目录管理、GPU 探测与推荐值展示、单模型 profile、采样参数热更、字号、快捷键 | 配置能落盘并生效 | 1 人日 |
| M6 打磨 | 滚动策略、空状态、复制/重生成、错误提示、日志面板、性能调优 | 可日常使用 | 1 人日 |
| M7 特化 | 适配器注册、`<box>`/`<ref>` 解析、图片叠加层、SkiaSharp 烧图导出、2B 真图跑通 | 上传图片 → 出框 → 对齐显示 | 1 人日 |

合计约 7 人日（单人，MTMD 链路已验证，不再预留探索时间）。

**进度**：M1 完成（2026-11-09，含 Markdown 流式、表格/代码块/公式/mermaid、链接配色）；M2 完成（2026-11-09，含 GGUF 元数据、mmproj 配对、CUDA12/Vulkan/CPU 后端、GPU 推荐值、自动加载上次模型、设置面板；真实模型已跑通一轮对话，M3 的流式主链路也随之验证）；M3 完成（停止、错误提示、首 token 延迟、tok/s、KV 增量预填充）；M4 完成（多会话 CRUD + 重启恢复、携带历史/每次全新、token 预算裁剪、system prompt、顶栏上下文占用）；M5 完成（设置面板、模型目录管理、单模型参数覆盖并落盘、字号、快捷键）；M6 完成（消息复制/重新生成/删除、代码块语法高亮、空状态、贴底跟随与「回到底部」、应用图标、运行日志抽屉、消息虚拟化、引擎侧上下文兜底）；M7 完成（图片上传与 EXIF 纠偏 + 最长边 1600、mtmd `<__media__>` 通道、WeVisOne referring 模板、`<box>`/`<ref>` 解析（含吞首 token 与近重复框去重）、坐标叠加层（Viewbox 对齐 + 单实例 hover 标签）、SkiaSharp 烧图导出、会话内图片与检测结果持久化、设置里的特化开关与模型名匹配；2B 真图实测 58 框对齐）。实现顺序与偏差见 `misc.md` 第 13、14 节。

## 8. 验收清单

- [ ] 冷启动到可输入 < 2s（不含模型加载）
- [x] 扫描 200 个 gguf 目录 < 3s，UI 不冻结（实测 163 ms）
- [x] 首次启动无目录时进空状态，添加目录后能扫出模型；重启后自动加载上次模型，文件被删时给提示且不影响启动
- [ ] SmolLM2-135M（Q8_0）加载、流式输出、取消与上下文裁剪全部通过（秒级迭代的基准模型）
- [x] WeVisOne-VL-2B（Q4_K_S + mmproj-f16）在 CUDA12 下加载成功，`SupportsVision == true`，GPU 层数修改后重新加载生效
- [ ] 同一模型在 Vulkan 后端下也能加载并完成一次图片推理
- [ ] 连续 20 轮对话无内存持续增长、无 UI 卡顿（拖动窗口仍流畅）
- [x] 公式与图表：`$\frac{a}{b}$`、`$$…$$` 与 ```` ```mermaid graph TD ```` 各渲染一次，配色跟浅色主题，流式结束后无闪烁
- [x] 窗口渲染未受 SkiaSharp 4.153.1 影响（无 MissingMethodException、无黑块）
- [x] 「携带历史」能记住上一轮信息（同会话连续两轮已复现）
- [ ] 「每次全新」在同一会话内不携带上一轮
- [x] 停止生成后已输出内容保留且可继续提问
- [x] 关闭应用再打开，会话列表与消息完整恢复
- [x] 消息操作：复制进剪贴板、重新生成末轮、删除该轮后列表与统计同步
- [x] 会话 `⋮` 菜单：重命名就地编辑（Esc 取消不改标题）、清空消息、删除会话；空会话清空时提示「当前会话没有消息」
- [x] 滚动策略：流式输出时贴底才跟随，上滚后出现「回到底部 ↓」，点击回底并隐藏；自发消息强制回底
- [x] 126 条消息的会话滚动流畅，界面只实例化可见消息（虚拟化），上滚/回底/发送后的跟随均正确
- [x] 运行日志窗口：日志/清空/复制/关闭可用，级别分色，最多保留 500 条；扫描、加载、每轮请求与结束、错误都有记录
- [ ] 显存不足时给出中文可读提示，不崩溃
- [ ] 所选后端初始化失败时报错并可重新选择，不静默降级到 CPU
- [x] 坐标样例：2B 对一张图输出框，界面框与图对齐；窗口缩放、图片长宽比变化后仍对齐（1568×1068 人群图出 58 框，缩放窗口后仍贴合；烧图导出坐标一致）
- [ ] 无目标时输出 `None`，界面显示「未找到」而不是空框或报错（解析器 `None`/空输出已单测通过；但 2B 对纯色图仍会幻觉出 1 个框，属模型侧行为，需要更难的负样本再验）

## 9. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| Avalonia 12 生态较新，Material/MarkView 版本线不齐 | 样式或控件模板异常 | 锁 12.1.x + Material 3.21.0 + MarkView 12.4.x；先做 M1 冒烟验证再铺功能 |
| Math/Mermaid 把 SkiaSharp 抬到 4.153.1，Avalonia 12.1.5 编于 3.119.4 | 运行时 MissingMethodException、渲染黑块或字形异常 | M1 首日验：空窗口 + 一段含公式和 mermaid 的 Markdown；真崩就先摘这两个包退回 3.119.4，公式降级为源码显示 |
| LLamaSharp 0.30 的 MTMD API 与验证代码绑定 | 升级版本时返工 | 已验证的形状集中封装在 `BonsaiLLM.Llama`，不外泄 `MtmdWeights` 等类型 |
| CUDA12 运行库依赖（cudart/cublas） | 用户机器加载失败 | 包内 `ggml-cuda.dll` 已 512MB、运行库随包；启动自检 + 明确错误 + 引导用户换后端 |
| 输出体积约 1GB | 拷贝慢、占磁盘 | 自用可接受；不压体积、不做单文件自包含 |
| 一期明确不做 AOT/裁剪 | 后续想上时要补适配 | 选型保持 AOT 友好（不引反射重库、JSON 走源生成）；`PublishAot` 留到后续单独验证 SkiaSharp 4.153 + Avalonia 12 |
| GGUF chat template 差异 | 回复格式错乱 | 优先读模板，缺失走架构映射，模板可导出到日志/设置页查看 |
| 流式渲染导致长会话卡顿 | 体验差 | 节流 + 仅最后一条 streaming + 消息虚拟化 |
| mtmd 吞首 token、2B 小目标检出率低 | 框缺失/错位 | 解析层补偿 + 归一化去重；精度用 4B 对照，不阻塞 UI 链路 |
| mmproj 与主模型不匹配 | 加载失败或输出乱码 | 同前缀配对 + `SupportsVision` 校验，错误信息指明缺哪个文件 |
| Vulkan 后端下 qwen3vl/mmproj 未验证 | 用户选了 Vulkan 且视觉不可用 | M2 立刻验一次；不可用则报错并提示改用 CUDA，不自动切换 |

## 10. 决策记录

- **不预置默认扫描根**：`modelDirectories` 初始为空，首次启动空状态引导添加目录，不探测固定路径。
- **启动自动加载上次模型**：以 `lastModelPath` 为准；文件失效则清空该字段并提示，不拿别的模型顶上。
- **显卡与显存自动探测**：设置页直接填入推荐 `gpuLayers` / `contextSize`，用户可覆盖，不需要手工填。
- **后端不做自动探测排序**：启动时由用户选 `cuda12` / `vulkan` / `cpu`，选完即初始化；失败直接报错，让用户重新选择，不静默降级。
- **特化入口按模型名**：维护一份「需要特化的模型列表」（默认 `WeVisOne`），命中才启用检测适配器，不按 `architecture` 推断。
- **不做 2B / 4B 常驻快捷切换**：需要时手动换模型。
