# ConsDTRv1

`ConsDTRv1` 的 C# / TorchSharp 研究实现，对齐 [Draft-2 规格](docs/ConsDTRv1.MD)。它处理结构化对象与精确任务语义，通过硬拓扑运输操作数，再由独立验证器检查证书。

```text
ConsDTR.LAB.Host → ConsDTR.Lib → ConsDTR.Runtime
                 TorchSharp     .NET 确定性内核
```

- **Runtime**：强类型任务输入、代际槽位、局部原子编辑、版本化读写凭据、精确操作、证据 DAG、完整权威 journal、WAL 恢复和独立证书复验。无需 TorchSharp 或神经权重即可复验。
- **Lib**：共享低秩关系消息、真实边表 gather/scatter、有界循环更新、离散路由/操作/停止策略、冻结推理、CPU/CUDA 设备选择、生产控制器行为克隆、自主硬轨迹 REINFORCE、优化器与检查点恢复。
- **LAB.Host**：可直接修改的 [示例](ConsDTR.LAB.Host/Examples/DtrExample.cs)、真实硬路由教师训练、实验入口与协议/数值验证集。

## 运行

要求 Windows x64、.NET 10。Lib 保留 `TorchSharp-cuda-windows 0.107.0`，CPU 与 CUDA 使用同一模型。显式 `cuda:N` 不可用时会报错；只有 `auto` 允许自动选择。

在仓库根目录执行：

```powershell
$env:NUGET_HTTP_CACHE_PATH = "$PWD\ConsDTR.Lib\obj\nuget-http-cache"
dotnet restore ConsDTR.sln --disable-parallel
dotnet build ConsDTR.sln -m:1 --no-restore
dotnet run --project ConsDTR.LAB.Host --no-build -- --demo --device cpu
dotnet run --project ConsDTR.LAB.Host --no-build -- --demo --device cuda:0
dotnet run --project ConsDTR.LAB.Host --no-build -- --self-test --device cpu
dotnet run --project ConsDTR.LAB.Host --no-build -- --self-test --device cuda:0
```

示例默认把证书、单独返回的可信锚及摘要写到 `run/`。Rider 的工作目录设为 `run/` 时，传 `--output .` 可直接使用该目录。GPU 成功必须以实际运行结果为准。

## 任务与结果

| 任务 | 可认证输出 | 精确操作 |
|---|---|---|
| GroundHorn/v1 | 可推导；闭合集证明 NOT_DERIVABLE | 输入规则 token + 当前已运输的事实 token |
| Reachability/v1 | 有效 walk/simple path；闭合割集证明不可达 | START_PATH / EXTEND_PATH；逐输入边检查割集 |
| FiniteCSP/v1 | 给定有限域赋值 SATISFIES | 逐约束检查同一不可变赋值 revision |

负证书与 CSP 全量检查采用 PROPOSE → BEGIN → NEXT → FINALIZE。FINALIZE 要求完整、去重的输入覆盖。身份/域元数据与已验证事实使用不同类型。路径有效不宣称最短；CSP 未找到证书返回 UNKNOWN，v1 不签发 UNSAT。

集合、割集及赋值可以通过 `EXTEND_CLOSED_SET`、`EXTEND_CUT`、`EXTEND_ASSIGNMENT` 跨候选页累积。每次 EXTEND 必须读取当前运输的旧候选，并提交绑定下一 revision 的 typed delta；输出仍是未验证 Candidate。赋值修改会使旧检查失效，完整认证仍从新的 BEGIN 开始。

`DtrInferenceRunner.Run(task, options)` 使用 LearnedOnly：模型选择候选意图、局部编辑、具体动作和停止请求，禁止在线优化器。每个 STOP 仅请求独立验证；参数未训练时可能几乎全部 UNKNOWN。

`DtrTaskPlanExecutor.RunHornFallback(problem, options)` 明确使用 FairFallback。公平闭包调度在独立模块构造可计费计划，每步仍实际执行清边、运输、内核提交与最终复验。整段连续公平工作不与神经编辑交错，保护中的操作不会被模型打断。

`TaskTeacherPlans.PathWitness`、`CutWitness`、`AssignmentWitness` 只接受调用者提供的候选证据，并以 OracleDiagnostic 执行；不会把诊断成功计为 LearnedOnly 的模型能力。

## 保障机制

