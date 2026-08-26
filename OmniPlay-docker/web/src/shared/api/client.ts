export interface HealthStatus {
  service: string;
  version: string;
  environment: string;
  rootDirectory: string;
  databasePath: string;
  databaseReady: boolean;
  serverTime: string;
}

export interface RuntimeSelfCheckSnapshot {
  status: "ok" | "warn" | "error" | string;
  checkedAt: string;
  items: RuntimeSelfCheckItem[];
}

export interface RuntimeSelfCheckItem {
  key: string;
  label: string;
  status: "ok" | "warn" | "error" | string;
  detail: string;
  data: Record<string, string> | null;
}

export interface AuthStatus {
  isSetupRequired: boolean;
  isAuthenticated: boolean;
  username: string | null;
  role: string | null;
}

export interface MediaSourceSummary {
  id: number;
  name: string;
  kind: string;
  baseUrl: string;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
  lastScannedAt: string | null;
}

export interface AppSettingsSnapshot {
  appName: string;
  phase: string;
  tmdb: TmdbSettings;
  cache: CacheSettings;
  playback: PlaybackSettings;
  proxy: ProxySettings;
  automation: AutomationSettings;
}

export interface TmdbSettings {
  enableMetadataEnrichment: boolean;
  enablePosterDownloads: boolean;
  customApiKey: string;
  customAccessToken: string;
  language: string;
}

export interface TmdbConnectionTestResult {
  isReachable: boolean;
  source: string;
  statusCode: number | null;
  message: string;
}

export interface ProxySettings {
  isEnabled: boolean;
  url: string;
  username: string;
  password: string;
  bypassList: string;
}

export interface ProxyConnectionTestResult {
  isReachable: boolean;
  proxyUrl: string;
  targetUrl: string;
  statusCode: number | null;
  message: string;
}

export interface CacheSettings {
  hlsRetentionHours: number;
  hlsMaxGb: number;
  hlsCachePath: string;
  imageCleanupScope: "orphans-and-untracked" | "orphans-only" | string;
  webDavRetentionHours: number;
  webDavMaxGb: number;
  subtitleCachePath: string;
  subtitleMaxGb: number;
  subtitleCacheStrategy: "disabled" | "optimized" | "full" | string;
}

export interface PlaybackSettings {
  directStream: boolean;
  hlsRemux: boolean;
  transcode: boolean;
  showEpisodeDetails: boolean;
  playbackQualityPreference: "original-priority" | "auto" | "compatibility" | string;
  defaultAudioLanguage: "smart" | "en" | "ja" | string;
  defaultSubtitleLanguage: "en" | "zh" | string;
}

export interface AutomationSettings {
  scheduledLibraryRefreshEnabled: boolean;
  scheduledLibraryRefreshIntervalHours: number;
}

export interface AppSettingsUpdateRequest {
  tmdb?: TmdbSettings;
  cache?: CacheSettings;
  playback?: PlaybackSettings;
  proxy?: ProxySettings;
  automation?: AutomationSettings;
}

export interface LibraryRefreshRequest {
  sortKey: "title" | "rating" | "year" | string;
  sortDirection: "asc" | "desc" | string;
}

export interface MediaSourceUpdateRequest {
  name?: string;
  isEnabled?: boolean;
}

export interface LocalDirectoryBrowseResult {
  currentPath: string;
  parentPath: string | null;
  entries: LocalDirectoryEntry[];
}

export interface LocalDirectoryEntry {
  name: string;
  path: string;
  isReadable: boolean;
  isHidden: boolean;
}

export interface WebDavConnectionTestResult {
  isReachable: boolean;
  url: string;
  statusCode: number | null;
  message: string;
}

export interface WebDavDirectoryBrowseResult {
  currentUrl: string;
  parentUrl: string | null;
  entries: WebDavDirectoryEntry[];
}

export interface WebDavDirectoryEntry {
  name: string;
  url: string;
  isReadable: boolean;
  lastModified: string | null;
}

export interface LibraryItemSummary {
  id: string;
  itemKind: "movie" | "tv" | string;
  title: string;
  releaseDate: string | null;
  overview: string | null;
  posterAssetId: string | null;
  voteAverage: number | null;
  doubanRating: number | null;
  isLocked: boolean;
  isWatched: boolean;
  videoFileCount: number;
  maxProgressSeconds: number;
  maxDurationSeconds: number;
  updatedAt: string;
}

