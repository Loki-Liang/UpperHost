# OpenHands 接入

简体中文 | [English](openhands.md)

OpenDeviceStudio 已加入 OpenHands V1 / Agent Canvas 仓库级配置。OpenHands 只作为开发工具，不是 OpenDeviceStudio 运行时依赖。

## 分层治理

OpenHands 是默认实际开发执行器。治理按职责拆分：

    AGENTS.md
      -> 适用 agents/*.md
      -> .openhands/skills/*.md 可复用方法
      -> ADR + Issue 验收/证据
      -> 实现/测试
      -> 五审
      -> CI 证据

- AGENTS.md：薄的仓库宪法和规则路由。
- .github/governance/agent-governance.json：机器可校验的路由清单。
- agents/*.md：各专项长期固定流程和规则，保持执行器无关；不放入 .openhands，避免仓库治理绑定单一执行器。
- .openhands/skills/*.md：反方 Review、Pre-mortem、不变量、状态机、数据全链路、容量/背压、故障注入、契约攻击、升级回滚等方法。
- .openhands/skills/repo.md：OpenHands 执行画像，只负责加载和执行规则，不重复复制专项规则正文。

Skill 解决“怎么把一件事做好”；Agent 规则决定“什么时候必须做”。

## 标准交付链路

    CURRENT FACTS
      -> 加载适用规则
      -> 阅读现有架构/代码/测试
      -> Acceptance + Evidence Matrix
      -> 实现
      -> Focused Validation
      -> 五审
      -> 整改发现
      -> .openhands/pre-commit.sh
      -> Pull Request
      -> exact-head Windows GitHub Actions
      -> squash merge
      -> 核对 main

OpenHands 禁止只汇报 Review 发现后停止；Blocker 必须整改并重新 Review。

## 五审

agents/review.md 固化统一生产质量门禁：

1. 架构审；
2. 故障审；
3. 实现审；
4. 验收/证据审；
5. 维护者审。

五审调用独立 Skill，不再靠一个越来越长的总提示词。

## 仓库 Hook

- .openhands/setup.sh：幂等准备 .NET 10 并 Restore solution。
- .openhands/pre-commit.sh：治理校验、架构/源码脚手架门禁、Build、Unit Test。
- scripts/validate_agent_governance.py：防止根规则、专项 Agent、OpenHands Profile、Skills 静默漂移。
- GitHub Actions：再次执行治理 validator/tests，并作为 Windows 权威集成门禁。

## 平台验证边界

OpenHands 常运行在 Linux，而 OpenDeviceStudio 包含 Windows/WPF 项目。Linux 提交前验证使用：

    dotnet restore OpenDeviceStudio.slnx -p:EnableWindowsTargeting=true
    dotnet build OpenDeviceStudio.slnx -c Release -p:EnableWindowsTargeting=true
    dotnet test tests/OpenDeviceStudio.Tests/OpenDeviceStudio.Tests.csproj -c Release --no-build

最终 Windows Build、完整测试、Package Compatibility 和 Source Scaffold 验证仍由 GitHub Actions 负责。

## 推荐任务指令

以后给 OpenHands 的任务可以明显缩短：

    基于最新 main 完成此 Issue。
    严格执行 AGENTS.md，并按 governance manifest 加载所有适用专项 Agent。
    编码前先建立 Acceptance/Evidence Matrix。
    调用适用 Skill，先 focused validation，再执行五审。
    Review 发现必须整改，禁止只汇报。
    最后运行 .openhands/pre-commit.sh，把 PR 推进到 exact-head 可合并状态。

## 密钥

禁止把 OpenHands/模型凭据、PAT、签名材料或设备 Secret 提交到仓库。
