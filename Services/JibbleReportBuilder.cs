using System;
using System.Collections.Generic;
using System.Linq;
using HCMSys.Models;

namespace HCMSys.Services
{
    /// <summary>
    /// Pure in-memory transforms from the day-level timesheet grid rows we already fetch from Jibble
    /// into the two MENA HR report layouts (Attendance Report, Overtime Roster). No API calls here —
    /// both reports are built entirely from data JibbleApiService.GetTimesheetGridRowsAsync already
    /// returned, which is why this lives as a standalone builder rather than inside the API service.
    /// </summary>
    public static class JibbleReportBuilder
    {
        /// <summary>
        /// One row per employee, counting how many days in [fromDate, toDate] fell into each status —
        /// mirrors the "Attendance Report" PDF (Full Name, Member Code, Group, Manager(s), Expected Days
        /// of Work, Regular Days Work, Absentees, Time Off, Rest Day Worked, Public Holiday Worked).
        /// </summary>
        public static List<AttendanceSummaryRowDto> BuildAttendanceSummary(List<JibbleTimesheetGridRowDto> rows, string fromDate, string toDate)
        {
            int expectedDays = 1;
            if (DateTime.TryParse(fromDate, out var fromDt) && DateTime.TryParse(toDate, out var toDt) && toDt >= fromDt)
            {
                expectedDays = (toDt.Date - fromDt.Date).Days + 1;
            }

            // Group by Member Code where available (unique per employee), falling back to Full Name —
            // same convention used for the Employee filter in the grid, to avoid merging two different
            // people who happen to share a name.
            string KeyOf(JibbleTimesheetGridRowDto r) => (!string.IsNullOrWhiteSpace(r.MemberCode) && r.MemberCode != "-") ? r.MemberCode : r.FullName;

            var summaries = rows
                .GroupBy(KeyOf)
                .Select(g =>
                {
                    var first = g.First();
                    return new AttendanceSummaryRowDto
                    {
                        FullName = first.FullName,
                        MemberCode = first.MemberCode,
                        Group = first.Department,
                        Managers = first.Managers,
                        ExpectedDaysOfWork = expectedDays,
                        RegularDaysWork = g.Count(r => r.StatusBadge == "Worked"),
                        Absentees = g.Count(r => r.StatusBadge == "Absent"),
                        TimeOff = g.Count(r => r.StatusBadge == "Time Off"),
                        RestDayWorked = g.Count(r => r.StatusBadge == "Rest Day" && r.WorkedHoursDecimal > 0),
                        PublicHolidayWorked = g.Count(r => r.IsPublicHoliday && r.WorkedHoursDecimal > 0)
                    };
                })
                .OrderBy(s => s.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return summaries;
        }

        /// <summary>
        /// Derives "overtime instances" from the attendance rows: every day someone logged hours on what
        /// should have been a Rest Day or Public Holiday. Jibble does not expose a real overtime/time-off
        /// *request* API to this app (no policy, compensation, requested-on timestamp, or approval status
        /// data exists anywhere in what we fetch), so TimeOffPolicy/Compensation/RequestedOn/Status are
        /// fixed placeholders on the DTO — callers must make that clear to whoever reads the export.
        /// </summary>
        public static List<OvertimeRosterRowDto> BuildOvertimeRoster(List<JibbleTimesheetGridRowDto> rows)
        {
            return rows
                .Where(r => r.WorkedHoursDecimal > 0 && (r.StatusBadge == "Rest Day" || r.IsPublicHoliday))
                // Sort by the raw yyyy-MM-dd date (correct chronological order), then project to the
                // display-formatted DTO — sorting on FormattedDate ("dd-MMM-yy") would sort alphabetically
                // by day-of-month first and scramble the order.
                .OrderBy(r => r.Date, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new OvertimeRosterRowDto
                {
                    FullName = r.FullName,
                    Group = r.Department,
                    StartDate = r.FormattedDate,
                    StartTime = r.FirstIn,
                    EndTime = r.LastOut,
                    Duration = r.WorkedHours,
                    Reason = (r.Justification == "-" || string.IsNullOrWhiteSpace(r.Justification))
                        ? (r.IsPublicHoliday ? "Public Holiday" : "Rest Day")
                        : r.Justification,
                    RequestedOn = r.FormattedDate
                })
                .ToList();
        }
    }
}
