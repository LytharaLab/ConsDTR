# ConsDTRv1 架构设计规格

> 架构名称：ConsDTRv1 - DTR = Dynamic Topology Reasoner。
>
> 作者：Con89524
>
> 规格版本：1.0；架构版本：ConsDTRv1；设计日期：2026-10-09；状态：**Draft-2**。可供实现与审查，尚未实现、训练或验证性能。
>
> 核心定义：冻结预训练参数，以有状态、硬离散、局部可编辑的神经计算图执行非自然语言推理；每个被正式接受的答案必须具有可独立复验的结构化证据。

Draft-2 修订重点：拆解路由/操作训练并设置自主学习门槛；目录加入结构直达与有界公平曝光；拓扑贡献必须通过因果干预和重新训练对照；正式证书携带从 genesis 到权威封口点的完整日志，禁止导出器按“相关性”裁剪事务。原有健全性条件保留，不以这些改进承诺训练必然成功。

## 1. 设计目标与保证边界

ConsDTRv1 同时设计神经计算、拓扑控制、精确状态转换、训练和结果协议。它不把一幅动态关系图或一段事后解释当作完成的 DTR。

本规格中的“纯推理”指不以自然语言理解、语言生成或语言模型预训练为核心，输入输出为结构化对象、规则、约束、数值与状态。**正式保证模式具有神经与确定性内核两部分**：神经网络学习选取计算路径和推理动作，确定性内核执行/检查已经选定的有限基本操作。若进一步要求所有执行和检查都仅由浮点神经网络承担，则本规格不宣称同样的答案正确性保证。

设计所提供的保证如下，条件和证明见第 11 节。

| 属性 | v1 的具体保证 | 必要条件 |
|---|---|---|
| 真正动态拓扑 | 硬连接决定执行的神经消息和精确操作数运输 | 禁止密集读取旁路；按实际边执行 |
| 局部变化 | 每个微步的编辑数量、端点数量和度数均有上限 | 事务式编辑和预算检查 |
| 预训练后推理 | 推理时参数冻结；状态、连接与证据可变化 | 推理会话不运行优化器 |
| 显式推理 | 正式答案引用执行时产生的类型化事件和证据 DAG | 提交门不可绕过；独立复验 |
| 已验证答案的正确性 | 已验证答案满足指定任务的正式语义 | 输入映射、规则、内核和实现正确 |
| 数值有界 | 神经隐状态在规定区间内保持有界 | 有界初始化、消息和凸组合更新 |
| 有限问题的条件完备性 | 明确启用公平回退时，有限 ground Horn 闭包问题可决定 | 全覆盖调度、足够容量、步数与正确任务语义 |

不由这些定理保证的属性包括：预训练必然收敛、神经策略必然找到解、固定预算总能完成、未知领域普适正确、优于 Transformer、通用智能、全局状态收敛，以及新颖性。错误内核、错误输入语义和硬件故障也不被抽象证明自动排除。

本版本建立完整的独立架构族定义；与 Transformer 相当的实际能力、规模效率和生态成熟度必须通过后续实现和实验评价。

## 2. 三种图与两个状态层

必须区分三个图，禁止以其中一个冒充另一个。

1. **输入关系图 \(G_X\)**：题目提供的对象和关系，只读。未知规则可以作为输入或候选假设，但不能直接登记为已经成立的事实。
2. **神经计算图 \(G_t\)**：连接神经计算槽位，实际决定消息传播和操作数到达。允许循环；推理期间局部编辑。
3. **证据图 \(P_t\)**：已提交事件的依赖 DAG，只能追加合法证据；删除计算连接不能删除历史证据。

两个状态层分别为：连续潜状态 \(H_t\) 和精确类型化状态 \(R_t\)。\(H_t\) 可以包含未验证猜测；只有 \(R_t\) 中具有合法来源的记录可以作为已验证前提。

完整状态定义为：

\[
S_t=(H_t,G_t,R_t,P_t,J_t,B_t,\mathcal I_t,Z_t,\Lambda_t),\qquad
S_{t+1}=\mathcal F_\theta(S_t;X,Q,\xi_t).
\tag{1}
\]

- \(\theta\)：编码器、路由器、消息函数、节点更新和动作策略的预训练参数。
- \(X,Q\)：不可变输入与目标，包括正式任务语义版本。
- \(J_t\)：候选枚举游标与可选公平调度账本。
- \(B_t\)：剩余计算、节点、边、证据和精确数据容量预算。
- \(\mathcal I_t\)：当前快照的 inbox 和读取凭据；每步重建。
- \(\xi_t\)：本步随机采样；会话状态/恢复文件还保存 RNG 状态，确定性评估则声明明确选择规则。
- \(Z_t\)：有期限、可撤销的学习意图和候选 pin 状态，不含已验证结论。
- \(\Lambda_t\)：运行时权威的连续事件日志；状态提交与日志提交采用同一逻辑事务。

参数在会话中满足 \(\theta_{t+1}=\theta_t\)。v1 不使用在线梯度更新、临时随机参数生长或跨请求持久化可塑权重。

```mermaid
flowchart LR
    X[只读结构化输入与目标] --> Enc[预训练编码器]
    Enc --> H[连续神经状态]
    H --> Router[学习到的局部编辑策略]
    Router --> Tx[硬拓扑事务]
    Tx --> Graph[实际稀疏计算图]
    Graph --> Msg[沿边传播消息与精确凭据]
    Msg --> H
    Msg --> Act[学习到的推理动作]
    Act --> Kernel[局部确定性执行与检查]
    Kernel --> R[已提交精确状态]
    R --> Enc
    Kernel --> P[追加证据事件]
    P --> Verify[独立复验与输出门]
    Verify --> O[已验证结果或明确的未完成状态]
```

## 3. 计算单元与实际神经拓扑

### 3.1 槽位不是输入对象本身

每个计算槽位 \(i\) 有身份 \((\mathrm{slotId},\mathrm{generation})\)、状态 \(h_i\in[-B,B]^d\)、类型、绑定记录句柄和激活状态。类型包含 `OBSERVATION`、`REGISTER`、`WORKER`、`GOAL`。对象可以绑定到槽位，但槽位是神经计算单元；同一 worker 可以先后处理不同对象或规则。

v1 采用**神经元组之间的块稀疏拓扑**。组内计算固定，组间连接动态；不宣称已经实现逐标量神经元任意重连。激活预分配槽位复用预训练模板，不创建未知权重。容量是显式配置，可随着训练/推理配置增长，不能无限自动增长。

槽位回收先通过正常 REMOVE 事务逐批删除所有关联边，每条删除与其端点都计入边编辑/支持集合预算；零度槽位才允许 reset。reset 清空隐状态、inbox、绑定句柄并增加 generation，不能以一个槽位操作免费删除全部边。证据记录拥有独立身份，不因槽位复用而改变。v1 默认保留已提交事实槽位到会话结束；事实换页与跨机器存储不属于首版。

### 3.2 关系类型与有效权重

数据边表示 \(e=(j,i,r,\mathrm{port},\mathrm{edgeRevision})\)。神经关系类型集合 \(\mathcal T_{edge}\) 的大小固定且有限，包括证据输入、规则输入、候选值、结果写入和神经工作消息；它不同于随题目变化的 Horn 规则集合 \(\mathcal R\)。连接类型是通信用途，不是未经验证的语义事实。

定义硬邻接掩码：

\[
A_{ijr}^t\in\{0,1\},\qquad
W_{ijr}^{\mathrm{effective},t}=A_{ijr}^t U_rV_r,
\tag{2}
\]

其中 \(V_r\in\mathbb R^{q\times d}\)、\(U_r\in\mathbb R^{d\times q}\) 为共享预训练参数。不存在为所有节点对训练独立稠密参数的要求。

执行时必须按边表 gather/scatter 或等价的块稀疏核计算。`A=0` 的边不运输消息、精确值或证据引用；被删除的边不会被稠密计算后简单置零的隐含数据路径替代。

首个正确性原型可以用稠密张量表达式辅助比对，但该后端须标为 `DenseReference`，不可据此宣称稀疏执行的速度或显存收益。

### 3.3 控制读取也计入计算图

\(G_t\) 的执行图包含数据边和控制探测边。控制器对候选节点的隐状态评分需要读取，不能把这些读取当作“免费、无连接的全局访问”。每个探测边计入候选读取预算和审计 `ControlReadSet`，每步用完即释放。

控制探测只获得类型化头信息和连续潜状态，不直接获得可提交的精确操作数。**精确动作的前提仍必须沿当前有效数据边到达 worker inbox。** 潜状态可能编码广泛信息；保证的是显式证据依赖和实际读取限制，不是每个潜向量维度都具有可解释语义。

## 4. 局部拓扑事务

### 4.1 局部性的定义

一个微步先选择大小不超过 \(u\) 的数据编辑/槽位事务端点集合 \(U_t\)。所有被编辑数据边的两端都在 \(U_t\) 中，编辑数不超过 \(b_e\)。控制探测端点采用独立预算，每步最多涉及 \(a(c+1)\) 个候选/目标端点，不套用 u。这里的局部重连按数据编辑支持集合和操作数量定义，允许少量长距离连接，不要求每次重连只连接原图的一跳邻居。

v1 编辑指令：`ADD_EDGE`、`REMOVE_EDGE`、`RETYPE_EDGE`、`ACTIVATE_SLOT`、`RESET_WORKER`、`NO_OP`。

`RETYPE_EDGE` 替换一个端点对的关系/端口，记一个编辑；`REMOVE+ADD` 记两个。数据边、控制探测边和槽位操作使用分开的预算，均计入总成本。不得用“交换整张邻接矩阵”作为一个编辑。

数据图必须满足：

\[
\deg^-_t(i)\le k_{\mathrm{in}},\quad
\deg^+_t(i)\le k_{\mathrm{out}},\quad
|E_t^{\mathrm{data}}|\le N_t k_{\mathrm{in}},\quad
N_t\le N_{\max}.
\tag{3}
\]

还必须满足端点存活、generation 匹配、类型/端口兼容、无重复端口冲突和无越权写入。输入事实不可覆盖，历史证据不可删除。

### 4.2 原子提交

拓扑编辑在快照上事务执行：先验证完整批次，再一次提交；任何失败使数据图保持原样。并发写入同一端口/槽位的候选须串行化或拒绝。

确实改变持久数据图或槽位生命周期的成功事务，使 `TopologyRevision` 恰好递增 1；拒绝、NO_OP 和结构不变的空事务不递增。同一 `(SessionId, TopologyRevision)` 唯一对应一份持久结构；规则见第 8.4 节。本步读取凭据只能使用当前版本。消息运输、动作检查和证据提交之间禁止再次改变拓扑。提交后才进入下一微步。

局部编辑成功不代表推理动作成功。如果动作失败，拓扑可保留以便后续计算，但精确状态不接受未验证结论。拓扑日志与语义证据日志分别记录结果。

### 4.3 真实依赖约束

动作的每个操作数必须拥有读取凭据：

\[
\mathrm{receipt}=(\mathrm{sourceHandle},\mathrm{valueRevision},
\mathrm{edgeId},\mathrm{topologyRevision},\mathrm{receiverGeneration},\mathrm{port}).
\tag{4}
\]

内核检查该凭据确实来自当前执行图、指向当前值且落在合法端口。仅提交一个旧 `proofId` 而没有当前运输凭据必须失败。规则自身也必须从规则节点到达，不能通过 ruleId 任意全局读取。

目标写入还需要 `WriteReceipt=(workerHandle,targetHandle,targetRevisionBeforeWrite,edgeId,topologyRevision,writePort)`。它仅授权当前出边及目标版本；提交时重新检查目标未被占用/改变，并把写凭据、输出精确值和新版本一起登记。输入节点和已提交不可覆盖记录不能作为任意写目标。

Draft-2 中上述字段是最小内容，还必须附带 `SessionId, MicrostepId, TransportSnapshotSeq, source/target BindingRevision, edgeRevision, ControlViewId` 和两端完整 generation。精确读取/写入凭据分别引用权威日志记录；TopologyRevision 相同不意味着值绑定或临时控制视图相同。完整合同见第 8.4 节。

精确数据只能在自己的寄存器或 inbox 中持久化；普通隐状态、候选目录或神经指针不能创建已验证前提。worker 跨微步保留精确数据时，必须先通过合法 `COPY` 提交到显式寄存器，再经后续边运输。提交的目标寄存器必须通过当前 `WRITE_RESULT` 出边授权；不得通过任意目标 slotId 全局写入。

## 5. 候选生成与拓扑策略

### 5.1 有界候选，而非隐含全连接检索

每步最多为 \(a\) 个目标槽位生成候选，每槽最多 \(c\) 个。Draft-2 采用三路来源：`PublicStructuralIndex`、`ExistingOrPinned` 与 `FairExposure`，分别具有不可越界的配额 \(c_s,c_p,c_f\)，满足 \(c_s+c_p+c_f\le c\)。桶键与顺序来源于输入和已提交离散历史，不依赖参数相关的硬 top-k。

