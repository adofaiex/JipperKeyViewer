# JipperKeyViewer 项目上下文（代理每次请求都会读到，压缩不会丢失）

## 事实锚点（2026-09-18 核实）
- `CHANGELOG.md` 共 274 行；顶部条目 `## 1.7.2`（FreeMake custom-layout editor / 文字描边阴影 /
  视频键节点 / .jkv 分享包等）；最旧条目 1.6.1。以实际 read 结果为准。

## 当前任务（由用户维护，完成后更新）
- 已完成：FreeMake 编辑器撤销历史、Profile/预设切换瞬态隔离、迁移幂等标记与失败重试、
  Rename/Delete 失败路径、Custom 统计标签/CountInTotal/雨滴/图片层级/视频回退、`.jkv`
  导入暂存回滚与包大小/路径限制等本轮修复。
- 下一步候选：真实游戏回归（编辑器撤销、Profile 切换、视频编解码、UMM/TGT）；`.jkv`
  端到端导入回归；解绑节点历史计数的最终产品语义。

### 撤销历史修复已落地（2026-09-24）
- `Editor/EditorHistory.cs`：`Push` 闭合 0.4s 微调连发窗口（调 `EndNudge`）；`PushNudge` 改为
  先 `Push` 再打戳（先打戳会被 `Push` 清掉，导致每次微调各记一条）。
- `Editor/KeyViewerEditor.cs`：撤销管线统一为**后置状态快照**；`OpenFreeMakeEditor` 播种基线
  （此前首次 Push 后 `position=0`、`CanUndo=false`，首个结构编辑不可撤销）；
  `SnapshotEditorDocument`/`RestoreEditorSnapshot` 快照整份文档（`Nodes`+`Groups`+两个 NextId
  +`TotalCount`，字段名与旧格式兼容），恢复时按 `Id` 重映射选区并清空捕获状态；拖拽/缩放经
  `fmDragHistoryPushed`/`fmResizeHistoryPushed` 每次手势只记一条；属性、计数、组名与组可见性
  均入栈。
- `Core/KeyViewer.cs`：`SwitchProfile` 成功后调 `ResetEditorHistoryForProfileSwitch()`，清掉旧
  配置的时间线、选区、活动节点/组与捕获状态并重播基线。
- 切换/预设隔离已扩展到剪贴板、捕获、拖拽/缩放手势、属性 UI 状态和 `customGhostStates`。

### 本轮继续修复已落地（2026-09-25）
- 对齐/分布只保留一条后置历史；水平/垂直居中按真实中心计算。
- Profile/预设切换清理完整编辑器瞬态，删除/清空节点时同步清理捕获状态。
- 迁移增加每 Profile `DataVersion` 标记、失败保留旧 meta 以便重试、批量 Profile 改用
  Newtonsoft + legacy carrier 保留；Profile 缺 `Count` 字段拒绝加载，错误长度绑定保留前缀。
- `RenameProfile` 不再删除目标或失败后更新元数据；`DeleteProfile` 删除失败保留列表项。
- Custom stat 节点标签接通运行时和编辑器；`CountInTotal` 重算全局 Total，KPS 记录所有按压。
- Custom 第三排雨滴设置可见并遵循全局排开关；全局雨色实时刷新；图片预算/Depth 排序统一。
- 视频保留按压静态图，普通属性修改不重启播放器；`errorReceived` 标记失败并回退静态图，异常路径
  清理临时对象。
- `.jkv` 导入检查 Profile 激活结果、拒绝同名不同源资源、缺失 Count 拒绝、资源路径穿越和重复
  目标；导入采用暂存/提交/失败回滚，并限制包文件、展开体积、单条目和条目数；修正非当前
  Profile 的字体索引回退；本地 Harness HintPath/空数组/包安全测试问题已修正（Harness 被 gitignore）。
