using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCMSys.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HCMSys.Services
{
    public class JibbleApiService : IJibbleApiService
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<JibbleApiService> _logger;

        private readonly string _clientId;
        private readonly string _clientSecret;

        private const string TokenUrl = "https://identity.prod.jibble.io/connect/token";
        private const string TokenCacheKey = "Jibble_Auth_Token";

        // Page sizes used when paginating Jibble's OData endpoints. These are request hints only —
        // every fetch loop below keeps paging (using $skip and, where available, @odata.count) until
        // the API itself reports there is nothing left, rather than assuming a fixed number of pages.
        private const int TimesheetsPageSize = 100;
        private const int PeoplePageSize = 200;
        private const int TimeEntriesPageSize = 1000;

        // Hard safety valve only, to guarantee the pagination loops below can never spin forever
        // against a misbehaving API (e.g. one that keeps returning a full page indefinitely).
        private const int MaxPagesPerFetch = 200;

        public JibbleApiService(HttpClient httpClient, IMemoryCache cache, IConfiguration configuration, ILogger<JibbleApiService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _logger = logger;
            _httpClient.Timeout = TimeSpan.FromSeconds(60);

            // Credentials come from configuration (appsettings.json "JibbleApi" section, overridable via
            // environment variables such as JibbleApi__ClientId / JibbleApi__ClientSecret in production)
            // rather than being hardcoded in source, matching how HcmsApi credentials are configured.
            _clientId = configuration["JibbleApi:ClientId"] ?? string.Empty;
            _clientSecret = configuration["JibbleApi:ClientSecret"] ?? string.Empty;

            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                _logger.LogError("Jibble API credentials are not configured. Set JibbleApi:ClientId and JibbleApi:ClientSecret in appsettings.json or as environment variables.");
            }
        }

        public async Task<string?> GetAuthTokenAsync()
        {
            if (_cache.TryGetValue(TokenCacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
            {
                return cachedToken;
            }

            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                _logger.LogError("Cannot obtain Jibble token: ClientId/ClientSecret are not configured.");
                return null;
            }

            try
            {
                var bodyParams = new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", _clientId },
                    { "client_secret", _clientSecret }
                };

                using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new FormUrlEncodedContent(bodyParams)
                };

                var response = await _httpClient.SendAsync(tokenRequest);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var tokenResp = JsonSerializer.Deserialize<JibbleTokenResponse>(json);
                    if (tokenResp != null && !string.IsNullOrEmpty(tokenResp.AccessToken))
                    {
                        var cacheDuration = TimeSpan.FromSeconds(Math.Max(300, tokenResp.ExpiresIn - 120));
                        _cache.Set(TokenCacheKey, tokenResp.AccessToken, cacheDuration);
                        return tokenResp.AccessToken;
                    }
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to obtain Jibble token: {Status} - {Error}", response.StatusCode, err);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while obtaining Jibble OAuth token");
            }

            return null;
        }

        public async Task<JibbleTimesheetFetchResult> GetTimesheetGridRowsAsync(string? fromDate = null, string? toDate = null, bool forceRefresh = false)
        {
            var warnings = new List<string>();

            // No hardcoded reporting period: an omitted or unparsable date defaults to the current
            // calendar month, resolved fresh against today's date every call.
            var today = DateTime.Today;
            var currentMonthStart = new DateTime(today.Year, today.Month, 1);
            var currentMonthEnd = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

            if (string.IsNullOrWhiteSpace(fromDate) || !DateTime.TryParse(fromDate, out var fromDt)) fromDt = currentMonthStart;
            if (string.IsNullOrWhiteSpace(toDate) || !DateTime.TryParse(toDate, out var toDt)) toDt = currentMonthEnd;
            if (toDt < fromDt)
            {
                // Defensive swap: never let a reversed range silently return nothing.
                (fromDt, toDt) = (toDt, fromDt);
            }

            string normFrom = fromDt.ToString("yyyy-MM-dd");
            string normTo = toDt.ToString("yyyy-MM-dd");
            string cacheKey = $"Jibble_Timesheets_{normFrom}_{normTo}";

            if (!forceRefresh && _cache.TryGetValue(cacheKey, out JibbleTimesheetFetchResult? cachedResult) && cachedResult != null)
            {
                return cachedResult;
            }

            var token = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Unable to fetch Jibble timesheets: No token available.");
                warnings.Add("Unable to authenticate with the Jibble API. No timesheet data could be retrieved.");
                return new JibbleTimesheetFetchResult { Rows = new List<JibbleTimesheetGridRowDto>(), Warnings = warnings, EffectiveFromDate = normFrom, EffectiveToDate = normTo };
            }

            try
            {
                // The Timesheets endpoint is queried per calendar month (Period=Month), so a range that
                // spans multiple months is broken into the distinct months it touches; the results are
                // merged per-person and the final rows are trimmed back down to the exact requested range.
                var months = new List<DateTime>();
                var cursor = new DateTime(fromDt.Year, fromDt.Month, 1);
                var lastMonth = new DateTime(toDt.Year, toDt.Month, 1);
                while (cursor <= lastMonth)
                {
                    months.Add(cursor);
                    cursor = cursor.AddMonths(1);
                }

                var timesheetsTask = FetchAllMonthsTimesheetsAsync(token, months, forceRefresh);
                var managersTask = GetPeopleManagersMapAsync(token, forceRefresh);
                var notesTask = GetTimeEntriesNotesMapAsync(token, normFrom, normTo, forceRefresh);

                await Task.WhenAll(timesheetsTask, managersTask, notesTask);

                var (personTimesheets, timesheetsOk) = await timesheetsTask;
                var (managersMap, managersOk) = await managersTask;
                var (notesMap, notesOk) = await notesTask;

                if (!timesheetsOk)
                {
                    warnings.Add("Some timesheet data could not be fully retrieved from Jibble for the selected range — the list below may be incomplete.");
                }
                if (!managersOk)
                {
                    warnings.Add("Manager information could not be retrieved from Jibble — the Manager(s) column may show '-' for some employees.");
                }
                if (!notesOk)
                {
                    warnings.Add("Some employee time-entry notes could not be retrieved from Jibble and may be missing from the Justification column.");
                }

                var rows = FlattenTimesheets(personTimesheets, managersMap, notesMap);

                // Trim to the exact requested window: fetching whole calendar months can pull in
                // days outside [fromDate, toDate] when the range doesn't align to month boundaries.
                rows = rows.Where(r => DateTime.TryParse(r.Date, out var d) && d.Date >= fromDt.Date && d.Date <= toDt.Date)
                           .OrderBy(r => r.Date).ThenBy(r => r.FullName).ToList();

                var result = new JibbleTimesheetFetchResult { Rows = rows, Warnings = warnings, EffectiveFromDate = normFrom, EffectiveToDate = normTo };

                // Cache for 60 minutes (user can click 'Refresh API' to bypass cache anytime)
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(60));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while querying Jibble APIs in parallel");
                warnings.Add("An unexpected error occurred while retrieving timesheet data from Jibble.");
            }

            return new JibbleTimesheetFetchResult { Rows = new List<JibbleTimesheetGridRowDto>(), Warnings = warnings, EffectiveFromDate = normFrom, EffectiveToDate = normTo };
        }

        /// <summary>
        /// Fetches Timesheets for each requested month in parallel (each fully paginated and cached at
        /// month granularity), then merges same-person records across months so multi-month ranges
        /// don't produce duplicate person entries.
        /// </summary>
        private async Task<(List<JibblePersonTimesheetModel> Data, bool Ok)> FetchAllMonthsTimesheetsAsync(string token, List<DateTime> months, bool forceRefresh)
        {
            var tasks = months.Select(m => FetchTimesheetsForMonthAsync(token, m, forceRefresh)).ToList();
            var results = await Task.WhenAll(tasks);

            bool allOk = results.All(r => r.Ok);

            var merged = new Dictionary<string, JibblePersonTimesheetModel>();
            foreach (var (monthList, _) in results)
            {
                foreach (var pt in monthList)
                {
                    var key = !string.IsNullOrWhiteSpace(pt.PersonId) ? pt.PersonId! : (pt.Person?.Id ?? Guid.NewGuid().ToString());
                    if (!merged.TryGetValue(key, out var existing))
                    {
                        merged[key] = pt;
                    }
                    else
                    {
                        existing.Daily ??= new List<JibbleDailyTimesheetModel>();
                        if (pt.Daily != null) existing.Daily.AddRange(pt.Daily);
                    }
                }
            }

            return (merged.Values.ToList(), allOk);
        }

        /// <summary>
        /// Fetches every page of Timesheets for a single calendar month, looping on $skip until the API
        /// returns fewer rows than requested (i.e. the last page), instead of assuming a single page is
        /// everything. Cached at month granularity so overlapping date-range requests reuse fetched months.
        /// </summary>
        private async Task<(List<JibblePersonTimesheetModel> Data, bool Ok)> FetchTimesheetsForMonthAsync(string token, DateTime monthStart, bool forceRefresh)
        {
            string monthCacheKey = $"Jibble_TimesheetsMonth_{monthStart:yyyy-MM}";
            if (!forceRefresh && _cache.TryGetValue(monthCacheKey, out List<JibblePersonTimesheetModel>? cachedMonth) && cachedMonth != null)
            {
                return (cachedMonth, true);
            }

            var result = new List<JibblePersonTimesheetModel>();
            string monthDateParam = monthStart.ToString("yyyy-MM-dd");
            bool ok = true;

            try
            {
                int skip = 0;
                for (int page = 0; page < MaxPagesPerFetch; page++)
                {
                    string queryUrl = $"https://time-attendance.prod.jibble.io/v1/Timesheets?Date={Uri.EscapeDataString(monthDateParam)}&Period=Month&$expand=person&$count=true&$top={TimesheetsPageSize}&$skip={skip}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        var err = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Error from Jibble Timesheets API ({Month}): {StatusCode} - {Message}", monthDateParam, response.StatusCode, err);
                        ok = false;
                        break;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var odataResp = await JsonSerializer.DeserializeAsync<JibbleODataResponse<JibblePersonTimesheetModel>>(stream, options);

                    var pageItems = odataResp?.Value ?? new List<JibblePersonTimesheetModel>();
                    result.AddRange(pageItems);

                    if (pageItems.Count < TimesheetsPageSize) break; // last page reached
                    skip += pageItems.Count;

                    if (odataResp?.Count.HasValue == true && skip >= odataResp.Count.Value) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while querying Jibble Timesheets API for {Month}", monthDateParam);
                ok = false;
            }

            if (ok)
            {
                _cache.Set(monthCacheKey, result, TimeSpan.FromMinutes(60));
            }

            return (result, ok);
        }

        public async Task<(Dictionary<string, string> Data, bool Ok)> GetPeopleManagersMapAsync(string token, bool forceRefresh = false)
        {
            string cacheKey = "Jibble_People_Managers";
            if (!forceRefresh && _cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
            {
                return (cached, true);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool ok = true;

            try
            {
                int skip = 0;
                for (int page = 0; page < MaxPagesPerFetch; page++)
                {
                    // Jibble Workspace People API with UnitManagers and Group.UnitManagers expanded, fully paginated.
                    string url = $"https://workspace.prod.jibble.io/v1/People?$select=id,email,code,fullName,managers&$expand=UnitManagers($select=id,fullName),Group($expand=UnitManagers($select=id,fullName))&$count=true&$top={PeoplePageSize}&$skip={skip}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to query People API: {StatusCode}", response.StatusCode);
                        ok = false;
                        break;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var odataResp = await JsonSerializer.DeserializeAsync<JibbleODataResponse<JibbleWorkspacePersonModel>>(stream, options);

                    var pageItems = odataResp?.Value ?? new List<JibbleWorkspacePersonModel>();
                    foreach (var p in pageItems)
                    {
                        AddPersonManagersToMap(map, p);
                    }

                    if (pageItems.Count < PeoplePageSize) break; // last page reached
                    skip += pageItems.Count;

                    if (odataResp?.Count.HasValue == true && skip >= odataResp.Count.Value) break;
                }

                if (ok)
                {
                    _cache.Set(cacheKey, map, TimeSpan.FromMinutes(60));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception querying Jibble People API");
                ok = false;
            }

            return (map, ok);
        }

        private void AddPersonManagersToMap(Dictionary<string, string> map, JibbleWorkspacePersonModel p)
        {
            var mgrNames = new List<string>();

            // 1. Direct UnitManagers
            if (p.UnitManagers != null)
            {
                foreach (var um in p.UnitManagers)
                {
                    if (!string.IsNullOrWhiteSpace(um.FullName))
                        mgrNames.Add(um.FullName.Trim());
                }
            }

            // 2. Group UnitManagers
            if (p.Group?.UnitManagers != null)
            {
                foreach (var gm in p.Group.UnitManagers)
                {
                    if (!string.IsNullOrWhiteSpace(gm.FullName))
                        mgrNames.Add(gm.FullName.Trim());
                }
            }

            // 3. Any direct managers
            if (p.Managers != null)
            {
                foreach (var m in p.Managers)
                {
                    if (m is JsonElement elem && elem.ValueKind == JsonValueKind.String)
                    {
                        var val = elem.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) mgrNames.Add(val.Trim());
                    }
                }
            }

            // Distinct list of managers
            var distinctMgrs = mgrNames.Distinct().ToList();
            if (distinctMgrs.Count == 0) return;

            string mgrsJoined = string.Join(", ", distinctMgrs);

            // Id is guaranteed unique per person, so it's always safe to key/overwrite by it. Code and
            // FullName are only used as fallback lookup keys (for when a caller doesn't have the person's
            // id handy) and are first-write-wins so that a name/code shared by two people never lets the
            // second person's data silently clobber the first person's already-resolved manager(s).
            if (!string.IsNullOrWhiteSpace(p.Id))
            {
                map[p.Id] = mgrsJoined;
            }
            if (!string.IsNullOrWhiteSpace(p.Code) && !map.ContainsKey(p.Code))
            {
                map[p.Code] = mgrsJoined;
            }
            if (!string.IsNullOrWhiteSpace(p.FullName) && !map.ContainsKey(p.FullName))
            {
                map[p.FullName] = mgrsJoined;
            }
        }

        public async Task<(Dictionary<(string personId, string date), string> Data, bool Ok)> GetTimeEntriesNotesMapAsync(string token, string startDate, string endDate, bool forceRefresh = false)
        {
            string cacheKey = $"Jibble_TimeEntries_Notes_{startDate}_{endDate}";
            if (!forceRefresh && _cache.TryGetValue(cacheKey, out Dictionary<(string personId, string date), string>? cached) && cached != null)
            {
                return (cached, true);
            }

            var result = new Dictionary<(string personId, string date), string>();
            var rawNotes = new Dictionary<(string personId, string date), List<string>>();
            bool ok = true;

            try
            {
                // First page also asks for @odata.count so we know exactly how many more pages are needed,
                // instead of assuming a fixed number of chunks. All remaining pages are then fetched in parallel.
                var (firstPage, totalCount) = await FetchTimeEntriesPageAsync(token, startDate, endDate, 0, TimeEntriesPageSize, includeCount: true);
                if (firstPage == null)
                {
                    ok = false;
                }
                else
                {
                    var allEntries = new List<JibbleTimeEntryModel>(firstPage);

                    int knownTotal = totalCount ?? firstPage.Count;
                    if (firstPage.Count == TimeEntriesPageSize && knownTotal > TimeEntriesPageSize)
                    {
                        var remainingSkips = new List<int>();
                        for (int skip = TimeEntriesPageSize; skip < knownTotal; skip += TimeEntriesPageSize)
                        {
                            remainingSkips.Add(skip);
                            if (remainingSkips.Count >= MaxPagesPerFetch) break; // safety valve
                        }

                        var pageTasks = remainingSkips.Select(skip => FetchTimeEntriesPageAsync(token, startDate, endDate, skip, TimeEntriesPageSize, includeCount: false)).ToList();
                        var pageResults = await Task.WhenAll(pageTasks);

                        foreach (var (chunk, _) in pageResults)
                        {
                            if (chunk == null) { ok = false; continue; }
                            allEntries.AddRange(chunk);
                        }
                    }
                    else if (firstPage.Count == TimeEntriesPageSize && totalCount == null)
                    {
                        // API didn't return a count but the first page was completely full — keep paging
                        // sequentially until a short page tells us we've reached the end.
                        int skip = TimeEntriesPageSize;
                        for (int page = 0; page < MaxPagesPerFetch; page++)
                        {
                            var (chunk, _) = await FetchTimeEntriesPageAsync(token, startDate, endDate, skip, TimeEntriesPageSize, includeCount: false);
                            if (chunk == null) { ok = false; break; }
                            allEntries.AddRange(chunk);
                            if (chunk.Count < TimeEntriesPageSize) break;
                            skip += chunk.Count;
                        }
                    }

                    foreach (var entry in allEntries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Note) || string.IsNullOrWhiteSpace(entry.PersonId) || string.IsNullOrWhiteSpace(entry.BelongsToDate))
                            continue;

                        var cleanNote = entry.Note.Trim();
                        if (string.IsNullOrEmpty(cleanNote)) continue;

                        var key = (entry.PersonId, entry.BelongsToDate);
                        if (!rawNotes.TryGetValue(key, out var list))
                        {
                            list = new List<string>();
                            rawNotes[key] = list;
                        }

                        if (!list.Contains(cleanNote))
                        {
                            list.Add(cleanNote);
                        }
                    }
                }

                // Format into joined string for each (personId, date)
                foreach (var kvp in rawNotes)
                {
                    result[kvp.Key] = string.Join(" | ", kvp.Value);
                }

                if (ok)
                {
                    _cache.Set(cacheKey, result, TimeSpan.FromMinutes(60));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception querying Jibble TimeEntries API");
                ok = false;
            }

            return (result, ok);
        }

        private async Task<(List<JibbleTimeEntryModel>? Items, int? Count)> FetchTimeEntriesPageAsync(string token, string startDate, string endDate, int skip, int top, bool includeCount)
        {
            try
            {
                string rawFilter = $"belongsToDate ge {startDate} and belongsToDate le {endDate}";
                string countParam = includeCount ? "&$count=true" : "";
                string url = $"https://time-tracking.prod.jibble.io/v1/TimeEntries?$filter={Uri.EscapeDataString(rawFilter)}&$select=id,personId,time,belongsToDate,type,note&$top={top}&$skip={skip}{countParam}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var odataResp = await JsonSerializer.DeserializeAsync<JibbleODataResponse<JibbleTimeEntryModel>>(stream, options);
                    return (odataResp?.Value ?? new List<JibbleTimeEntryModel>(), odataResp?.Count);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Error fetching TimeEntries page (skip={Skip}): {StatusCode} - {Message}", skip, response.StatusCode, err);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching TimeEntries page with skip={Skip}", skip);
            }

            return (null, null);
        }

        private List<JibbleTimesheetGridRowDto> FlattenTimesheets(
            List<JibblePersonTimesheetModel>? personTimesheets,
            Dictionary<string, string> managersMap,
            Dictionary<(string personId, string date), string> notesMap)
        {
            var result = new List<JibbleTimesheetGridRowDto>();
            if (personTimesheets == null || personTimesheets.Count == 0) return result;

            foreach (var pt in personTimesheets)
            {
                var person = pt.Person;
                string personId = pt.PersonId ?? person?.Id ?? string.Empty;
                string fullName = !string.IsNullOrWhiteSpace(person?.FullName) ? person.FullName.Trim() : "Unknown";
                string memberCode = !string.IsNullOrWhiteSpace(person?.Code) ? person.Code.Trim() : "-";
                string position = !string.IsNullOrWhiteSpace(person?.PositionName) ? person.PositionName.Trim() : "-";
                string department = !string.IsNullOrWhiteSpace(person?.GroupName) ? person.GroupName.Trim() : "-";

                // Resolve Manager(s) from People API (UnitManagers + Group.UnitManagers)
                string managers = "-";
                if (!string.IsNullOrWhiteSpace(personId) && managersMap.TryGetValue(personId, out var mgrs) && !string.IsNullOrWhiteSpace(mgrs))
                {
                    managers = mgrs;
                }
                else if (!string.IsNullOrWhiteSpace(memberCode) && memberCode != "-" && managersMap.TryGetValue(memberCode, out var mgrsCode) && !string.IsNullOrWhiteSpace(mgrsCode))
                {
                    managers = mgrsCode;
                }
                else if (!string.IsNullOrWhiteSpace(fullName) && fullName != "Unknown" && managersMap.TryGetValue(fullName, out var mgrsName) && !string.IsNullOrWhiteSpace(mgrsName))
                {
                    managers = mgrsName;
                }
                else if (person?.Managers != null && person.Managers.Count > 0)
                {
                    managers = string.Join(", ", person.Managers.Where(m => !string.IsNullOrWhiteSpace(m)));
                }
                if (string.IsNullOrWhiteSpace(managers)) managers = "-";

                var dailyList = pt.Daily;
                if (dailyList == null || dailyList.Count == 0) continue;

                foreach (var d in dailyList)
                {
                    string rawDate = d.Date ?? string.Empty;
                    string formattedDate = rawDate;
                    string dayName = "-";

                    if (DateTime.TryParse(rawDate, out var parsedDate))
                    {
                        formattedDate = parsedDate.ToString("dd-MMM-yy", CultureInfo.InvariantCulture);
                        dayName = parsedDate.ToString("dddd", CultureInfo.InvariantCulture);
                    }

                    // Format First In
                    string firstInDisplay = FormatTime(d.FirstInOffset, d.FirstIn);

                    // Format Last Out
                    string lastOutDisplay = FormatTime(d.LastOutOffset, d.LastOut);

                    // Format Worked Hours
                    var (workedDecimal, workedDisplay) = ParseIsoDuration(d.TrackedHours?.Worked);

                    // Justification & Absence determination
                    string justification = "-";
                    string absence = "-";
                    string statusBadge = "Worked";

                    var timeOff = d.TimeOff;
                    bool hasWorked = workedDecimal > 0 || firstInDisplay != "-";

                    // Jibble names public holidays as one of the timeOff.types entries (e.g. "Public Holiday",
                    // seen verbatim in real data as "Hijri new year (Public Holiday)"). Computed independently
                    // of the StatusBadge branching below so "worked on a public holiday" can still be detected
                    // even on a day that also happens to be flagged as a rest day.
                    bool isPublicHoliday = timeOff?.Types != null && timeOff.Types.Any(t => !string.IsNullOrWhiteSpace(t.Name) && t.Name.IndexOf("Holiday", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (timeOff != null && timeOff.Types != null && timeOff.Types.Count > 0)
                    {
                        var typeNames = string.Join(", ", timeOff.Types.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
                        justification = !string.IsNullOrWhiteSpace(typeNames) ? typeNames : "Time Off";

                        var (timeOffHours, _) = ParseIsoDuration(timeOff.PaidTimeOff);
                        if (timeOffHours == 0)
                        {
                            var (unpaidHours, _) = ParseIsoDuration(timeOff.UnpaidTimeOff);
                            timeOffHours = unpaidHours;
                        }

                        absence = timeOffHours > 0 ? $"{justification} ({timeOffHours:0.#}h)" : justification;
                        statusBadge = "Time Off";
                    }
                    else if (timeOff != null && timeOff.IsRestDay)
                    {
                        justification = "Rest Day";
                        absence = "Rest Day";
                        statusBadge = "Rest Day";
                    }
                    else if (!hasWorked)
                    {
                        absence = "Absent";
                        justification = "-";
                        statusBadge = "Absent";
                    }
                    else
                    {
                        statusBadge = "Worked";
                    }

                    // Attach note from TimeEntries API if available for this person on this date
                    if (!string.IsNullOrWhiteSpace(personId) && notesMap.TryGetValue((personId, rawDate), out var noteText) && !string.IsNullOrWhiteSpace(noteText))
                    {
                        if (justification == "-" || string.IsNullOrWhiteSpace(justification))
                        {
                            justification = noteText;
                        }
                        else
                        {
                            justification = $"{justification} | {noteText}";
                        }
                    }

                    result.Add(new JibbleTimesheetGridRowDto
                    {
                        Date = rawDate,
                        FormattedDate = formattedDate,
                        Day = dayName,
                        FullName = fullName,
                        MemberCode = memberCode,
                        Position = position,
                        Department = department,
                        Managers = managers,
                        Justification = justification,
                        FirstIn = firstInDisplay,
                        LastOut = lastOutDisplay,
                        WorkedHours = workedDisplay,
                        WorkedHoursDecimal = Math.Round(workedDecimal, 2),
                        Absence = absence,
                        StatusBadge = statusBadge,
                        IsAbsenceOrTimeOff = statusBadge != "Worked" || (absence != "-" && !string.IsNullOrWhiteSpace(absence)),
                        IsPublicHoliday = isPublicHoliday
                    });
                }
            }

            // Order by Date ascending, then Full Name ascending
            return result.OrderBy(r => r.Date).ThenBy(r => r.FullName).ToList();
        }

        private string FormatTime(string? timeOffsetStr, string? timeOfDayStr)
        {
            if (!string.IsNullOrWhiteSpace(timeOffsetStr) && DateTimeOffset.TryParse(timeOffsetStr, out var dto))
            {
                return dto.ToString("hh:mm tt", CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(timeOfDayStr))
            {
                var timePart = timeOfDayStr.Split('.')[0]; // remove subsecond ticks
                if (TimeSpan.TryParse(timePart, out var ts))
                {
                    return DateTime.Today.Add(ts).ToString("hh:mm tt", CultureInfo.InvariantCulture);
                }
            }

            return "-";
        }

        private (double Hours, string Display) ParseIsoDuration(string? isoDuration)
        {
            if (string.IsNullOrWhiteSpace(isoDuration) || isoDuration == "PT0S" || isoDuration == "P0D")
            {
                return (0, "0h 0m");
            }

            try
            {
                int days = 0;
                int hours = 0;
                int minutes = 0;

                var dMatch = Regex.Match(isoDuration, @"(\d+)D");
                if (dMatch.Success) days = int.Parse(dMatch.Groups[1].Value);

                var hMatch = Regex.Match(isoDuration, @"(\d+)H");
                if (hMatch.Success) hours = int.Parse(hMatch.Groups[1].Value);

                var mMatch = Regex.Match(isoDuration, @"(\d+)M");
                if (mMatch.Success) minutes = int.Parse(mMatch.Groups[1].Value);

                double totalHours = days * 24 + hours + (minutes / 60.0);

                var parts = new List<string>();
                int netHours = days * 24 + hours;
                if (netHours > 0) parts.Add($"{netHours}h");
                if (minutes > 0 || parts.Count == 0) parts.Add($"{minutes}m");

                return (totalHours, string.Join(" ", parts));
            }
            catch
            {
                return (0, "0h 0m");
            }
        }
    }
}
