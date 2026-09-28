# Tasks

## 1. 后端:状态机与控制服务

- [x] 1.1 在 `src/OpenDeepWiki.Entities/Repositories/Repository.cs` 为 `RepositoryStatus` 追加 `Paused = 4`、`Stopped = 5`,验证 `dotnet build OpenDeepWiki.sln` 通过且现有测试不受影响
- [x] 1.2 新增仓库处理控制服务(`IRepositoryProcessingControlService` + 实现,置于 `src/OpenDeepWiki/Services/Repositories/`),实现 Pause / Resume / Stop:条件更新状态(优先 `ExecuteUpdate`,回退事务内查询+更新,遵循 `EfContextCapabilities` 现有模式),非法源状态或影响 0 行时返回可区分的失败结果;新增 `tests/OpenDeepWiki.Tests/Services/Repositories/RepositoryProcessingControlServiceTests.cs`(沿用该目录 SQLite 测试基建)覆盖全部合法/非法状态转移,验证 `dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj` 全绿
- [x] 1.3 实现同一服务的 Restart:非 Processing 状态先经 `IRepositoryFullRegenerationCleaner` 清空再置 Pending(清空失败保持原状态);Processing 状态依次执行「条件更新为 Stopped → 轮询等待租约/锁释放(1s 间隔、上限 ~10s)→ 清空 → 置 Pending」,等待超时返回"稍后重试"结果;在同组测试文件覆盖三条路径与超时分支,验证 `dotnet test` 全绿

## 2. 后端:worker 控制感知与状态守卫

- [x] 2.1 在 `src/OpenDeepWiki/Services/Repositories/` 新增控制监听组件(每 ~2s 查询仓库状态,一旦不再是 `Processing` 即触发回调/取消,容忍查询异常),新增对应单测(触发时机、取消后退出、异常容忍),验证 `dotnet test` 全绿
- [x] 2.2 改造 `RepositoryProcessingWorker`:每个任务建立 `jobControlCts = CreateLinkedTokenSource(stoppingToken)` 并启动监听;把 job token 传入执行链;捕获 `OperationCanceledException` 时区分停机路径(条件化写回 Pending,保持现状)与用户控制路径(不写状态、仅释放租约);将开始处理与最终态(Completed/Failed/停机 Pending)的所有 `Status` 写入改为条件更新,影响 0 行时放弃写入;新增/扩展单测覆盖"0 行放弃"与两条取消路径的区分,验证 `dotnet test` 全绿

## 3. 后端:API 端点与 canControl

- [x] 3.1 在 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs` 新增四个端点 `POST /api/v1/repositories/pause|resume|stop|restart`(复用 `owner`/`repo` 请求体与 `RegenerateResponse` 风格响应),叠加权限(未登录 401、非所有者且非管理员 403、不存在 404、非法转移/竞态 409),新增端点级测试(权限矩阵与错误码),验证 `dotnet test` 全绿
- [x] 3.2 为 `RepositoryTreeResponse`(src/OpenDeepWiki/Models/RepositoryTreeResponse.cs)增补 `canControl` 字段并在树接口按当前请求用户是否为仓库所有者/管理员计算(匿名一律 false),验证单测覆盖三种用户视角(匿名 / 非所有者 / 所有者或管理员)
- [x] 3.3 在 `docs/content/docs/api-reference/repositories.mdx` 按现有章节格式补充四个端点的文档(用途、请求体、错误码),验证通过 docs 目录的构建命令(见 `docs/package.json` scripts)渲染正常

## 4. 前端:类型与 API 客户端

- [x] 4.1 更新 `web/types/repository.ts`(`RepositoryStatus` 追加 `"Paused" | "Stopped"`;`RepositoryTreeResponse` 追加 `canControl`)与 `web/lib/repository-api.ts`(新增 `pauseRepository` / `resumeRepository` / `stopRepository` / `restartRepository`),验证 `cd web && npm run lint` 通过

## 5. 前端:处理页交互与 i18n

- [x] 5.1 改造 `web/components/repo/repository-processing-status.tsx`:增加 Paused / Stopped 状态呈现;按状态矩阵在 `canControl` 为真时展示操作按钮(暂停/继续/停止/重新开始,含 loading 与 toast 反馈、409/403 提示后刷新、继续后恢复轮询、Paused/Stopped 停止自动跳转);并更新 `web/app/[owner]/[repo]/layout.tsx` 使 Paused / Stopped 渲染处理页,验证 `cd web && npm run dev` 下按矩阵逐状态手测(含无权用户不显示按钮)
- [x] 5.2 在 `web/i18n/messages/*/home.json` 的 8 个语言目录新增 paused/stopped 状态与四个操作及提示文案(zh/en 完整精确,其余语言保持风格一致),验证切换 zh/en 语言界面文案正确、缺失键由现有 fallback 机制兜底

## 6. 集成验证

- [x] 6.1 端到端手测(本地 `dotnet run` + `npm run dev`):Processing 中暂停→继续(确认已持久化文档不重做、槽位被其他仓库获得)、停止→重新开始、Processing 时直接重新开始(旧任务终止且无残留写入)、无权用户操作收到 403,任一步失败即回到对应任务组修复
- [x] 6.2 全量回归:`dotnet build OpenDeepWiki.sln`、`dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj`、`cd web && npm run build && npm run lint` 全部通过
  - 回归结论(2026-09-28):build 0 警告 0 错误;test 669 通过 / 2 失败,均为存量问题(`RepositorySkillMarkdownBuilderTests.BuildSkillMarkdown_ShouldCreateEnglishDescriptorAndDocumentIndex`、`RepositoryAnalyzerSourceTests.BuildGitCliUploadPackArgument_IncludesSafeDirectoriesBeforeUploadPack`,与本次改动无关);web build 成功;`npm run lint` 存在 133 个文件共 80 错 66 警的存量债务,本次改动涉及的所有文件(repository-processing-status.tsx、layout.tsx、repository-api.ts、types/repository.ts、8 个 home.json 等)零新增发现
