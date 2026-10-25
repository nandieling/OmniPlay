# CSQLite

SQLite 官方合并源（`sqlite-amalgamation-3540100`，即 3.54.1，取自
<https://www.sqlite.org/download.html>），直接编进核心库。

为什么要自带：Windows 上没有系统 `sqlite3.dll` 可链；GRDB 在 Windows 上又编不过
（`GRDBSQLite` 找不到 `sqlite3.h`，见 docs/SPIKE.md §1）。库这一层 mac 与 Windows
必须是同一份实现、同一份 SQLite，所以合并源跟着仓库走。

升级时换掉 `sqlite3.c` 与 `include/sqlite3.h`、`include/sqlite3ext.h` 三个文件即可，
编译开关在根目录 `Package.swift` 的 `CSQLite` 目标里。
