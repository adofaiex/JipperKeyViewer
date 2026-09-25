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

### 雨滴/输入 与 编辑器/GUI 审查修复（2026-09-26，第 35 轮）
- **雨滴起始 Y 无 NaN 净化**：`RainStartYRow1..3`/鬼雨同名项是唯一没有 `Mathf.Max` 下限的按排
  值，NaN 能通过所有比较并进入 `rawRain.rect`，一滴坏雨滴就把 NaN 顶点写进**共享**合并 mesh，
  整块雨滴画布每帧被污染；`SyncCachedSpeeds` 的 `==` 缓存也对 NaN 永不命中。现按 −4000..4000
  钳制（NaN/Inf → −223），并改用「相同或同为 NaN」比较。`DrawDrop` 入口另加 NaN/Inf 守卫。
- **点状轨迹完全失效**：阴影/描边先画**实心**矩形，本体再分段画点，间隙里漏出的仍是实心色，
  看起来就是一整条实心条（GUI 里阴影与描边开关相邻，极易同开）。现在阴影/描边在点状下同样
  切段；段数触顶时点长与间距同比缩放（此前只放大步长，轨迹比设置值稀疏得多且无提示）。
- **描边宽 0 把鬼雨变成实心板**：width=0 且 sides=all 时 `DrawRainOutlineBySide` 画出覆盖整滴
  的实心四边形；普通雨被后画的本体盖住，但鬼雨在 `drawMain:false` 下会被完全遮住。现要求
  宽度为正才画描边。
- **鬼键路径裸索引 `Keys[i]`**：靠「GhostKey* 不超过 Keys 长度」的巧合成立，越界会中断整帧
  输入处理。现与 `ProcessKeyGroup` 一致地加边界与判空守卫。
- **KPS/Total 逐帧分配**：固定布局路径每帧 `new List<Key>(1)`（且 lastKps 常变），
  `new string(buf,off,len)` 也抵消了 `NumBuffer` 的收益。现直接处理单个面板引用。
- **在取色器里按 Delete 会删除并落盘选中节点**（高危）：编辑器快捷键只认 `fme_` 前缀，
  漏掉了取色器的 `cpi_` 控件名。现按多前缀判定。
- **编辑器与设置窗口的取色器控件名冲突**（高危）：两个窗口可同时打开、共用同一个序号计数器，
  同名控件会共用一份文本缓冲与焦点身份，且设置页的陈旧缓冲清理会删掉编辑器正在编辑的条目。
  现在编辑器使用独立命名空间 `fme_cpi_`，并在自己的 pass 里重置序号 + 走 Begin/End 文本缓冲。
- **撤销会静默清零已累积的按键计数**（高危）：快照序列化了 `FmNode.Count` 与 `TotalCount`，
  游玩后按 Ctrl+Z 会把计数一起回滚并被 `EditorMutated` 落盘。现在快照默认剥离计数（克隆节点
  后把副本清零，不触碰活跃对象），恢复时按 id 从实时文档回填；显式「重置计数」那条用
  `preserveCounts:false`，该操作仍可撤销。
- **拖拽/缩放只记第一移动帧的快照**：之后任意一次撤销都会把几何拽回手势中途。现在手势结束时
  用最终态就地替换栈顶条目（`EditorHistory.ReplaceTop`）。
- **撤销/删除/粘贴期间手势残留**：换文档后手势仍持有旧实例，逐帧往脱离文档的对象写坐标，节点
  「卡住不动」。现在这些入口先清交互状态；`ClearEditorInteractionState` 另补 `fmResizing` 与
  小地图拖拽标志（此前窗口缩放标志残留会导致下次 MouseDrag 莫名拖动窗口）。
- **属性面板显示一个节点、应用另一个节点**：X/Y 是相对编辑，按 `v - 活动节点.X` 施加到全体，
  但字段显示的是 `selection[0]`。现把基准节点传入字段，显示与差值同源。
- **预设新建配置可能覆盖磁盘上的同名文件**：重名检查只看内存 `ProfileNames`（仅在展开列表时
  同步），手动拷入或孤儿文件不在其中。现同时检查 `GetProfilePath`。
- **包列表/DmNote 列表每个 IMGUI 事件都扫目录**（排序比较器每次比较两次
  `GetLastWriteTimeUtc`）。现按展开期缓存，折叠/导入时失效。