- 公共结构索引提供类型/端口兼容性、输入显式对象 ID 和关系 incidence 的直接寻址。例如查询一条输入规则中出现的原子身份，可返回对应槽位头信息；不检查这些前提是否已成立，不执行规则求闭包。
- 已有邻居和限时 pin 保留已曝光的有用句柄，避免多步路由因下一页切换而反复丢失端点。pin 不创建连接、不搬运精确值，期限不超过当前意图的 `MaxIntentSteps`；旧 generation 或绑定版本必须重新确认。
- 公平通路扫描较宽的结构兼容桶，使精细索引漏掉的候选仍有曝光机会。已有邻居、pin 和结构直达不能抢占公平配额。

三个来源去重后的空余位置可以使用公开的固定填充规则，但不能回退为免费全桶扫描。pin 只在已经曝光的句柄上建立，不提供老师端点。目录构建、维护、索引访问、去重、pin 检查、快照保存和 tombstone 检查均计费。

本步目录由 X、已提交离散历史和旧意图 Z_t 确定。非公平目标、结构查询键和 pin 必须来源于已计 log-prob 的历史动作，或公开确定性规则；新意图对查询的改变在下一微步生效。pin 按已采样意图建立/续用，ABORT/到期时移除，超额按固定 FIFO/身份序裁剪；已有邻居、去重和填充也使用固定顺序。禁止从 h/logits 直接做未记录概率的硬 top-k、阈值裁剪或神经优先队列。若以后增加同一步随机查询，其选择也必须成为联合策略字段，不能仍宣称候选支持与参数无关。

目录只暴露绑定身份、类型、对象标签和可访问性等头信息；索引查询不执行所有规则前提匹配，不提供“当前全部可推导结论”。后者会把主要推理偷偷交给符号内核。

候选形成后，通过显式控制探测读取受限的潜状态，再评分：

\[
s_{ijr}=\operatorname{MLP}_\theta[
h_i;h_j;\tau_i;\tau_j;\eta_r;\eta_Q;\eta_{e_{ij}};\eta_B].
\tag{5}
\]

目标嵌入 \(\eta_Q\) 可广播；如果增加全局状态聚合，必须把聚合读取作为控制依赖和真实成本记录。v1 不设置隐藏全状态随机读取 API。

### 5.2 策略支持与探索

结构合法的编辑形成 \(\mathcal A_{\mathrm{struct}}(S_t)\)，神经策略只在此集合上选择。合法掩码检查结构、句法、类型和容量，不提前检查所有可能动作的语义真值。

训练时编辑、worker、规则/操作、操作数句柄与端口选择均为离散策略动作。使用 \(\epsilon\) 探索或非零概率分布能改善覆盖机会，但不保证有限预算内找齐正确连接。

候选不足时继续推进游标或输出明确失败状态。公平回退见第 12 节，它有独立运行身份，不能计作神经策略的学习成功。

### 5.3 目标公平与不被重启的曝光 epoch

仅让被策略频繁选择的 worker 分页，不能保障其它目标的候选曝光。每个 epoch 冻结完整的结构合法目标集合 \(W_e\)、各目标兼容桶的 membership/order revision；每个 entry 保存 `(slotId,generation,BindingRevision,MetadataRevision)`。除显式计费且计入 G_e 的构建/维护暂停外，每步至少预留 \(a_f\) 个公平目标服务位置（\(1\le a_f\le a\)），按固定 round-robin 服务；其余位置可以服务当前意图/历史动作指定的目标。调度器只提供头信息，边与语义动作仍由神经策略选择。

每次公平服务检查该目标当前桶页中的至少 \(\min(c_f,\text{剩余条目数})\) 个快照条目，或处理一个空桶；不是允许策略选择扫描零项。空桶占用一次服务并计费，桶切换不另外占公平服务次数。已完成目标的跳过采用有界队列操作；不能在一个微步免费越过无限空项。

新增条目/目标进入下一 epoch；删除、reset、generation、绑定或索引元数据版本变化在旧快照中成为 tombstone，变更后的 entry 进入下一 epoch。公平曝光、结构直达和 pin 均核对 live 版本，不通过时不得使用旧标签冒充当前对象。tombstone 和重复项同样消耗扫描配额，不能靠免费跳过隐藏成本。不得因为任意一次图/绑定更新重置整个扫描游标。只有全部冻结目标完成全部桶页后才结束 epoch。使用持久有序索引/MVCC 避免每步全量复制，保留旧版本的内存和维护成本仍计入预算。

目录的公平曝光属于 `LearnedOnly` 的输入访问合同，**不是 T8 的公平求解回退**：它不会选规则、检查所有前提、添加边或提交结论。

### 5.4 命题 T2a：条件有界的候选曝光

设 epoch 内目标数为 \(w_e=|W_e|\)，目标 i 的第 b 个桶有 \(M_{ib}\) 个快照条目；每次公平服务最多一页。定义：

\[
H_e=\left\lceil w_e/a_f\right\rceil,\qquad
D_i=\sum_b\max\left(1,\left\lceil M_{ib}/c_f\right\rceil\right),\qquad
E_e=G_e+H_e\max_i D_i.
\tag{6}
\]

**假设。** 有限快照；构建和会暂停公平服务的维护总共最多占 G_e 个微步，非暂停管理工作在有界预算内完成；公平服务没有被其它通路抢占；所讨论目标/候选持续存活、类型合法且绑定/相关索引元数据版本不变；运行与目录预算至少覆盖该 epoch。每次服务处理一页或一个空桶，维护不重启扫描。空目标集合规定 max D_i=0，构建完成即结束空 epoch。

**结论与证明。** 除去至多 G_e 个暂停微步，每个目标至多 H_e 个微步获得一次服务，第 D_i 次服务前其全部桶页/空桶已处理，因此该快照内任一符合假设的候选至多 E_e 步曝光。若连续 epoch 各有统一完整上界 E，新登记且持续合法、版本稳定的条目至多等待当前 epoch 的余量再经历下一 epoch，故最多 2E 步曝光。□

该上界已包含声明的构建/维护暂停、空桶服务；没有可验证的 G_e 上界就不能承诺微步等待上界。持续 reset/重绑定、瞬时候选、运行不足预算或无限新增造成没有统一 E 时，不能沿用该结论。**曝光不保证选择、有效路由或任务求解。** 它为 T2 提供候选最终可出现的条件，不证明参数会选择目标图。

没有结构线索的宽桶中，最坏仍需 \(\Omega(M)\) 次条目检查；公平分页不能消除这个信息访问成本。以 w_e=256、单非空桶大小 256、a_f=1、c_f=4、G_e=0 为例，E_e=16384 步，远高于初始 512 步。只有 8 个 worker 是合法目标的显式子模式中同配置曝光项为 512 步，实际还需加构建/维护项；不能把该子模式用于 T2 的任意合法目标图声明。

### 5.5 曝光预算与扫描主导的失败处理

manifest 记录目标模式、公平配额、桶数/桶大小上限、epoch 构建/维护预算和包含 G_e 的 `EpochExposureBound`。启动时用完整 E_e 与可用运行/目录预算比较，报告 `FullEpochFitsBudget`；不满足时仍可运行快速策略，但不得宣称完整曝光覆盖。

每个实例记录首次曝光等待、总检查条目、tombstone、索引维护成本、目录时间/总时间比值。达到显式目录预算只返回 `UNKNOWN(reason=EXPOSURE_BUDGET_EXHAUSTED)` 或对应资源状态，不静默降低公平配额、跳过仍声明的覆盖，也不自动开启求解回退。

扩展验收采用 2 倍/4 倍规模和无关干扰曲线，同时报告认证覆盖率和扫描成本。公共结构索引应使有显式寻址线索的任务避免反复全桶扫描；无结构线索的最坏成本如实保留。若目录持续占据主要成本且覆盖停滞，先调整索引/访问模式并重新评测，不能仅扩大隐藏扫描预算后声称推理效率提升。

## 6. 稀疏消息与循环神经更新

每个微步使用同一组参数。定义有界消息：

\[
v_{ji}^t=B\tanh\left(U_rV_r h_j^t+D_r z_j^t+b_r\right),
\tag{7}
\]

其中 \(z_j^t\in\mathbb R^q\) 是该节点所拥有的输入/已提交记录的局部编码，\(D_r\in\mathbb R^{d\times q}\)；没有记录的 worker 使用零值和类型嵌入。连续编码不作为精确数值证明。长对象编码的成本单独计入，不能把任意长度数据当作免费定长输入。

消息门 \(w_{ji}\in[0,1]\)，聚合为：

\[
m_i^t=\frac{\sum_{j\to i}w_{ji}v_{ji}^t}{1+\sum_{j\to i}w_{ji}},
\qquad
u_i^t=B\tanh F_\theta(h_i^t,m_i^t,z_i^t,\eta_Q),
\tag{8}
\]

\[
\alpha_i^t=\sigma A_\theta(h_i^t,m_i^t,z_i^t),\qquad
h_i^{t+1}=(1-\alpha_i^t)\odot h_i^t+\alpha_i^t\odot u_i^t.
\tag{9}
\]

式 (8) 的求和只遍历当前已提交的 \(E^{data,+}\)。控制探测边只参与式 (5) 的评分，不参与式 (7)–(9) 的消息或精确运输。i 遍历全部活槽位；U_t 仅限制编辑端点，a 仅限制候选目标数。

分母中的常数 1 等价于加入零消息，避免空邻居情况和随度数增长的未归一化累积。\(F_\theta\) 的初始实现为两层共享 MLP：输入投影到 \(2d\)，GELU 后投影到 \(d\)，外接 tanh；门控为共享线性层加 sigmoid。类型嵌入维度、数值编码器和关系低秩维度均由 manifest 明确。

不同 worker 可由类型嵌入调制同一计算单元，v1 不预先硬编码“聪明节点”“逻辑节点”等未经验证的功能标签。

模型的推理时间来自重复调用共享单元和连接编辑，不来自输出自然语言思维链。初版每微步执行一次式 (7)–(9)，需要更深计算时继续循环；不隐藏未计费的多层内部重复。

正式保证依赖精确证据内核，不依赖浮点状态恰好等于某个离散真值。出现 NaN/Infinity 必须终止该轨迹并输出 `RESOURCE_OR_NUMERIC_FAILURE`，不能继续签发已验证结论。

## 7. 显式推理动作与最小内核

### 7.1 不是事后说明

神经动作策略根据 worker 更新后的隐状态选择操作和实际 inbox 句柄：

\[
a_t\sim\pi_\theta(\cdot\mid h_w^{t+1},\mathrm{InboxHeaders}_w,Q,B_t).
\tag{10}
\]

已验证状态唯一变化路径为：

\[
(R_{t+1},P_{t+1})=
\begin{cases}
\operatorname{Commit}(R_t,P_t,a_t,\mathcal I_t),&K(a_t,\mathcal I_t,R_t,X)=\mathrm{accept};\\
(R_t,P_t),&\mathrm{otherwise}.
\end{cases}
\tag{11}
\]

检查器仅检查本次具体提议，并执行有限基本操作。它不能在 `Check` 内运行 BFS、SAT 搜索、全规则闭包或其它隐藏求解器。完整性回退是另外的显式调度模式。

### 7.2 通用操作与任务包

内核基础操作为 `INPUT_REF`、`COPY`、`EQ`、`COMPARE`、`ADD`、`SUB`、`SELECT` 和 `PROPOSE_VALUE`。`INPUT_REF` 只用于初始化登记输入根，或确认已经沿边到达的输入 token；不能通过任意 inputId 全局取值。整数使用显式位宽或带容量限制的精确整数，禁止把浮点近似相等当作数学相等。所有溢出、除零、不支持类型和容量超限必须拒绝。

`PROPOSE_VALUE` 产生类型正确的候选值，不产生“此值满足目标”的事实。猜测与已验证命题使用不同类型，不能隐式转换。

首版有以下三个正式任务包，共享神经核心和拓扑动作：

| 任务包 | 语义操作 | 允许正式声称的结论 |
|---|---|---|
| `GroundHorn/v1` | `APPLY_GROUND_RULE`、`CHECK_CLOSED_SET` | 可导出；具有闭合集证书时不可导出 |
| `Reachability/v1` | `START_PATH`、`EXTEND_PATH`、`CHECK_CUT` | 存在有效路径；具有闭合割集时不可达 |
| `FiniteCSP/v1` | `PROPOSE_ASSIGNMENT`、`CHECK_CONSTRAINT`、`CHECK_ALL` | 给定赋值满足全部约束 |

这些任务包是操作语义和验证器，不是每个任务一套独立神经网络。添加任务包须同时指定输入语义、操作规则、独立验证器、训练数据和验证边界。

