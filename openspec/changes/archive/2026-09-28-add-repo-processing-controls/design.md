# Design: 仓库处理控制(暂停 / 停止 / 重新开始)

## Context

动机与范围见 `proposal.md`;行为契约见 `specs/repository-processing-controls/spec.md`。以下是塑造技术方案的关键现状与约束:

- 仓库处理由 `RepositoryProcessingWorker`(30s 轮询)→ `IWikiGenerationCoordinator`(集群槽位 + 仓库锁,180s 租约 / 30s 心跳)→ `RepositoryBranchProcessor`(分支 → 语言循环)→ `WikiGenerator`(目录 + 逐文档并行生成)执行。链路中所有方法已贯穿传递 `CancellationToken`。
- 现有取消信号只有应用停机的 `stoppingToken`;停机取消会把仓库状态**写回 Pending**(下次启动自动续跑)。用户控制取消必须与停机取消区分开。
- `RepositoryStatus` 只有 `Pending/Processing/Completed/Failed`,无暂停/停止语义。
- 已生成文档持久化在 `DocCatalogs`/`DocFiles`;`GenerateDocumentsAsync` 已有 `GetPersistedDocumentPathsAsync` 跳过已持久化文档——这是断点恢复的现成基础。
- 目录生成(`GenerateCatalogAsync`)没有"已存在即跳过"逻辑,每次都会重新生成一次目录。
- 现有「重新生成」(用户端 `POST /api/v1/repositories/regenerate`)要求 Failed/Completed 状态,权限为仓库所有者或管理员,清空复用 `IRepositoryFullRegenerationCleaner`。
- 集群多实例部署:任何控制机制不能依赖单实例内存状态。
- 状态存储在 `Repositories` 表,枚举以 int 持久化(追加值无需 schema 迁移);无乐观并发列,但项目已有 `EfContextCapabilities.SupportsExecuteUpdate` 模式用于原子条件更新。

## Goals / Non-Goals

**Goals:**

- 用最小改动为仓库级处理链引入可及时生效的暂停/继续/停止/重新开始。
- 保证多实例下状态一致:控制请求跨实例可见,执行停止后资源(槽位/锁)确定释放。
- 断点恢复正确性:继续处理不重做已持久化文档。
- 与停机取消语义、现有「重新生成」端点平滑共存。

**Non-Goals:**

- 不深入改造 `WikiGenerator` 内部算法(不引入文档级检查点或恢复指针)。
- 不改动分支任务(`BranchGenerationWorker`)、增量更新、翻译、思维导图等后台链路。
- 不新增数据库列或迁移(方案避免新字段)。
- 不实现处理中实时双向通信(轮询间隔内生效即可)。

## Decisions

### D1. 状态机:在 `RepositoryStatus` 追加 `Paused = 4`、`Stopped = 5`

直接扩展现有枚举而非引入第二维度字段。追加值不影响现有 19 个引用点的状态比较(它们判断具体状态而非全集)。前端 `RepositoryStatus` 联合类型与 API 状态名同步扩展。

操作 × 源状态转换矩阵(全部经条件更新原子执行,0 行 → 409):

```
操作        | 允许的源状态                    | 结果状态
------------+--------------------------------+----------------------
暂停        | Pending, Processing            | Paused
继续        | Paused                         | Pending
停止        | Pending, Processing, Paused    | Stopped
重新开始    | 任意                           | Pending(先清空内容)
worker 完成 | Processing                     | Completed
worker 失败 | Processing                     | Failed
停机取消    | Processing                     | Pending(现有行为)
```

`Paused`/`Stopped` 不在 worker 调度候选(`Pending`/`Processing`)内,无需额外过滤。

### D2. 控制信号:数据库状态即信号 + 每任务控制监听循环

用户操作在 API 侧对 `Repositories.Status` 做**条件更新**;正在执行的任务由 worker 内一个轻量监听循环感知(每 ~2s 查询该仓库状态,一旦不再是 `Processing` 即触发取消)。

理由:

