namespace MusicEngine.Tests;

using Audio;
using Downloads;
using Models;
using Providers;
using Search;
using Text;

/// <summary>
/// Offline unit tests for the text/pipeline brain + optional live smoke tests.
///   dotnet run                     → offline tests only
///   dotnet run -- live             → offline + live search/download tests (needs network)
/// </summary>
public static class Program
{
    private static int _failures;

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
                Console.WriteLine($"  {hayName,-15} overlap={Text.TrackTextNormalizer.KeysOverlap(hay, need)}" +
                    $" tokens={Text.TrackTextNormalizer.ContainsAllTokens(hay, need)}" +
                    $" tokensRev={Text.TrackTextNormalizer.ContainsAllTokens(need, hay)}" +
                    $" phrase={Text.TrackTextNormalizer.ContainsPhraseSpaceless(hay, need)}" +
                    $" phraseRev={Text.TrackTextNormalizer.ContainsPhraseSpaceless(need, hay)}");
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
            var regF = new Providers.ProviderRegistry(cfgF, reachF,
                new ITunesProvider(httpF),
                new DeezerProvider(httpF, proxyUrl: cfgF.ProxyUrl),
                new YouTubeProvider(httpF, proxyUrl: cfgF.ProxyUrl),
                new SoundCloudProvider(httpF, proxyUrl: cfgF.ProxyUrl),
                new RadioJavanProvider(httpF, proxyUrl: cfgF.ProxyUrl),
                new Nex1MusicProvider(httpF),
                new PersianSitesProvider(httpF),
                new PersianIndexProvider(cfgF),
                new YtDlpProvider(cfgF));
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
            var provA = new Providers.ProviderRegistry(cfgA, reach,
                new ITunesProvider(httpA),
                new DeezerProvider(httpA, proxyUrl: cfgA.ProxyUrl),
                new YouTubeProvider(httpA, proxyUrl: cfgA.ProxyUrl),
                new SoundCloudProvider(httpA, proxyUrl: cfgA.ProxyUrl),
                new RadioJavanProvider(httpA, proxyUrl: cfgA.ProxyUrl),
                new Nex1MusicProvider(httpA),
                new PersianSitesProvider(httpA),
                new PersianIndexProvider(cfgA),
                new YtDlpProvider(cfgA));
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
        if (args.Contains("debugpersian"))
        {
            var cfg0 = Configuration.AppConfig.Load();
            var pip = new PersianIndexProvider(cfg0);
            Console.WriteLine($"PersianIndex available: {pip.IsAvailable}");
            await foreach (var r in pip.SearchAsync("مهرزاد منو نترسون", 5))
                Console.WriteLine($"  post: {r.Metadata.Title} → {r.SourceUrl}");
            Console.WriteLine("---- pipeline ----");
            using var http0 = new Http.SharedHttpClient(cfg0.ProxyUrl);
            SearchService.DebugPhases = true;
            var svc0 = new SearchService(new ISearchProvider[]
            {
                new ITunesProvider(http0),
                new RadioJavanProvider(http0, proxyUrl: cfg0.ProxyUrl),
                new Nex1MusicProvider(http0),
                new PersianSitesProvider(http0),
                pip,
            }, searchTimeoutSeconds: cfg0.SearchTimeoutSeconds);
            await foreach (var w in svc0.SearchAsync("mehrzad mano natarsoon"))
                Console.WriteLine($"  work: {w.Representative.Metadata.DisplayTitle} [{w.Representative.Provider}] versions={w.Versions.Count}");

            Console.WriteLine("---- manual steps ----");
            var expanded = Text.FinglishQueryExpander.Expand("mehrzad mano natarsoon");
            foreach (var e in expanded) Console.WriteLine($"  expansion: {e}");
            var parsed = Text.QueryParser.Parse("mehrzad mano natarsoon");
            Console.WriteLine($"  parsed: artist='{parsed.Artist}' title='{parsed.Title}' explicit={parsed.HasExplicitStructure}");
            var itunes = new List<SearchResult>();
            await foreach (var r in new ITunesProvider(http0).SearchAsync($"\"{parsed.Artist}\" \"{parsed.Title}\"", 25))
                itunes.Add(r);
            Console.WriteLine($"  iTunes fielded rows: {itunes.Count}");
            var goal = GoalResolver.Resolve(parsed, itunes);
            Console.WriteLine($"  goal: artist='{goal.Artist}' title='{goal.Title}' dur={goal.Duration}");
            await foreach (var r in pip.SearchAsync("مهرزاد منو نترسون", 5))
            {
                Console.WriteLine($"  gate(post '{r.Metadata.Title}') goal={SearchService.PassesGoalGate(r, goal)} loose={SearchService.PassesLooseGate(r, goal)} junkTitle={Text.JunkFilter.IsJunkTitle(r.Metadata.Title)}");
            }
            return;
        }