v1 不提供 `UNSAT`、最短路径最优性、任意实数定理或开放世界事实真伪的通用保证。若未来添加这些能力，必须添加覆盖完整输出谓词的证书，而不是复用“路径有效”或“一个赋值有效”的检查。

### 7.3 Ground Horn 的正式语义

输入 \(\Gamma=(\mathcal A,F,\mathcal R)\)，\(\mathcal A\) 为有限命题原子集合，\(F\subseteq\mathcal A\)，规则为无变量的 definite Horn 子句：

\[
r:\quad a_1\land\cdots\land a_\ell\to b,
\quad a_i,b\in\mathcal A,\quad 1\le\ell\le\ell_{\max}.
\tag{12}
\]

事实编码为输入假设，不以无前提规则重复表示。规则前提在解析时按原子身份规范化为集合、移除重复项，并记录原始到规范化映射；合取的幂等性保持语义不变，路由无需为同一前提重复占用端口。v1 不含否定前提、函数符号、无限域或自动变量统一。需要 ground 化的任务须在输入规范内显式完成并计入成本，不能隐藏指数展开。

`APPLY_GROUND_RULE` 接收一个规则 token 和 \(\ell\) 个已验证原子 token。内核只检查 token 真正到达、规则确属输入、原子逐项匹配及输出目标，随后生成结论 \(b\) 的证据。它不会选择下一条规则或搜索前提。

对目标 \(q\)，正答含义为 \(\Gamma\vdash q\)。负答含义为 `NOT_DERIVABLE`，即 \(\Gamma\not\models q\)；不等于 \(\Gamma\models\neg q\)。

负证书 \(C\subseteq\mathcal A\) 必须满足：

\[
F\subseteq C,\quad q\notin C,\quad
\forall (A_r\to b_r)\in\mathcal R:\ A_r\subseteq C\Rightarrow b_r\in C.
\tag{13}
\]

检查整个闭合集须分块读取所有输入规则，计入时间和运输；`CHECK_CLOSED_SET` 是有界状态机而非一个无限成本微步。证书可以由神经策略提出，也可以来自独立标记的公平回退。

所有全量证书检查使用如下显式累积状态机：`BEGIN_CHECK` 固定候选集合/赋值的不可变 revision、任务内容和需覆盖的输入 ID 清单；`CHECK_NEXT` 从当前 inbox 消费前一份累积凭据、候选数据和一个规则/边/约束，检查该条并提交新版累积凭据；`FINALIZE_CHECK` 仅在去重的覆盖账本与完整输入清单一致、全部检查成功且目标条件满足时签发证书。覆盖账本、输入个数、候选版本及前一累积凭据均计入精确容量和证据。候选改变必须重新 BEGIN；抽样通过、重复检查一条或未扫描完不能通过 FINALIZE。每步只需有限数量的当前 inbox 操作数，避免最终一次读取所有证据绕过度数上限。

### 7.4 路径与约束的语义

路径证书为 \((v_0=s,v_1,\ldots,v_L=t)\)，逐边 `EXTEND_PATH` 检查输入中存在对应边。允许重复节点时仍是有效 walk；要求简单路径的任务需另加无重复检查。有效路径不证明最短路径。

不可达证书为集合 \(S\)：\(s\in S,t\notin S\)，且输入中没有从 \(S\) 指向 \(V\setminus S\) 的边。证书检查扫描全部相关输入边，不能只检查神经采样到的边。

CSP 赋值 \(\sigma\) 是候选数据，`CHECK_ALL` 只有在全部变量已赋合法值、每个输入约束均以精确语义检查通过后，才能产生 `SATISFIES` 证书。各次检查必须绑定同一个不可变 `assignmentRevision`；修改赋值会使旧检查对新版本无效。v1 未找到赋值时返回 `UNKNOWN`，不能返回 `UNSAT`。

## 8. 证据事件、显式性与输出门

### 8.1 事件结构

每个已提交事件最少包含：

```text
ProofId, SessionId, TaskSemanticsVersion
Operation, InputProofIds, InputValueRevisions
RuleOrConstraintInputId, ExactOutput, OutputType, OutputRevision
WorkerId, WorkerGeneration, TopologyRevision
DataReadReceipts, WriteReceipt, TargetHandle, TargetValueRevision
ControlReadSet, CommitStep
ModelManifestId, InputDigest, ExecutionMode
CommitJournalSeq, ReceiptJournalSeqs, TransportSnapshotSeq
SourceBindingRevisions, TargetBindingRevision, ControlViewId
```

精确内容和凭据需要完整可读取；散列仅用于身份/完整性辅助，不能代替规则检查。证据 DAG 引用的事件必须拥有严格更小的 `ProofId`。分支假设标注作用域，不能提升为无条件已验证事实；首版不提供未定义的“撤销后继续使用”语义。祖先之外的语义提交也在完整 journal 中携带足够的原操作/精确内容，不能因为 ProofDAG 裁剪而省略重建所需状态。

神经潜状态日志可按预算选存，用于模型分析；它不属于逻辑正确性的必要证明。`ControlReadSet` 记录实际评分和状态读取，避免把控制依赖误报成只来自数据边。

### 8.2 显式推理的精确定义

显式保证覆盖：输入假设、使用的规则/操作、确切前提、执行结果、步骤依赖、拓扑运输与正式输出。非输入即成立的正式结论必须至少有一个语义操作事件，不能用重复 COPY 或空日志冒充推导。

在受信运行时满足 journal 覆盖且没有旁路的假设下，此保证说明执行时发生了可检查的形式推导；它不说明每次神经打分都可解释，也不证明证据是唯一、最短或与人的推理方式相同。候选证据在运行过程中提交，不允许先输出答案、再补编轨迹。仅检查自洽证书不能证明物理机器实际执行，参见第 8.4.4 节。

### 8.3 输出状态

```text
VERIFIED_RESULT          // 证书证明完整输出谓词
VERIFIED_NEGATIVE        // 专用负证书通过，含任务语义
UNKNOWN                 // 当前预算或策略未得到可接受证书
UNVERIFIED_CANDIDATE     // 仅研究模式，可展示猜测，不是正式结论
UNSUPPORTED             // 超出任务包、类型或容量规范
RESOURCE_OR_NUMERIC_FAILURE
```

正式运行模式不得将 `UNVERIFIED_CANDIDATE` 自动降格包装成已完成答案。学习到的停止头只请求检查输出；它不能把预算耗尽升级为否定结论，也不能宣布搜索已经穷尽。

结果附带 `Mode=LearnedOnly|FairFallback|OracleDiagnostic`、精确输出谓词、证书、总成本和完成原因。回退介入后的整次成功归为 `FairFallback`，另报告介入前神经阶段结果。

### 8.4 完整、权威的拓扑重放协议

**取消“导出相关拓扑事务”的自由裁剪。** Draft-2 正式证书携带 `RuntimeJournal` 从 canonical genesis 到权威 `SEALED_EXPORT_CUT` 的完整连续前缀 \(\Lambda[0..L]\)。ProofDAG 可以裁剪到正式输出祖先，但日志不能按“与结论是否相关”裁剪；旁支删除、失败、NO_OP、模式变化与生命周期事件都可能影响后续结构或计费。

`RuntimeJournal` 是运行时唯一状态提交入口。内存状态变化与日志追加处于同一逻辑事务；持久化采用写前日志或等价提交协议，只有可恢复的已提交日志决定对外可见状态。写日志失败/容量耗尽时不得先改状态再签发正式答案。崩溃恢复从已提交前缀重建，未完成事务不伪装成完整运行；禁止旁路改图/绑定、恢复时改写旧记录或补造缺失事件。

#### 8.4.1 初态、版本和连续序号

`GENESIS` 完整保存规范化 X/Q、SessionId、架构/草案状态、任务/内核/日志 schema 版本、模型检查点绑定、模式、所有初始槽位及 generation/type/活性/绑定、初始数据边及 edgeId/edgeRevision、输入精确记录/版本、初始预算和初始化成本。初始 `TopologyRevision=0`，即使初图为空也必须明确编码。

每条事件最少保存：

```text
EventSeq, SessionId, MicrostepIdOrTerminalPhaseId, Phase, EventKind
CanonicalPayload, PreTopologyRevision, PostTopologyRevision
PreExactStateRevision, PostExactStateRevision
PreStateDigest, PostStateDigest, PrevEventDigest, EventDigest
DeltaWorkCost, CumulativeWorkCost, ExecutionMode
```

- `EventSeq` 必须精确覆盖 0..L；不能重排、重复、跳号或复用。
- `TopologyRevision` 标识持久数据图和槽位生命周期。真正改变结构才递增恰好 1；拒绝、NO_OP、空成功事务保持。相同 revision 不得对应两份不同结构。
- 精确绑定、值与对象状态使用独立 `BindingRevision/ValueRevision/ExactStateRevision`；每次改变有完整日志 payload 与前后版本，不借“图没有变”省略。
- 临时控制视图用 `(SessionId,MicrostepId,Phase,TopologyRevision,ControlViewId)` 标识，创建、读取、释放都记录。它不凭 TopologyRevision 单独命名。
- `RETYPE` 递增 edgeRevision；删边后新建的 edgeId 不得偷偷复用旧身份。reset/activation 属于结构事务，旧/新 generation 都记录。

#### 8.4.2 不可省略的事件与快照

完整日志至少覆盖：`STEP_BEGIN/END`；目录目标/epoch/桶版本、查询、候选身份与 cursor；pin/意图建立、续用与中止；`CONTROL_PROBE_OPEN/READ/CLOSE`；提出的完整拓扑批次与接受/拒绝/NO_OP；槽位生命周期；运输起止和每份精确读写凭据；语义提议/拒绝/提交与绑定版本；模式切换、公平保护；预算扣除；STOP、预复验、数值/资源失败、输出准备和封口。

控制读取事件保存所有实际读取的身份和状态版本，包括未被选择但参与打分的候选；神经潜状态数值可选存，不因此省略其读取身份。确定性重放只恢复声明的结构/精确状态，不把可选潜状态日志当作神经逐位重放。

`TransportSnapshotSeq` 固定一个运输开始事件的前态。复验从 genesis 逐条重建该快照的图、句柄和精确绑定，然后核对 receipt 的当前边、edgeRevision、source/receiver generation、绑定/值版本、端口和输出写目标。旧版本仅可用于原历史事件的上下文，不能被新动作作为“当前 inbox”引用。

日志使用固定的 `DTRCanonical` 编码：带类型标签和长度前缀的字段；固定整数编码/字节序；UTF-8 字符串；set/map 按定义的键序排序；重复键、非规范编码、未知语义字段、非法枚举和整数溢出拒绝。摘要链基于这一唯一编码；普通 JSON 输出可作为展示形式，不作为规范字节编码。

`StateDigest` 的域明确为持久结构、精确绑定/值引用、目录/意图/保护元数据和计费状态，不含自身摘要、签名或日志链，以免递归散列；可选神经状态摘要另列。`EventDigest` 对规范事件除自身摘要字段外的字节计算。重放仍逐项核对状态/操作，不把摘要相等代替语义检查。

#### 8.4.3 封口、导出权限与外部锚

`CertificateManifest` 绑定如下字段：

```text
ArchitectureId=ConsDTRv1, SpecRevision=Draft-2, JournalSchema
SessionId, GenesisDigest, InputQueryDigest, Kernel/TaskVersion
ModelCheckpointDigest, ExecutionMode, ExactOutput, ProofRootDigest
FirstSeq=0, LastSeq=L, EventCount=L+1
TerminalTopologyRevision, TerminalExactStateRevision
TerminalStateDigest, TerminalEventDigest, DeclaredWorkCost
ExportAuthorityId, EndAnchorId
```

封口由运行时控制，不能由 exporter 自选 cut。步骤完成、读取清理、计费和输出候选冻结后，用独立 terminal meter 执行预复验。预复验拒绝时，记录失败、结清 terminal meter、显式解除输出冻结，再以新微步继续。只有预复验接受后才进入不可恢复的固定结果记录、预算结算和封口尾部，禁止再修改 G/R/P/输出。`SEALED_EXPORT_CUT` 绑定最终输出、ProofRoot、模式和状态版本；封口之后本会话不再产生可被该证书引用的执行事件。

避免自散列循环：封口 payload 先绑定输出等内容，算出最后事件摘要后，再生成外部 `EndAnchor=(SessionId,GenesisDigest,L,TerminalEventDigest,OutputDigest,ProofRootDigest,Mode,CheckpointDigest)`。manifest 引用已完成的锚，封口 payload 不包含自己尚未计算的摘要。

独立验证器的 expected EndAnchor 从可信运行时、可信存储/透明日志，或预先配置的 authority 公钥验证得到，不能只相信证书自己附带的“完整日志 head”。exporter 只能序列化已封口字节，不得删除事件、换 cut、另签自己的锚。摘要链本身不能证明没有遗漏；签名只证明某 authority 认可这些字节，不独自证明 GPU 运算或没有隐藏旁路。