export interface LibraryItemCustomMetadataUpdateRequest {
  title: string;
  releaseDate: string | null;
  overview: string | null;
  voteAverage: number | null;
  doubanRating: number | null;
  posterFile?: File | null;
  episodeId?: string | null;
  episodeSubtitle?: string | null;
}

export interface LibraryItemDetail extends LibraryItemSummary {
  tmdbId: number | null;
  douban: DoubanMetadata | null;
  videoFiles: VideoFileSummary[];
  seasons: SeasonDetail[];
}

export interface DoubanMetadata {
  libraryItemId: string;
  subjectId: string;
  subjectUrl: string;
  title: string;
  originalTitle: string | null;
  year: string | null;
  rating: number | null;
  ratingCount: number | null;
  summary: string | null;
  genres: string | null;
  countries: string | null;
  posterUrl: string | null;
  fetchedAt: string;
}

export interface DoubanMetadataImportRequest {
  subjectId: string;
  subjectUrl: string;
  title: string;
  originalTitle?: string | null;
  year?: string | null;
  rating?: number | null;
  ratingCount?: number | null;
  summary?: string | null;
  genres?: string | null;
  countries?: string | null;
  posterUrl?: string | null;
  fetchedAt?: string | null;
}

export interface SeasonDetail {
  id: string;
  seasonNumber: number;
  title: string | null;
  posterAssetId: string | null;
  episodes: EpisodeDetail[];
}

export interface EpisodeDetail {
  id: string;
  seasonId: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  overview: string | null;
  stillAssetId: string | null;
  airDate: string | null;
  videoFile: VideoFileSummary | null;
  /** All files mapped to this season/episode. videoFile remains the preferred resume file. */
  videoFiles?: VideoFileSummary[];
}

export interface VideoFileSummary {
  id: string;
  sourceId: number;
  sourceName: string;
  relativePath: string;
  fileName: string;
  mediaKind: string;
  fileSizeBytes: number | null;
  durationSeconds: number;
  positionSeconds: number;
  isWatched: boolean;
  episodeId: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  episodeTitle: string | null;
  container: string | null;
  videoCodec: string | null;
  audioCodec: string | null;
  subtitleSummary: string | null;
  audioTracks: VideoFileStreamSummary[];
  subtitleStreams: VideoFileStreamSummary[];
}

export interface VideoFileStreamSummary {
  index: number;
  kind: string;
  codec: string | null;
  language: string | null;
  title: string | null;
  channels: number | null;
  channelLayout: string | null;
  isDefault: boolean;
  isForced: boolean;
}

export interface PlaybackFileStreams {
  audioTracks: VideoFileStreamSummary[];
  subtitleStreams: VideoFileStreamSummary[];
}

export interface LibraryScanSummary {
  sourceCount: number;
  newMovieCount: number;
  newVideoFileCount: number;
  removedVideoFileCount: number;
  newTvShowCount: number;
  diagnostics: string[];
  hasDiagnostics: boolean;
}

export interface LibraryScanProgress {
  phase: string;
  sourceCount: number;
  completedSourceCount: number;
  currentSourceName: string | null;
  totalVideoFileCount: number;
  processedVideoFileCount: number;
  probeCandidateCount: number;
  probedVideoFileCount: number;
  currentRelativePath: string | null;
  updatedAt: string;
}

export interface LibraryScanStatus {
  isRunning: boolean;
  startedAt: string | null;
  completedAt: string | null;
  lastSummary: LibraryScanSummary | null;
  lastError: string | null;
  isCancellationRequested: boolean;
  cancellationRequestedAt: string | null;
  progress: LibraryScanProgress | null;
  wasCanceled: boolean;
}

export interface LibraryMetadataEnrichmentSummary {
  scannedItems: number;
  matchedItems: number;
  updatedItems: number;
  downloadedPosters: number;
  diagnostics: string[];
}

export interface LibraryMetadataEnrichmentProgress {
  phase: string;
  targetItemCount: number;
  processedItemCount: number;
  matchedItemCount: number;
  updatedItemCount: number;
  downloadedPosterCount: number;
  currentItemId: string | null;
  currentTitle: string | null;
  updatedAt: string;
  phaseTargetCount?: number | null;
  phaseProcessedCount?: number | null;
}

