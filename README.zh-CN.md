<p align="center"><img src="docs/assets/logo.svg" width="440" alt="DiskScope" /></p>

<p align="center">让 AI 逐层看懂磁盘空间。</p>

<p align="center"><a href="README.md">English</a> · <a href="https://github.com/11Godlike11/diskscope/releases/latest">下载 Windows EXE</a> · <a href="docs/MCP.md">MCP 工具说明</a></p>

DiskScope 是专门给 AI 使用的 Windows 磁盘分析和清理工具。它读取 NTFS 文件元数据建立索引，MFT 多扇区修复固定按 512 字节边界处理，兼容 4K 原生扇区卷。每次只展开一层目录，让 AI 沿着最大的目录继续查找，避免把全盘百万条文件记录塞进上下文。

提供独立 EXE、JSON 命令行和标准 stdio MCP。发布包自带运行时，使用时无需安装 Python、Node.js 或 .NET。支持本地 NTFS、Windows 10/11 x64；本次实际验证环境为 Windows 11。

## 下载和接入

1. 从 [Releases](https://github.com/11Godlike11/diskscope/releases/latest) 下载 `DiskScope-版本-win-x64.zip`。
2. 解压到固定目录，例如 `C:\Tools\DiskScope`。
3. 在该目录打开 PowerShell，执行：

```powershell
# 只接入分析工具，不暴露删除工具
.\Register-Codex.ps1

# 需要让 AI 协助清理时，启用预览和删除工具
.\Register-Codex.ps1 -EnableCleanup
```

修改接入配置后重启客户端。扫描整卷需要管理员权限；也可以先在管理员终端运行 `DiskScope.exe scan F:`，再让普通权限的 AI 查询保存的索引。

其他 MCP 客户端可以使用以下配置，替换为实际安装路径：

```json
{
  "mcpServers": {
    "diskscope": {
      "command": "C:\\Tools\\DiskScope\\DiskScope.exe",
      "args": ["mcp", "--read-only"]
    }
  }
}
```

去掉 `--read-only` 才会暴露清理工具。该选项禁止清理操作，分析仍会写入本地索引。默认数据目录为 `%LOCALAPPDATA%\DiskScope`，可用 `--data-dir 路径` 修改。

## 直接对 AI 这样说

> 用 diskscope 分析 F 盘。先看实时剩余空间，再扫描或使用现有索引，按实际占用逐层展开，每次最多 10 项。核实大目录属于哪个应用、用途是什么。保留素材、存档、源码和服务数据。对具体清理范围生成预览，只执行我已授权的范围。后台任务必须查询到完成，删除后刷新索引并核对实际剩余空间，结束时调用 disk_release 释放索引。

## 渐进式披露和后台任务

查看盘符 → 扫描并查询进度 → 展开一层 → 查局部大文件 → 清理预览 → 审阅并授权 → 执行 → 核对释放空间。

- `disk_scan`、`disk_cleanup_plan` 和 `disk_cleanup_execute` 立即返回任务编号。
- 用 `disk_job_status` 查询阶段、计数和结果。预览完成后才会得到 `plan_id` 和 `confirm_token`。
- 查询结果最多 100 项，默认 20 项；有更多内容时使用分页或继续展开指定目录。
- 默认只保留一个卷的内存索引，空闲两分钟后释放；`disk_release` 可立即卸载，磁盘上的索引仍保留。
- Windows 的系统文件缓存由系统管理，DiskScope 不会自动清空系统缓存或修改其他程序的内存。

详细工具参数见 [MCP.md](docs/MCP.md)。

## 命令行或代码调用

AI 不支持 MCP 时，可以调用 EXE 并解析返回的 JSON：

```powershell
.\DiskScope.exe volumes
.\DiskScope.exe scan F:
.\DiskScope.exe browse 'F:\' --limit 10
.\DiskScope.exe largest 'F:\Downloads' --extension zip --min-bytes 104857600 --limit 10
.\DiskScope.exe plan 'F:\明确可丢弃的缓存目录'
.\DiskScope.exe plan-show PLAN_ID
# 仅执行已审阅、已授权的具体方案
.\DiskScope.exe execute PLAN_ID --confirm CONFIRM_TOKEN
.\DiskScope.exe execution-status PLAN_ID
```

命令行扫描和清理同步执行；stdout 为 JSON，诊断信息写 stderr。错误会返回非零退出码。

## 删除行为与边界

**永久删除，不进入回收站，不是跨文件事务。** 预览记录精确文件集，方案一小时过期。执行核对文件 ID、卷序列号、大小、修改时间、物理占用和链接数，拒绝经过目录连接或符号链接，通过父目录共享锁阻止重命名或删除，并在预览和删除时核对句柄真实路径与期望路径及保护规则。阻止同一方案重放，遇到第一个文件错误即停止。优先使用 `FileDispositionInfoEx` 删除只读文件，不修改文件属性；系统不支持时回退到旧接口，此时只读文件仍可能失败。新文件和目录保留。

确认 token 用于匹配方案，不代表用户授权。发生超时或断开时，先查询任务或执行日志，不能直接重放删除。清理系统组件或卸载软件应使用对应管理功能。

硬链接只计一次物理占用；只删一个链接名可能不释放空间。稀疏文件的逻辑长度可能远大于实际占用。扫描是活动卷的时间点索引，并非 VSS 一致性快照。详见 [安全和统计说明](docs/SAFETY.md)。

不支持 UNC、FAT/exFAT/ReFS 和大小写敏感目录中的同名变体。索引和日志包含本机路径，请勿上传到公开 issue。发布 EXE 尚未代码签名；SHA-256 文件用于核对下载完整性。

## 构建

源码构建需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build src/DiskScope.csproj -c Release
dotnet run --project tests/DiskScope.Tests.csproj -c Release -- "$env:TEMP\diskscope-test-$([guid]::NewGuid())"
.\scripts\package.ps1
```

测试目录必须不存在，测试只删除新建的测试文件。输出 ZIP 和校验文件位于 `artifacts`。查看 [贡献指南](CONTRIBUTING.md) 和 [验证范围](docs/VALIDATION.md)。

MIT 开源，© 2026 11Godlike11。独立项目，与 WizTree、OpenAI 和 Microsoft 无关联。
