# WinKit.Core 无依赖原生集成测试

运行（仓库根）：

```powershell
& .\.tools\dotnet\dotnet.exe run --project .\tests\WinKit.Core.Tests\WinKit.Core.Tests.csproj -c Release
```

所有测试使用 `Path.GetTempPath()` 下随机 `WinKit-core-tests-<GUID>` 目录，先检查未存在才创建。清理只处理该自有目录，不跟随重解析点；测试 ACL 变更仅施加到自有临时子目录并在 `finally` 恢复。测试不使用任何用户现有 NUL 文件。

覆盖：精确 nul/NUL 与 nul.txt/null/普通文件/保留名目录区分；Unicode 及 >260 长路径；非法设备/相对/歧义路径；越界及伪造候选；缺失文件和父目录；文件身份及原地修改；普通根替换及祖先 junction 替换；嵌套/root/ancestor junction；扫描时祖先/root 句柄防重命名；只读、共享占用、并发写、共享读；ACL 拒绝显式 Issue；预取消与部分扫描取消；不同路径相同身份硬链接；最新扫描授权；进度通知异常；启用大小写敏感目录下 nul/NUL 和 scan/SCAN 精确处理；可用时符号链接替换。

测试结果独立统计 `passed / skipped / failed`。符号链接或大小写敏感目录需要系统支持和相应权限，不会自动提权，不支持时明确 `SKIP`，不冒充 PASS。任何 FAIL 返回退出码 1；仅成功/明确跳过返回 0。

本机验证（SDK 10.0.404，Release）：**25 passed, 1 skipped, 0 failed (26 total)**。跳过项目为文件符号链接创建：Windows 错误 1314（缺少所需权限）。目录 junction 和 NTFS 大小写敏感目录测试实际执行通过。

未覆盖真实网络 UNC/DFS、可写内存映射、非本机文件系统或内核级对手；见核心的安全说明。
