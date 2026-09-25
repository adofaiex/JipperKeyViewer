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

### 仍待实机或后续处理
- Unity 游戏内回归：FreeMake 撤销/切换、视频真实编码回退、UMM 首次显示、TGT 回放。
- `.jkv` 仍需完整游戏内端到端导入回归（当前已有离线校验/事务原语测试）。
- 解绑节点是否应保留历史 Count 尚需产品定义；当前不自动删除用户 Count。

## 行为规则
1. **文件为准**：行号/内容对不上时相信工具读取结果，不要判定为"乱码"后反复重读。
2. 会话被压缩或换模型后：先重读本文件定位任务再继续。
3. 用中文回复用户。
