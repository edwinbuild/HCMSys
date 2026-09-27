using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClosedXML.Excel;
using HCMSys.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
            var rows = result.Rows;
            string effectiveFromDate = result.EffectiveFromDate;
            string effectiveToDate = result.EffectiveToDate;

            // The service already fetches/trims to [effectiveFromDate, effectiveToDate], but the extra
            // comparisons below are kept as a harmless defensive no-op in case the two ever diverge.
            rows = rows.Where(r => string.Compare(r.Date, effectiveFromDate, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            rows = rows.Where(r => string.Compare(r.Date, effectiveToDate, StringComparison.OrdinalIgnoreCase) <= 0).ToList();
            if (!string.IsNullOrWhiteSpace(employee))
            {
                rows = rows.Where(r => r.FullName.Equals(employee, StringComparison.OrdinalIgnoreCase) || r.MemberCode.Equals(employee, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(department))
            {
                rows = rows.Where(r => r.Department.Equals(department, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows = rows.Where(r => r.StatusBadge.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (onlyAbsence)
            {
                rows = rows.Where(r => r.IsAbsenceOrTimeOff).ToList();
            }

            // Ensure sorted by Date ascending, then Full Name ascending
            rows = rows.OrderBy(r => r.Date).ThenBy(r => r.FullName).ToList();

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