每个微步必须按类型化阶段状态机结束。正常封口要求无开放控制探测/运输、无未完成事务，所有引用事件不晚于 cut；不能缺少 STEP_END 或把释放/费用移到未导出的执行尾部。预复验成本计入日志；封口前通过 journal 一次性扣除 `FinalGateWorkAllowance` 和封口/序列化/锚取得的有界配额，不只是 reserve，封口后不退款、不改写 canonical Budget/DeclaredWorkCost。最后 gate/exporter 按该 cap 和最大字节数执行，超限只能返回非正式失败。实际外部验证/交付用量作为绑定该 cut 的收据另列，不回写或替换封口账本；这是保守计费配额，不冒充精确墙钟成本，避免自验证成本的无限循环。

完整前缀是 v1 正式模式要求。checkpoint 仅作为重放缓存，必须先由 genesis 完整前缀核对；独立 checkpoint、按相关性导出的子图或自签锚不得沿用此保障。未来受信快照模式须另列新增信任假设，本 Draft-2 不提供它。

#### 8.4.4 三种声明及正式输出条件

| 声明 | 证明内容 | 不证明的内容 |
|---|---|---|
| `SemanticVerified` | X/Q/ProofDAG 证明完整输出谓词 | 证据一定由神经模型产生 |
| `RouteReplayVerified` | 完整唯一重建的记录图/精确快照中，动作运输与写入合法 | 字节记录等于物理机器执行 |
| `ExecutionBound` | 可信运行时/锚把记录绑定到声明会话 | 签名本身证明无旁路、GPU逐位结果或模型能力 |

正式 `VERIFIED_RESULT/VERIFIED_NEGATIVE` 必须同时满足 SemanticVerified、RouteReplayVerified 和配置规定的可信锚校验。语义证书单独有效但日志不完整时，可以返回独立语义复验报告，不能冒充 ConsDTRv1 的完整正式运行证书。

### 8.5 命题 T6a：被引用快照的唯一重建

**假设。** genesis 唯一指定初态；每种允许状态变化都有完整 payload 和唯一确定性转移；验证器检查连续序号、阶段、前后版本、计费及规范编码；前缀符合独立取得的权威 EndAnchor；运行时所有变化通过 journal。使用摘要比较时额外假设无碰撞，使用签名时额外假设 authority 配置和验签正确。

**结论。** 每个引用的 `(SessionId,TopologyRevision)` 唯一确定持久图/槽位代际，每个 TransportSnapshotSeq 唯一确定精确绑定和执行视图。同一权威 cut 下，exporter 不能通过省略、重排或替换事务使不同结构被接受。

**证明。** genesis 确定初态。按 EventSeq 归纳，前态已经唯一，下一事件的规范 payload 和确定性转移唯一确定后态；失败/NO_OP 是结构恒等转移，但仍改变计费/日志状态。版本登记拒绝相同 revision 的结构别名。序号连续性、阶段检查及外部锚排除省略/截尾/重复/重排；规范摘要绑定在无碰撞假设下排除替换。因此所有引用快照唯一。运行时无 journal 旁路时，该重建与声明的实际逻辑状态一致。□

没有独立可信的终端锚时，只能证明“所提供的自洽日志有唯一重建”，不能证明日志包含真实执行的全部事件；日志覆盖和实现可信是明确的实现义务，不由散列代替。

## 9. 一个微步的完整执行顺序

```text
1. 检查任务规范与参数冻结，通过 journal 先扣除微步预算并记 STEP_BEGIN，再创建只读状态快照；拒绝/NO_OP/早退均不会退还已发生成本。
2. 从结构索引/pin/公平epoch生成受限候选，完整记录目标、页、版本、身份与成本。
3. 建立有界控制探测；神经策略选有限意图和逐边编辑，概率采样前检查非有限值，控制读取和意图均写日志。
4. 原子检查并提交编辑与对应日志；结构实际改变才递增 TopologyRevision，失败/NO_OP也记录。
5. 沿当前数据边运输消息与精确 token，生成本版本 inbox/凭据。
6. 用共享参数执行一次稀疏消息和神经更新；立即检查非有限值，失败则先返回数值失败。
7. 神经策略选择 worker、操作与 inbox 操作数，动作 logits 同样先做数值检查；公平动作使用调度器已指定的选择。
8. 检查具体动作，通过同一 journal 事务提交精确结果/绑定/证据；拒绝也留完整记录。
9. 请求停止时，冻结候选输出和ProofRoot，先完成本步释放/计费/STEP_END，再独立预复验冻结前缀；随后只允许固定结算尾部、权威封口和最终gate，全部通过才签发正式结果。
10. 每个工作项执行前预检预算并增量计费；早退记录失败并关闭当前阶段，日志不足导致无法完整记录时只能返回非正式失败。不得先export，再在未导出的尾部修改状态或补费用。
```

每步最多提交一个语义动作，以简化 v1 的顺序语义、复验和训练。将来并行提交须定义读写冲突检测与等价串行化证明，不能直接假定成立。

```text
Run(X, Q, manifest, mode):
    validate_input_and_capacity(X, Q, manifest)
    freeze(theta)
    runtime = initialize_with_canonical_genesis(X, Q, manifest, mode)
    while runtime.try_begin_metered_step_and_log():
        snapshot = runtime.read_only_snapshot()
        meter = runtime.current_meter()  // 各工作项预检/计费；所有持久写入经journal
        if mode == FairFallback and fairness_slot_due(snapshot):
            intent = fair_scheduler.next_explicit_intent(snapshot, meter)
        else:
            candidates = runtime.directory_pages_and_log(snapshot, meter)
            intent = neural_policy.propose_checked(snapshot, candidates, meter)
            if intent.numeric_failure:
                return runtime.close_failure_without_verified_output()
        runtime.commit_intent_and_topology_or_log_reject(intent, meter)
        inbox = runtime.transport_and_log_current_snapshot(meter)
        neural_snapshot = runtime.post_edit_neural_snapshot() // RESET/ACTIVATE后的匹配代际状态
        H = shared_sparse_processor(neural_snapshot.H, runtime.G, inbox, meter)
        if numeric_failure(H):
            return runtime.close_failure_without_verified_output()
        action = intent.resolve_action_checked(H, inbox.headers, meter)
        if action.numeric_failure:
            return runtime.close_failure_without_verified_output()
        view = runtime.restricted_kernel_read_view(inbox, action)
        check = kernel.check_this_action(action, view, meter)
        runtime.commit_H_and_checked_action_with_journal(H, check, meter)
        if intent.request_stop:
            prefix = runtime.prepare_output_close_step_and_freeze(meter)
            terminal_meter = runtime.begin_independent_terminal_meter()
            precheck = independent_verifier.check_preseal_prefix(X, Q, prefix, terminal_meter)
            if not precheck.accept:
                runtime.record_failed_precheck_unfreeze_and_resume(terminal_meter)
                continue
            allowance = runtime.debit_final_gate_and_export_allowance(terminal_meter)
            sealed = runtime.append_fixed_tail_and_authoritative_seal(precheck, terminal_meter)
            certificate = exporter.serialize_complete_prefix(sealed, allowance)
            expected_anchor = runtime.obtain_anchor_outside_exporter(sealed, allowance)
            result = independent_verifier.final_gate(X, Q, certificate, expected_anchor, allowance)
            return result.with_bound_verification_receipt_and_final_costs()
        runtime.close_and_log_current_step(meter)
    return runtime.close_unknown_and_log_terminal_costs()
```

所有 metered 调用在不足预算时通过统一出口返回 `RESOURCE_OR_NUMERIC_FAILURE` 并结清成本，不能跳过检查继续执行。普通微步预算耗尽按上面路径返回 UNKNOWN。回退在完成目标证据/闭合集时确定性设置 `request_stop`；未完成公平事务的资源按第 12 节保护。final_gate 不通过时返回非正式失败，不能返回 VERIFIED。预复验与正式封口检查是不同阶段，前者绝不先向用户输出正式答案。

`kernel.check_this_action` 和独立 verifier 是两个实现职责：前者限制在线提交，后者从输入和完整前缀重建语义/路由，不信任在线内核的 `accept=true` 标记。预复验可缓存冻结前缀的检查状态；最终gate必须核对缓存对应的字节前缀、仅允许的固定尾部与可信锚。外部独立验证没有该缓存时须完整重放。两者都存在实现错误风险，抽象证明不能替代工程验证。

在线内核实际接口采用受限 `KernelReadView`，只暴露当前已验证 inbox、写授权、任务头信息和当前动作相关的不可变元数据，不暴露任意全局事实/证据读取。证书包导出输入绑定、ProofDAG、genesis-to-seal 完整 RuntimeJournal 与权威锚，不能自选“相关事务”。语义复验不需要 theta；完整神经执行重放另需权重/RNG/数值后端，不能由证书复验冒充。

## 10. 一条可逐步复验的完整轨迹

输入：\(F=\{A\}\)，\(r_1:A\to B\)，\(r_2:B\to C\)，查询 \(C\)。示例槽位为事实 `sA`、规则 `sr1/sr2`、worker `w`、结果寄存器 `sB/sC`。

| 阶段 | 实际拓扑/操作 | 精确状态与证据 |
|---|---|---|
| 初始化 | 绑定只读输入 | `p0=INPUT_REF(A)` |
| 第一次重连 | 添加 `sA -> w:premise0`、`sr1 -> w:rule`、`w -> sB:write` | 生成当前读取/写入凭据 |
| 第一次推导 | `APPLY_GROUND_RULE(r1,[p0])` | 提交 `p1:B`，依赖 `p0` 和输入规则 r1 |
| 第二次重连 | 删除旧边，添加 `sB -> w:premise0`、`sr2 -> w:rule`、`w -> sC:write` | 新版本运输 B 与 r2，授权写入 sC |
| 第二次推导 | `APPLY_GROUND_RULE(r2,[p1])` | 提交 `p2:C`，依赖 `p1` 和输入规则 r2 |
| 输出 | 验证 `p2` 的完整祖先证据 | `VERIFIED_RESULT(C, proof=p2)` |

输出寄存器绑定由提交动作指定，属于受检查的目标写端口；绑定操作计费，不能隐含全局广播。若一次编辑预算不够，分成多个有界微步完成重连。

对该轨迹删除 `sB -> w` 并重建 inbox，第二次动作必须因缺少当前读取凭据失败；只提交历史 `p1` 也必须失败。若存在另一条合法运输路径，系统可以明确地重新连接并另行成功，这不属于绕过。

输入改为 \(F=\{A\}\)、仅 \(r_1:A\to B\)、查询 \(C\) 时，未找到 C 本身不足以输出负答。可以提交 \(\{A,B\}\) 的闭合集证书并扫描全部规则，独立复验后输出 `VERIFIED_NEGATIVE(NOT_DERIVABLE(C))`。

## 11. 数学性质与证明

以下为抽象规格层面的命题和证明，不是对尚未实现的 C# 内核进行机器形式化验证。每条命题只在列明条件下成立，经验验收不能代替这些条件。

### 定理 T1：拓扑合法性与局部编辑不变量

**假设。** 初态满足第 4 节全部约束；唯一编辑入口为完整检查后原子提交；失败时保持原图；句柄生命周期与槽位操作也在该事务中检查。

**结论。** 任意神经策略输出序列下，所有已提交数据图均满足类型、度数、容量和端点有效性约束；每步数据编辑/槽位事务支持集不超过 \(u\)，数据边编辑数不超过 \(b_e\)。控制探测满足独立有界读取预算。

**证明。** 初态合法。假定第 t 步合法，第 t+1 步要么保持旧图，要么提交已经检查满足全部约束的新图。因此逐步归纳成立。检查完整批次而非逐项先写后查，排除了中间失败留下部分非法状态的情况。该证明不依赖神经策略准确率。□

### 定理 T2：编辑语言具备连接重构能力

**假设。** 固定槽位及兼容端口下，E 与 E* 均合法；合法目标边最终可进入候选；允许至少一个删除/添加；不额外要求保持连通；端点支持预算足够完成一个边操作。只考虑可变边，不删除保护边。

**结论。** 存在有限合法编辑序列把 E 变为 E*。

**证明。** 先删除 \(E\setminus E^*\)，度数只减小；再添加 \(E^*\setminus E\)，此阶段每个中间图都是 E* 的子图，因此不超过其度数上限，且类型兼容。取每次一个操作即可满足局部预算。最多需要 \(|E\setminus E^*|+|E^*\setminus E|\) 个被接受的边操作；候选等待和探测另计，不包含在此上界内。□

该命题证明动作空间的能力，不证明学习到的控制器会重构正确图。永远屏蔽目标边的候选策略不满足假设。