- 另修：`UpdateKeyColors` 在判空前索引 `Keys[i]`；`ResetFootKeyViewer` 不清文字渐变缓存
  （Destroy 延迟到帧末，清理器这一帧看不到）；`ApplyCustomBackgroundGradient` 对 `slot<0` 的
  死调用；节点颜色数组净化回归测试。Harness 增至 94 项。

### 剩余审查项修复（2026-09-26，第 36 轮）
- **鬼雨贴图永久改写共享贴图的 wrapMode**：`SetSprite` 把独立贴图的 wrapMode 翻成 Repeat 后
  从不还原；同一张 PNG 若还被自定义图片节点使用，就会一直保持 Repeat 并在边缘拉色。
  现在换精灵时还原原值。
- **雨排映射三处各写一份**：起始 Y 按颜色字节、速度/高度按槽位或节点 RainRow、宽度又按颜色
  字节——三者今天恰好一致，但任何一处单独改动都会变成"第 2 排速度配第 1 排宽度"。
  现统一为 `RowFromRainByte` 一个映射。
- **`RawRain` 逐帧回访 `KeyViewer.Settings`**：宽度在热路径上通过静态组件反向引用取值
  （Settings 为 null 时还会 NRE），且绕过 `SyncCachedSpeeds` 缓存。现改为创建时把该排宽度解析
  进 `NodeWidth`。
- **`removed` 标志是死代码**：`ReturnRawRain` 把它清成 false，而它正是在同一次调用前由
  `ReturnRawRainAndRemove` 置位的。任何"只回收、不从 rainList 移除"的调用方都会把已回收雨滴
  当幽灵雨滴交回渲染器。现在只有从池中取出的 `GetRawRain` 才复活它。
- **拖动任意颜色滑杆 → 每个 MouseDrag 事件（60-120/秒）重建整层覆盖层**：
  `DrawEditorColorField` 走的是 `EditorPropertyChanged` → `RequestEditorRebuild` →
  `ResetKeyViewer`，即销毁重建所有按键 GameObject、重置两层形状 mesh、清空全部雨滴。
  现在实色也走就地刷新（与光效/渐变/文字渐变同款）：按键色、光效、背景/描边渐变就地重算，
  文字样式走 `UpdateAllFonts`，雨滴只清雨滴（下一次按压即用新颜色）。
- **拖拽吸附每帧分配**：`EditorSnapDrag`/`EmitAlignLine` 每个被拖节点每帧新建 List 与
  float[3]（112 节点全选拖动 = 每帧数百个短命数组）。现改为复用暂存。
- **「视频」按钮与「图片」按钮完全等价**：产出既无 ImagePath 也无 VideoPath 的未绑定节点，
  画布上是灰色占位框，还白占该组 8 个未绑定图片名额之一。现创建后自动聚焦视频路径输入框。
- **节点改键捕获不解除设置页的武装态**：两边同时武装时同一次物理按键会被
  `ProcessKeySelection` 与节点捕获同时消费，顺带改掉固定布局槽位绑定。现武装前显式解除。
- **另存为可能覆盖磁盘同名文件**（与预设新建同一类问题），现一并检查 `GetProfilePath`。

### 功能补齐与低危修复（2026-09-26，第 37 轮）
- **鬼雨缺节点级描边方向覆盖**：普通雨有 `UseCustomRainBorderSides`/`RainBorderSides`，
  鬼雨只有圆角与点状覆盖，节点无法单独调整鬼雨描边方向。现补
  `UseCustomGhostRainBorderSides`/`GhostRainBorderSides`（0=全部/1=垂直/2=水平，与普通雨同一
  套编码）+ 编辑器 UI + 三语提示，默认关闭，旧配置行为不变。
- **节点无法在全局开启时单独关闭点状雨滴**：`UseCustomRainDotted` 一旦勾选就无条件强制
  `dotted=true`，用户想"全局开着、唯独这个节点用实心"根本做不到；而且 `EnsureCustomNodes`
  还把点长从 0 钳到 1，把 `0` 这个可能的关闭值变成了 1 像素点。现在点长 0 = 该节点显式关闭
  点状（普通雨与鬼雨），钳制范围随之放宽到 0..100，帮助文本三语更新。
- **编辑器选区 `Contains` 每帧 O(n)**：`DrawEditorNode`/`DrawEditorMinimap` 每个节点每帧都要
  问一次"是否选中"，112 节点全选时每帧约 1.2 万次引用比较。现改为 `EditorSelectionList`
  （有序 List + 同步 HashSet），所有修改都经由此类型，两者不会失步。
