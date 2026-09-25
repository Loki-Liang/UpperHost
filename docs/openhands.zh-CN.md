# OpenHands 接入

简体中文 | [English](openhands.md)

UpperHost 已加入 OpenHands V1 / Agent Canvas 的仓库级配置。OpenHands 只作为开发工具，不是 UpperHost 的运行时依赖。

## 默认开发策略

对于常规功能开发、缺陷修复、重构、测试、文档和架构实现，**OpenHands 是 UpperHost 默认的实际开发执行器**。根目录 `AGENTS.md` 是仓库总控规则，`.openhands/skills/repo.md` 是 OpenHands 专用执行细则。

标准交付链路：

```text
Issue/Task -> OpenHands -> Focused Validation -> .openhands/pre-commit.sh
          -> Pull Request -> Windows GitHub Actions -> Review -> Squash Merge -> main
```

其他助手可以负责任务调度、现状检查、Review 和 CI 排查，但生产实现默认留在 OpenHands；只有 `AGENTS.md` 明确的备用场景才允许切换执行器。

## 已接入内容

- `.openhands/skills/repo.md`：OpenHands 自动加载的仓库规则，包含架构边界、目录职责、开发流程和验证要求。
- `.openhands/setup.sh`：幂等初始化 OpenHands 工作区，准备 .NET 10 并 Restore solution。
- `.openhands/pre-commit.sh`：适用于 Linux Sandbox 的 Build + Unit Test 门禁。
- GitHub Actions 对 OpenHands Shell Hook 做语法校验，防止配置长期失效。

## 连接仓库

使用当前 OpenHands V1 / Agent Canvas 或 OpenHands Cloud，连接 GitHub 后选择：

```text
Loki-Liang/UpperHost
```

每个开发任务都应从最新 `main` 开始，并在修改代码前先加载仓库 Skill。

本机使用 Agent Canvas 时，按 OpenHands 当前官方安装方式部署。需要隔离 Agent 对本机文件系统的访问时，应优先使用 Sandbox/Docker 模式。

## 平台验证边界

UpperHost 包含 WPF 项目，权威 CI 在 Windows 上运行；OpenHands 常见执行环境是 Linux Sandbox。

因此仓库 Hook 使用：

```bash
dotnet restore UpperHost.slnx -p:EnableWindowsTargeting=true
dotnet build UpperHost.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test tests/UpperHost.Tests/UpperHost.Tests.csproj -c Release --no-build
```

这让 OpenHands 在提交前先发现普通 Restore、编译和单元测试错误。最终仍由 GitHub Actions 在 Windows 上执行 Build、Test、NuGet Pack 和生成模板 Smoke Build。

## 密钥

禁止把 OpenHands API Key、模型 API Key、PAT、签名材料或任何机器级凭据提交到仓库。认证必须配置在 OpenHands 自身或对应 Secret Store 中。

## 推荐任务指令

给 OpenHands 分配 Issue 时可使用以下闭环要求：

```text
基于最新 main 完成此 Issue。修改前先读取 AGENTS.md 和 .openhands/skills/repo.md。
保持现有架构边界，生产实现与对应测试一起提交。
先跑 focused validation，再跑仓库 pre-commit 门禁；失败时从首个可行动错误继续修复，
不要重复从头跑已经通过的步骤。最后 Review diff，并把分支/PR 推进到可合并状态，
禁止只汇报问题不落地实现。
```