### 定理 T3：任意合法硬拓扑序列下的神经状态有界性

**假设。** \(\|h_i^0\|_\infty\le B\)，新激活槽位也如此初始化；参数与运算为有限实数；更新严格采用式 (7)–(9)。

**结论。** 任意 t 与槽位 i 均有 \(\|h_i^t\|_\infty\le B\)，从而 \(\|h_i^t\|_2\le\sqrt d B\)。

**证明。** tanh 使消息和候选更新逐坐标处于 [-B,B]。式 (8) 是有界消息与零向量的凸组合。式 (9) 每个坐标又是旧值和候选值的凸组合。对坐标和时间归纳即可；拓扑如何切换不改变结论。□

浮点实现需要非有限值检查和最终 `clamp(-B,B)` 维护可表示区间；不能声称抽象实数推导证明了所有 GPU 数值行为。该有界性不蕴含收敛、抗扰稳定、正确推理或梯度不消失。硬选择一般不连续，状态相关门也不自动形成收缩映射。

### 定理 T4：证据 DAG 无环且具有有限根来源

**假设。** 输入根为经过任务解析检查的不可变记录；新事件只引用严格较小的 ProofId；所有精确引用检查身份、revision 与作用域；证据数量有限。

**结论。** 每个已提交结论都有有限、无环的祖先证据结构，其根来自输入或列明的受信操作语义。

**证明。** 沿证据依赖向前追溯，ProofId 严格下降，因此有限集合内不可能形成环，也不能无限下降；最终到达根。槽位 generation 与值 revision 检查排除身份复用导致的伪前提。□

神经图有环不影响这个结论；同步更新读取 H_t、写入 H_(t+1)，按版本展开的执行读取也具有时间方向。

### 定理 T5：提交和正式输出的条件健全性

**假设。** 输入解析符合正式任务语义；每条内核语义操作对该任务保持成立性；候选和假设不会被伪装为无条件事实；精确算术无未检查溢出；提交和最终序列化不能绕过验证。独立输出验证器检查完整输出谓词 \(P_{X,Q}(y)\)。

**结论。** 对确定的正式任务输入，

\[
\operatorname{Verify}(X,Q,y,P)=\operatorname{accept}
\quad\Longrightarrow\quad P_{X,Q}(y).
\tag{14}
\]

**证明。** 输入事实在其任务解释中成立。按事件顺序归纳：每次接受的操作使用已成立的前提且保持所声明语义，因此结论成立。候选值没有被赋予未验证命题地位。最后检查结论蕴含完整输出谓词，并验证将序列化的 y 与被检查的 y 相同，故式 (14) 成立。□

输入必须完整绑定：独立复验读取规范化输入本身及证书；InputDigest 只是辅助。若只通过散列绑定输入，还需明确无碰撞假设。这个定理不是“模型从现实世界获得的前提必然正确”。

### 引理 T5a：Horn 闭合集负证书正确

**假设。** 有限 definite Horn 语义，C 满足式 (13)。

**证明。** 将 C 中原子解释为真，C 外为假。输入事实均真。对每条规则，前提不全真时子句成立；前提全真时闭合性保证结论也真。因此此解释是 \(\Gamma\) 的一个模型，而 q 在该模型中为假，故 \(\Gamma\not\models q\)。等价地，最小闭包包含于 C，查询不在最小闭包中。它不证明 \(\neg q\) 为逻辑结论。□

### 引理 T5b：可达性负证书正确

**假设。** s 属于 S、t 不属于 S，且不存在离开 S 的输入边。

**证明。** 任意从 s 出发的有向路径第一点在 S；若第 j 点在 S，下一点也必须在 S。归纳所有可达点都在 S，故 t 不可达。□

### 定理 T6：已提交步骤的运输来源性

**假设。** inbox 仅由当前有效数据边产生；动作只能选择其合法凭据；运输后到提交前图和读取值不变；按第 8.4 节记录完整凭据、目标写边及 control read set，T6a 的完整前缀/唯一重建条件成立。

**结论。** 每个已提交动作的精确操作数都有在实际执行图中的合法来源；针对同一次快照，删除唯一运输边并重新计算 inbox 后，原动作不能继续接受。

**证明。** 内核对每个操作数检查式 (4)。凭据不存在、图版本不匹配、源值改变或 receiver generation 不匹配均拒绝。删除唯一运输边后没有合法当前凭据，所以原操作数选择失败。目标写边采用同样检查。□

这不是“所有神经信息只来自证据边”的命题：控制探测、目标广播和历史隐状态也是神经依赖，必须如实记录。来源性也不证明模型在统计上有用地利用了每条边；这由第 19 节消融检验。

### 定理 T7：有界运行终止

**假设。** 有限微步预算 T_max，每次循环包括拒绝与 NO_OP 都消耗至少一步；每个内部操作有类型/长度/时间限制并终止；不存在免费重试循环。

**结论。** 一次运行最多执行 T_max 个微步，返回一个协议定义的状态。

**证明。** 剩余整数步数每轮严格下降，不能无限执行。每轮内部终止，故总运行终止。□

终止可以是 UNKNOWN，不等于找到答案。最终复验也需要独立预算；复验超限时不能先返回已验证成功。

### 定理 T8：有限 Horn 域的公平回退条件完备性

**假设。** \(m=|\mathcal A|\)、\(r=|\mathcal R|\) 有限，\(q\in\mathcal A\)；输入属于第 7.3 节，已验证原子集合单调增长且不能被覆盖/回收；事实存储可容纳全部 m 个原子；规则、事实、worker、目标、证据与最终复验均有足够容量/预算；\(k_{in}\ge\ell_{max}+1\)、\(k_{out}\ge1\)。公平回退显式访问每条规则，并以有限最多 \(\kappa\) 个微步完成一次规则检查所需的清边、路由、读取和提交。当前公平操作的路由/值版本受到保护，不允许神经决策无限延迟或破坏它；所分析执行不触发外部中断、数值/资源故障。获得目标推导或闭合集证书后，回退确定性请求输出复验，不依赖学习 STOP。

**结论。** 在足够总预算下，回退能对任意查询 q 返回可导出证据或式 (13) 的负证书。

**证明。** 令 \(C_0=F\)。重复全规则扫描，每个满足前提的规则显式执行并把结论加入当前集合。集合单调增长，每次有变化的扫描至少新增一个原子，因此至多 m 次有变化的扫描。再一次完整无变化扫描后集合闭合。所有已加入原子具有合法推导；闭合集合恰是最小闭包。q 在其中则有正证据；不在其中则该集合满足式 (13)。□

最多 (m+1) 次全扫描，公平工作微步上界可取 \(\kappa(m+1)r\)，另加初始化、证据构造和最终复验成本。若 r=0，直接检查 F 与目标即可。\(\kappa\) 必须根据实际路由和输入长度配置给出，不能把规则扫描视为常数时间免费操作。

该完备性来自公平闭包调度与任务规则，而不是训练得到的参数。它只覆盖本节有限域，并不延伸到一般 CSP、无限一阶逻辑或未知操作语义。

## 12. 公平回退与神经快速模式

`LearnedOnly` 是默认研究模式。它只使用模型提案与普通候选游标，在预算内成功或返回明确状态，不能承诺有限预算完备性。

`FairFallback` 显式启用确定性的参考调度。对于 Horn 闭包，扫描规则并安排具体连接/操作；每至多 p 个神经微步插入一个公平微步，公平序列不会被模型打断或永久回避。回退经过相同硬连接、操作检查、证据提交和输出复验。

公平规则访问是可以跨微步的显式事务：预留 worker、目标槽和所需源值版本；直到该次规则访问成功/明确不满足，神经步不能编辑其保护边、reset 其槽位或改变其输入绑定。神经步只在不冲突区域工作。公平提案的动作、操作数和写目标由调度器确定，`intent.resolve_action` 对该提案直接使用已指定动作，不能再次让神经采样覆盖。也可以选择连续执行整段公平微步后再恢复神经步；两种配置均需计费和记录。

若公平工作需要 L 个微步，一种总预算上界为 (p+1)L，再加固定初始化/复验成本。回退的每个工作单元、冻结的寄存器版本和候选队列都需要计费。

回退不得放在检查器内部，也不能静默开启。它可以保障规定有限任务的完成，但对模型能力报告必须单独标记。支持图可达性等其它完备回退时，另写调度和完整性证明；v1 的 T8 不自动覆盖它们。

## 13. 硬拓扑训练设计

### 13.1 联合策略与教师轨迹

一次联合离散动作包含有限学习意图、候选页选择（v1 为确定性）、活动端点、编辑序列、worker、操作码、操作数指针、目标写端口、候选精确值和停止请求。Draft-2 将决策分为意图、逐边路由和最终操作，条件概率分解为：

\[
\pi_\theta(u_t\mid X,Q,\tau_{<t})
=\pi_\theta(z_t\mid\tau_{<t})
\prod_{j=1}^{n_e}\pi_\theta(e_{t,j}\mid\tau_{<t},z_t,e_{t,<j})
\pi_\theta(a_t\mid\tau_{<t},z_t,\Delta_t,H^+,\mathcal I_t).
\tag{15}
\]

编辑批次按自回归指令逐项采样，记录各项条件概率；不放回采样不能当作互相独立。可变长度批次必须显式采样 `END_EDITS`，并把其条件概率计入式 (15) 的编辑乘积；达到 b_e 上限时强制结束的概率为 1。n_e 包含非强制的结束字段，结束字段不改变图且只计控制成本。NO_OP 和 STOP 有明确概率。结构掩码不执行隐藏语义求解。

意图 z_t 包含 worker、操作族、已曝光的 anchor/操作数候选头句柄、目标写端口和 `CONTINUE/ABORT`。意图最长持续 `MaxIntentSteps` 个计费微步，stage 为 `PREPARE -> ROUTE -> COMPUTE -> COMMIT_OR_ABORT`。选择及续用/中止由学习策略完成；状态机只约束句法、生命周期和期限，不能自动生成正确连接或检验全部语义前提。意图不创建事实，不授权全局精确读取，最终动作仍只能使用当前 inbox。

跨步复用意图时，式 (15) 的意图因子只针对本步真正采样的决定计概率；固定沿用旧字段不重复记 log-prob。候选 pin 保留头句柄，不能替代 token 运输；需要保留精确前提仍按第 4.3 节 COPY/显式寄存器合同执行。意图超期、源版本改变或候选失效必须中止/重新选择并计费。

该分解减少单个分类头规模并允许局部训练目标，不一般性减少联合动作树的叶子数，也不证明探索复杂度或学习收敛改善。解码器不会充当隐含路由规划器。

动作编码必须可实现：操作码/类型/布尔值用有限分类头；句柄、worker 和端口用候选上的掩码分类头；64 位整数和 BitVec 按位自回归生成，不能建立 2^64 类 softmax；集合和赋值采用长度受限的结构化自回归生成。所有字段的条件 log-prob 相加形成式 (15) 的联合 log-prob，生成成本计入实际动作预算。

训练 rollout 独立保存候选页/顺序、子动作/掩码、各条件 log-prob、策略检查点、采样种子、状态版本、接受/拒绝、奖励/成本、终止原因和 BPTT 边界。它用于重新计算策略概率，不必混入正式逻辑证书。

老师算法仅在离线数据生成和训练辅助中使用。老师轨迹包含可执行的拓扑编辑、路由、操作数与证据，而非仅提供最终标签。首版行为克隆使用硬前向：

\[
\mathcal L_{BC}=-\sum_t\log\pi_\theta(u_t^*\mid X,Q,\tau_{<t}^*).
\tag{16}
\]

同一任务生成多条合法序列，随机化 worker 分配与无关编号，避免把唯一教师拓扑当作数学正确答案。已知多种可接受老师动作时，可优化其概率质量之和，但不宣称知道全部最优动作。

### 13.1a 路由与操作分开的训练课程

| 课程 | 训练时提供的脚手架 | 模型必须学习的部分 | 部署边界 |
|---|---|---|---|
| A：局部操作 | 由合法运输轨迹产生的已就绪 inbox | 操作/operand/写目标选择与局部提交 | 不计自主路由或 learned-only 成绩 |
| B：局部路由 | 给定训练意图；必要端点在当前候选页；初图有缺边/错接/端口冲突 | 自己逐边编辑、运输、更新和完成局部证据 | 不由老师自动连边；此课程成绩不是完整求解能力 |
| C：自主联合 | 逐渐撤掉意图/候选提示，使用真实目录/预算 | 自选意图、候选、路由和操作 | 最终必须无提示、无预接线、无回退验收 |
| D：失败恢复 | 对自主 rollout 的实际失败状态离线标注可行恢复轨迹 | 中止错误意图、换路由、处理拒绝与延迟曝光 | 教师只在训练使用，全部额外成本另记 |