export interface LibraryMetadataEnrichmentStatus {
  isRunning: boolean;
  startedAt: string | null;
  completedAt: string | null;
  lastSummary: LibraryMetadataEnrichmentSummary | null;
  lastError: string | null;
  isCancellationRequested: boolean;
  cancellationRequestedAt: string | null;
  progress: LibraryMetadataEnrichmentProgress | null;
  wasCanceled: boolean;
  targetLibraryItemId: string | null;
}

export interface PlaybackDecision {
  fileId: string;
  mode: "direct" | "hls-remux" | "hls-transcode" | "unavailable" | string;
  streamUrl: string | null;
  manifestUrl: string | null;
  sessionId: string | null;
  isReady: boolean;
  reason: string | null;
  durationSeconds: number | null;
}

export interface PlaybackCacheStatus {
  videoFileId: string;
  isRemote: boolean;
  isReady: boolean;
  isDownloading: boolean;
  canCancel: boolean;
  totalBytes: number | null;
  downloadedBytes: number;
  percent: number | null;
  state: string;
  errorMessage: string | null;
  canStreamDirect: boolean;
}

export interface SubtitleCacheStatus {
  pgsTotal: number;
  pgsCached: number;
  cachedBytes: number;
  subtitleTotal: number;
  subtitleCached: number;
}

export interface HlsPlaybackProfile {
  mode: string;
  transcodeVideo: boolean;
  qualityId: string;
  maxHeight: number | null;
  videoBitrateKbps: number | null;
  audioBitrateKbps: number;
  audioTrackIndex: number | null;
  subtitleMode: string;
  externalSubtitlePath: string | null;
  embeddedSubtitleStreamIndex: number | null;
  preferHardwareAcceleration: boolean;
  hardwareEncoder: string | null;
  hardwareDecoder: string | null;
  hardwareAcceleration: string | null;
  toneMapToSdr: boolean;
  toneMapMode: string;
  cacheKey: string;
}

export interface PlaybackDiagnosticStep {
  key: string;
  label: string;
  status: "ok" | "warn" | "error" | "info" | string;
  detail: string;
}

export interface PlaybackDiagnostics {
  fileId: string;
  fileName: string;
  isRemote: boolean;
  sourceKind: string;
  requestedQuality: string;
  requestedAudioTrackIndex: number | null;
  requestedSubtitleMode: string;
  requestedSubtitleId: string | null;
  hardwareRequested: boolean;
  baseMode: string;
  effectiveMode: string;
  reason: string;
  requiresFullCache: boolean;
  usesWebDavRangeProxy: boolean;
  usesDirectStream: boolean;
  usesHls: boolean;
  usesTranscode: boolean;
  burnsSubtitle: boolean;
  directStreamUrl: string | null;
  hlsManifestUrl: string | null;
  hlsProfile: HlsPlaybackProfile | null;
  ffmpegCommandPreview: string | null;
  cacheStatus: PlaybackCacheStatus | null;
  capabilities: FfmpegTranscodeCapabilities | null;
  steps: PlaybackDiagnosticStep[];
}

export interface PlaybackDecisionRequest {
  quality?: string;
  audioTrackIndex?: number | null;
  subtitleMode?: string;
  subtitleId?: string | null;
  hardware?: boolean;
}

export interface PlaybackSubtitleTrack {
  id: string;
  fileName: string;
  format: string;
  language: string | null;
  webVttUrl: string | null;
  canBurn: boolean;
}

export interface FfmpegTranscodeCapabilities {
  isAvailable: boolean;
  ffmpegPath: string;
  hardwareEncoders: string[];
  preferredHardwareEncoder: string | null;
  hardwareDecoders: string[];
  preferredHardwareDecoder: string | null;
  hardwareAccelerators: string[];
  errorMessage: string | null;
  checkedAt: string;
}

export interface HlsCacheCleanupSummary {
  removedSessionCount: number;
  removedBytes: number;
}

export interface TmdbMetadataMatch {
  id: number;
  mediaType: string;
  title: string;
  overview: string | null;
  releaseDate: string | null;
  posterPath: string | null;
  voteAverage: number | null;
  popularity: number | null;
}

export interface BackgroundTaskStatus {
  id: string;
  kind: string;
  title: string;
  state: string;
  isRunning: boolean;
  isCancellationRequested: boolean;
  canCancel: boolean;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  phase: string | null;
  progressText: string | null;
  progressPercent: number | null;
  currentItem: string | null;
  resultText: string | null;
  errorMessage: string | null;
}