        Test("Finglish converts 'tataloo behesht' → 'تتلو بهشت'",
            () => FinglishConverter.Convert("tataloo behesht") == "تتلو بهشت");

        Test("Finglish expands to persian + latin variants",
            () => FinglishQueryExpander.Expand("tataloo behesht").Count >= 2);

        Test("Cross-script overlap: تتلو بهشت == tataloo behesht",
            () => TrackTextNormalizer.KeysOverlap("تتلو بهشت", "tataloo behesht"));

        Test("Token gate keeps the exact song only",
            () => TrackTextNormalizer.ContainsAllTokens("Amir Tataloo - Behesht", "tataloo behesht")
            && !TrackTextNormalizer.ContainsAllTokens("Amir Tataloo - Man Bahat Ghahram", "tataloo behesht"));

        Test("Normalizer strips bracket junk",
            () => TrackTextNormalizer.Normalize("Ahange Sijal [320]") == "ahange sijal"
            && TrackTextNormalizer.Normalize("Bargard (Official Audio)") == "bargard");

        Test("QueryParser: 'amir tataloo - behesht'",
            () => QueryParser.Parse("amir tataloo - behesht") is { HasExplicitStructure: true, Artist: "amir tataloo", Title: "behesht" });

        Test("QueryParser heuristic: 'amir tataloo behesht'",
            () => QueryParser.Parse("amir tataloo behesht") is { Artist: "amir tataloo", Title: "behesht" });

        Test("JunkFilter rejects reaction/download junk, keeps real titles",
            () => JunkFilter.IsJunkTitle("دانلود آهنگ")
            && JunkFilter.IsJunkTitle("REACTION to behesht 😭")
            && !JunkFilter.IsJunkTitle("Behesht"));

        Test("GoalResolver picks the best catalog row",
            () =>
            {
                var parsed = QueryParser.Parse("amir tataloo behesht");
                var rows = new List<SearchResult>
                {
                    new() { Provider = ProviderId.ITunes, Id = "1", Metadata = new TrackMetadata { Title = "Halam Avaz Shod", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(3) } },
                    new() { Provider = ProviderId.ITunes, Id = "2", Metadata = new TrackMetadata { Title = "Behesht", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(4) } },
                };
                var goal = GoalResolver.Resolve(parsed, rows);
                return goal.Title == "behesht" || goal.Title == "Behesht";
            });

        Test("Goal gate rejects wrong song + wrong duration",
            () =>
            {
                var goal = new GoalSong("amir tataloo", "behesht", TimeSpan.FromSeconds(240), ProviderId.ITunes);
                var wrong = new SearchResult
                {
                    Provider = ProviderId.YouTube, Id = "x",
                    Metadata = new TrackMetadata { Title = "Man Bahat Ghahram", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(4) },
                };
                var longReaction = new SearchResult
                {
                    Provider = ProviderId.YouTube, Id = "y",
                    Metadata = new TrackMetadata { Title = "Behesht", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(17) },
                };
                var good = new SearchResult
                {
                    Provider = ProviderId.YouTube, Id = "z",
                    Metadata = new TrackMetadata { Title = "Behesht (Official Audio)", Artist = "Amir Tataloo", Duration = TimeSpan.FromSeconds(235) },
                };
                var swappedQuery = new GoalSong("behesht", "amir tataloo", TimeSpan.FromSeconds(240), ProviderId.Unknown);
                return !SearchService.PassesGoalGate(wrong, goal)
                    && !SearchService.PassesGoalGate(longReaction, goal)
                    && SearchService.PassesGoalGate(good, goal)
                    && SearchService.PassesGoalGate(good, swappedQuery);
            });

        Test("Spaceless phrase matching: glued Finglish hits spaced Persian titles",
            () =>
            {
                var real = "از کرج تا لنگه رود";
                var imposter = "ای دختر کرجی از ترکاشوند";
                return TrackTextNormalizer.ContainsPhraseSpaceless(real, "azkaraj")
                    && TrackTextNormalizer.ContainsPhraseSpaceless("Az Karaj Ta Langerud", "azkaraj")
                    && !TrackTextNormalizer.ContainsPhraseSpaceless(imposter, "azkaraj")
                    // short Persian needles must not substring ("کرج" inside "کرجی")
                    && !TrackTextNormalizer.ContainsAllTokens(imposter, "az karaj")
                    && TrackTextNormalizer.ContainsAllTokens(real, "az karaj");
            });

