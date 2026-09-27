using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClosedXML.Excel;
using HCMSys.Models;
using HCMSys.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Rotativa.AspNetCore;
using Rotativa.AspNetCore.Options;

namespace HCMSys.Controllers
{
    public class TimeAttendanceController : Controller
    {
        private readonly IJibbleApiService _jibbleService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<TimeAttendanceController> _logger;

        public TimeAttendanceController(IJibbleApiService jibbleService, IWebHostEnvironment env, ILogger<TimeAttendanceController> logger)
        {
            _jibbleService = jibbleService;
            _env = env;
            _logger = logger;
        }

        /// <summary>The 1st and last day of the current calendar month, computed fresh on every call —
        /// used only to pre-fill the date pickers before any fetch happens. The actual fetch-time default
        /// (when a request omits fromDate/toDate) is resolved independently, in the same way, by
        /// JibbleApiService itself — this is not the single source of truth for that, just the initial UI state.</summary>
        private static (string From, string To) CurrentMonthDefaults()
        {
            var today = DateTime.Today;
            var start = new DateTime(today.Year, today.Month, 1);
            var end = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
            return (start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
        }

        [HttpGet]
        public IActionResult Index()
        {
            ViewData["Title"] = "Timesheets";
            var (from, to) = CurrentMonthDefaults();
            ViewBag.DefaultFromDate = from;
            ViewBag.DefaultToDate = to;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetTimesheetData(string? fromDate = null, string? toDate = null, bool forceRefresh = false)
        {
            try
            {
                var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, forceRefresh);
                return Json(new
                {
                    success = true,
                    count = result.Rows.Count,
                    fromDate = result.EffectiveFromDate,
                    toDate = result.EffectiveToDate,
                    data = result.Rows,
                    warnings = result.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching timesheet data from Jibble");
                return Json(new
                {
                    success = false,
                    message = "Failed to load timesheet data from Jibble API: " + ex.Message,
                    data = new List<object>()
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportTimesheetCsv(string? fromDate = null, string? toDate = null)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            var rows = result.Rows;
            var sb = new StringBuilder();

            // 12 Exact Columns in user specified order:
            // Date, Day, Full Name, Member Code, Position, Department, Manager(s), First In, Last Out, Worked Hours, Absence, Justification
            sb.AppendLine("Date,Day,Full Name,Member Code,Position,Department,Manager(s),First In,Last Out,Worked Hours,Absence,Justification");

            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",",
                    EscapeCsv(r.FormattedDate),
                    EscapeCsv(r.Day),
                    EscapeCsv(r.FullName),
                    EscapeCsv(r.MemberCode),
                    EscapeCsv(r.Position),
                    EscapeCsv(r.Department),
                    EscapeCsv(r.Managers),
                    EscapeCsv(r.FirstIn),
                    EscapeCsv(r.LastOut),
                    EscapeCsv(r.WorkedHours),
                    EscapeCsv(r.Absence),
                    EscapeCsv(r.Justification)
                ));
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            string fileName = $"Timesheets_{result.EffectiveFromDate}_to_{result.EffectiveToDate}.csv";
            return File(bytes, "text/csv", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportTimesheetExcel(
            string? fromDate = null,
            string? toDate = null,
            string employee = "",
            string department = "",
            string status = "",
            bool onlyAbsence = false)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;
            var rows = ApplyGridFilters(result.Rows, effectiveFromDate, effectiveToDate, employee, department, status, onlyAbsence);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Timesheets");
            ws.ShowGridLines = true;

            // Row 1: Company Logo
            string logoPath = Path.Combine(_env.WebRootPath, "dist", "img", "logo.png");
            if (System.IO.File.Exists(logoPath))
            {
                try
                {
                    ws.AddPicture(logoPath)
                      .MoveTo(ws.Cell("A1"), 5, 5)
                      .WithSize(130, 45);
                }
                catch { }
            }

            // Header Texts
            ws.Cell("C1").Value = "HCMSys • MENA HR";
            ws.Cell("C1").Style.Font.Bold = true;
            ws.Cell("C1").Style.Font.FontSize = 14;
            ws.Cell("C1").Style.Font.FontColor = XLColor.FromHtml("#2C3E50");

            ws.Cell("C2").Value = "TIME & ATTENDANCE TIMESHEET REPORT";
            ws.Cell("C2").Style.Font.Bold = true;
            ws.Cell("C2").Style.Font.FontSize = 11;
            ws.Cell("C2").Style.Font.FontColor = XLColor.FromHtml("#4E73DF");

            // Row 4: Given Date Range & Filter Metadata
            string dateRangeSubtitle = $"Date Range: {FormatDisplayDate(effectiveFromDate)} to {FormatDisplayDate(effectiveToDate)}";
            if (!string.IsNullOrWhiteSpace(employee)) dateRangeSubtitle += $"  |  Employee: {employee}";
            if (!string.IsNullOrWhiteSpace(department)) dateRangeSubtitle += $"  |  Department: {department}";
            if (onlyAbsence) dateRangeSubtitle += "  |  Filter: Absences & Time Off Only";
            dateRangeSubtitle += $"  |  Total Records: {rows.Count:N0}";

            ws.Cell("A4").Value = dateRangeSubtitle;
            ws.Cell("A4").Style.Font.Italic = true;
            ws.Cell("A4").Style.Font.FontSize = 9.5;
            ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#5A5C69");
            ws.Range("A4:L4").Merge();

            // Row 6: Table Header (12 Columns in exact specified order)
            string[] headers = new[]
            {
                "Date", "Day", "Full Name", "Member Code", "Position", "Department",
                "Manager(s)", "First In", "Last Out", "Worked Hours", "Absence", "Justification"
            };

            int startRow = 6;
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(startRow, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 10;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4E73DF");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#3D5CBE");
            }
            ws.Row(startRow).Height = 26;

            // Rows 7+: Populate Data
            int currentRow = startRow + 1;
            foreach (var r in rows)
            {
                var rowCells = ws.Range(currentRow, 1, currentRow, 12);

                ws.Cell(currentRow, 1).Value = r.FormattedDate;
                ws.Cell(currentRow, 2).Value = r.Day;
                ws.Cell(currentRow, 3).Value = r.FullName;
                ws.Cell(currentRow, 4).Value = r.MemberCode;
                ws.Cell(currentRow, 5).Value = r.Position;
                ws.Cell(currentRow, 6).Value = r.Department;
                ws.Cell(currentRow, 7).Value = r.Managers;
                ws.Cell(currentRow, 8).Value = r.FirstIn;
                ws.Cell(currentRow, 9).Value = r.LastOut;
                ws.Cell(currentRow, 10).Value = r.WorkedHours;
                ws.Cell(currentRow, 11).Value = r.Absence;
                ws.Cell(currentRow, 12).Value = r.Justification;

                // Formatting & Alignment
                ws.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 3).Style.Font.Bold = true;
                ws.Cell(currentRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                // Zebra striping
                if (currentRow % 2 == 0)
                {
                    rowCells.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8F9FC");
                }

                // Thin borders
                for (int c = 1; c <= 12; c++)
                {
                    var cell = ws.Cell(currentRow, c);
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E3E6F0");
                    cell.Style.Font.FontSize = 9.5;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                }

                ws.Row(currentRow).Height = 20;
                currentRow++;
            }

            // Footer Row: End of Report
            int footerRow = currentRow + 1;
            var footerRange = ws.Range(footerRow, 1, footerRow, 12);
            footerRange.Merge();
            footerRange.Value = "*** End of Report ***";
            footerRange.Style.Font.Italic = true;
            footerRange.Style.Font.Bold = true;
            footerRange.Style.Font.FontSize = 10;
            footerRange.Style.Font.FontColor = XLColor.FromHtml("#858796");
            footerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            footerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            footerRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            footerRange.Style.Border.TopBorderColor = XLColor.FromHtml("#D1D3E2");
            ws.Row(footerRow).Height = 25;

            // Auto-fit column widths with minimums
            ws.Columns(1, 12).AdjustToContents();
            for (int c = 1; c <= 12; c++)
            {
                if (ws.Column(c).Width < 14) ws.Column(c).Width = 14;
            }

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileBytes = stream.ToArray();
            string fileName = $"Timesheet_Report_{effectiveFromDate}_to_{effectiveToDate}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        /// <summary>
        /// "Attendance Report" PDF — per-employee summary (Expected/Regular/Absentees/Time Off/Rest Day
        /// Worked/Public Holiday Worked) across the requested range, matching the MENA HR reference layout.
        /// Built entirely from the same day-level rows the grid/Excel export use — no separate API call.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportAttendanceReportPdf(
            string? fromDate = null,
            string? toDate = null,
            string employee = "",
            string department = "",
            string status = "",
            bool onlyAbsence = false)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;
            var rows = ApplyGridFilters(result.Rows, effectiveFromDate, effectiveToDate, employee, department, status, onlyAbsence);

            var summary = JibbleReportBuilder.BuildAttendanceSummary(rows, effectiveFromDate, effectiveToDate);

            var viewModel = new AttendanceReportPdfViewModel
            {
                Rows = summary,
                Meta = new JibblePdfReportMeta
                {
                    FromDateDisplay = FormatDisplayDate(effectiveFromDate),
                    ToDateDisplay = FormatDisplayDate(effectiveToDate),
                    DepartmentFilter = department,
                    EmployeeFilter = employee
                }
            };

            return BuildPdfResult("AttendanceReportPdf", viewModel, $"Attendance_Report_{effectiveFromDate}_to_{effectiveToDate}.pdf");
        }

        /// <summary>
        /// "Overtime Roster" PDF. Jibble does not expose an overtime/time-off-*request* API to this app —
        /// see JibbleReportBuilder.BuildOvertimeRoster for exactly what is and isn't real Jibble data here.
        /// The reference sample's fixed management sign-off block is intentionally omitted (per product
        /// decision) since it isn't attendance data and would need to be kept in sync by hand otherwise.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportOvertimeRosterPdf(
            string? fromDate = null,
            string? toDate = null,
            string employee = "",
            string department = "",
            string status = "",
            bool onlyAbsence = false)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;
            var rows = ApplyGridFilters(result.Rows, effectiveFromDate, effectiveToDate, employee, department, status, onlyAbsence);

            var roster = JibbleReportBuilder.BuildOvertimeRoster(rows);

            var viewModel = new OvertimeRosterPdfViewModel
            {
                Rows = roster,
                Meta = new JibblePdfReportMeta
                {
                    FromDateDisplay = FormatDisplayDate(effectiveFromDate),
                    ToDateDisplay = FormatDisplayDate(effectiveToDate),
                    DepartmentFilter = department,
                    EmployeeFilter = employee
                }
            };

            return BuildPdfResult("OvertimeRosterPdf", viewModel, $"Overtime_Roster_{effectiveFromDate}_to_{effectiveToDate}.pdf");
        }

        /// <summary>
        /// CSV equivalent of ExportAttendanceReportPdf — same rows, same filters, no wkhtmltopdf dependency.
        /// Useful for local development (PDF export requires a wkhtmltopdf binary the Dockerfile installs;
        /// this doesn't need it) and for anyone who just wants the numbers in a spreadsheet-friendly format.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportAttendanceReportCsv(
            string? fromDate = null,
            string? toDate = null,
            string employee = "",
            string department = "",
            string status = "",
            bool onlyAbsence = false)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;
            var rows = ApplyGridFilters(result.Rows, effectiveFromDate, effectiveToDate, employee, department, status, onlyAbsence);
            var summary = JibbleReportBuilder.BuildAttendanceSummary(rows, effectiveFromDate, effectiveToDate);

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Member Code,Group,Manager(s),Expected Days of Work,Regular Days Work,Absentees,Time Off,Rest Day Worked,Public Holiday Worked");
            foreach (var r in summary)
            {
                sb.AppendLine(string.Join(",",
                    EscapeCsv(r.FullName),
                    EscapeCsv(r.MemberCode),
                    EscapeCsv(r.Group),
                    EscapeCsv(r.Managers),
                    r.ExpectedDaysOfWork,
                    r.RegularDaysWork,
                    r.Absentees,
                    r.TimeOff,
                    r.RestDayWorked,
                    r.PublicHolidayWorked
                ));
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            string fileName = $"Attendance_Report_{effectiveFromDate}_to_{effectiveToDate}.csv";
            return File(bytes, "text/csv", fileName);
        }