export interface BackgroundTaskSnapshot {
  tasks: BackgroundTaskStatus[];
  activeTask: BackgroundTaskStatus | null;
}

export interface CacheUsageSummary {
  totalBytes: number;
  totalFileCount: number;
  buckets: CacheUsageBucket[];
  updatedAt: string;
}

export interface CacheUsageBucket {
  key: string;
  label: string;
  path: string;
  bytes: number;
  fileCount: number;
}

export interface HlsCachePrepareRequest {
  libraryItemIds?: string[];
  videoFileIds?: string[];
}

export async function getHealthStatus(): Promise<HealthStatus> {
  const response = await fetch("/api/health");
  if (!response.ok) {
    throw new Error(`Health request failed: ${response.status}`);
  }

  return response.json() as Promise<HealthStatus>;
}

export async function getAuthStatus(): Promise<AuthStatus> {
  return readJson<AuthStatus>("/api/auth/status");
}

export async function registerAdmin(username: string, password: string): Promise<AuthStatus> {
  const response = await fetch("/api/auth/register", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  return readResponse<AuthStatus>(response);
}

export async function login(username: string, password: string): Promise<AuthStatus> {
  const response = await fetch("/api/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  return readResponse<AuthStatus>(response);
}

export async function logout(): Promise<void> {
  const response = await fetch("/api/auth/logout", { method: "POST" });
  await readEmptyResponse(response);
}

export async function getMediaSources(): Promise<MediaSourceSummary[]> {
  return readJson<MediaSourceSummary[]>("/api/sources");
}

export async function browseLocalDirectories(path?: string): Promise<LocalDirectoryBrowseResult> {
  const query = path?.trim() ? `?path=${encodeURIComponent(path.trim())}` : "";
  return readJson<LocalDirectoryBrowseResult>(`/api/sources/local/directories${query}`);
}

export async function getBackgroundTasks(): Promise<BackgroundTaskSnapshot> {
  return readJson<BackgroundTaskSnapshot>("/api/tasks");
}

export async function getAppSettings(): Promise<AppSettingsSnapshot> {
  return readJson<AppSettingsSnapshot>("/api/settings");
}

export async function getRuntimeSelfCheck(): Promise<RuntimeSelfCheckSnapshot> {
  return readJson<RuntimeSelfCheckSnapshot>("/api/runtime/self-check");
}

export async function updateAppSettings(request: AppSettingsUpdateRequest): Promise<AppSettingsSnapshot> {
  const response = await fetch("/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  return readResponse<AppSettingsSnapshot>(response);
}

export async function testTmdbConnection(settings: TmdbSettings): Promise<TmdbConnectionTestResult> {
  const response = await fetch("/api/settings/tmdb/test", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });
  return readResponse<TmdbConnectionTestResult>(response);
}

export async function testProxyConnection(settings: ProxySettings): Promise<ProxyConnectionTestResult> {
  const response = await fetch("/api/settings/proxy/test", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });
  return readResponse<ProxyConnectionTestResult>(response);
}

export async function getCacheStatus(): Promise<CacheUsageSummary> {
  return readJson<CacheUsageSummary>("/api/cache/status");
}

export async function cancelBackgroundTask(taskId: string): Promise<BackgroundTaskStatus> {
  const response = await fetch(`/api/tasks/${encodeURIComponent(taskId)}/cancel`, { method: "POST" });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function addLocalMediaSource(path: string, name?: string): Promise<MediaSourceSummary> {
  const response = await fetch("/api/sources/local", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ path, name }),
  });

  return readResponse<MediaSourceSummary>(response);
}

export async function addWebDavMediaSource(
  url: string,
  name?: string,
  username?: string,
  password?: string,
): Promise<MediaSourceSummary> {
  const response = await fetch("/api/sources/webdav", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, name, username, password }),
  });

  return readResponse<MediaSourceSummary>(response);
}

export async function testWebDavConnection(
  url: string,
  username?: string,
  password?: string,
): Promise<WebDavConnectionTestResult> {
  const response = await fetch("/api/sources/webdav/test", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, username, password }),
  });

  return readResponse<WebDavConnectionTestResult>(response);
}

export async function browseWebDavDirectories(
  url: string,
  username?: string,
  password?: string,
): Promise<WebDavDirectoryBrowseResult> {
  const response = await fetch("/api/sources/webdav/browse", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, username, password }),
  });

  return readResponse<WebDavDirectoryBrowseResult>(response);
}