- 失败拓扑批次完全回滚；REMOVE、RETYPE、RESET 与 ACTIVATE 有独立身份和版本。reset 必须先显式删完关联边，后续神经状态按新 generation 清零。
- 精确操作仅使用当前 inbox 的 ReadReceipt，并通过当前 WRITE_RESULT 出边写入空寄存器。历史 proofId、删除的边、跨会话或陈旧版本都不授权新操作。
- 容量、精确长度、64 位整数、度数、编辑支持集合、探测数量、目录、日志、导出及最终复验都有有限预算。NO_OP 和拒绝仍扣步；非有限神经状态先失败，再决定输出。
- 目录分开结构直达、已有邻居/pin 和公平配额。epoch 冻结 membership，更新产生 tombstone，新增身份进入下一 epoch；不能用参数相关 top-k 缩小支持。
- 正式输出要求 **SemanticVerified + RouteReplayVerified + ExecutionBound**。独立验证器从输入和完整 genesis-to-seal 日志重建，不信任在线 `Accepted` 标记。
- `ExpectedEndAnchor` 必须来自可信运行时或可信存储。证书自带 head、公钥或签名不能自行建立可信完整性；调用者应独立保存和取得锚。
- `SealedExporter` 只导出已通过验证的完整封口前缀。检查点不能替代正式完整 journal。

实现保证依赖输入映射、确定性内核、独立验证实现及运行时/锚的信任条件。测试不等于机器形式化证明，也不证明 GPU 物理执行、无隐藏硬件故障或普适推理能力。

权重检查点显式保存策略编码版本 `ConsDTRv1-HardPolicy-Draft-2/v1`，绑定操作索引与编辑字段合同。缺少版本或内核/输入 schema 不匹配会拒绝加载；早期试验输出需重新生成，不能把同形状权重视为同策略。

## 训练与实验

```powershell
# 局部张量级硬消息与分类策略课程
dotnet run --project ConsDTR.LAB.Host --no-build -- --train-local --updates 20 --device cpu

# 真实 Runtime 硬路由、操作和完整证书教师课程；保存权重、Adam、配方与 RNG
dotnet run --project ConsDTR.LAB.Host --no-build -- --train-hard --updates 10 --device cuda:0 --output run\training

# 自主采样同一生产硬循环，没有教师/回退；保存完整 rollout 与优化器检查点
dotnet run --project ConsDTR.LAB.Host --no-build -- --train-onpolicy --updates 4 --device cuda:0 --output run\autonomous

# 使用同一检查点，分别采样 Learned / Frozen / RandomLegal 编辑器与 1x/2x/4x 链任务
dotnet run --project ConsDTR.LAB.Host --no-build -- --evaluate --cases 10 --device cuda:0 `
  --checkpoint run\training\hard-training-checkpoint
