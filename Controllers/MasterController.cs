using DocumentFormat.OpenXml.Bibliography;
using DocumentFormat.OpenXml.InkML;
using DocumentFormat.OpenXml.Office2010.Excel;
using DocumentFormat.OpenXml.Wordprocessing;
 
using HCMSys.Models;
 
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Threading.Tasks;
using HCMSys.Services;

namespace HCMSys.Controllers
{
    public class MasterController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly IHcmsApiService _apiService;

        public MasterController(IWebHostEnvironment env, IHcmsApiService apiService)
        {
            _env = env;
            _apiService = apiService;
        }

        [HttpGet]
        public async Task<IActionResult> ApiHealthCheck()
        {
            try
            {
                var token = await _apiService.GetAuthTokenAsync();
                return Json(new { online = !string.IsNullOrEmpty(token) });
            }
            catch
            {
                return Json(new { online = false });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetApiEmployees()
        {
            List<EmployeeDto> employees = new List<EmployeeDto>();
            try
            {
                employees = await _apiService.GetEmployeesAsync();
            }
            catch { }

            var normalized = (employees ?? new List<EmployeeDto>()).Select(e => new {
                id = e.IMasterId > 0 ? e.IMasterId : e.Id,
                iMasterId = e.IMasterId > 0 ? e.IMasterId : e.Id,
                empId = e.Id,
                code = !string.IsNullOrEmpty(e.SCode) ? e.SCode : $"EMP-{e.Id:D3}",
                sCode = !string.IsNullOrEmpty(e.SCode) ? e.SCode : $"EMP-{e.Id:D3}",
                name = !string.IsNullOrEmpty(e.SName) ? e.SName : "",
                sName = !string.IsNullOrEmpty(e.SName) ? e.SName : "",
                joiningDate = e.JoiningDate.HasValue ? e.JoiningDate.Value.ToString("dd-MMM-yyyy") : "",
                dateOfJoining = e.JoiningDate.HasValue ? e.JoiningDate.Value.ToString("dd-MMM-yyyy") : "",
                department = !string.IsNullOrEmpty(e.SDepartmentName) ? e.SDepartmentName : "",
                departmentName = !string.IsNullOrEmpty(e.SDepartmentName) ? e.SDepartmentName : "",
                sDepartmentName = !string.IsNullOrEmpty(e.SDepartmentName) ? e.SDepartmentName : "",
                designation = !string.IsNullOrEmpty(e.SDesignationName) ? e.SDesignationName : "",
                designationName = !string.IsNullOrEmpty(e.SDesignationName) ? e.SDesignationName : "",
                sDesignationName = !string.IsNullOrEmpty(e.SDesignationName) ? e.SDesignationName : "",
                reportingManager = !string.IsNullOrEmpty(e.SReportingToName) ? e.SReportingToName : "",
                sReportingToName = !string.IsNullOrEmpty(e.SReportingToName) ? e.SReportingToName : "",
                sReportingToCode = !string.IsNullOrEmpty(e.SReportingToCode) ? e.SReportingToCode : "",
                contact = "",
                currentOutstanding = 0.00
            });
            return Json(normalized);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiAssetCategories()
        {
            var categories = await _apiService.GetAssetCategoriesAsync();
            var normalized = categories.Select(c => new {
                id = c.IMasterId,
                iMasterId = c.IMasterId,
                code = !string.IsNullOrEmpty(c.SCode) ? c.SCode : "",
                sCode = !string.IsNullOrEmpty(c.SCode) ? c.SCode : "",
                name = !string.IsNullOrEmpty(c.SName) ? c.SName : "",
                sName = !string.IsNullOrEmpty(c.SName) ? c.SName : ""
            });
            return Json(normalized);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiAssets()
        {
            var assets = await _apiService.GetAssetsAsync();
            var normalized = assets.Select(a => new {
                id = a.IMasterId,
                iMasterId = a.IMasterId,
                code = !string.IsNullOrEmpty(a.SCode) ? a.SCode : "",
                sCode = !string.IsNullOrEmpty(a.SCode) ? a.SCode : "",
                name = !string.IsNullOrEmpty(a.SName) ? a.SName : "",
                sName = !string.IsNullOrEmpty(a.SName) ? a.SName : "",
                iCategoryId = a.ICategoryId,
                categoryCode = a.SCategoryCode,
                categoryName = a.SCategoryName,
                tagNumber = a.STagNumber
            });
            return Json(normalized);
        }

        [HttpPost]
        public async Task<IActionResult> SaveApiAssetAllocation()
        {
            SaveAssetAllocationDto? dto = null;
            byte[]? fileBytes = null;
            string? fileName = null;
            string? contentType = null;

            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                dto = new SaveAssetAllocationDto
                {
                    IHeaderId = int.TryParse(form["iHeaderId"], out var hId) ? hId : 0,
                    SDocNo = form["sDocNo"].ToString() ?? "",
                    DDocDate = form["dDocDate"].ToString() ?? "",
                    DPostDate = form["dPostDate"].ToString() ?? "",
                    IEmpId = int.TryParse(form["iEmpId"], out var empId) ? empId : 0,
                    ICompanyId = int.TryParse(form["iCompanyId"], out var compId) ? compId : 1,
                    IPayYearId = int.TryParse(form["iPayYearId"], out var pyId) ? pyId : 1,
                    SComments = form["sComments"].ToString(),
                    SEmployeeCode = form["sEmployeeCode"].ToString(),
                    SEmployeeName = form["sEmployeeName"].ToString(),
                    SDepartmentName = form["sDepartmentName"].ToString(),
                    SDesignationName = form["sDesignationName"].ToString(),
                    SReportingToName = form["sReportingToName"].ToString()
                };

                var assetsStr = form["Assets"].ToString();
                if (!string.IsNullOrEmpty(assetsStr))
                {
                    try
                    {
                        var parsedAssets = System.Text.Json.JsonSerializer.Deserialize<List<AssetAllocationBodyDto>>(assetsStr, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (parsedAssets != null) dto.Assets = parsedAssets;
                    }
                    catch { }
                }

                if (form.Files.Count > 0)
                {
                    var formFile = form.Files[0];
                    using var ms = new System.IO.MemoryStream();
                    await formFile.CopyToAsync(ms);
                    fileBytes = ms.ToArray();
                    fileName = formFile.FileName;
                    contentType = formFile.ContentType;
                }
            }
            else
            {
                using var reader = new System.IO.StreamReader(Request.Body);
                var body = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(body))
                {
                    dto = System.Text.Json.JsonSerializer.Deserialize<SaveAssetAllocationDto>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }

            if (dto == null) return BadRequest(new { success = false, message = "Invalid request payload." });

            // Validate that no duplicate asset items are in the same submission
            if (dto.Assets != null && dto.Assets.Count > 1)
            {
                var duplicate = dto.Assets.GroupBy(a => a.IAssetId > 0 ? a.IAssetId.ToString() : (a.SAssetCode ?? ""))
                                          .FirstOrDefault(g => g.Count() > 1);
                if (duplicate != null)
                {
                    return Json(new { success = false, message = "Duplicate asset item found in allocation list. Each asset item can only be added once per document." });
                }
            }

            var result = await _apiService.SaveAssetAllocationAsync(dto, fileBytes, fileName, contentType);
            return Json(result ?? new ApiResponseEnvelope<object> { Success = false, Message = "Failed to call Save API." });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteApiAssetAllocation(int id)
        {
            if (id <= 0) return BadRequest(new { success = false, message = "Invalid allocation ID." });
            
            var result = await _apiService.DeleteAssetAllocationAsync(id);
            if (result != null && result.Success)
            {
                return Json(new { success = true, message = result.Message ?? "Asset allocation deleted successfully." });
            }

            // Handle remote database constraint errors gracefully
            var rawMsg = result?.Message ?? "Delete API call failed.";
            if (rawMsg.Contains("entity changes", StringComparison.OrdinalIgnoreCase) || rawMsg.Contains("inner exception", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { 
                    success = false, 
                    isConstraintError = true,
                    message = "The remote database cannot hard-delete this allocation because child asset line items are linked via database foreign key constraints. In ERP systems, assets should be returned via Asset De-Allocation." 
                });
            }

            return Json(result ?? new ApiResponseEnvelope<object> { Success = false, Message = "Failed to call Delete API." });
        }

        [HttpGet]
        public async Task<IActionResult> GetApiAssetAllocations(int companyId = 1, int payYearId = 1)
        {
            var allocations = await _apiService.GetAssetAllocationsAsync(companyId, payYearId);
            return Json(allocations);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiAssetAllocationById(int id)
        {
            var allocation = await _apiService.GetAssetAllocationByIdAsync(id);
            if (allocation == null) return NotFound(new { success = false, message = "Asset allocation not found." });
            return Json(allocation);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiLeaveTransactions(int companyId = 1, int payYearId = 1)
        {
            var transactions = await _apiService.GetLeaveTransactionsAsync(companyId, payYearId);
            return Json(transactions);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiLeaveTransactionById(int id)
        {
            var transaction = await _apiService.GetLeaveTransactionByIdAsync(id);
            if (transaction == null) return NotFound(new { success = false, message = "Leave transaction not found." });
            return Json(transaction);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiEmployeeLeaves(int id)
        {
            var leaves = await _apiService.GetEmployeeLeavesAsync(id);
            return Json(leaves);
        }

        [HttpGet]
        public async Task<IActionResult> GetApiLeaveTypes(int? employeeId = null)
        {
            var types = await _apiService.GetLeaveTypesAsync(employeeId);
            return Json(types);
        }

        private bool TryParseDate(string? dateStr, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(dateStr)) return false;

            var formats = new[] { "yyyy-MM-dd", "dd-MMM-yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fffZ", "d-M-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "d-MMM-yyyy" };
            if (DateTime.TryParseExact(dateStr.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
            {
                return true;
            }
            return DateTime.TryParse(dateStr, out dt);
        }

        [HttpPost]
        public async Task<IActionResult> SaveApiLeaveTransaction()
        {
            SaveLeaveTransactionDto? dto = null;
            byte[]? fileBytes = null;
            string? fileName = null;
            string? contentType = null;

            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                dto = new SaveLeaveTransactionDto
                {
                    IHeaderId = int.TryParse(form["iHeaderId"], out var hId) ? hId : 0,
                    SDocNo = form["sDocNo"].ToString() ?? "",
                    DDocDate = form["dDocDate"].ToString() ?? "",
                    DPostDate = form["dPostDate"].ToString() ?? "",
                    ICompanyId = int.TryParse(form["iCompanyId"], out var compId) ? compId : 1,
                    IPayYearId = int.TryParse(form["iPayYearId"], out var pyId) ? pyId : 1,
                    ITransTypeId = int.TryParse(form["iTransTypeId"], out var tId) ? tId : 0,
                    SComments = form["sComments"].ToString()
                };

                var leavesStr = form["Leaves"].ToString();
                if (!string.IsNullOrEmpty(leavesStr))
                {
                    try
                    {
                        var parsedLeaves = System.Text.Json.JsonSerializer.Deserialize<List<LeaveTransactionBodyDto>>(leavesStr, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (parsedLeaves != null) dto.Leaves = parsedLeaves;
                    }
                    catch { }
                }

                if (form.Files.Count > 0)
                {
                    var formFile = form.Files[0];
                    using var ms = new System.IO.MemoryStream();
                    await formFile.CopyToAsync(ms);
                    fileBytes = ms.ToArray();
                    fileName = formFile.FileName;
                    contentType = formFile.ContentType;
                }
            }
            else
            {
                using var reader = new System.IO.StreamReader(Request.Body);
                var body = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(body))
                {
                    dto = System.Text.Json.JsonSerializer.Deserialize<SaveLeaveTransactionDto>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }

            if (dto == null) return BadRequest(new { success = false, message = "Invalid request payload." });

            // Validate overlapping leaves against existing transactions
            if (dto.Leaves != null && dto.Leaves.Count > 0)
            {
                var existingTxs = await _apiService.GetLeaveTransactionsAsync(dto.ICompanyId, dto.IPayYearId);
                if (TryParseDate(dto.DDocDate, out var newStart))
                {
                    foreach (var newItem in dto.Leaves)
                    {
                        var newDuration = newItem.FDuration > 0 ? (int)Math.Ceiling(newItem.FDuration) : 1;
                        var newEnd = newStart.AddDays(newDuration - 1);
                        if (TryParseDate(dto.DPostDate, out var parsedPostDate) && parsedPostDate > newStart && parsedPostDate > newEnd)
                        {
                            newEnd = parsedPostDate;
                        }

                        foreach (var exTx in existingTxs)
                        {
                            if (exTx.IHeaderId == dto.IHeaderId && dto.IHeaderId > 0) continue; // Skip self when updating

                            if (!TryParseDate(exTx.DDocDate, out var exStart)) continue;

                            var exLines = (exTx.Leaves ?? new List<LeaveTransactionBodyDto>())
                                          .Where(l => l.IEmployeeId == newItem.IEmployeeId)
                                          .ToList();

                            foreach (var exLine in exLines)
                            {
                                var exDuration = exLine.FDuration > 0 ? (int)Math.Ceiling(exLine.FDuration) : 1;
                                var exEnd = exStart.AddDays(exDuration - 1);
                                if (TryParseDate(exTx.DPostDate, out var exPostDate) && exPostDate > exStart && exPostDate > exEnd)
                                {
                                    exEnd = exPostDate;
                                }

                                // Overlap condition: startA <= endB && endA >= startB
                                if (newStart.Date <= exEnd.Date && newEnd.Date >= exStart.Date)
                                {
                                    string empName = !string.IsNullOrEmpty(newItem.SEmployeeName) ? newItem.SEmployeeName : (!string.IsNullOrEmpty(exLine.SEmployeeName) ? exLine.SEmployeeName : $"Employee #{newItem.IEmployeeId}");
                                    string docNo = !string.IsNullOrEmpty(exTx.SDocNo) ? exTx.SDocNo : $"#{exTx.IHeaderId}";
                                    return Json(new ApiResponseEnvelope<object>
                                    {
                                        Success = false,
                                        Message = $"Overlapping leave detected! {empName} already has a leave application ({docNo}) from {exStart:dd-MMM-yyyy} to {exEnd:dd-MMM-yyyy} ({exLine.FDuration} days). Overlapping leave applications are not permitted."
                                    });
                                }
                            }
                        }
                    }
                }
            }

            var result = await _apiService.SaveLeaveTransactionAsync(dto, fileBytes, fileName, contentType);
            return Json(result ?? new ApiResponseEnvelope<object> { Success = false, Message = "Failed to call Save Leave Transaction API." });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteApiLeaveTransaction(int id)
        {
            if (id <= 0) return BadRequest(new { success = false, message = "Invalid leave transaction ID." });
            var result = await _apiService.DeleteLeaveTransactionAsync(id);
            return Json(result ?? new ApiResponseEnvelope<object> { Success = false, Message = "Failed to call Delete Leave Transaction API." });
        }

        [HttpGet]
        public async Task<IActionResult> GetApiHRRequests()
        {
            try
            {
                var filePath = System.IO.Path.Combine(_env.WebRootPath, "api-assets", "hr_requests.json");
                if (System.IO.File.Exists(filePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(filePath);
                    return Content(json, "application/json");
                }
            }
            catch { }
            return Json(new List<object>());
        }

        [HttpGet]
        public async Task<IActionResult> GetApiRequestMaster()
        {
            try
            {
                var filePath = System.IO.Path.Combine(_env.WebRootPath, "api-assets", "request_master.json");
                if (System.IO.File.Exists(filePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(filePath);
                    return Content(json, "application/json");
                }
            }
            catch { }
            return Json(new List<object>());
        }

        [HttpGet]
        public async Task<IActionResult> GetApiCommonMaster()
        {
            try
            {
                var filePath = System.IO.Path.Combine(_env.WebRootPath, "api-assets", "common_master.json");
                if (System.IO.File.Exists(filePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(filePath);
                    return Content(json, "application/json");
                }
            }
            catch { }
            return Json(new { });
        }

        /// <summary>
        /// ////Project master
        /// </summary>
        /// <returns></returns>
        public ActionResult ProjectIndex()
        {
            return View();
        }

        public ActionResult LeaveApplicationIndex()
        {
            return View();
        }

        public ActionResult CreateLeaveApplication(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        public ActionResult LeaveReturnIndex()
        {
            return View();
        }

        public ActionResult CreateLeaveReturn(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        public ActionResult ClaimIndex()
        {
            return View();
        }

        public ActionResult CreateClaim()
        {
            return View();
        }

        public ActionResult BusinessTripIndex()
        {
            return View();
        }

        public ActionResult CreateBusinessTrip()
        {
            return View();
        }

        public ActionResult AssetAllocationIndex()
        {
            return View();
        }

        public ActionResult AssetDeAllocationIndex()
        {
            return View();
        }

        public ActionResult HRRequestIndex()
        {
            return View();
        }

        public ActionResult HRDisbursementIndex()
        {
            return View();
        }

        public ActionResult HRReturnIndex()
        {
            return View();
        }

        public ActionResult HRDocumentIssueIndex()
        {
            return View();
        }

        public ActionResult AssetAllocation(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        public ActionResult AssetDeAllocation()
        {
            return View();
        }

        public ActionResult HRRequest(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        public ActionResult HRDisbursement(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        public ActionResult HRReturn()
        {
            return View();
        }

        public ActionResult HRDocumentIssue()
        {
            return View();
        }

        public ActionResult OpeningLeavesIndex()
        {
            return View();
        }

        public ActionResult OpeningLeaves()
        {
            return View();
        }

        public ActionResult LeaveEligibilityIndex()
        {
            return View();
        }

        public ActionResult LeaveEligibility()
        {
            return View();
        }

        public ActionResult LeaveAdjustmentsIndex()
        {
            return View();
        }

        public ActionResult LeaveAdjustments()
        {
            return View();
        }

        // Loan Management Module Actions
        public ActionResult LoanRequestIndex()
        {
            return View();
        }

        public ActionResult CreateLoanRequest()
        {
            return View();
        }

        public ActionResult LoanDisbursementIndex()
        {
            return View();
        }

        public ActionResult CreateLoanDisbursement()
        {
            return View();
        }

        public ActionResult LoanDefermentIndex()
        {
            return View();
        }

        public ActionResult CreateLoanDeferment()
        {
            return View();
        }

        public ActionResult LoanSettlementIndex()
        {
            return View();
        }

        public ActionResult CreateLoanSettlement()
        {
            return View();
        }

        // Contract Assignment Module Actions
        public ActionResult ContractAssignmentIndex()
        {
            return View();
        }

        public ActionResult CreateContractAssignment(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        // Increment / Promotion / Transfer (Salary Revision) Module Actions
        public ActionResult SalaryRevisionIndex()
        {
            return View();
        }

        public ActionResult CreateSalaryRevision(int? id)
        {
            ViewBag.EditId = id ?? 0;
            return View();
        }

        private class WebClient : System.Net.WebClient
        {
            public int Timeout { get; set; }

            protected override WebRequest GetWebRequest(Uri uri)
            {
                WebRequest lWebRequest = base.GetWebRequest(uri);
                lWebRequest.Timeout = Timeout;
                ((HttpWebRequest)lWebRequest).ReadWriteTimeout = Timeout;
                return lWebRequest;
            }
        }

    }
}
