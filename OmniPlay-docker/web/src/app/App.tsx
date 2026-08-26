import { type CSSProperties, type FormEvent, type KeyboardEvent, type MouseEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import Hls from "hls.js";
import {
  ArrowLeft,
  ChevronDown,
  ChevronRight,
  CheckCircle2,
  Circle,
  CircleStop,
  Download,
  FolderOpen,
  FolderPlus,
  LogOut,
  Captions,
  CirclePause,
  CirclePlay,
  Music2,
  Pencil,
  Play,
  Save,
  Search,
  Settings,
  ExternalLink,
  SlidersHorizontal,
  SkipBack,
  SkipForward,
  Star,
  RefreshCw,
  Trash2,
  X,
} from "lucide-react";
import {
  addLocalMediaSource,
  applyLibraryItemMetadata,
  AppSettingsSnapshot,
  AuthStatus,
  BackgroundTaskSnapshot,
  BackgroundTaskStatus,
  bindLibraryItemDouban,
  browseLocalDirectories,
  CacheUsageSummary,
  CacheSettings,
  cancelLibraryScan,
  cancelMetadataEnrichment,
  cancelPlaybackCache,
  cleanupAssetCache,
  cleanupHlsCache,
  cleanupSubtitleCache,
  EpisodeDetail,
  getBackgroundTasks,
  getAppSettings,
  getAuthStatus,
  getCacheStatus,
  getLibraryScanStatus,
  getMetadataEnrichmentStatus,
  getPlaybackDecision,
  getPlaybackCacheStatus,
  getSubtitleCacheStatus,
  getPlaybackSubtitles,
  getPlaybackStreams,
  getRuntimeSelfCheck,
  getLibraryItemDetail,
  getLibraryItems,
  getMediaSources,
  LibraryItemDetail,
  LibraryItemCustomMetadataUpdateRequest,
  LibraryItemSummary,
  LibraryMetadataEnrichmentProgress,
  LibraryMetadataEnrichmentStatus,
  LibraryScanStatus,
  login,
  LocalDirectoryBrowseResult,
  logout,
  MediaSourceSummary,
  PlaybackCacheStatus,
  PlaybackFileStreams,
  PlaybackSubtitleTrack,
  ProxyConnectionTestResult,
  ProxySettings,
  RuntimeSelfCheckSnapshot,
  SubtitleCacheStatus,
  posterUrl,
  preparePlaybackCache,
  prewarmHlsCache,
  registerAdmin,
  removeMediaSource,
  searchLibraryItemMetadata,
  setLibraryItemWatchedStatus,
  scanLibrary,
  stopHlsSession,
  TmdbMetadataMatch,
  thumbnailUrl,
  tmdbPosterUrl,
  updateAppSettings,
  updateMediaSource,
  updatePlaybackProgress,
  testTmdbConnection,
  testProxyConnection,
  TmdbConnectionTestResult,
  updateLibraryItemCustomMetadata,
  VideoFileSummary,
} from "../shared/api/client";

const APP_VERSION = "1.6";
const APP_UPDATE_URL = "https://github.com/nandieling/OmniPlay";

type MetadataSearchState = {
  item: LibraryItemSummary;
  detail: LibraryItemDetail | null;
  query: string;
  year: string;
  doubanSubject: string;
  candidates: TmdbMetadataMatch[];
  isLoadingDetail: boolean;
  isSearching: boolean;
  isApplying: boolean;
  isBindingDouban: boolean;
  error: string;
};

type CustomMetadataEditState = {
  detail: LibraryItemDetail;
  episode?: EpisodeDetail | null;
  isSaving: boolean;
  error: string;
};

type WebSubtitleCue = {
  start: number;
  end: number;
  text: string;
};

type PlaybackLanguage = "en" | "ja" | "zh";

const defaultPlaybackSettings: AppSettingsSnapshot["playback"] = {
  directStream: true,
  hlsRemux: true,
  transcode: true,
  showEpisodeDetails: true,
  playbackQualityPreference: "auto",
  defaultAudioLanguage: "smart",
  defaultSubtitleLanguage: "zh",
};

const defaultAutomationSettings: AppSettingsSnapshot["automation"] = {
  scheduledLibraryRefreshEnabled: false,
  scheduledLibraryRefreshIntervalHours: 24,
};

const defaultCacheSettings: CacheSettings = {
  hlsRetentionHours: 24,
  hlsMaxGb: 30,
  hlsCachePath: "",
  imageCleanupScope: "orphans-and-untracked",
  webDavRetentionHours: 72,
  webDavMaxGb: 20,
  subtitleCachePath: "",
  subtitleMaxGb: 20,
  subtitleCacheStrategy: "disabled",
};

export function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [isAuthLoading, setIsAuthLoading] = useState(true);
  const [authError, setAuthError] = useState("");
  const [items, setItems] = useState<LibraryItemSummary[]>([]);
  const [sources, setSources] = useState<MediaSourceSummary[]>([]);
  const [taskSnapshot, setTaskSnapshot] = useState<BackgroundTaskSnapshot | null>(null);
  const [cacheStatus, setCacheStatus] = useState<CacheUsageSummary | null>(null);
  const [settings, setSettings] = useState<AppSettingsSnapshot | null>(null);
  const [isSavingCacheSettings, setIsSavingCacheSettings] = useState(false);
  const [isSourceManagerOpen, setIsSourceManagerOpen] = useState(false);
  const [isSavingSource, setIsSavingSource] = useState(false);
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const [isSavingSettings, setIsSavingSettings] = useState(false);
  const [isHlsSelectionMode, setIsHlsSelectionMode] = useState(false);
  const [selectedHlsItemIds, setSelectedHlsItemIds] = useState<string[]>([]);
  const [selectedHlsVideoFileIds, setSelectedHlsVideoFileIds] = useState<string[]>([]);
  const [hlsCacheNotice, setHlsCacheNotice] = useState("");
  const [searchText, setSearchText] = useState("");
  const [isSearchOpen, setIsSearchOpen] = useState(false);
  const [isSortOpen, setIsSortOpen] = useState(false);
  const [sortKey, setSortKey] = useState<"title" | "rating" | "year">("year");
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("desc");
  const [isLoading, setIsLoading] = useState(true);
  const [isScanning, setIsScanning] = useState(false);
  const [scanStatus, setScanStatus] = useState<LibraryScanStatus | null>(null);
  const [isScraping, setIsScraping] = useState(false);
  const [scrapeStatus, setScrapeStatus] = useState<LibraryMetadataEnrichmentStatus | null>(null);
  const [selectedDetail, setSelectedDetail] = useState<LibraryItemDetail | null>(null);
  const [selectedSeasonByDetailId, setSelectedSeasonByDetailId] = useState<Record<string, string>>({});
  const [metadataSearch, setMetadataSearch] = useState<MetadataSearchState | null>(null);
  const [customMetadataEdit, setCustomMetadataEdit] = useState<CustomMetadataEditState | null>(null);
  const [playingFile, setPlayingFile] = useState<VideoFileSummary | null>(null);
  const [isDetailLoading, setIsDetailLoading] = useState(false);
  const [statusText, setStatusText] = useState("");
  const [errorText, setErrorText] = useState("");
  const [tmdbConnectionAlert, setTmdbConnectionAlert] = useState("");
  const scanWasRunningRef = useRef(false);
  const scrapeWasRunningRef = useRef(false);
  const scrapeLoadedUpdatedItemsRef = useRef(0);
  const scrapeLoadedCurrentItemRef = useRef("");
  const cacheCleanupWasRunningRef = useRef(false);
  const sourceCleanupWasRunningRef = useRef(false);
  const seenCompletedHlsTasksRef = useRef<Set<string>>(new Set());
  const selectedDetailRef = useRef<LibraryItemDetail | null>(null);
  const libraryScrollYRef = useRef(0);
  const tmdbLastAlertedErrorRef = useRef("");

  const loadData = useCallback(async () => {
    setErrorText("");
    const [nextItems, nextSources, nextCacheStatus, nextSettings] = await Promise.all([
      getLibraryItems(),
      getMediaSources(),
      getCacheStatus(),
      getAppSettings(),
    ]);
    setItems(nextItems);
    setSources(nextSources);
    setCacheStatus(nextCacheStatus);
    setSettings(nextSettings);
  }, []);

  const refreshLibraryAfterMetadataChange = useCallback(
    async (updatedItemId?: string) => {
      const [nextItems, nextCacheStatus] = await Promise.all([
        getLibraryItems(),
        getCacheStatus(),
      ]);
      setItems(nextItems);
      setCacheStatus(nextCacheStatus);

      const detailId = selectedDetailRef.current?.id;
      if (detailId && (!updatedItemId || updatedItemId === detailId)) {
        const detail = await getLibraryItemDetail(detailId);
        setSelectedDetail(detail);
        selectedDetailRef.current = detail;
      }
    },
    [],
  );

  const applyScanStatus = useCallback(
    (nextStatus: LibraryScanStatus) => {
      setScanStatus(nextStatus);
      setIsScanning(nextStatus.isRunning);
      const nextStatusText = formatScanStatus(nextStatus);
      if (nextStatus.isRunning && nextStatusText) {
        setStatusText(nextStatusText);
      } else if (!nextStatus.isRunning && !nextStatus.wasCanceled) {
        setStatusText("");
      }

      if (scanWasRunningRef.current && !nextStatus.isRunning && !nextStatus.wasCanceled) {
        void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
      }

      scanWasRunningRef.current = nextStatus.isRunning;
    },
    [loadData],
  );

	  const applyScrapeStatus = useCallback(
	    (nextStatus: LibraryMetadataEnrichmentStatus) => {
      setScrapeStatus(nextStatus);
      setIsScraping(nextStatus.isRunning);
      const nextStatusText = formatScrapeStatus(nextStatus);
      if (nextStatus.isRunning && nextStatusText) {
        setStatusText(nextStatusText);
      } else if (!nextStatus.isRunning && !nextStatus.wasCanceled) {
        setStatusText("");
      }

      const updatedItems = nextStatus.progress?.updatedItemCount ?? 0;
      const currentItemId = nextStatus.progress?.currentItemId ?? "";
      const shouldRefreshForRunningScrape =
        nextStatus.isRunning &&
        ((updatedItems > scrapeLoadedUpdatedItemsRef.current) ||
          (currentItemId && currentItemId !== scrapeLoadedCurrentItemRef.current));
      if (shouldRefreshForRunningScrape) {
        scrapeLoadedUpdatedItemsRef.current = updatedItems;
        scrapeLoadedCurrentItemRef.current = currentItemId;
        void refreshLibraryAfterMetadataChange(currentItemId).catch((error: unknown) =>
          setErrorText(error instanceof Error ? error.message : String(error)),
        );
      }

      if (
        !nextStatus.isRunning &&
        isTmdbConnectionFailure(nextStatus.lastError) &&
        nextStatus.lastError !== tmdbLastAlertedErrorRef.current
      ) {
        tmdbLastAlertedErrorRef.current = nextStatus.lastError ?? "";
        setTmdbConnectionAlert(nextStatus.lastError ?? "");
      }

      if (scrapeWasRunningRef.current && !nextStatus.isRunning && !nextStatus.wasCanceled) {
        scrapeLoadedUpdatedItemsRef.current = 0;
        scrapeLoadedCurrentItemRef.current = "";
        void refreshLibraryAfterMetadataChange().catch((error: unknown) =>
          setErrorText(error instanceof Error ? error.message : String(error)),
        );
        const detailId = selectedDetailRef.current?.id;
        if (!detailId || (nextStatus.targetLibraryItemId && nextStatus.targetLibraryItemId !== detailId)) {
          scrapeWasRunningRef.current = nextStatus.isRunning;
          return;
        }

        void getLibraryItemDetail(detailId)
          .then(setSelectedDetail)
          .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
      }

      if (!nextStatus.isRunning) {
        scrapeLoadedUpdatedItemsRef.current = 0;
        scrapeLoadedCurrentItemRef.current = "";
      }

      scrapeWasRunningRef.current = nextStatus.isRunning;
    },
    [refreshLibraryAfterMetadataChange],
	  );

  function refreshCacheStatusAfterTasks(snapshot: BackgroundTaskSnapshot) {
    const cacheTaskIsRunning =
      snapshot.activeTask?.kind === "asset-cache-cleanup" ||
      snapshot.activeTask?.kind === "hls-cache-cleanup" ||
      snapshot.activeTask?.kind === "webdav-cache-cleanup" ||
      snapshot.activeTask?.kind === "hls-cache-prewarm" ||
      snapshot.activeTask?.kind === "library-scan" ||
      snapshot.activeTask?.kind === "metadata-enrichment";
    if (cacheCleanupWasRunningRef.current && !cacheTaskIsRunning) {
      void getCacheStatus()
        .then(setCacheStatus)
        .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
    }

    cacheCleanupWasRunningRef.current = cacheTaskIsRunning;

    snapshot.tasks
      .filter((task) => task.kind === "hls-cache-prewarm" && task.state === "completed")
      .forEach((task) => {
        if (seenCompletedHlsTasksRef.current.has(task.id)) {
          return;
        }

        seenCompletedHlsTasksRef.current.add(task.id);
        setHlsCacheNotice(task.resultText || "HLS 缓存完成");
      });

    const sourceCleanupIsRunning = snapshot.activeTask?.kind === "media-source-cleanup";
    if (sourceCleanupWasRunningRef.current && !sourceCleanupIsRunning) {
      void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
    }

    sourceCleanupWasRunningRef.current = sourceCleanupIsRunning;
  }

  useEffect(() => {
    void getAuthStatus()
      .then(setAuthStatus)
      .catch((error: unknown) => setAuthError(error instanceof Error ? error.message : String(error)))
      .finally(() => setIsAuthLoading(false));
  }, []);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      setIsLoading(false);
      return;
    }

    setIsLoading(true);
    void loadData()
      .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)))
      .finally(() => setIsLoading(false));
  }, [authStatus?.isAuthenticated, loadData]);

  useEffect(() => {
    selectedDetailRef.current = selectedDetail;
  }, [selectedDetail]);

  useEffect(() => {
    if (!hlsCacheNotice) {
      return;
    }

    const handle = window.setTimeout(() => setHlsCacheNotice(""), 5200);
    return () => window.clearTimeout(handle);
  }, [hlsCacheNotice]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

    let disposed = false;
    let pollHandle = 0;

    async function pollStatus() {
      try {
        const nextStatus = await getLibraryScanStatus();
        if (!disposed) {
          applyScanStatus(nextStatus);
        }
      } catch {
        // Status polling is only a fallback for scan progress; keep the main UI usable.
      }
    }

    void pollStatus();
    pollHandle = window.setInterval(() => void pollStatus(), 1500);
    if (typeof EventSource === "undefined") {
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

    const events = new EventSource("/api/library/scan/events");
    events.addEventListener("status", (event) => {
      try {
        applyScanStatus(JSON.parse((event as MessageEvent<string>).data) as LibraryScanStatus);
      } catch {
        // Ignore malformed progress frames.
      }
    });
    events.onerror = () => {
      events.close();
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScanStatus, authStatus?.isAuthenticated]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

    let disposed = false;
    let pollHandle = 0;

    async function pollStatus() {
      try {
        const nextStatus = await getMetadataEnrichmentStatus();
        if (!disposed) {
          applyScrapeStatus(nextStatus);
        }
      } catch {
        // Status polling is only a fallback for metadata progress.
      }
    }

    void pollStatus();
    pollHandle = window.setInterval(() => void pollStatus(), 1500);
    if (typeof EventSource === "undefined") {
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

    const events = new EventSource("/api/library/scrape/events");
    events.addEventListener("status", (event) => {
      try {
        applyScrapeStatus(JSON.parse((event as MessageEvent<string>).data) as LibraryMetadataEnrichmentStatus);
      } catch {
        // Ignore malformed progress frames.
      }
    });
    events.onerror = () => {
      events.close();
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScrapeStatus, authStatus?.isAuthenticated]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

    let disposed = false;
    let pollHandle = 0;

	    async function pollTasks() {
	      try {
	        const nextSnapshot = await getBackgroundTasks();
	        if (!disposed) {
	          setTaskSnapshot(nextSnapshot);
	          refreshCacheStatusAfterTasks(nextSnapshot);
	        }
	      } catch {
	        // Task center status is supplementary; keep the library usable if it fails.
      }
    }

    void pollTasks();
    if (typeof EventSource === "undefined") {
      pollHandle = window.setInterval(() => void pollTasks(), 1500);
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

	    const events = new EventSource("/api/tasks/events");
	    events.addEventListener("status", (event) => {
	      try {
	        const nextSnapshot = JSON.parse((event as MessageEvent<string>).data) as BackgroundTaskSnapshot;
	        setTaskSnapshot(nextSnapshot);
	        refreshCacheStatusAfterTasks(nextSnapshot);
	      } catch {
	        // Ignore malformed task frames.
	      }
    });
    events.onerror = () => {
      events.close();
      if (!disposed && pollHandle === 0) {
        pollHandle = window.setInterval(() => void pollTasks(), 1500);
      }
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
	  }, [authStatus?.isAuthenticated]);

  const displayedItems = useMemo(() => {
    const normalizedSearch = searchText.trim().toLowerCase();
    return items
      .filter((item) => item.title.toLowerCase().includes(normalizedSearch))
      .sort((a, b) => compareLibraryItems(a, b, sortKey, sortDirection));
  }, [items, searchText, sortDirection, sortKey]);

  const continueItems = useMemo(() => {
    return items
      .filter((item) => !item.isWatched && item.maxProgressSeconds > 0 && item.maxDurationSeconds > 0)
      .slice(0, 12);
  }, [items]);
  const activeTask = taskSnapshot?.activeTask ?? null;
  const activeHlsCacheTask = activeTask?.kind === "hls-cache-prewarm" ? activeTask : null;
  const activeCacheLogTask =
    activeTask && (activeTask.kind === "hls-cache-prewarm" || activeTask.phase === "subtitle-cache")
      ? activeTask
      : null;
  const selectedHlsCount = selectedHlsItemIds.length + selectedHlsVideoFileIds.length;
  const scanPercent = scanProgressPercent(scanStatus);
  const scrapePercent = scrapeProgressPercent(scrapeStatus);
  const topStatusText =
    errorText ||
    (activeCacheLogTask ? formatBackgroundTask(activeCacheLogTask) : "") ||
    (isHlsSelectionMode ? `选择 HLS 缓存目标：已选 ${selectedHlsCount} 个` : "") ||
    (scrapeStatus?.isRunning ? formatScrapeStatus(scrapeStatus) : "") ||
    (scanStatus?.isRunning ? formatScanStatus(scanStatus) : "") ||
    statusText;

  async function handleAuthenticate(username: string, password: string) {
    setAuthError("");
    const nextStatus = authStatus?.isSetupRequired
      ? await registerAdmin(username, password)
      : await login(username, password);
    setAuthStatus(nextStatus);
  }

  async function handleLogout() {
    setErrorText("");
    setStatusText("");
    await logout();
    setAuthStatus(await getAuthStatus());
    setItems([]);
    setSources([]);
    setTaskSnapshot(null);
    setCacheStatus(null);
    setSelectedDetail(null);
    setPlayingFile(null);
  }

  async function handleCreateSources(paths: string[]): Promise<boolean> {
    const normalizedPaths = Array.from(new Set(paths.map((path) => path.trim()).filter(Boolean)));
    if (normalizedPaths.length === 0) {
      return false;
    }

    setIsSavingSource(true);
    setErrorText("");
    setStatusText(normalizedPaths.length === 1 ? "正在添加媒体源" : `正在添加 ${normalizedPaths.length} 个媒体源`);
    try {
      for (const path of normalizedPaths) {
        await addLocalMediaSource(path);
      }
      setSources(await getMediaSources());
      if (isScanning || isScraping) {
        setStatusText("媒体源已添加，当前扫描/刮削结束后可再次扫描");
        return true;
      }

      setStatusText("媒体源已添加，正在自动扫描并刮削");
      applyScanStatus(await scanLibrary(buildRefreshRequest()));
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleUpdateSource(sourceId: number, name: string, isEnabled: boolean): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在更新媒体源");
    try {
      const updated = await updateMediaSource(sourceId, { name: name.trim(), isEnabled });
      const nextItems = await getLibraryItems();
      setSources((current) => current.map((source) => (source.id === updated.id ? updated : source)));
      setItems(nextItems);
      setSelectedDetail((current) => (current && nextItems.some((item) => item.id === current.id) ? current : null));
      setStatusText("媒体源已更新");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleRemoveSource(sourceId: number): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在移除媒体源");
    try {
      const task = await removeMediaSource(sourceId);
      setSources((current) => current.filter((source) => source.id !== sourceId));
      setTaskSnapshot((snapshot) => ({
        tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
        activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
      }));
      await loadData();
      setStatusText(task.isRunning ? "媒体源已移除，正在清理索引" : "媒体源已移除");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  function buildRefreshRequest() {
    return { sortKey, sortDirection };
  }

  async function handleRefreshLibrary() {
    setIsScanning(true);
    setErrorText("");
    setTmdbConnectionAlert("");
    setStatusText("正在扫描并刮削");
    try {
      applyScanStatus(await scanLibrary(buildRefreshRequest()));
      setStatusText("扫描并刮削已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScanning(false);
    }
  }

  async function handleCancelScan() {
    setErrorText("");
    setStatusText("正在取消扫描");
    try {
      applyScanStatus(await cancelLibraryScan());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCancelScrape() {
    setErrorText("");
    setStatusText("正在取消刮削");
    try {
      applyScrapeStatus(await cancelMetadataEnrichment());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCancelRefresh() {
    if (isScanning || activeTask?.kind === "library-scan") {
      await handleCancelScan();
      return;
    }

    if (isScraping) {
      await handleCancelScrape();
    }
  }

  async function handleCleanupAssetCache() {
    setErrorText("");
    setStatusText("正在提交图片缓存清理");
    try {
      const task = await cleanupAssetCache();
      setTaskSnapshot((snapshot) => ({
        tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
        activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
      }));
      setStatusText("图片缓存清理已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  function pushBackgroundTask(task: BackgroundTaskStatus) {
    setTaskSnapshot((snapshot) => ({
      tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
      activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
    }));
  }

  function toggleHlsSelectionMode() {
    setIsHlsSelectionMode((current) => {
      const next = !current;
      if (!next) {
        setSelectedHlsItemIds([]);
        setSelectedHlsVideoFileIds([]);
      }

      return next;
    });
  }

  function toggleSelectedHlsItem(itemId: string) {
    setSelectedHlsItemIds((current) =>
      current.includes(itemId) ? current.filter((id) => id !== itemId) : [...current, itemId],
    );
  }

  function toggleSelectedHlsVideoFile(fileId: string) {
    setSelectedHlsVideoFileIds((current) =>
      current.includes(fileId) ? current.filter((id) => id !== fileId) : [...current, fileId],
    );
  }

  async function handleStartHlsCache() {
    const libraryItemIds = selectedHlsItemIds;
    const videoFileIds = selectedHlsVideoFileIds;
    if (libraryItemIds.length === 0 && videoFileIds.length === 0) {
      setStatusText("请选择要生成 HLS 缓存的影视或分集");
      return;
    }

    setErrorText("");
    setStatusText("正在提交 HLS 缓存任务");
    try {
      const task = await prewarmHlsCache({ libraryItemIds, videoFileIds });
      pushBackgroundTask(task);
      setIsHlsSelectionMode(false);
      setSelectedHlsItemIds([]);
      setSelectedHlsVideoFileIds([]);
      setStatusText("HLS 缓存任务已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCleanupTranscodeCache() {
    setErrorText("");
    setStatusText("正在提交 HLS 缓存清理");
    try {
      const task = await cleanupHlsCache();
      pushBackgroundTask(task);
      setStatusText("HLS 缓存清理已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleClearHlsCache() {
    setErrorText("");
    setStatusText("正在提交 HLS 缓存清除");
    try {
      const task = await cleanupHlsCache(undefined, undefined, true);
      pushBackgroundTask(task);
      setStatusText("HLS 缓存清除已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleClearSubtitleCache() {
    setErrorText("");
    setStatusText("正在提交字幕缓存清除");
    try {
      const task = await cleanupSubtitleCache(undefined, true);
      pushBackgroundTask(task);
      setStatusText("字幕缓存清除已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleSaveCacheSettings(
    hlsRetentionHours: number,
    hlsMaxGb: number,
    imageCleanupScope: string,
    webDavRetentionHours: number,
    webDavMaxGb: number,
    subtitleCachePath: string,
    subtitleMaxGb: number,
    subtitleCacheStrategy: string,
  ) {
    setErrorText("");
    setStatusText("正在保存缓存策略");
    setIsSavingCacheSettings(true);
    try {
      const nextSettings = await updateAppSettings({
        cache: {
          hlsRetentionHours,
          hlsMaxGb,
          hlsCachePath: settings?.cache.hlsCachePath ?? "",
          imageCleanupScope,
          webDavRetentionHours,
          webDavMaxGb,
          subtitleCachePath,
          subtitleMaxGb,
          subtitleCacheStrategy,
        },
      });
      setSettings(nextSettings);
      setStatusText("缓存策略已保存");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSavingCacheSettings(false);
    }
  }

  async function handleSaveAppSettings(
    tmdb: AppSettingsSnapshot["tmdb"],
    cache: CacheSettings,
    playback: AppSettingsSnapshot["playback"],
    proxy: ProxySettings,
    automation: AppSettingsSnapshot["automation"],
  ) {
    setErrorText("");
    setStatusText("正在保存设置");
    setIsSavingSettings(true);
    try {
      const nextSettings = await updateAppSettings({ tmdb, cache, playback, proxy, automation });
      setSettings(nextSettings);
      setStatusText("设置已保存");
      setIsSettingsOpen(false);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSavingSettings(false);
    }
  }

  function syncOpenDetail(detail: LibraryItemDetail) {
    if (selectedDetailRef.current?.id === detail.id) {
      setSelectedDetail(detail);
      selectedDetailRef.current = detail;
    }
  }

  async function handleOpenMetadataSearch(item: LibraryItemSummary) {
    setErrorText("");
    setStatusText("");
    setMetadataSearch({
      item,
      detail: null,
      query: item.title,
      year: item.releaseDate?.slice(0, 4) ?? "",
      doubanSubject: "",
      candidates: [],
      isLoadingDetail: true,
      isSearching: false,
      isApplying: false,
      isBindingDouban: false,
      error: "",
    });

    try {
      const detail = await getLibraryItemDetail(item.id);
      setMetadataSearch((current) =>
        current?.item.id === item.id
          ? {
              ...current,
              detail,
              isLoadingDetail: false,
              year: current.year || detail.releaseDate?.slice(0, 4) || "",
            }
          : current,
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === item.id ? { ...current, isLoadingDetail: false, error: message } : current,
      );
    }
  }

  async function handleSearchMetadataFromCard() {
    if (!metadataSearch) {
      return;
    }

    const query = metadataSearch.query.trim();
    if (!query) {
      setMetadataSearch({ ...metadataSearch, error: "请输入影视名称。" });
      return;
    }

    setMetadataSearch({ ...metadataSearch, isSearching: true, error: "", candidates: [] });
    try {
      const candidates = await searchLibraryItemMetadata(
        metadataSearch.item.id,
        query,
        "all",
        metadataSearch.year.trim(),
      );
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id
          ? {
              ...current,
              candidates,
              isSearching: false,
              error: candidates.length === 0 ? `未找到与「${query}」相关的影视。` : "",
            }
          : current,
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id ? { ...current, isSearching: false, error: message } : current,
      );
    }
  }

  async function handleApplyMetadataSearchCandidate(match: TmdbMetadataMatch) {
    if (!metadataSearch) {
      return;
    }

    setMetadataSearch({ ...metadataSearch, isApplying: true, error: "" });
    try {
      const detail = await applyLibraryItemMetadata(metadataSearch.item.id, match);
      syncOpenDetail(detail);
      setMetadataSearch(null);
      setStatusText("已应用匹配项并锁定元数据");
      await loadData();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id ? { ...current, isApplying: false, error: message } : current,
      );
    }
  }

  async function handleBindDoubanFromCard() {
    if (!metadataSearch) {
      return;
    }

    const subject = metadataSearch.doubanSubject.trim();
    if (!subject) {
      setMetadataSearch({ ...metadataSearch, error: "请填写豆瓣影视链接或 subject ID。" });
      return;
    }

    setMetadataSearch({ ...metadataSearch, isBindingDouban: true, error: "" });
    try {
      const detail = await bindLibraryItemDouban(metadataSearch.item.id, subject);
      syncOpenDetail(detail);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id
          ? {
              ...current,
              detail,
              doubanSubject: detail.douban?.subjectUrl ?? subject,
              isBindingDouban: false,
              error: "",
            }
          : current,
      );
      setStatusText("已绑定豆瓣链接");
      await loadData();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id ? { ...current, isBindingDouban: false, error: message } : current,
      );
    }
  }

  async function handleOpenCustomMetadataEdit(item: LibraryItemSummary) {
    setErrorText("");
    setStatusText("正在读取条目资料");
    try {
      const detail = await getLibraryItemDetail(item.id);
      setCustomMetadataEdit({ detail, episode: null, isSaving: false, error: "" });
      setStatusText("");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  function handleOpenCustomMetadataEditFromDetail(detail: LibraryItemDetail, episode?: EpisodeDetail | null) {
    setErrorText("");
    setStatusText("");
    setCustomMetadataEdit({ detail, episode: episode ?? null, isSaving: false, error: "" });
  }

  async function handleSaveCustomMetadata(request: LibraryItemCustomMetadataUpdateRequest) {
    if (!customMetadataEdit) {
      return;
    }

    setCustomMetadataEdit({ ...customMetadataEdit, isSaving: true, error: "" });
    try {
      const detail = await updateLibraryItemCustomMetadata(customMetadataEdit.detail.id, request);
      syncOpenDetail(detail);
      setCustomMetadataEdit(null);
      setStatusText("已保存自定义资料并锁定元数据");
      await loadData();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setCustomMetadataEdit((current) => (current ? { ...current, isSaving: false, error: message } : current));
    }
  }

  async function toggleLibraryItemWatched(item: LibraryItemSummary) {
    const nextWatched = !item.isWatched;
    setErrorText("");
    try {
      const detail = await setLibraryItemWatchedStatus(item.id, nextWatched);
      syncOpenDetail(detail);
      setStatusText(nextWatched ? "已标记为已看" : "已标记为未看");
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function openDetail(item: LibraryItemSummary) {
    libraryScrollYRef.current = window.scrollY;
    setIsDetailLoading(true);
    setErrorText("");
    setPlayingFile(null);
    try {
      setSelectedDetail(await getLibraryItemDetail(item.id));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsDetailLoading(false);
    }
  }

  function restoreLibraryScroll() {
    const scrollY = libraryScrollYRef.current;
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => window.scrollTo({ top: scrollY }));
    });
  }

  async function refreshCurrentDetail() {
    if (!selectedDetail) {
      return;
    }

    try {
      setSelectedDetail(await getLibraryItemDetail(selectedDetail.id));
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handlePlayFromDetail(file: VideoFileSummary, options?: { refreshMain?: boolean }) {
    if (!selectedDetail) {
      setPlayingFile(file);
      return;
    }

    try {
      const freshDetail = await getLibraryItemDetail(selectedDetail.id);
      setSelectedDetail(freshDetail);
      selectedDetailRef.current = freshDetail;
      const freshFile = options?.refreshMain
        ? resolveMainPlaybackFile(freshDetail)
        : findVideoFileById(freshDetail, file.id);
      setPlayingFile(freshFile ?? file);
    } catch {
      setPlayingFile(file);
    }
  }

  if (isAuthLoading || !authStatus?.isAuthenticated) {
    return (
      <AuthGate
        errorText={authError}
        isLoading={isAuthLoading}
        isSetupRequired={authStatus?.isSetupRequired ?? false}
        onSubmit={(username, password) => handleAuthenticate(username, password)}
      />
    );
  }

  if (selectedDetail && playingFile) {
    return (
      <PlayerView
        detail={selectedDetail}
        file={playingFile}
        onBack={() => {
          setPlayingFile(null);
          void refreshCurrentDetail();
        }}
        playbackSettings={normalizePlaybackSettings(settings?.playback)}
      />
    );
  }

  if (selectedDetail) {
    return (
      <>
        <DetailView
          detail={selectedDetail}
          errorText={errorText}
          selectedSeasonId={selectedSeasonByDetailId[selectedDetail.id] ?? ""}
          showEpisodeDetails={normalizePlaybackSettings(settings?.playback).showEpisodeDetails}
          hlsSelectionMode={isHlsSelectionMode}
          isHlsTaskRunning={!!activeHlsCacheTask}
          selectedHlsItemIds={selectedHlsItemIds}
          selectedHlsVideoFileIds={selectedHlsVideoFileIds}
          onBack={() => {
            setPlayingFile(null);
            setSelectedDetail(null);
            restoreLibraryScroll();
          }}
          onEditMetadata={(episode) => handleOpenCustomMetadataEditFromDetail(selectedDetail, episode)}
          onStartHlsCache={() => void handleStartHlsCache()}
          onToggleHlsFile={toggleSelectedHlsVideoFile}
          onToggleHlsItem={toggleSelectedHlsItem}
          onToggleHlsSelectionMode={toggleHlsSelectionMode}
          onPlay={(file, options) => void handlePlayFromDetail(file, options)}
          onSeasonChange={(seasonId) =>
            setSelectedSeasonByDetailId((current) =>
              current[selectedDetail.id] === seasonId ? current : { ...current, [selectedDetail.id]: seasonId },
            )
          }
        />
        {customMetadataEdit ? (
          <CustomMetadataEditModal
            errorText={customMetadataEdit.error}
            isSaving={customMetadataEdit.isSaving}
            detail={customMetadataEdit.detail}
            episode={customMetadataEdit.episode}
            onClose={() => setCustomMetadataEdit(null)}
            onSave={(request) => void handleSaveCustomMetadata(request)}
          />
        ) : null}
        {hlsCacheNotice ? <CacheCompleteNotice message={hlsCacheNotice} onClose={() => setHlsCacheNotice("")} /> : null}
        {tmdbConnectionAlert ? (
          <TmdbConnectionAlert
            message={tmdbConnectionAlert}
            onClose={() => setTmdbConnectionAlert("")}
            onOpenSettings={() => {
              setTmdbConnectionAlert("");
              setSelectedDetail(null);
              setIsSettingsOpen(true);
            }}
          />
        ) : null}
      </>
    );
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div className="topStatus" aria-live="polite">
          {topStatusText ? <strong className={errorText ? "error" : ""}>{topStatusText}</strong> : null}
          <div>
            {scanPercent !== null ? (
              <progress aria-label="scan progress" className="scanProgress" max={100} value={scanPercent} />
            ) : null}
            {scrapePercent !== null ? (
              <progress aria-label="scrape progress" className="scanProgress" max={100} value={scrapePercent} />
            ) : null}
          </div>
        </div>
        <div className="toolbar" aria-label="library tools">
          <button
            aria-label="media sources"
            onClick={() => setIsSourceManagerOpen(true)}
            title="媒体源"
          >
            <FolderPlus size={20} />
          </button>
          <button
            aria-label="scan and scrape"
            disabled={isScanning || isScraping || sources.length === 0}
            onClick={handleRefreshLibrary}
            title="扫描并刮削"
          >
            <RefreshCw size={19} className={isScanning || isScraping ? "spin" : ""} />
          </button>
          {isScanning || isScraping ? (
            <button aria-label="cancel refresh" onClick={() => void handleCancelRefresh()} title="取消">
              <CircleStop size={19} />
            </button>
          ) : null}
          <button aria-label="settings" disabled={!settings} onClick={() => setIsSettingsOpen(true)} title="设置">
            <Settings size={19} />
          </button>
          <button aria-label="logout" onClick={() => void handleLogout()} title="退出登录">
            <LogOut size={18} />
          </button>
        </div>
      </header>

      {isSourceManagerOpen ? (
        <SourceManagerPanel
          disabled={isSavingSource}
          isSaving={isSavingSource}
          onAdd={(paths) => handleCreateSources(paths)}
          onClose={() => setIsSourceManagerOpen(false)}
          onRemove={(sourceId) => handleRemoveSource(sourceId)}
          onUpdate={(sourceId, name, isEnabled) => handleUpdateSource(sourceId, name, isEnabled)}
          scanStatus={scanStatus}
          sources={sources}
        />
      ) : null}

      {isSettingsOpen && settings ? (
        <SettingsPanel
          cacheStatus={cacheStatus}
          isSaving={isSavingSettings}
          onClose={() => setIsSettingsOpen(false)}
          onClearHlsCache={() => void handleClearHlsCache()}
          onClearSubtitleCache={() => void handleClearSubtitleCache()}
          onSave={(tmdb, cache, playback, proxy, automation) =>
            void handleSaveAppSettings(tmdb, cache, playback, proxy, automation)
          }
          settings={settings}
        />
      ) : null}

      {metadataSearch ? (
        <MetadataSearchModal
          state={metadataSearch}
          onApply={(match) => void handleApplyMetadataSearchCandidate(match)}
          onBindDouban={() => void handleBindDoubanFromCard()}
          onChangeDoubanSubject={(doubanSubject) =>
            setMetadataSearch((current) => (current ? { ...current, doubanSubject } : current))
          }
          onChangeQuery={(query) => setMetadataSearch((current) => (current ? { ...current, query } : current))}
          onChangeYear={(year) => setMetadataSearch((current) => (current ? { ...current, year } : current))}
          onClose={() => setMetadataSearch(null)}
          onOpenDoubanSearch={() => openDoubanSearch(metadataSearch.query || metadataSearch.item.title)}
          onSearch={() => void handleSearchMetadataFromCard()}
        />
      ) : null}

      {customMetadataEdit ? (
        <CustomMetadataEditModal
          errorText={customMetadataEdit.error}
          isSaving={customMetadataEdit.isSaving}
          detail={customMetadataEdit.detail}
          episode={customMetadataEdit.episode}
          onClose={() => setCustomMetadataEdit(null)}
          onSave={(request) => void handleSaveCustomMetadata(request)}
        />
      ) : null}

      {tmdbConnectionAlert ? (
        <TmdbConnectionAlert
          message={tmdbConnectionAlert}
          onClose={() => setTmdbConnectionAlert("")}
          onOpenSettings={() => {
            setTmdbConnectionAlert("");
            setSelectedDetail(null);
            setIsSettingsOpen(true);
          }}
        />
      ) : null}

      <section className="continue">
        <h1>继续播放</h1>
        {continueItems.length > 0 ? (
          <div className="posterGrid">
            {continueItems.map((item) => (
              <PosterCard
                item={item}
                key={item.id}
                onEdit={(target) => void handleOpenCustomMetadataEdit(target)}
                onOpen={openDetail}
                onSearchMetadata={(target) => void handleOpenMetadataSearch(target)}
                hlsSelectionMode={isHlsSelectionMode}
                hlsSelected={selectedHlsItemIds.includes(item.id)}
                onToggleHlsSelection={toggleSelectedHlsItem}
                onToggleWatched={(target) => void toggleLibraryItemWatched(target)}
                showProgress
              />
            ))}
          </div>
        ) : null}
      </section>

      <section className="library">
        <div className="sectionHeader">
          <h2>所有影视</h2>
          <div className="libraryHeaderActions">
            <button aria-label="search library" onClick={() => setIsSearchOpen((current) => !current)} title="搜索">
              <Search size={19} />
            </button>
            <div className="sortMenu">
              <button aria-label="sort library" onClick={() => setIsSortOpen((current) => !current)} title="排序">
                <SlidersHorizontal size={18} />
              </button>
              {isSortOpen ? (
                <div className="sortOptions">
                  {[
                    ["title", "名称"],
                    ["rating", "评分"],
                    ["year", "上映年份"],
                  ].map(([value, label]) => (
                    <button
                      className={sortKey === value ? "active" : ""}
                      key={value}
                      onClick={() => {
                        setSortKey(value as "title" | "rating" | "year");
                        setIsSortOpen(false);
                      }}
                      type="button"
                    >
                      <span>{label}</span>
                    </button>
                  ))}
                </div>
              ) : null}
            </div>
            <button
              aria-label="toggle sort direction"
              className="directionButton"
              onClick={() => setSortDirection((current) => (current === "desc" ? "asc" : "desc"))}
              title={sortDirection === "desc" ? "降序" : "升序"}
            >
              {sortDirection === "desc" ? "↓" : "↑"}
            </button>
            <button
              aria-label="hls cache selection"
              className={isHlsSelectionMode ? "headerTextButton active" : "headerTextButton"}
              disabled={!!activeHlsCacheTask}
              onClick={toggleHlsSelectionMode}
              title="HLS 缓存"
              type="button"
            >
              <Download size={16} />
              <span>HLS</span>
            </button>
            {isHlsSelectionMode ? (
              <>
                <button
                  aria-label="start hls cache"
                  className="headerTextButton"
                  disabled={selectedHlsCount === 0 || !!activeHlsCacheTask}
                  onClick={() => void handleStartHlsCache()}
                  title="开始缓存"
                  type="button"
                >
                  <CheckCircle2 size={16} />
                  <span>开始</span>
                </button>
                <button
                  aria-label="cancel hls cache selection"
                  onClick={toggleHlsSelectionMode}
                  title="取消选择"
                  type="button"
                >
                  <X size={18} />
                </button>
              </>
            ) : null}
          </div>
        </div>
        {isSearchOpen ? (
          <div className="librarySearchRow">
            <input
              aria-label="search library"
              onChange={(event) => setSearchText(event.target.value)}
              placeholder="搜索"
              value={searchText}
            />
          </div>
        ) : null}
        <div className="posterGrid">
          {isLoading ? <div className="emptyRow">加载中</div> : null}
          {!isLoading && displayedItems.length === 0 ? <div className="emptyRow">媒体库为空</div> : null}
          {displayedItems.map((item) => (
            <PosterCard
              item={item}
              key={item.id}
              onEdit={(target) => void handleOpenCustomMetadataEdit(target)}
              onOpen={openDetail}
              onSearchMetadata={(target) => void handleOpenMetadataSearch(target)}
              hlsSelectionMode={isHlsSelectionMode}
              hlsSelected={selectedHlsItemIds.includes(item.id)}
              onToggleHlsSelection={toggleSelectedHlsItem}
              onToggleWatched={(target) => void toggleLibraryItemWatched(target)}
            />
          ))}
        </div>
      </section>
      {hlsCacheNotice ? <CacheCompleteNotice message={hlsCacheNotice} onClose={() => setHlsCacheNotice("")} /> : null}
    </main>
  );
}

function AuthGate({
  errorText,
  isLoading,
  isSetupRequired,
  onSubmit,
}: {
  errorText: string;
  isLoading: boolean;
  isSetupRequired: boolean;
  onSubmit: (username: string, password: string) => Promise<void>;
}) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [localError, setLocalError] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLocalError("");
    setIsSubmitting(true);
    try {
      await onSubmit(username, password);
    } catch (error) {
      setLocalError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <main className="authShell">
      <form className="authPanel" onSubmit={handleSubmit}>
        <header>
          <h1>OmniPlay</h1>
          <span>{isSetupRequired ? "注册管理员" : "登录"}</span>
        </header>
        <label>
          <span>用户名</span>
          <input
            autoComplete="username"
            disabled={isLoading || isSubmitting}
            onChange={(event) => setUsername(event.target.value)}
            value={username}
          />
        </label>
        <label>
          <span>密码</span>
          <input
            autoComplete={isSetupRequired ? "new-password" : "current-password"}
            disabled={isLoading || isSubmitting}
            onChange={(event) => setPassword(event.target.value)}
            type="password"
            value={password}
          />
        </label>
        {errorText || localError ? <strong>{localError || errorText}</strong> : null}
        <button disabled={isLoading || isSubmitting} type="submit">
          <span>{isSubmitting ? "处理中" : isSetupRequired ? "注册" : "登录"}</span>
        </button>
      </form>
    </main>
  );
}

function compareLibraryItems(
  a: LibraryItemSummary,
  b: LibraryItemSummary,
  sortKey: "title" | "rating" | "year",
  sortDirection: "asc" | "desc",
): number {
  const direction = sortDirection === "desc" ? -1 : 1;
  let result = 0;

  if (sortKey === "title") {
    result = a.title.localeCompare(b.title, "zh-Hans");
  } else if (sortKey === "rating") {
    result = compareNullableNumber(a.voteAverage, b.voteAverage, direction);
  } else {
    result = compareNullableNumber(readReleaseYear(a.releaseDate), readReleaseYear(b.releaseDate), direction);
  }

  if (result === 0) {
    result = a.title.localeCompare(b.title, "zh-Hans");
  }

  return sortKey === "title" ? result * direction : result;
}

function compareNullableNumber(a: number | null, b: number | null, direction: 1 | -1): number {
  if (a === null && b === null) {
    return 0;
  }

  if (a === null) {
    return 1;
  }

  if (b === null) {
    return -1;
  }

  return (a - b) * direction;
}

function readReleaseYear(releaseDate: string | null): number | null {
  if (!releaseDate) {
    return null;
  }

  const year = Number(releaseDate.slice(0, 4));
  return Number.isFinite(year) ? year : null;
}

function formatUserVisibleError(message: string): string {
  if (
    message.includes("Received an unexpected EOF or 0 bytes from the transport stream") ||
    message.toLowerCase().includes("unexpected eof")
  ) {
    return "网络连接提前断开（EOF）：通常是代理、TMDB/WebDAV 目标服务或中间网络中断导致，请检查代理、DNS/Host 和目标服务后重试。";
  }

  return message;
}

function formatScanStatus(status: LibraryScanStatus): string {
  if (status.isRunning) {
    if (status.isCancellationRequested) {
      return "正在取消扫描";
    }

    const progress = status.progress;
    if (!progress) {
      return "正在扫描";
    }

    const phaseLabel =
      {
        starting: "正在启动扫描",
        discovering: "扫描文件",
        probing: "探测媒体",
        indexing: "写入媒体库",
        "source-completed": "媒体源完成",
      }[progress.phase] ?? "正在扫描";
    const fileCount =
      progress.phase !== "probing" && progress.totalVideoFileCount > 0
        ? `${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`
        : progress.phase === "discovering" && progress.processedVideoFileCount > 0
          ? `已发现 ${progress.processedVideoFileCount} 个视频`
        : "";
    const probeCount =
      progress.phase === "probing" && progress.probeCandidateCount > 0
        ? `，探测 ${progress.probedVideoFileCount}/${progress.probeCandidateCount}`
        : "";
    const source = progress.currentSourceName ? ` · ${progress.currentSourceName}` : "";
    return `${phaseLabel}${fileCount ? ` ${fileCount}` : ""}${probeCount}${source}`;
  }

  if (status.wasCanceled) {
    return "扫描已取消";
  }

  if (status.lastError) {
    return formatUserVisibleError(status.lastError);
  }

  if (status.lastSummary) {
    return `刷新完成：${status.lastSummary.newVideoFileCount} 个新视频`;
  }

  return "";
}

function scanProgressPercent(status: LibraryScanStatus | null): number | null {
  const progress = status?.progress;
  if (!status?.isRunning || !progress || progress.totalVideoFileCount <= 0) {
    return null;
  }

  if (progress.phase === "probing" && progress.probeCandidateCount > 0) {
    return Math.round((progress.probedVideoFileCount / progress.probeCandidateCount) * 100);
  }

  return Math.round((progress.processedVideoFileCount / progress.totalVideoFileCount) * 100);
}

function formatScrapeStatus(status: LibraryMetadataEnrichmentStatus): string {
  if (status.isRunning) {
    if (status.isCancellationRequested) {
      return "正在取消刮削";
    }

    const progress = status.progress;
    if (!progress) {
      return "正在刮削";
    }

    const phaseLabel =
      {
        starting: "准备刮削",
        searching: "匹配元数据",
        "fetching-details": "精确刷新 TMDB",
        "fetching-episodes": "刷新分集信息和剧照",
        "downloading-poster": "下载海报",
        updating: "写入元数据",
      }[progress.phase] ?? "正在刮削";
    const count = scrapeStatusDisplayCount(progress);
    const itemCount = count ? `${Math.min(count.processed, count.target)}/${count.target}` : "";
    const title = progress.currentTitle ? ` · ${progress.currentTitle}` : "";
    return `${phaseLabel}${itemCount ? ` ${itemCount}` : ""}${title}`;
  }

  if (status.wasCanceled) {
    return "刮削已取消";
  }

  if (status.lastError) {
    return formatUserVisibleError(status.lastError);
  }

  if (status.lastSummary) {
    return `刮削完成：${status.lastSummary.updatedItems} 个条目，${status.lastSummary.downloadedPosters} 张海报`;
  }

  return "";
}

function isTmdbConnectionFailure(message?: string | null): boolean {
  return !!message && message.includes("TMDB API 无法连接");
}

function openDoubanSearch(query: string) {
  const normalized = query.trim();
  if (!normalized) {
    return;
  }

  const url = `https://search.douban.com/movie/subject_search?search_text=${encodeURIComponent(normalized)}`;
  window.open(url, "_blank", "noopener,noreferrer");
}

function scrapeProgressPercent(status: LibraryMetadataEnrichmentStatus | null): number | null {
  const progress = status?.progress;
  if (!status?.isRunning || !progress) {
    return null;
  }

  const count = scrapeStatusDisplayCount(progress);
  if (!count) {
    return null;
  }

  return Math.round((count.processed / count.target) * 100);
}

function scrapeStatusDisplayCount(progress: LibraryMetadataEnrichmentProgress): { processed: number; target: number } | null {
  if (
    progress.phase === "fetching-episodes" &&
    typeof progress.phaseTargetCount === "number" &&
    progress.phaseTargetCount > 0
  ) {
    return {
      processed: Math.max(0, progress.phaseProcessedCount ?? 0),
      target: progress.phaseTargetCount,
    };
  }

  if (progress.targetItemCount <= 0) {
    return null;
  }

  return {
    processed: Math.max(0, progress.processedItemCount),
    target: progress.targetItemCount,
  };
}

function SourceManagerPanel({
  disabled,
  isSaving,
  onAdd,
  onClose,
  onRemove,
  onUpdate,
  scanStatus,
  sources,
}: {
  disabled: boolean;
  isSaving: boolean;
  onAdd: (paths: string[]) => Promise<boolean>;
  onClose: () => void;
  onRemove: (sourceId: number) => Promise<boolean>;
  onUpdate: (sourceId: number, name: string, isEnabled: boolean) => Promise<boolean>;
  scanStatus: LibraryScanStatus | null;
  sources: MediaSourceSummary[];
}) {
  const [browsePath, setBrowsePath] = useState("");
  const [browseResult, setBrowseResult] = useState<LocalDirectoryBrowseResult | null>(null);
  const [browseError, setBrowseError] = useState("");
  const [isBrowsing, setIsBrowsing] = useState(false);
  const [draftNames, setDraftNames] = useState<Record<number, string>>({});
  const [selectedLocalPaths, setSelectedLocalPaths] = useState<string[]>([]);
  const [removedSourceIds, setRemovedSourceIds] = useState<number[]>([]);

  useEffect(() => {
    setDraftNames(Object.fromEntries(sources.map((source) => [source.id, source.name])));
    setRemovedSourceIds((current) => current.filter((sourceId) => sources.some((source) => source.id === sourceId)));
  }, [sources]);

  useEffect(() => {
    let disposed = false;
    void loadDirectory(browsePath, () => disposed);

    return () => {
      disposed = true;
    };
  }, []);

  async function loadDirectory(path: string, isDisposed: () => boolean = () => false) {
    setIsBrowsing(true);
    setBrowseError("");
    try {
      const result = await browseLocalDirectories(path);
      if (!isDisposed()) {
        setBrowseResult(result);
        setBrowsePath(result.currentPath);
      }
    } catch (error) {
      if (!isDisposed()) {
        setBrowseError(error instanceof Error ? error.message : String(error));
      }
    } finally {
      if (!isDisposed()) {
        setIsBrowsing(false);
      }
    }
  }

  async function handleRemove(source: MediaSourceSummary) {
    if (!window.confirm(`移除媒体源“${formatMediaSourceDisplayName(source)}”？`)) {
      return;
    }

    if (await onRemove(source.id)) {
      setRemovedSourceIds((current) => (current.includes(source.id) ? current : [...current, source.id]));
    }
  }

  function toggleLocalPath(path: string) {
    setSelectedLocalPaths((current) =>
      current.includes(path) ? current.filter((item) => item !== path) : [...current, path],
    );
  }

  async function handleMountSelected() {
    const paths = selectedLocalPaths;
    if (paths.length === 0) {
      return;
    }

    const added = await onAdd(paths);
    if (added) {
      setSelectedLocalPaths([]);
    }
  }

  const canMountSelected = selectedLocalPaths.length > 0;
  const directoryEntries = (browseResult?.entries ?? [])
    .slice()
    .sort((left, right) => {
      if (left.isReadable !== right.isReadable) {
        return left.isReadable ? -1 : 1;
      }

      return left.name.localeCompare(right.name, "zh-CN", { sensitivity: "base", numeric: true });
    });

  return (
    <div className="settingsOverlay" role="dialog" aria-label="media source manager" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close media sources" onClick={onClose} type="button" />
      <aside className="settingsDrawer sourceDrawer">
        <header className="settingsHeader">
          <div>
            <h2>媒体源</h2>
            <span>{sources.length} 个来源</span>
          </div>
          <button aria-label="close media sources" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <section className="sourceList">
          {sources.filter((source) => !removedSourceIds.includes(source.id)).length === 0 ? (
            <div className="sourceEmpty">还没有媒体源</div>
          ) : null}
          {sources.filter((source) => !removedSourceIds.includes(source.id)).map((source) => {
            const draftName = draftNames[source.id] ?? source.name;
            const isSourceScanning = isScanningSource(scanStatus, source);
            const displayName = formatMediaSourceDisplayName(source);
            return (
              <article className="sourceRow compactSourceRow" key={source.id}>
                <div className="sourceMeta">
                  <strong>{displayName}</strong>
                  {isSourceScanning ? <span>{formatSourceScanStatus(scanStatus)}</span> : null}
                </div>
                <div className="sourceActions">
                  <label className="sourceToggle">
                    <input
                      checked={source.isEnabled}
                      disabled={disabled}
                      onChange={(event) => void onUpdate(source.id, draftName, event.target.checked)}
                      type="checkbox"
                    />
                    <span>{source.isEnabled ? "启用" : "停用"}</span>
                  </label>
                  <button
                    aria-label={`remove ${displayName}`}
                    disabled={disabled}
                    onClick={() => void handleRemove(source)}
                    type="button"
                  >
                    <Trash2 size={15} />
                  </button>
                </div>
              </article>
            );
          })}
        </section>

        <section className="directoryBrowser" aria-label="local directory browser">
          <div className="directoryPath">
            <span className="currentDirectoryPath">{browseResult?.currentPath ?? (browsePath || "/")}</span>
            <button
              aria-label="mount selected paths"
              disabled={disabled || !canMountSelected}
              onClick={() => void handleMountSelected()}
              type="button"
            >
              <FolderPlus size={15} />
              <span>
                {isSaving ? "挂载中" : selectedLocalPaths.length > 0 ? `挂载 ${selectedLocalPaths.length}` : "挂载"}
              </span>
            </button>
          </div>
          {browseError ? <strong>{browseError}</strong> : null}
          <div className="directoryList">
            {browseResult?.parentPath ? (
              <div
                className="directoryRow"
                key="parent"
              >
                <button
                  className="directoryOpenButton"
                  disabled={disabled || isBrowsing}
                  onClick={() => void loadDirectory(browseResult.parentPath ?? "")}
                  type="button"
                >
                  <ArrowLeft size={15} />
                  <span>上级目录</span>
                </button>
              </div>
            ) : null}
            {directoryEntries.map((entry) => (
              <div
                className="directoryRow"
                key={entry.path}
                title={entry.path}
              >
                <button
                  className="directoryOpenButton"
                  disabled={disabled || isBrowsing || !entry.isReadable}
                  onClick={() => void loadDirectory(entry.path)}
                  type="button"
                >
                  <FolderOpen size={15} />
                  <span>{entry.name}</span>
                </button>
                {!entry.isReadable ? <em>不可访问</em> : null}
                <button
                  aria-label={selectedLocalPaths.includes(entry.path) ? `取消标星 ${entry.name}` : `标星 ${entry.name}`}
                  className={selectedLocalPaths.includes(entry.path) ? "starButton selected" : "starButton"}
                  disabled={disabled || !entry.isReadable}
                  onClick={() => toggleLocalPath(entry.path)}
                  title={selectedLocalPaths.includes(entry.path) ? "取消选择" : "选择挂载"}
                  type="button"
                >
                  <Star size={15} />
                </button>
              </div>
            ))}
            {!browseResult || browseResult.entries.length > 0 || browseResult.parentPath ? null : (
              <div className="sourceEmpty">没有子目录</div>
            )}
          </div>
        </section>
      </aside>
    </div>
  );
}

function formatMediaSourceDisplayName(source: MediaSourceSummary): string {
  const path = source.baseUrl.replace(/\\/g, "/").replace(/\/+$/g, "");
  const name = path.split("/").filter(Boolean).pop();
  return name || source.name;
}

function isScanningSource(scanStatus: LibraryScanStatus | null, source: MediaSourceSummary): boolean {
  if (!scanStatus?.isRunning) {
    return false;
  }

  const currentSourceName = scanStatus.progress?.currentSourceName;
  return currentSourceName ? currentSourceName === source.name : false;
}

function formatSourceScanStatus(scanStatus: LibraryScanStatus | null): string | null {
  const progress = scanStatus?.progress;
  if (!scanStatus?.isRunning || !progress) {
    return null;
  }

  if (progress.totalVideoFileCount > 0) {
    return `刷新 ${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`;
  }

  return "刷新中";
}

type CachePathPickerTarget = "hls" | "subtitles";

function CacheDirectoryPickerModal({
  initialPath,
  label,
  onClose,
  onSelect,
}: {
  initialPath: string;
  label: string;
  onClose: () => void;
  onSelect: (path: string) => void;
}) {
  const [browsePath, setBrowsePath] = useState(initialPath === "默认缓存目录" ? "" : initialPath);
  const [browseResult, setBrowseResult] = useState<LocalDirectoryBrowseResult | null>(null);
  const [browseError, setBrowseError] = useState("");
  const [isBrowsing, setIsBrowsing] = useState(false);

  useEffect(() => {
    let disposed = false;
    void loadDirectory(browsePath, () => disposed);

    return () => {
      disposed = true;
    };
  }, []);

  async function loadDirectory(path: string, isDisposed: () => boolean = () => false) {
    setIsBrowsing(true);
    setBrowseError("");
    try {
      const result = await browseLocalDirectories(path);
      if (!isDisposed()) {
        setBrowseResult(result);
        setBrowsePath(result.currentPath);
      }
    } catch (error) {
      if (!isDisposed()) {
        setBrowseError(error instanceof Error ? error.message : String(error));
      }
    } finally {
      if (!isDisposed()) {
        setIsBrowsing(false);
      }
    }
  }

  const currentPath = browseResult?.currentPath ?? (browsePath || "/");
  const directoryEntries = (browseResult?.entries ?? [])
    .slice()
    .sort((left, right) => {
      if (left.isReadable !== right.isReadable) {
        return left.isReadable ? -1 : 1;
      }

      return left.name.localeCompare(right.name, "zh-CN", { sensitivity: "base", numeric: true });
    });

  return (
    <div className="settingsOverlay modalOverlay cachePathPickerOverlay" role="dialog" aria-label={label} aria-modal="true">
      <button className="settingsBackdrop" aria-label="close cache path picker" onClick={onClose} type="button" />
      <aside className="metadataDialog cachePathPickerDialog">
        <header className="settingsHeader">
          <div>
            <h2>{label}</h2>
            <span>选择 docker 容器可访问的目录</span>
          </div>
          <button aria-label="close cache path picker" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>
        <section className="cachePathPickerBody">
          <div className="directoryBrowser">
            <div className="directoryPath">
              <span className="currentDirectoryPath">{currentPath}</span>
              <button disabled={isBrowsing || !currentPath} onClick={() => onSelect(currentPath)} type="button">
                <CheckCircle2 size={15} />
                <span>使用此目录</span>
              </button>
            </div>
            {browseError ? <strong>{browseError}</strong> : null}
            <div className="directoryList">
              {browseResult?.parentPath ? (
                <div className="directoryRow" key="parent">
                  <button
                    className="directoryOpenButton"
                    disabled={isBrowsing}
                    onClick={() => void loadDirectory(browseResult.parentPath ?? "")}
                    type="button"
                  >
                    <ArrowLeft size={15} />
                    <span>上级目录</span>
                  </button>
                </div>
              ) : null}
              {directoryEntries.map((entry) => (
                <div className="directoryRow" key={entry.path} title={entry.path}>
                  <button
                    className="directoryOpenButton"
                    disabled={isBrowsing || !entry.isReadable}
                    onClick={() => void loadDirectory(entry.path)}
                    type="button"
                  >
                    <FolderOpen size={15} />
                    <span>{entry.name}</span>
                  </button>
                  {!entry.isReadable ? <em>不可访问</em> : null}
                  <button
                    aria-label={`选择 ${entry.name}`}
                    className="starButton selected"
                    disabled={isBrowsing || !entry.isReadable}
                    onClick={() => onSelect(entry.path)}
                    title="选择此目录"
                    type="button"
                  >
                    <CheckCircle2 size={15} />
                  </button>
                </div>
              ))}
              {!browseResult || browseResult.entries.length > 0 || browseResult.parentPath ? null : (
                <div className="sourceEmpty">没有子目录</div>
              )}
            </div>
          </div>
        </section>
      </aside>
    </div>
  );
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function SettingsPanel({
  cacheStatus,
  isSaving,
  onClose,
  onClearHlsCache,
  onClearSubtitleCache,
  onSave,
  settings,
}: {
  cacheStatus: CacheUsageSummary | null;
  isSaving: boolean;
  onClose: () => void;
  onClearHlsCache: () => void;
  onClearSubtitleCache: () => void;
  onSave: (
    tmdb: AppSettingsSnapshot["tmdb"],
    cache: CacheSettings,
    playback: AppSettingsSnapshot["playback"],
    proxy: ProxySettings,
    automation: AppSettingsSnapshot["automation"],
  ) => void;
  settings: AppSettingsSnapshot;
}) {
  const [tmdb, setTmdb] = useState(settings.tmdb);
  const [tmdbCredential, setTmdbCredential] = useState(readTmdbCredential(settings.tmdb));
  const [tmdbTest, setTmdbTest] = useState<TmdbConnectionTestResult | null>(null);
  const [tmdbTestError, setTmdbTestError] = useState("");
  const [isTestingTmdb, setIsTestingTmdb] = useState(false);
  const [cache, setCache] = useState(normalizeCacheSettings(settings.cache));
  const [playback, setPlayback] = useState(normalizePlaybackSettings(settings.playback));
  const [proxy, setProxy] = useState(settings.proxy);
  const [automation, setAutomation] = useState(normalizeAutomationSettings(settings.automation));
  const [automationIntervalHours, setAutomationIntervalHours] = useState(
    String(normalizeAutomationSettings(settings.automation).scheduledLibraryRefreshIntervalHours),
  );
  const [proxyTest, setProxyTest] = useState<ProxyConnectionTestResult | null>(null);
  const [proxyTestError, setProxyTestError] = useState("");
  const [isTestingProxy, setIsTestingProxy] = useState(false);
  const [selfCheck, setSelfCheck] = useState<RuntimeSelfCheckSnapshot | null>(null);
  const [selfCheckError, setSelfCheckError] = useState("");
  const [isCheckingRuntime, setIsCheckingRuntime] = useState(false);
  const [isSelfCheckExpanded, setIsSelfCheckExpanded] = useState(false);
  const [cachePathPicker, setCachePathPicker] = useState<CachePathPickerTarget | null>(null);
  const [cacheClearTarget, setCacheClearTarget] = useState<"hls" | "subtitles" | null>(null);

  useEffect(() => {
    setTmdb(settings.tmdb);
    setTmdbCredential(readTmdbCredential(settings.tmdb));
    setTmdbTest(null);
    setTmdbTestError("");
    setCache(normalizeCacheSettings(settings.cache));
    setPlayback(normalizePlaybackSettings(settings.playback));
    setProxy(settings.proxy);
    setAutomation(normalizeAutomationSettings(settings.automation));
    setAutomationIntervalHours(String(normalizeAutomationSettings(settings.automation).scheduledLibraryRefreshIntervalHours));
    setProxyTest(null);
    setProxyTestError("");
  }, [settings]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSave(
      {
        ...applyTmdbCredential(tmdb, tmdbCredential),
        enableMetadataEnrichment: true,
        enablePosterDownloads: true,
        language: "zh-CN",
      },
      {
        ...cache,
        hlsRetentionHours: Math.min(720, Math.max(1, Math.round(cache.hlsRetentionHours || 24))),
        hlsMaxGb: Math.min(1024, Math.max(1, Math.round(cache.hlsMaxGb || 30))),
        hlsCachePath: cache.hlsCachePath?.trim() ?? "",
        webDavRetentionHours: Math.min(720, Math.max(1, Math.round(cache.webDavRetentionHours || 72))),
        webDavMaxGb: Math.min(1024, Math.max(1, Math.round(cache.webDavMaxGb || 20))),
        subtitleCachePath: cache.subtitleCachePath?.trim() ?? "",
        subtitleMaxGb: Math.min(1024, Math.max(1, Math.round(cache.subtitleMaxGb || 20))),
        subtitleCacheStrategy: normalizeSubtitleCacheStrategy(cache.subtitleCacheStrategy),
      },
      {
        ...playback,
        directStream: true,
        hlsRemux: true,
        transcode: true,
      },
      normalizeProxySettings(proxy),
      normalizeAutomationSettings({
        ...automation,
        scheduledLibraryRefreshIntervalHours: parsePositiveIntegerInput(automationIntervalHours, 24),
      }),
    );
  }

  async function handleTmdbTest() {
    setTmdbTest(null);
    setTmdbTestError("");
    setIsTestingTmdb(true);
    try {
      setTmdbTest(
        await testTmdbConnection({
          ...applyTmdbCredential(tmdb, tmdbCredential),
          enableMetadataEnrichment: true,
          enablePosterDownloads: true,
          language: "zh-CN",
        }),
      );
    } catch (error) {
      setTmdbTestError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsTestingTmdb(false);
    }
  }

  async function handleRuntimeSelfCheck() {
    setSelfCheckError("");
    setIsCheckingRuntime(true);
    try {
      setSelfCheck(await getRuntimeSelfCheck());
      setIsSelfCheckExpanded(true);
    } catch (error) {
      setSelfCheckError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsCheckingRuntime(false);
    }
  }

  async function handleProxyTest() {
    setProxyTest(null);
    setProxyTestError("");
    setIsTestingProxy(true);
    try {
      setProxyTest(await testProxyConnection(normalizeProxySettings(proxy)));
    } catch (error) {
      setProxyTestError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsTestingProxy(false);
    }
  }

  const hlsCacheBucket = cacheStatus?.buckets.find((bucket) => bucket.key === "transcode");
  const subtitleCacheBucket = cacheStatus?.buckets.find((bucket) => bucket.key === "subtitles");
  const hlsCacheDisplayPath = cache.hlsCachePath?.trim() || hlsCacheBucket?.path || "默认缓存目录";
  const subtitleCacheDisplayPath = cache.subtitleCachePath?.trim() || subtitleCacheBucket?.path || "默认缓存目录";
  const settingsHlsBytes = cacheStatus ? sumCacheBuckets(cacheStatus, ["transcode"]) : 0;
  const settingsSubtitleBytes = cacheStatus ? sumCacheBuckets(cacheStatus, ["subtitles"]) : 0;

  function handleSelectCachePath(path: string) {
    if (cachePathPicker === "hls") {
      setCache((current) => ({ ...current, hlsCachePath: path }));
    } else if (cachePathPicker === "subtitles") {
      setCache((current) => ({ ...current, subtitleCachePath: path }));
    }

    setCachePathPicker(null);
  }

  function handleConfirmCacheClear() {
    if (cacheClearTarget === "hls") {
      onClearHlsCache();
    } else if (cacheClearTarget === "subtitles") {
      onClearSubtitleCache();
    }

    setCacheClearTarget(null);
  }

  const runtimeSelfCheckSection = (
    <section className="settingsSection runtimeCheckSection">
      <div className="settingsSectionHeader">
        <h3>运行自检</h3>
        <button disabled={isCheckingRuntime} onClick={() => void handleRuntimeSelfCheck()} type="button">
          <RefreshCw size={15} className={isCheckingRuntime ? "spin" : ""} />
          <span>{isCheckingRuntime ? "检查中" : "检查"}</span>
        </button>
      </div>
      {selfCheckError ? <p className="runtimeCheckError">{selfCheckError}</p> : null}
      {selfCheck ? (
        <>
          <div className="runtimeCheckSummaryRow">
            <div className={`runtimeCheckSummary ${selfCheck.status}`}>
              <strong>{selfCheck.status}</strong>
              <span>{formatDateTime(selfCheck.checkedAt)}</span>
            </div>
            <button
              aria-expanded={isSelfCheckExpanded}
              className="runtimeCheckToggle"
              onClick={() => setIsSelfCheckExpanded((current) => !current)}
              type="button"
            >
              {isSelfCheckExpanded ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
              <span>{isSelfCheckExpanded ? "收起自检信息" : "展开自检信息"}</span>
            </button>
          </div>
          {isSelfCheckExpanded ? (
            <div className="runtimeCheckList">
              {selfCheck.items.map((item) => (
                <article className={`runtimeCheckItem ${item.status}`} key={item.key}>
                  <span>{item.label}</span>
                  <p>{item.detail}</p>
                </article>
              ))}
            </div>
          ) : null}
        </>
      ) : (
        <p className="runtimeCheckHint">检查 FFmpeg、监听端口、缓存目录、SQLite 和硬件解码/编码。</p>
      )}
    </section>
  );

  return (
    <div className="settingsOverlay" role="dialog" aria-label="settings panel" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close settings" onClick={onClose} type="button" />
      <aside className="settingsDrawer">
        <header className="settingsHeader">
          <div>
            <h2>设置</h2>
          </div>
          <div className="settingsHeaderActions">
            <button disabled={isSaving} form="app-settings-form" type="submit">
              <Save size={15} />
              <span>{isSaving ? "保存中" : "保存"}</span>
            </button>
            <button aria-label="close settings" onClick={onClose} type="button">
              <X size={18} />
            </button>
          </div>
        </header>

        <form className="settingsForm" id="app-settings-form" onSubmit={handleSubmit}>
          <section className="settingsSection">
            <div className="settingsSectionHeader">
              <h3>TMDB</h3>
              <button disabled={isTestingTmdb} onClick={() => void handleTmdbTest()} type="button">
                <RefreshCw size={15} className={isTestingTmdb ? "spin" : ""} />
                <span>{isTestingTmdb ? "检测中" : "检测"}</span>
              </button>
            </div>
            <p className="settingsHint">请填写自定义 API Key 或 Read Access Token；也可以通过环境变量提供凭据。</p>
            <label className="settingsField">
              <span>自定义 API</span>
              <input
                autoComplete="off"
                onChange={(event) => setTmdbCredential(event.target.value)}
                placeholder="API Key 或 Bearer Token，填写后优先使用"
                type="password"
                value={tmdbCredential}
              />
            </label>
            {tmdbTest ? (
              <div className={`tmdbTestResult ${tmdbTest.isReachable ? "ok" : "error"}`}>
                <strong>{tmdbTest.isReachable ? "连通" : "失败"}</strong>
                <span>{[tmdbTest.source, tmdbTest.statusCode ? `HTTP ${tmdbTest.statusCode}` : null, tmdbTest.message].filter(Boolean).join(" · ")}</span>
              </div>
            ) : null}
            {tmdbTestError ? <p className="runtimeCheckError">{tmdbTestError}</p> : null}
          </section>

          <section className="settingsSection">
            <div className="settingsSectionHeader">
              <h3>代理</h3>
              <button disabled={isTestingProxy} onClick={() => void handleProxyTest()} type="button">
                <RefreshCw size={15} className={isTestingProxy ? "spin" : ""} />
                <span>{isTestingProxy ? "检测中" : "检测"}</span>
              </button>
            </div>
            <label className="settingsToggle">
              <input
                checked={proxy.isEnabled}
                onChange={(event) => setProxy({ ...proxy, isEnabled: event.target.checked })}
                type="checkbox"
              />
              <span>启用代理</span>
            </label>
            <label className="settingsField">
              <span>代理地址</span>
              <input
                autoComplete="off"
                onChange={(event) => setProxy({ ...proxy, url: event.target.value })}
                placeholder="http://localhost:20171 或 http://192.168.1.2:7890"
                value={proxy.url}
              />
            </label>
            <p className="settingsHint">默认 host 网络可直接填宿主机 localhost。bridge 网络请填写宿主机 LAN IP，并确认代理监听 0.0.0.0/LAN。</p>
            {proxyTest ? (
              <div className={`tmdbTestResult ${proxyTest.isReachable ? "ok" : "error"}`}>
                <strong>{proxyTest.isReachable ? "可用" : "失败"}</strong>
                <span>{[proxyTest.proxyUrl, proxyTest.statusCode ? `HTTP ${proxyTest.statusCode}` : null, proxyTest.message].filter(Boolean).join(" · ")}</span>
              </div>
            ) : null}
            {proxyTestError ? <p className="runtimeCheckError">{proxyTestError}</p> : null}
          </section>

          {runtimeSelfCheckSection}

          <section className="settingsSection">
            <h3>缓存</h3>
            <div className="settingsCacheUsage">
              <span>HLS {formatBytes(settingsHlsBytes)}</span>
              <span>字幕 {formatBytes(settingsSubtitleBytes)}</span>
            </div>
            <div className="settingsActionRow">
              <button
                className="settingsDangerButton"
                disabled={settingsHlsBytes <= 0}
                onClick={() => setCacheClearTarget("hls")}
                type="button"
              >
                <Trash2 size={15} />
                <span>清除 HLS 缓存</span>
              </button>
              <button
                className="settingsDangerButton"
                disabled={settingsSubtitleBytes <= 0}
                onClick={() => setCacheClearTarget("subtitles")}
                type="button"
              >
                <Trash2 size={15} />
                <span>清除字幕缓存</span>
              </button>
            </div>
            <label className="settingsField">
              <span>HLS 保留小时</span>
              <input
                max={720}
                min={1}
                onChange={(event) => setCache({ ...cache, hlsRetentionHours: Number(event.target.value) })}
                type="number"
                value={cache.hlsRetentionHours}
              />
            </label>
            <label className="settingsField">
              <span>HLS 上限 GB</span>
              <input
                max={1024}
                min={1}
                onChange={(event) => setCache({ ...cache, hlsMaxGb: Number(event.target.value) })}
                type="number"
                value={cache.hlsMaxGb}
              />
            </label>
            <div className="settingsPathField">
              <span>HLS 缓存位置</span>
              <div>
                <strong title={hlsCacheDisplayPath}>{hlsCacheDisplayPath}</strong>
                <button onClick={() => setCachePathPicker("hls")} type="button">
                  <FolderOpen size={15} />
                  <span>选择</span>
                </button>
                <button onClick={() => setCache({ ...cache, hlsCachePath: "" })} type="button">
                  <span>默认</span>
                </button>
              </div>
            </div>
            <label className="settingsField">
              <span>图片清理范围</span>
              <select
                onChange={(event) => setCache({ ...cache, imageCleanupScope: event.target.value })}
                value={cache.imageCleanupScope}
              >
                <option value="orphans-and-untracked">孤儿记录和残留文件</option>
                <option value="orphans-only">仅孤儿记录</option>
              </select>
            </label>
            <div className="settingsPathField">
              <span>字幕缓存位置</span>
              <div>
                <strong title={subtitleCacheDisplayPath}>{subtitleCacheDisplayPath}</strong>
                <button onClick={() => setCachePathPicker("subtitles")} type="button">
                  <FolderOpen size={15} />
                  <span>选择</span>
                </button>
                <button onClick={() => setCache({ ...cache, subtitleCachePath: "" })} type="button">
                  <span>默认</span>
                </button>
              </div>
            </div>
            <label className="settingsField">
              <span>字幕缓存上限 GB</span>
              <input
                max={1024}
                min={1}
                onChange={(event) => setCache({ ...cache, subtitleMaxGb: Number(event.target.value) })}
                type="number"
                value={cache.subtitleMaxGb ?? 20}
              />
            </label>
            <label className="settingsField">
              <span>字幕预缓存方案</span>
              <select
                onChange={(event) => setCache({ ...cache, subtitleCacheStrategy: event.target.value })}
                value={cache.subtitleCacheStrategy ?? "disabled"}
              >
                <option value="disabled">关闭字幕预缓存</option>
                <option value="optimized">优化缓存方案</option>
                <option value="full">全量缓存</option>
              </select>
            </label>
            <p className="settingsHint">
              默认关闭后台字幕预处理；播放时仍会即时转换所选字幕。优化缓存只预处理更可能播放的下一集；全量缓存会遍历媒体库里所有可缓存字幕，耗时和磁盘占用更高。
            </p>
          </section>

          <section className="settingsSection">
            <h3>自动扫描</h3>
            <label className="settingsToggle">
              <input
                checked={automation.scheduledLibraryRefreshEnabled}
                onChange={(event) =>
                  setAutomation({ ...automation, scheduledLibraryRefreshEnabled: event.target.checked })
                }
                type="checkbox"
              />
              <span>定时扫描刮削</span>
            </label>
            <label className="settingsField">
              <span>扫描间隔（小时）</span>
              <input
                disabled={!automation.scheduledLibraryRefreshEnabled}
                inputMode="numeric"
                max={720}
                min={1}
                onChange={(event) => setAutomationIntervalHours(event.target.value.replace(/[^\d]/g, ""))}
                pattern="[0-9]*"
                type="text"
                value={automationIntervalHours}
              />
            </label>
          </section>

          <section className="settingsSection">
            <h3>播放</h3>
            <label className="settingsToggle">
              <input
                checked={playback.showEpisodeDetails}
                onChange={(event) => setPlayback({ ...playback, showEpisodeDetails: event.target.checked })}
                type="checkbox"
              />
              <span>分集详情</span>
            </label>
            <label className="settingsField">
              <span>浏览器播放质量</span>
              <select
                onChange={(event) => setPlayback({ ...playback, playbackQualityPreference: event.target.value })}
                value={playback.playbackQualityPreference}
              >
                <option value="original-priority">原画优先</option>
                <option value="auto">自动</option>
                <option value="compatibility">兼容优先</option>
              </select>
            </label>
            <label className="settingsField">
              <span>默认音轨优先</span>
              <select
                onChange={(event) => setPlayback({ ...playback, defaultAudioLanguage: event.target.value })}
                value={playback.defaultAudioLanguage}
              >
                <option value="smart">智能匹配影视制作国别</option>
                <option value="en">英语</option>
                <option value="ja">日语</option>
              </select>
            </label>
            <label className="settingsField">
              <span>默认字幕优先</span>
              <select
                onChange={(event) => setPlayback({ ...playback, defaultSubtitleLanguage: event.target.value })}
                value={playback.defaultSubtitleLanguage}
              >
                <option value="zh">{"中文 > 英语 > 最后一条"}</option>
                <option value="en">{"英语 > 中文 > 最后一条"}</option>
              </select>
            </label>
          </section>

          <section className="settingsSection">
            <div className="settingsVersionRow">
              <div>
                <h3>版本</h3>
                <span>当前版本 {APP_VERSION}</span>
              </div>
              <button
                onClick={() => window.open(APP_UPDATE_URL, "_blank", "noopener,noreferrer")}
                type="button"
              >
                <ExternalLink size={15} />
                <span>检查更新</span>
              </button>
            </div>
          </section>

        </form>
      </aside>
      {cacheClearTarget ? (
        <div className="settingsOverlay modalOverlay settingsConfirmOverlay" role="dialog" aria-modal="true" aria-label="确认清除缓存">
          <button className="settingsBackdrop" aria-label="cancel cache cleanup" onClick={() => setCacheClearTarget(null)} type="button" />
          <div className="settingsConfirmDialog">
            <h3>确认清除{cacheClearTarget === "hls" ? " HLS" : "字幕"}缓存？</h3>
            <p>
              将删除当前可清理的{cacheClearTarget === "hls" ? " HLS 转码缓存" : "字幕缓存"}文件。
              {cacheClearTarget === "hls" ? " 正在使用的播放会话不会被中断。" : ""}
            </p>
            <div>
              <button onClick={() => setCacheClearTarget(null)} type="button">
                取消
              </button>
              <button className="settingsDangerButton" onClick={handleConfirmCacheClear} type="button">
                <Trash2 size={15} />
                <span>确认清除</span>
              </button>
            </div>
          </div>
        </div>
      ) : null}
      {cachePathPicker ? (
        <CacheDirectoryPickerModal
          initialPath={cachePathPicker === "hls" ? hlsCacheDisplayPath : subtitleCacheDisplayPath}
          label={cachePathPicker === "hls" ? "HLS 缓存位置" : "字幕缓存位置"}
          onClose={() => setCachePathPicker(null)}
          onSelect={handleSelectCachePath}
        />
      ) : null}
    </div>
  );
}

function readTmdbCredential(settings: AppSettingsSnapshot["tmdb"]): string {
  return settings.customAccessToken || settings.customApiKey || "";
}

function applyTmdbCredential(
  settings: AppSettingsSnapshot["tmdb"],
  credential: string,
): AppSettingsSnapshot["tmdb"] {
  const normalizedCredential = credential.trim();
  if (!normalizedCredential) {
    return { ...settings, customApiKey: "", customAccessToken: "" };
  }

  if (looksLikeTmdbAccessToken(normalizedCredential)) {
    return {
      ...settings,
      customApiKey: "",
      customAccessToken: normalizedCredential.replace(/^Bearer\s+/i, "").trim(),
    };
  }

  return { ...settings, customApiKey: normalizedCredential, customAccessToken: "" };
}

function normalizeCacheSettings(settings?: Partial<CacheSettings> | null): CacheSettings {
  return {
    ...defaultCacheSettings,
    ...settings,
    hlsRetentionHours: settings?.hlsRetentionHours ?? 24,
    hlsMaxGb: settings?.hlsMaxGb ?? 30,
    hlsCachePath: settings?.hlsCachePath ?? "",
    imageCleanupScope: settings?.imageCleanupScope ?? "orphans-and-untracked",
    webDavRetentionHours: settings?.webDavRetentionHours ?? 72,
    webDavMaxGb: settings?.webDavMaxGb ?? 20,
    subtitleCachePath: settings?.subtitleCachePath ?? "",
    subtitleMaxGb: settings?.subtitleMaxGb ?? 20,
    subtitleCacheStrategy: normalizeSubtitleCacheStrategy(settings?.subtitleCacheStrategy),
  };
}

function normalizeSubtitleCacheStrategy(value?: string | null): "disabled" | "optimized" | "full" {
  if (value === "full" || value === "optimized") {
    return value;
  }

  return "disabled";
}

function looksLikeTmdbAccessToken(value: string): boolean {
  const token = value.replace(/^Bearer\s+/i, "").trim();
  return token.startsWith("eyJ") || token.length > 80;
}

function normalizePlaybackSettings(settings?: Partial<AppSettingsSnapshot["playback"]> | null): AppSettingsSnapshot["playback"] {
  const audioLanguage = settings?.defaultAudioLanguage;
  const subtitleLanguage = settings?.defaultSubtitleLanguage;
  return {
    ...defaultPlaybackSettings,
    ...settings,
    directStream: true,
    hlsRemux: true,
    transcode: true,
    showEpisodeDetails: settings?.showEpisodeDetails ?? true,
    defaultAudioLanguage:
      audioLanguage === "en" || audioLanguage === "ja" ? audioLanguage : "smart",
    defaultSubtitleLanguage: subtitleLanguage === "en" ? "en" : "zh",
    playbackQualityPreference: normalizePlaybackQualityPreference(settings?.playbackQualityPreference),
  };
}

function normalizePlaybackQualityPreference(value?: string | null): AppSettingsSnapshot["playback"]["playbackQualityPreference"] {
  if (value === "original-priority" || value === "compatibility") {
    return value;
  }

  return "auto";
}

function normalizeAutomationSettings(
  settings?: Partial<AppSettingsSnapshot["automation"]> | null,
): AppSettingsSnapshot["automation"] {
  return {
    ...defaultAutomationSettings,
    ...settings,
    scheduledLibraryRefreshEnabled: settings?.scheduledLibraryRefreshEnabled ?? false,
    scheduledLibraryRefreshIntervalHours: normalizeIntervalHours(settings?.scheduledLibraryRefreshIntervalHours),
  };
}

function normalizeIntervalHours(value?: number | null): number {
  return Math.min(720, Math.max(1, Math.round(value || 24)));
}

function parsePositiveIntegerInput(value: string, fallback: number): number {
  const parsed = Number(value.trim());
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function normalizeProxySettings(settings: ProxySettings): ProxySettings {
  return {
    ...settings,
    url: settings.url.trim(),
    username: "",
    password: "",
    bypassList: "",
  };
}

function CacheMaintenance({
  cacheStatus,
  cacheSettings,
  disabled,
  isSaving,
  onCleanupAssets,
  onCleanupTranscode,
  onSaveSettings,
}: {
  cacheStatus: CacheUsageSummary | null;
  cacheSettings: CacheSettings | null;
  disabled: boolean;
  isSaving: boolean;
  onCleanupAssets: () => void;
  onCleanupTranscode: () => void;
  onSaveSettings: (
    hlsRetentionHours: number,
    hlsMaxGb: number,
    imageCleanupScope: string,
    webDavRetentionHours: number,
    webDavMaxGb: number,
    subtitleCachePath: string,
    subtitleMaxGb: number,
    subtitleCacheStrategy: string,
  ) => void;
}) {
  const [hlsRetentionHours, setHlsRetentionHours] = useState(24);
  const [hlsMaxGb, setHlsMaxGb] = useState(30);
  const [imageCleanupScope, setImageCleanupScope] = useState("orphans-and-untracked");
  const [subtitleCachePath, setSubtitleCachePath] = useState("");
  const [subtitleMaxGb, setSubtitleMaxGb] = useState(20);
  const [subtitleCacheStrategy, setSubtitleCacheStrategy] = useState("disabled");

  useEffect(() => {
    if (!cacheSettings) {
      return;
    }

    setHlsRetentionHours(cacheSettings.hlsRetentionHours);
    setHlsMaxGb(cacheSettings.hlsMaxGb);
    setImageCleanupScope(cacheSettings.imageCleanupScope);
    setSubtitleCachePath(cacheSettings.subtitleCachePath ?? "");
    setSubtitleMaxGb(cacheSettings.subtitleMaxGb ?? 20);
    setSubtitleCacheStrategy(normalizeSubtitleCacheStrategy(cacheSettings.subtitleCacheStrategy));
  }, [
    cacheSettings?.hlsRetentionHours,
    cacheSettings?.hlsMaxGb,
    cacheSettings?.imageCleanupScope,
    cacheSettings?.subtitleCachePath,
    cacheSettings?.subtitleMaxGb,
    cacheSettings?.subtitleCacheStrategy,
  ]);

  if (!cacheStatus) {
    return null;
  }

  const imageBytes = sumCacheBuckets(cacheStatus, ["posters", "thumbnails"]);
  const transcodeBytes = sumCacheBuckets(cacheStatus, ["transcode"]);
  const subtitleBytes = sumCacheBuckets(cacheStatus, ["subtitles"]);

  return (
    <section className="cacheMaintenance" aria-label="cache maintenance">
      <div>
        <strong>{formatBytes(cacheStatus.totalBytes)}</strong>
        <span>
          图片 {formatBytes(imageBytes)} · HLS {formatBytes(transcodeBytes)} · 字幕 {formatBytes(subtitleBytes)} · {cacheStatus.totalFileCount} 文件
        </span>
      </div>
      <div className="cacheActions">
        <input
          aria-label="hls retention hours"
          min={1}
          max={720}
          onChange={(event) => setHlsRetentionHours(Number(event.target.value))}
          type="number"
          value={hlsRetentionHours}
        />
        <input
          aria-label="hls max gb"
          min={1}
          max={1024}
          onChange={(event) => setHlsMaxGb(Number(event.target.value))}
          type="number"
          value={hlsMaxGb}
        />
        <select
          aria-label="image cleanup scope"
          onChange={(event) => setImageCleanupScope(event.target.value)}
          value={imageCleanupScope}
        >
          <option value="orphans-and-untracked">孤儿+残留</option>
          <option value="orphans-only">仅孤儿</option>
        </select>
        <input
          aria-label="subtitle max gb"
          min={1}
          max={1024}
          onChange={(event) => setSubtitleMaxGb(Number(event.target.value))}
          type="number"
          value={subtitleMaxGb}
        />
        <select
          aria-label="subtitle cache strategy"
          onChange={(event) => setSubtitleCacheStrategy(event.target.value)}
          value={subtitleCacheStrategy}
        >
          <option value="disabled">关闭字幕</option>
          <option value="optimized">优化字幕</option>
          <option value="full">全量字幕</option>
        </select>
        <button
          disabled={disabled || isSaving || !cacheSettings}
          onClick={() => onSaveSettings(
            hlsRetentionHours,
            hlsMaxGb,
            imageCleanupScope,
            cacheSettings?.webDavRetentionHours ?? 72,
            cacheSettings?.webDavMaxGb ?? 20,
            subtitleCachePath,
            subtitleMaxGb,
            subtitleCacheStrategy,
          )}
        >
          <span>{isSaving ? "保存中" : "保存"}</span>
        </button>
        <button disabled={disabled} onClick={onCleanupAssets}>
          <Trash2 size={15} />
          <span>图片</span>
        </button>
        <button disabled={disabled} onClick={onCleanupTranscode}>
          <Trash2 size={15} />
          <span>HLS</span>
        </button>
      </div>
    </section>
  );
}

function sumCacheBuckets(cacheStatus: CacheUsageSummary, keys: string[]): number {
  return cacheStatus.buckets
    .filter((bucket) => keys.includes(bucket.key))
    .reduce((total, bucket) => total + bucket.bytes, 0);
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }

  return `${value >= 10 ? value.toFixed(1) : value.toFixed(2)} ${units[unitIndex]}`;
}

function TaskCenter({
  snapshot,
  onCancel,
}: {
  snapshot: BackgroundTaskSnapshot | null;
  onCancel: (taskId: string) => void;
}) {
  const tasks = snapshot?.tasks.slice(0, 4) ?? [];
  if (tasks.length === 0) {
    return null;
  }

  return (
    <section className="taskCenter" aria-label="task center">
      {tasks.map((task) => (
        <div className="taskRow" key={task.id}>
          <div>
            <strong>{task.title}</strong>
            <span>{formatBackgroundTask(task)}</span>
            {task.progressPercent !== null && task.isRunning ? (
              <progress max={100} value={Math.round(task.progressPercent)} />
            ) : null}
          </div>
          {task.canCancel ? (
            <button aria-label={`cancel ${task.title}`} onClick={() => onCancel(task.id)}>
              <CircleStop size={16} />
            </button>
          ) : null}
        </div>
      ))}
    </section>
  );
}

function CacheCompleteNotice({ message, onClose }: { message: string; onClose: () => void }) {
  return (
    <div className="cacheCompleteNotice" role="status" aria-live="polite">
      <div>
        <CheckCircle2 size={22} />
        <strong>缓存完成</strong>
        <span>{message}</span>
        <button aria-label="close cache notice" onClick={onClose} type="button">
          <X size={16} />
        </button>
      </div>
    </div>
  );
}

function TmdbConnectionAlert({
  message,
  onClose,
  onOpenSettings,
}: {
  message: string;
  onClose: () => void;
  onOpenSettings: () => void;
}) {
  return (
    <div className="settingsOverlay modalOverlay" role="alertdialog" aria-label="tmdb connection alert" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close tmdb alert" onClick={onClose} type="button" />
      <section className="metadataDialog tmdbConnectionDialog">
        <header className="settingsHeader">
          <div>
            <h2>TMDB API 无法连接</h2>
          </div>
          <button aria-label="close tmdb alert" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>
        <div className="metadataDialogBody">
          <p className="tmdbConnectionMessage">
            检测到有待刮削的影视，但 TMDB 暂时无法连接。请开启代理，或在设置中添加自定义 TMDB API 后重新刮削。
          </p>
          <p className="tmdbConnectionDetail">{formatUserVisibleError(message)}</p>
        </div>
        <footer className="settingsFooter">
          <button onClick={onClose} type="button">
            关闭
          </button>
          <button onClick={onOpenSettings} type="button">
            <Settings size={15} />
            <span>打开设置</span>
          </button>
        </footer>
      </section>
    </div>
  );
}

function formatBackgroundTask(task: BackgroundTaskSnapshot["tasks"][number]): string {
  if (task.isCancellationRequested) {
    return "正在取消";
  }

  if (task.isRunning) {
    return task.progressText ?? "正在执行";
  }

  if (task.state === "completed") {
    return task.resultText ?? "已完成";
  }

  if (task.state === "canceled") {
    return "已取消";
  }

  if (task.state === "failed") {
    return task.errorMessage ? formatUserVisibleError(task.errorMessage) : "执行失败";
  }

  return task.progressText ?? task.state;
}

function MetadataSearchModal({
  state,
  onApply,
  onBindDouban,
  onChangeDoubanSubject,
  onChangeQuery,
  onChangeYear,
  onClose,
  onOpenDoubanSearch,
  onSearch,
}: {
  state: MetadataSearchState;
  onApply: (match: TmdbMetadataMatch) => void;
  onBindDouban: () => void;
  onChangeDoubanSubject: (subject: string) => void;
  onChangeQuery: (query: string) => void;
  onChangeYear: (year: string) => void;
  onClose: () => void;
  onOpenDoubanSearch: () => void;
  onSearch: () => void;
}) {
  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSearch();
  }

  return (
    <div className="settingsOverlay modalOverlay" role="dialog" aria-label="metadata search" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close metadata search" onClick={onClose} type="button" />
      <section className="metadataDialog">
        <header className="settingsHeader">
          <div>
            <h2>手动匹配</h2>
            <span>{state.item.title}</span>
          </div>
          <button aria-label="close metadata search" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <div className="metadataDialogBody">
          <div className="sourceInfoBox">
            <span>源文件名称</span>
            <strong>{state.isLoadingDetail ? "正在读取源文件信息" : sourceFileLabel(state.detail)}</strong>
          </div>

          <form className="metadataSearchForm" onSubmit={handleSubmit}>
            <input
              aria-label="TMDB search query"
              onChange={(event) => onChangeQuery(event.target.value)}
              placeholder="输入正确的影视名称"
              value={state.query}
            />
            <input
              aria-label="TMDB search year"
              inputMode="numeric"
              onChange={(event) => onChangeYear(event.target.value)}
              placeholder="年份"
              value={state.year}
            />
            <button disabled={state.isSearching || state.isApplying || !state.query.trim()} type="submit">
              <Search size={16} />
              <span>{state.isSearching ? "搜索中" : "搜索"}</span>
            </button>
          </form>

          <div className="metadataSearchForm doubanBindForm">
            <input
              aria-label="Douban subject"
              onChange={(event) => onChangeDoubanSubject(event.target.value)}
              placeholder="豆瓣链接或 subject ID"
              value={state.doubanSubject}
            />
            <button
              disabled={state.isSearching || state.isApplying || state.isBindingDouban}
              onClick={onOpenDoubanSearch}
              type="button"
            >
              <ExternalLink size={15} />
              <span>浏览器搜索豆瓣</span>
            </button>
            <button
              disabled={state.isSearching || state.isApplying || state.isBindingDouban || !state.doubanSubject.trim()}
              onClick={onBindDouban}
              type="button"
            >
              <span>{state.isBindingDouban ? "绑定中" : "绑定链接"}</span>
            </button>
          </div>
          <p className="doubanBindHint">找到正确豆瓣条目后复制 subject 链接回来绑定；Docker 版会尝试抓取豆瓣评分，遇到 403 时只保存链接。</p>
          {state.detail?.douban ? (
            <div className="doubanBindingStatus">
              <span>当前绑定</span>
              <a href={state.detail.douban.subjectUrl} rel="noreferrer" target="_blank">
                {state.detail.douban.subjectUrl}
              </a>
              {state.detail.douban.rating ? <strong>豆瓣 {state.detail.douban.rating.toFixed(1)}</strong> : null}
              {!state.detail.douban.rating ? <em>未抓到豆瓣评分，可能是豆瓣返回 403 或验证页面。</em> : null}
            </div>
          ) : null}

          {state.error ? <strong className="metadataDialogError">{state.error}</strong> : null}
          {!state.error && state.candidates.length === 0 && !state.isSearching ? (
            <div className="metadataDialogEmpty">暂无搜索结果，请输入关键字后回车开始匹配</div>
          ) : null}
          {state.isSearching ? <div className="metadataDialogEmpty">正在向 TMDB 请求数据</div> : null}

          {state.candidates.length > 0 ? (
            <div className="metadataDialogResults">
              {state.candidates.map((candidate) => (
                <article className="metadataCandidate" key={`${candidate.mediaType}-${candidate.id}`}>
                  <div className="metadataPoster">
                    {tmdbPosterUrl(candidate.posterPath) ? (
                      <img alt="" src={tmdbPosterUrl(candidate.posterPath)!} />
                    ) : (
                      <span>TMDB</span>
                    )}
                  </div>
                  <div>
                    <h3>{candidate.title}</h3>
                    <p>
                      {[candidate.releaseDate?.slice(0, 4), candidate.voteAverage ? candidate.voteAverage.toFixed(1) : null]
                        .filter(Boolean)
                        .join(" · ") || "TMDB"}
                    </p>
                    <span>{candidate.overview || "暂无简介"}</span>
                  </div>
                  <button disabled={state.isApplying} onClick={() => onApply(candidate)} type="button">
                    关联此项
                  </button>
                </article>
              ))}
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}

function CustomMetadataEditModal({
  detail,
  episode,
  errorText,
  isSaving,
  onClose,
  onSave,
}: {
  detail: LibraryItemDetail;
  episode?: EpisodeDetail | null;
  errorText: string;
  isSaving: boolean;
  onClose: () => void;
  onSave: (request: LibraryItemCustomMetadataUpdateRequest) => void;
}) {
  const [title, setTitle] = useState(detail.title);
  const [releaseDate, setReleaseDate] = useState(detail.releaseDate ?? "");
  const [tmdbVoteAverage, setTmdbVoteAverage] = useState(
    typeof detail.voteAverage === "number" && Number.isFinite(detail.voteAverage) ? detail.voteAverage.toFixed(1) : "",
  );
  const [doubanRating, setDoubanRating] = useState(
    typeof detail.doubanRating === "number" && Number.isFinite(detail.doubanRating) ? detail.doubanRating.toFixed(1) : "",
  );
  const [overview, setOverview] = useState(detail.overview ?? "");
  const [episodeSubtitle, setEpisodeSubtitle] = useState(extractEpisodeSubtitle(episode?.title));
  const [posterFile, setPosterFile] = useState<File | null>(null);
  const [posterPreview, setPosterPreview] = useState<string | null>(null);
  const [localError, setLocalError] = useState("");

  useEffect(() => {
    if (!posterFile) {
      setPosterPreview(null);
      return;
    }

    const previewUrl = URL.createObjectURL(posterFile);
    setPosterPreview(previewUrl);
    return () => URL.revokeObjectURL(previewUrl);
  }, [posterFile]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLocalError("");

    const trimmedTitle = title.trim();
    if (!trimmedTitle) {
      setLocalError("请填写影视名称。");
      return;
    }

    const trimmedTMDBVote = tmdbVoteAverage.trim();
    const parsedTMDBVote = trimmedTMDBVote ? Number(trimmedTMDBVote) : null;
    if (parsedTMDBVote !== null && (!Number.isFinite(parsedTMDBVote) || parsedTMDBVote < 0 || parsedTMDBVote > 10)) {
      setLocalError("TMDB评分需要在 0 到 10 之间。");
      return;
    }

    const trimmedDoubanRating = doubanRating.trim();
    const parsedDoubanRating = trimmedDoubanRating ? Number(trimmedDoubanRating) : null;
    if (parsedDoubanRating !== null && (!Number.isFinite(parsedDoubanRating) || parsedDoubanRating < 0 || parsedDoubanRating > 10)) {
      setLocalError("豆瓣评分需要在 0 到 10 之间。");
      return;
    }

    onSave({
      title: trimmedTitle,
      releaseDate: releaseDate.trim() || null,
      overview: overview.trim() || null,
      voteAverage: parsedTMDBVote,
      doubanRating: parsedDoubanRating,
      posterFile,
      episodeId: episode?.id ?? null,
      episodeSubtitle: episode ? episodeSubtitle.trim() : null,
    });
  }

  const currentPoster = detail.posterAssetId ? posterUrl(detail.posterAssetId) : null;
  const episodeLabel = episode ? episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber) : null;

  return (
    <div className="settingsOverlay modalOverlay" role="dialog" aria-label="custom metadata editor" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close custom editor" onClick={onClose} type="button" />
      <form className="metadataDialog editDialog" onSubmit={handleSubmit}>
        <header className="settingsHeader">
          <div>
            <h2>手动编辑资料</h2>
            <span>{episodeLabel ? `${detail.title} · ${episodeLabel}` : detail.title}</span>
          </div>
          <button aria-label="close custom editor" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <div className="metadataDialogBody editDialogBody">
          <section className="editSection">
            <h3>源文件信息</h3>
            <div className="sourceInfoBox compact">
              <span>文件</span>
              <strong>{episodeSourceFileLabel(episode) ?? sourceFileLabel(detail)}</strong>
            </div>
          </section>

          <section className="editSection">
            <h3>基础信息</h3>
            <label className="settingsField">
              <span>影视名称</span>
              <input disabled={isSaving} onChange={(event) => setTitle(event.target.value)} value={title} />
            </label>
            <label className="settingsField">
              <span>上映时间</span>
              <input
                disabled={isSaving}
                onChange={(event) => setReleaseDate(event.target.value)}
                placeholder="2025-01-01"
                value={releaseDate}
              />
            </label>
            <label className="settingsField">
              <span>TMDB评分</span>
              <input
                disabled={isSaving}
                inputMode="decimal"
                onChange={(event) => setTmdbVoteAverage(event.target.value)}
                placeholder="0.0 - 10.0"
                value={tmdbVoteAverage}
              />
            </label>
            <label className="settingsField">
              <span>豆瓣评分</span>
              <input
                disabled={isSaving}
                inputMode="decimal"
                onChange={(event) => setDoubanRating(event.target.value)}
                placeholder="0.0 - 10.0"
                value={doubanRating}
              />
            </label>
          </section>

          <section className="editSection">
            <h3>剧情简介</h3>
            <textarea disabled={isSaving} onChange={(event) => setOverview(event.target.value)} value={overview} />
          </section>

          {episode ? (
            <section className="editSection">
              <h3>分集信息</h3>
              <label className="settingsField">
                <span>副标题</span>
                <input
                  disabled={isSaving}
                  onChange={(event) => setEpisodeSubtitle(event.target.value)}
                  placeholder="例如：播客"
                  value={episodeSubtitle}
                />
              </label>
            </section>
          ) : null}

          <section className="editSection">
            <h3>海报管理</h3>
            <div className="posterEditRow">
              <div className="posterPreview">
                {posterPreview || currentPoster ? <img alt="" src={posterPreview ?? currentPoster!} /> : <span>暂无海报</span>}
              </div>
              <label className="settingsField">
                <span>选择本地图片</span>
                <input
                  accept="image/png,image/jpeg,image/webp"
                  disabled={isSaving}
                  onChange={(event) => setPosterFile(event.target.files?.[0] ?? null)}
                  type="file"
                />
              </label>
            </div>
          </section>

          {localError || errorText ? <strong className="metadataDialogError">{localError || errorText}</strong> : null}
        </div>

        <footer className="settingsFooter">
          <button disabled={isSaving} onClick={onClose} type="button">
            取消
          </button>
          <button disabled={isSaving} type="submit">
            <Save size={16} />
            <span>{isSaving ? "保存中" : "保存修改并锁定"}</span>
          </button>
        </footer>
      </form>
    </div>
  );
}

function sourceFileLabel(detail: LibraryItemDetail | null): string {
  const file = detail?.videoFiles[0] ?? null;
  if (!file) {
    return "未找到关联的视频文件";
  }

  return file.relativePath || file.fileName || "未知文件名";
}

function episodeSourceFileLabel(episode?: EpisodeDetail | null): string | null {
  const file = episode?.videoFile ?? null;
  if (!file) {
    return null;
  }

  return file.relativePath || file.fileName || "未知文件名";
}

function extractEpisodeSubtitle(title?: string | null): string {
  if (!title) {
    return "";
  }

  const separatorIndex = title.indexOf("·");
  return separatorIndex < 0 ? "" : title.slice(separatorIndex + 1).trim();
}

function PosterCard({
  hlsSelected = false,
  hlsSelectionMode = false,
  item,
  onEdit,
  onOpen,
  onSearchMetadata,
  onToggleHlsSelection,
  onToggleWatched,
  showProgress = false,
}: {
  hlsSelected?: boolean;
  hlsSelectionMode?: boolean;
  item: LibraryItemSummary;
  onEdit?: (item: LibraryItemSummary) => void;
  onOpen?: (item: LibraryItemSummary) => void;
  onSearchMetadata?: (item: LibraryItemSummary) => void;
  onToggleHlsSelection?: (itemId: string) => void;
  onToggleWatched?: (item: LibraryItemSummary) => void;
  showProgress?: boolean;
}) {
  const year = item.releaseDate?.slice(0, 4) ?? "";
  const tmdbRating =
    typeof item.voteAverage === "number" && Number.isFinite(item.voteAverage) ? item.voteAverage.toFixed(1) : null;
  const doubanRating =
    typeof item.doubanRating === "number" && Number.isFinite(item.doubanRating) ? item.doubanRating.toFixed(1) : null;
  const progressPercent = playbackProgressPercent(item);

  function handleCardAction(event: MouseEvent<HTMLButtonElement>, action?: (target: LibraryItemSummary) => void) {
    event.preventDefault();
    event.stopPropagation();
    action?.(item);
  }

  function handleOpenOrSelect() {
    if (hlsSelectionMode) {
      onToggleHlsSelection?.(item.id);
      return;
    }

    onOpen?.(item);
  }

  return (
    <article className={hlsSelected ? "posterCard selectedForHls" : "posterCard"} onClick={handleOpenOrSelect}>
      <div className="posterArt">
        {item.posterAssetId ? <img alt="" src={posterUrl(item.posterAssetId)} /> : null}
        {tmdbRating || doubanRating ? (
          <div className="posterRatingStack">
            {tmdbRating ? (
              <span className="posterRating tmdbRating">{tmdbRating}</span>
            ) : null}
            {doubanRating ? (
              <span className="posterRating doubanRating">{doubanRating}</span>
            ) : null}
          </div>
        ) : null}
        {hlsSelectionMode ? (
          <button
            aria-label={hlsSelected ? `取消 HLS 缓存 ${item.title}` : `选择 HLS 缓存 ${item.title}`}
            className={hlsSelected ? "hlsSelectBadge selected" : "hlsSelectBadge"}
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onToggleHlsSelection?.(item.id);
            }}
            title={hlsSelected ? "取消缓存选择" : "选择缓存"}
            type="button"
          >
            {hlsSelected ? <CheckCircle2 size={16} /> : <Download size={15} />}
          </button>
        ) : null}
        <div className="posterArtActions">
          <button
            aria-label={`搜索刮削 ${item.title}`}
            onClick={(event) => handleCardAction(event, onSearchMetadata)}
            title="搜索刮削"
            type="button"
          >
            <Search size={16} />
          </button>
          <button
            aria-label={`自定义编辑 ${item.title}`}
            onClick={(event) => handleCardAction(event, onEdit)}
            title="自定义编辑"
            type="button"
          >
            <Pencil size={15} />
          </button>
        </div>
      </div>
      {showProgress ? (
        <div
          aria-label="播放进度"
          aria-valuemax={100}
          aria-valuemin={0}
          aria-valuenow={progressPercent}
          className="posterProgress"
          role="progressbar"
        >
          <div className="posterProgressFill" style={{ width: `${progressPercent}%` }} />
        </div>
      ) : null}
      <div className="posterTitleRow">
        <h3>{item.title}</h3>
        <button
          aria-label={item.isWatched ? `标为未看 ${item.title}` : `标为已看 ${item.title}`}
          className={item.isWatched ? "watchedButton watched" : "watchedButton"}
          onClick={(event) => handleCardAction(event, onToggleWatched)}
          title={item.isWatched ? "标为未看" : "标为已看"}
          type="button"
        >
          {item.isWatched ? <CheckCircle2 size={15} /> : <Circle size={15} />}
        </button>
      </div>
      {year ? <p>{year}</p> : null}
    </article>
  );
}

function playbackProgressPercent(item: LibraryItemSummary): number {
  if (item.maxDurationSeconds <= 0 || item.maxProgressSeconds <= 0) {
    return 0;
  }

  return Math.round((Math.min(item.maxProgressSeconds, item.maxDurationSeconds) / item.maxDurationSeconds) * 100);
}

function parseDoubanMetadataList(value?: string | null): string[] {
  if (!value?.trim()) {
    return [];
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (Array.isArray(parsed)) {
      return parsed
        .map((item) => (typeof item === "string" ? item.trim() : ""))
        .filter(Boolean);
    }
  } catch {
    // Fall back to delimiter parsing for older cached values.
  }

  return value
    .split(/[\/,，、|]/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function DetailView({
  detail,
  errorText,
  hlsSelectionMode,
  isHlsTaskRunning,
  selectedSeasonId,
  selectedHlsItemIds,
  selectedHlsVideoFileIds,
  showEpisodeDetails,
  onBack,
  onEditMetadata,
  onStartHlsCache,
  onToggleHlsFile,
  onToggleHlsItem,
  onToggleHlsSelectionMode,
  onPlay,
  onSeasonChange,
}: {
  detail: LibraryItemDetail;
  errorText: string;
  hlsSelectionMode: boolean;
  isHlsTaskRunning: boolean;
  selectedSeasonId: string;
  selectedHlsItemIds: string[];
  selectedHlsVideoFileIds: string[];
  showEpisodeDetails: boolean;
  onBack: () => void;
  onEditMetadata: (episode: EpisodeDetail) => void;
  onStartHlsCache: () => void;
  onToggleHlsFile: (fileId: string) => void;
  onToggleHlsItem: (itemId: string) => void;
  onToggleHlsSelectionMode: () => void;
  onPlay: (file: VideoFileSummary, options?: { refreshMain?: boolean }) => void;
  onSeasonChange: (seasonId: string) => void;
}) {
  const poster = detail.posterAssetId ? posterUrl(detail.posterAssetId) : null;
  const year = detail.releaseDate?.slice(0, 4);
  const overview = detail.overview || detail.douban?.summary || "暂无简介";
  const fusedTextMetadataParts = [
    ...parseDoubanMetadataList(detail.douban?.genres).slice(0, 3),
    ...parseDoubanMetadataList(detail.douban?.countries).slice(0, 2),
  ];
  const playableEpisodes = detail.seasons.flatMap((season) => season.episodes).filter((episode) => episode.videoFile);
  const mainEpisode =
    detail.itemKind === "tv"
      ? playableEpisodes.find((episode) => hasUnfinishedProgress(episode.videoFile)) ??
        playableEpisodes.find((episode) => episode.videoFile && !episode.videoFile.isWatched) ??
        playableEpisodes[0] ??
        null
      : null;
  const movieFiles = detail.itemKind === "movie" ? moviePlaybackFiles(detail) : [];
  const mainMovieFile = detail.itemKind === "movie" ? resolveMovieMainFile(movieFiles) : null;
  const mainFile = mainEpisode?.videoFile ?? mainMovieFile ?? null;
  const mainTimeline = detail.itemKind === "movie"
    ? multipartSavedTimeline(movieFiles)
    : savedFileTimeline(mainFile);
  const mainProgressPercent = mainTimeline.durationSeconds > 0
    ? Math.round((Math.min(mainTimeline.positionSeconds, mainTimeline.durationSeconds) / mainTimeline.durationSeconds) * 100)
    : fileProgressPercent(mainFile);
  const mainStatusLabel = detail.itemKind === "movie" ? watchedStatusLabelForFiles(movieFiles) : watchedStatusLabel(mainFile);
  const mainElapsedLabel = formatPlaybackTime(mainTimeline.positionSeconds);
  const mainDurationLabel = mainTimeline.durationSeconds > 0 ? formatPlaybackTime(mainTimeline.durationSeconds) : "--:--";
  const playButtonText = [
    mainTimeline.positionSeconds > 5 ? "继续播放" : "开始播放",
    mainEpisode ? episodeDisplayLabel(mainEpisode.seasonNumber, mainEpisode.episodeNumber) : null,
  ]
    .filter(Boolean)
    .join(" ");
  const resolvedSelectedSeasonId = detail.seasons.some((season) => season.id === selectedSeasonId)
    ? selectedSeasonId
    : detail.seasons[0]?.id ?? "";
  const selectedSeason = detail.seasons.find((season) => season.id === resolvedSelectedSeasonId) ?? null;
  const selectedHlsCount = selectedHlsItemIds.length + selectedHlsVideoFileIds.length;
  const detailPosterSelected = selectedHlsItemIds.includes(detail.id);
  const [subtitleCacheStatus, setSubtitleCacheStatus] = useState<SubtitleCacheStatus | null>(null);
  const [isSubtitleCacheLoading, setIsSubtitleCacheLoading] = useState(false);
  const subtitleCacheBadge = subtitleCacheStatusBadge(subtitleCacheStatus, isSubtitleCacheLoading, mainFile);

  useEffect(() => {
    if (resolvedSelectedSeasonId !== selectedSeasonId) {
      onSeasonChange(resolvedSelectedSeasonId);
    }
  }, [detail.id, onSeasonChange, resolvedSelectedSeasonId, selectedSeasonId]);

  useEffect(() => {
    const fileId = mainFile?.id ?? "";
    if (!fileId) {
      setSubtitleCacheStatus(null);
      setIsSubtitleCacheLoading(false);
      return;
    }

    let cancelled = false;
    setSubtitleCacheStatus(null);
    setIsSubtitleCacheLoading(hasPrewarmableSubtitle(mainFile));
    getSubtitleCacheStatus(fileId)
      .then((status) => {
        if (!cancelled) {
          setSubtitleCacheStatus(status);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setSubtitleCacheStatus(null);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsSubtitleCacheLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [mainFile?.id]);

  return (
    <main className="detailShell">
      {poster ? <img alt="" className="detailBackdrop" src={poster} /> : null}
      <header className="detailTopbar">
        <button aria-label="back" onClick={onBack}>
          <ArrowLeft size={19} />
        </button>
        <div className="detailTopbarActions">
          <button
            aria-label="hls cache selection"
            className={hlsSelectionMode ? "topbarTextButton active" : "topbarTextButton"}
            disabled={isHlsTaskRunning}
            onClick={onToggleHlsSelectionMode}
            title="HLS 缓存"
            type="button"
          >
            <Download size={16} />
            <span>HLS</span>
          </button>
          {hlsSelectionMode ? (
            <>
              <button
                aria-label="start hls cache"
                className="topbarTextButton"
                disabled={selectedHlsCount === 0 || isHlsTaskRunning}
                onClick={onStartHlsCache}
                title="开始缓存"
                type="button"
              >
                <CheckCircle2 size={16} />
                <span>开始</span>
              </button>
              <button aria-label="cancel hls cache selection" onClick={onToggleHlsSelectionMode} title="取消选择" type="button">
                <X size={18} />
              </button>
            </>
          ) : null}
        </div>
      </header>

      <section className="detailHero">
        {subtitleCacheBadge ? (
          <div className={`detailSubtitleCache ${subtitleCacheBadge.tone}`}>
            <Captions size={16} />
            <span>{subtitleCacheBadge.label}</span>
          </div>
        ) : null}
        <button
          aria-label={detailPosterSelected ? `取消 HLS 缓存 ${detail.title}` : `选择 HLS 缓存 ${detail.title}`}
          className={[
            "detailPoster",
            hlsSelectionMode ? "selectableForHls" : "",
            detailPosterSelected ? "selectedForHls" : "",
          ].filter(Boolean).join(" ")}
          onClick={() => {
            if (hlsSelectionMode) {
              onToggleHlsItem(detail.id);
            }
          }}
          tabIndex={hlsSelectionMode ? 0 : -1}
          title={hlsSelectionMode ? "选择当前影视缓存" : undefined}
          type="button"
        >
          {poster ? <img alt="" src={poster} /> : null}
          {hlsSelectionMode ? (
            <span className={detailPosterSelected ? "hlsSelectBadge selected" : "hlsSelectBadge"}>
              {detailPosterSelected ? <CheckCircle2 size={16} /> : <Download size={15} />}
            </span>
          ) : null}
        </button>
        <div className="detailMeta">
          <h1>{detail.title}</h1>
          <div className="detailFacts">
            {year ? <span>{year}</span> : null}
            {detail.voteAverage ? (
              <span className="detailRating tmdbRating">
                <Star size={18} fill="currentColor" />
                {detail.voteAverage.toFixed(1)}
              </span>
            ) : null}
            {detail.doubanRating ? (
              <span className="detailRating doubanRating">
                <Star size={18} fill="currentColor" />
                {detail.doubanRating.toFixed(1)}
              </span>
            ) : null}
            {fusedTextMetadataParts.map((part) => (
              <span key={part}>{part}</span>
            ))}
          </div>
          <p>{overview}</p>
          {errorText ? <strong className="detailError">{errorText}</strong> : null}
          {mainFile ? (
            <div className="detailActions">
              <button aria-label="play" onClick={() => onPlay(mainFile, { refreshMain: true })}>
                <Play size={18} />
                <span>{playButtonText}</span>
              </button>
              <span className={mainStatusLabel === "已播" ? "detailWatchStatus watched" : "detailWatchStatus"}>
                {mainStatusLabel}
              </span>
              <div className="detailTimelineAxis">
                <span>{mainElapsedLabel}</span>
                <div
                  aria-label="播放进度"
                  aria-valuemax={100}
                  aria-valuemin={0}
                  aria-valuenow={mainProgressPercent}
                  className="detailTimeline"
                  role="progressbar"
                >
                  <div style={{ width: `${mainProgressPercent}%` }} />
                </div>
                <span>{mainDurationLabel}</span>
              </div>
            </div>
          ) : null}
        </div>
      </section>

      {detail.itemKind === "tv" && selectedSeason ? (
        <section className="episodeSection">
          <div className="seasonHeader">
            <select
              aria-label="season"
              onChange={(event) => onSeasonChange(event.target.value)}
              value={selectedSeason.id}
            >
              {detail.seasons.map((season) => (
                <option key={season.id} value={season.id}>
                  {seasonDisplayLabel(season, detail.title)}
                </option>
              ))}
            </select>
          </div>
          <div className="episodeGrid">
            {selectedSeason.episodes.map((episode) => (
              <EpisodeCard
                episode={episode}
                key={episode.id}
                onEditMetadata={onEditMetadata}
                onPlay={onPlay}
                poster={poster}
                hlsSelected={!!episode.videoFile && selectedHlsVideoFileIds.includes(episode.videoFile.id)}
                hlsSelectionMode={hlsSelectionMode}
                onToggleHlsFile={onToggleHlsFile}
                showDetails={showEpisodeDetails}
              />
            ))}
          </div>
        </section>
      ) : null}

      {detail.itemKind === "movie" && movieFiles.length > 0 ? (
        <section className="fileSection">
          <h2>版本 / 文件（{movieFiles.length}）</h2>
          {movieFiles.map((file, index) => (
            <FileRow
              file={file}
              key={file.id}
              label={movieFiles.length > 1 ? `版本 ${index + 1}` : "正片"}
              onPlay={onPlay}
            />
          ))}
        </section>
      ) : null}
    </main>
  );
}

function EpisodeCard({
  episode,
  hlsSelected,
  hlsSelectionMode,
  onEditMetadata,
  onPlay,
  onToggleHlsFile,
  poster,
  showDetails,
}: {
  episode: EpisodeDetail;
  hlsSelected: boolean;
  hlsSelectionMode: boolean;
  onEditMetadata: (episode: EpisodeDetail) => void;
  onPlay: (file: VideoFileSummary, options?: { refreshMain?: boolean }) => void;
  onToggleHlsFile: (fileId: string) => void;
  poster: string | null;
  showDetails: boolean;
}) {
  const files = episodeVideoFiles(episode);
  const file = episode.videoFile ?? files[0] ?? null;
  const episodeLabel = episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber);
  const title = episode.title?.trim() || episodeLabel;

  function handlePlay() {
    if (!hlsSelectionMode && file) {
      onPlay(file);
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (hlsSelectionMode || !file || (event.key !== "Enter" && event.key !== " ")) {
      return;
    }

    event.preventDefault();
    onPlay(file);
  }

  return (
    <article
      className={["episodeCard", showDetails ? "" : "simpleEpisode", file ? "clickableEpisode" : "", hlsSelected ? "selectedForHls" : ""]
        .filter(Boolean)
        .join(" ")}
      onClick={handlePlay}
      onKeyDown={handleKeyDown}
      role={file && !hlsSelectionMode ? "button" : undefined}
      tabIndex={file && !hlsSelectionMode ? 0 : undefined}
    >
      <div className="episodeStillFrame">
        {episode.stillAssetId ? (
          <img alt="" className="episodeStill" src={thumbnailUrl(episode.stillAssetId)} />
        ) : (
          <div className="episodeStill episodeStillPlaceholder">
            {poster ? <img alt="" src={poster} /> : null}
          </div>
        )}
        <button
          aria-label="自定义编辑"
          className="episodeStillEdit"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onEditMetadata(episode);
          }}
          title="自定义编辑"
          type="button"
        >
          <Pencil size={15} />
        </button>
        {hlsSelectionMode && file ? (
          <button
            aria-label={hlsSelected ? `取消 HLS 缓存 ${title}` : `选择 HLS 缓存 ${title}`}
            className={hlsSelected ? "episodeHlsSelect selected" : "episodeHlsSelect"}
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onToggleHlsFile(file.id);
            }}
            title={hlsSelected ? "取消缓存选择" : "选择缓存"}
            type="button"
          >
            {hlsSelected ? <CheckCircle2 size={15} /> : <Download size={14} />}
          </button>
        ) : null}
      </div>
      <div className="episodeBody">
        <h3>{title}</h3>
        <p>{episodeLabel}</p>
      </div>
    </article>
  );
}

function episodeDisplayLabel(seasonNumber: number, episodeNumber: number): string {
  return seasonNumber === 0 ? `特别篇第 ${episodeNumber} 集` : `第 ${seasonNumber} 季第 ${episodeNumber} 集`;
}

function episodeVideoFiles(episode: EpisodeDetail): VideoFileSummary[] {
  const candidates = episode.videoFiles?.length ? episode.videoFiles : episode.videoFile ? [episode.videoFile] : [];
  const seen = new Set<string>();
  return candidates.filter((file) => {
    if (seen.has(file.id)) {
      return false;
    }
    seen.add(file.id);
    return true;
  });
}

function seasonDisplayLabel(season: { seasonNumber: number; title: string | null }, showTitle: string): string {
  const fallback = season.seasonNumber === 0 ? "特别篇" : `第 ${season.seasonNumber} 季`;
  const title = season.title?.trim();
  if (!title) {
    return fallback;
  }

  const compactTitle = normalizeCompactTitle(title);
  const compactShowTitle = normalizeCompactTitle(showTitle);
  if (compactShowTitle && compactTitle.includes(compactShowTitle) && compactTitle !== normalizeCompactTitle(fallback)) {
    return fallback;
  }

  return title;
}

function normalizeCompactTitle(value: string): string {
  return value.replace(/[\s._\-:：'’!！]+/g, "").toLowerCase();
}

function fileProgressPercent(file: VideoFileSummary | null): number {
  if (!file || file.durationSeconds <= 0 || file.positionSeconds <= 0) {
    return 0;
  }

  return Math.round((Math.min(file.positionSeconds, file.durationSeconds) / file.durationSeconds) * 100);
}

const unknownMoviePartIndex = Number.MAX_SAFE_INTEGER;
const moviePartPrefixes = ["volume", "part", "disc", "disk", "dvd", "vol", "pt", "cd"];
const chineseMoviePartIndexes: Record<string, number> = {
  上: 1,
  上半: 1,
  上篇: 1,
  前篇: 1,
  下: 2,
  下半: 2,
  下篇: 2,
  后篇: 2,
  後篇: 2,
};

function parseRomanNumber(value: string): number | null {
  const roman = value.trim().toLowerCase();
  if (!/^[ivxlcdm]+$/.test(roman)) {
    return null;
  }

  const values: Record<string, number> = { i: 1, v: 5, x: 10, l: 50, c: 100, d: 500, m: 1000 };
  let total = 0;
  for (let index = 0; index < roman.length; index += 1) {
    const current = values[roman[index]] ?? 0;
    const next = values[roman[index + 1]] ?? 0;
    total += current < next ? -current : current;
  }
  return total > 0 ? total : null;
}

function parseMoviePartToken(value: string | undefined): number | null {
  const token = (value ?? "").trim().toLowerCase();
  if (!token) {
    return null;
  }

  if (/^\d{1,3}$/.test(token)) {
    const numeric = Number(token);
    return numeric > 0 ? numeric : null;
  }

  if (chineseMoviePartIndexes[value ?? ""]) {
    return chineseMoviePartIndexes[value ?? ""];
  }

  return parseRomanNumber(token);
}

function moviePartSortIndex(file: VideoFileSummary): number {
  const raw = `${file.relativePath || ""}/${file.fileName || ""}`;
  const tokens = raw.split(/[\\/\s._\-\[\]\(\)\{\}【】（）,:：]+/u).filter(Boolean);
  for (let index = 0; index < tokens.length; index += 1) {
    const rawToken = tokens[index];
    const token = rawToken.toLowerCase();
    if (chineseMoviePartIndexes[rawToken]) {
      return chineseMoviePartIndexes[rawToken];
    }

    for (const prefix of moviePartPrefixes) {
      if (token === prefix) {
        const parsed = parseMoviePartToken(tokens[index + 1]);
        if (parsed !== null) {
          return parsed;
        }
      } else if (token.startsWith(prefix)) {
        const parsed = parseMoviePartToken(token.slice(prefix.length));
        if (parsed !== null) {
          return parsed;
        }
      }
    }
  }

  return unknownMoviePartIndex;
}

function compareVideoFilesForPlayback(lhs: VideoFileSummary, rhs: VideoFileSummary): number {
  const lhsPartIndex = moviePartSortIndex(lhs);
  const rhsPartIndex = moviePartSortIndex(rhs);
  if (lhsPartIndex !== rhsPartIndex) {
    return lhsPartIndex - rhsPartIndex;
  }

  return lhs.relativePath.localeCompare(rhs.relativePath, undefined, { numeric: true, sensitivity: "base" }) ||
    lhs.fileName.localeCompare(rhs.fileName, undefined, { numeric: true, sensitivity: "base" }) ||
    lhs.id.localeCompare(rhs.id);
}

function moviePlaybackFiles(detail: LibraryItemDetail): VideoFileSummary[] {
  if (detail.itemKind !== "movie") {
    return [];
  }

  return [...detail.videoFiles].sort(compareVideoFilesForPlayback);
}

function resolveMovieMainFile(files: VideoFileSummary[]): VideoFileSummary | null {
  return files.find((file) => hasUnfinishedProgress(file)) ??
    files.find((file) => !isEffectivelyWatched(file)) ??
    files[0] ??
    null;
}

function resolveMainPlaybackFile(detail: LibraryItemDetail): VideoFileSummary | null {
  if (detail.itemKind === "movie") {
    return resolveMovieMainFile(moviePlaybackFiles(detail));
  }

  const episodeFiles = detail.seasons
    .flatMap((season) => season.episodes)
    .flatMap((episode) => episodeVideoFiles(episode));
  return episodeFiles.find((file) => hasUnfinishedProgress(file)) ??
    episodeFiles.find((file) => !isEffectivelyWatched(file)) ??
    episodeFiles[0] ??
    null;
}

function findVideoFileById(detail: LibraryItemDetail, fileId: string): VideoFileSummary | null {
  const movieFile = detail.videoFiles.find((file) => file.id === fileId);
  if (movieFile) {
    return movieFile;
  }

  for (const season of detail.seasons) {
    for (const episode of season.episodes) {
      const file = episodeVideoFiles(episode).find((candidate) => candidate.id === fileId);
      if (file) {
        return file;
      }
    }
  }

  return null;
}

function isEffectivelyWatched(file: VideoFileSummary | null | undefined): boolean {
  return !!file && (file.isWatched || fileProgressPercent(file) >= 95);
}

function savedFileTimeline(file: VideoFileSummary | null): { positionSeconds: number; durationSeconds: number } {
  if (!file) {
    return { positionSeconds: 0, durationSeconds: 0 };
  }

  const durationSeconds = Math.max(0, file.durationSeconds);
  const positionSeconds = durationSeconds > 0
    ? Math.min(Math.max(0, file.positionSeconds), durationSeconds)
    : Math.max(0, file.positionSeconds);
  return { positionSeconds, durationSeconds };
}

function multipartSavedTimeline(files: VideoFileSummary[]): { positionSeconds: number; durationSeconds: number } {
  return files.reduce(
    (summary, file) => {
      const durationSeconds = Math.max(0, file.durationSeconds);
      if (durationSeconds <= 0) {
        return summary;
      }

      summary.durationSeconds += durationSeconds;
      summary.positionSeconds += isEffectivelyWatched(file)
        ? durationSeconds
        : Math.min(Math.max(0, file.positionSeconds), durationSeconds);
      return summary;
    },
    { positionSeconds: 0, durationSeconds: 0 },
  );
}

function multipartLiveTimeline(
  files: VideoFileSummary[],
  currentIndex: number,
  currentTime: number,
  currentDuration: number,
  bufferedUntil: number,
): { positionSeconds: number; bufferedSeconds: number; durationSeconds: number } {
  let offsetSeconds = 0;
  let durationSeconds = 0;
  for (let index = 0; index < files.length; index += 1) {
    const fileDuration = Math.max(
      0,
      index === currentIndex
        ? Math.max(files[index].durationSeconds, currentDuration, currentTime, bufferedUntil)
        : files[index].durationSeconds,
    );
    if (index < currentIndex) {
      offsetSeconds += fileDuration;
    }
    durationSeconds += fileDuration;
  }

  return {
    positionSeconds: offsetSeconds + Math.max(0, currentTime),
    bufferedSeconds: offsetSeconds + Math.max(0, bufferedUntil),
    durationSeconds,
  };
}

type MultipartTimelineSegment = {
  durationText: string;
  isCurrent: boolean;
  key: string;
  label: string;
  startPercent: number;
  widthPercent: number;
};

function multipartTimelineSegments(
  files: VideoFileSummary[],
  currentIndex: number,
  currentTime: number,
  currentDuration: number,
  bufferedUntil: number,
): MultipartTimelineSegment[] {
  const durations = files.map((file, index) => Math.max(
    0,
    index === currentIndex
      ? Math.max(file.durationSeconds, currentDuration, currentTime, bufferedUntil)
      : file.durationSeconds,
  ));
  const totalDuration = durations.reduce((total, durationSeconds) => total + durationSeconds, 0);
  if (files.length <= 1 || totalDuration <= 0) {
    return [];
  }

  let cursor = 0;
  return files.map((file, index) => {
    const durationSeconds = durations[index] ?? 0;
    const segment = {
      durationText: formatPlaybackTime(durationSeconds),
      isCurrent: index === currentIndex,
      key: file.id,
      label: `第 ${index + 1} 段`,
      startPercent: (cursor / totalDuration) * 100,
      widthPercent: (durationSeconds / totalDuration) * 100,
    };
    cursor += durationSeconds;
    return segment;
  });
}

function resolveMultipartSeekTarget(
  files: VideoFileSummary[],
  currentIndex: number,
  currentDuration: number,
  currentTime: number,
  targetSeconds: number,
): { index: number; seconds: number } {
  let cursor = 0;
  for (let index = 0; index < files.length; index += 1) {
    const durationSeconds = Math.max(
      0,
      index === currentIndex
        ? Math.max(files[index].durationSeconds, currentDuration, currentTime)
        : files[index].durationSeconds,
    );
    if (durationSeconds <= 0) {
      continue;
    }

    if (targetSeconds <= cursor + durationSeconds || index === files.length - 1) {
      return { index, seconds: Math.max(0, Math.min(durationSeconds, targetSeconds - cursor)) };
    }

    cursor += durationSeconds;
  }

  return { index: Math.max(0, files.length - 1), seconds: 0 };
}

function watchedStatusLabelForFiles(files: VideoFileSummary[]): string {
  if (files.length === 0) {
    return "未播";
  }

  if (files.every((file) => isEffectivelyWatched(file))) {
    return "已播";
  }

  return files.some((file) => file.positionSeconds > 5 || isEffectivelyWatched(file)) ? "未播完" : "未播";
}

function hasUnfinishedProgress(file: VideoFileSummary | null | undefined): boolean {
  if (!file || file.positionSeconds <= 5) {
    return false;
  }

  return file.durationSeconds <= 0 || fileProgressPercent(file) < 95;
}

function watchedStatusLabel(file: VideoFileSummary | null): string {
  if (!file) {
    return "未播";
  }

  if (file.isWatched || fileProgressPercent(file) >= 95) {
    return "已播";
  }

  return file.positionSeconds > 5 ? "未播完" : "未播";
}

function hasPrewarmableSubtitle(file: VideoFileSummary | null | undefined): boolean {
  return !!file && file.subtitleStreams.length > 0;
}

function subtitleCacheStatusBadge(
  status: SubtitleCacheStatus | null,
  isLoading: boolean,
  file: VideoFileSummary | null,
): { label: string; tone: "checking" | "ready" | "pending" } | null {
  if (!status) {
    return isLoading && hasPrewarmableSubtitle(file)
      ? { label: "字幕缓存 检测中", tone: "checking" }
      : null;
  }

  if (status.subtitleTotal <= 0) {
    return null;
  }

  if (status.subtitleCached >= status.subtitleTotal) {
    return { label: `字幕已缓存 ${status.subtitleCached}/${status.subtitleTotal}`, tone: "ready" };
  }

  return { label: `字幕未全缓存 ${status.subtitleCached}/${status.subtitleTotal}`, tone: "pending" };
}

function FileRow({
  file,
  label,
  onPlay,
}: {
  file: VideoFileSummary;
  label?: string;
  onPlay: (file: VideoFileSummary) => void;
}) {
  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "Enter" && event.key !== " ") {
      return;
    }

    event.preventDefault();
    onPlay(file);
  };

  return (
    <div className="fileRow" onClick={() => onPlay(file)} onKeyDown={handleKeyDown} role="button" tabIndex={0}>
      <div>
        <strong>{label ?? "正片"}</strong>
        <span>{formatMediaProbe(file)}</span>
      </div>
    </div>
  );
}

function formatMediaProbe(file: VideoFileSummary): string {
  return [
    file.container,
    file.videoCodec ? `V:${file.videoCodec}` : null,
    file.audioCodec ? `A:${file.audioCodec}` : null,
    file.audioTracks.length > 0 ? `${file.audioTracks.length} 音轨` : null,
    file.subtitleStreams.length > 0 ? `${file.subtitleStreams.length} 内嵌字幕` : null,
    file.subtitleSummary ? `S:${file.subtitleSummary}` : null,
  ]
    .filter(Boolean)
    .join(" · ") || "未探测";
}

function formatAudioTrack(track: VideoFileSummary["audioTracks"][number]): string {
  return `${formatLanguageLabel(track.language, track.title)} - ${formatAudioTrackCodec(track)}`;
}

function formatSubtitleStream(stream: VideoFileSummary["subtitleStreams"][number]): string {
  return `${formatLanguageLabel(stream.language, stream.title)} - ${formatSubtitleCodec(stream.codec)}`;
}

function formatExternalSubtitleTrack(subtitle: PlaybackSubtitleTrack): string {
  return `${formatLanguageLabel(subtitle.language, subtitle.fileName)} - ${formatSubtitleCodec(subtitle.format)}`;
}

function formatLanguageLabel(language?: string | null, fallback?: string | null): string {
  const text = normalizeSearchText([language, fallback]);
  const tokens = tokenizeSearchText(text);
  if (tokens.some((token) => [
    "zh",
    "zho",
    "chi",
    "chs",
    "cht",
    "cmn",
    "yue",
    "cn",
    "chn",
    "zhcn",
    "zhtw",
    "zhhk",
    "sc",
    "tc",
    "gb",
    "big5",
    "mandarin",
    "cantonese",
    "chinese",
  ].includes(token))
      || /中文|国语|國語|普通话|普通話|粤语|粵語|简体|簡體|繁体|繁體|中字|双语|雙語/.test(text)) {
    return "中文";
  }

  if (tokens.some((token) => ["en", "eng", "english", "us", "usa", "uk"].includes(token)) || /英语|英文|英語/.test(text)) {
    return "英语";
  }

  if (tokens.some((token) => ["ja", "jp", "jpn", "japanese"].includes(token)) || /日本|日语|日語|日文/.test(text)) {
    return "日语";
  }

  if (tokens.some((token) => ["ko", "kor", "korean"].includes(token)) || /韩语|韓語|韩文|韓文|韩国|韓国|韓國/.test(text)) {
    return "韩语";
  }

  if (tokens.some((token) => ["fr", "fre", "fra", "french"].includes(token))) {
    return "法语";
  }

  if (tokens.some((token) => ["de", "ger", "deu", "german"].includes(token))) {
    return "德语";
  }

  if (tokens.some((token) => ["es", "spa", "spanish"].includes(token))) {
    return "西语";
  }

  return language?.trim() || "未知";
}

function formatAudioTrackCodec(track: VideoFileSummary["audioTracks"][number]): string {
  const channelText = formatAudioChannels(track.channels, track.channelLayout);
  const codecText = formatAudioCodec(track.codec, track.title);
  return [codecText, channelText].filter(Boolean).join(" ") || "未知音轨";
}

function formatAudioChannels(channels?: number | null, channelLayout?: string | null): string | null {
  if (channels && channels > 0) {
    if (channels === 8) {
      return "7.1";
    }
    if (channels === 7) {
      return "6.1";
    }
    if (channels === 6) {
      return "5.1";
    }
    if (channels === 2) {
      return "2.0";
    }
    if (channels === 1) {
      return "1.0";
    }
    return `${channels}ch`;
  }

  const layout = channelLayout?.trim().toLowerCase();
  if (!layout) {
    return null;
  }

  if (layout.includes("7.1")) {
    return "7.1";
  }
  if (layout.includes("6.1")) {
    return "6.1";
  }
  if (layout.includes("5.1")) {
    return "5.1";
  }
  if (layout.includes("stereo") || layout.includes("2.0")) {
    return "2.0";
  }
  if (layout.includes("mono") || layout.includes("1.0")) {
    return "1.0";
  }

  return channelLayout ?? null;
}

function formatAudioCodec(codec?: string | null, title?: string | null): string | null {
  const normalized = (codec ?? "").trim().toLowerCase();
  const titleText = (title ?? "").trim().toLowerCase();
  const hasAtmos = titleText.includes("atmos") || normalized.includes("atmos");
  const base = (() => {
    switch (normalized) {
      case "truehd":
      case "mlp":
        return "TrueHD";
      case "eac3":
      case "e-ac-3":
      case "eac-3":
        return "DD+";
      case "ac3":
      case "ac-3":
        return "DD";
      case "dts":
        return titleText.includes("dts-hd") || titleText.includes("ma") ? "DTS-HD MA" : "DTS";
      case "dts_hd_ma":
      case "dts-hd-ma":
        return "DTS-HD MA";
      case "flac":
        return "FLAC";
      case "aac":
        return "AAC";
      case "opus":
        return "Opus";
      case "mp3":
        return "MP3";
      default:
        return codec?.trim() || null;
    }
  })();

  return hasAtmos && base && !base.includes("Atmos") ? `${base} Atmos` : base;
}

function formatSubtitleCodec(codec?: string | null): string {
  switch ((codec ?? "").trim().toLowerCase().replace("-", "_")) {
    case "hdmv_pgs_subtitle":
    case "pgs":
      return "PGS";
    case "ass":
      return "ASS";
    case "ssa":
      return "SSA";
    case "subrip":
    case "srt":
      return "SRT";
    case "webvtt":
    case "vtt":
      return "WebVTT";
    case "mov_text":
      return "MOV Text";
    case "dvd_subtitle":
    case "dvdsub":
      return "DVD";
    default:
      return codec?.trim() || "字幕";
  }
}

function embeddedSubtitleId(stream: VideoFileSummary["subtitleStreams"][number], ordinal: number): string {
  return `embedded_${stream.index}_si_${ordinal}`;
}

function isEmbeddedSubtitleIdForStream(
  subtitleId: string,
  stream: VideoFileSummary["subtitleStreams"][number],
  ordinal: number,
): boolean {
  return subtitleId === embeddedSubtitleId(stream, ordinal) || subtitleId === `embedded_${stream.index}`;
}

function findSelectedEmbeddedSubtitle(
  file: VideoFileSummary,
  subtitleId: string,
): VideoFileSummary["subtitleStreams"][number] | null {
  if (!subtitleId.startsWith("embedded_")) {
    return null;
  }

  return file.subtitleStreams.find((stream, ordinal) => isEmbeddedSubtitleIdForStream(subtitleId, stream, ordinal)) ?? null;
}

function findSelectedEmbeddedSubtitleOrdinal(file: VideoFileSummary, subtitleId: string): number | null {
  if (!subtitleId.startsWith("embedded_")) {
    return null;
  }

  const index = file.subtitleStreams.findIndex((stream, ordinal) => isEmbeddedSubtitleIdForStream(subtitleId, stream, ordinal));
  return index >= 0 ? index : null;
}

function canUseEmbeddedSubtitleAsWebTrack(stream: VideoFileSummary["subtitleStreams"][number]): boolean {
  const codec = (stream.codec ?? "").trim().toLowerCase();
  return [
    "ass",
    "ssa",
    "subrip",
    "srt",
    "webvtt",
    "mov_text",
    "text",
  ].includes(codec);
}

function embeddedSubtitleWebVttUrl(fileId: string, ordinal: number): string {
  return `/api/playback/files/${encodeURIComponent(fileId)}/embedded-subtitles/${ordinal}.vtt`;
}

function resolvePreferredAudioTrack(
  detail: LibraryItemDetail,
  file: VideoFileSummary,
  preference: string,
): string {
  if (file.audioTracks.length === 0) {
    return "auto";
  }

  const matchedTrack = resolveAudioLanguagePriority(detail, file, preference)
    .map((language) =>
      file.audioTracks.find((track) =>
        matchesLanguagePreference(
          [track.language, track.title, track.codec, track.channelLayout, track.channels ? `${track.channels}ch` : null],
          language,
        ),
      ) ?? null,
    )
    .find(Boolean) ?? null;

  return String((matchedTrack ?? file.audioTracks[0]).index);
}

function resolveAudioLanguagePriority(
  detail: LibraryItemDetail,
  file: VideoFileSummary,
  preference: string,
): PlaybackLanguage[] {
  const requestedLanguage = preference === "smart"
    ? resolveSmartAudioLanguage(detail, file)
    : normalizePlaybackLanguage(preference);
  return requestedLanguage ? [requestedLanguage] : [];
}

function normalizePlaybackLanguage(value?: string | null): PlaybackLanguage | null {
  return value === "en" || value === "ja" || value === "zh" ? value : null;
}

function uniquePlaybackLanguages(values: Array<PlaybackLanguage | null>): PlaybackLanguage[] {
  const seen = new Set<PlaybackLanguage>();
  return values.filter((language): language is PlaybackLanguage => {
    if (!language || seen.has(language)) {
      return false;
    }

    seen.add(language);
    return true;
  });
}

function resolveSmartAudioLanguage(detail: LibraryItemDetail, file: VideoFileSummary): PlaybackLanguage | null {
  const text = normalizeSearchText([
    detail.title,
    file.fileName,
    file.relativePath,
    file.episodeTitle,
  ]);
  const tokens = tokenizeSearchText(text);
  if (tokens.some((token) => ["japan", "japanese", "jpn", "jp", "anime"].includes(token)) || /[\u3040-\u30ff]/.test(text)) {
    return "ja";
  }

  if (tokens.some((token) => ["china", "chinese", "mandarin", "cantonese", "chn", "cn", "hk", "tw"].includes(token))) {
    return "zh";
  }

  if (tokens.some((token) => ["usa", "us", "uk", "gb", "english", "eng", "america", "britain"].includes(token))) {
    return "en";
  }

  return null;
}

function resolvePreferredSubtitleSelection(
  file: VideoFileSummary,
  subtitles: PlaybackSubtitleTrack[],
  preference: string,
): { id: string; mode: string } | null {
  const selections = listSubtitleSelections(file, subtitles);
  for (const language of resolveSubtitleLanguagePriority(preference)) {
    const matchedSelection = selections.find((selection) => matchesLanguagePreference(selection.languageValues, language));
    if (matchedSelection) {
      return { id: matchedSelection.id, mode: matchedSelection.mode };
    }
  }

  const fallbackSelection = selections.at(-1);
  return fallbackSelection ? { id: fallbackSelection.id, mode: fallbackSelection.mode } : null;
}

function listSubtitleSelections(
  file: VideoFileSummary,
  subtitles: PlaybackSubtitleTrack[],
): Array<{ id: string; mode: string; languageValues: Array<string | null | undefined> }> {
  return [
    ...file.subtitleStreams.map((stream, ordinal) => ({
      id: embeddedSubtitleId(stream, ordinal),
      mode: isBitmapSubtitleCodec(stream.codec) ? "burn-bitmap" : "burn",
      languageValues: [stream.language, stream.title, stream.codec],
    })),
    ...subtitles.map((subtitle) => ({
      id: subtitle.id,
      mode: "burn",
      languageValues: [subtitle.language],
    })),
  ];
}

function resolveSubtitleLanguagePriority(preference: string): Array<"en" | "zh"> {
  return preference === "en" ? ["en", "zh"] : ["zh", "en"];
}

function resolveSubtitlePlaybackMode(
  subtitleId: string,
  file: VideoFileSummary,
  subtitles: PlaybackSubtitleTrack[],
): string {
  if (!subtitleId) {
    return "off";
  }

  if (file.subtitleStreams.some((stream, ordinal) => isEmbeddedSubtitleIdForStream(subtitleId, stream, ordinal))) {
    const stream = file.subtitleStreams.find((candidate, ordinal) => isEmbeddedSubtitleIdForStream(subtitleId, candidate, ordinal));
    return stream && isBitmapSubtitleCodec(stream.codec) ? "burn-bitmap" : "burn";
  }

  const externalSubtitle = subtitles.find((subtitle) => subtitle.id === subtitleId);
  return externalSubtitle ? "burn" : "off";
}

function resolveBurnedSubtitlePlaybackMode(subtitleId: string, file: VideoFileSummary): string {
  const embeddedSubtitle = findSelectedEmbeddedSubtitle(file, subtitleId);
  if (!embeddedSubtitle) {
    return "burn";
  }

  return isBitmapSubtitleCodec(embeddedSubtitle.codec) ? "burn-bitmap" : "burn";
}

function isBitmapSubtitleCodec(codec?: string | null): boolean {
  const normalized = (codec ?? "").trim().toLowerCase().replace("-", "_");
  return ["hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "dvdsub"].includes(normalized);
}

function matchesLanguagePreference(values: Array<string | null | undefined>, preference: string): boolean {
  const language = preference === "ja" || preference === "zh" || preference === "en" ? preference : "zh";
  const text = normalizeSearchText(values);
  const tokens = tokenizeSearchText(text);
  const exactAliases: Record<"en" | "ja" | "zh", string[]> = {
    en: ["en", "eng", "english", "us", "usa", "uk"],
    ja: ["ja", "jp", "jpn", "japanese"],
    zh: [
      "zh",
      "zho",
      "chi",
      "chs",
      "cht",
      "cmn",
      "yue",
      "cn",
      "chn",
      "zhcn",
      "zhtw",
      "zhhk",
      "sc",
      "tc",
      "gb",
      "big5",
      "mandarin",
      "cantonese",
      "chinese",
    ],
  };
  const phraseAliases: Record<"en" | "ja" | "zh", string[]> = {
    en: ["english", "英语", "英文", "英語"],
    ja: ["japanese", "日本語", "日语", "日語", "日文"],
    zh: [
      "chinese",
      "mandarin",
      "cantonese",
      "中文",
      "国语",
      "國語",
      "普通话",
      "普通話",
      "粤语",
      "粵語",
      "简体",
      "簡體",
      "繁体",
      "繁體",
      "中字",
      "双语",
      "雙語",
    ],
  };

  return exactAliases[language].some((alias) => tokens.includes(alias)) ||
    phraseAliases[language].some((alias) => text.includes(alias));
}

function normalizeSearchText(values: Array<string | null | undefined>): string {
  return values.filter(Boolean).join(" ").normalize("NFKC").toLowerCase();
}

function tokenizeSearchText(value: string): string[] {
  return value.split(/[^a-z0-9]+/).filter(Boolean);
}

function formatPlaybackCacheStatus(status: PlaybackCacheStatus): string {
  if (!status.isRemote) {
    return "正在准备播放";
  }

  const percent = status.percent === null ? null : `${Math.floor(status.percent)}%`;
  const bytes =
    status.totalBytes && status.totalBytes > 0
      ? `${formatBytes(status.downloadedBytes)} / ${formatBytes(status.totalBytes)}`
      : formatBytes(status.downloadedBytes);
  return percent ? `正在缓存 WebDAV ${percent} · ${bytes}` : `正在缓存 WebDAV · ${bytes}`;
}

function PlayerView({
  detail,
  file: initialFile,
  onBack,
  playbackSettings,
}: {
  detail: LibraryItemDetail;
  file: VideoFileSummary;
  onBack: () => void;
  playbackSettings: AppSettingsSnapshot["playback"];
}) {
  const playlistFiles = useMemo(() => {
    if (detail.itemKind !== "movie") {
      return [initialFile];
    }

    const files = moviePlaybackFiles(detail);
    return files.length > 0 ? files : [initialFile];
  }, [detail, initialFile]);
  const [currentFileId, setCurrentFileId] = useState(initialFile.id);
  const requestedFileIndex = playlistFiles.findIndex((candidate) => candidate.id === currentFileId);
  const initialFileIndex = playlistFiles.findIndex((candidate) => candidate.id === initialFile.id);
  const currentFileIndex = requestedFileIndex >= 0 ? requestedFileIndex : Math.max(0, initialFileIndex);
  const playlistFile = playlistFiles[currentFileIndex] ?? initialFile;
  const [playbackStreamsByFileId, setPlaybackStreamsByFileId] = useState<Record<string, PlaybackFileStreams>>({});
  const playbackStreams = playbackStreamsByFileId[playlistFile.id] ?? null;
  const file = useMemo(
    () => playbackStreams
      ? {
          ...playlistFile,
          audioTracks: playbackStreams.audioTracks,
          subtitleStreams: playbackStreams.subtitleStreams,
        }
      : playlistFile,
    [playbackStreams, playlistFile],
  );
  const isMultipartMovie = detail.itemKind === "movie" && playlistFiles.length > 1;
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const lastSavedAtRef = useRef(0);
  const sessionIdRef = useRef<string | null>(null);
  const hlsSessionIdsRef = useRef<Set<string>>(new Set());
  const controlsHideTimerRef = useRef(0);
  const playbackPositionRef = useRef(file.positionSeconds);
  const pendingSeekSecondsRef = useRef<number | null>(file.positionSeconds > 1 ? file.positionSeconds : null);
  const pendingUserSeekRef = useRef<number | null>(null);
  const seekHoldUntilRef = useRef(0);
  const subtitleCueCacheRef = useRef<Map<string, WebSubtitleCue[]>>(new Map());
  const suppressProgressSaveRef = useRef(false);
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [playbackMode, setPlaybackMode] = useState<"direct" | "hls" | null>(null);
  const [playerStatus, setPlayerStatus] = useState("正在准备播放");
  const [playerError, setPlayerError] = useState("");
  const [audioTrack, setAudioTrack] = useState(() =>
    resolvePreferredAudioTrack(detail, file, playbackSettings.defaultAudioLanguage),
  );
  const [subtitleMode, setSubtitleMode] = useState("off");
  const [selectedSubtitleId, setSelectedSubtitleId] = useState("");
  const [useHardware] = useState(true);
  const [subtitles, setSubtitles] = useState<PlaybackSubtitleTrack[]>([]);
  const [cacheStatus, setCacheStatus] = useState<PlaybackCacheStatus | null>(null);
  const [showPlayerControls, setShowPlayerControls] = useState(true);
  const [openPlayerMenu, setOpenPlayerMenu] = useState<"audio" | "subtitle" | null>(null);
  const [isPaused, setIsPaused] = useState(false);
  const [subtitleCues, setSubtitleCues] = useState<WebSubtitleCue[]>([]);
  const [currentTime, setCurrentTime] = useState(file.positionSeconds);
  const [duration, setDuration] = useState(file.durationSeconds);
  const [bufferedUntil, setBufferedUntil] = useState(0);
  const title = formatPlayerTitle(detail, file);
  const subTitle = playerSubTitle(file);
  const selectedAudioTrack = audioTrack === "auto"
    ? null
    : file.audioTracks.find((track) => String(track.index) === audioTrack) ?? null;
  const implicitAudioTrack = file.audioTracks[0] ?? null;
  const selectedAudioTrackOrdinal = selectedAudioTrack
    ? file.audioTracks.findIndex((track) => track.index === selectedAudioTrack.index)
    : -1;
  const requestedAudioTrackIndex =
    selectedAudioTrack &&
    implicitAudioTrack &&
    selectedAudioTrack.index !== implicitAudioTrack.index &&
    selectedAudioTrackOrdinal >= 0
      ? selectedAudioTrackOrdinal
      : null;
  const selectedAudioTrackLabel = selectedAudioTrack ? formatAudioTrack(selectedAudioTrack) : "默认音轨";
  const selectedSubtitle = subtitles.find((subtitle) => subtitle.id === selectedSubtitleId) ?? null;
  const selectedEmbeddedSubtitle = findSelectedEmbeddedSubtitle(file, selectedSubtitleId);
  const selectedEmbeddedSubtitleOrdinal = findSelectedEmbeddedSubtitleOrdinal(file, selectedSubtitleId);
  const canFallbackToBurnedSubtitle =
    subtitleMode === "web" &&
    !!selectedSubtitleId &&
    (!!selectedSubtitle || !!selectedEmbeddedSubtitle);
  const selectedEmbeddedSubtitleWebVttUrl =
    selectedEmbeddedSubtitle &&
    selectedEmbeddedSubtitleOrdinal !== null &&
    subtitleMode === "web" &&
    canUseEmbeddedSubtitleAsWebTrack(selectedEmbeddedSubtitle)
      ? embeddedSubtitleWebVttUrl(file.id, selectedEmbeddedSubtitleOrdinal)
      : null;
  const selectedWebSubtitleUrl = subtitleMode === "web"
    ? selectedSubtitle?.webVttUrl ?? selectedEmbeddedSubtitleWebVttUrl
    : null;
  const selectedSubtitleLabel = selectedEmbeddedSubtitle
    ? formatSubtitleStream(selectedEmbeddedSubtitle)
    : selectedSubtitle
      ? formatExternalSubtitleTrack(selectedSubtitle)
      : "关闭字幕";
  const requiresFullCacheForPlayback =
    requestedAudioTrackIndex !== null ||
    (!!selectedSubtitleId && subtitleMode.startsWith("burn")) ||
    (!!selectedEmbeddedSubtitle && subtitleMode === "web");
  const isPlaybackCachePlayable =
    cacheStatus?.isReady || (!!cacheStatus?.canStreamDirect && !requiresFullCacheForPlayback) || false;
  const seekSeconds = 10;
  const timeline = isMultipartMovie
    ? multipartLiveTimeline(playlistFiles, currentFileIndex, currentTime, duration, bufferedUntil)
    : {
        positionSeconds: currentTime,
        bufferedSeconds: bufferedUntil,
        durationSeconds: duration,
      };
  const progressValue = timeline.durationSeconds > 0
    ? Math.min(100, Math.max(0, (timeline.positionSeconds / timeline.durationSeconds) * 100))
    : 0;
  const bufferedValue = timeline.durationSeconds > 0
    ? Math.min(100, Math.max(progressValue, (timeline.bufferedSeconds / timeline.durationSeconds) * 100))
    : 0;
  const timelineSegments = isMultipartMovie
    ? multipartTimelineSegments(playlistFiles, currentFileIndex, currentTime, duration, bufferedUntil)
    : [];
  const playbackDecisionSubtitleMode = selectedWebSubtitleUrl ? "off" : selectedSubtitleId ? subtitleMode : "off";
  const playbackDecisionSubtitleId = selectedWebSubtitleUrl ? null : selectedSubtitleId || null;
  const activeSubtitleText = selectedWebSubtitleUrl
    ? subtitleCues
        .filter((cue) => currentTime >= cue.start && currentTime <= cue.end)
        .map((cue) => cue.text)
        .filter(Boolean)
        .join("\n")
    : "";

  useEffect(() => {
    setCurrentFileId(initialFile.id);
  }, [detail.id, initialFile.id]);

  const resetControlTimer = useCallback((forceVisible = true) => {
    window.clearTimeout(controlsHideTimerRef.current);
    if (forceVisible) {
      setShowPlayerControls(true);
    }

    const video = videoRef.current;
    if (!video || video.paused || playerStatus || playerError) {
      return;
    }

    controlsHideTimerRef.current = window.setTimeout(() => {
      setShowPlayerControls(false);
    }, 3000);
  }, [playerError, playerStatus]);

  useEffect(() => {
    return () => window.clearTimeout(controlsHideTimerRef.current);
  }, []);

  useEffect(() => {
    return () => {
      void stopAllHlsSessions();
    };
  }, []);

  useEffect(() => {
    setAudioTrack(resolvePreferredAudioTrack(detail, file, playbackSettings.defaultAudioLanguage));
    setSelectedSubtitleId("");
    setSubtitleMode("off");
    setSubtitles([]);
    setSubtitleCues([]);
    setOpenPlayerMenu(null);
    setCurrentTime(file.positionSeconds);
    setDuration(file.durationSeconds);
    setBufferedUntil(0);
    setIsPaused(false);
    setPlaybackMode(null);
    setPlaybackUrl(null);
    playbackPositionRef.current = file.positionSeconds;
    pendingSeekSecondsRef.current = file.positionSeconds > 1 ? file.positionSeconds : null;
    pendingUserSeekRef.current = null;
    seekHoldUntilRef.current = 0;
    suppressProgressSaveRef.current = false;
  }, [
    detail.title,
    file.id,
    file.relativePath,
    playbackSettings.defaultAudioLanguage,
  ]);

  useEffect(() => {
    setAudioTrack(resolvePreferredAudioTrack(detail, file, playbackSettings.defaultAudioLanguage));
    setSelectedSubtitleId("");
    setSubtitleMode("off");
    setSubtitles([]);
    setSubtitleCues([]);
    setOpenPlayerMenu(null);
  }, [
    detail.title,
    file.id,
    file.audioTracks,
    file.subtitleStreams,
    playbackSettings.defaultAudioLanguage,
    playbackSettings.defaultSubtitleLanguage,
  ]);

  useEffect(() => {
    if (!showPlayerControls) {
      setOpenPlayerMenu(null);
    }
  }, [showPlayerControls]);

  const saveProgress = useCallback(
    async (force = false) => {
      if (suppressProgressSaveRef.current) {
        return;
      }

      const video = videoRef.current;
      if (!video) {
        return;
      }

      const positionSeconds = readCurrentPlaybackTime();
      const durationSeconds = resolvePlaybackDuration(video.duration, duration || file.durationSeconds);
      if (!force && Math.abs(positionSeconds - lastSavedAtRef.current) < 10) {
        return;
      }

      lastSavedAtRef.current = positionSeconds;
      try {
        await updatePlaybackProgress(file.id, positionSeconds, durationSeconds);
      } catch {
        // Playback must keep running even if a transient progress save fails.
      }
    },
    [duration, file.id, file.durationSeconds, file.positionSeconds],
  );

  useEffect(() => {
    lastSavedAtRef.current = file.positionSeconds;
  }, [file.id, file.positionSeconds]);

  useEffect(() => {
    let isCancelled = false;
    void getPlaybackStreams(playlistFile.id)
      .then((streams) => {
        if (isCancelled) {
          return;
        }

        setPlaybackStreamsByFileId((current) => {
          const previous = current[playlistFile.id];
          if (previous
            && previous.audioTracks.length === streams.audioTracks.length
            && previous.subtitleStreams.length === streams.subtitleStreams.length
            && previous.audioTracks.every((track, index) => track.index === streams.audioTracks[index]?.index)
            && previous.subtitleStreams.every((track, index) => track.index === streams.subtitleStreams[index]?.index)) {
            return current;
          }

          return {
            ...current,
            [playlistFile.id]: streams,
          };
        });
      })
      .catch(() => undefined);

    return () => {
      isCancelled = true;
    };
  }, [playlistFile.id]);

  useEffect(() => {
    let isCancelled = false;
    let pollHandle = 0;
    const canUseCacheStatus = (status: PlaybackCacheStatus) =>
      status.isReady || (status.canStreamDirect && !requiresFullCacheForPlayback);

    async function pollCache(start: boolean) {
      try {
        const nextStatus = start ? await getPlaybackCacheStatus(file.id) : await getPlaybackCacheStatus(file.id);
        if (isCancelled) {
          return;
        }

        setCacheStatus(nextStatus);
        if (nextStatus.errorMessage) {
          setPlayerError(nextStatus.errorMessage);
          setPlayerStatus("");
          return;
        }

        if (canUseCacheStatus(nextStatus)) {
          setPlayerStatus("正在准备播放");
          return;
        }

        const preparedStatus = start ? await preparePlaybackCache(file.id) : nextStatus;
        if (isCancelled) {
          return;
        }

        setCacheStatus(preparedStatus);
        if (preparedStatus.errorMessage) {
          setPlayerError(preparedStatus.errorMessage);
          setPlayerStatus("");
          return;
        }

        if (canUseCacheStatus(preparedStatus)) {
          setPlayerStatus("正在准备播放");
          return;
        }

        setPlayerStatus(formatPlaybackCacheStatus(preparedStatus));
        pollHandle = window.setTimeout(() => void pollCache(false), 800);
      } catch (error) {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
          setPlayerStatus("");
        }
      }
    }

    rememberPlaybackPosition();
    setCacheStatus(null);
    setPlayerStatus("正在准备缓存");
    setPlayerError("");
    void pollCache(true);

    return () => {
      isCancelled = true;
      window.clearTimeout(pollHandle);
    };
  }, [file.id, requiresFullCacheForPlayback]);

  useEffect(() => {
    if (!isPlaybackCachePlayable) {
      return;
    }

    let isCancelled = false;
    void getPlaybackSubtitles(file.id)
      .then((nextSubtitles) => {
        if (isCancelled) {
          return;
        }

        setSubtitles(nextSubtitles);
        setSelectedSubtitleId((current) => {
          const stillAvailable =
            current &&
            (nextSubtitles.some((subtitle) => subtitle.id === current) ||
              file.subtitleStreams.some((stream, ordinal) => isEmbeddedSubtitleIdForStream(current, stream, ordinal)));
          if (stillAvailable) {
            return current;
          }

          const preferred = resolvePreferredSubtitleSelection(
            file,
            nextSubtitles,
            playbackSettings.defaultSubtitleLanguage,
          );
          setSubtitleMode(preferred?.mode ?? "off");
          return preferred?.id ?? "";
        });
      })
      .catch((error: unknown) => {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      });

    return () => {
      isCancelled = true;
    };
  }, [file, file.id, file.subtitleStreams, isPlaybackCachePlayable, playbackSettings.defaultSubtitleLanguage]);

  useEffect(() => {
    if (!isPlaybackCachePlayable) {
      return;
    }

    let isCancelled = false;
    let retryHandle = 0;

    async function resolvePlayback(attempt: number) {
      try {
        const decision = await getPlaybackDecision(file.id, {
          audioTrackIndex: requestedAudioTrackIndex,
          subtitleMode: playbackDecisionSubtitleMode,
          subtitleId: playbackDecisionSubtitleId,
          hardware: useHardware,
        });
        if (isCancelled) {
          return;
        }

        if (decision.durationSeconds && decision.durationSeconds > 0) {
          setDuration((current) => Math.max(current, decision.durationSeconds ?? 0));
        }

        sessionIdRef.current = decision.sessionId;
        if (decision.sessionId) {
          hlsSessionIdsRef.current.add(decision.sessionId);
        }
        if (decision.mode === "direct" && decision.streamUrl) {
          setPlaybackMode("direct");
          setPlaybackUrl(decision.streamUrl);
          setPlayerStatus("");
          setPlayerError("");
          void stopStaleHlsSessions(null);
          return;
        }

        if (decision.mode.startsWith("hls") && decision.manifestUrl) {
          if (decision.isReady) {
            setPlaybackMode("hls");
            setPlaybackUrl(decision.manifestUrl);
            setPlayerStatus("");
            setPlayerError("");
            void stopStaleHlsSessions(decision.sessionId);
            return;
          }

          setPlayerStatus(decision.reason ?? "正在准备 HLS");
          retryHandle = window.setTimeout(() => void resolvePlayback(attempt + 1), 1200);
          return;
        }

        setPlayerError(decision.reason ?? "当前文件暂时无法播放");
      } catch (error) {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      }
    }

    rememberPlaybackPosition();
    setPlayerStatus("正在准备播放");
    setPlayerError("");
    void resolvePlayback(0);

    return () => {
      isCancelled = true;
      window.clearTimeout(retryHandle);
    };
  }, [
    audioTrack,
    file.id,
    file.subtitleStreams,
    isPlaybackCachePlayable,
    playbackDecisionSubtitleId,
    playbackDecisionSubtitleMode,
    requestedAudioTrackIndex,
    useHardware,
  ]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !playbackUrl || !playbackMode) {
      return;
    }

    let hls: Hls | null = null;
    suppressProgressSaveRef.current = false;
    const resumeSeconds = resolveResumeSeconds();
    if (resumeSeconds > 0) {
      pendingUserSeekRef.current = resumeSeconds;
      seekHoldUntilRef.current = performance.now() + 15000;
      setCurrentTime(resumeSeconds);
    }

    const startPlayback = () => {
      applyResumeSeekIfNeeded();
      setIsPaused(false);
      void video.play().catch((error: unknown) => {
        if (!isAutoplayIgnoredError(error)) {
          setIsPaused(true);
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      });
    };

    if (playbackMode === "hls" && Hls.isSupported()) {
      hls = new Hls({
        enableWorker: true,
        startPosition: resumeSeconds,
        startFragPrefetch: true,
        maxBufferLength: 45,
        maxMaxBufferLength: 90,
        backBufferLength: 30,
      });
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        applyResumeSeekIfNeeded();
        startPlayback();
      });
      hls.loadSource(playbackUrl);
      hls.attachMedia(video);
    } else if (
      playbackMode === "hls" &&
      !video.canPlayType("application/vnd.apple.mpegurl")
    ) {
      setPlayerError("当前浏览器不支持 HLS 播放。");
      return;
    } else {
      video.src = playbackUrl;
      startPlayback();
    }

    return () => {
      rememberPlaybackPosition(false);
      suppressProgressSaveRef.current = true;
      hls?.destroy();
      video.removeAttribute("src");
      video.load();
      window.setTimeout(() => {
        suppressProgressSaveRef.current = false;
      }, 0);
    };
  }, [playbackMode, playbackUrl]);

  useEffect(() => {
    const controller = new AbortController();
    if (!selectedWebSubtitleUrl) {
      setSubtitleCues([]);
      return;
    }

    const cached = subtitleCueCacheRef.current.get(selectedWebSubtitleUrl);
    if (cached) {
      setSubtitleCues(cached);
      if (cached.length === 0 && canFallbackToBurnedSubtitle) {
        fallbackToBurnedSubtitle();
      }
      return () => controller.abort();
    }

    setSubtitleCues([]);
    fetch(selectedWebSubtitleUrl, { credentials: "same-origin", signal: controller.signal })
      .then((response) => {
        if (!response.ok) {
          throw new Error(`subtitle request failed: ${response.status}`);
        }

        return response.text();
      })
      .then((text) => {
        if (!controller.signal.aborted) {
          const cues = parseWebVttCues(text);
          subtitleCueCacheRef.current.set(selectedWebSubtitleUrl, cues);
          setSubtitleCues(cues);
          if (cues.length === 0 && canFallbackToBurnedSubtitle) {
            fallbackToBurnedSubtitle();
          }
        }
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          setSubtitleCues([]);
          if (canFallbackToBurnedSubtitle) {
            fallbackToBurnedSubtitle();
          }
        }
      });

    return () => {
      controller.abort();
    };
  }, [canFallbackToBurnedSubtitle, selectedWebSubtitleUrl]);

  useEffect(() => {
    function handleKeyDown(event: globalThis.KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target?.closest("input, select, textarea, button")) {
        return;
      }

      if (event.code === "Space") {
        event.preventDefault();
        togglePlayPause();
        return;
      }

      if (event.key === "ArrowLeft") {
        event.preventDefault();
        seekRelative(-seekSeconds);
        return;
      }

      if (event.key === "ArrowRight") {
        event.preventDefault();
        seekRelative(seekSeconds);
      }
    }

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  });

  function readCurrentPlaybackTime(): number {
    const video = videoRef.current;
    const pendingSeek = pendingUserSeekRef.current;
    if (pendingSeek !== null && performance.now() < seekHoldUntilRef.current) {
      return Math.max(0, pendingSeek);
    }

    const hasLoadedSource =
      !!video &&
      Boolean(video.currentSrc || video.getAttribute("src")) &&
      video.readyState > 0;
    if (
      video &&
      hasLoadedSource &&
      !suppressProgressSaveRef.current &&
      Number.isFinite(video.currentTime)
    ) {
      return Math.max(0, video.currentTime);
    }

    return Math.max(0, playbackPositionRef.current);
  }

  function applyResumeSeekIfNeeded() {
    const video = videoRef.current;
    const target = pendingSeekSecondsRef.current ?? pendingUserSeekRef.current;
    if (!video || target === null || target <= 1) {
      return;
    }

    const effectiveDuration = resolvePlaybackDuration(video.duration, duration || file.durationSeconds);
    if (effectiveDuration > 0 && target >= effectiveDuration - 8) {
      return;
    }

    if (Number.isFinite(video.duration) || video.readyState > 0) {
      video.currentTime = target;
    }
    playbackPositionRef.current = target;
    pendingUserSeekRef.current = target;
    seekHoldUntilRef.current = performance.now() + 15000;
    setCurrentTime(target);
  }

  function rememberPlaybackPosition(updateState = true) {
    const nextTime = readCurrentPlaybackTime();
    playbackPositionRef.current = nextTime;
    pendingSeekSecondsRef.current = nextTime > 1 ? nextTime : null;
    if (updateState) {
      setCurrentTime(nextTime);
    }
  }

  function resolveResumeSeconds(): number {
    const target = Math.max(0, pendingSeekSecondsRef.current ?? playbackPositionRef.current ?? file.positionSeconds);
    const effectiveDuration = duration || file.durationSeconds;
    if (target <= 1) {
      return 0;
    }

    return effectiveDuration <= 0 || target < effectiveDuration - 8 ? target : 0;
  }

  function updateBufferedProgress() {
    const video = videoRef.current;
    const effectiveDuration = video ? resolvePlaybackDuration(video.duration, duration || file.durationSeconds) : 0;
    if (!video || effectiveDuration <= 0 || video.buffered.length === 0) {
      setBufferedUntil((current) => (current === 0 ? current : 0));
      return;
    }

    const playbackTime = Number.isFinite(video.currentTime) ? video.currentTime : playbackPositionRef.current;
    let nextBufferedUntil = 0;
    for (let index = 0; index < video.buffered.length; index += 1) {
      const start = video.buffered.start(index);
      const end = video.buffered.end(index);
      if (start <= playbackTime + 0.5 && end >= playbackTime) {
        nextBufferedUntil = Math.max(nextBufferedUntil, end);
      } else if (end > playbackTime && nextBufferedUntil <= playbackTime) {
        nextBufferedUntil = Math.max(nextBufferedUntil, end);
      }
    }

    setBufferedUntil(Math.min(effectiveDuration, Math.max(0, nextBufferedUntil)));
  }

  function handleLoadedMetadata() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const nextDuration = resolvePlaybackDuration(video.duration, duration || file.durationSeconds);
    setDuration(nextDuration);
    setIsPaused(false);
    const resumeSeconds = resolveResumeSeconds();
    if (resumeSeconds > 0) {
      video.currentTime = resumeSeconds;
      playbackPositionRef.current = resumeSeconds;
      pendingSeekSecondsRef.current = null;
      pendingUserSeekRef.current = resumeSeconds;
      seekHoldUntilRef.current = performance.now() + 15000;
      setCurrentTime(resumeSeconds);
    } else if (pendingSeekSecondsRef.current !== null) {
      pendingSeekSecondsRef.current = null;
      playbackPositionRef.current = 0;
      setCurrentTime(0);
    }
    updateBufferedProgress();

    void video.play().catch((error: unknown) => {
      if (!isAutoplayIgnoredError(error)) {
        setIsPaused(true);
        setPlayerError(error instanceof Error ? error.message : String(error));
      }
    });
  }

  function handleTimeUpdate() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const nextCurrentTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
    const pendingSeek = pendingUserSeekRef.current;
    if (
      pendingSeek !== null &&
      performance.now() < seekHoldUntilRef.current &&
      Math.abs(nextCurrentTime - pendingSeek) > 1.5
    ) {
      updateBufferedProgress();
      return;
    }

    if (pendingSeek !== null && Math.abs(nextCurrentTime - pendingSeek) <= 1.5) {
      pendingUserSeekRef.current = null;
      seekHoldUntilRef.current = 0;
    }

    playbackPositionRef.current = nextCurrentTime;
    setCurrentTime(nextCurrentTime);
    const nextDuration = resolvePlaybackDuration(video.duration, duration || file.durationSeconds);
    if (nextDuration > 0) {
      setDuration(nextDuration);
    }
    updateBufferedProgress();
    void saveProgress(false);
  }

  function handlePlayStateChange(paused: boolean) {
    setIsPaused(paused);
    resetControlTimer(true);
  }

  function togglePlayPause() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (video.paused) {
      setIsPaused(false);
      void video.play().catch((error: unknown) => {
        if (!isPlaybackAbortError(error)) {
          setIsPaused(true);
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      });
    } else {
      video.pause();
    }
    resetControlTimer(true);
  }

  function applyCurrentFileSeek(nextTime: number) {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    video.currentTime = nextTime;
    playbackPositionRef.current = nextTime;
    pendingSeekSecondsRef.current = nextTime > 1 ? nextTime : null;
    pendingUserSeekRef.current = nextTime;
    seekHoldUntilRef.current = performance.now() + 3000;
    setCurrentTime(nextTime);
    resetControlTimer(true);
  }

  function seekRelative(seconds: number) {
    const video = videoRef.current;
    const effectiveDuration = video ? resolvePlaybackDuration(video.duration, duration || file.durationSeconds) : 0;
    if (!video || effectiveDuration <= 0) {
      return;
    }

    if (isMultipartMovie && timeline.durationSeconds > 0) {
      const target = resolveMultipartSeekTarget(
        playlistFiles,
        currentFileIndex,
        effectiveDuration,
        video.currentTime,
        Math.max(0, Math.min(timeline.durationSeconds, timeline.positionSeconds + seconds)),
      );
      void switchPlaylistFile(target.index, target.seconds);
      return;
    }

    applyCurrentFileSeek(Math.max(0, Math.min(effectiveDuration, video.currentTime + seconds)));
  }

  async function handleSeekPercent(value: string) {
    const video = videoRef.current;
    const effectiveDuration = video ? resolvePlaybackDuration(video.duration, duration || file.durationSeconds) : 0;
    if (!video || effectiveDuration <= 0) {
      return;
    }

    const numericValue = Number(value);
    if (isMultipartMovie && timeline.durationSeconds > 0) {
      const target = resolveMultipartSeekTarget(
        playlistFiles,
        currentFileIndex,
        effectiveDuration,
        video.currentTime,
        (numericValue / 100) * timeline.durationSeconds,
      );
      await switchPlaylistFile(target.index, target.seconds);
      return;
    }

    applyCurrentFileSeek((numericValue / 100) * effectiveDuration);
  }

  function handleSeeked() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const nextTime = Number.isFinite(video.currentTime) ? video.currentTime : playbackPositionRef.current;
    pendingUserSeekRef.current = null;
    seekHoldUntilRef.current = 0;
    playbackPositionRef.current = nextTime;
    pendingSeekSecondsRef.current = nextTime > 1 ? nextTime : null;
    setCurrentTime(nextTime);
    updateBufferedProgress();
  }

  async function switchPlaylistFile(targetIndex: number, startSeconds: number, persistCurrent = true) {
    if (!playlistFiles[targetIndex]) {
      return;
    }

    if (targetIndex === currentFileIndex) {
      applyCurrentFileSeek(startSeconds);
      return;
    }

    if (persistCurrent) {
      await saveProgress(true);
    }
    const targetFile = playlistFiles[targetIndex];
    playbackPositionRef.current = startSeconds;
    pendingSeekSecondsRef.current = startSeconds > 1 ? startSeconds : null;
    pendingUserSeekRef.current = startSeconds;
    seekHoldUntilRef.current = performance.now() + 3000;
    setCurrentTime(startSeconds);
    setDuration(targetFile.durationSeconds);
    setBufferedUntil(0);
    setCurrentFileId(targetFile.id);
    resetControlTimer(true);
  }

  async function handleEnded() {
    await saveProgress(true);
    if (!isMultipartMovie || currentFileIndex + 1 >= playlistFiles.length) {
      return;
    }

    const video = videoRef.current;
    const finishedDuration = resolvePlaybackDuration(video?.duration ?? 0, duration || file.durationSeconds);
    if (finishedDuration > 0) {
      await updatePlaybackProgress(file.id, finishedDuration, finishedDuration).catch(() => undefined);
    }
    await switchPlaylistFile(currentFileIndex + 1, 0, false);
  }

  async function handleBack() {
    await saveProgress(true);
    await stopAllHlsSessions();
    if (cacheStatus?.canCancel) {
      await cancelPlaybackCache(file.id).catch(() => undefined);
    }
    onBack();
  }

  async function stopAllHlsSessions() {
    const sessionIds = Array.from(hlsSessionIdsRef.current);
    hlsSessionIdsRef.current.clear();
    sessionIdRef.current = null;
    await Promise.all(sessionIds.map((sessionId) => stopHlsSession(sessionId).catch(() => undefined)));
  }

  async function stopStaleHlsSessions(activeSessionId: string | null) {
    const staleSessionIds = Array.from(hlsSessionIdsRef.current)
      .filter((sessionId) => sessionId !== activeSessionId);
    for (const sessionId of staleSessionIds) {
      hlsSessionIdsRef.current.delete(sessionId);
    }
    await Promise.all(staleSessionIds.map((sessionId) => stopHlsSession(sessionId).catch(() => undefined)));
  }

  async function handleCancelCache() {
    try {
      const nextStatus = await cancelPlaybackCache(file.id);
      setCacheStatus(nextStatus);
      setPlayerStatus("");
      setPlayerError(nextStatus.errorMessage ?? "缓存已取消。");
    } catch (error) {
      setPlayerError(error instanceof Error ? error.message : String(error));
    }
  }

  function handleSelectSubtitle(subtitleId: string) {
    rememberPlaybackPosition(false);
    setSelectedSubtitleId(subtitleId);
    setSubtitleMode(resolveSubtitlePlaybackMode(subtitleId, file, subtitles));
    setOpenPlayerMenu(null);
  }

  function fallbackToBurnedSubtitle() {
    rememberPlaybackPosition(false);
    setPlayerStatus("字幕转换失败，正在切换为烧录字幕");
    setSubtitleMode(resolveBurnedSubtitlePlaybackMode(selectedSubtitleId, file));
  }

  return (
    <main className="playerShell" onMouseMove={() => resetControlTimer(true)}>
      <section className="playerStage" onClick={() => setShowPlayerControls((current) => !current)}>
        <video
          autoPlay
          onClick={(event) => event.stopPropagation()}
          onEnded={() => void handleEnded()}
          onLoadedMetadata={handleLoadedMetadata}
          onPause={() => {
            handlePlayStateChange(true);
            void saveProgress(true);
          }}
          onPlay={() => handlePlayStateChange(false)}
          onProgress={updateBufferedProgress}
          onSeeked={handleSeeked}
          onTimeUpdate={handleTimeUpdate}
          ref={videoRef}
        />

        {activeSubtitleText ? (
          <div className="playerSubtitleOverlay">
            {activeSubtitleText.split("\n").map((line, index) => (
              <span key={`${index}-${line}`}>{line}</span>
            ))}
          </div>
        ) : null}

        <div className={showPlayerControls ? "playerChrome visible" : "playerChrome"}>
          <header className="playerTopbar">
            <button aria-label="back" onClick={() => void handleBack()} type="button">
              <ArrowLeft size={22} />
            </button>
            <div>
              <h1>{title}</h1>
              {subTitle ? <p>{subTitle}</p> : null}
            </div>
          </header>

          <div className="playerBottomBar" onClick={(event) => event.stopPropagation()}>
            <div className="playerTransport">
              <button aria-label="backward" onClick={() => seekRelative(-seekSeconds)} type="button">
                <SkipBack size={34} />
              </button>
              <button className="playerPlayButton" aria-label={isPaused ? "play" : "pause"} onClick={togglePlayPause} type="button">
                {isPaused ? <CirclePlay size={62} /> : <CirclePause size={62} />}
              </button>
              <button aria-label="forward" onClick={() => seekRelative(seekSeconds)} type="button">
                <SkipForward size={34} />
              </button>
            </div>

            <div className="playerTimeline">
              <span>{formatPlaybackTime(timeline.positionSeconds)}</span>
              <div className="playerTimelineTrack">
                <input
                  aria-label="播放进度"
                  max={100}
                  min={0}
                  onChange={(event) => void handleSeekPercent(event.target.value)}
                  onInput={(event) => void handleSeekPercent(event.currentTarget.value)}
                  step={0.1}
                  style={{
                    "--player-progress": `${progressValue}%`,
                    "--player-buffered": `${bufferedValue}%`,
                  } as CSSProperties}
                  type="range"
                  value={progressValue}
                />
                {timelineSegments.length > 1 && (
                  <div aria-hidden="true" className="playerTimelineSegments">
                    {timelineSegments.map((segment) => (
                      <span
                        className={`playerTimelineSegment${segment.isCurrent ? " current" : ""}`}
                        key={segment.key}
                        style={{
                          left: `${segment.startPercent}%`,
                          width: `${segment.widthPercent}%`,
                        }}
                        title={`${segment.label} ${segment.durationText}`}
                      />
                    ))}
                  </div>
                )}
              </div>
              <span>{timeline.durationSeconds > 0 ? (isMultipartMovie ? formatPlaybackTime(timeline.durationSeconds) : `-${formatPlaybackTime(Math.max(0, timeline.durationSeconds - timeline.positionSeconds))}`) : "--:--"}</span>
            </div>

            <div className="playerMenuBar">
              <div className="playerMenuItem">
                <button
                  aria-expanded={openPlayerMenu === "audio"}
                  aria-label="音轨"
                  className={openPlayerMenu === "audio" ? "playerMenuTrigger active" : "playerMenuTrigger"}
                  onClick={() => setOpenPlayerMenu((current) => (current === "audio" ? null : "audio"))}
                  title={`音轨：${selectedAudioTrackLabel}`}
                  type="button"
                >
                  <Music2 size={17} />
                </button>
                {openPlayerMenu === "audio" ? (
                  <div className="playerMenuPopover audioMenu">
                    {file.audioTracks.length > 0 ? (
                      file.audioTracks.map((track) => (
                        <button
                          className={String(track.index) === audioTrack ? "active" : ""}
                          key={track.index}
                          onClick={() => {
                            setAudioTrack(String(track.index));
                            setOpenPlayerMenu(null);
                          }}
                          type="button"
                        >
                          <span className="playerMenuCheck">{String(track.index) === audioTrack ? "✓" : ""}</span>
                          <span>{formatAudioTrack(track)}</span>
                        </button>
                      ))
                    ) : (
                      <span className="playerMenuEmpty">无音轨信息</span>
                    )}
                  </div>
                ) : null}
              </div>

              <div className="playerMenuItem">
                <button
                  aria-expanded={openPlayerMenu === "subtitle"}
                  aria-label="字幕"
                  className={openPlayerMenu === "subtitle" ? "playerMenuTrigger active" : "playerMenuTrigger"}
                  onClick={() => setOpenPlayerMenu((current) => (current === "subtitle" ? null : "subtitle"))}
                  title={`字幕：${selectedSubtitleLabel}`}
                  type="button"
                >
                  <Captions size={17} />
                </button>
                {openPlayerMenu === "subtitle" ? (
                  <div className="playerMenuPopover subtitleMenu">
                    <button
                      className={!selectedSubtitleId ? "active" : ""}
                      onClick={() => handleSelectSubtitle("")}
                      type="button"
                    >
                      <span className="playerMenuCheck">{!selectedSubtitleId ? "✓" : ""}</span>
                      <span>关闭字幕</span>
                    </button>
                    {file.subtitleStreams.map((stream, ordinal) => {
                      const subtitleId = embeddedSubtitleId(stream, ordinal);
                      const isSelected = isEmbeddedSubtitleIdForStream(selectedSubtitleId, stream, ordinal);
                      return (
                        <button
                          className={isSelected ? "active" : ""}
                          key={subtitleId}
                          onClick={() => handleSelectSubtitle(subtitleId)}
                          type="button"
                        >
                          <span className="playerMenuCheck">{isSelected ? "✓" : ""}</span>
                          <span>{formatSubtitleStream(stream)}</span>
                        </button>
                      );
                    })}
                    {subtitles.map((subtitle) => (
                      <button
                        className={selectedSubtitleId === subtitle.id ? "active" : ""}
                        key={subtitle.id}
                        onClick={() => handleSelectSubtitle(subtitle.id)}
                        type="button"
                      >
                        <span className="playerMenuCheck">{selectedSubtitleId === subtitle.id ? "✓" : ""}</span>
                        <span>{formatExternalSubtitleTrack(subtitle)}</span>
                      </button>
                    ))}
                  </div>
                ) : null}
              </div>
            </div>
          </div>
        </div>

        {playerStatus ? (
          <div className="playerOverlay">
            <span>{playerStatus}</span>
            {cacheStatus?.canCancel ? (
              <button onClick={() => void handleCancelCache()} type="button">
                取消
              </button>
            ) : null}
          </div>
        ) : null}
        {playerError ? <div className="playerOverlay error">{playerError}</div> : null}
      </section>
    </main>
  );
}

function formatPlayerTitle(detail: LibraryItemDetail, file: VideoFileSummary): string {
  if (file.seasonNumber === null || file.episodeNumber === null) {
    return detail.title;
  }

  return `${detail.title} ${episodeDisplayLabel(file.seasonNumber, file.episodeNumber)}`;
}

function playerSubTitle(file: VideoFileSummary): string {
  return [file.container, file.videoCodec, file.audioCodec].filter(Boolean).join(" · ");
}

function resolvePlaybackDuration(videoDuration: number, fileDuration: number): number {
  const normalizedVideoDuration = Number.isFinite(videoDuration) && videoDuration > 0 ? videoDuration : 0;
  const normalizedFileDuration = Number.isFinite(fileDuration) && fileDuration > 0 ? fileDuration : 0;
  return Math.max(normalizedVideoDuration, normalizedFileDuration);
}

function parseWebVttCues(text: string): WebSubtitleCue[] {
  const normalized = text.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const blocks = normalized.split(/\n{2,}/);
  const cues: WebSubtitleCue[] = [];
  for (const block of blocks) {
    const lines = block
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean);
    if (lines.length === 0) {
      continue;
    }

    const firstLine = lines[0].replace(/^\uFEFF/, "");
    if (
      firstLine === "WEBVTT" ||
      firstLine.startsWith("NOTE") ||
      firstLine.startsWith("STYLE") ||
      firstLine.startsWith("REGION")
    ) {
      continue;
    }

    const timingIndex = lines.findIndex((line) => line.includes("-->"));
    if (timingIndex < 0) {
      continue;
    }

    const [startToken, endTokenWithSettings] = lines[timingIndex].split("-->").map((part) => part.trim());
    const endToken = endTokenWithSettings?.split(/\s+/)[0] ?? "";
    const start = parseWebVttTimestamp(startToken);
    const end = parseWebVttTimestamp(endToken);
    if (start === null || end === null || end <= start) {
      continue;
    }

    const cueText = lines
      .slice(timingIndex + 1)
      .map(cleanWebVttCueLine)
      .filter(Boolean)
      .join("\n");
    if (cueText) {
      cues.push({ start, end, text: cueText });
    }
  }

  return cues;
}

function parseWebVttTimestamp(value: string): number | null {
  const normalized = value.replace(",", ".");
  const parts = normalized.split(":");
  if (parts.length < 2 || parts.length > 3) {
    return null;
  }

  const seconds = Number(parts.pop());
  const minutes = Number(parts.pop());
  const hours = parts.length > 0 ? Number(parts.pop()) : 0;
  if (![hours, minutes, seconds].every(Number.isFinite)) {
    return null;
  }

  return hours * 3600 + minutes * 60 + seconds;
}

function cleanWebVttCueLine(value: string): string {
  return value
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<[^>]+>/g, "")
    .replace(/\{\\[^}]+\}/g, "")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .trim();
}

function formatPlaybackTime(seconds: number): string {
  const safeSeconds = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  const remainingSeconds = safeSeconds % 60;
  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainingSeconds).padStart(2, "0")}`;
  }

  return `${minutes}:${String(remainingSeconds).padStart(2, "0")}`;
}

function isPlaybackAbortError(error: unknown): boolean {
  if (error instanceof DOMException && error.name === "AbortError") {
    return true;
  }

  const message = error instanceof Error ? error.message : String(error);
  return /operation was aborted|play\(\) request was interrupted/i.test(message);
}

function isAutoplayIgnoredError(error: unknown): boolean {
  if (isPlaybackAbortError(error)) {
    return true;
  }

  if (error instanceof DOMException && error.name === "NotAllowedError") {
    return true;
  }

  const message = error instanceof Error ? error.message : String(error);
  return /not allowed|user didn't interact|user gesture/i.test(message);
}