- **每键颜色面板的索引无守卫**：`keyCodes[i]`、`keyCodes[backSequence[b]]`、
  `PerKeyBackground[idx]` 全部裸索引。任何绕过 `EnsureSettingsArrays` 的加载路径都会在 OnGUI
  内抛 `IndexOutOfRange`，结果是**整个设置窗口被禁用**而不只是这一行。现全部加守卫。
- `GetKeyScale(keyIndex)` 从"每滴每帧"提到"每键每帧"（该键所有雨滴的按压缩放是常量）。
- Harness 增至 96 项（含点长 0 保持关闭态、描边方向索引钳制回归）。

### 设置持久化与迁移修复（2026-09-26，第 38 轮）
- **写盘失败完全静默**：`SaveSettings` 只打一行日志就把异常吞掉，界面毫无提示。磁盘写满、
  Profile 目录只读、或文件被同步软件占用时，用户继续编辑，**自上次成功保存以来的所有改动
  会在下次启动静默丢失**，而 GUI 看起来完全正常。现保留失败消息并在设置窗口顶部持续显示
  红色横幅（`LastSaveError`），直到某次保存成功才消失（三语文案）。
- **旧版 Profile 在 meta 升级后到达就不会被迁移**：迁移只由 **meta** 的 `Version` 驱动，
  而 `MigrateAllProfileFiles` 每个 meta 版本只跑一次。手动拷入、从备份恢复、或由旧版本
  `.jkv` 解包而来的 Profile 永远看不到那次升级，于是脚键计数与每键颜色仍留在 v4 之前的
  槽位上——**脚键计数加载后恒为 0**。现把 v3→v4 脚键平移抽成幂等的 `MigrateFootSlots`，
  由版本升级流程与 `LoadProfile` 共用；新增回归测试（含二次调用幂等性）。Harness 增至 98 项。

### 资源层与持久化深审修复（2026-09-26，第 38 轮下半）
资源层（视频/图片/字体）：
- **装饰视频回退贴图漏登记 → 每次布局重建泄漏一张 GPU 贴图**：`UpdateCustomVideoFallbacks`
  为无键装饰节点加载静态回退图后直接赋给 `raw.texture`，没有加入 `customDecorationTextures`，
  而 `ReleaseCustomTextures` 只遍历 Keys 数组与该列表。现已登记。
- **解码失败的视频每次重建都被销毁重建**：复用快路径排除 `Failed` 条目，于是拖一次节点或调一次
  颜色就会销毁死播放器+RT、重新创建、重新解码、再次失败、再次刷日志——永久的
  "分配→失败→释放→再分配"循环。现已确认失败的文件不再重试（直接返回 null 走静态图），
  并在 `OnVideoError` 里立刻 `Pause()` + 断开 `targetTexture`。
- **`BeginBuild`/`EndBuild` 之间没有 try/finally**：中间任一处抛异常都会让代次已自增而回收
  未执行，本轮与上一轮创建的播放器全部继续解码、屏幕上却什么都没有。现已加 finally。
- **`new Font(path)` 从不销毁**：`CreateFontAsset` 烘焙完图集即不持有源 Font，现两条加载路径
  都在 finally 中 `Destroy(font)`，字体集重载不再每次泄漏一份原生字体面。
- **`ScanCustomFonts` 的目录 IO 在 try 之外**：权限拒绝/路径其实是文件时异常从
  `TryLoadResources → EnableKeyViewer → OnEnable` 逃出，**整个覆盖层构建中断**、按键根本不出现。
  现整个目录段包 try/catch + 明确报错。
- **`GetFontMaterial` 反射无防护**：缓存的 MemberInfo 来自首个字体类型且无条件复用，
  `GetValue`/强转都可能抛异常并逃逸进覆盖层构建。现按类型重解析 + 整体 try/catch，
  解析不到成员时改为 `Loader.Error`（此前只有一行 Info，症状是描边阴影静默失效）。
- **`KvImageLoader` 磁盘加载零防护**：路径来自用户可编辑配置且接受任意绝对路径，而
  `LoadImage` 按 PNG 头部声明的尺寸分配——65535×65535 索要约 17 GB 显存，OOM 被通用 catch
  吞掉后"加载成功"却得到损坏贴图；他人分享的 .jkv 就能携带这种文件。现加 16 MB 文件上限、
  4096×4096 尺寸上限（读 PNG IHDR）、以及反射异常的 `TargetInvocationException` 内层解包
  （此前日志永远是"Exception has been thrown by the target of an invocation."）。
  另修：反射查找失败被永久负缓存（查找前置位）、`GetMethods` 顺序依赖（只接受 2/3 参）、
  九宫格边框硬编码 22（改为从 border 推导）、`Destroy` 后再读 `tex.width`。
