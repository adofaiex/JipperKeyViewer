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

### `.jkv` 落点与隐私收尾（2026-09-26，第 41 轮）
- **绝对路径引用连路径字符串一起泄露**：`C:\Users\<用户>\...` 会被原样写进包内
  `settings.json`，把导出方的用户名与目录结构泄露给每个接收方。现置空（接收方显示"图片未找到"
  占位）；既然文件本来就不会被打包，保留字符串对导出方自己也只剩"在自己机器上重新导入"这一种
  边缘用途。
- **Windows 保留设备名**：`NUL.txt` 不是文件——`File.Exists` 返回 TRUE，于是重名检查通过、
  `FileMode.CreateNew` 又对空设备"成功"，该条目**静默消失**。现拒绝 `CON/PRN/AUX/NUL/COM1-9/
  LPT1-9`（带或不带扩展名、不区分大小写）。
- **结尾的点/空格会被 Windows 静默去掉**：`key.png ` 与 `key.png` 在磁盘上冲突，而重名检查认为
  它们不同。现拒绝。
- **嵌套条目路径**：导出器只产生扁平文件名，嵌套路径不带来任何好处却成倍放大落点面，现拒绝。
- **junction / 符号链接穿透**：`GetSafePackageTargetPath` 的前缀检查是**词法**的，看不见磁盘上
  已存在的 `CustomImages\` 子目录其实是 junction——预先放置一个链接就会让每次写入落到 Mod 目录
  之外。现对已存在的目标检查 `FileAttributes.ReparsePoint`。
- **`WriteFileEntry` 对消失的资源静默 `return`**：导出照样**成功**，而引用已被改写成裸文件名，
  接收方拿到指向不存在资源的布局，导出方毫不知情；条目数/体积闸门也与实际写入不一致。现抛
  `FileNotFoundException`，由外层 catch 走"导出失败"并清掉 `.tmp`。
- **回滚会删掉用户自己的 `.corrupt` 安全副本**：唯一名分配只检查 `File.Exists(<name>.json)`，
  所以导入可以落在一个其 `.corrupt` 是用户早先安全副本的名字上。现 `TrackProfile` 先记录该
  `.corrupt` 此前是否存在，只删本次导入自己造成的。
- **被强杀的导入永久泄漏空间**：`TryDeleteDirectory` 只删 GUID 子目录，父目录
  `<mod>/.jkv-staging/` 永不清除，而进程崩溃留下的 GUID 目录（最多 2 GB）无人回收。现新增
  `SweepStaleStaging`（24 小时以上的 GUID 目录 + 空父目录），在 `TryLoadResources` 时执行。
- Harness 增至 110 项（含保留设备名、结尾点/空格、嵌套路径拒绝与扁平名仍通过的回归）。

### 编辑器/输入深审修复（2026-09-26，第 42 轮）
两份子代理报告（编辑器撤销+交互状态、输入处理+每帧热路径）合计挑出 10 个必修项。

高危：
- **【帧崩溃】雨滴存活数无上限 + 设置页不钳制**：`CreateRainDropForKey` 直接 `rainList.Add`，
  没有任何上限判断，而设置页文本框刻意不钳制键入值。寿命 = `RainHeight*300/RainSpeed`，
  键入「高度 100000、速度 1」→ 8.3 **小时**；单键每秒 10 次按压即堆积数十万滴，每滴每帧写进
  **同一个**合并 mesh 并把该层标脏，帧时间彻底崩塌。`MAX_RAWRAIN_POOL_SIZE` 只管池不管存活数。
  现：按排高度钳到滑杆区间（1..2000）并先过 `SanitizeRowFloat`（这几行是当时仅存仍缺 NaN/Inf
  净化的按排浮点），另加每键 128 滴硬上限——超限时回收**最老**的一滴（轨迹是流，砍尾部不可见）。
- **【静默改数据 + 每帧数十次整层重建】浮点字段用 `"0.##"` 回显**：超过两位小数的值渲染后被
  四舍五入，回解差异 > 0.001 阈值，于是该字段在**每个** Layout/Repaint 都"提交"一次。最常见
  来源是拖拽产生的 `X=100.3333`——面板一绘制就被改写为 100.33，且对每个此类字段各跑一遍
  apply（压历史 + 存盘 + `ResetKeyViewer`），单帧可达数十次完整拆解。现改用 `"R"`（可精确往返）
  并把判定从「数值是否差 0.001」改为「**文本是否变化**」。
- **【撤销永久抹掉全表计数】**：`EditorHistory` 是后置状态时间线，普通条目一律
  `PreserveCounts=true`（剥离计数）以免撤销回滚游玩计数——但这意味着「重置计数」销毁的状态
  从未进过时间线：只压后置条目时撤销落到前一条并从**刚被清零的实时文档**回填，按一次 Ctrl+Z
  就永久抹掉整张计数表并落盘。现清零**之前**先压一条保计数条目，清零后再压后置条目。
- **【两处回归，与已记录修复不符】**
  - `ClearEditorInteractionState` 清 `fmHasKeyFocus`（快捷键门槛，只在画布 MouseDown 恢复）
    → **按一次 Ctrl+Z 之后所有编辑器快捷键失效**，必须再点一下画布；按住 Ctrl+Z 只退一步。
    现删除该行（窗口是否打开由 OnGUI 早退保证，焦点不是手势）。
  - `DrawEditorToggle` 无条件补调 `EditorPropertyChanged()` → 架空本轮刚加的
    `EditorOnlyChanged`（`fm_unselectable` 仍会 `ResetKeyViewer`），且已自行处理就地刷新的调用点
    会跑两遍回调（多一次整档快照 + 一次拆解）。现加 `after` 尾参，并让四个就地刷新处理器
    置 `editorInPlaceRefresh` 标志，由 `DrawEditorToggle` 自动识别——**将来新增的调用点也自动
    受益**，不必逐个改。
- **【卡死路径】小地图拖拽与窗口缩放手势缺 `Input.GetMouseButton(0)` 兜底**（画布拖拽路径
  本就有）：丢一次 MouseUp（Alt-Tab、拖出窗口、焦点变化）后标志**永久**为真，小地图分支吞掉
  全部 MouseDown/Drag/Up 并返回（选中/拖拽/手柄/框选/快捷键全失效）；窗口缩放则是此后每次
  MouseDrag 都把窗口拽成鼠标形状。现两处都补兜底，窗口缩放的释放另限定 `e.button == 0`。

中/低危：
- **逐帧 `Enum.TryParse` 鬼键/面板绑定**：`Enum.TryParse` 会对约 500 个 KeyCode 名字做
  OrdinalIgnoreCase 比较，112 节点全绑定即每帧约 5.6 万次字符串比较——而同一方法里**主绑定
  早已缓存**（`CustomKeyBindCached`），鬼键与面板纯属遗漏。现按同一模式加
  `CustomGhostCode`/`CustomPanelCode` 缓存。
- **工具栏撤销/重做按钮不清手势态**（只有键盘快捷键清）：同一撤销因触发方式不同而行为不同。
  现 `EditorUndo`/`EditorRedo` 自行清理，幂等。
- **`EnsureCustomNodes` 不修重复 Id**：Id 是选区重映射、撤销计数回填与捕获状态共用的身份键，
  重复会让其中一个无法按 id 选中并拿到对方的按压计数。现复用静态 HashSet 去重（零分配）。
- **`!Application.isFocused` 出口只 return 不复位**（其余五处都复位）：Alt-Tab 回来后下一键被吞成
  改绑并 `SaveSettings`，屏幕上却无捕获 UI 可解释。现改为解除武装。
- **`_hasKeyPressActivity` 三个早退都不清闩锁**：切到自定义布局或关掉每键 KPS 后该标志**永久**
  为真，此后每帧跑完 40 个队列的排空循环却无任何写入。现早退前清标志。
- `SetupKey` 主键分支 `keyTexts` 未判空（脚键分支判了）——绕过 `EnsureSettingsArrays` 的加载
  路径会在改键那一帧抛 NRE。现补齐。
- Harness 增至 116 项（含雨高净化、存活上限、浮点字段精确往返/不再截断两位小数/非有限值显示）。

### 每帧分配与增量计数（2026-09-26，第 43 轮）
- **`FloatSliderField` 每 IMGUI 事件约 7 次堆分配**：`new GUIContent(label)`、`"fsf_" + n` 拼接、
  `slid.ToString(format)`、三处 `GUILayout.Width(...)`（`GUILayoutOption` 是 **class**）以及
  `TextInputField(..., params GUILayoutOption[])` 的数组。雨滴页单页 30 处调用 × 每帧 ≥2 个事件
  ≈ **每帧 400+ 次、60fps 下约 25,000 次/秒**托管分配，是全项目最大 GC 压力源，且正是用户调
  雨滴时一直待着的那一页。现：`GUILayoutOption` 与格式化回显改为实例字段并**只在值真的变化时**
  重建、控件名预生成数组、Label `GUIContent` 按字符串缓存。
  注：宽度/标签缓存刻意用**实例**字段而非静态——静态初始化器会在该分部类首次被触碰时运行，
  从而把 `GUIContent`/`GUILayoutOption` 拖进类型的静态构造，让从不绘制设置页的调用方也需要
  `UnityEngine.IMGUIModule`（Harness 立刻暴露了这一点）。
- **`CustomGroupTotal` 每帧 O(面板数 × 节点数)**：该方法每帧被**每块** Total 面板各调一次，而
  实现是带每节点一次 `GroupId` 字符串比较的完整 `CustomNodes` 遍历。现改为增量
  `Dictionary<string, long>`：计数只在 `ApplyCustomKeyEdge`（一次按压）与
  `RecalculateCustomTotalCount`（结构/成员变化）两处改变，前者增量、后者置无效后重建，
  重建后的首次读取走惰性构建，保证该表始终由实时文档推导。
- **`HasTextGradientSettings()` 在出厂默认路径上每帧扫全部节点**：完全没有渐变时（默认）它仍要
  遍历 `CustomNodes` 只为返回 false——2048 节点的文档即每帧 2048 次迭代，永不停歇。现按
  「文档长度戳 + 答案」缓存，`ClearTextGradientStates()`（每个调用方都是覆盖层重建/字体变更）
  负责失效。
- **`UpdateCustomVideoFallbacks` 每帧遍历全部节点**：回退是对一次解码错误回调的**一次性**反应，
  扫描却在每帧对每个节点做 `IsNullOrWhiteSpace` + 两次哈希探针。现由 `OnVideoError` 登记到
  `pendingVideoFallbacks`，集合为空时**完全不扫描**；处理后从集合移除，仍待处理且已不存在的 id
  会被剪除（否则一次失败会让该扫描永远运行）。
- **粘贴把源节点的按压计数也复制过来**：`EditorCopySelection` 直接 `Clone()`，而 `Clone` 复制实时
  `Count`，于是每次粘贴都把**源**节点的计数再加进文档一遍——把一次游玩的总数翻倍，第二次粘贴
  再翻一倍。剪贴板是**模板**不是记录，粘贴出的节点是全新节点。现复制后清零。
- **MelonLoader 每帧 `Enum.TryParse` 解析热键**（对所有用户，每秒 60 次重新发现同一个值）；
  `ReadPressedKey` 每次捕获 `Enum.GetValues` 分配约 500 元素数组。现缓存解析结果（仅在存储
  字符串变化时重解析）并复用 `KeyViewer.AllKeyCodes`（为此把该字段从 `private` 提升为
  `public`——各加载器需要同一份列表）。
- **刻意不修**：`KvEasing.Ease` 的字符串 switch 保留。`Normalize` 返回静态 `Names` 数组的元素，
  故各 case 与 interned 字面量比较、`string.Equals` 以引用相等短路；改写成下标必须重编 26 个
  case，一个 off-by-one 就会让已保存的配置静默用上**错误**的缓动曲线，不值得省那几次指针比较。

### 节点归属与撤销身份（2026-09-26，第 44 轮）
- **节点被归到已删除的组名下（不可见 + 不受开关控制）**：`fmActiveGroupId` 只在组删除按钮里清，
  于是经撤销/文档切换/配置加载删掉一个组后，活动组仍指向那个死 id——下一个新增节点、乃至粘贴
  的节点都会被归到它名下：在组管理器里**不可见**、**无视**该组的可见性开关、也无法通过"选择
  该组"到达。粘贴更糟：剪贴板的生命周期长于文档的组表（只在切换配置时清空）。现新增
  `EditorGroupExists`（拆出纯函数形式以便离线验证），新增与粘贴两条路径都在归组前校验，死 id
  一律落到文档化的空组默认值。
- **撤销会把 id 计数器倒转 → 双向走时间线产生重复 Id**：粘贴出 id 5..9（计数器 → 10），撤销两次
  （计数器 → 5），新增节点拿到 id 5，重做——文档里于是有**两个** id 5 的节点。Id 是选区重映射、
  撤销计数回填与每节点 IMGUI 控件名共用的身份键，冲突会静默吞掉其中一个。现计数器只单调上升
  （取 `max`），文档的撤销/重做保持精确且永不重复发放 id。
- **关闭编辑器不清撤销栈**：每条都是整份文档的 JSON 快照（112 节点约 0.5 MB），此前只有切换配置
  才释放——长时间会话反复开关编辑器数十次会常驻数十 MB 无用历史，重开后还会看到一条基线早于窗口
  本身的撤销栈。现随窗口一并 `Clear()`。
- **撤销后活动组/缓动弹窗悬空**：`RestoreEditorSnapshot` 换掉了组表与节点表，却不清
  `fmActiveGroupId` 与 `fmEasingPicker`（切换配置的路径会清，撤销不会）。缓动弹窗是按每节点控件
  **名**为键的，所属节点消失后弹窗会悬在一个不再绘制的控件上。现一并清理。
- **跨窗口捕获只堵了一半**：编辑器武装节点捕获时会解除设置页武装（此前已修），但**反向没有**——
  设置页武装固定布局改键不解除 `fmCaptureNode`。两个窗口可同时打开（Melon 驱动两个 IMGUI pass），
  而 `ProcessKeySelection` 在 `Update` 里轮询 `Input.GetKeyDown`，**早于**编辑器的 `OnGUI` KeyDown
  处理，于是同一次物理按键被两边同时消费：固定布局槽位绑定与 FreeMake 节点 KeyBind 一起被改。
  现设置页武装时调 `CancelEditorNodeCapture`。
- **两路节点捕获可同时武装**：主键捕获与鬼键捕获在同一个 pass 里各跑各的 KeyDown 块，一个按键被
  处理两遍。鬼键按钮此前**既不**解除设置页武装**也不**解除主键捕获；主键按钮不解除鬼键捕获。
  现两边互相解除。
- **节点捕获未排除加载器设置热键**：`ProcessKeySelection` 排除了（`loader.SettingsHotkey`），
  编辑器没排——武装中按 F1 会既把热键绑进节点、又关掉设置窗口。现两者共用同一份排除。
- Harness 增至 120 项（含 `EditorGroupExists` 纯函数形式的 4 条：活组接受、死组拒绝、空组恒存在、
  无组时任何非空 id 都是死组）。

### OnGUI 分配与雨滴清理（2026-09-26，第 45 轮）
- **`DrawTabBar` 每 IMGUI 事件 7 个对象**：`string[6]` 字面量 + 六次 `"tab_" + key` 拼接，
  在**一直开着**的那个窗口上每帧持续分配。现 i18n 键表提为静态、已翻译标签按语言缓存。
- **`DrawPerKeyColorEditor` 每按键每事件 3 个数组**：`string[8]` + `Color[8]` × 2 的数组字面量，
  24 个按键 × 每帧 ≥2 事件即每帧 150+ 个对象，就在 Colors 页上。现改为三个复用暂存数组。
- **`DrawPerKeyCountReset` 每按键每事件 3 次分配**：`Count[s].ToString()`、控件名拼接、
  以及 `"reset_counts" + " (" + n + ")"` 的两次拼接——包括用户根本没滚到的按键。现按钮文本按
  其显示的计数缓存、控件名按槽位预展开。
- **`DrawColorPicker` 每取色器每事件约 11 次分配**：五个 `"prefix" + n` 拼接、四次
  `channel.ToString("F2")`（Layout 与 Repaint 各格式化一次同一个值）、以及
  `ColorToHex` 的插值串。现两个前缀是封闭集合故控件名预展开成表、通道回显只在值变化时重建、
  Hex 串按颜色哈希缓存（有界 512 条）。
- **静态初始化器把 IMGUI 拖进类型加载**：`static readonly GUILayoutOption` 会在该分部类首次被
  触碰时运行静态构造，使**从不绘制界面的**调用方也需要 `UnityEngine.IMGUIModule`——Harness
  立刻以程序集加载失败暴露了这一点。凡是 IMGUI 类型的缓存宽度/内容一律用**实例**字段。
- **关掉雨滴后在途雨滴冻结**：`UpdateEffects` 不再跑，所有在途雨滴停在原地、合并 mesh 定格在最后
  一帧的顶点，用户重新打开时会看到雨滴在半空"复活"。固定布局与自定义布局两条路径都改为关闭时
  `ClearActiveDrops`（与 GUI 里其它雨滴子开关的做法一致）。
- **`KvTextStyle.KeyViewerApplier` 静态委托在 `OnDestroy` 未摘除**：它是赋值故从不重复注册，
  但会让静态字段在本组件销毁后仍持有它直到进程结束，此后任何 `Apply` 调用都会写向死组件。
  现 `OnDestroy` 对称清理。
- **刻意不改**：合并 shape mesh 每次按键全量重建。`OnPopulateMesh` 以 `VertexHelper.Clear()`
  为前提，而 uGUI 的 `VertexHelper` **没有区间重建**能力，故「只重建脏区间」无法在单一合并 mesh
  的架构下实现；为它拆成多个 mesh 会增加绘制批次，代价远大于收益。同理，按压动画的协程改为
  字段驱动统一 tick 触及动画语义本身，而它每次按键边沿只分配 2 个迭代器（20 KPS 约 40/秒），
  收益不足以承担按压缩放出现异常的风险。

### 两个窗口的文本缓冲互扫（2026-09-26，第 46 轮）
- **【高危】编辑器窗口的 pass 会清空设置窗口正在输入的文本**：文本缓冲表 `textInputBuffer`
  与「本 pass 已绘制」集合 `textCtrlsDrawnThisPass` 是**两个窗口共用**的，靠 `fme_` 前缀区分。
  `EndTextInputPass` 会跳过 `fme_` 条目（正确）却清扫**所有非** `fme_` 的条目——而编辑器 pass
  在绘制前刚调过 `BeginTextInputPass()` **清空**了该集合，故集合里只剩编辑器自己的名字。
  于是两个窗口同时打开时，设置窗口的**每一个**文本缓冲都被判为「本 pass 未绘制」而丢弃：
  在设置窗口输入十六进制色或滑杆值时点一下 FreeMake 编辑器，进行中的文本被静默清掉。
  唯一的豁免是 `key == focused`，而它恰好在焦点移进编辑器的那一刻失效——正是触发条件本身。
  现编辑器 pass 改用其专属的 `EditorGcBuffers()`（只扫 `fme_`），不再调用共用的清扫；`EditorGcBuffers`
  的注释本就声明了两者「互不越界」，此前那一行调用正好违反了它。
- **组总数增量缓存改为按文档身份判有效**：`groupTotals` 原先只靠「有没有人记得调
  `RecalculateCustomTotalCount`」来失效。任何换掉 `Settings.Data` 的路径（切换配置、套用预设、
  `.jkv` 导入、DmNote 导入、导入失败回滚）若漏调，某个组总数会在**本次会话余下时间**里与文档
  静默不符。现额外记录该表所依据的 `ProfileData` 实例，身份不同即重建——缓存自愈，不再依赖
  枚举调用点。

### 两份全新审计的修复（2026-09-26，第 47 轮）
两份子代理分别审 DmNote 导入/`.jkv` 包与加载器/启动路径，合计 16 条；本轮修掉其中 9 条。

高危：
- **【整层雨滴画布永久损坏】每节点雨滴几何三元组是唯一漏净化字段**：`RainWidth`/`RainHeight`/
  `RainSpeed` 原样进入 `RawRain.UpdateLocation`，而那里失败形态不是「数字不对」——过大的速度让
  y 溢出成 Inf，`sizeY = Inf - Inf + height` 变成 NaN，回收判据 `if (sizeY < 0)` **对 NaN 为
  假**，于是该雨滴永不退役，其 NaN 顶点每帧写进**共享**合并雨滴 mesh，整块画布永久乱码。
  触发源：不可信的 DmNote 预设 `"rainSpeed":1e38`，或用户在编辑器节点雨滴高度/速度文本框键入
  （`Mathf.Max(0f,v)` 挡不住）。按排全局值在第 42 轮补了 1..2000 钳制，按节点这条路径没跟上。
- **【`.jkv` 可写满磁盘】展开上限只按归档自报的 `entry.Length` 计算**，那是中央目录里由文件自己
  提供的元数据；真正落盘的 `Stage()` 是一个不带计数器的 `CopyTo`。声明 1 字节、膨胀出数百 MB
  的条目能通过每一项检查后被同步写入（主线程硬冻结）再 move 进 `CustomImages\`。现改为手写
  拷贝循环累计**实际写出**的字节数，超 `MaxPackageEntryBytes` 即抛（`KvImageLoader` 的 16MB/
  4096² 只在加载时生效，救不了落盘）。
- **【每帧 NRE 淹没真错误 + 热键永久失灵】`MelonEntry.OnUpdate`**：偏好创建失败时 `_hotkeyEntry`
  为 null，而 `OnUpdate` 无条件解引用——「保持关闭」的实际效果是每秒 60+ 次 NullReferenceException
  把真正那行错误埋掉；热键捕获时 `MelonPreferences.Save()` 抛异常（cfg 被锁/损坏），而
  `_capturingHotkey = false` 在它**之后**，捕获态永久卡住、`OnUpdate` 每帧提前返回，设置热键彻底
  失灵、用户再也无法用键盘关窗。现开头判空，并把清捕获态移到任何可能抛异常的动作**之前**。
- **【一次写盘失败毁掉整个窗口】四处 IMGUI 调用点绕过 `SaveSettings()`**：`DrawTabBar` 的
  `SaveMetaOnly`、`DrawProfileSaveAs` 的两次写、`SyncProfilesWithDisk`、编辑器套用预设的
  `SaveCurrentProfile` 都是裸调，磁盘满/目录只读时异常从 GUILayout 回调抛出——**已 Begin 未 End
  的布局组留在栈上**，自该帧起该窗口布局错乱、控件不再响应直到重启，且 `lastSaveError` 从未被
  设置所以红色横幅也不出现。标签页触发面最广（磁盘一满，点任意一次标签就触发）。现新增
  `GuardedSave(what, write)` 统一走同一套 try/catch + 横幅。
- **【【回归，上一轮引入】取色器控件名查表忽略了 `prefix`**：预展开表被裸序号索引，编辑器也拿到
  `cpi_N`，静默废掉 `fme_cpi_` 隔离（即「在一个窗口打字改写另一个窗口的字段」那条老 bug 复发）。
  越界判据也比错了长度（表实为 1024 而判 1024 意味着序号可取到 `fme_cpi_*` 段）。现改为**每个
  窗口一张表**，由 `prefix` 选表，判据用该表自身长度。

中/低危：
- **【重载后每次按键计数翻倍】加载器卸载无拆解**：MelonLoader 卸载不触发 `OnToggle(false)`，
  带 `DontDestroyOnLoad` 的覆盖层存活并继续绘制/计数，而界面上已没有任何东西能关掉它；用**新版
  DLL** 重载会多出第二个组件——两层画布叠加、两条 Update 读同一批物理按键，每次按压计数 **+2**，
  旧组件仍在写配置。现新增 `Main.Shutdown()`（含委托退订）并在 Melon 入口的
  `OnDeinitializeMelon` 调用。
- **同进程第二个加载器被静默吞掉**：UMM 入口在**构造函数**里就挂好 handler 的事件、之后才调
  `Main.Init`，而 `Init` 在 `initialized` 为真时直接 return ——于是这些委托一个都没订阅、
  `Loader.Instance` 仍指向旧 handler、设置面板画不出内容，UMM 却仍报告「已加载」。现明确报错。
  实现要点：C# 不允许在声明类型之外给事件赋 null，故把三个委托存进字段，`Shutdown` 才能 `-=`。
- **武装中的改键在关掉显示时「冻结」**：`ProcessKeySelection`（失焦/关窗/UMM 隐藏时解除武装的
  唯一出口）整段被 `Update` 的启用门控包住，而 `DisableKeyViewer` 不碰 `SelectedKey`。症状：武装
  一个槽位 → 关掉总开关看干净画面 → 再打开 → 接下来按的**第一个**键被静默绑进该槽位并落盘。
  现 `DisableKeyViewer` 开头解除武装。
- **DmNote 雨滴字段名两种前缀**：宽度读 `noteWidth`、高度读 `rainHeight`、速度读 `rainSpeed`，
  而同函数其它 note 作用域字段全是 `note*` 前缀。现两种拼写按 note 优先顺序都读。
- **DmNote 标签跨表静默丢节点**：标签只要在任一表里就被选中，但每张表严格读取——两张表标签命名
  不一致时只导入一张，提示却说「已导入 2 个节点」而 7 个按键节点整个消失。该导入器对其它一切
  不支持项都发去重警告，唯独这条报告成功。现补 `dmnote_partial_tab` 警告（三语）。
- **DmNote 回滚的 `SaveMetaOnly` 静默吞异常**：失败会让磁盘指向**已删除**的文件，下次启动走
  「Profile not found」、用户原本在用的配置不被加载，且日志无、横幅无。`.jkv` 导入器对完全相同的
  操作是会上报的——两条路径不一致。现上报并置横幅。
- **`Loader.Instance = null` 不清缓存**：`Main.Shutdown()` 现在会赋 null，而拆解若留下旧路径
  缓存，后续实例读写的文件夹就与它报告的不是同一个。现 null 分支同样清空。
- Harness 增至 **123** 项（含每节点雨滴 NaN/Inf 净化、边界、以及键入 8 小时寿命被钳制的回归）。

### 旧字段修复：改用版本闸门并加覆盖测试（2026-09-26，第 48 轮）
- **【静默毁掉用户配置】`savedPoison` 启发式会误伤合法节点**：该判据是「双不透明度 0、双缩放
  ≤0.5、Glow 0、缓动串为空」。但「不要标签不要光晕」是完全正常的节点配方，而缓动选择器出现
  之前写下的节点其缓动串**天然**为空。于是这些节点在**每次加载**与**每次覆盖层重建**时被静默
  重置约 30 个字段**并落盘**。启发式永远只能是猜测。
  现改用 Profile 自身的 `DataVersion` 闸门（`DataVersion < NodeTextDefaultsVersion`）——它直接
  说明磁盘文件是否写于该字段存在之前，而 `SaveCurrentProfile` 会把它向前盖章，故修复每个
  Profile 最多跑一次、且绝不在当前版本 Profile 上跑。闸门覆盖了 `savedPoison` 想处理的场景
  （被错误构建重存过的 Profile 早已被盖章）。
  `EnsureCustomNodes`（每次覆盖层重建都跑、不在带版本控制的加载路径上）现施加同样闸门。
- **`NodeTextDefaultsVersion` 是无任何读取点的死常量**：它注释承诺的「递增以让修复对更早 Profile
  生效」根本不可能发生——修复没有版本闸门也不是 one-shot，唯一门槛是值启发式。现它真正被读取
  （闸门），并加了**强制覆盖**的 Harness 测试：把 FmNode 所有数值/布尔字段清零，跑「修复 +
  EnsureCustomNodes」（真实加载顺序），要求**每一个**带非零初始化器的字段最终都是非零。
  新增字段而漏加修复条目，现在会让**测试失败**而不是静默读成 0。
  注：实现上不能拿「新节点做差集」——恰恰对我们关心的字段，修复恢复的就是初始化器本已给出的值，
  永远看不出「变了」；必须先清零再跑修复。
- **覆盖测试当场查出 12 个此前漏掉的字段**（均为成批加入、故历版清单都漏了）：
  1.7.2 的整套文字描边/阴影块 8 个（`KeyText`/`CountText` 的 `OutlineThickness`、`ShadowEnabled`、
  `ShadowOffsetX/Y`）——旧节点全读成 0/false，描边与阴影整个消失；以及 `CountInTotal`（false
  会让节点每次加载都被排除在全局 Total 之外）与 `Opacity`（0 会被 `EnsureCustomNodes` 钳成 0
  原样保留，旧节点渲染得**完全不可见**——正是本修复最初要解决的那类故障）。现全部补入。
  `Width`/`Height` 由 `EnsureCustomNodes` 的钳制负责，故测试跑真实加载顺序而非只看修复体。
- Harness 增至 **127** 项（含：合法 0.5 缩放不被值修复动、非法 0 缩放仍被修复、**当前版本
  Profile 的故意 0 不透明度/0.5 缩放/无光晕原样通过加载**、陈旧 Profile 仍被修复、以及那条覆盖
  测试）。

### DmNote 统计面板绑定（2026-09-26，第 49 轮）
- **【用户的 Total 面板直接消失】统计面板拿按键名去判定类型**：回退条件是
  `names.Count >= statElements.Count`，而**最常见**的「7 个按键 + 2 个面板」预设恰好满足，故回退
  常态触发。于是：(a) 无 `displayText` 的面板把某个**按键的名字**当成了自己的标签；(b) 无
  `statType` 的面板在 `ResolveDmNoteStatType` 里拿那个按键名去匹配，既不含 total 也不含 kps，
  于是被**丢弃**——用户的 Total 面板就这么没了，只留下一条泛化的「不支持的统计面板」提示，
  而真实原因（面板没写 statType）根本没提到。
  现回退条件改为**等长**：等长是「同一下标互相对应」的唯一条件，不等长时传 null，让面板回退到
  全局 KPS/Total 标签（无名面板本就该如此），并新增 `dmnote_stat_names_skipped` 提示说明原因。
- **无 statType 的面板改用专门提示**：`dmnote_skip_stat_untyped`，不再把「没写 statType」报成
  「不支持的面板」——后者把用户引向错误的排查方向。
- **`UseCustomCountFontStyle = true` 是死赋值**：四行后被 `ApplyDmNoteFontStyles` 无条件覆盖
  （后者依据 counter 对象是否真的带样式键来决定）。若哪天活下来，每个导入节点都会声称自己有一个
  并不存在的计数字体覆盖。现删除并在原处写明理由。
- Harness 增至 **129** 项（含「keys 非等长平行数组时统计面板仍保住自己的 statType」与
  「无 statType 的面板给出具体原因」）。

### 文字渐变/样式层深审（2026-09-26，第 50 轮）
子代理用 ilspycmd **反编译实际的 `Libs/Unity.TextMeshPro.dll`** 核实 TMP 行为（非凭记忆），
查出第 34 轮那条「已修复」其实**并没有修好**。

- **【【招牌功能失效】标签字形渐变被第一次按键永久抹掉**：第 34 轮的修法是「在缓存早退之前
  强制白色基色」。反编译证明：TMP_Text.color 的 setter 置 `m_havePropertiesChanged` 并调
  `SetVerticesDirty()` → 注册 PreRender 重建 → `GenerateTextMesh()` **无条件**用
  `m_fontColor32` 重绘每个顶点色。于是：按压写实色 → 本行写白色 → 缓存比较
  `Text/Left/Right` 全都没变 → return → PreRender 把整条标签重绘成纯白。**渐变正是被这行
  本该保护它的代码毁掉的**，且因为标签文字再也不变，没有任何东西会重新应用它。
  计数渐变只是因为**计数文字每次按压都变**才侥幸躲过——所以这个 bug 在最常被测的计数上
  完全看不出来，而在标签上是致命的。
  现两处都修：(a) 缓存多一个条件 `GradientStillApplied`——读第一个可见字符的首顶点，确认
  mesh 里**确实**还带着我们写的着色（TMP 就地重绘 `colors32`，故一次数组读取即可检测）；
  本帧改过 `text.color` 时直接不走早退。(b) **从源头**让按压写色变成渐变感知：
  `ApplyCustomKeyColors` / `ApplyCustomSpecialColors` / 固定布局的 `UpdateKeyColors` 在
  渐变生效时写 `Color.white`——因为渐变 pass 在同一帧内跑得更早，**赢不了** TMP 重建这个竞态，
  必须在源头就不写实色。按下渐变变体提供真实的按下颜色，故无损失。
- **【勾选节点渐变毫无作用】`InvalidateGradientScanCache` 在自身文件外零调用点**：该扫描按
  节点**数**打戳，而勾选渐变既不改节点数、也不会（`editorInPlaceRefresh`）重建覆盖层，于是
  `HasTextGradientSettings` 一直返回陈旧的 false。反方向的陈旧值（关掉最后一个渐变）则让每帧
  全量遍历 `Keys` 的开销**永久**钉住，恰好抵消第 43 轮那次优化的收益。现
  `EditorTextGradientPropertyChanged` 开头调用它，两个方向一起修好。
- **【材质淘汰会销毁自己正要返回的材质 + 遍历中删字典抛异常】**：
  `GetTextStyleMaterial` 先插入（引用计数按构造就是 0——**唯一**必定匹配淘汰判据的条目）再淘汰，
  于是判定可能落在正要返回的材质上，把**已销毁**的 Material 交给调用者并赋给每个存活标签；
  `Object.Destroy` 是延迟的，故文本渲染一帧后**变空白**。更糟的是旧代码在遍历该字典的
  `foreach` **内部**调 `Remove`——.NET 会使枚举器失效，下一次 `MoveNext()` 抛
  `InvalidOperationException`；它没被发现只是因为通常先命中 `break`（需恰好同一次迭代降到上限，
  并无保证），而异常会浮到 IMGUI 回调里**禁用整个设置窗口**。现：淘汰排除刚插入的键、
  改为先收集**再**删除（另加一个 `List<long>` 暂存——文本暂存同时在用）。
- **【全局描边/阴影色绕过净化】**：第 39 轮加的 `ColorOf` 只覆盖节点 `float[]` 分支；这四个
  全局 `Color` 字段（`KeyText`/`CountText` 的 `OutlineColor`、`ShadowColor`）没有任何地方清洗，
  `EnsureCustomNodes` 也只净化节点数组。共享的 `.jkv` 或手改配置能把 NaN/Inf 送进
  `SetColor("_OutlineColor"/"_UnderlayColor")`（片元输出 NaN → 描边变黑或消失），且 `Color32`
  缓存键把每个非有限颜色量化成同一个 int，两个不同的坏样式撞到同一材质。现加 `SafeColor`。
- `KvEasing.cs` 判定**干净**：26 条曲线与 Penner 定义逐条核对无误（含 1.70158/1.525 的 back
  常数与 in-out expo 的 1e-10 守卫），字符串 switch 保留是有意决定。`SampleCurve` 全仓库无
  调用方（死代码），但成本可忽略，未删。

### `.jkv` 文本读取的有界化（2026-09-26，第 51 轮）
- **settings 条目先物化再判上限**：`ReadPackageEntryText` 先检查 `entry.Length`，然后
  `ReadToEnd()`，**之后**才测 `text.Length`——上限是事后才生效的。32MB 字节的条目会先变成最多
  3200 万**字符**（约 64MB）才被拒绝；且那次事后比较是拿**字符**数比**字节**上限（非 ASCII 时
  最多差 2 倍）。settings 条目是**最先**读取的（早于任何资源落盘），故是对恶意包消耗内存最
  廉价的位置。现改为按**字节**计数、读取过程中即停在上限，峰值分配被上限本身约束；BOM 处理
  （UTF-8/UTF-16 LE/BE）显式实现，保持 StreamReader 的默认行为。
  **诚实说明**：旧代码**也会**拒绝超限条目，只是拒绝得晚；这不是「修好了某个崩溃」，而是把
  峰值分配拉回上限之内。故对应测试断言的是**契约**（上限被强制、正常条目往返、BOM 被剥离），
  而非内存曲线。

### 雨滴渲染层深审（2026-09-26，第 52 轮）
子代理审 `RainLayer`/`RawRain`/`RainSystem`。先说结论：**逐帧托管分配为零**（逐一核过每个
`new`：都是 struct、`Nullable.Value` 不装箱），池/存活数/活跃集三处上限与剪枝也都正确。
真正的缺口全在**非有限值**与**陈旧状态**上。

- **【整块鬼雨画布永久损坏】鬼雨层压根没有有限值守卫**：`DrawDrop` 专门写了守卫（注释原话：
  「一个坏雨滴就会污染整块雨滴画布」），而 `GhostRainLayer.OnPopulateMesh` 读的是**同一个**
  `rainList` 里的**同一个** `rain.rect`，唯一判据却是 `r.width <= 0f`——**对 NaN 与 ±Inf 都为假**。
  一滴坏鬼雨就把每滴 8 个 NaN 顶点写进共享鬼雨 mesh，把**整块**鬼雨画布（所有键、所有雨滴）在
  本次会话余下时间全部搞坏。更糟的是 `AddTiled` 的 `while (x < r.xMax - 0.01f)` **没有迭代
  上限**：xMax 为无穷时永不退出，每次迭代加 4 个顶点直到进程卡死。现两层共用
  `IsFiniteRect`（含 xMax/yMax —— 无限宽度产生的正是 width 有限而 xMax 溢出的形态），`AddTiled`
  加 256 次/轴上限并兜住非正步长（按**裁剪后**尺寸前进，与原语义完全一致）。
- **【`+Inf` 速度造出不死雨滴】按排速度是最后一个漏净化的按排值**：`Mathf.Max` 是
  `a > b ? a : b`，所以 NaN 恰好被兜到最小值（**安全**），而 `Mathf.Max(+Inf, x)` 返回 +Inf
  ——`y = elapsedMs * Inf = Inf` → `sizeY = Inf - Inf + height = NaN` → `if (sizeY < 0)` 对 NaN
  为**假** → **雨滴永不退役**，每帧往共享 mesh 写 NaN 顶点。鬼雨从不淡出，故不死鬼雨是必然的。
  极大但**有限**的速度虽不崩但同样错：生长期间 `FinalSize.y` 每帧被重新赋为当前 y，`sizeY`
  代数上塌成常量，雨滴永远无法越过顶端回收——用户看到一根卡住的全高条。现按 0.001..200 钳制。
- **按排宽度同洞**：`RawRain` 的 `Mathf.Max(NodeWidth, 1f)` 也放 +Inf 过 →
  `rect = (cx - Inf*0.5, …, Inf, h)`。而 `DrawDrop` 的守卫**只对 `scaleF`** 测了 `IsInfinity`，
  故 xMin=-Inf / xMax=+Inf 直接进了共享 mesh。现宽度也过 `SanitizeRowFloat` + 0..2000 钳制，
  且守卫补上矩形的 `IsInfinity`。
- **【左/右对齐雨滴挂在节点外面】用的是陈旧的 `key.rainWidth = 50`**：该字段只有**固定布局**的
  按键工厂写过一次，于是每个 FreeMake 按键永远保留字段默认值，而矩形用的是真实宽度。左/右
  对齐时雨滴按 `(50 - w)/2` 偏移：节点宽 200、RainWidth 80 就有 15px 在框外。按排默认值本就是
  50/40/30，故第 2/3 排节点在**完全没配置**时就已偏 5–10px。居中对齐恰好正确（两个偏移相消），
  所以一直没人发现。现改用雨滴自身解析出的宽度 `w`。
- **`UpdateEffects` 裸索引 `keys[ki]`**：`ki` 来自上一个 `Keys` 数组，而 `rainActiveKeys` 只由
  `ClearActiveDrops` 清空。子代理逐一追了全部调用点，诚实报告**没能**构造出可复现路径——这是
  潜在缺口而非已确认崩溃；仍值得修，因为同类「裸 `Keys[i]` 掀翻整帧」在鬼键路径上（第 35 轮）
  已被当作 bug 处理。现守卫后自愈（丢弃死下标继续）。
- **`ClearActiveDrops` 在 `keys == null` 早退**之后才清记账**：覆盖层关闭期间切换开关会静默留下
  陈旧下标，而那正是 `UpdateEffects` 用来索引的东西（本类其它四处早退都照常记账）。现先清。
- Harness 增至 **134** 项（按排速度在 `SyncCachedSpeeds` 之后必须全有限）。
  注：`RainLayer` 继承 `MaskableGraphic`，Harness 运行时加载不了该基类，故共享守卫
  `IsFiniteRect` **无法**离线单测——这条只能靠代码审查。子代理给出的 `AddTiled` 循环改法在落地时
  修正了一处它描述得不够精确的地方：原循环按**裁剪后**的 `wTile`/`hTile` 前进，改写成 for 的
  步进表达式会跳过被裁剪的边缘块，故步进仍留在循环体内。

### 「颜色控件不起作用」的量化排查与雨色实时刷新（2026-09-26，第 54 轮）
- **先证明不是数据模型的锅**：脚本枚举 `ProfileData`（423 个 public 字段）与 `FmNode`（187 个
  public 字段），对每个字段统计其在 `Core/`+`Rendering/`+`Rain/`+`Loader/`（即运行时目录）里的
  引用。**FmNode 零死端字段**；`ProfileData` 只有 2 个零运行时引用，其中 `GUIUtils` 是静态辅助
  类的误报，`Unselectable` 是**有意**的纯编辑器语义（第 40 轮已记录：游戏中根本没有「不可选中」
  这个概念）。故用户反复报的「颜色控件不起作用」**不是**「控件写了没人读」造成的。
- **【设置窗口改雨色完全看不到效果】雨滴颜色是创建时烙入的，而 `UpdateAllKeyColors` 不碰雨滴
  系统**：默认轨迹（约 0.3 秒）下几乎察觉不到，但**高轨道配慢速度**时一滴能活好几秒——此时改
  雨色，在屏雨滴会保持旧色直到全部恰好过期，控件看起来就是坏的。FreeMake 编辑器此前用
  `ClearActiveDrops` 绕过了这点，而**设置窗口根本没有**这个调用。
  现新增 `RainSystem.RefreshDropColors(Key[])`：就地重解析在屏每一滴的 `mainColor`（含每键鬼雨
  色与每节点双色渐变的 `ColorTop`），颜色相同则跳过、变了才标脏。就地重绘而非清空，轨迹保持
  连续。
- **顺带把编辑器那条「清空」换掉**：`EditorColorPropertyChanged` 此前调
  `ClearActiveDrops(Keys)`，于是一次颜色滑杆拖动（每秒 60-120 次事件）要把每个键及其每滴存活
  雨滴走 60-120 遍，**且拖动过程中轨迹明显反复弹出**。现改用同一个 `RefreshDropColors`——同样
  的遍历、同样的成本，但没有弹出。

### 设置窗口审计（2026-09-26，第 55 轮）
子代理审设置窗口五个文件 + 数据模型，逐一打开每个「只有一次命中」的读取点以排除「读在死代码里」。

- **主问题：死端字段一个也没有**（与第 54 轮我自己的脚本结论一致，两条独立路径互证）。唯一
  零运行时读取的字段是 `LegacyCustomNodesJson`/`LegacyLayerGroupsJson`（由**加载**路径
  `ImportLegacyCarriers` 读取，不是渲染路径）与 `FmNode.Unselectable`（纯编辑器语义）——都不是死端。
  故用户反复报的「控件不起作用」在本版本上**不复现为死字段**，真正的原因是下面两条**陈旧**问题。
- **【一个存储异常就能毁掉整个设置窗口】`SyncProfilesWithDisk` 的第二处调用点漏了 `GuardedSave`**：
  `:308`（点 `.jkv` 导入按钮展开列表）是裸调，而同一函数的**另一处**调用点 `:195` 已被加保护，
  第 47 轮的记录也把它列为「已转换」——只是这一处被漏掉了。该函数内部
  `Directory.CreateDirectory`/`GetFiles`/`SaveCurrentProfile`/`SaveMetaOnly` **全无 try**。磁盘满或
  `Profiles\` 只读时异常从 GUILayout 回调逃出，留下未闭合的布局组（窗口错乱直到重启），且从不
  设置 `lastSaveError`，连红色横幅都不出现。
- **雨排阴影/描边的颜色、宽度、偏移不刷新在途雨滴**：这四项只在 `SetRowEffect` 里写模型，
  没有清雨滴；而这些值只在 `CreateRainDropForKey` 里烙入、从不逐帧重读。**同一方法**里 20 行之上
  的启用开关会清，下方六个圆角/描边/点状控件也都会清——唯独这四项漏了。于是屏幕上每滴都保持旧值、
  只有新生成的才用新值：轨迹明显双色，在高度 2000 / 速度 50 下可持续约 40 秒；鬼雨永不淡出，最糟。
  现接入 `RefreshInFlightDrops()`。
  **刻意清空而非就地重绘**：阴影/描边的解析是 `CreateRainDropForKey` 里约 60 行按排索引的设置读取，
  在第二处重新实现会让两者漂移，而漂移的症状正是雨滴渲染出设置页声称已关闭的阴影。
- **两个未加保护的 `Process.Start`/`Directory.CreateDirectory`**：`:561-573` 的「打开配置文件夹」
  与「打开字体文件夹」，而同文件几百行外结构完全相同的 Packages(`:312`) 与 DmNote(`:367`) 按钮都包了
  try。只读 Mod 目录下 `CreateDirectory` 抛 `UnauthorizedAccessException`（正是 `ResolveModPath` 要
  回退的那种情况），`explorer.exe` 起不来时 `Process.Start` 抛 `Win32Exception`——任一者逃出 GUILayout
  回调都会让整个设置窗口失效直到重启，且这些路径不是保存、没有横幅可显示。现一并包上。
- **每键字号按钮每事件分配两个对象**（第 43/45 轮那类缺陷漏掉的双胞胎）：`DrawPerKeyTextSizeBtn`
  内联拼字符串 + `GUILayout.MinWidth(50)`，而该排每事件最多调用 42 次（8+8+8+16+2）→ 每事件约 84
  个对象、60fps 下约 1 万/秒，只要折叠面板开着就一直付。**同族的 `DrawPerKeyColorBtn` 本来就是对的**。
  现按槽缓存标签 + 实例 `GUILayoutOption`（实例是强制的：静态 `GUILayoutOption` 会把 IMGUIModule
  拖进静态构造）。
- **颜色页两个数组字面量每事件重建**：颜色页正是人们一直开着的那一页，`string[12]` + `Color[12]`
  在每个 IMGUI 事件重建。现标签表按语言缓存（与 `DrawTabBar` 同一套）。
- **第 1/2 排每键字号缺边界守卫**：第 1 排无守卫地索引 `keyCodes[i]`、第 2 排只判了 `b < 8` 就索引
  `keyCodes[backSequence[b]]`，而紧随其后的**第 3 排循环反倒有守卫**。今天由 `EnsureSettingsArrays`
  兜住，属纵深防御；但 IMGUI 回调里的越界会让整个设置窗口失效直到重启。另删掉一个从未被显示的
  `sizeLabel`（每事件一次 `ToString` + 拼接）。

### 撤销栈按字节封顶（2026-09-26，第 56 轮）
- **只按条数封顶根本没有约束任何真实成本**：`TimelineCapacity = 64`，而每条都是**整份文档**的
  JSON 快照——112 节点的布局约 0.5 MB，故编辑器开着期间常驻约 **32 MB**。第 44 轮只是让关闭
  编辑器时释放，并没有降低稳态占用；它一直被列为「待处理」，但降条数会直接让用户损失撤销深度。
  现加**字节上限**（16 MB），取条数与字节两个上限中**先咬紧**的那个：小文档因此保有完整撤销
  深度，只有大文档才会被裁。裁剪永不越过当前位置，也永不把时间线清空（否则第一次结构编辑就
  不可撤销——即第 44 轮修过的那个回归）。
- 实现上把 `snapshotBytes` 做成**增量**计数（Push/ReplaceTop/Undo/Redo/Clear 维护），使裁剪为
  O(1) 而不必每次 Push 重新求和。`Undo`/`Redo` 原先写的是 `snapshots[position] = current ??
  snapshots[position]`——`current` 为 null 时是自赋值（无害），但会漏掉记账，故改成显式分支。
- Harness 增至 **138** 项（新测试灌入 12 条 3MB 快照 = 72 MB，要求条数与字节两个上限都满足、
  且至少留一条使撤销仍可用）。该测试对旧代码是**判别性**的：旧代码没有 `snapshotBytes` 字段。

### 孤立翻译键清理 + 固定布局深审（2026-09-26，第 57 轮）
- **12 个键定义后无人引用**（36 条三语文本）：`fm_group_assign/del/select`（已被带插值的
  `fm_gtip_*` 工具提示取代）、`fm_stat_layout`、`fm_stat_hide_label_hint`、`fm_rain_color`、
  `fm_rain_follow_row`、`fm_special_color_hint`、`layout_108k`、`save`、`pkg_err_format`、
  `dmnote_imported`。逐一核实过：**没有任何控件真的缺标签**，全是历次重构的遗留。
- **加了自动化检查**（Harness 增至 **140** 项）：键只要以**任意**字符串字面量出现、或以一个紧邻
  拼接的字面量开头（`"tab_" + TabKeys[i]`）即算被使用。两条规则缺一不可——只匹配
  `I18n.Tr("…")` 会误报约 60 个帮助键（它们是裸传给 `DrawEditorHelpMarker`、由它内部调
  `I18n.Tr`）。改名后旧键残留从此会被**测试**抓到。
- **【换脚键样式会冻结一屏光晕】光晕 Image 不是按键根的子物体**：它挂在 `keyGlowLayer` 下
  （`CustomLayout.cs:952`），而那是 `KeyViewerSizeObject` 的整幅拉伸子物体、被 `ResetKeyViewer`
  的重建清扫**显式排除**。故 `ResetFootKeyViewer` 的两次 `Destroy` 碰不到它：它仍 enabled、
  停在最后的矩形与颜色上、仍被绘制，且仍留在 `fixedGlowImages` 里（顺带把已销毁的 `Key` 组件
  钉在托管内存中）。每换一次脚键样式就在屏上叠一组冻结光晕，而新按键在第一次按压前一直没有光晕
  （`ApplyFixedGlow` 的 `TryGetValue` 落空——新 `Key` 是另一个对象）。只有整层重建才会走到
  `ClearFixedGlowImages` 扫掉。属功能开关门控（`EnableFixedKeyGlow` 默认关）。现销毁前显式收掉
  光晕并从字典移除，重建后立刻 `ApplyFixedKeyGlows()`。
- **`CanvasWidth` 除以 `Screen.height` 无守卫，而默认 X = 0 会把 Inf 变成 NaN**：
  `Screen.height` 在某些窗口状态（最小化/零高度交换链窗口）不保证为正 → `CanvasWidth` = +Inf；
  而 `MainKeyViewerPosition` 默认 `x = 0`，定位算法是乘法，`0 * Inf` = **NaN**，被写进
  `SetRect` → **共享**的合并 `KeyShapeLayer`，一个非有限矩形毁掉屏上**每一个**按键框。
  子代理诚实标注了触发条件不确定（Unity 通常会钳制），但这是这两个文件里唯一没有非有限守卫的
  数值输入，且爆炸半径是整个按键层。现抽出 `ComputeCanvasWidth()` 同时守卫除数与结果。
- **【构建中途抛出会让 Update 每帧 NRE】`EnableKeyViewer` 没有异常屏障**：`KeyViewerObject` 在
  开头就赋值，而 `PressTimes`/`keyPressTimes`/`lastPerKeyKps`/**`Stopwatch`** 要到函数**尾部**
  才创建。窗口内任何抛出都会带着「对象活着但 `Stopwatch` 为 null」逃出 `OnEnable`，于是**下一帧**
  就死在 `Stopwatch.ElapsedMilliseconds` 上——每帧一个 NRE，输入处理、按键计数、KPS、雨滴全部
  失效，只能靠关掉再打开恢复。抛出并非假设：构建要跑反射驱动的字体材质工作、约 40-105 对
  `new GameObject`+`AddComponent`、以及 `GetLayout`。`SwitchProfile` 恰恰把它等价的重建包在
  try/catch + 回滚里。现把构建主体拆成 `BuildOverlay()` 并加屏障，失败即整体拆解。
- `ApplyKeyColors` 此前只判 `pi < 0` 就索引四个 `PerKey*` 数组，而两个同族读取点
  （`ApplyColorToKey`、`PerKeyColorArraysValid`）都判了上界；本处在 `CreateKey` **内部**，
  越界会从 `EnableKeyViewer` 中途逃出、升级成永久损坏的 Update。现加 `PerKeyColorsCoverSlot`，
  不覆盖时回落到全局分支（与 `ApplyColorToKey` 一致）。
- 删掉 `DisableKeyViewer` 里连着调了两次的 `ClearActiveDrops`——开头那次已经干了活，第二次纯属
  冗余，且把注释错误地挂在了自己身上。幂等所以没出问题，但重复的拆解步骤会被后来人读成
  「开头那次是承重的」。
- 子代理另确认**干净**的部分：`Key.cs`（纯数据持有者，无 Update/OnDestroy/终结器，持有资源全在
  `ReleaseCustomTextures` 里释放）、固定布局的 GameObject 生命周期（逐个追了 `new GameObject`
  与父子关系，`ResetKeyViewer` 的清扫全覆盖，无泄漏、无「装好后又被销毁」的顺序 bug）、以及固定
  路径上的索引安全（含 108K 的 105 个槽位为何永远走不到 `Count[40]`）。

### 图片加载的 TOCTOU 与一次性闩锁（2026-09-26，第 58 轮）
接第 57 轮子代理审计 `KvImageLoader` 的剩余两条。
- **16 MB 上限是 TOCTOU 的**：`new FileInfo(path).Length` 判定、再 `File.ReadAllBytes(path)`
  载入——这是**两次独立的打开**。期间增长的文件（或第二次打开时解析到别处的路径）能绕过文档
  承诺的上限，把整份内容塞进托管 `byte[]`。这道上限是「用户可填 / `.jkv` 可携带的路径」与
  无界分配之间**唯一**的屏障，故必须是精确的而非参考性的。现改为单个 `FileStream`，按**实际读到
  的**字节强制上限，并拒绝读取期间改变大小的文件；顺带省掉一个从不释放的 `FileInfo`。
- **反射查找的失败被永久闩锁**：`_loadImageCached` 一旦置位就永不复位，所以一次早期失败（快速
  `OnEnable` 时模块尚不可解析、宿主较晚加载模块、域重载顺序问题）会让 `_cachedLoadImage` 在整个
  进程内为 null，此后**每一张**图片都失败、只有一行日志、无从恢复。
  （第 38 轮已修过「查找**前**置位」那一条，但闩锁本身仍是永久的。）
  现：成功永久缓存；**失败**每 10 秒重试一次，并对抱怨限流，使真的缺失时不会每张图刷一次日志。
- **`ResetKeyViewerPosition` 每次调 `GetLayout` 三次**（两个偏移辅助各一次 + 调用点一次），
  而 `GetLayout` 返回的结构体里带一个刚分配的 `ExtraSlot[]`（最多 14 项）；自定义位置 X/Y 滑杆
  在每个 IMGUI 事件都驱动它（拖动时 60-120 次/秒）。本代码库已为 `KpsTotalIsSlim` 的同一开销加过
  缓存，这三处绕过了它。现把布局解析提到调用点一次，两个辅助改为接受已解析的 `LayoutDesc`。

### 设置窗口剩余的每事件分配 + 一条否证结论（2026-09-26，第 59 轮）
- 清掉第 55 轮审计里列的剩余每事件数组字面量：
  - `DrawKpsTotalColors`（`KeyViewerColorGUI`）的 `string[3]` + `Color[3]`：每事件画两次；
    `DrawFullKeyboardColorSection` 的 `string[6]` + `Color[6]`：每事件六次。i18n 标签**不能**提为
    静态（见第 45 轮：静态初始化器会把 IMGUI 类型拖进类型静态构造），故标签表是每次绘制区块
    填充的**实例**暂存；颜色便宜且无 i18n，仍是局部。
  - `DrawLanguageSection` 的 `string[3]` 与 `DrawFontSection` 的 `string[9]`+`int[9]`×2：全是固定
    字面量表，**不含** IMGUI 类型，提为静态是安全的。
  - `BuildFontStyleSummary` 每事件 `new List<string>(4)` + `string.Join`——它在**每个** IMGUI
    事件都跑，包括用户已滚远、根本看不到的字体行。现改为复用 `StringBuilder`（结果很短，有界）。
- **【否证结论，不是修复】「被索引却未定长的设置数组」这一整类问题都不存在**：脚本枚举了
  `KeyViewerSettings.cs` 里 88 个数组字段，减去 `EnsureSettingsArrays` 覆盖的 49 个后，剩下 39 个
  中只有 2 个在别处被下标访问——`CounterAnimBezier`（每次覆盖层重建都由 `EnsureCustomNodes` 校正
  null/长度/NaN）与 `ProfileNames`（`DrawProfileList` 有 null 判空 + 有界循环）。其余 37 个只有写、
  从不索引。这与第 37 轮「每键颜色面板的索引无守卫」是**同一类**问题，那次修完后该类已闭合。
  （第一版脚本把每个键拿去和**每个文件**比，得出 474 个键全部孤立的假结果；改成全局汇总字面量后
  才正确。第二版又因 `EnsureSettingsArrays` 实际位于 `Core\KeyViewer.cs` 而匹配到空体。两版都靠
  实际输出发现，没有留下假绿。）

### 仍待实机或后续处理
- Unity 游戏内回归：FreeMake 撤销/切换、视频真实编码回退、UMM 首次显示、TGT 回放。
- `.jkv` 仍需完整游戏内端到端导入回归（当前已有离线校验/事务原语测试）。
- 解绑节点是否应保留历史 Count 尚需产品定义；当前不自动删除用户 Count。

## 行为规则
1. **文件为准**：行号/内容对不上时相信工具读取结果，不要判定为"乱码"后反复重读。
2. 会话被压缩或换模型后：先重读本文件定位任务再继续。
3. 用中文回复用户。
