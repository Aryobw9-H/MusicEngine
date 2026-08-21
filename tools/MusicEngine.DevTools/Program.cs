namespace MusicEngine.DevTools;

using Downloads;
using Models;
using Providers;
using Search;
using Text;

/// <summary>
/// Developer tools for the MusicEngine engine: interactive debug subcommands
/// (bisect, debugsc, gatecheck, fifty, debugapp, dl, debugpersian) and the live
/// network smoke suite. Moved out of the test project when it became xUnit
/// (MODERN-02) — these are tools, not tests.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var live = args.Contains("live");
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("bisect"))
        {
            await BisectAsync();
            return;
        }
        if (args.Contains("debugsc"))
        {
            var cfgS = Configuration.AppConfig.Load();
            Console.WriteLine($"proxy: {cfgS.ProxyUrl ?? "(none)"}");
            using var httpS = new Http.SharedHttpClient(cfgS.ProxyUrl);
            var client = httpS.Create("SoundCloudDebug", proxied: true);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://m.soundcloud.com/");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 16_5_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/99.0.4844.47 Mobile/15E148 Safari/604.1");
                using var resp = client.Send(req);
                var html = resp.Content.ReadAsStringAsync().Result;
                Console.WriteLine($"m.soundcloud: {(int)resp.StatusCode}, {html.Length} chars, clientId present: {html.Contains("\"clientId\":\"")}");
                var m = System.Text.RegularExpressions.Regex.Match(html, "\"clientId\":\"(\\w+)\"");
                if (m.Success)
                {
                    var id = m.Groups[1].Value;
                    Console.WriteLine($"id: {id[..Math.Min(12, id.Length)]}...");
                    using var search = client.GetAsync($"https://api-v2.soundcloud.com/search/tracks?q=amir%20tataloo&limit=3&client_id={id}").Result;
                    Console.WriteLine($"api search: {(int)search.StatusCode}");
                    var body = search.Content.ReadAsStringAsync().Result;
                    Console.WriteLine($"body head: {body[..Math.Min(160, body.Length)]}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex}");
            }
            return;
        }
        if (args.Contains("gatecheck"))
        {
            // gatecheck <goalArtist> <goalTitle> <rowArtist> <rowTitle>
            // Decomposes the strict/loose gate decisions for one pair.
            var g = new GoalSong(args[1], args[2], null, ProviderId.Unknown);
            var r = new SearchResult
            {
                Provider = ProviderId.PersianIndex, Id = "x",
                Metadata = new TrackMetadata { Title = args[4], Artist = args[3] },
                Downloadable = true,
            };
            Console.WriteLine($"strict: {SearchService.PassesGoalGate(r, g)}");
            Console.WriteLine($"loose:  {SearchService.PassesLooseGate(r, g)}");
            foreach (var (hay, need, hayName) in new[]
                     {
                         (args[3], args[1], "artist/artist"), (args[4], args[2], "title/title"),
                         (args[3] + " " + args[4], args[1], "combined/artist"), (args[3] + " " + args[4], args[2], "combined/title"),
                         (args[1], args[3], "artist-rev"), (args[2], args[4], "title-rev"),
                     })
            {
                Console.WriteLine($"  {hayName,-15} overlap={TrackTextNormalizer.KeysOverlap(hay, need)}" +
                    $" tokens={TrackTextNormalizer.ContainsAllTokens(hay, need)}" +
                    $" tokensRev={TrackTextNormalizer.ContainsAllTokens(need, hay)}" +
                    $" phrase={TrackTextNormalizer.ContainsPhraseSpaceless(hay, need)}" +
                    $" phraseRev={TrackTextNormalizer.ContainsPhraseSpaceless(need, hay)}");
            }
            return;
        }
        if (args.Contains("fifty"))
        {
            // Batch correctness run: 50 mixed queries through the app-exact
            // pipeline (reachability + registry + routing). Reports per-query
            // works count, top hit and latency; flags zero-result queries.
            var queries = new[]
            {
                // Iranian artists, Finglish spelling. NOTE: every query here was
                // verified to be a real, findable song — earlier versions
                // contained misremembered titles ("tehran jasbi", "ala deyi")
                // that returned unrelated junk; an honest zero is correct for
                // non-existent songs, but the batch must measure the app.
                "tataloo behesht", "fadaei azkaraj", "mehrzad mano natarsoon", "sijal bargard",
                "pishro dokhtar karaji", "hichkas ye mosht sarbaz",
                "quf miravam", "reza sadeghi delshore",
                "ebi shab", "googoosh do panjere", "shadmehr deejad",
                "moein ghabre mani", "hayedeh shabeh eshgh", "siavash ghomeishi dokhtar irooni",
                "tataloo man bahat ghahram", "arash temptation",
                "saman jalili ghalbe man", "macan band adat",
                "mehdi ahmadvand chjoori mitouni", "masoud roh nikan ashobe ghalbam",
                "farzad farzini lanati", "mohsen yeganeh to hata", "fadaei mahal",
                // Persian script
                "تتلو بهشت", "فدایی از کرج تا لنگه رود", "مهرزاد منو نترسون", "شادمهر عشق",
                "رضا صادقی دلشوره", "ماکان بند کولاک", "مهدی احمدوند چجوری میتونی", "حمید صفت نرو",
                // International
                "coldplay yellow", "eminem lose yourself", "linkin park numb", "weeknd blinding lights",
                "queen bohemian rhapsody", "adele hello", "ed sheeran perfect", "billie eilish bad guy",
                "imagine dragons believer", "michael jackson billie jean", "sia chandelier",
                "avicii wake me up", "alan walker faded",
                // Artist-only
                "fadaei", "tataloo", "coldplay", "eminem",
            };
            var cfgF = Configuration.AppConfig.Load();
            // Query override: `-- fifty D:\path\queries.txt` (UTF-8, one query
            // per line) re-runs a subset instead of the fixed 50.
            var overridePath = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
            string[] queriesToRun = queries;
            if (overridePath is { Length: > 0 } && File.Exists(overridePath))
                queriesToRun = File.ReadAllLines(overridePath)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            var reachF = new Network.Reachability(cfgF.ProxyUrl);
            using var httpF = new Http.SharedHttpClient(cfgF.ProxyUrl, reachF);
            var pipF = new PersianIndexProvider(cfgF);
            await pipF.EnsureAvailableAsync();
            var regF = new Providers.ProviderRegistry(cfgF, reachF,
                new ITunesProvider(httpF),
                new DeezerProvider(httpF),
                new YouTubeProvider(httpF),
                new SoundCloudProvider(httpF),
                new RadioJavanProvider(httpF),
                new Nex1MusicProvider(httpF),
                new PersianSitesProvider(httpF),
                pipF,
                new YtDlpProvider(cfgF),
                new RozMusicProvider(httpF),
                new MusicDelProvider(httpF),
                new BehMelodyProvider(httpF),
                new Melody98Provider(httpF),
                new AparatProvider(httpF),
                new BiaMusicProvider(httpF),
                new BeatMasteringProvider(httpF),
                new MusicsFaProvider(httpF));
            await regF.RefreshRoutesAsync();
            Console.WriteLine($"offline: [{string.Join(", ", regF.OfflineSources)}]");
            var svcF = new SearchService(regF.EnabledSearchProviders(),
                searchTimeoutSeconds: cfgF.SearchTimeoutSeconds);
            var pass = 0; var fail = 0;
            foreach (var q in queriesToRun)
            {
                var swF = System.Diagnostics.Stopwatch.StartNew();
                List<TrackWork> worksF = new();
                try { worksF = await svcF.RunAsync(q, null).WaitAsync(TimeSpan.FromSeconds(45)); }
                catch (TimeoutException) { Console.WriteLine($"⏱  TIMEOUT  {q}"); }
                var top = worksF.FirstOrDefault()?.Representative.Metadata.DisplayTitle ?? "—";
                var ok = worksF.Count > 0;
                if (ok) pass++; else fail++;
                Console.WriteLine($"{(ok ? "✔" : "✘")} [{swF.ElapsedMilliseconds / 1000.0,4:0.0}s] {worksF.Count,2} works | {q} → {top}");
            }
            Console.WriteLine($"\n== {pass}/{queriesToRun.Length} passed, {fail} failed ==");
            reachF.Dispose();
            return;
        }
        if (args.Contains("debugapp"))
        {
            // Mirrors App.xaml.cs construction: reachability-routed clients +
            // registry auto-disable + phase timings. Arg = query.
            var cfgA = Configuration.AppConfig.Load();
            var reach = new Network.Reachability(cfgA.ProxyUrl);
            using var httpA = new Http.SharedHttpClient(cfgA.ProxyUrl, reach);
            var pipA = new PersianIndexProvider(cfgA);
            await pipA.EnsureAvailableAsync();
            var provA = new Providers.ProviderRegistry(cfgA, reach,
                new ITunesProvider(httpA),
                new DeezerProvider(httpA),
                new YouTubeProvider(httpA),
                new SoundCloudProvider(httpA),
                new RadioJavanProvider(httpA),
                new Nex1MusicProvider(httpA),
                new PersianSitesProvider(httpA),
                pipA,
                new YtDlpProvider(cfgA),
                new RozMusicProvider(httpA),
                new MusicDelProvider(httpA),
                new BehMelodyProvider(httpA),
                new Melody98Provider(httpA),
                new AparatProvider(httpA),
                new BiaMusicProvider(httpA),
                new BeatMasteringProvider(httpA),
                new MusicsFaProvider(httpA));
            await provA.RefreshRoutesAsync();
            Console.WriteLine($"offline: [{string.Join(", ", provA.OfflineSources)}]");
            foreach (var h in new[] { "www.youtube.com", "api-v2.soundcloud.com", "api.deezer.com", "rj-deskcloud.com", "nex1music.com", "music-fa.com" })
                Console.WriteLine($"  route {h}: {reach.Peek(h)}");

            // Raw routed GET through the exact client the providers use.
            var rawClient = httpA.Create("probe-test", proxied: true);
            var swRaw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var rr = await rawClient.GetAsync("https://api.deezer.com/search?q=coldplay&limit=1");
                var body = await rr.Content.ReadAsStringAsync();
                Console.WriteLine($"raw routed deezer GET: {(int)rr.StatusCode}, {body.Length} chars in {swRaw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"raw routed deezer GET FAILED after {swRaw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            }
            SearchService.DebugPhases = true;
            var svcA = new SearchService(provA.EnabledSearchProviders(),
                searchTimeoutSeconds: cfgA.SearchTimeoutSeconds);
            var tA = System.Diagnostics.Stopwatch.StartNew();
            var statuses = new List<string>();
            var worksA = await svcA.RunAsync(args.Skip(1).FirstOrDefault() ?? "fadaei azkaraj",
                new SearchService.Callbacks
                {
                    Status = s => { statuses.Add($"+{tA.ElapsedMilliseconds / 1000.0:0.0}s {s}"); },
                    Batch = b => { if (b.Count > 0) statuses.Add($"+{tA.ElapsedMilliseconds / 1000.0:0.0}s BATCH {b.Count} works"); },
                });
            foreach (var s in statuses) Console.WriteLine("  " + s);
            Console.WriteLine($"FINAL: {worksA.Count} works in {tA.ElapsedMilliseconds / 1000.0:0.0}s");
            foreach (var w in worksA.Take(4))
                Console.WriteLine($"  work: {w.Representative.Metadata.DisplayTitle} [{w.Representative.Provider}]");
            reach.Dispose();
            return;
        }
        if (args.Contains("dl"))
        {
            // --dl <query>: run the REAL DownloadManager against the top search
            // result and print the phases/messages — shows which sources the
            // resolver actually consults (incl. the Iranian slow tier).
            var cfgD = Configuration.AppConfig.Load();
            var reachD = new Network.Reachability(cfgD.ProxyUrl);
            using var httpD = new Http.SharedHttpClient(cfgD.ProxyUrl, reachD);
            var pipD = new PersianIndexProvider(cfgD);
            await pipD.EnsureAvailableAsync();
            var regD = new Providers.ProviderRegistry(cfgD, reachD,
                new ITunesProvider(httpD),
                new DeezerProvider(httpD),
                new YouTubeProvider(httpD),
                new SoundCloudProvider(httpD),
                new RadioJavanProvider(httpD),
                new Nex1MusicProvider(httpD),
                new PersianSitesProvider(httpD),
                pipD,
                new YtDlpProvider(cfgD),
                new RozMusicProvider(httpD),
                new MusicDelProvider(httpD),
                new BehMelodyProvider(httpD),
                new Melody98Provider(httpD),
                new AparatProvider(httpD),
                new BiaMusicProvider(httpD),
                new BeatMasteringProvider(httpD),
                new MusicsFaProvider(httpD));
            await regD.RefreshRoutesAsync();
            Console.WriteLine($"offline: [{string.Join(", ", regD.OfflineSources)}]");
            var svcD = new SearchService(regD.EnabledSearchProviders(),
                searchTimeoutSeconds: cfgD.SearchTimeoutSeconds);
            var worksD = new List<TrackWork>();
            await foreach (var w in svcD.SearchAsync(args[1])) worksD.Add(w);
            Console.WriteLine($"works: {worksD.Count} — top: {worksD.FirstOrDefault()?.Representative.Metadata.DisplayTitle}");
            if (worksD.Count == 0) return;
            foreach (var v in worksD[0].DownloadableVersions.Take(12))
                Console.WriteLine($"    version: {v.Provider} | {v.Metadata.DisplayTitle}");

            var dlDir = Path.Combine(Path.GetTempPath(), "musicengine-dl-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dlDir);
            cfgD.OutputDirectory = dlDir; // never write into the real library
            var dmD = new DownloadManager(regD.EnabledSearchProviders(),
                regD.DownloadProviders(), cfgD, new Audio.TrackTagger(httpD));
            dmD.JobProgress += (_, p) =>
            {
                if (p.Phase is DownloadPhase.Resolving or DownloadPhase.Downloading or DownloadPhase.Tagging
                    or DownloadPhase.Completed or DownloadPhase.Failed)
                {
                    Console.WriteLine($"    [{p.Phase}] {p.Message}");
                }
            };
            var finalD = await dmD.EnqueueAsync(worksD[0]);
            Console.WriteLine($"FINAL: {finalD.Phase} — {finalD.Message}");
            try { Directory.Delete(dlDir, true); } catch { }
            return;
        }
        if (args.Contains("debugpersian"))
        {
            var cfg0 = Configuration.AppConfig.Load();
            var pip = new PersianIndexProvider(cfg0);
            await pip.EnsureAvailableAsync();
            Console.WriteLine($"PersianIndex available: {pip.IsAvailable}");
            await foreach (var r in pip.SearchAsync("مهرزاد منو نترسون", 5))
                Console.WriteLine($"  post: {r.Metadata.Title} → {r.SourceUrl}");
            Console.WriteLine("---- pipeline ----");
            using var http0 = new Http.SharedHttpClient(cfg0.ProxyUrl);
            SearchService.DebugPhases = true;
            var svc0 = new SearchService(new ISearchProvider[]
            {
                new ITunesProvider(http0),
                new RadioJavanProvider(http0),
                new Nex1MusicProvider(http0),
                new PersianSitesProvider(http0),
                pip,
            }, searchTimeoutSeconds: cfg0.SearchTimeoutSeconds);
            await foreach (var w in svc0.SearchAsync("mehrzad mano natarsoon"))
                Console.WriteLine($"  work: {w.Representative.Metadata.DisplayTitle} [{w.Representative.Provider}] versions={w.Versions.Count}");

            Console.WriteLine("---- manual steps ----");
            var expanded = FinglishQueryExpander.Expand("mehrzad mano natarsoon");
            foreach (var e in expanded) Console.WriteLine($"  expansion: {e}");
            var parsed = QueryParser.Parse("mehrzad mano natarsoon");
            Console.WriteLine($"  parsed: artist='{parsed.Artist}' title='{parsed.Title}' explicit={parsed.HasExplicitStructure}");
            var itunes = new List<SearchResult>();
            await foreach (var r in new ITunesProvider(http0).SearchAsync($"\"{parsed.Artist}\" \"{parsed.Title}\"", 25))
                itunes.Add(r);
            Console.WriteLine($"  iTunes fielded rows: {itunes.Count}");
            var goal = GoalResolver.Resolve(parsed, itunes);
            Console.WriteLine($"  goal: artist='{goal.Artist}' title='{goal.Title}' dur={goal.Duration}");
            await foreach (var r in pip.SearchAsync("مهرزاد منو نترسون", 5))
            {
                Console.WriteLine($"  gate(post '{r.Metadata.Title}') goal={SearchService.PassesGoalGate(r, goal)} loose={SearchService.PassesLooseGate(r, goal)} junkTitle={JunkFilter.IsJunkTitle(r.Metadata.Title)}");
            }
            return;
        }

        if (args.Contains("domestic"))
        {
            // Verify the domestic (no-proxy) sources end-to-end through the app
            // pipeline: aimusicall search + liveness probe, nex1music search +
            // a real mp3 download.
            var cfgD2 = Configuration.AppConfig.Load();
            using var httpD2 = new Http.SharedHttpClient(cfgD2.ProxyUrl);

            Console.WriteLine("==== PersianSitesProvider (aimusicall.ir) ====");
            var ps = new PersianSitesProvider(httpD2);
            var psRows = 0;
            await foreach (var r in ps.SearchAsync("جهنم", 5))
            {
                psRows++;
                Console.WriteLine($"  live: {r.Metadata.Artist} — {r.Metadata.Title} → {r.SourceUrl[..Math.Min(70, r.SourceUrl.Length)]}");
            }
            Console.WriteLine(psRows == 0
                ? "  0 live rows — CDN dl.aimusicall.ir serves 404 for all files (liveness probe filtered dead links)."
                : $"  {psRows} live row(s) — CDN is serving again.");

            Console.WriteLine("==== Nex1MusicProvider (nex1music.com) ====");
            var nx = new Nex1MusicProvider(httpD2);
            var nxRows = new List<SearchResult>();
            await foreach (var r in nx.SearchAsync("mehrzad mano natarsoon", 3))
            {
                nxRows.Add(r);
                Console.WriteLine($"  row: {r.Metadata.Artist} — {r.Metadata.Title} [{r.MaxQuality}] → {r.SourceUrl[..Math.Min(60, r.SourceUrl.Length)]}");
            }
            if (nxRows.Count == 0)
            {
                Console.WriteLine("  No rows — nex1music unreachable or layout changed again.");
                return;
            }

            var dlDir = Path.Combine(Path.GetTempPath(), "musicengine-domestic-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dlDir);
            var opts = new DownloadOptions { OutputDirectory = dlDir };
            var res = await nx.DownloadAsync(nxRows[0], opts,
                new Progress<DownloadProgress>(p => Console.WriteLine($"    [{p.Phase}] {p.Message}")),
                CancellationToken.None);
            var fi = new FileInfo(res.FilePath);
            Console.WriteLine($"  DOWNLOADED: {fi.Name} — {fi.Length / 1024.0:F0} KB — exists={fi.Exists}");
            Console.WriteLine(fi.Exists && fi.Length > 100_000
                ? "  ✅ REAL DOWNLOAD VERIFIED through the app pipeline (domestic, no proxy)."
                : "  ⚠️ file missing/tiny — download failed.");
            try { Directory.Delete(dlDir, true); } catch { }
            return;
        }

        if (live) await RunLiveTestsAsync();
    }

    // ---------------- live tests ----------------

    /// <summary>Runs the pipeline for 'coldplay yellow' with one provider added at
    /// a time (30s cap each) to find which provider hangs on a dead-proxy network.</summary>
    private static async Task BisectAsync()
    {
        SearchService.DebugPhases = true;
        var cfg = Configuration.AppConfig.Load();
        using var http = new Http.SharedHttpClient(cfg.ProxyUrl);

        var steps = new (string Name, Func<ISearchProvider[]> Make)[]
        {
            ("iTunes", () => new ISearchProvider[] { new ITunesProvider(http) }),
            ("+Deezer", () => new ISearchProvider[] { new ITunesProvider(http), new DeezerProvider(http) }),
            ("+YouTube", () => new ISearchProvider[] { new ITunesProvider(http), new YouTubeProvider(http) }),
            ("+SoundCloud", () => new ISearchProvider[] { new ITunesProvider(http), new SoundCloudProvider(http) }),
            ("+RadioJavan", () => new ISearchProvider[] { new ITunesProvider(http), new RadioJavanProvider(http) }),
            ("ALL", () => new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http),
                new YouTubeProvider(http),
                new SoundCloudProvider(http),
                new RadioJavanProvider(http),
            }),
        };

        foreach (var (name, make) in steps)
        {
            var providers = make();
            Console.WriteLine($"—— {name} —— ", DateTime.UtcNow.ToString("HH:mm:ss"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var svc = new SearchService(providers, searchTimeoutSeconds: cfg.SearchTimeoutSeconds);
                var count = 0;
                var run = Task.Run(async () =>
                {
                    await foreach (var w in svc.SearchAsync("coldplay yellow")) count++;
                });
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(40))) != run)
                {
                    Console.WriteLine($"  ✘ {name}: HUNG >40s");
                    continue;
                }
                await run;
                Console.WriteLine($"  ✔ {name}: {count} works in {sw.ElapsedMilliseconds / 1000.0:0.0}s");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✘ {name}: {ex.Message}");
            }
        }
    }

    private static async Task RunLiveTestsAsync()
    {
        Console.WriteLine("\n―― live tests (network required) ――");

        var cfg = Configuration.AppConfig.Load();
        using var http = new Http.SharedHttpClient(cfg.ProxyUrl);

        await Live("iTunes search 'tataloo'", async () =>
        {
            var p = new ITunesProvider(http);
            var results = await CollectAsync(p.SearchAsync("tataloo", 10));
            return results.Any(r => r.PreviewOnly && r.DirectStreamUri is not null);
        });

        await Live("Radio Javan search 'تتلو بهشت'", async () =>
        {
            var p = new RadioJavanProvider(http);
            var results = await CollectAsync(p.SearchAsync("تتلو بهشت", 10));
            return results.Count > 0 && results.All(r => r.Downloadable);
        });

        await Live("Persian sites (aimusicall) search 'tataloo behesht'", async () =>
        {
            var p = new PersianSitesProvider(http);
            var results = await CollectAsync(p.SearchAsync("tataloo behesht", 5));
            Console.WriteLine($"    → {results.Count} direct MP3s");
            // NOTE: search-only — dl.aimusicall.ir (the file CDN) has been dead
            // server-side for months; the app auto-disables such dead download
            // points via reachability probes. File downloads are covered by the
            // PersianIndex/RadioJavan/SoundCloud end-to-end tests below.
            return results.Count > 0;
        });

        await Live("Radio Javan download first result", async () =>
        {
            var p = new RadioJavanProvider(http);
            var results = await CollectAsync(p.SearchAsync("تتلو بهشت", 5));
            if (results.Count == 0) return false;
            var dir = Path.Combine(Path.GetTempPath(), "musicengine-test");
            Directory.CreateDirectory(dir);
            var r = await p.DownloadAsync(results[0], new DownloadOptions { OutputDirectory = dir });
            var ok = File.Exists(r.FilePath) && new FileInfo(r.FilePath).Length > 100_000;
            try { Directory.Delete(dir, true); } catch { }
            return ok;
        });

        await Live("SoundCloud native search + client_id", async () =>
        {
            var p = new SoundCloudProvider(http);
            await p.EnsureInitializedAsync();
            var results = await CollectAsync(p.SearchAsync("amir tataloo", 5));
            Console.WriteLine($"    → {results.Count} tracks");
            return results.Count > 0 && results.All(r => r.Downloadable);
        });

        await Live("SoundCloud native download (progressive stream)", async () =>
        {
            var p = new SoundCloudProvider(http);
            await p.EnsureInitializedAsync();
            var results = await CollectAsync(p.SearchAsync("amir tataloo", 5));
            if (results.Count == 0) return false;
            var dir = Path.Combine(Path.GetTempPath(), "musicengine-sc-test");
            Directory.CreateDirectory(dir);
            var r = await p.DownloadAsync(results[0], new DownloadOptions { OutputDirectory = dir });
            var ok = File.Exists(r.FilePath) && new FileInfo(r.FilePath).Length > 200_000;
            Console.WriteLine($"    → saved {new FileInfo(r.FilePath).Length / 1024} KB: {Path.GetFileName(r.FilePath)}");
            try { Directory.Delete(dir, true); } catch { }
            return ok;
        });

        await Live("Full pipeline search 'tataloo behesht'", async () =>
        {
            var providers = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http),
                new YouTubeProvider(http),
                new SoundCloudProvider(http),
                new RadioJavanProvider(http),
                new Nex1MusicProvider(http),
                new PersianSitesProvider(http),
            };
            var svc = new SearchService(providers, searchTimeoutSeconds: cfg.SearchTimeoutSeconds);
            var works = new List<TrackWork>();
            await foreach (var w in svc.SearchAsync("tataloo behesht")) works.Add(w);
            Console.WriteLine($"    → {works.Count} works; top: {works.FirstOrDefault()?.Representative.Metadata.DisplayTitle}");
            return works.Count > 0;
        });

        await Live("Full pipeline search 'coldplay yellow'", async () =>
        {
            var providers = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http),
                new YouTubeProvider(http),
                new SoundCloudProvider(http),
                new RadioJavanProvider(http),
            };
            var svc = new SearchService(providers, searchTimeoutSeconds: cfg.SearchTimeoutSeconds);
            var works = new List<TrackWork>();
            await foreach (var w in svc.SearchAsync("coldplay yellow")) works.Add(w);
            var top = works.FirstOrDefault();
            Console.WriteLine($"    → {works.Count} works; top: {top?.Representative.Metadata.DisplayTitle} " +
                $"(downloadable versions: {top?.Versions.Count(v => v.Result.Downloadable)})");
            return works.Count > 0 && works.Any(w => w.Representative.PreviewOnly);
        });

        await Live("Full pipeline search 'fadaei azkaraj' (goal gate)", async () =>
        {
            var pipG = new PersianIndexProvider(cfg);
            await pipG.EnsureAvailableAsync();
            var providers = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http),
                new YouTubeProvider(http),
                new SoundCloudProvider(http),
                new RadioJavanProvider(http),
                new Nex1MusicProvider(http),
                new PersianSitesProvider(http),
                pipG,
            };
            var svc = new SearchService(providers, searchTimeoutSeconds: cfg.SearchTimeoutSeconds);
            var works = new List<TrackWork>();
            await foreach (var w in svc.SearchAsync("fadaei azkaraj")) works.Add(w);
            foreach (var w in works.Take(5))
                Console.WriteLine($"    → work: {w.Representative.Metadata.DisplayTitle}");
            // The goal: "Az Karaj ta Langeh Rud" (از کرج تا لنگه رود). The glued
            // query word "azkaraj"→"ازکرج" must match it, and the wrong-but-similar
            // "ای دختر کرجی از ترکاشوند" must NOT be the top hit.
            var wanted = works.FirstOrDefault(w =>
                TrackTextNormalizer.ContainsPhraseSpaceless(w.Representative.Metadata.Title ?? "", "azkaraj")
                || (w.Representative.Metadata.Title ?? "").Contains("لنگه", StringComparison.Ordinal)
                || (w.Representative.Metadata.Title ?? "").Contains("karaj", StringComparison.OrdinalIgnoreCase));
            var top = works.FirstOrDefault();
            var wrongOnTop = top is not null
                && (top.Representative.Metadata.Title ?? "").Contains("دختر کرجی", StringComparison.Ordinal);
            return wanted is not null && !wrongOnTop;
        });

        await Live("END-TO-END: search + DownloadManager + tagged file (upmusics)", async () =>
        {
            var pip = new PersianIndexProvider(cfg);
            await pip.EnsureAvailableAsync();
            var searchProviders = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new RadioJavanProvider(http),
                new Nex1MusicProvider(http),
                new PersianSitesProvider(http),
                pip,
            };
            var svc = new SearchService(searchProviders, searchTimeoutSeconds: cfg.SearchTimeoutSeconds);
            var works = new List<TrackWork>();
            await foreach (var w in svc.SearchAsync("mehrzad mano natarsoon")) works.Add(w);
            Console.WriteLine($"    → {works.Count} works; top: {works.FirstOrDefault()?.Representative.Metadata.DisplayTitle}");
            if (works.Count == 0) return false;

            var testCfg = Configuration.AppConfig.Load();
            testCfg.OutputDirectory = Path.Combine(Path.GetTempPath(), "musicengine-e2e");
            Directory.CreateDirectory(testCfg.OutputDirectory);
            var dm = new DownloadManager(searchProviders,
                new IDownloadProvider[]
                {
                    new RadioJavanProvider(http),
                    new Nex1MusicProvider(http),
                    new PersianSitesProvider(http),
                    pip,
                    new YtDlpProvider(testCfg),
                    new RozMusicProvider(http),
                    new MusicDelProvider(http),
                    new BehMelodyProvider(http),
                    new Melody98Provider(http),
                    new AparatProvider(http),
                },
                testCfg, new Audio.TrackTagger(http));

            var final = await dm.EnqueueAsync(works[0]);
            Console.WriteLine($"    → phase={final.Phase} msg={final.Message} file={final.FilePath}");
            var ok = final.Phase == DownloadPhase.Completed
                     && File.Exists(final.FilePath)
                     && new FileInfo(final.FilePath!).Length > 300_000;
            if (ok)
            {
                Console.WriteLine($"    → saved {new FileInfo(final.FilePath!).Length / 1024 / 1024.0:0.0} MB: {Path.GetFileName(final.FilePath!)}");
                try { Directory.Delete(testCfg.OutputDirectory, true); } catch { }
            }
            return ok;
        });
    }

    private static async Task Live(string name, Func<Task<bool>> assertion)
    {
        Console.WriteLine($"… [live] {name}");
        try
        {
            var run = assertion();
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(75))) != run)
            {
                Console.WriteLine($"✘ [live] {name} — TIMED OUT after 75s");
                return;
            }
            var ok = await run;
            Console.WriteLine($"{(ok ? "✔" : "✘")} [live] {name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✘ [live] {name}\n    threw: {ex.Message}");
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
