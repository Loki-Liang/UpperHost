# 兼容性策略

简体中文 | [English](compatibility.md)

UpperHost 采用源码直接二开，但可复用 Runtime 与 canonical 产品脚手架仍然属于版本化契约。兼容性校验直接放进现有 required `build-test-scaffold`，不是一个可有可无的旁路 Workflow。

## 保护表面

门禁保护三类接口：

1. **Source Scaffold**：`app/UpperHost.App`、源码 `ProjectReference` 拓扑、启动组合、WPF Target 与必要配置路径。
2. **Runtime Package API**：CI 产出的所有可复用 `src/UpperHost.*` 包，包括 Abstractions、Hosting、Control、Dataflow、Workflows、StateMachines、Provider/Starter 与对外 Presentation 契约。
3. **可选 NuGet 分发**：这些 Runtime 包真正发布时继续遵守同一 Package/API 兼容规则。NuGet 只用于可选的跨仓库复用，不是 clone/fork 源码脚手架的运行前置。

Source Scaffold Contract 定义在 `eng/compatibility/source-scaffold-contract.json`，由 `scripts/validate_source_scaffold_contract.py` 执行。

## exact-base API 对比

PR CI 先 Pack 当前 Runtime，再下载 **head SHA 与 PR base SHA 完全一致** 的成功 main CI 所产生的 `upperhost-packages`。如果 exact base 没有成功 Artifact，门禁直接失败，禁止退回更旧的成功 main 作为 Baseline。

API 比较使用微软 `Microsoft.DotNet.ApiCompat.Tool`：

- 普通 Baseline Validation 检出删除/重命名成员、不兼容签名与 Package 兼容破坏；
- Strict Baseline Validation 同时识别新增 Public API，防止无意扩大公共契约；
- CI 自测会真实构造“删除 Public Member”和“新增 Public Member”两个探针，证明普通/Strict 模式确实能拦截对应变化。

仓库不会自己实现一套脆弱的 API Diff Parser。

## 有意新增 Public API

如果接口确实应该公开，本 PR 必须新增或修改：

```text
eng/compatibility/api-additions/<PackageId>.md
```

文件至少包含非空字段：

```text
Issue: #123
Reason: 为什么必须成为公共接口
Surface: 新增的类型/成员
```

旧 PR 留下的 Approval 不能授权这次变化；Gate 会检查 Approval 文件相对本次 PR base 确实发生了修改。

## Breaking Change

有意 Breaking Change 必须在同一交付中同时具备：

1. 明确标记 Breaking 的 Issue/PR 和影响范围；
2. 新契约对应测试；
3. Migration Instructions；
4. 合适的 SemVer/Package Version 变更；
5. Release Notes/Migration Notes；
6. 本 PR 修改的 `eng/compatibility/breaking/<PackageId>.md`，并包含 Issue / Reason / Migration / Version。

删除 Baseline、关闭 ApiCompat、削弱 Source Contract 或跳过 Gate 都不能作为“修复”方式。

## Configuration 与 Source Scaffold 变化

`app/UpperHost.App` 继续是 clone/fork 后唯一 canonical 产品入口。Contract 会检查 WPF Target、源码 `ProjectReference`、启动组合和稳定配置路径。修改这些接口时，必须显式更新 Contract，并同时给出 Migration/文档与匹配测试。

pre-commit 会在本地先运行 Source-Scaffold Contract；Package Compatibility 因为依赖经过验证的 exact-base Package Artifact，权威结果继续来自 PR Windows CI。

## Release 集成

#27 已明确要求 tag/GitHub Release/可选 NuGet Publish 前必须通过 Compatibility Gate，因此 Release 必须复用同一策略，不能绕过 PR 证据。

#42 负责更深的 clean Source-Scaffold E2E。本 Gate 保护结构/启动/配置契约并保留现有 Source-Scaffold Build Validation，但不冒充 #42 已完成。