```

`DtrTrainer.TrainOnPolicy(task, options, random, observer)` 直接采样生产的目录 → 编辑 → 运输 → 提交循环，保留全部条件 log-prob、结构支持、候选顺序、随机状态、接受/拒绝、实际账本成本及唯一终止步。`DtrHardRolloutRunner` 可单独收集；`DtrHardTeacherRunner` 在同一控制器的实际支持内提供离线教师字段，缺曝光时必须等待或失败。`--train-hard` 训练的正是部署使用的 `ScoreRoutes`，不是另一套路由模型。

原有 `TrainBehaviorCloning(hardReplay)` 与 `TrainOnPolicy(hardRollout)` 回调接口可扩展课程。64 位候选按位生成；集合/赋值长度与不放回指针都有概率。提前 END_EDITS 计入似然；结构强制结束的概率为 1。REINFORCE 拒绝教师强制、确定性评估和旧参数似然。使用收集器时，通过 `TakeStepsForTraining()` 或 `TakePolicyTracesForBehaviorCloning()` 转交张量所有权；训练完成后才释放，否则无法反传。

自主训练示例保存每次更新前的行为策略、原始 task/options、完整诊断 journal 和似然记录，失败轨迹也可审计。诊断 journal 本身不等于通过独立可信锚认证的正式输出；最终的权重/Adam/RNG 检查点另行保存。

训练调用者负责提供课程、批次和展开。完整 BPTT 与无裁剪模式满足规格 T9 的声明边界；配置截断时，生产循环实际 detach 隐状态并记录边界，空闲步也遵守配置。配置截断或梯度裁剪时不沿用完整无偏声明。`DtrReward.PotentialDifference` 在所有终态将潜势设为零。`DtrTrainingRecipe` 记录优化器、成本系数、种子、课程及划分指纹。内置自主课程将 `ExactDataCostWeight` 应用于记录的完整确定性工作成本，终止奖励仅奖励独立复验成功的证书。

当前内置的硬训练例子是短 Horn 路由教师课程，**不是已经达标的自主预训练模型**。大规模多任务训练、失败恢复数据聚合、至少五个独立训练种子、保留结构测试，以及第 19 节的能力/拓扑收益验收需要实际实验。五个采样种子不能替代五个训练种子。

`TaskProblemGenerator` 提供可复现 Horn/graph/BooleanCSP 输入。训练/测试应先按结构模板分组划分，再重命名实例；重复结构换编号不构成保留结构测试。可使用通用模型/训练 API 扩展课程和对照网络，均须维持相同精确证书合同。

`Lib/Evaluation/DtrEvaluationRunner` 接受三种任务的测试实例，按任务/难度/执行模式分别报告成本、认证覆盖率、UNKNOWN、失败及 CSP SATWitnessCoverage。它重新取得并验证 runtime 的独立可信锚才计入认证。Wilson 区间和配对 bootstrap 使用显式统计配置；划分指纹、实际检查点摘要、共同预算和调用者声明的训练种子随报告保存。未测量指标不会伪装为零。

## 会话保存与恢复

`DtrSessionArchive` 保存完整日志前缀、float32 隐状态位模式、目录 epoch/桶/页/队列、pins、意图原期限与两个 RNG 流。快照摘要通过运行时 journal 绑定，恢复同时检查独立保留的 prefix head、模型权重摘要和所有版本。

```csharp
DtrSessionArchive? saved = null;
var result = new DtrInferenceRunner(model).Run(task, options, checkpoint: context =>
{
    if (context.Runtime.Snapshot().MicrostepId != 8) return false;
    saved = context.Save("run/session.json");
    return true; // 在已完成的微步停止，独占恢复该会话
});
// 将 saved.ExpectedPrefixHead 从可信运行时通道另行保管。
using var resume = DtrSessionArchive.Load("run/session.json", trustedPrefixHead, model, task);
var continued = new DtrInferenceRunner(model).Run(task, resume: resume);
```

恢复保留原预算与扫描进度，不能通过加载重启 epoch、延期意图或增加步数。恢复工作、检查点导出和日志字节仍计费。`Run` 消费恢复工作区的 Runtime 生命周期。调用者必须独占恢复同一 SessionId，并保护可信 head 的来源；该接口不提供多个分叉正式运行共享同一会话身份的授权。GPU 隐状态可保存，但不同数值后端间不宣称逐位执行一致。

## 成本与当前性能边界

工作成本、目录检查条目、编辑数、拒绝数、证据和日志长度可审计；墙钟/GPU 性能另行测量。神经处理及目录维护使用明确记录的保守操作配额，不能当作精确 FLOPs 或精确设备耗时。

`ConsDTRv1-NeuralScalarAllowance/v2` 预付已曝光支持内的编码、低秩消息、编辑评分、前缀和候选按位生成上界，以及 CPU 索引/选项维护。关系、特征、候选长度和真实页大小都进入公式；溢出和预算不足明确失败。默认 `MaxWork=100,000,000`，可按实验合同调整并随报告保存。性能评测必须固定共同费用表和预算，不能把不同版本的工作单位直接比较。

Runtime 原型在若干事务与摘要步骤复制/编码全状态，并如实计入费用；目录 epoch 的桶数组共享，但完整日志/快照序列化仍可能随目标数增长。当前不宣称已经达到最优增量内核、规模速度或显存收益。

`Frozen` 空图的失败只说明没有运输路径；在没有独立证明该固定图可完成任务前，不能从该结果宣称动态编辑优于可行固定图。消息关闭/打乱实验与独立重新训练对照也必须单独报告。

## 验证入口

`--self-test` 包含随机事务、完整 journal 删改攻击、跨会话/旧凭据、写授权、容量失败、WAL 恢复、Horn 真值枚举、路径/CSP 正负及 revision 攻击、跨页候选累积、公平曝光、TorchSharp 数值/梯度/权重/优化器/RNG 恢复、真实自主训练循环、会话恢复及三任务评估。

参考：[TorchSharp 官方项目](https://github.com/dotnet/TorchSharp)、[官方张量生命周期说明](https://github.com/dotnet/TorchSharp/blob/main/docfx/articles/memory.md)。