- FreeMake 节点光效改为参考 Quartz 的共享柔光 Sprite：默认关闭，单一缓存纹理、节点级颜色/范围/0–100% 强度，按下态直接更新 Image，不重建按键 Mesh；仍需实机帧时确认。
- 固定布局也加入独立的全局缓存光效层（默认关闭），与 FreeMake 节点设置分离；修改全局颜色/范围/强度或按压态只更新 Image，不触碰按键 Mesh/雨滴逻辑，仍需实机帧时确认。
- FreeMake 节点光效与固定布局全局光效均新增可选的独立按下态覆盖（颜色/范围/强度），对齐 Quartz 的 idle/active box-shadow 分离；旧配置默认沿用常态光效。
- FreeMake 节点与固定布局新增可选垂直背景渐变及独立按下渐变；渐变直接写入现有合并 `KeyShapeLayer` 顶点色，不创建每键贴图/Image，并覆盖圆角与九宫格路径。
- 同一合并 Mesh 方案扩展到边框渐变：FreeMake 节点与固定布局均支持独立按下边框渐变，圆角环、直角环和九宫格描边均按 Y 插值，不增加绘制批次或贴图。
- 新增 Quartz 风格的静态左右字形渐变（FreeMake 节点级、固定布局全局级，分别支持标签/计数）；只缓存文字/颜色状态，文字变化时才 `ForceMeshUpdate`，不逐帧逐字符扫描。
- 文字渐变继续补齐 Quartz 的 active/idle 分离：FreeMake 节点和固定布局的标签/计数渐变均可选独立按下颜色；按压只触发一次状态缓存更新。
- Quartz 最新还包含雨滴圆角/边框弧线、JS/CSS 变换与 DmNote 渐变解析；当前 Jipper 保持 108K 无雨逻辑不变，后续若移植雨滴圆角会先隔离到非 108K 路径。
- 已先移植 Quartz 的可选雨滴描边圆角：全局默认关闭，使用无分配弧段表；RawRain 复用现有状态，108K 的 `CreateRainDropForKey` 早退和无雨逻辑保持不变。
- 继续移植 Quartz 的点状雨滴轨迹：全局默认关闭，按 Dot Length/Gap Length 在现有合并 RainLayer 内分段，不创建对象；仍隔离 108K 无雨路径。
- FreeMake 雨滴节点目前沿用全局圆角/点状设置；后续若需要，可按 Quartz 的 per-note 覆盖模型继续下沉到 FmNode，而不影响固定布局。
- 已完成首轮 per-note 雨滴覆盖：FreeMake 节点可单独设置圆角半径、点长度和点间距；未覆盖时继续跟随全局，108K 仍无雨。
- 继续补齐 Quartz 的 `noteBorderSide`：全局和 FreeMake 节点均可选择全部/垂直/水平描边，圆角模式也按方向裁剪弧段。
- 当前 RainLayer 方向/圆角/点状功能均为可选路径，默认状态仍走旧矩形与实心轨迹；未触碰 108K 的早退逻辑。
- 鬼雨也支持 FreeMake 节点级圆角和点状覆盖；普通雨与鬼雨的状态分别保存，不互相污染。
- FreeMake 节点新增独立 TMP 字体样式（粗体/斜体/下划线/删除线）；字体切换时保留节点覆盖，不触发额外 Mesh 重建。
- 已按 TMP 实际枚举值修正删除线掩码为 64（避免误用 LowerCase 位）；旧配置中直接序列化的 `FontStyleFlags` 仍保持兼容。
- FreeMake 计数文字现在可独立覆盖字体样式；关闭时继承标签样式，开启后可单独设置粗体/斜体/下划线/删除线，字体切换仍保持覆盖。
- 按 Quartz 的 `noteAlignment` 思路，FreeMake 节点新增雨滴左/中/右对齐；固定布局仍保持原有居中行为，108K 无雨路径不变。
- FreeMake 字体样式继续补齐 Quartz 的小写/大写/小型大写/上标/下标选项，标签和计数共用同一套 TMP 掩码。
- FreeMake 节点新增独立计数字号（0 跟随标签/全局），字体切换与 KPS/Total 路径也会重新应用节点覆盖。
- FreeMake 节点新增独立计数字颜色及按下颜色；普通按键、图片按键和 KPS/Total 节点均可覆盖，关闭时继承标签/全局颜色。
- FreeMake 节点新增 `CountShowWhilePressed`：可让计数在按住期间隐藏，松开恢复；默认开启，旧配置行为不变。
- FreeMake 节点新增计数字独立 X/Y 偏移，便于对齐自定义标签/计数布局；默认 0，不影响旧配置。
- 同时补齐标签文字独立 X/Y 偏移，标签和计数可以分别微调而不改变节点几何。
- FreeMake 标签和计数新增独立旋转角度（-180°～180°），只修改各自 RectTransform，不触发 Mesh 重建。
- FreeMake 标签和计数新增独立缩放（0.5～2.0），与按压缩放分离，同样只修改各自 RectTransform。
- 标签和计数新增可选的按下缩放覆盖；默认关闭，按下/松开只更新各自 RectTransform，不触发 Mesh 重建。
- 标签和计数新增可选的按下旋转覆盖；与常态旋转分离，按下/松开只更新各自 RectTransform。
- 标签和计数新增可选的按下 X/Y 偏移覆盖；按下/松开从保存的常态基准位置计算，不累加漂移。
- FreeMake 节点新增按住时隐藏标签开关，与计数的按住显示开关对称，默认关闭。
- FreeMake 标签和计数新增独立不透明度（0–1），同时作用于实色和静态字形渐变；不增加绘制批次。

