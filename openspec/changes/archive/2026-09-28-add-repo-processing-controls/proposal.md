# Proposal: 仓库处理控制(暂停 / 停止 / 重新开始)

## Why

仓库 wiki 生成是一条长耗时、大量消耗 LLM 调用的后台流程,但处理一经启动便完全无法干预:

- 用户没有暂停/停止的手段,只能等它跑完或失败;想中断只能重启服务(重启后任务还会自动重跑)。
- 现有「重新生成」要求仓库处于 Failed/Completed 状态,处理中调用直接 409 拒绝——最需要推倒重来的时刻反而做不到。
- 处理会占用集群级生成槽位(`WikiGenerationSlot`),一个「跑飞的」仓库会一直占位,阻塞其他仓库排队。

给仓库所有者与管理员提供对处理的直接控制,把这套系统从「提交后听天由命」变成「可控、可干预」,是本次变更的目标。

## What Changes

- **状态机扩展**:仓库处理状态新增 `Paused`(已暂停)与 `Stopped`(已停止),与现有 `Pending` / `Processing` / `Completed` / `Failed` 并列。
- **新增四个用户操作**,仅作用于仓库级 wiki 生成(不改变分支生成任务等其他后台流程的现有行为):
  - **暂停**:处理中的仓库在下一个检查点停止推进,保留已生成内容与进度信息,并释放集群生成槽位。
  - **继续**:从暂停处断点恢复,已完成的部分不重做。
  - **停止**:终止处理并保留已生成内容,状态置为 `Stopped`,之后可重新开始。
  - **重新开始**:清空已生成内容从头重跑;在 Processing / Paused / Stopped / Failed / Completed 状态下均可用(处理中调用时先终止当前执行再入队)。
- **处理页交互**:仓库进度页按当前状态与用户权限显示对应操作按钮,并轮询感知新状态。
- **新增用户端 API**:pause / resume / stop / restart 四组端点,权限沿用现有「重新生成」模型(仓库所有者或管理员)。
- **协作式检查点**:处理 worker 在执行途中(分支 / 语言 / 文档等边界)检查暂停与停止请求,保证操作及时生效,且不破坏已完成的工作。

## Capabilities

### New Capabilities

- `repository-processing-controls`: 仓库级 wiki 生成处理的用户控制能力——暂停/继续/停止/重新开始的状态语义、权限规则、集群槽位释放、内容保留策略与处理页呈现。

### Modified Capabilities

无。项目尚无已沉淀的 capability(`openspec/specs/` 为空),本次全部为新引入。

## Impact

- **后端**
  - `RepositoryStatus` 枚举(追加 `Paused` / `Stopped`)与 `Repository` 实体(如需控制字段则含一次 EF 迁移,详见 design)。
  - `RepositoryProcessingWorker`:检查点、断点恢复、与租约/心跳的交互。
  - `IWikiGenerationCoordinator` / `WikiGenerationHeartbeat`:暂停/停止时的槽位释放。
  - 用户端仓库服务(`RepositoryService` 等):新增四个控制端点及错误码。
- **前端**
  - `repository-processing-status.tsx`(操作按钮、新状态呈现)、`repository-api.ts`、`types/repository.ts`、i18n 文案。
- **兼容性**
  - 状态枚举为纯追加,无破坏性 schema 变更;API 返回的新状态名为增量变化(前后端同仓部署)。

## 明确不做(Non-goals)

- 不改动分支生成任务(`BranchGenerationTask`)仅能取消排队中任务的现状。
- 不覆盖增量更新、翻译、思维导图、Graphify 等独立后台任务。
- 不引入「定时暂停」或管理员级批量暂停等运维编排能力。