- **编辑器 `fmTexCache.Clear()` 不销毁贴图**：每次导入图片把此前全部贴图变孤儿泄漏显存；
  且 null 也进缓存，导致损坏文件在**每个** OnGUI 重绘被重新读盘并刷一条日志。现销毁 +
  独立负缓存 `fmTexFailures`。

持久化/迁移（续）：
- **写盘失败被当成"配置损坏"→ 全量重置并覆盖用户文件**：`LoadSettings` 的 catch-all 不区分
  解析失败与 `IOException`。迁移链里的 `SaveCurrentProfile` 抛异常（磁盘满/只读/被占用）时，
  内存被重置为默认值，随后的 `SaveSettings`（每次场景加载都跑）就把默认值覆盖进用户真实配置，
  并废掉迁移的"回滚 meta 版本号以便重试"记账。现按 `IsStorageFailure` 分流：IO 类失败保留
  已加载状态并显示失败横幅。
- **"Profile not found" 分支把默认值写回刚判定不存在的那个路径**：`File.Exists` 对被占用/
  云端占位符同样返回 false，一次临时文件锁就会造成不可感知的配置销毁。该分支现只在内存中
  启用默认值，不写盘。
- **`Version` 字段缺失被当成 v1**：JsonUtility 不运行字段初始化器，缺 `Version` 即为 0，会让
  整条迁移链从 v1 重跑，而 `MigrateV2toV3` 会重置配置列表。现 `Version<=0` 视为"未知"，
  按当前版本（`KeyViewerSettings.CurrentVersion`）处理。
- **`MigrateV2toV3`/`MigrateV4toV5` 缺失败回滚**，且 V2toV3 无条件重置 `CurrentProfile`/
  `ProfileNames`——重试时反而主动破坏用户配置列表。现补回滚 + 仅在列表为空时补建 Default。
- **批量迁移遇"文件缺失"仍推进 meta 版本门**：当时读不到（云盘/未挂载/权限）的 Profile 永远
  不会被回访，脚键计数恒 0、KPS 面板旧约定。现计为失败让 meta 门回滚重试。
- **`Contains("FullKpsPosition")` 整文件子串嗅探**：FreeMake 节点的自定义文字/图片路径/按键名
  中出现该字面量时，真正 v4 形态的文件会被判成 v5，其**构造默认值**被翻转并盖上
  `DataVersion=6`——不可逆的静默错位。现改为 `JObject` 根对象属性判定（解析失败才回退子串）。
- **`LoadProfile` 补 v5→v6 惰性修复**：与 `MigrateFootSlots` 对称——meta 早已升级后才到达的
  v5 形态 Profile 原本永远看不到那次翻转，随后还会被盖上 `DataVersion=6` 永久锁死旧约定。

### 材质缓存回收与 Profile 操作加固（2026-09-26，第 39 轮）
- **`textStyleMaterials` 无界增长（单次滑杆手势可铸数万个 Material）**：缓存键按 1/1000 量化，
  而 GUI 滑杆连续、`UpdateAllFonts` 在**每个** tick 都跑——把阴影偏移从 −20 拖到 +20 一次就能
  铸出数万个材质，在拆解前一个都不会释放。直接淘汰又会让仍在用它的文本**变空白**，所以现按
  `Material.GetInstanceID` 做**引用计数**（`ApplyFontMaterial`），只有引用为 0 的条目才被销毁；
  三个实际赋值点与 `KvTextStyle.Apply`（经桥接）全部改走该 setter。拆解时连同引用表一起清空，
  避免销毁后的 instance id 被复用导致新材质被永久误判为"使用中"。
- **`KvTextStyle.Bits` 的 NaN 归一化方向反了**：所有 NaN 量化后本就相同，真正的问题是 0 是
  **合法**样式值——把 NaN 映射到 0 会让"NaN 粗细"与"粗细 0"共用材质；且 1e30f × 1000 溢出后
  `RoundToInt` 在 x86 上未定义。现按 ±10 钳制并单独处理 Inf。
- **`KvTextStyle.ColorOf` 不做 NaN/Inf 净化**：分量直接来自配置 JSON，一路传到
  `SetColor("_OutlineColor")` 与 Color32 缓存键。本代码库其它颜色路径都做了净化，唯独此处
  依赖一个自己不做断言的上游不变量。现就地回退。
