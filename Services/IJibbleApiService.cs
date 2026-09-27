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
        /// <param name="fromDate">Start date (yyyy-MM-dd). Omit/null/invalid to default to the 1st of the current calendar month.</param>
        /// <param name="toDate">End date (yyyy-MM-dd). Omit/null/invalid to default to the last day of the current calendar month.</param>
        Task<JibbleTimesheetFetchResult> GetTimesheetGridRowsAsync(string? fromDate = null, string? toDate = null, bool forceRefresh = false);
    }
}
