using System.Collections.Generic;
using System.Threading.Tasks;
using HCMSys.Models;

namespace HCMSys.Services
{
    public interface IJibbleApiService
    {
        Task<string?> GetAuthTokenAsync();

        /// <summary>
        /// Returns the flattened timesheet grid rows for every day in [fromDate, toDate] (inclusive),
        /// fully paginated against Jibble's Timesheets/People/TimeEntries endpoints (no row-count ceiling),
        /// merged with resolved manager names and employee notes. The range may span multiple calendar
        /// months. Any sub-fetch that failed or degraded is reported back via Warnings rather than silently
        /// producing incomplete data.
        /// </summary>
        Task<JibbleTimesheetFetchResult> GetTimesheetGridRowsAsync(string fromDate = "2026-08-01", string toDate = "2026-08-31", bool forceRefresh = false);
    }
}