- **`DeleteProfile` 先删文件后写 meta**：meta 写失败（或同一窗口崩溃）会让 settings.json 指向
  一个已不存在的文件。现改为**先写 meta 再 unlink**——删除失败只留下孤儿文件，下次
  `SyncProfilesWithDisk` 会加回列表，这是可恢复的方向；meta 失败则还原内存列表。
- **`RenameProfile` 回滚失败会让 meta 指向死文件**：反向 `File.Move` 失败时文件只存在于新名下，
  而 meta 仍写旧名——下次启动按"找不到"处理并写一份全新默认值，用户数据看起来就消失了。
  现改为回滚失败时**保留新名字**并写 meta。
- **`.corrupt` 备份互相覆盖**：所有损坏恢复点都用 `File.Copy(..., true)`，一次瞬时故障就毁掉
  唯一仍然完好的那份备份。现统一走 `RotateCorruptBackup`，保留上一份为 `.corrupt.1`。
- **`WriteAllTextSafe` 只保证 rename 原子、不保证内容**：`File.WriteAllText` 只写到 OS 缓存，
  返回后立刻断电仍可能留下截断文件。现经 FileStream 写并 `Flush(true)` 到设备；`File.Replace`
  在部分 Mono/Wine/Proton 与网络盘/exFAT 上未实现且**每次都失败**（永久性保存失败，用户永远
  看着错误横幅），现捕获 `PlatformNotSupportedException` 降级为 delete+move。
- **`SwitchProfile` 首次 `SaveCurrentProfile` 无保护**：异常逃逸进 IMGUI 调用方打断该帧，且因为
  绕过 `SaveSettings` 而到不了错误横幅。现捕获并设置 `lastSaveError`。
- **`LoadSettings` 的建目录在 try 之外**：目录无法创建时异常逃出 Awake，`Settings` 保持 null，
  之后每次 `Settings.Data` 都 NRE。现捕获、用默认值继续并显示失败。
- **`SyncProfilesWithDisk` 把损坏 meta 里的 null/空白条目原样写回**：现过滤。

### 资源预算与包导入/加载器加固（2026-09-26，第 40 轮）
显存与字体预算：
- **游戏字体无上限全量烘焙**：`Resources.FindObjectsOfTypeAll<Font>()` 返回游戏中加载的**每一个**
  字体，每个都要烘焙一张 1024×1024（有时更大）字形图集加材质——标题画面有几百个字体时会静默
  吃掉用户从未申请、也无法关闭的数百 MB 显存。现限 32 个并明确提示。
- **自定义字体同样无上限**：`CustomFont` 目录由用户控制，往里丢上百个文件会在启动时烘焙上百
  张图集。现限 24 个并提示；顺带修 `CreateFontAsset` 失败时源 `Font` 泄漏。
- **视频节点无显存预算**：`MaxDimension=2048` + ARGB32 → 单个满尺寸节点已是 16 MB，而一份
  FreeMake 文档最多可含 2048 个视频节点（32 GB 显存）。现设 256 MB 总预算，超出的节点回退
  到静态图片；条目销毁时归还预算，`ReleaseAll` 直接归零。
- **`BucketSize` 未挡 NaN/Inf**：`CeilToInt(NaN)` 为 0、`CeilToInt(Infinity)` 未定义——手改的
  配置能直接决定渲染纹理尺寸。现与本代码库其它位置一致显式净化。

`.jkv` 包（子代理审查）：
- **【回归，上一轮引入】导入预盖 `DataVersion` 架空两条惰性迁移**：`imported.DataVersion =
  Settings.Version` 让 `LoadProfile` 的 `MigrateFootSlots` / v5→v6 翻转直接跳过——从**休眠** v3
  配置导出的包（`ExportProfilePackage` 原样复制文件，不像 `SaveCurrentProfile` 会提升）导入后，
  脚键计数卡在旧的 20 基线槽位（读出来恒为 0）、v5 配置的 Y 约定被锁死为错误值。**用户无需任何
  操作即可复现**。现删除该行，提升交给 `SaveCurrentProfile` 在惰性修复之后进行。
- **导入对节点/组数量零上限（DoS）**：`EnsureCustomNodes` 的限制是**按组**计数，声明数百个不同
  `GroupId` 的包可以完全绕过；组查找是全表扫描，构成 O(节点×组) 十亿级比较足以卡死进程。
  现按 DmNote 导入器的同一量级加 4096 节点 / 64 组上限。
