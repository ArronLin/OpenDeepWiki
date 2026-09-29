# Design

## Context

见 `proposal.md` 了解动机。当前状态与约束：

- 控制能力后端已于上一变更交付：`POST /api/v1/repositories/{pause|resume|stop|restart}`（权限=仓库所有者或管理员，带并发状态守卫）。前端封装 `pauseRepository` / `resumeRepository` / `stopRepository` / `restartRepository` 已存在于 `web/lib/repository-api.ts`。
- 管理后台详情页 `web/app/admin/repositories/[id]/page.tsx` 为客户端组件；头部操作区现有：刷新、同步统计、触发增量、生成架构图、重新生成全部。页面已直接混用 v1 客户端（`fetchProcessingLogs` / `fetchRepoTree` / `fetchRepoDoc`）。
- 管理后台仅 Admin 角色可访问（`admin-layout-client.tsx` 对非 Admin 重定向）——按钮无需在前端重复做权限判断，端点侧已校验。
- 状态展示现状：`_components/utils.ts` 的 `normalizeTaskStatus` 不认识 paused/stopped（归为 other）、`TASK_STATUS_TONE` 无对应色调、`getLocalizedTaskStatus` 对未知状态回退原文。
- 页面已有两处 10 秒轮询（Graphify 活跃、分支任务活跃），均调用 `loadBaseData()` + `loadLogs({silent})`。
- i18n：`web/i18n/messages/<lang>/admin.json`，8 个语言目录（de/en/es/fr/ja/ko/pt-BR/zh）。

## Goals / Non-Goals

**Goals:**

- 详情页提供与处理页一致的状态矩阵控制操作（暂停/继续/停止/重新开始），操作反馈与状态刷新闭环。
- 正确处理 Paused/Stopped 的展示（本地化文案 + 徽章色调）。
- 处理活跃（Pending/Processing）时自动刷新状态与进度，且不干扰页面上的其他操作（如文档编辑、全屏 loading 闪烁）。

**Non-Goals:**

- 后端零改动（复用现有 v1 控制端点）。
- 不改列表页批量重新生成、branch 任务级控制、处理页（上一变更已交付）。
- 不引入前端测试框架（项目未配置）。
- 不改"重新开始"与旧"重新生成"的后端语义差异（见 Risks）。

## Decisions

### D1. 复用 v1 控制端点，不新增 admin 端点

按钮直接调用 `repository-api.ts` 中已有的四个函数（传 `repository.orgName/repoName`）。理由：端点已具备权限与并发守卫、admin 详情页已有混用 v1 客户端的先例、纯前端改动面最小。
替代方案：新增 `/api/admin/repositories/{id}/pause` 等端点——被否：行为完全重复，只为形式统一而复制一层授权与状态机。

### D2. 控制按钮位于头部操作区，移除"重新生成全部"

用户已确认：详情页单仓重跑入口由状态矩阵的"重新开始"统一承接，移除旧的"重新生成全部"按钮（及其 `handleRegenerateRepository` 处理函数与 `regenerateRepository` 引入），避免 Failed/Completed 状态下两个功能相似的按钮并存。后端端点与列表页"批量重新生成"保持不变。

### D3. 六状态按钮矩阵（与处理页一致）

| 状态 | 暂停 | 继续 | 停止 | 重新开始 |
|---|---|---|---|---|
| Pending / Processing | 显示 | - | 显示 | 显示 |
| Paused | - | 显示 | 显示 | 显示 |
| Stopped / Failed / Completed | - | - | - | 显示 |

前端在 admin 页面内独立实现该矩阵（与 `repository-processing-status.tsx` 逻辑相同），不强行复用处理页的整页横幅组件——两者页面结构差异大，复用会引入不必要的耦合。

### D4. "重新开始"需确认，其余操作直接执行

"重新开始"清空已生成内容，点击后 `window.confirm`（与页面现有 `confirmRegenerateAll` / `confirmDelete` 模式一致）；暂停/继续/停止为可逆操作，直接执行并以 toast 反馈。操作进行中按钮显示 loading 并禁用。

### D5. 操作后刷新：成功与失败都刷新

操作成功 → toast 成功 + `loadBaseData` + `loadLogs`；操作失败（409 冲突/403/网络）→ toast 错误信息 + 同样刷新到最新状态。对齐处理页"操作完成后页面刷新到最新状态"的既有语义。

### D6. 活跃轮询：silent 模式，且不触碰文档编辑

- 触发条件：`repository` 状态为 Pending 或 Processing（进入 Paused/Stopped/Failed/Completed 后停止轮询）。
- 周期：10 秒（与页面现有轮询一致）；轮询内容：`loadBaseData` + `loadLogs({silent})`——**不调用 `loadDoc` / `loadTree`**，避免覆盖正在编辑的文档草稿（`docDraft` 由 `doc` 变化触发重置）或打断文档浏览。
- `loadBaseData` 增加 `options?: { silent?: boolean }` 参数：silent 时不设置 `pageLoading`（避免每 10 秒整页全屏 loading 闪烁）且错误不弹 toast（与 `loadLogs` 的 silent 语义一致）。**现有分支任务轮询一并改为 silent 调用**，修复其既有的全屏闪烁问题，保持页面内两处轮询行为一致。

### D7. 状态展示扩展（utils + i18n）

- `normalizeTaskStatus`：新增 paused/stopped 识别（含中英文判断）。
- `TASK_STATUS_TONE`：paused → `warning`，stopped → `neutral`；脉冲动效仍仅在 processing 状态启用。
- `getLocalizedTaskStatus`：新增 paused/stopped 本地化分支。
- i18n（8 语言 `admin.json`）：`repositories.paused` / `repositories.stopped` 状态文案；`management.pause` / `resume` / `stop` / `restart` 按钮文案；`management.confirmRestart` 确认文案。

### D8. 按钮渲染与权限

管理后台仅 Admin 可访问，按钮按矩阵直接渲染（不做 canControl 判断）；若端点返回 403（例如令牌失效等边界情况），按 D5 统一提示并刷新。

## Risks / Trade-offs

- [restart 与旧 regenerate 的行为差异] 旧 regenerate 在 branch 生成任务活跃时通过仓库生成锁拒绝请求；restart（上变更交付的控制端点）不做该锁检查。→ 沿用既有端点行为，本变更不改后端；并发冲突由端点的状态守卫与 worker 协调机制兜底。管理后台入口与处理页入口行为一致。
- [轮询期间与其他轮询叠加] 页面可能同时存在 Graphify 轮询与处理活跃轮询，两个 10 秒定时器并发请求。→ 请求均为只读接口，频率低（10s）；沿用现有模式，不引入额外协调机制。
- [i18n 遗漏] 8 语言目录需同步新增键，漏改语言回退英文。→ 按上一变更流程对 8 个目录同步补齐并在验收中检查。
- [移除按钮冲击习惯] 常在详情页用"重新生成全部"的管理员会发现按钮消失。→ 由"重新开始"替代（行为一致：清空重跑），文案与 toast 需明确"已重新开始"；proposal 已标记该 UI 变更。

## Migration Plan

- 无数据/接口迁移；纯前端变更。
- 部署：重建 web 镜像并重启 web 容器（backend 无需重建）。
- 回滚：还原前端代码并重建 web 镜像。

## Open Questions

（无——影响规格与实现的决策均已确认。）
