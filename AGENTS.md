# JipperKeyViewer 项目上下文（代理每次请求都会读到，压缩不会丢失）

## 事实锚点（2026-09-18 核实）
- `CHANGELOG.md` 共 274 行；顶部条目 `## 1.7.2`（FreeMake custom-layout editor / 文字描边阴影 /
  视频键节点 / .jkv 分享包等）；最旧条目 1.6.1。以实际 read 结果为准。

## 当前任务（由用户维护，完成后更新）
- 已完成：FreeMake 编辑器撤销历史修复（2026-09-24 实机验证通过）。
- 下一步候选：切换 Profile 的编辑器瞬态隔离（含 customGhostStates）、迁移事务窗口、
  RenameProfile 失败路径、Harness.csproj 的 HintPath 修正。

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
- 遗留：切换隔离只做了编辑器历史/选区，`customGhostStates`（`Core/CustomLayout.cs`）仍未清。

### 已知未修（外部只读审计，均未实机复现）
- `Harness/Harness.csproj:11` HintPath 指向 `..\JipperKeyViewer\bin\Release\JipperKeyViewer.dll`，
  而 Release 产物在仓库根 `bin\` → `dotnet build Harness` 直接 CS0246 失败（Harness 已被
  .gitignore 忽略，不影响发布）。
- 普通 `SwitchProfile` 不隔离编辑器瞬态：`customGhostStates` 不在 `ClearKpsTimers`/
  `InitializeCustomLayout` 清空，同 Id 继承旧鬼雨按下态。
- 迁移无跨文件事务：`MigrateV5toV6`/`MigrateV3toV4` 先写 profile 后写 meta，中途崩溃会重复变换；
  批量失败只 catch+Warning，内存版本已升级，失败文件不再自动重试。
- `RenameProfile` 捕获 `File.Move` 异常后仍无条件更新 `ProfileNames`/`CurrentProfile` 并保存。
- Custom 节点第 3 排雨滴无 UI：`HasThirdRow` 只在固定 Key20/Key24 为真，`KeyViewerRainGUI`
  隐藏第 3 排全部设置，但编辑器允许 `RainRow=3`。
- `FmNode.CustomText` 对 stat 节点是死字段：运行时 `SetKpsTotalDisplay` 只读全局 `KpsLabel`/
  `TotalLabel`，Custom 页也无标签编辑入口。
- `CountInTotal` 语义不一致：门控 `PressTimes` 但分组 KPS 无论 flag 都入队；关 flag/清绑后旧
  贡献残留。

## 行为规则
1. **文件为准**：行号/内容对不上时相信工具读取结果，不要判定为"乱码"后反复重读。
2. 会话被压缩或换模型后：先重读本文件定位任务再继续。
3. 用中文回复用户。
