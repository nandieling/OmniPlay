import Foundation

/// 空库首次启动时铺的一层演示数据。Windows 版还没有媒体源扫描，界面得先有东西可画；
/// 等扫描接上，这段也只在「库里一部片都没有」时跑一次。
enum DemoSeed {
    struct Item {
        let title: String
        let originalTitle: String?
        let year: Int
        let minutes: Double
        let position: Double
        let watched: Bool
        let genres: [String]
        let directors: [String]
        let casts: [String]
    }

    static let items: [Item] = [
        Item(title: "坠落的审判", originalTitle: "Anatomie d'une chute", year: 2023, minutes: 151, position: 62,
             watched: false, genres: ["剧情", "悬疑"], directors: ["茹斯汀·特里耶"], casts: ["桑德拉·惠勒", "斯万·阿劳德"]),
        Item(title: "霸王别姬", originalTitle: "Farewell My Concubine", year: 1993, minutes: 171, position: 0,
             watched: true, genres: ["剧情", "爱情"], directors: ["陈凯歌"], casts: ["张国荣", "张丰毅", "巩俐"]),
        Item(title: "奥本海默", originalTitle: "Oppenheimer", year: 2023, minutes: 181, position: 34,
             watched: false, genres: ["传记", "历史"], directors: ["克里斯托弗·诺兰"], casts: ["基里安·墨菲", "艾米莉·布朗特"]),
        Item(title: "千与千寻", originalTitle: "千と千尋の神隠し", year: 2001, minutes: 125, position: 0,
             watched: false, genres: ["动画", "奇幻"], directors: ["宫崎骏"], casts: ["柊瑠美", "入野自由"]),
        Item(title: "花样年华", originalTitle: "In the Mood for Love", year: 2000, minutes: 98, position: 88,
             watched: false, genres: ["剧情", "爱情"], directors: ["王家卫"], casts: ["梁朝伟", "张曼玉"]),
        Item(title: "星际穿越", originalTitle: "Interstellar", year: 2014, minutes: 169, position: 0,
             watched: true, genres: ["科幻", "冒险"], directors: ["克里斯托弗·诺兰"], casts: ["马修·麦康纳", "安妮·海瑟薇"]),
        Item(title: "活着", originalTitle: "To Live", year: 1994, minutes: 132, position: 0,
             watched: false, genres: ["剧情", "家庭"], directors: ["张艺谋"], casts: ["葛优", "巩俐"]),
        Item(title: "布达佩斯大饭店", originalTitle: "The Grand Budapest Hotel", year: 2014, minutes: 99, position: 12,
             watched: false, genres: ["喜剧", "剧情"], directors: ["韦斯·安德森"], casts: ["拉尔夫·费因斯", "托尼·雷沃罗利"]),
        Item(title: "罗生门", originalTitle: "羅生門", year: 1950, minutes: 88, position: 0,
             watched: false, genres: ["剧情", "悬疑"], directors: ["黑泽明"], casts: ["三船敏郎", "京町子"]),
        Item(title: "牯岭街少年杀人事件", originalTitle: "A Brighter Summer Day", year: 1991, minutes: 237, position: 0,
             watched: false, genres: ["剧情", "犯罪"], directors: ["杨德昌"], casts: ["张震", "杨静宜"]),
        Item(title: "泰坦尼克号", originalTitle: "Titanic", year: 1997, minutes: 194, position: 0,
             watched: true, genres: ["剧情", "爱情"], directors: ["詹姆斯·卡梅隆"], casts: ["莱昂纳多·迪卡普里奥", "凯特·温斯莱特"]),
        Item(title: "让子弹飞", originalTitle: "Let the Bullets Fly", year: 2010, minutes: 132, position: 51,
             watched: false, genres: ["喜剧", "动作"], directors: ["姜文"], casts: ["姜文", "周润发", "葛优"]),
    ]

    /// 铺一层演示数据，返回写入的影视条数。
    @discardableResult
    static func run(in store: LibraryStore, now: Date = Date()) throws -> Int {
        let sourceID = try store.addSource(kind: "local", name: "演示片库", endpoint: "demo://library", now: now)
        for (offset, item) in items.enumerated() {
            let movieID = try store.addMovie(
                LibraryStore.NewMovie(title: item.title, originalTitle: item.originalTitle, year: item.year,
                                      genres: item.genres, directors: item.directors, casts: item.casts),
                now: now)
            try store.addVideoFile(
                movieID: movieID, sourceID: sourceID,
                path: "demo://library/\(item.year)/\(offset + 1).mkv",
                duration: item.minutes * 60,
                position: item.position * 60,
                lastPlayedAt: item.position > 0 ? now.addingTimeInterval(-Double(offset + 1) * 5_400) : nil,
                watched: item.watched)
        }
        return items.count
    }
}
