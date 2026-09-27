using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCMSys.Models
{
    public class JibbleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("organizationId")]
        public string? OrganizationId { get; set; }

        [JsonPropertyName("personId")]
        public string? PersonId { get; set; }
    }

    public class JibbleODataResponse<T>
    {
        [JsonPropertyName("@odata.context")]
        public string? Context { get; set; }

        [JsonPropertyName("@odata.count")]
        public int? Count { get; set; }

        [JsonPropertyName("value")]
        public List<T> Value { get; set; } = new List<T>();
    }

    public class JibblePersonTimesheetModel
    {
        [JsonPropertyName("personId")]
        public string? PersonId { get; set; }

        [JsonPropertyName("total")]
        public string? Total { get; set; }

        [JsonPropertyName("totalTracked")]
        public string? TotalTracked { get; set; }

        [JsonPropertyName("totalPayroll")]
        public string? TotalPayroll { get; set; }

        [JsonPropertyName("person")]
        public JibblePersonModel? Person { get; set; }

        [JsonPropertyName("daily")]
        public List<JibbleDailyTimesheetModel>? Daily { get; set; }
    }

    public class JibblePersonModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("groupName")]
        public string? GroupName { get; set; }

        [JsonPropertyName("positionName")]
        public string? PositionName { get; set; }

        [JsonPropertyName("employmentTypeName")]
        public string? EmploymentTypeName { get; set; }

        [JsonPropertyName("managers")]
        public List<string>? Managers { get; set; }

        [JsonPropertyName("timeZone")]
        public string? TimeZone { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public class JibbleDailyTimesheetModel
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("firstIn")]
        public string? FirstIn { get; set; }

        [JsonPropertyName("firstInOffset")]
        public string? FirstInOffset { get; set; }

        [JsonPropertyName("lastOut")]
        public string? LastOut { get; set; }

        [JsonPropertyName("lastOutOffset")]
        public string? LastOutOffset { get; set; }

        [JsonPropertyName("startTime")]
        public string? StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public string? EndTime { get; set; }

        [JsonPropertyName("trackedHours")]
        public JibbleTrackedHoursModel? TrackedHours { get; set; }

        [JsonPropertyName("payrollHours")]
        public JibblePayrollHoursModel? PayrollHours { get; set; }

        [JsonPropertyName("timeOff")]
        public JibbleTimeOffModel? TimeOff { get; set; }
    }

    public class JibbleTrackedHoursModel
    {
        [JsonPropertyName("worked")]
        public string? Worked { get; set; }

        [JsonPropertyName("total")]
        public string? Total { get; set; }

        [JsonPropertyName("totalBreakTime")]
        public string? TotalBreakTime { get; set; }
    }

    public class JibblePayrollHoursModel
    {
        [JsonPropertyName("total")]
        public string? Total { get; set; }
    }

    public class JibbleTimeOffModel
    {
        [JsonPropertyName("paidTimeOff")]
        public string? PaidTimeOff { get; set; }

        [JsonPropertyName("unpaidTimeOff")]
        public string? UnpaidTimeOff { get; set; }

        [JsonPropertyName("isRestDay")]
        public bool IsRestDay { get; set; }

        [JsonPropertyName("types")]
        public List<JibbleTimeOffTypeModel>? Types { get; set; }
    }

    public class JibbleTimeOffTypeModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("compensation")]
        public string? Compensation { get; set; }
    }

    /// <summary>
    /// Flattened DTO tailored precisely to the 12 columns requested by the user:
    /// Date | Day | Full Name | Member Code | Position | Department | Manager(s) | First In | Last Out | Worked Hours | Absence | Justification
    /// </summary>
    public class JibbleTimesheetGridRowDto
    {
        public string Date { get; set; } = string.Empty;           // e.g. "2026-08-01"
        public string FormattedDate { get; set; } = string.Empty;  // e.g. "01-Aug-2026"
        public string Day { get; set; } = string.Empty;            // e.g. "Saturday"
        public string FullName { get; set; } = string.Empty;       // e.g. "Nasser Al Syabi"
        public string MemberCode { get; set; } = string.Empty;     // e.g. "NAS-1"
        public string Position { get; set; } = string.Empty;       // e.g. "Call Center Agent" or "-"
        public string Department { get; set; } = string.Empty;     // e.g. "Call Center Agent" or "-"
        public string Managers { get; set; } = string.Empty;       // e.g. "Manager Name" or "-"
        public string Justification { get; set; } = string.Empty;  // e.g. "Shift off" or "Rest Day" or employee note
        public string FirstIn { get; set; } = string.Empty;        // e.g. "02:42 PM" or "-"
        public string LastOut { get; set; } = string.Empty;        // e.g. "11:18 PM" or "-"
        public string WorkedHours { get; set; } = string.Empty;    // e.g. "9h 48m" or "-"
        public double WorkedHoursDecimal { get; set; }             // e.g. 9.80
        public string Absence { get; set; } = string.Empty;        // e.g. "Shift off (8h)" or "Rest Day" or "Absent" or "-"
        public string StatusBadge { get; set; } = string.Empty;    // "Worked", "Rest Day", "Time Off", "Absent"
        public bool IsAbsenceOrTimeOff { get; set; }               // true if record represents Absence, Rest Day, or Time Off
    }

    public class JibbleWorkspacePersonModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("managers")]
        public List<object>? Managers { get; set; }

        [JsonPropertyName("unitManagers")]
        public List<JibbleUnitManagerModel>? UnitManagers { get; set; }

        [JsonPropertyName("group")]
        public JibbleWorkspaceGroupModel? Group { get; set; }
    }

    public class JibbleUnitManagerModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }
    }

    public class JibbleWorkspaceGroupModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("unitManagers")]
        public List<JibbleUnitManagerModel>? UnitManagers { get; set; }
    }

    public class JibbleTimeEntryModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("personId")]
        public string? PersonId { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("belongsToDate")]
        public string? BelongsToDate { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    /// <summary>
    /// Wraps the flattened timesheet grid rows together with any non-fatal warnings
    /// picked up while composing them (e.g. a sub-fetch such as Managers or Notes
    /// failed/was truncated even though the main Timesheets data came back fine).
    /// This lets the UI surface a warning instead of silently showing incomplete data.
    /// </summary>
    public class JibbleTimesheetFetchResult
    {
        public List<JibbleTimesheetGridRowDto> Rows { get; set; } = new List<JibbleTimesheetGridRowDto>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