- **跨实例正确**:状态在共享数据库,任何实例的 worker 都能感知,无需消息总线或内存注册表。
- **零内存态依赖**:暂停/停止不需要保存执行进度指针——恢复完全基于数据库中已持久化的目录与文档(见 D3)。
- **复用现有取消管道**:链路已完整传递 `CancellationToken`(并行文档生成、LLM 流式读取均响应),无需在 `WikiGenerator` 深层散布检查点。

备选方案:

- *深层检查点直接查库*:需改动 `WikiGenerator`/`RepositoryBranchProcessor` 多层签名,侵入大,放弃。
- *实例内内存 CTS 注册表*:多实例下 API 请求可能落在非执行实例,不可靠,放弃。

**关键不变式**:worker 对该仓库 `Status` 的**所有写入必须条件化**:

- 开始处理时:`WHERE Id = @id AND Status IN (Pending, Processing)`(防止把已被用户控制的仓库"复活"为 Processing)。
- 写入最终态(Completed/Failed/停机回 Pending):`WHERE Id = @id AND Status = Processing`;受影响 0 行表示状态已被用户控制,放弃写入(用户状态优先,内容已完成也不覆盖)。

### D3. 暂停/继续:取消 + 基于既有持久化状态的断点恢复

- **暂停/停止**:任务被取消后走"用户控制路径"——**不写仓库状态**(API 已写入目标状态)、仅释放租约与锁;由既有 `finally { coordinator.ReleaseAsync(...) }` 完成,天然满足槽位释放要求。
- **继续**:`Paused → Pending`,worker 按现有逻辑重拾。断点恢复依据:
  - 文档:`GenerateDocumentsAsync` 跳过已持久化文档(现成能力)。
  - 目录:重新生成一次(接受单次 LLM 成本,换取目录完整性;若暂停点发生在目录生成中途,重生成可修复残缺目录)。
  - 工作区:重新 `PrepareWorkspaceAsync`,不依赖内存态。
- 取消来源判定:每个任务新建 `jobControlCts = CreateLinkedTokenSource(stoppingToken)`,监听循环通过它取消;捕获 `OperationCanceledException` 时区分:
  - `stoppingToken` 已取消 → 停机路径(条件化写回 Pending,保持现状语义)。
  - 仅 `jobControlCts` 取消 → 用户控制路径(不写状态,仅释放资源)。

### D4. 重新开始的执行顺序(含 Processing 竞态)

- 非 Processing(Pending/Paused/Stopped/Failed/Completed):先复用 `IRepositoryFullRegenerationCleaner` 清空内容,随后条件更新为 `Pending`。清空失败则状态保持原样,操作报错。
- Processing(执行中):按顺序执行,避免旧任务在清空后残留写入:
  1. 条件更新 `Processing → Stopped`(取得控制权,触发 D2 取消);
  2. 轮询等待该仓库的生成锁/槽位释放(间隔 1s,上限 ~10s);
  3. 租约释放后清空内容;
  4. 条件更新 `Stopped → Pending`;worker 拾取并全量生成。
- 超时兜底:若第 2 步超时,返回"停止已受理,请稍后重试重新开始";仓库停在 `Stopped`,用户重试时走非 Processing 路径(清空 + Pending),结果一致。

备选方案:*新增 `RestartRequested` 字段由 worker 侧清空* —— 需新字段、新调度分支与标志生命周期管理;*不等待直接清空* —— 旧任务残留写入竞态。均放弃。

### D5. API 与权限

- 新增用户端端点(与现有 `regenerate` 并列,请求体复用 `owner`/`repo` 结构):

```
POST /api/v1/repositories/pause
POST /api/v1/repositories/resume
POST /api/v1/repositories/stop
POST /api/v1/repositories/restart
```

