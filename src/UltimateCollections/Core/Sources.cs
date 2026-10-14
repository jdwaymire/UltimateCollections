// Sources.cs
//
// Downloads the online sources of a collection: TMDb discover (keywords, actors, release years, ratings) and public
// MDBList lists. Everything here costs API requests, so it only runs when ListCache decides a list is due.
//
// NOTE: TMDb paging is not reliable for long lists; titles can slip between pages. See DiscoverAsync for what we do
// about it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UltimateCollections.Core
{
    // JSON (de)serializer. In Emby this wraps the server's IJsonSerializer.
    // DTO property names equal the JSON names, so it works with any case rule.
    public interface IJson
    {
        T Deserialize<T>(string json);
        string Serialize(object value);
    }

    public interface ISyncLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    // One collection's downloaded list: TMDb / IMDb ids of movies and shows. Listed is the number of titles the
    // sources had, only for the log.
    public sealed class SourceList
    {
        public HashSet<long> Tmdb { get; } = new HashSet<long>();
        public HashSet<string> Imdb { get; } = new HashSet<string>();
        public HashSet<long> TvTmdb { get; } = new HashSet<long>();
        public HashSet<string> TvImdb { get; } = new HashSet<string>();
        public int Listed { get; set; }
    }

    // ---- HTTP ----

    // Plain HTTP GET with retries on 429/5xx and network errors (4 tries, waiting 2, 4, 8 s, or what Retry-After
    // says). One shared HttpClient for the whole process, as HttpClient is meant to be used.
    public class Http
    {
        static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("UltimateCollections/0.3 (Emby plugin)");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        public virtual async Task<string> GetAsync(string url, IDictionary<string, string> headers, CancellationToken ct)
        {
            const int tries = 4;
            for (var attempt = 0; ; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (headers != null)
                        foreach (var h in headers)
                            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    HttpResponseMessage resp;
                    try
                    {
                        resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException) when (attempt < tries - 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct).ConfigureAwait(false);
                        continue;
                    }
                    using (resp)
                    {
                        var code = (int)resp.StatusCode;
                        if ((code == 429 || code >= 500) && attempt < tries - 1)
                        {
                            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (!resp.IsSuccessStatusCode)
                            throw new HttpStatusException(code, $"HTTP {code} {resp.ReasonPhrase} for {Redact(url)}");
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        // Error messages end up in the log, so the query string is cut off and only the path is shown.
        static string Redact(string url)
        {
            var q = url.IndexOf('?');
            return q < 0 ? url : url.Substring(0, q);
        }
    }

    // An error answer from the server (not a network failure), with its status code, so a caller can tell a refused
    // token (401) from the rest.
    public class HttpStatusException : HttpRequestException
    {
        public int Code { get; }

        public HttpStatusException(int code, string message) : base(message) => Code = code;
    }

    // TMDb said no to the token. Thrown for every request after the first refusal too, without asking TMDb again.
    public class TmdbTokenException : InvalidOperationException
    {
        public TmdbTokenException(string message) : base(message) { }
    }

    // ---- TMDb / MDBList DTOs ----

    // Lower-case property names on purpose: they are the JSON names.
#pragma warning disable IDE1006
    public sealed class TmdbNamed { public long id { get; set; } public string name { get; set; } }
    public sealed class TmdbPage
    {
        public List<TmdbNamed> results { get; set; }
        public int total_pages { get; set; }
        public int total_results { get; set; }
    }
    public sealed class TmdbGenres { public List<TmdbNamed> genres { get; set; } }
    public sealed class MdbListItem { public long? id { get; set; } public string imdb_id { get; set; } public string mediatype { get; set; } }
#pragma warning restore IDE1006

    // ---- TMDb ----

    // TMDb API v3 with the user's read access token (Bearer header, so the token never sits in a URL). Throttled
    // to 4 requests/s to stay polite; Calls counts requests for the log, since keeping that number low is the point.
    public class TmdbClient
    {
        static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);   // max 4 requests/s
        readonly Http _http;
        readonly IJson _json;
        readonly Dictionary<string, string> _headers;
        readonly Dictionary<string, Dictionary<string, long>> _genres = new Dictionary<string, Dictionary<string, long>>();
        DateTime _last = DateTime.MinValue;

        public int Calls { get; private set; }

        // Where notes about incomplete downloads go (the sync log); may be null.
        public Action<string> Warn { get; set; }

        // Set once TMDb refuses the token (HTTP 401): the message for the log. The run then makes no more TMDb requests.
        public string Refused { get; private set; }

        // NOTE: the usual cause is the 32-character "API Key" pasted instead of the "API Read Access
        // Token". We send the token as a Bearer header, and TMDb answers 401 to a key sent that way.
        public const string RefusedMessage = "TMDb refused the token (HTTP 401 Unauthorized). Use the \"API Read Access Token\" " +
            "(the long one, starting with eyJ), not the shorter \"API Key\"; both are on themoviedb.org > Settings > API. " +
            "If it already is the Read Access Token, it may have been regenerated there: copy it again";

        public TmdbClient(Http http, IJson json, string token)
        {
            _http = http;
            _json = json;
            _headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        }

        // Every TMDb request goes through here: waits out the 250 ms gap, counts the call, builds the query string.
        // After a 401 it throws straight away: a refused token stays refused, so asking again only costs requests.
        async Task<T> GetAsync<T>(string path, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
        {
            if (Refused != null)
                throw new TmdbTokenException(Refused);
            var wait = MinInterval - (DateTime.UtcNow - _last);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            _last = DateTime.UtcNow;
            Calls++;
            var q = string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            string body;
            try
            {
                body = await _http.GetAsync("https://api.themoviedb.org" + path + (q.Length > 0 ? "?" + q : ""), _headers, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpStatusException e) when (e.Code == 401)
            {
                Refused = RefusedMessage;
                throw new TmdbTokenException(Refused);
            }
            return _json.Deserialize<T>(body);
        }

        // Q("a", "1", "b", "2") -> query pairs; keeps the call sites short.
        static IEnumerable<KeyValuePair<string, string>> Q(params string[] kv)
        {
            for (var i = 0; i < kv.Length; i += 2)
                yield return new KeyValuePair<string, string>(kv[i], kv[i + 1]);
        }

        public Task<long> KeywordIdAsync(string kw, CancellationToken ct) => SearchIdAsync("keyword", kw, ct);

        public Task<long> PersonIdAsync(string name, CancellationToken ct) => SearchIdAsync("person", name, ct);

        // TMDb id of a keyword or person by exact name (a number is used as the id, no request). Several people with
        // the same name: TMDb's first (most popular) one. No exact match is an error that lists the closest names,
        // rather than guessing and filling the collection with the wrong titles.
        async Task<long> SearchIdAsync(string what, string text, CancellationToken ct)
        {
            if (long.TryParse(text, out var id))
                return id;
            var res = (await GetAsync<TmdbPage>("/3/search/" + what, Q("query", text), ct).ConfigureAwait(false))?.results
                      ?? new List<TmdbNamed>();
            var exact = res.FirstOrDefault(r => string.Equals(r.name, text, StringComparison.OrdinalIgnoreCase));
            if (exact == null)
                throw new InvalidOperationException($"TMDb {what} '{text}' not found (close: {string.Join(", ", res.Take(5).Select(r => r.name))})");
            return exact.id;
        }

        // TMDb genre ids for "movie" or "tv". TV uses other genres (no Horror, 'Sci-Fi & Fantasy', ...): names that do
        // not exist for TV are skipped there; unknown movie genres are an error. The genre lists are fetched once per
        // run and kept in _genres.
        public async Task<List<long>> GenreIdsAsync(IEnumerable<string> names, string kind, CancellationToken ct)
        {
            if (!_genres.TryGetValue(kind, out var map))
            {
                var list = (await GetAsync<TmdbGenres>("/3/genre/" + kind + "/list", Q(), ct).ConfigureAwait(false))?.genres
                           ?? new List<TmdbNamed>();
                _genres[kind] = map = list.ToDictionary(g => g.name.ToLowerInvariant(), g => g.id);
            }
            var outIds = new List<long>();
            foreach (var n in names)
            {
                if (map.TryGetValue(n.ToLowerInvariant(), out var gid))
                    outIds.Add(gid);
                else if (kind == "movie")
                    throw new InvalidOperationException($"TMDb genre '{n}' unknown (valid: {string.Join(", ", map.Keys.OrderBy(x => x))})");
            }
            return outIds;
        }

        // Movies ("movie") or shows ("tv") having ANY of the keywords (if given), with ANY of the actors (if given,
        // movies only), in the year range (if given), with at least min_rating / min_votes, minus excluded
        // genres/keywords. All filtering is done by TMDb, so we only page through the answer.
        // Note the separators: '|' means OR for with_*, ',' for without_* means none of them.
        public async Task<HashSet<long>> DiscoverAsync(string kind, IReadOnlyList<string> keywords, IReadOnlyList<string> actors,
            IReadOnlyList<string> exclGenres,
            IReadOnlyList<string> exclKeywords, int maxPages, string[] years, string minRating, string minVotes, CancellationToken ct)
        {
            var p = new List<KeyValuePair<string, string>>(Q("include_adult", "false"));
            if (keywords.Count > 0)
            {
                var ids = new List<long>();
                foreach (var k in keywords)
                    ids.Add(await KeywordIdAsync(k, ct).ConfigureAwait(false));
                p.AddRange(Q("with_keywords", string.Join("|", ids)));
            }
            if (actors.Count > 0)
            {
                var ids = new List<long>();
                foreach (var a in actors)
                    ids.Add(await PersonIdAsync(a, ct).ConfigureAwait(false));
                p.AddRange(Q("with_cast", string.Join("|", ids)));
            }
            if (years != null)
            {
                var field = kind == "movie" ? "primary_release_date" : "first_air_date";
                p.AddRange(Q(field + ".gte", years[0] + "-01-01", field + ".lte", years[1] + "-12-31"));
            }
            if (!string.IsNullOrEmpty(minRating))
                p.AddRange(Q("vote_average.gte", minRating));
            if (!string.IsNullOrEmpty(minVotes))
                p.AddRange(Q("vote_count.gte", minVotes));
            if (exclGenres.Count > 0)
            {
                var gids = await GenreIdsAsync(exclGenres, kind, ct).ConfigureAwait(false);
                if (gids.Count > 0)
                    p.AddRange(Q("without_genres", string.Join(",", gids)));
            }
            if (exclKeywords.Count > 0)
            {
                var ids = new List<long>();
                foreach (var k in exclKeywords)
                    ids.Add(await KeywordIdAsync(k, ct).ConfigureAwait(false));
                p.AddRange(Q("without_keywords", string.Join(",", ids)));
            }

            // NOTE: TMDb computes every page per request and does not keep a fixed order among titles that tie on the sort
            // value, so while paging a title can slip across a page boundary and be missed. Found 2026-10-09 on the
            // 1003-title Halloween list: by popularity 1 title missed, by release date 4 (a different 4 each time), while
            // TMDb's total_results (1003) was exact. So a list that fits in maxPages is read in release-date order and,
            // if fewer titles came back than total_results, read again in other orders and merged until complete
            // (at most two extra passes). A complete list costs nothing extra.
            // A list longer than maxPages is ranked (popularity / vote count) to choose which titles make it: page 1 is
            // asked again in that order (one extra request); "top N" is approximate anyway, so it is not re-read.
            var date = kind == "movie" ? "primary_release_date" : "first_air_date";
            var ranked = keywords.Count > 0 ? "popularity.desc" : "vote_count.desc";     // as before 2026-10-09
            Task<TmdbPage> Page(string sort, int page) =>
                GetAsync<TmdbPage>("/3/discover/" + kind, p.Concat(Q("sort_by", sort, "page", page.ToString())), ct);

            // Reads all pages of one sort order into 'into', starting from a page 1 we already have. TMDb serves at
            // most 500 pages, and we stop at maxPages either way.
            async Task ReadAsync(string sort, TmdbPage first, HashSet<long> into)
            {
                var d = first;
                for (var page = 1; ; page++)
                {
                    if (page > 1)
                        d = await Page(sort, page).ConfigureAwait(false);
                    foreach (var m in d?.results ?? new List<TmdbNamed>())
                        into.Add(m.id);
                    if (page >= Math.Min(Math.Min(d?.total_pages ?? 1, 500), maxPages))
                        break;
                }
            }

            var result = new HashSet<long>();
            var firstPage = await Page(date + ".asc", 1).ConfigureAwait(false);
            if (Math.Min(firstPage?.total_pages ?? 1, 500) > maxPages)
            {
                await ReadAsync(ranked, await Page(ranked, 1).ConfigureAwait(false), result).ConfigureAwait(false);
                return result;
            }
            await ReadAsync(date + ".asc", firstPage, result).ConfigureAwait(false);
            var total = firstPage?.total_results ?? 0;
            foreach (var again in new[] { date + ".desc", "original_title.asc" })
            {
                if (result.Count >= total)
                    break;
                Warn?.Invoke($"TMDb paging missed {total - result.Count} of {total} titles; reading the list again ({again})");
                await ReadAsync(again, await Page(again, 1).ConfigureAwait(false), result).ConfigureAwait(false);
            }
            if (result.Count < total)
                Warn?.Invoke($"TMDb list still {total - result.Count} of {total} titles short after three passes; using what came back");
            return result;
        }
    }

    // ---- MDBList ----

    // Public MDBList lists are a free JSON download, no account or key needed.
    public class MdbListClient
    {
        readonly Http _http;
        readonly IJson _json;

        public MdbListClient(Http http, IJson json)
        {
            _http = http;
            _json = json;
        }

        // slug = "user/list", fetched from https://mdblist.com/lists/<user>/<list>/json.
        public async Task<List<MdbListItem>> ItemsAsync(string slug, CancellationToken ct)
        {
            var body = await _http.GetAsync("https://mdblist.com/lists/" + slug.Trim('/') + "/json", null, ct).ConfigureAwait(false);
            return _json.Deserialize<List<MdbListItem>>(body) ?? new List<MdbListItem>();
        }
    }

    // ---- putting a collection's list together ----

    public static class ListFetcher
    {
        // Downloads all online sources of one collection and merges them (several sources = ANY). This is the
        // 'download' that ListCache calls when a saved list is missing or due. The title source is not here: it
        // needs no download and is matched against the library every night.
        // TMDb keyword, actor and year settings go into one discover query, per kind (movie, and tv with 'tv yes').
        // MDBList shows are skipped unless 'tv yes'. Only the last release_years line counts.
        public static async Task<SourceList> FetchAsync(CollectionDefinition col, TmdbClient tmdb, MdbListClient mdb, int maxPages,
            CancellationToken ct)
        {
            var r = new SourceList();
            var tv = col.WantsTv;
            var actors = col.GetList("tmdb_actor");
            if (actors.Count > 0 && tv)
                throw new ConfigException("tmdb_actor works for movies only (TMDb's TV search has no cast filter); remove 'tv yes'");
            if (col.GetList("tmdb_keyword").Count > 0 || actors.Count > 0 || col.GetList("release_years").Count > 0)
            {
                string[] years = null;
                if (col.GetList("release_years").Count > 0)
                {
                    years = col.GetList("release_years").Last().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (years.Length != 2 || !years.All(y => y.All(char.IsDigit)))
                        throw new ConfigException("release_years needs two years, e.g. 'release_years 1980 1989'");
                }
                foreach (var kind in tv ? new[] { "movie", "tv" } : new[] { "movie" })
                {
                    var ids = await tmdb.DiscoverAsync(kind, col.GetList("tmdb_keyword"), actors, col.GetList("exclude_genre"),
                        col.GetList("exclude_keyword"), maxPages, years, col.Get("min_rating"), col.Get("min_votes"), ct).ConfigureAwait(false);
                    (kind == "movie" ? r.Tmdb : r.TvTmdb).UnionWith(ids);
                    r.Listed += ids.Count;
                }
            }
            foreach (var slug in col.GetList("mdblist"))
            {
                foreach (var it in await mdb.ItemsAsync(slug, ct).ConfigureAwait(false))
                {
                    var show = it.mediatype == "show";
                    if (show && !tv)
                        continue;
                    r.Listed++;
                    if (it.id.HasValue)
                        (show ? r.TvTmdb : r.Tmdb).Add(it.id.Value);
                    if (!string.IsNullOrEmpty(it.imdb_id))
                        (show ? r.TvImdb : r.Imdb).Add(it.imdb_id);
                }
            }
            return r;
        }
    }
}