必要候选不在真实页时，老师不得在主策略轨迹中免费插入它：等待页、使用公开结构索引或把样本标为 `ExposureMiss`，等待消耗实际微步/目录成本。课程 B 的候选提示是明确标记的 `TrainingScaffold`；随着课程 C 撤除，必须检查这种分布偏移。

辅助头训练端口/receipt 就绪预测、选中规则的前提角色、编辑后一小窗口真实取得所需 receipt 的概率，以及给定 inbox 的操作选择。标签来自已执行合法教师轨迹、已验证替代轨迹或实际反事实运行；单一路径之外的合法替代不能标为“语义错误”。辅助头只提供监督/打分，不能签发证书或成为全局语义合法掩码。

### 13.1b 短期 credit、失败归因与停止扩容条件

按真实子动作前缀使用 baseline：\(b_{t,j}(\tau_{<t},u_{t,<j})\) 可依赖此前字段，不能依赖它要乘的当前字段。依赖 z_t 的 baseline 可用于后续编辑，不能乘到 z_t 自己的 score。baseline 采用 stop-gradient；价值拟合损失另列。

on-policy 主目标使用完整任务回报的 reward-to-go。短路由窗口内 receipt 获得/局部提交监督帮助缩短归因路径，但窗口截断梯度、局部成功奖励和混合 BC 是明确的辅助目标，不冒充 T9 的完整无偏任务梯度。老师和反事实运行消耗的训练成本单独记录。

每次失败依次归因并记录条件成功率：`ExposureMiss`（可行教师方案的必要端点尚未曝光）、`ExposedButUnchosen`、`ChosenButNotRouted`、`ReceiptReadyButWrongOperation`、`ValidProgressThenStall`、`PrematureStop`。归因优先考虑已知多种可行方案；只缺某一老师路径不证明没有其它解。部署能直接记录可观测失败；需要 oracle 的归因仅离线评测。

预先固定短路由集、训练预算和检查频率。若无提示的自主路由准确率长期低于第 19 节门槛，停止扩大任务/模型规模，先诊断曝光、编辑或操作学习。强验证器和 FairFallback 高成功率不能让该学习验收通过。

### 13.2 精确动作的梯度边界

连续状态和 logits 使用 BPTT；硬编辑、整数操作、语义接受/拒绝不具有普通连续梯度。主训练路线采用行为克隆预热和 on-policy 策略梯度，所有部署/测试采用硬连接。