- 授权沿用「重新生成」模型:未登录 401;非所有者且非管理员 403;仓库不存在 404;非法状态转移或条件更新 0 行 409(附明确错误信息)。响应结构与现有 `RegenerateResponse` 风格一致。
- 条件更新优先走 `ExecuteUpdate`;`EfContextCapabilities` 不支持时回退事务内查询+更新(遵循项目现有模式)。
- **前端权限数据**:`RepositoryTreeResponse` 增补 `canControl`(服务端按当前请求用户是否为所有者/管理员计算;匿名一律 false),处理页据此显示/隐藏操作按钮,避免前端自行推断。

### D6. 前端交互

- `types/repository.ts`:`RepositoryStatus` 联合类型追加 `"Paused" | "Stopped"`;`RepositoryTreeResponse` 追加 `canControl`。
- `repository-api.ts`:新增 `pauseRepository` / `resumeRepository` / `stopRepository` / `restartRepository` 四个调用。
- `repository-processing-status.tsx`:
  - 状态呈现:`statusConfig` 增加 Paused(琥珀/暂停图标)与 Stopped(中性/停止图标)配置;文案走 i18n。
  - 操作按钮按矩阵展示(仅 `canControl` 为真时):

```
仓库状态    | 暂停 | 继续 | 停止 | 重新开始
------------+------+------+------+---------
Pending     | 显示 | -    | 显示 | 显示
Processing  | 显示 | -    | 显示 | 显示
Paused      | -    | 显示 | 显示 | 显示
Stopped     | -    | -    | -    | 显示
Failed      | -    | -    | -    | 显示(替代现有 Retry)
Completed   | -    | -    | -    | 显示
```

  - 交互:操作进行中按钮 loading;成功即触发状态刷新并恢复轮询(「继续」后自动回到轮询);409/403 以现有 toast(sonner)提示并刷新状态;Paused/Stopped 状态停止"完成后自动跳转"逻辑。
- `layout.tsx`:处理页渲染条件加入 `"Paused"` / `"Stopped"`。
- i18n:`home.json` 新增 `status.paused`、`status.stopped`、`actions.*`、`pausedTip`、`stoppedTip` 等键,8 个语言目录同步(至少 zh/en 完整;现有组件自带 fallback 兜底)。

## Risks / Trade-offs

- [取消生效延迟(监听间隔 ~2s + 取消传播)] → 间隔固定 2s 量级;LLM 流式调用即时响应取消,文档级并行任务在下一个取消点退出。满足"短暂延迟内停止推进"。
- [restart 对 Processing 的等待窗口] → 等待租约释放上限 ~10s(通常 ≤3s);超时走"稍后重试"兜底,状态停在 Stopped 语义安全、可重入。
- [旧任务残留写入] → 通过"条件化写入 + restart 等待租约释放后再清空"双保险;写入 0 行即放弃,不覆盖用户控制的终态。
- [暂停时某文档写了一半] → 文档写入为文档级操作,已持久化才被跳过;半成品文档在恢复时重新生成(现有 `HasPersistedDocumentContentAsync` 以非空内容 + 时间戳判定)。
- [继续时目录重生成的成本] → 单次目录 LLM 调用;如后续成本敏感,可再优化为"目录已存在则跳过"(延后,不影响本设计)。
- [8 语言文案维护成本] → 组件已有英文 fallback;要求 zh/en 完整,其余语言随发版补齐。
- [DB 查询开销] → 每个进行中任务每 2s 一次单列查询(集群内进行中任务数 ≤ MaxConcurrentGenerations),可忽略。

## Migration Plan

- **无 schema 迁移**:枚举追加 int 值;无新列;历史数据不受影响。
- **部署顺序**:前后端同仓发布即可;若必须分步,后端先行(新增状态名对旧前端表现为未知状态,建议避免长时间窗口)。
- **回滚注意**:回滚到旧版本前,先将所有 `Paused`/`Stopped` 仓库恢复为 `Pending` 或 `Completed`,避免旧前端/旧逻辑遇到未知状态值时行为异常。

## Open Questions

- 管理后台仓库列表页是否也要加入单仓控制按钮(当前范围仅处理页;后台已有批量重新生成)。
- 暂停/继续/停止操作是否需要通知或审计记录(现有处理日志是否足够)。
