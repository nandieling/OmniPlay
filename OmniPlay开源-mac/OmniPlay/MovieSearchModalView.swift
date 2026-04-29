import SwiftUI
import GRDB

struct MovieSearchModalView: View {
    let movie: Movie
    @Environment(\.dismiss) var dismiss
    
    @State private var searchQuery: String = ""
    @State private var searchYear: String = ""
    @State private var originalFilename: String = "正在加载源文件信息..."
    
    @State private var searchResults: [TMDBResult] = []
    @State private var isSearching = false
    @State private var errorMessage = ""
    
    init(movie: Movie) {
        self.movie = movie
        _searchQuery = State(initialValue: movie.title)
    }
    
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("重新匹配 TMDB").font(.title2).fontWeight(.bold)
                Spacer()
                Button(action: { dismiss() }) { Image(systemName: "xmark.circle.fill").font(.title2).foregroundColor(.gray) }.buttonStyle(.plain)
            }
            .padding().background(Color(NSColor.windowBackgroundColor))
            Divider()
            
            VStack(alignment: .leading, spacing: 15) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("源文件名称：").font(.caption).foregroundColor(.secondary)
                    Text(originalFilename)
                        .font(.body).fontWeight(.medium).foregroundColor(.blue)
                        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.blue.opacity(0.1)).cornerRadius(8).textSelection(.enabled)
                }
                
                HStack(spacing: 12) {
                    TextField("输入正确的影视名称...", text: $searchQuery)
                        .textFieldStyle(RoundedBorderTextFieldStyle())
                    
                    TextField("年份(可选)", text: $searchYear)
                        .textFieldStyle(RoundedBorderTextFieldStyle())
                        .frame(width: 80)
                    
                    Button("搜索") { performSearch() }
                        .buttonStyle(.borderedProminent)
                        .disabled(isSearching || searchQuery.isEmpty)
                        .keyboardShortcut(.defaultAction)
                }
                
                if isSearching {
                    ProgressView("正在向 TMDB 请求数据...").frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if !errorMessage.isEmpty {
                    Text(errorMessage).foregroundColor(.red).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if searchResults.isEmpty {
                    Text("暂无搜索结果，请输入关键字后回车开始匹配").foregroundColor(.gray).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(searchResults, id: \.id) { result in
                        HStack(spacing: 12) {
                            CachedPosterView(posterPath: result.posterPath)
                                .frame(width: 50, height: 75).cornerRadius(6)
                            
                            VStack(alignment: .leading, spacing: 4) {
                                Text(displayTitle(for: result)).font(.headline)
                                let displayDate = result.releaseDate ?? result.firstAirDate ?? ""
                                Text(String(displayDate.prefix(4))).font(.subheadline).foregroundColor(.secondary)
                                if let overview = result.overview, !overview.isEmpty {
                                    Text(overview).font(.caption).foregroundColor(.gray).lineLimit(2)
                                }
                            }
                            Spacer()
                            Button("关联此项") { saveResult(result) }.buttonStyle(.bordered)
                        }
                        .padding(.vertical, 4)
                    }
                    .listStyle(.plain)
                }
            }
            .padding(20)
        }
        .frame(width: 600, height: 500)
        .onAppear { fetchOriginalFile() }
    }
    
    private func performSearch() {
        isSearching = true
        errorMessage = ""
        searchResults = []
        
        Task {
            do {
                let extracted = MediaNameParser.extractSearchMetadata(from: searchQuery)
                let parentChinese = MediaNameParser.extractParentFolderChineseTitle(from: originalFilename)
                let extractedForeign = extracted.foreignTitle?.trimmingCharacters(in: .whitespacesAndNewlines)
                let normalizedQuery = (extracted.chineseTitle ?? extracted.foreignTitle ?? extracted.fullCleanTitle ?? searchQuery)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                let secondaryQuery = extractedForeign
                let cleanYearInput = searchYear.trimmingCharacters(in: .whitespacesAndNewlines)
                let effectiveYear = cleanYearInput.isEmpty ? (extracted.year ?? "") : cleanYearInput
                let preferredMediaType = MediaNameParser.isLikelyTVEpisodePath(originalFilename) ? "tv" : nil
                let fileName = (originalFilename as NSString).lastPathComponent
                let preferredSeason = MediaNameParser.resolvePreferredSeason(
                    from: originalFilename,
                    fileName: fileName,
                    fallbackIndex: 0
                )
                var results = try await TMDBService.shared.searchCandidates(
                    query: normalizedQuery,
                    year: effectiveYear,
                    preferredMediaType: preferredMediaType,
                    preferredSeason: preferredSeason,
                    secondaryQuery: secondaryQuery
                )
                var finalQuery = normalizedQuery
                
                // 文件名关键词无结果时，自动回退父目录中文名重试一次。
                if results.isEmpty,
                   let folderCN = parentChinese,
                   !folderCN.isEmpty,
                   folderCN != normalizedQuery {
                    let retry = try await TMDBService.shared.searchCandidates(
                        query: folderCN,
                        year: effectiveYear,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        secondaryQuery: secondaryQuery
                    )
                    if !retry.isEmpty {
                        results = retry
                        finalQuery = folderCN
                    }
                }

                // 中文名无结果时，回退外文名重试（例如 四月一日灵异事件簿·笼:徒梦 -> xxxHolic）。
                if results.isEmpty,
                   let foreign = extractedForeign,
                   !foreign.isEmpty,
                   foreign != finalQuery {
                    let retry = try await TMDBService.shared.searchCandidates(
                        query: foreign,
                        year: effectiveYear,
                        preferredMediaType: preferredMediaType,
                        preferredSeason: preferredSeason,
                        secondaryQuery: extracted.chineseTitle
                    )
                    if !retry.isEmpty {
                        results = retry
                        finalQuery = foreign
                    }
                }
                
                // 🌟 使用更简洁的 MainActor 更新 UI
                await MainActor.run {
                    self.searchQuery = finalQuery
                    self.searchYear = effectiveYear
                    self.searchResults = results
                    self.isSearching = false
                    if results.isEmpty { self.errorMessage = "未找到与「\(finalQuery)」相关的影视" }
                }
            } catch {
                await MainActor.run {
                    self.errorMessage = "网络请求失败：\(error.localizedDescription)"
                    self.isSearching = false
                }
            }
        }
    }
    
    private func saveResult(_ result: TMDBResult) {
        let resultId = Int64(result.id)
        let resultTitle = displayTitle(for: result)
        let resultReleaseDate = result.releaseDate ?? result.firstAirDate
        let resultOverview = result.overview
        let resultPoster = result.posterPath
        let resultVoteAverage = result.voteAverage
        let oldMovieId = movie.id
        let localThumbDirectory = ThumbnailManager.shared.thumbDirectory
        
        Task {
            do {
                let generatedTasks = try await AppDatabase.shared.dbQueue.write { db -> [(String, String, Int64?, Int, Int)] in
                    var tempTasks: [(String, String, Int64?, Int, Int)] = []
                    let newMovie = Movie(id: resultId, title: resultTitle, releaseDate: resultReleaseDate, overview: resultOverview, posterPath: resultPoster, voteAverage: resultVoteAverage, isLocked: true)
                    
                    if try Movie.fetchOne(db, key: resultId) != nil { try newMovie.update(db) } else { try newMovie.insert(db) }
                    
                    let seedFiles = try VideoFile.fetchVisibleFiles(movieId: oldMovieId, in: db)
                    let sourceIds = Set(seedFiles.map(\.sourceId))
                    let parentFolders = Set(seedFiles.map { (($0.relativePath as NSString).deletingLastPathComponent) })
                    let siblingFiles = try VideoFile.fetchAllVisible(in: db).filter { file in
                        sourceIds.contains(file.sourceId) &&
                        parentFolders.contains((file.relativePath as NSString).deletingLastPathComponent) &&
                        (file.movieId == oldMovieId || file.mediaType == "unmatched" || (file.movieId ?? 0) < 0)
                    }
                    
                    var filesById: [String: VideoFile] = [:]
                    for file in seedFiles + siblingFiles {
                        filesById[file.id] = file
                    }
                    let files = Array(filesById.values)
                    let sortedFiles = files.enumerated().sorted {
                        MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset) <
                        MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset)
                    }.map(\.element)
                    
                    let isTVShow = resultTitle.contains("季") || resultTitle.contains("集") || files.contains {
                        let name = $0.fileName
                        return name.range(of: #"[sS]\d{1,2}[eE]\d{1,2}"#, options: .regularExpression) != nil || name.range(of: #"[eE][pP]?\d{1,3}"#, options: .regularExpression) != nil || name.range(of: #"第\d{1,3}[集话]"#, options: .regularExpression) != nil
                    }
                    
                    for (index, var file) in sortedFiles.enumerated() {
                        file.movieId = resultId; file.mediaType = "movie"; try file.update(db)
                        if isTVShow {
                            let parsed = MediaNameParser.parseEpisodeInfo(from: file.fileName, fallbackIndex: index)
                            let oldImageURL = localThumbDirectory.appendingPathComponent("\(file.id).jpg"); try? FileManager.default.removeItem(at: oldImageURL)
                            tempTasks.append((file.id, resultTitle, resultId, parsed.season, parsed.episode))
                        }
                    }
                    if oldMovieId != resultId { let remainingFilesCount = try VideoFile.filter(Column("movieId") == oldMovieId).fetchCount(db); if remainingFilesCount == 0 { _ = try Movie.deleteOne(db, key: oldMovieId) } }
                    return tempTasks
                }
                
                if let path = resultPoster { PosterManager.shared.downloadPoster(posterPath: path) }
                if !generatedTasks.isEmpty { ThumbnailManager.shared.startBatchWebFetch(tasks: generatedTasks) }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil); dismiss() }
            } catch { await MainActor.run { self.errorMessage = "保存失败: \(error.localizedDescription)" } }
        }
    }
    
    private func fetchOriginalFile() {
        Task {
            do {
                let currentMovieId = movie.id
                let file = try await AppDatabase.shared.dbQueue.read { db -> VideoFile? in
                    try VideoFile.fetchVisibleFirstFile(movieId: currentMovieId, in: db)
                }
                await MainActor.run {
                    if let file {
                        let fileName = file.fileName
                        let relativePath = file.relativePath
                        if !relativePath.isEmpty { self.originalFilename = relativePath } else if !fileName.isEmpty { self.originalFilename = fileName } else { self.originalFilename = "未知文件名" }
                        
                        let extracted = self.smartExtractMovieTitleAndYear(from: self.originalFilename)
                        if !extracted.title.isEmpty { self.searchQuery = extracted.title.trimmingCharacters(in: .whitespacesAndNewlines) }
                        if let y = extracted.year, !y.isEmpty { self.searchYear = y }
                    } else { self.originalFilename = "未找到关联的视频文件" }
                }
            } catch {
                await MainActor.run { self.originalFilename = "读取文件信息失败" }
            }
        }
    }
    
    func smartExtractMovieTitleAndYear(from rawPath: String) -> (title: String, year: String?) {
        let extracted = MediaNameParser.extractSearchMetadata(from: rawPath)
        let parentChinese = MediaNameParser.extractParentFolderChineseTitle(from: rawPath)
        if let cn = extracted.chineseTitle,
           let parentChinese,
           !parentChinese.isEmpty,
           cn.contains(parentChinese),
           cn != parentChinese {
            return (parentChinese, extracted.year)
        }
        return (extracted.chineseTitle ?? parentChinese ?? extracted.foreignTitle ?? extracted.fullCleanTitle ?? "", extracted.year)
    }

    private func displayTitle(for result: TMDBResult) -> String {
        let extracted = MediaNameParser.extractSearchMetadata(from: originalFilename)
        let chineseFallback = extracted.chineseTitle ?? MediaNameParser.extractParentFolderChineseTitle(from: originalFilename)
        return result.preferredTitle(chineseFallback: chineseFallback)
    }
}
