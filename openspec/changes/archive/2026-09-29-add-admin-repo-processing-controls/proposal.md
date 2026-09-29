# Proposal

## Why

管理后台是管理员日常管理仓库的主入口，但仓库处理控制操作（暂停/继续/停止/重新开始）目前只存在于仓库处理页（`/[owner]/[repo]`）。管理员在管理后台详情页发现处理异常、需要干预时没有任何操作入口，必须跳转到处理页；同时详情页对 Paused/Stopped 状态缺乏正确的展示（无本地化文案与状态色调），且处理进度不会自动刷新——管理员看到的页面状态经常滞后于实际进度。

## What Changes

- 管理后台仓库详情页头部操作区新增控制按钮组：**暂停 / 继续 / 停止 / 重新开始**，按仓库状态矩阵显示，与处理页行为一致：
  - Pending / Processing → 暂停、停止、重新开始
  - Paused → 继续、停止、重新开始
  - Stopped / Failed / Completed → 重新开始
- **BREAKING**（管理后台 UI）：移除详情页原有的"重新生成全部"按钮，由状态矩阵中的"重新开始"统一承接单仓重跑；后端端点保留，列表页"批量重新生成"不受影响。
- "重新开始"为破坏性操作（清空已生成内容），执行前弹出确认对话框（与页面现有破坏性操作模式一致）；暂停/继续/停止直接执行。
- 控制操作成功后立即刷新仓库状态、处理日志与按钮矩阵；操作进行中按钮显示 loading；失败（409 冲突/无权限等）给出明确提示并刷新到最新状态。
- 详情页支持 Paused/Stopped 状态：状态徽章色调与本地化文案（8 语言）。
- 详情页在仓库处理活跃（Pending/Processing）时每 10 秒自动轮询刷新状态与进度（与页面现有 Graphify/分支任务轮询模式一致）。

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `repository-processing-controls`: 新增"管理后台详情页控制交互"需求——管理后台仓库详情页必须按状态矩阵提供控制操作、正确展示 Paused/Stopped、并在处理活跃时自动刷新；权限与并发语义复用既有控制端点。

## Impact

- **前端（唯一改动面）**：
  - `web/app/admin/repositories/[id]/page.tsx`：操作区新增控制按钮组、移除"重新生成全部"按钮、活跃状态自动轮询、操作后刷新
  - `web/app/admin/repositories/[id]/_components/utils.ts`：状态归一化与色调映射扩展（paused/stopped）
  - `web/lib/repository-api.ts`：复用现有 `pauseRepository` / `resumeRepository` / `stopRepository` / `restartRepository`（无新增 API）
  - `web/i18n/messages/*/admin.json`（8 语言）：新增状态与操作文案
- **后端**：无改动（复用现有 `/api/v1/repositories/{pause|resume|stop|restart}` 端点；管理员权限在端点侧已支持）
- **管理习惯**：详情页"重新生成全部"入口被"重新开始"取代；批量操作与 API 兼容性不受影响