- **未绑定装饰节点循环没有上限**：与按键槽位循环的 2048 上限**不冗余**（一个节点要么是按键
  槽位、要么是装饰），满是装饰节点的文档会为每个节点创建一个 GameObject + 一张贴图。现补上限。
- **`Count` 字段用子串 `IndexOf("\"Count\"")` 判定**：被截断的 settings.json 只要某处（节点文字、
  路径）含该字面量就能通过，`PopulateObject` 随后留下**构造默认值**——静默产出空白配置并报告
  "导入成功"。现改用已存在的 `HasRootProperty`。
- **导出会把绝对路径指向的文件内容打包带走**：保留路径*字符串*是有意的，但打包*内容*不是——
  `C:\Users\<用户>\Desktop\private.png` 会被复制进可分享的 `.jkv`。现绝对路径只保留引用、
  不打包内容并警告；无法解析的相对路径改为置空，不再把本机目录结构原样写进包。
- **字体名未净化 → 路径穿越**：`FontName` 是可被 `.jkv` 控制的 JSON 字段，此处直接拼进路径，
  `Custom: ..\..\..\Users\<用户>\Documents\secret` 会让**导出**读出该文件并打进包里。现限定纯文件名。
- **导出前半段在 try 之外**：`SaveCurrentProfile` 遇磁盘满/只读时抛异常逃进 IMGUI 调用方，且
  若继续会导出**过期**布局。现捕获、设置错误横幅并放弃导出；配置名比较改 `OrdinalIgnoreCase`。
- **「本地同名文件优先」完全静默**：接收方看到错图是 `.jkv` 最常见的实际故障，却日志与提示都
  干净。现收集被跳过的目标，在导入消息与警告里说明（三语）。
- **回滚路径的 `SaveMetaOnly` 静默吞异常**：回滚删除了导入的配置，meta 写失败会让 settings.json
  指着一个已不存在的文件。现上报并显示横幅。

加载器（子代理审查）：
- **【高危】`Loader.ModPath` 静默回退 `"."` → 配置写进游戏安装目录**：`?? "."` 让 `ModPath`
  **永远非 null**，于是 `config/`、`assets/`、`CustomFont/`、`CustomImages/`、`Packages/`、
  `.jkv-staging/` 全部相对进程工作目录解析。只读安装下是一连串异常；可写时则把配置散落在游戏
  目录并随游戏更新一起消失。现改为 `ResolveModPath()`：无效时一次性报错并落到
  `Application.persistentDataPath`（该调用本身也包 try，可能抛异常，最后退到临时目录）；
  `KeyViewer.cs`/`KeyViewerPackages.cs` 里本就存在但因 `"."` 而**不可达**的三处
  `?? Application.persistentDataPath` 死代码由此真正生效。
- **三个路径缓存永不失效**：`configPath`/`profileDir`/`packagesDir` 是静态惰性缓存，
  `Loader.Instance` 一旦被换（重载、同进程第二个加载器）仍指向旧目录，读写分裂。现由
  `Instance` 的 setter 统一清空。
- **MelonLoader 偏好创建抛异常 → 永久半初始化**：`OnInitializeMelon` 中途抛错会跳过
  `Main.Init`，而 `OnSceneWasInitialized` 仍调 `Main.EnableNow()`，此时 `Loader.Instance` 为
  null——正是会把文件写进游戏安装目录的状态。现包 try/catch、明确报错并保持关闭。
- **`Main.Init` 的 `initialized` 在做任何事之前置位**：上面任何一处抛错都会让门永久锁死，
  Mod 再也无法初始化。现移到全部订阅完成之后。

编辑器：
- **`fm_unselectable` 也触发全量重建**：这是纯编辑器语义（运行中的游戏根本没有"不可选中"
  概念），却在 `EditorMutated` 里销毁重建所有按键 GameObject。现改走 `EditorOnlyChanged`
  （只压历史 + 落盘，不重建）。

### 仍待实机或后续处理
- Unity 游戏内回归：FreeMake 撤销/切换、视频真实编码回退、UMM 首次显示、TGT 回放。
- `.jkv` 仍需完整游戏内端到端导入回归（当前已有离线校验/事务原语测试）。
- 解绑节点是否应保留历史 Count 尚需产品定义；当前不自动删除用户 Count。

## 行为规则
1. **文件为准**：行号/内容对不上时相信工具读取结果，不要判定为"乱码"后反复重读。
2. 会话被压缩或换模型后：先重读本文件定位任务再继续。
3. 用中文回复用户。
