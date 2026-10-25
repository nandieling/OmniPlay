# 片库这一层

两个平台跑同一份库文件，所以这里不引 GRDB（Windows 上编不过，见 docs/SPIKE.md §1），
而是自带 SQLite 官方合并源 + 一层薄封装。

- `../../CSQLite/`：官方 amalgamation，版本见该目录 README。
- `SQLiteDatabase.swift`：执行 / 查询 / 写入 / 事务，四件事，多一样都不加。
- `LibraryStore.swift`：表结构与读写。表名列名跟 mac 版对齐（movie / videoFile /
  mediaSource / mediaMark），以后两边互导库不用做字段翻译。
- `DemoSeed.swift`：空库首次启动时铺的演示数据，媒体源扫描接上之后依然只在空库时跑。

## 表

| 表 | 作用 | 关键列 |
| --- | --- | --- |
| `mediaSource` | 挂载点 | `kind` `endpoint` `isEnabled` |
| `movie` | 一部影视的元数据 | `title` `year` `posterPath`，演职员存 JSON 文本 |
| `videoFile` | 一个可播放文件 | `movieId` 级联删，`lastPosition` `watched` |
| `mediaMark` | 已看 / 评分 / 评论，NeoDB 同步的本地账本 | `key` = `movie:<id>` |

排序不在库里做：名称排序走 `Library/HomeSort.swift` 那套（中文按拼音、外文排后面），
SQL 只负责筛，两个平台的结果才能逐字一致。