软连接、Gumbel-Softmax 或直通估计可作为另行标记的辅助实验。有限温度优化松弛目标；直通估计通常有偏，不具有本规格下一节的无偏结论。[Gumbel-Softmax 原论文](https://arxiv.org/abs/1611.01144)

### 13.3 命题 T9：有界硬轨迹策略梯度

**假设。** 训练轨迹步数有界；每个输入的离散动作空间有限（整数位宽、候选值长度等均有界）；对给定离散历史，候选、合法支持、精确转换和终止条件与 theta 无关；允许动作概率可微且为正；奖励只依赖离散轨迹、证书结果和确定性计费，训练终止不依赖实际墙钟时间。潜状态 H_theta 根据该离散历史重建，其对 logits 的依赖完整反向传播；所分析参数邻域内神经运算有限并满足可微假设，不触发参数相关数值失败。

\[
J(\theta)=\sum_\tau p_\theta(\tau\mid X,Q)R(\tau),\qquad
\nabla_\theta J
=\mathbb E\left[R(\tau)\sum_t\nabla_\theta\log\pi_\theta(u_t\mid\tau_{<t})\right].
\tag{17}
\]

**证明。** 有限轨迹和允许交换有限和与导数。利用 \(\nabla p=p\nabla\log p\) 和式 (15) 的乘积分解即得式 (17)。在当步动作选取前确定的 baseline b_t 有条件期望 \(b_t\sum_u\pi(u)\nabla\log\pi(u)=b_t\nabla1=0\)，因此 stop-gradient baseline 不改变估计期望。□

这只证明梯度估计的期望性质，不保证低方差、能力获得或全局最优。参数相关硬 top-k 丢弃候选、忽略候选采样概率、离策略数据不校正、截断 BPTT、裁剪梯度或参数相关浮点失败/硬阈值终止，均不能直接沿用完整无偏结论。实际运行仍执行数值检查并报告失败，不能以理论假设掩盖失败轨迹。[REINFORCE 原论文](https://doi.org/10.1007/BF00992696)

建议初始回报：

\[
R=r_{cert}\mathbf1[\text{独立证书验证成功}]
-\lambda_s N_{step}-\lambda_e N_{edit}
-\lambda_v N_{check}-\lambda_b C_{exact}.
\tag{18}
\]

拒绝、NO_OP、输入编码、候选扫描和最终复验都计费。训练初期成本系数较小，避免模型学会从不重连或过早停止。步骤奖励不能把重复证明同一事实变成无限获利；Horn 新事实去重，已存在事实可复用但不重复获得进展奖励。

### 13.3a 命题 T9a：有限轨迹潜势整形的目标不变性

为降低只在末端得到反馈的工程困难，可以试验固定潜势 \(\Phi(s,t)\)，s 仅含声明的离散历史/任务状态和剩余预算；不得依赖当前 theta 或实际墙钟。所有终态，包括成功、UNKNOWN、超限和主动中止，均定义 \(\Phi=0\)。对无折扣回报定义：

\[
r'_t=r_t+\Phi(s_{t+1},t+1)-\Phi(s_t,t),\qquad
\sum_{t=0}^{T-1}r'_t=\sum_{t=0}^{T-1}r_t-\Phi(s_0,0).
\tag{19}
\]

**证明。** 势差逐项望远镜消去，终态项为零；初态由输入和初始预算固定，对该输入的所有策略仅减同一常数，因此策略的原始目标排序不变。□

使用折扣 gamma 时势差须改为 \(\gamma\Phi(s_{t+1})-\Phi(s_t)\)，按同一折扣求和并处理终态。这里给出的是本规格有限轨迹条件下的直接推导，可参考潜势整形原始研究。[Policy invariance under reward transformations](https://ai.stanford.edu/~ang/papers/shaping-icml99.pdf)

精确 Monte Carlo reward-to-go 下该潜势差等价于当前状态的 baseline；它本身不保证探索或降低估计方差。TD/近似价值训练可能改善工程反馈，但不自动继承完整无偏结论。非潜势的每次就绪/新事实奖励、短窗口目标或 teacher 可见提示，须明确标为不同辅助目标。若潜势计算需老师求解，其使用仅限训练且计费；部署不获得该老师信息。

### 13.4 可执行训练阶段

| 阶段 | 训练内容 | 离开本阶段的必要检查 |
|---|---|---|
| P0 | 内核、运输、目录、老师轨迹与独立复验 | 老师硬轨迹可完整重放；伪证据被拒绝 |
| P1 | 课程 A/B：先局部操作，再局部路由；硬轨迹 BC 和局部辅助头 | 两个条件成功率分别通过；不把脚手架成绩报为完整自主成绩 |
| P2 | 课程 C/D：撤脚手架、自主 rollout、真实失败恢复和曝光延迟 | 无老师/无预接线自主路由通过门槛；分布偏移错误减少 |
| P3 | on-policy 硬动作采样、完整有界 BPTT/策略梯度 | 认证覆盖提升；无提前停止投机 |
| P4 | 冻结检查点与超参数，保留测试和消融 | 满足预注册验收才能发布能力结论 |

P2 可参考数据聚合思路，但不直接继承额外假设尚未满足的理论保证。[DAgger 原论文](https://proceedings.mlr.press/v15/ross11a.html)

训练的老师、随机种子、优化器、学习率、batch、展开步数、最大输入和成本权重必须随检查点存档。具体超参数由原型测量确定；本规格不预先承诺样本量或 GPU 训练时长。

## 14. 非自然语言预训练与泛化划分

任务实例使用强类型 IR，而非自然语言 token：`Bool`、`BoundedInt`、`BitVec(width)`、`Symbol`、`Atom`、`Rule`、`Vertex`、`Constraint`、`Assignment`。原子和对象的身份采用会话内句柄；身份相等由精确层检查，神经向量相近不表示相等。

首批数据生成覆盖链、分叉、汇合、环、冗余规则、无关干扰、多个合法证据、无解/不可导出样本及长度变化。CSP 首版先采用有限布尔域和有明确操作码的约束，例如 CNF 子句和 equality。操作码语义是架构契约，不是模型凭空发现的未知数学运算。

每个样本保存生成器版本、结构种子、参数种子、重命名种子、规范化输入和独立老师结果。老师输出不混入部署输入。

| 数据划分 | 具体控制 | 可以支持的结论 |
|---|---|---|
| ID | 同任务族、独立结构实例 | 已知分布的新样本能力 |
| 规模外推 | 原子/顶点/变量数变为 2 倍、4 倍 | 在容量允许下的尺寸外推 |
| 深度外推 | 增加必要证据或搜索深度 | 更长计算的能力 |
| 结构保留 | 按依赖图/程序模板分组再划分 | 未见依赖结构能力 |
| 重命名 | 随机重排身份与规则顺序 | 对无意义编号变化的鲁棒性 |
| 组合保留 | 基本操作已见、组合方式未见 | 组合泛化 |
| 新规则输入 | 输入中提供支持 IR 表达的新规则 | 已定义操作语义内的新规则处理 |

同一结构改编号不能同时出现在训练和“新结构”测试。生成器模板须先分组划分，再实例化。重命名鲁棒性是测试目标；在具有物理槽位 tie-break 和顺序游标的实现中，不默认宣称严格置换等变。

需要进一步研究“从示例归纳规则”时，增加 `(support examples, query)` 任务族：学习提出规则候选，再在明确范围上检验。有限示例吻合不能证明规则在所有输入上成立；该能力不是 v1 现有健全性定理的直接推论。

## 15. 计算与内存成本

令 N 为活槽位数量，E 为当前数据边数，a 为本步候选目标数量，c 为每目标候选数，d 为隐维度，q 为关系低秩维度，L 为精确操作数/证据长度。

候选索引初次构建成本单独计入输入编码；有序桶每次页查询可采用 \(O(a(c+\log N))\) 上界，另计 epoch 构建/旧版本保留、桶管理和索引更新。实际扫描/tombstone 数不能隐藏在“最多 c 个返回候选”中。不允许通过全对相似度生成候选后声称这个上界。

v1 默认对全部活槽位同步更新。关系投影缓存按源节点/关系计算，门和聚合沿实际边计算，一步神经成本可写为：

\[
C_{neural}=O(Nd^2+N|\mathcal T_{edge}|dq+(E+ac)d).
\tag{20}
\]

式 (20) 假设每候选/每边评分使用输入维度 O(d)、固定小宽度的轻量评分器；若评分器隐藏宽度与 d 同阶，则候选评分项应改为 O(ac d²)，必须按实现实际报告。也不得把“每边完整矩阵乘法”误报成 O(Ed)。

局部事务使用受影响节点的邻接表增量检查，成本依赖 b_e 与度数，不应每步克隆整张图。精确检查成本单列 \(C_K(L)\)，大整数位长、闭合集全扫描和证书复验不能当作常数。

\[
C_{step}=C_{directory}+C_{probe}+C_{edit}+C_{neural}+C_K(L)+C_{journal},
\tag{21}
\]

\[
M=O(Nd+Nk+ac)+M_X+M_R+M_P+M_{journal}+M_{directorySnapshots}+M_{training},
\tag{22}
\]

其中 k 代表边度数上限量级。训练 BPTT 需保存展开状态和动作；模型参数规模与计算深度、图规模分别统计。有限槽位不意味着无限整数/证据能免费存储，因此 M_R、M_P 和精确位宽都有独立上限。

完整 journal、规范编码、落盘/摘要和独立重放成本均实际计费；不能裁日志换取更好的正式性能成绩。逻辑计费采用 manifest 固定的 `WorkCostSchedule`（微步、扫描条目、probe、编辑、精确长度、事件/字节等）；墙钟时间、GPU测量另列，不能声称由逻辑证书证明这些测量真实。

实时性能必须测量 CPU/GPU 同步、数据搬运和不规则 gather/scatter。减少边数不能推出实际加速；v1 不承诺低于 Transformer 的延迟或能耗。

## 16. ConsDTRv1 初始配置与存档契约

以下为首个原型起点，不是已经验证的最优设置。

| 配置项 | 起点/约束 |
|---|---|
| `ArchitectureId` | `ConsDTRv1` |
| `SpecRevision` / `JournalSchema` | `Draft-2` / `ConsDTRv1-Replay-Draft-2`；旧稀疏日志不满足正式完整重放 |
| `HiddenDim` / `RelationRank` | 128 / 32；局部编码维度 q=32 |
| `ScoreHiddenDim` | 16，固定小宽度；关系投影/局部投影按源节点与关系缓存 |
| `HiddenBound` | B=1 |
| `MaxSlots` | 256，包含输入、规则、原子寄存器、worker 和目标 |
| `WorkerSlots` | 8，首版每步最多一个语义提交 |
| `MaxInDegree` / `MaxOutDegree` | 8 / 8 |
| `MaxRuleArity` | 4，因此 worker 可接收规则+全部前提 |
| `MaxActiveCandidateTargets` | 4 |
| `CandidatesPerTarget` | 16 |
| `StructuralCandidates` / `ExistingOrPinnedCandidates` / `FairCandidates` | 4 / 8 / 4，互不抢占公平配额 |
| `FairTargetsPerStep` | 1；其余最多 3 个目标服务当前意图/策略 |
| `CandidateEpochTargetMode` | 明确列出全部结构合法目标；worker-only 子模式单独标记 |
| `MaxIntentSteps` / `PinnedCandidatesMax` | 16 / 8；超期中止，pin 不授权精确访问 |
| `MaxEditEndpoints` / `MaxEdgeEditsPerStep` | 16 / 8 |
| `MaxSlotOpsPerStep` | 2 |
| `TrainingUnrollSteps` | 32 起步，按课程提高并如实记录截断 |
| `InferenceMaxSteps` | 512 起步；长任务调整，不能据此保证完备 |
| `ExactIntegerWidth` | 首版 64 位 checked 算术；BitVec 单独声明模语义 |
| `MaxProofEvents` | 4096 起步，且额外限制总证书字节数 |
| `MaxJournalEvents` / `MaxJournalBytes` | 262144 / 64 MiB 原型起点；不足时不能签发完整正式证书 |
| `CertificateReplayPolicy` | genesis-to-seal 全前缀、独立可信 EndAnchor；不接受 exporter 自选相关日志 |
| `ExecutionMode` | `LearnedOnly`；回退须显式配置 |

总槽位预检：输入规则槽+所需原子/值槽+worker 槽+目标槽不得超过 MaxSlots。T8 所需全部原子寄存器必须预留；不能在推理途中用“容量不足”覆盖完备性假设。

证书总字节上限、精确对象长度、候选值长度、目录/epoch预算、最终复验/封口预算、日志编码/费用表、可信锚配置、内核版本、任务语义版本和参数 dtype 都是必填 manifest 字段；缺省时拒绝加载，不能解释为无限。参数量由实际模块精确统计，不从 HiddenDim 猜测一个 SLM 大小。

模型文件保存：架构和 manifest、全部 theta、输入类型规范、内核语义版本、训练配方、检查点标识和数据划分指纹。工作区/图是会话状态，不默认随模型权重跨请求保存。会话恢复另存完整状态版本并重新检查所有引用。

## 17. 当前仓库的 C# / TorchSharp 实现映射

当前仓库配置为 `ConsDTR.Lib` 与 `ConsDTR.LAB.Host`，目标框架 net10.0；库项目已声明 `TorchSharp-cuda-windows` 0.107.0。这里是读取到的项目配置，不表示 CUDA 已成功初始化或训练已运行。TorchSharp 官方项目提供 .NET 的张量/神经网络接口。[官方仓库](https://github.com/dotnet/TorchSharp)

建议职责划分如下；这些为待实现模块，不是现存 API。

| 模块 | 职责 | 必须禁止的旁路 |
|---|---|---|
| `Core/TopologyState` | 代际句柄、边表、原子局部事务 | 直接替换整图绕过约束 |
| `Core/CandidateDirectory` | 类型索引、页游标、受限探测 | 隐藏全对检索或语义求解 |
| `Core/IntentState` | 有限学习意图、候选pin和超期中止 | 自动代替策略规划正确路由 |
| `Neural/ConsDTRModel` | 编码器、稀疏处理器、编辑/动作策略 | 直接返回已验证答案 |
| `Runtime/TokenTransport` | inbox、精确凭据、目标写边 | 根据任意 proofId 全局读写 |
| `Kernel/VerifiedKernel` | 检查具体操作并原子提交 | 隐藏搜索、信任潜状态命题 |
| `Certificates/ReplayVerifier` | 无权重、无老师结果的独立复验 | 信任在线 accept 标记 |
| `Runtime/RuntimeJournal` | 原子提交、完整连续日志、版本和费用 | 可绕过日志的改图/绑定入口 |
| `Certificates/SealedExporter` | 仅序列化权威封口前缀 | 自选cut、裁事务、自签可信锚 |
| `Tasks/*` | 输入语义、操作及任务生成器 | 每任务偷偷使用不同神经处理器 |
| `Training/*` | 老师轨迹、BC、rollout、策略梯度 | 测试时调用老师 |
| `Evaluation/*` | 消融、成本、覆盖率与泛化报告 | 混合回退与 learned-only 成绩 |

接口形状示意：

```csharp
readonly record struct NodeHandle(int Slot, uint Generation);
readonly record struct ProofId(long Value);
readonly record struct TopologyRevision(long Value);

interface ITopologyTransactions { /* ValidateWholeBatch, Commit */ }
interface ICandidateDirectory { /* NextPage, AuditReads */ }
interface ISparseNeuralProcessor { /* ForwardAlongActualEdges */ }
interface ITokenTransport { /* BuildInbox, ValidateReceipts */ }
interface IVerifiedKernel { /* CheckOneAction, CommitAccepted */ }
interface ICertificateVerifier { /* VerifyFromInputAndCertificate */ }
interface IRuntimeJournal { /* AtomicAppendAndCommit, Seal */ }
interface IExpectedEndAnchor { /* ObtainedOutsideExporter, VerifyAuthority */ }
interface IFairScheduler { /* ExplicitNextMicrostep */ }
```

精确对象、证据和图事务由 CPU 的强类型不可变对象管理；连续模型由 TorchSharp 张量执行。证书验证不读取神经权重。禁止用 C# 弱类型字典里的字符串相等代替未定义操作语义，也禁止神经分支修改受信事实。

硬动作和 CPU/GPU 同步可能成为瓶颈，先保证正确性再优化批量执行。TorchSharp 中具体稀疏算子、autograd 能力和 GPU 支持须在实现时通过最小实验确认；本规格不依赖某个未经验证 API 的存在。

## 18. 正确性与协议验收

必须完成以下实现检查，数学证明不能代替代码检查：

1. **结构属性测试**：随机合法/非法事务，检查入出度、类型、支持集合、预算和原子回滚。
2. **身份测试**：释放/复用槽位、错误 generation、陈旧 valueRevision、未来 ProofId、跨会话引用均拒绝。
3. **真正运输测试**：删除唯一前提边或写边并重建 inbox，原动作必须失败；硬稀疏与 DenseReference 在同图同权重下消息结果一致至声明的浮点容差。
4. **语义篡改测试**：错规则、伪事实、缺前提、自引用、错误查询、错误输入绑定、溢出及类型冒充不得接受。
5. **负证书测试**：闭合集漏规则结论、割集漏一条离开边、不完整赋值及新赋值复用旧检查均拒绝。
6. **独立复验**：从输入和证书重建结论；更换神经参数不改变同一 ProofDAG 的纯 SemanticVerified 结果，checkpoint/ExecutionBound 另检。禁止只是调用在线检查器两次冒充独立实现。
7. **输出测试**：UNKNOWN 不变成负答；有效路径不变成最短；满足赋值不变成 UNSAT；序列化输出与被验证对象一致。
8. **资源测试**：NO_OP/拒绝也耗步，证据容量和整数长度受限，最终复验超限不返回 VERIFIED。
9. **核规模审计**：明确受信代码和操作表；检查器不得调用全局求解器。回退模式只能通过显式入口启用。

10. **曝光合同测试**：静态桶、目标饥饿、删除/reset、登记新条目、重复项、空桶和目录预算耗尽；T2a 假设成立时实际首次曝光不超过条件上界。索引更新不得重启 epoch，公平配额不得被 pin 抢占。
11. **重放删改攻击**：构造包含 ADD/REMOVE/RETYPE、拒绝、NO_OP、reset/reuse、probe、绑定变化及两个语义提交的完整轨迹，分别删除每一类记录（包括与输出祖先无关的事务）、截尾、重排、重复 seq、篡改 pre/post revision、制造同 revision 不同图、复用旧 edgeRevision/generation、跨会话 receipt、替换绑定、漏 STEP_END/释放、删失败降低费用、降级 mode 或替换 checkpoint；在相同权威 EndAnchor 下全部拒绝。
12. **锚与封口测试**：自签 head、错误 authority、公钥未配置、cut 早于引用、封口后改输出/图、任意相关日志子集与未验证 checkpoint 均不通过正式模式。测试恢复后仍是同一连续前缀、日志满时不会先提交再省略。
13. **完整性与语义分离测试**：有效 ProofDAG 配合不完整 journal 可以得到单独 SemanticVerified，但不能得到正式运行 VERIFIED；换神经参数不改变逻辑证书真假，却可能使会话 checkpoint 绑定校验失败，两种结果分开。

有限测试中的零误接受不能证明无限域正确。将来可用证明助手形式化任务语义、事务规则与 T5，再证明实现满足抽象规范；在此之前明确标记“规格层证明 + 实现验证”，不能写成“已机器证明 C# 正确”。

## 19. 能力、拓扑贡献与实验验收

### 19.1 必报指标

\[
\mathrm{CertifiedCoverage}_{learned}
=\frac{\text{预算内 learned-only 且独立复验成功实例数}}
{\text{全部测试实例数}}.
\tag{23}
\]

分别报告各任务/难度下的覆盖率、UNKNOWN 比例、拒绝动作数、编辑数、候选扫描量、证据长度、总操作成本、实际延迟/显存、至少 5 个训练种子的差异。已接受答案的正确性与覆盖率是不同指标；正确性门可以很可靠而模型几乎不会解题。

Draft-2 增加链式诊断：必要候选曝光率（oracle 仅离线）、曝光后选择率、选中后 receipt 齐备率、receipt 齐备后操作正确率、局部成功后完整任务完成率；首次曝光等待分布、EpochExposureBound、扫描/维护成本、目录时间占比、pin 命中/失效、意图中止及长停滞。不能用“检查器接受准确率”替代这些学习指标。

CSP 还必须报告 `SATWitnessCoverage = 预算内 learned-only 取得已复验赋值的 SAT 样本数 / 独立评测 oracle 确认可满足的样本数`。oracle 只用于评测分组，不能提供给模型。因为 v1 没有 UNSAT 证书，混合 CSP 总体认证覆盖率的上限就是 SAT 样本比例；无解压力样本要求无错误 SAT 接受并明确返回 UNKNOWN，UNKNOWN 不计为成功负判。

FairFallback 的调用率、介入前 learned-only 成果、额外成本和最终成功单独列出。OracleDiagnostic 仅用于调试；它不得出现在正式模型成绩中。

### 19.2 必须的对照与干预

| 对照/干预 | 检验问题 |
|---|---|
| 固定图循环网络，重新训练 | 动态连接是否超过相同循环计算的收益 |
| 仅输入时建图，之后冻结 | 推理中持续编辑是否必要 |
| 同预算随机合法编辑 | 学到的编辑是否优于随机变化 |
| 冻结检查点禁止编辑 | 当前模型是否实际依赖重连 |
| 保持类型/度数的消息打乱、合法重接 | 消息内容与具体连接是否有因果贡献 |
| 循环 Transformer、常规 Transformer | 相近预算下的整体架构差异 |
| 随机提议+同一检查器 | 是否只是验证器和随机搜索在解题 |
| 明确的确定性老师算法 | 与参考算法的成本和正确性差距 |

证明运输测试允许刻意删断必需边；性能消融则须尽量保持合法结构和预算，不能只靠制造非法类型让对照失败。重新训练对照与冻结模型干预回答不同问题，均须报告。

模型使用相同结构化输入、相同输出证书合同和相近预算；传统 Transformer 若仅被要求输出最终标签，不能与 DTR 的证书任务直接比较。分别按参数、实际神经 FLOPs、完整系统成本和延迟作比较，无法同时精确匹配的项目如实说明。

### 19.2a 拓扑贡献分三级验收

**一级：协议必要性。** T6 的唯一边删除导致拒绝，仅证明运输门没有旁路。不能把这类必然拒绝当成“模型学会了有价值的动态拓扑”。

**二级：同一模型的策略干预。** 冻结 theta，从相同输入和同一会话快照（H/G/R/P、预算、目录epoch/cursor/pin、意图和随机流）分叉，干预 `Editor=Learned|Frozen|RandomLegal`。使用预注册的共同预算 B 与完整费用表，衡量：

\[
\Delta_{editor}(B)=\mathbb E[
Y_{Learned}(B)-Y_{Control}(B)],\qquad
Y=\mathbf1[\text{完整证书在预算内通过}].
\tag{24}
\]

干预后仍运行相同目录算法、配额与目标模式；变化后的合法支持可以因离散历史而不同，不能声称两条轨迹永远有相同候选。额外诊断使用仅由输入/外生种子产生、各策略一致可见的固定候选磁带，以隔离目录变化；它不是正式部署成绩。

冻结初始空图导致任务必败不能作为主要证据。分别报告全体实例，以及独立离线诊断确认“该冻结图在相同资源合同下存在合法完成轨迹”的子集；也可在计费的一次输入建图后冻结。诊断轨迹不能给模型。若固定图不存在合法路由，只能报告表达/路由容量差异，不能宣称同可行空间中学习编辑更好。

**三级：架构比较。** 从头训练固定图循环处理器、一次建图处理器、随机合法编辑和动态编辑，匹配数据、调参机会、连续处理器容量、递归步数、目录、精确内核与证书合同。固定图可采用能够运输操作数的有界度静态通信骨架，不能刻意断开必需来源。至少五个种子、保留结构/规模测试、配对差异与置信区间。该收益是所测任务和预算下的经验架构收益，不是普适最优性。

### 19.2b 分离神经消息、精确路由、目录和验证器贡献

从相同合法会话快照起步，保持同一种精确 token 运输机制和预算合同，分别关闭跨边连续消息、打乱消息内容、仅保留局部循环状态；后续图、精确值和行为按各自策略自然分叉，不能声称全程相同运输轨迹。固定图轨迹重放只作为局部消息诊断，不能注入原分支未来 token 强制成功。额外改变目录为“仅公平宽桶/加入结构索引”。随机提议与 learned 提议使用同一个局部检查器，以区分循环计算、神经消息、精确运输、索引寻址和验证器拒绝。

若关闭跨边连续消息没有统计可辨的退化，则目前仅证实拓扑对精确运输/路由的贡献，未证实动态神经消息计算的独立收益；不能据编辑动画或验证率声称后一收益。若只有结构索引改变带来改善，报告目录收益。若随机提议+检查器已经解释主要覆盖率，报告有限神经增益。

随机编辑必须满足类型、度数和端口规则，并拥有与学习编辑相同预算/候选信息，不能以非法编辑作为对照。所有模式的失败尝试、候选探测、拒绝检查和完整日志成本都计入；检查器不得代替对照补全证书。

### 19.3 原型的预注册研究门槛

以下是拟定的研发目标，不是理论保证或已经达到的结果：

- 所有老师/模型正式证书通过独立复验，定向故障集没有误接受。
- learned-only ID 的 Horn/可达性总体认证覆盖率、CSP 的 SATWitnessCoverage 分别以 95% 为目标；混合 CSP 的总体覆盖率另列，不能沿用 95% 门槛。
- 容量允许的 2 倍规模和保留结构测试对应上述可证答指标，以 90% 为初始目标，4 倍规模先报告完整曲线；负样本、正样本及 CSP 无解压力集分列。
- 至少 5 个独立训练种子；与关键固定图对照的配对差异报告 95% 置信区间。
- 只有同预算改善的区间下界大于 0，才初步声称动态拓扑有收益；统计方法、样本量和比较项须事先固定。
- 实测总延迟和显存；若编辑成本抵消收益，如实记录。

Draft-2 的额外发布门槛：

- 固定预算的短自主路由集，在所有必要候选已按公开合同曝光的条件下，以 95% 局部认证完成率为初始目标；完整无提示目录任务另外报告，不能只筛选可见样本粉饰覆盖。
- 训练预算、连续未改善检查次数和扩容停止规则预先存档。路由长期学不会时验收失败，即使内核/回退完全正确也不能发布“预训练路由能力”。
- 在静态、注册/删除、目标饥饿测试中通过 T2a 的条件曝光验收；有明确结构线索的扩展集必须报告索引收益和目录开销，无线索集保留最坏访问下界。
- 关键二级干预及三级重新训练比较中，预注册预算点的改善区间下界需大于 0，才能声称动态编辑有独立收益；多重比较处理预先固定，不只选最好预算点。
- 要声称“动态神经消息推理”收益，还需通过第 19.2b 的消息干预；仅精确 token 路由收益单独命名。
- 完整 prefix、版本唯一性、权威锚和删改攻击检查均通过；任何有效输出配不完整 journal 都不能取得正式运行 VERIFIED。

失败时保留失败集和原因，不更换测试分布掩盖退化。如果随机提议/公平回退贡献占主导，应报告神经启发式的有限价值，不能归因为预训练通用推理能力。

## 20. 实现顺序与版本冻结

1. 建立任务 IR、精确输入解析、语义操作和独立证书验证器；手写重放第 10 节轨迹。
2. 建立硬边表、句柄代际、运输凭据、原子事务、完整 RuntimeJournal/EndAnchor 和预算；先通过日志删改及删除边拒绝检查。
3. 实现共享神经单元、结构索引/公平曝光、有限意图和策略；先以 DenseReference 对照硬边后端，测候选成本。
4. 先通过分开的局部操作/路由课程和无提示短路由门槛，再扩大 ground Horn 深度与干扰；加入路径/CSP，维持共享处理器。
5. 硬 rollout 与策略梯度；证书检查接入真实奖励；分别验证公式假设和实际截断策略。
6. 做 learned-only 基线和因果消融，再实现独立标记的 Horn 公平回退验证 T8。
7. 完成保留测试、成本统计和模型存档，达到验收后冻结 ConsDTRv1 检查点与接口。

首版不同时引入逐标量拓扑编辑、无限节点增长、未经定义的自动规则发明、任意分支回退、多机内核和在线可塑权重。这些会改变可证明性质、训练支持集或资源模型，须作为后续版本单独设计。

## 21. 相关研究与本设计的关系

本规格中的定理由以上定义和假设直接推导。以下论文为机制参考，不是 ConsDTRv1 的已验证性能证据，也不构成架构新颖性证明。

| 原始来源 | 可参考内容 | 本规格额外规定 |
|---|---|---|
| [Dynamic Graph CNN](https://arxiv.org/abs/1801.07829) | 特征空间中的动态邻接与消息计算 | 持久图状态、有限局部编辑、操作数运输和证据提交 |
| [A Generalist Neural Algorithmic Learner](https://proceedings.mlr.press/v198/ibarz22a.html) | 共享 GNN 处理器学习多个算法 | 硬路由执行和任务证书合同 |
| [Learning Discrete Structures for GNNs](https://proceedings.mlr.press/v97/franceschi19a.html) | 学习离散图结构 | 推理过程中的动作化拓扑事务 |
| [PonderNet](https://arxiv.org/abs/2107.05407) | 学习计算步数 | STOP 仅提出检查请求，不能替代证书 |
| [Gumbel-Softmax](https://arxiv.org/abs/1611.01144) | 离散选择的连续松弛 | 明确松弛目标与硬动作目标区别 |
| [REINFORCE](https://doi.org/10.1007/BF00992696) | 离散随机策略梯度 | 明确 T9 的支持、预算和反向传播条件 |
| [Policy invariance under reward transformations](https://ai.stanford.edu/~ang/papers/shaping-icml99.pdf) | 潜势奖励整形 | T9a 明确所有终态归零，不把整形当学习保证 |
| [DAgger](https://proceedings.mlr.press/v15/ross11a.html) | 数据聚合与自主状态分布 | 不自动引用未满足假设的学习保证 |
| [HRM](https://arxiv.org/abs/2506.21734)、[TRM](https://arxiv.org/abs/2510.04871) | 小型循环/递归推理模型 | 作为基线，不把特定任务结果延伸为通用智能 |
| [CLRS 官方仓库](https://github.com/google-deepmind/clrs) | 算法数据、处理器和评测参考 | 需映射到 DTR 明确输入与证书语义 |

## 22. ConsDTRv1 与 DTR 理想的对齐检查

| DTR 理想 | v1 对应的实质机制 | 如何发现没有真正做到 |
|---|---|---|
| 预训练获得推理能力 | 共享处理器、拓扑/动作联合策略、程序生成数据 | 老师/回退不介入时覆盖率很低 |
| 推理时局部改变神经拓扑 | 块有效权重、硬边表、有限事务 | 删除边后消息/操作数仍能原样通过 |
| 非自然语言 | 强类型 IR 与精确操作语义 | 部署实际依赖语言模型解释/补全 |
| 推理过程可变化 | 有状态连接、循环更新、重新选择操作 | 仅输入时建图即达到相同效果 |
| 显式推理 | 实际提交事件、前提与执行证据 DAG | 只输出注意力图或事后文字解释 |
| 一定保障 | 条件健全性、负证书、终止和有限域回退 | UNKNOWN 被包装成否定或完成 |
| 架构可扩展 | 权重共享、容量/预算独立、统一任务包接口 | 每加一个任务需偷偷换一套网络/求解器 |
| 有数学基础 | T1–T9、T2a/T6a/T9a 的假设、证明与实现义务 | 把有界性、证书有效性说成 AGI 证明 |

### 22.1 Draft-2 的四项风险闭环

| 风险 | 必须实现的改进 | 验收失败意味着什么 |
|---|---|---|
| 硬策略难学 | 有限意图/逐字段策略、分开操作与路由课程、真实硬反馈和失败恢复 | 不能由强验证器推断 learned-only 能力 |
| 候选饥饿/扫描主导 | 公共结构索引、不可抢占公平配额、epoch 不重启、曝光/成本上界 | 曝光不足或目录过贵时明确 UNKNOWN/成本瓶颈 |
| 拓扑贡献不独立 | 同快照干预、可行固定图、重新训练比较、神经消息单独干预 | 无收益则不发布对应贡献声明 |
| exporter 选择性日志 | 权威 genesis-to-seal 完整前缀、独立版本/锚、唯一重建与删改攻击 | 语义有效也不能获得完整正式运行证书 |

**版本冻结原则：**只有实际神经计算图被编辑、硬边影响执行、提交证据具有合法来源、正式输出通过独立复验，并且 learned-only 的能力经过对照实验，才可以发布实现的 ConsDTRv1。此文档完成的是架构设计；它不预先宣称这些实现与训练验收已经完成。

## 附录 A. Draft-1 设计阶段的复核记录

本规格经过三个独立审查方向：数学证明、执行机制、训练/验收一致性。审查修复包括数值失败检查顺序、写入凭据、reset 删边预算、控制探测与数据消息区分、公平路由保护，以及 CSP 正样本覆盖率分母。

设计阶段另外执行了以下有限枚举核对：

- 三个原子的全部事实集、最多两条非空前提 Horn 规则，共 5568 个查询实例；闭包推导与直接真值模型枚举一致，5052 个负证书实例满足正确语义。
- 三个顶点的 64 个无自环有向图；闭合割集负证书与独立可达性计算一致。
- 三个节点、两种关系类型、入出度各不超过 1 的 65 个合法图；4225 个有序图对均能按“先删差异、再加目标”经过合法中间图重构。
- UTF-8、章节/公式编号、Markdown 代码围栏和显示数学分隔符完整性检查通过。

这些核对只检查有限规格实例，不是 ConsDTRv1 实现测试、无限域证明或证明助手验证；本次未训练模型、未运行 CUDA，也未产生能力/性能测量。

## 附录 B. Draft-2 修订与核对记录

本次保持架构名称 ConsDTRv1 和规格版本 1.0，将草案状态改为 Draft-2。新增 T2a 的条件曝光上界、T6a 的唯一重建合同和 T9a 的有限潜势整形证明；对原定理的假设和实现入口同步修订。全部 24 个公式按阅读顺序重新编号，旧公式引用同步更新。

三个独立审查方向分别检查训练/因果验收、候选目录、日志重放。必要修复包括：空桶和维护暂停计入曝光上界；绑定/索引元数据版本纳入快照；pin 和优先查询不引入未记录的参数相关硬筛选；END_EDITS 概率；消息干预后的轨迹自然分叉；独立 terminal meter 与封口前固定配额扣除；预复验拒绝的显式解冻；RESET/ACTIVATE 后读取正确代际隐状态。

设计阶段执行的新增有限核对：

- 8,448 组候选曝光参考调度实例，包含不等长桶、空桶、计费 tombstone 和维护暂停；持续有效候选按条件上界曝光。
- 最小确定性 journal 参考模型中，73 组删除、重排、重复、revision 篡改、截尾和重新计算摘要的攻击，在独立固定权威锚下均拒绝。
- 刻意重写并重新计算摘要的较短自洽日志能通过它自己的新锚，但不能通过原权威锚；此例确认“证书自带 head”不足以证明完整性。
- 文档的 UTF-8、22 个主章节、24 个公式、代码/数学围栏、必需协议字段和 Draft-2/架构版本一致性检查通过。

参考模型只验证这些有限协议实例，不是生产 RuntimeJournal、DTRCanonical 编码器、C# 内核或神经训练测试，也不证明真实机器执行。四项风险得到具体缓解机制、条件证明和可拒绝的验收标准；探索效率、规模性能与拓扑收益仍须真实训练/实现实验确认。
