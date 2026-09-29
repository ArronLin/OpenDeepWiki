# Tasks

## 1. 状态展示与 i18n 基础

- [x] 1.1 扩展 `web/app/admin/repositories/[id]/_components/utils.ts`：`normalizeTaskStatus` 识别 paused/stopped（中英文），`TASK_STATUS_TONE` 增加 paused→warning、stopped→neutral；验证：`cd web && npm run lint` 通过
- [x] 1.2 `web/app/admin/repositories/[id]/page.tsx` 的 `getLocalizedTaskStatus` 增加 paused/stopped 本地化分支（脉冲动效仍仅 processing 启用）；验证：TypeScript 构建通过，页面处于 Paused 时状态徽章显示中文"已暂停"
- [x] 1.3 8 语言 `admin.json`（de/en/es/fr/ja/ko/pt-BR/zh）新增：`repositories.paused`、`repositories.stopped`、`management.pause`、`management.resume`、`management.stop`、`management.restart`、`management.confirmRestart`；验证：node 解析 8 个 JSON 无语法错误且键在 8 个目录中齐备

## 2. 控制按钮与操作闭环

- [x] 2.1 详情页头部操作区新增控制按钮组：按六状态矩阵渲染（Pending/Processing→暂停/停止/重新开始；Paused→继续/停止/重新开始；Stopped/Failed/Completed→重新开始），操作进行中按钮 loading 并禁用，"重新开始"执行前 `window.confirm`，暂停/继续/停止直接执行；同时移除"重新生成全部"按钮、`handleRegenerateRepository` 及相关 import；验证：`cd web && npm run build` 通过
- [x] 2.2 操作处理后立即刷新（成功与失败均调用 `loadBaseData` + `loadLogs`），成功/失败分别以 toast 反馈（409 冲突等错误信息透传）；验证：本地手测——处理中仓库点"暂停"后按钮矩阵切换为继续/停止/重新开始、状态徽章变为"已暂停"；对已暂停仓库点"暂停"（构造冲突）toast 提示且状态不变
- [x] 2.3 清理无引用代码与文案：grep 确认 `regenerateRepository`（单仓版）、`confirmRegenerateAll` 在 web/ 下无其他引用后，清理对应 i18n 键（8 语言）；验证：`grep -rn "confirmRegenerateAll" web/` 无结果且 `npm run lint` 通过

## 3. 活跃轮询

- [x] 3.1 `loadBaseData` 增加 `options?: { silent?: boolean }`：silent 时不设置 `pageLoading`（避免全屏 loading 闪烁）、错误不弹 toast；现有分支任务轮询调用点同步改为 `loadBaseData({ silent: true })`；验证：手动刷新仍显示全屏 loading，分支任务活跃时页面无周期闪烁
- [x] 3.2 新增处理活跃轮询：`repository` 状态为 Pending/Processing 时每 10 秒调用 `loadBaseData({silent:true})` + `loadLogs({silent:true})`，进入 Paused/Stopped/Failed/Completed 后自动停止；轮询不得触碰 `loadDoc`/`loadTree`（保护编辑中的文档草稿）；验证：手测——处理中打开详情页，进度自动更新且无全屏闪烁；在文档编辑框输入时轮询不重置内容

## 4. 集成验收（真实环境）

- [x] 4.1 重建 web 镜像并重启 web 容器，后端镜像无需重建；验证：`docker compose build web && docker compose up -d web` 后页面可访问
- [x] 4.2 端到端手测（对照 spec "管理后台详情页控制交互" 全部场景）：触发一个仓库重新开始 → 详情页显示暂停/停止/重新开始 → 暂停后矩阵切换、状态显示"已暂停"、处理在检查点停止 → 继续后恢复生成 → 停止后显示"已停止" → 重新开始（含确认对话框）清空重排；同时回归：处理页控制按钮正常、列表页"批量重新生成"可用