- 修复旧配置文字消失：Newtonsoft 字段模式反序列化缺失的新增 FmNode 字段时会绕过字段初始化，
  导致 `TextOpacity=0`、`LabelScale=0`，旧节点加载后文字透明/缩成 0。`ProfileData.SyncArraysFromLists`
  现在按非法 `LabelScale<=0`（以及被错误构建钳制重存后的 0.5/0.5 + 双 0 不透明度）标记恢复新增
  字段默认值（不透明度、缩放、雨对齐、按压/计数动画等），并加入旧配置与已损坏配置回归测试。
- 修复 DmNote 数字键名与更多字段映射：数字虚拟键、常用别名、Numpad、计数、计数动画/贝塞尔、
  `quartzPressScale`、根级 `noteEffect`/`noteSettings.speed` 均已接入。
- 新增第一阶段 DmNote JSON 预设导入（`Core/KeyViewerDmNoteImport.cs`）：读取 `keyPositions` / 旧版
  `positions` / `statPositions`，支持按键绑定、几何、颜色/透明度、渐变、边框圆角、雨滴参数与
  字体样式映射；始终创建新 FreeMake Profile，不覆盖当前配置。`graphPositions`、`knobPositions`
  与嵌入图片暂跳过并提示。设置页新增 DmNotePresets 文件夹列表和打开文件夹按钮。

### 双子代理全面审查后的修复（2026-09-26，第 34 轮）
- **DmNote 别名遍历**：ReadNumber 曾按步长 2 走，把 `x`/`y`/`w`/`h`/`row`/`zIndex` 等奇数位别名
  全部静默丢弃——而 x/y 正是 DmNote 原生写法，导致整份预设解析出 0 个节点；旧用例只用
  dx/dy/width/height（恰好全在偶数位）所以测试全绿也没发现。现改为遍历全部别名并过滤 NaN/Inf。
- **缺 selectedKeyType 直接失败**：`(A && B) || C` 的优先级让 `TabExists(stats, null)` 执行，
  JObject[null] 抛 ArgumentNullException，整份文件被判为不可读。现已加括号与空值防护。