export async function scanMediaSource(
  sourceId: number,
  request: LibraryRefreshRequest,
): Promise<LibraryScanStatus> {
  const response = await fetch(`/api/sources/${encodeURIComponent(sourceId)}/scan`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  return readResponse<LibraryScanStatus>(response);
}

export async function updateMediaSource(
  sourceId: number,
  request: MediaSourceUpdateRequest,
): Promise<MediaSourceSummary> {
  const response = await fetch(`/api/sources/${encodeURIComponent(sourceId)}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  return readResponse<MediaSourceSummary>(response);
}

export async function removeMediaSource(sourceId: number): Promise<BackgroundTaskStatus> {
  const response = await fetch(`/api/sources/${encodeURIComponent(sourceId)}`, {
    method: "DELETE",
  });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function getLibraryItems(): Promise<LibraryItemSummary[]> {
  return readJson<LibraryItemSummary[]>("/api/library/items");
}

export async function getLibraryItemDetail(id: string): Promise<LibraryItemDetail> {
  return readJson<LibraryItemDetail>(`/api/library/items/${encodeURIComponent(id)}`);
}

export async function searchLibraryItemMetadata(
  libraryItemId: string,
  query?: string,
  mediaType?: string,
  year?: string,
): Promise<TmdbMetadataMatch[]> {
  const params = new URLSearchParams();
  if (query?.trim()) {
    params.set("query", query.trim());
  }
  if (mediaType?.trim()) {
    params.set("mediaType", mediaType.trim());
  }
  if (year?.trim()) {
    params.set("year", year.trim());
  }

  const queryString = params.size > 0 ? `?${params}` : "";
  return readJson<TmdbMetadataMatch[]>(
    `/api/library/items/${encodeURIComponent(libraryItemId)}/metadata/search${queryString}`,
  );
}

export async function applyLibraryItemMetadata(
  libraryItemId: string,
  match: TmdbMetadataMatch,
): Promise<LibraryItemDetail> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/metadata/apply`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(match),
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function bindLibraryItemDouban(libraryItemId: string, subject: string): Promise<LibraryItemDetail> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/douban/bind`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ subject }),
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function importLibraryItemDouban(
  libraryItemId: string,
  request: DoubanMetadataImportRequest,
): Promise<LibraryItemDetail> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/douban/import`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function setLibraryItemLocked(libraryItemId: string, isLocked: boolean): Promise<LibraryItemDetail> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/metadata/lock`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ libraryItemId, isLocked }),
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function updateLibraryItemCustomMetadata(
  libraryItemId: string,
  request: LibraryItemCustomMetadataUpdateRequest,
): Promise<LibraryItemDetail> {
  const body = new FormData();
  body.set("title", request.title);
  body.set("releaseDate", request.releaseDate ?? "");
  body.set("overview", request.overview ?? "");
  body.set("voteAverage", request.voteAverage === null ? "" : String(request.voteAverage));
  body.set("doubanRating", request.doubanRating === null ? "" : String(request.doubanRating));
  if (request.episodeId) {
    body.set("episodeId", request.episodeId);
    body.set("episodeSubtitle", request.episodeSubtitle ?? "");
  }
  if (request.posterFile) {
    body.set("poster", request.posterFile);
  }

  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/metadata/custom`, {
    method: "PATCH",
    body,
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function getLibraryScanStatus(): Promise<LibraryScanStatus> {
  return readJson<LibraryScanStatus>("/api/library/scan/status");
}

export async function scanLibrary(request: LibraryRefreshRequest): Promise<LibraryScanStatus> {
  const response = await fetch("/api/library/scan", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  return readResponse<LibraryScanStatus>(response);
}

export async function cancelLibraryScan(): Promise<LibraryScanStatus> {
  const response = await fetch("/api/library/scan/cancel", { method: "POST" });
  return readResponse<LibraryScanStatus>(response);
}

export async function setWatchedStatus(videoFileId: string, isWatched: boolean, durationSeconds?: number): Promise<void> {
  const response = await fetch("/api/playback/watched", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ videoFileId, isWatched, durationSeconds }),
  });
  await readEmptyResponse(response);
}

export async function setLibraryItemWatchedStatus(
  libraryItemId: string,
  isWatched: boolean,
): Promise<LibraryItemDetail> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/watched`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ libraryItemId, isWatched }),
  });
  return readResponse<LibraryItemDetail>(response);
}

export async function updatePlaybackProgress(
  videoFileId: string,
  positionSeconds: number,
  durationSeconds: number,
): Promise<void> {
  const response = await fetch("/api/playback/progress", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ videoFileId, positionSeconds, durationSeconds }),
  });
  await readEmptyResponse(response);
}

export async function getMetadataEnrichmentStatus(): Promise<LibraryMetadataEnrichmentStatus> {
  return readJson<LibraryMetadataEnrichmentStatus>("/api/library/scrape/status");
}

export async function scrapeLibrary(): Promise<LibraryMetadataEnrichmentStatus> {
  const response = await fetch("/api/library/scrape", { method: "POST" });
  return readResponse<LibraryMetadataEnrichmentStatus>(response);
}

export async function rescrapeLibraryItem(libraryItemId: string): Promise<LibraryMetadataEnrichmentStatus> {
  const response = await fetch(`/api/library/items/${encodeURIComponent(libraryItemId)}/rescrape`, { method: "POST" });
  return readResponse<LibraryMetadataEnrichmentStatus>(response);
}

export async function cancelMetadataEnrichment(): Promise<LibraryMetadataEnrichmentStatus> {
  const response = await fetch("/api/library/scrape/cancel", { method: "POST" });
  return readResponse<LibraryMetadataEnrichmentStatus>(response);
}

export async function getPlaybackDecision(
  videoFileId: string,
  request: PlaybackDecisionRequest = {},
): Promise<PlaybackDecision> {
  const params = new URLSearchParams();
  if (request.quality) {
    params.set("quality", request.quality);
  }
  if (request.audioTrackIndex !== undefined && request.audioTrackIndex !== null) {
    params.set("audioTrackIndex", String(request.audioTrackIndex));
  }
  if (request.subtitleMode) {
    params.set("subtitleMode", request.subtitleMode);
  }
  if (request.subtitleId) {
    params.set("subtitleId", request.subtitleId);
  }
  if (request.hardware) {
    params.set("hardware", "true");
  }

  const query = params.size > 0 ? `?${params}` : "";
  return readJson<PlaybackDecision>(`/api/playback/decision/${encodeURIComponent(videoFileId)}${query}`);
}

export async function getPlaybackDiagnostics(
  videoFileId: string,
  request: PlaybackDecisionRequest = {},
): Promise<PlaybackDiagnostics> {
  const params = new URLSearchParams();
  if (request.quality) {
    params.set("quality", request.quality);
  }
  if (request.audioTrackIndex !== undefined && request.audioTrackIndex !== null) {
    params.set("audioTrackIndex", String(request.audioTrackIndex));
  }
  if (request.subtitleMode) {
    params.set("subtitleMode", request.subtitleMode);
  }
  if (request.subtitleId) {
    params.set("subtitleId", request.subtitleId);
  }
  if (request.hardware) {
    params.set("hardware", "true");
  }

  const query = params.size > 0 ? `?${params}` : "";
  return readJson<PlaybackDiagnostics>(`/api/playback/diagnostics/${encodeURIComponent(videoFileId)}${query}`);
}

export async function getPlaybackCacheStatus(videoFileId: string): Promise<PlaybackCacheStatus> {
  return readJson<PlaybackCacheStatus>(`/api/playback/files/${encodeURIComponent(videoFileId)}/cache`);
}

export async function getSubtitleCacheStatus(videoFileId: string): Promise<SubtitleCacheStatus> {
  return readJson<SubtitleCacheStatus>(`/api/playback/files/${encodeURIComponent(videoFileId)}/subtitle-cache/status`);
}

export async function preparePlaybackCache(videoFileId: string): Promise<PlaybackCacheStatus> {
  const response = await fetch(`/api/playback/files/${encodeURIComponent(videoFileId)}/cache/prepare`, {
    method: "POST",
  });
  return readResponse<PlaybackCacheStatus>(response);
}

export async function cancelPlaybackCache(videoFileId: string): Promise<PlaybackCacheStatus> {
  const response = await fetch(`/api/playback/files/${encodeURIComponent(videoFileId)}/cache/cancel`, {
    method: "POST",
  });
  return readResponse<PlaybackCacheStatus>(response);
}

export async function getPlaybackCapabilities(): Promise<FfmpegTranscodeCapabilities> {
  return readJson<FfmpegTranscodeCapabilities>("/api/playback/capabilities");
}

export async function getPlaybackSubtitles(videoFileId: string): Promise<PlaybackSubtitleTrack[]> {
  return readJson<PlaybackSubtitleTrack[]>(`/api/playback/files/${encodeURIComponent(videoFileId)}/subtitles`);
}

export async function getPlaybackStreams(videoFileId: string): Promise<PlaybackFileStreams> {
  return readJson<PlaybackFileStreams>(`/api/playback/files/${encodeURIComponent(videoFileId)}/streams`);
}

export async function stopHlsSession(sessionId: string): Promise<void> {
  const response = await fetch(`/api/playback/hls/${encodeURIComponent(sessionId)}/stop`, {
    method: "POST",
    keepalive: true,
  });
  await readEmptyResponse(response);
}

export async function cleanupHlsCache(maxAgeHours?: number, maxGb?: number, clearAll?: boolean): Promise<BackgroundTaskStatus> {
  const params = new URLSearchParams();
  if (maxAgeHours !== undefined) {
    params.set("maxAgeHours", String(maxAgeHours));
  }
  if (maxGb !== undefined) {
    params.set("maxGb", String(maxGb));
  }
  if (clearAll !== undefined) {
    params.set("clearAll", String(clearAll));
  }
  const queryString = params.toString();
  const query = queryString ? `?${queryString}` : "";
  const response = await fetch(`/api/playback/hls/cleanup${query}`, {
    method: "POST",
  });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function cleanupSubtitleCache(maxGb?: number, clearAll?: boolean): Promise<BackgroundTaskStatus> {
  const params = new URLSearchParams();
  if (maxGb !== undefined) {
    params.set("maxGb", String(maxGb));
  }
  if (clearAll !== undefined) {
    params.set("clearAll", String(clearAll));
  }
  const queryString = params.toString();
  const query = queryString ? `?${queryString}` : "";
  const response = await fetch(`/api/playback/subtitles/cache/cleanup${query}`, {
    method: "POST",
  });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function prewarmHlsCache(request: HlsCachePrepareRequest): Promise<BackgroundTaskStatus> {
  const response = await fetch("/api/playback/hls/prewarm", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
  });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function cleanupWebDavCache(maxAgeHours?: number): Promise<BackgroundTaskStatus> {
  const query = maxAgeHours === undefined ? "" : `?maxAgeHours=${encodeURIComponent(maxAgeHours)}`;
  const response = await fetch(`/api/playback/webdav/cache/cleanup${query}`, {
    method: "POST",
  });
  return readResponse<BackgroundTaskStatus>(response);
}

export async function cleanupAssetCache(): Promise<BackgroundTaskStatus> {
  const response = await fetch("/api/assets/cache/cleanup", { method: "POST" });
  return readResponse<BackgroundTaskStatus>(response);
}

export function posterUrl(posterAssetId: string): string {
  return `/api/assets/posters/${encodeURIComponent(posterAssetId)}`;
}

export function thumbnailUrl(thumbnailAssetId: string): string {
  return `/api/assets/thumbnails/${encodeURIComponent(thumbnailAssetId)}`;
}

export function tmdbPosterUrl(posterPath: string | null): string | null {
  if (!posterPath?.trim()) {
    return null;
  }

  const normalizedPath = posterPath.startsWith("/") ? posterPath : `/${posterPath}`;
  return `https://image.tmdb.org/t/p/w185${normalizedPath}`;
}

export function playbackStreamUrl(videoFileId: string): string {
  return `/api/playback/files/${encodeURIComponent(videoFileId)}/stream`;
}

async function readJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  return readResponse<T>(response);
}

async function readResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = (await response.json()) as { error?: string; detail?: string; title?: string };
      message = body.error ?? body.detail ?? body.title ?? message;
    } catch {
      // Keep the HTTP status text when the server returns an empty or non-JSON error.
    }
    throw new Error(message);
  }

  return response.json() as Promise<T>;
}

async function readEmptyResponse(response: Response): Promise<void> {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = (await response.json()) as { error?: string; detail?: string; title?: string };
      message = body.error ?? body.detail ?? body.title ?? message;
    } catch {
      // Keep the HTTP status text when the server returns an empty or non-JSON error.
    }
    throw new Error(message);
  }
}