        Test("Goal gate: 'fadaei azkaraj' matches the real song, rejects the imposter",
            () =>
            {
                var goal = new GoalSong("fadaei", "azkaraj", null, ProviderId.Unknown);
                var real = new SearchResult
                {
                    Provider = ProviderId.YouTube, Id = "r",
                    Metadata = new TrackMetadata { Title = "از کرج تا لنگه رود", Artist = "Fadaei", Duration = TimeSpan.FromMinutes(4) },
                };
                var imposter = new SearchResult
                {
                    Provider = ProviderId.PersianIndex, Id = "i",
                    Metadata = new TrackMetadata { Title = "ای دختر کرجی از ترکاشوند", Artist = "فدایی", Duration = TimeSpan.FromMinutes(4) },
                };
                var realLatin = new SearchResult
                {
                    Provider = ProviderId.YouTube, Id = "rl",
                    Metadata = new TrackMetadata { Title = "Az Karaj Ta Langerud", Artist = "Fadaei", Duration = TimeSpan.FromMinutes(4) },
                };
                return SearchService.PassesGoalGate(real, goal)
                    && SearchService.PassesGoalGate(realLatin, goal)
                    && !SearchService.PassesGoalGate(imposter, goal)
                    && !SearchService.PassesLooseGate(imposter, goal);
            });

        Test("FileNaming builds clean 'Artist - Title.mp3'",
            () => FileNaming.Build(new TrackMetadata { Artist = "Amir Tataloo", Title = "Behesht" },
                new SearchResult { Provider = ProviderId.YouTube, Id = "abc", Metadata = new TrackMetadata { Title = "junk title", Artist = "x" } })
                == "Amir Tataloo - Behesht.mp3");

        Console.WriteLine(_failures == 0
            ? "\n✔ All offline tests passed"
            : $"\n✘ {_failures} test(s) FAILED");
        if (_failures > 0) Environment.Exit(1);

        if (live) await RunLiveTestsAsync();
    }

    private static void Test(string name, Func<bool> assertion)
    {
        bool ok;
        try { ok = assertion(); }
        catch (Exception ex) { Console.WriteLine($"✘ {name}\n    threw: {ex.Message}"); _failures++; return; }
        Console.WriteLine($"{(ok ? "✔" : "✘")} {name}");
        if (!ok) _failures++;
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
            ("+Deezer", () => new ISearchProvider[] { new ITunesProvider(http), new DeezerProvider(http, proxyUrl: cfg.ProxyUrl) }),
            ("+YouTube", () => new ISearchProvider[] { new ITunesProvider(http), new YouTubeProvider(http, proxyUrl: cfg.ProxyUrl) }),
            ("+SoundCloud", () => new ISearchProvider[] { new ITunesProvider(http), new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl) }),
            ("+RadioJavan", () => new ISearchProvider[] { new ITunesProvider(http), new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl) }),
            ("ALL", () => new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http, proxyUrl: cfg.ProxyUrl),
                new YouTubeProvider(http, proxyUrl: cfg.ProxyUrl),
                new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl),
                new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
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
            var p = new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl);
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
            var p = new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl);
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
            var p = new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl);
            await p.EnsureInitializedAsync();
            var results = await CollectAsync(p.SearchAsync("amir tataloo", 5));
            Console.WriteLine($"    → {results.Count} tracks");
            return results.Count > 0 && results.All(r => r.Downloadable);
        });

        await Live("SoundCloud native download (progressive stream)", async () =>
        {
            var p = new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl);
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
                new DeezerProvider(http, proxyUrl: cfg.ProxyUrl),
                new YouTubeProvider(http, proxyUrl: cfg.ProxyUrl),
                new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl),
                new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
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
                new DeezerProvider(http, proxyUrl: cfg.ProxyUrl),
                new YouTubeProvider(http, proxyUrl: cfg.ProxyUrl),
                new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl),
                new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
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
            var providers = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new DeezerProvider(http, proxyUrl: cfg.ProxyUrl),
                new YouTubeProvider(http, proxyUrl: cfg.ProxyUrl),
                new SoundCloudProvider(http, proxyUrl: cfg.ProxyUrl),
                new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
                new Nex1MusicProvider(http),
                new PersianSitesProvider(http),
                new PersianIndexProvider(cfg),
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
            var searchProviders = new ISearchProvider[]
            {
                new ITunesProvider(http),
                new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
                new Nex1MusicProvider(http),
                new PersianSitesProvider(http),
                new PersianIndexProvider(cfg),
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
                    new RadioJavanProvider(http, proxyUrl: cfg.ProxyUrl),
                    new Nex1MusicProvider(http),
                    new PersianSitesProvider(http),
                    new PersianIndexProvider(cfg),
                    new YtDlpProvider(testCfg),
                },
                testCfg, new TrackTagger(http));

            var final = await dm.EnqueueAsync(works[0]);
            Console.WriteLine($"    → phase={final.Phase} msg={final.Message} file={final.FilePath}");
            var ok = final.Phase == DownloadPhase.Completed
                     && File.Exists(final.FilePath)
                     && new FileInfo(final.FilePath).Length > 300_000;
            if (ok)
            {
                Console.WriteLine($"    → saved {new FileInfo(final.FilePath).Length / 1024 / 1024.0:0.0} MB: {Path.GetFileName(final.FilePath)}");
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