        /// <summary>
        /// CSV equivalent of ExportOvertimeRosterPdf — see JibbleReportBuilder.BuildOvertimeRoster for what
        /// is and isn't real Jibble data here. No wkhtmltopdf dependency, unlike the PDF version.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportOvertimeRosterCsv(
            string? fromDate = null,
            string? toDate = null,
            string employee = "",
            string department = "",
            string status = "",
            bool onlyAbsence = false)
        {
            var result = await _jibbleService.GetTimesheetGridRowsAsync(fromDate, toDate, false);
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;
            var rows = ApplyGridFilters(result.Rows, effectiveFromDate, effectiveToDate, employee, department, status, onlyAbsence);
            var roster = JibbleReportBuilder.BuildOvertimeRoster(rows);

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Group,Time Off Policy,Compensation,Start Date,Start Time,End Time,Duration,Reason,Requested On,Status");
            foreach (var r in roster)
            {
                sb.AppendLine(string.Join(",",
                    EscapeCsv(r.FullName),
                    EscapeCsv(r.Group),
                    EscapeCsv(r.TimeOffPolicy),
                    EscapeCsv(r.Compensation),
                    EscapeCsv(r.StartDate),
                    EscapeCsv(r.StartTime),
                    EscapeCsv(r.EndTime),
                    EscapeCsv(r.Duration),
                    EscapeCsv(r.Reason),
                    EscapeCsv(r.RequestedOn),
                    EscapeCsv(r.Status)
                ));
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            string fileName = $"Overtime_Roster_{effectiveFromDate}_to_{effectiveToDate}.csv";
            return File(bytes, "text/csv", fileName);
        }

        /// <summary>Shared filter pass reused by the Excel export and both PDF reports, so the three exports
        /// never drift out of sync on what "apply these filters" means.</summary>
        private static List<JibbleTimesheetGridRowDto> ApplyGridFilters(
            List<JibbleTimesheetGridRowDto> rows,
            string effectiveFromDate,
            string effectiveToDate,
            string employee,
            string department,
            string status,
            bool onlyAbsence)
        {
            // The service already fetches/trims to [effectiveFromDate, effectiveToDate], but the extra date
            // comparisons below are kept as a harmless defensive no-op in case the two ever diverge.
            IEnumerable<JibbleTimesheetGridRowDto> filtered = rows
                .Where(r => string.Compare(r.Date, effectiveFromDate, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(r => string.Compare(r.Date, effectiveToDate, StringComparison.OrdinalIgnoreCase) <= 0);

            if (!string.IsNullOrWhiteSpace(employee))
            {
                filtered = filtered.Where(r => r.FullName.Equals(employee, StringComparison.OrdinalIgnoreCase) || r.MemberCode.Equals(employee, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(department))
            {
                filtered = filtered.Where(r => r.Department.Equals(department, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                filtered = filtered.Where(r => r.StatusBadge.Equals(status, StringComparison.OrdinalIgnoreCase));
            }
            if (onlyAbsence)
            {
                filtered = filtered.Where(r => r.IsAbsenceOrTimeOff);
            }

            return filtered.OrderBy(r => r.Date).ThenBy(r => r.FullName).ToList();
        }

        private IActionResult BuildPdfResult(string viewName, object model, string fileName)
        {
            // Rotativa.AspNetCore 1.4.0's ViewAsPdf ignores the per-instance WkhtmlPath/WkhtmltopdfPath
            // properties in practice (verified against the real binary) — it always resolves the
            // wkhtmltopdf executable from the global RotativaConfiguration.Setup() call in Program.cs,
            // which points at wwwroot/Rotativa/wkhtmltopdf. That's the one place the binary needs to live.
            //
            // That binary is only ever placed there by the Dockerfile at image-build time (apt-installs
            // the patched-Qt wkhtmltopdf, then symlinks it in) — a bare local checkout / `dotnet run`
            // outside Docker won't have it. Check for it explicitly and fail with a clear, actionable
            // message instead of letting Rotativa's Process.Start throw an unhandled Win32Exception.
            string wkhtmltopdfPath = Path.Combine(_env.WebRootPath, "Rotativa", "wkhtmltopdf");
            if (!System.IO.File.Exists(wkhtmltopdfPath))
            {
                _logger.LogError("PDF export requested but wkhtmltopdf is not installed at {Path}. It is installed automatically inside the Docker image; for local development outside Docker, install it manually or run the app via `docker build`/`podman build` + run instead.", wkhtmltopdfPath);
                return StatusCode(503, new
                {
                    success = false,
                    message = "PDF export isn't available in this environment: the wkhtmltopdf binary Rotativa needs isn't installed at " + wkhtmltopdfPath + ". It's installed automatically in the Docker image used for deployment. To test PDF export locally outside Docker, install wkhtmltopdf yourself and place/symlink it at that exact path, or run the app via Docker/podman instead — the Excel/CSV exports and the rest of the app work fine either way."
                });
            }

            return new ViewAsPdf(viewName, model)
            {
                FileName = fileName,
                PageSize = Size.Letter,
                PageOrientation = Orientation.Portrait,
                ContentDisposition = ContentDisposition.Attachment,
                // wkhtmltopdf's "smart shrinking" tries to rescale the whole page if it thinks content
                // doesn't fit the page width, but it can misfire on long multi-page tables and squash
                // everything (including page count) down to one illegibly tiny page. Both report views are
                // built with fixed table layouts precisely so they don't need this — disable it outright.
                CustomSwitches = "--disable-smart-shrinking"
            };
        }

        private static string FormatDisplayDate(string yyyyMMdd)
        {
            return DateTime.TryParse(yyyyMMdd, out var dt) ? dt.ToString("dd-MMM-yyyy") : yyyyMMdd;
        }

        private string EscapeCsv(string? val)
        {
            if (string.IsNullOrEmpty(val)) return "\"\"";
            val = val.Replace("\"", "\"\"");
            return $"\"{val}\"";
        }
    }
}