- **tab 与键名错位**：选中的 tab 不存在时，元素回退到别的 tab，键名却仍取不存在的 tab，
  导致每个按键绑定到错误名称。现在 tab 必须真实存在，元素与键名严格同源。
- **导入健壮性**：几何字段存在但无法解析时不再回退成 60x60 的假节点（跳过并提示）；rgb/rgba
  分量钳制到 0-1；不透明度 NaN 防护；警告去重；元素数上限 4096（遍历中即拒绝，避免打爆 Mono 堆）；
  Profile 名同时避开内存列表与磁盘孤儿文件；导入回滚补写 meta（否则 settings.json 会指向已删除
  的 Profile，下次启动静默回到空配置）；新配置不再继承被克隆配置的 Count/TotalCount。
- **文字渐变按一次键就消失**：渐变缓存只比较文本与两端颜色，不比较 `text.color`；按压路径无条件写
  实色并让 TMP 在帧末重建 mesh，缓存命中后不再补回渐变，标签渐变从此永久丢失。现在在缓存早退
  之前强制白色基色；`ForceMeshUpdate(true)` 忽略激活状态（隐藏文字此前会跳过重建并被记成已应用，
  重新显示时整段纯白）；重建失败/空文本不再写入"已应用"状态；渐变颜色加 NaN/Inf 防护。
- **计数弹跳与按下变换互相覆盖**：弹跳收尾写死 scale=1 与按下时的位置，松开后标签永久停在按下
  偏移，且忽略 LabelScale/CountScale。现在弹跳叠加在节点变换之上，收尾交还给
  ApplyCustomPressedTextTransform；文字基准位置改为"中性基准 + 节点偏移"，不再用 `+=` 累加。
- **图片节点首次按键前无光效/文字色**：NodeType 3 创建时整段跳过了 ApplyCustomKeyColors，
  光效与文字色要等第一次按压才生效（未绑定装饰却立即生效）。现统一在创建时应用。
- **旧配置修复不再误伤用户**：savedPoison 判据收紧为额外要求 GlowSize=0 且按压缓动字符串为空
  （只有错误路径才会产生），用户主动设置的"0 不透明度 + 0.5 缩放"配置不再被重置。
- **切换 Profile 事务化**：SwitchProfile 在改写 CurrentProfile 之后的重建阶段抛异常时，会把
  内存指向新配置而覆盖层半重建；现在捕获异常并回滚到原配置并重建。
- **其它健壮性**：SanitizeFileName(null) 不再 NRE；WriteAllTextSafe 失败会清理 .tmp；
  UnityStructConverter 单个分量解析失败不再让整份配置被判损坏；EnsureCustomNodes 净化光效/渐变/
  文字颜色数组（长度≠4 或含 NaN 一律丢弃并回退全局色）；自定义布局不再套用固定布局的每键字号；
  .jkv 回滚会清理 LoadProfile 留下的 .corrupt 孤儿；渐变颜色全透明时回退实色而不是让按键消失；
  圆角几何改用静态暂存数组（原先每次 mesh 重建分配 315 个短命数组）；光效 rect 未变化时不重写。
- Harness 增至 92 项（含 x/y/w/h/row/zIndex 别名、缺 selectedKeyType、tab 错位、合法零不透明度
  不被重置等回归）。

### 仍待实机或后续处理
- Unity 游戏内回归：FreeMake 撤销/切换、视频真实编码回退、UMM 首次显示、TGT 回放。
- `.jkv` 仍需完整游戏内端到端导入回归（当前已有离线校验/事务原语测试）。
- 解绑节点是否应保留历史 Count 尚需产品定义；当前不自动删除用户 Count。

## 行为规则
1. **文件为准**：行号/内容对不上时相信工具读取结果，不要判定为"乱码"后反复重读。
2. 会话被压缩或换模型后：先重读本文件定位任务再继续。
3. 用中文回复用户。
