// /Controllers/ImportController.cs
using System;
using System.IO;
using System.Web;
using System.Web.Mvc;
using RpacCommercial.Helpers;
using RpacCommercial.Models;
using RpacCommercial.Repositories;

namespace RpacCommercial.Controllers
{
    [Authorize]
    public class ImportController : Controller
    {
        private readonly IImportRepository _importRepository;
        private readonly ISupplierRepository _supplierRepository;

        public ImportController(IImportRepository importRepository, ISupplierRepository supplierRepository)
        {
            _importRepository = importRepository;
            _supplierRepository = supplierRepository;
        }

        public ActionResult Index(string status = null, int? supplierId = null, string search = null, int? page = null)
        {
            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "Import Management";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            ViewBag.SelectedStatus = status;
            ViewBag.SelectedSupplierId = supplierId;
            ViewBag.SearchText = search;

            var shipments = System.Linq.Enumerable.ToList(_importRepository.GetAllImports(status, null, null, supplierId, search));

            int pageSize = 20;
            int currentPage = page ?? 1;
            if (currentPage < 1) currentPage = 1;

            int totalItems = shipments.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
            
            // Handle case where currentPage is greater than totalPages
            if (currentPage > totalPages && totalPages > 0) currentPage = totalPages;

            var paginatedShipments = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Take(System.Linq.Enumerable.Skip(shipments, (currentPage - 1) * pageSize), pageSize));

            ViewBag.CurrentPage = currentPage;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = totalItems;
            ViewBag.PageSize = pageSize;

            return View(paginatedShipments);
        }

        [HttpGet]
        public ActionResult Create()
        {
            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "New Import Shipment";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(ImportShipment shipment, HttpPostedFileBase scanwellQtyFile, HttpPostedFileBase egxFile, HttpPostedFileBase transwingFile, HttpPostedFileBase insuranceFile)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    // 1. Scanwell Attachment
                    if (scanwellQtyFile != null && scanwellQtyFile.ContentLength > 0)
                    {
                        string fileName = "Scanwell_" + DateTime.Now.Ticks + Path.GetExtension(scanwellQtyFile.FileName);
                        shipment.ScanwellQtyContent = ReadFileBytes(scanwellQtyFile);
                        shipment.ScanwellQtyPath = "/Uploads/Imports/" + fileName;
                        shipment.ScanwellQtyFileName = scanwellQtyFile.FileName;
                    }

                    // 2. EGX Attachment
                    if (egxFile != null && egxFile.ContentLength > 0)
                    {
                        string fileName = "EGX_" + DateTime.Now.Ticks + Path.GetExtension(egxFile.FileName);
                        shipment.FGXContent = ReadFileBytes(egxFile);
                        shipment.FGXPath = "/Uploads/Imports/" + fileName;
                        shipment.FGXFileName = egxFile.FileName;
                    }

                    // 3. Transwing Attachment
                    if (transwingFile != null && transwingFile.ContentLength > 0)
                    {
                        string fileName = "Transwing_" + DateTime.Now.Ticks + Path.GetExtension(transwingFile.FileName);
                        shipment.TranswingContent = ReadFileBytes(transwingFile);
                        shipment.TranswingPath = "/Uploads/Imports/" + fileName;
                        shipment.TranswingFileName = transwingFile.FileName;
                    }

                    // 4. Insurance Attachment (.msg)
                    if (insuranceFile != null && insuranceFile.ContentLength > 0)
                    {
                        string fileName = "Insurance_" + DateTime.Now.Ticks + Path.GetExtension(insuranceFile.FileName);
                        shipment.InsuranceContent = ReadFileBytes(insuranceFile);
                        shipment.InsurancePath = "/Uploads/Imports/" + fileName;
                        shipment.InsuranceFileName = insuranceFile.FileName;
                    }

                    // Set created metadata
                    shipment.CreatedBy = SessionHelper.UserId;
                    shipment.Status = "Pending Clearance";
                    shipment.ClearanceDate = null;
                    shipment.ActualClearanceAmount = null;

                    _importRepository.AddImport(shipment);
                    TempData["SuccessMessage"] = "Import shipment successfully logged.";
                    return RedirectToAction("Index");
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", "Error logging shipment: " + ex.Message);
                }
            }

            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "New Import Shipment";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            return View(shipment);
        }

        [HttpGet]
        public ActionResult Edit(int id)
        {
            var shipment = _importRepository.GetImportById(id);
            if (shipment == null)
            {
                return HttpNotFound();
            }

            // A user cannot amend a record if it has already been cleared (only managers can change it)
            var userRole = SessionHelper.UserRole;
            if (shipment.Status == "Cleared" && userRole != "Manager" && userRole != "Admin" && userRole != "Management")
            {
                TempData["ErrorMessage"] = "Cleared shipments cannot be modified by standard users.";
                return RedirectToAction("Index");
            }

            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "Edit Import Shipment";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            return View(shipment);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(ImportShipment shipment, HttpPostedFileBase scanwellQtyFile, HttpPostedFileBase egxFile, HttpPostedFileBase transwingFile, HttpPostedFileBase insuranceFile)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var existing = _importRepository.GetImportById(shipment.ImportId);
                    if (existing == null)
                    {
                        return HttpNotFound();
                    }

                    var userRole = SessionHelper.UserRole;
                    if (existing.Status == "Cleared" && userRole != "Manager" && userRole != "Admin" && userRole != "Management")
                    {
                        TempData["ErrorMessage"] = "Cleared shipments cannot be modified by standard users.";
                        return RedirectToAction("Index");
                    }

                    if (existing.Status == "Pending Clearance")
                    {
                        shipment.ClearanceDate = null;
                        shipment.ActualClearanceAmount = null;
                        shipment.Status = "Pending Clearance";
                    }

                    // 1. Scanwell Attachment
                    if (scanwellQtyFile != null && scanwellQtyFile.ContentLength > 0)
                    {
                        string fileName = "Scanwell_" + DateTime.Now.Ticks + Path.GetExtension(scanwellQtyFile.FileName);
                        shipment.ScanwellQtyContent = ReadFileBytes(scanwellQtyFile);
                        shipment.ScanwellQtyPath = "/Uploads/Imports/" + fileName;
                        shipment.ScanwellQtyFileName = scanwellQtyFile.FileName;
                    }

                    // 2. EGX Attachment
                    if (egxFile != null && egxFile.ContentLength > 0)
                    {
                        string fileName = "EGX_" + DateTime.Now.Ticks + Path.GetExtension(egxFile.FileName);
                        shipment.FGXContent = ReadFileBytes(egxFile);
                        shipment.FGXPath = "/Uploads/Imports/" + fileName;
                        shipment.FGXFileName = egxFile.FileName;
                    }

                    // 3. Transwing Attachment
                    if (transwingFile != null && transwingFile.ContentLength > 0)
                    {
                        string fileName = "Transwing_" + DateTime.Now.Ticks + Path.GetExtension(transwingFile.FileName);
                        shipment.TranswingContent = ReadFileBytes(transwingFile);
                        shipment.TranswingPath = "/Uploads/Imports/" + fileName;
                        shipment.TranswingFileName = transwingFile.FileName;
                    }

                    // 4. Insurance Attachment (.msg)
                    if (insuranceFile != null && insuranceFile.ContentLength > 0)
                    {
                        string fileName = "Insurance_" + DateTime.Now.Ticks + Path.GetExtension(insuranceFile.FileName);
                        shipment.InsuranceContent = ReadFileBytes(insuranceFile);
                        shipment.InsurancePath = "/Uploads/Imports/" + fileName;
                        shipment.InsuranceFileName = insuranceFile.FileName;
                    }

                    _importRepository.UpdateImport(shipment);
                    TempData["SuccessMessage"] = "Import shipment details updated successfully.";
                    return RedirectToAction("Index");
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", "Error amending details: " + ex.Message);
                }
            }

            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "Edit Import Shipment";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            return View(shipment);
        }

        [HttpPost]
        public JsonResult ClearShipment(int id, DateTime clearanceDate, decimal actualClearanceAmount)
        {
            try
            {
                if (clearanceDate == DateTime.MinValue || actualClearanceAmount < 0)
                {
                    return Json(new { success = false, message = "Invalid clearance details provided." });
                }

                _importRepository.ClearShipment(id, clearanceDate, actualClearanceAmount);
                return Json(new { success = true, message = "Import shipment successfully cleared." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpGet]
        public ActionResult DownloadAttachment(int id, string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                TempData["ErrorMessage"] = "No attachment type specified.";
                return RedirectToAction("Index");
            }

            var shipment = _importRepository.GetImportById(id);
            if (shipment == null)
            {
                TempData["ErrorMessage"] = "The requested shipment record does not exist.";
                return RedirectToAction("Index");
            }

            byte[] fileContent = null;
            string fileName = null;
            string legacyPath = null;

            if (type.Equals("Scanwell", StringComparison.OrdinalIgnoreCase))
            {
                fileContent = shipment.ScanwellQtyContent;
                fileName = shipment.ScanwellQtyFileName;
                legacyPath = shipment.ScanwellQtyPath;
            }
            else if (type.Equals("EGX", StringComparison.OrdinalIgnoreCase))
            {
                fileContent = shipment.FGXContent;
                fileName = shipment.FGXFileName;
                legacyPath = shipment.FGXPath;
            }
            else if (type.Equals("Transwing", StringComparison.OrdinalIgnoreCase))
            {
                fileContent = shipment.TranswingContent;
                fileName = shipment.TranswingFileName;
                legacyPath = shipment.TranswingPath;
            }
            else if (type.Equals("Insurance", StringComparison.OrdinalIgnoreCase))
            {
                fileContent = shipment.InsuranceContent;
                fileName = shipment.InsuranceFileName;
                legacyPath = shipment.InsurancePath;
            }

            // Check if we have the file content in the database (BLOB)
            if (fileContent != null && fileContent.Length > 0)
            {
                string contentType = System.Web.MimeMapping.GetMimeMapping(fileName);
                if (fileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = "application/vnd.ms-outlook";
                }
                return File(fileContent, contentType, fileName);
            }

            // Fallback: If DB content is NULL, check the local disk (backwards compatibility)
            if (!string.IsNullOrEmpty(legacyPath))
            {
                string physicalPath = GetPhysicalPath(legacyPath);
                if (System.IO.File.Exists(physicalPath))
                {
                    string contentType = System.Web.MimeMapping.GetMimeMapping(physicalPath);
                    if (physicalPath.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = "application/vnd.ms-outlook";
                    }
                    string downloadName = !string.IsNullOrEmpty(fileName) ? fileName : Path.GetFileName(physicalPath);
                    return File(physicalPath, contentType, downloadName);
                }
            }

            TempData["ErrorMessage"] = "The requested attachment file could not be found.";
            if (Request.UrlReferrer != null)
            {
                return Redirect(Request.UrlReferrer.ToString());
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public JsonResult Delete(int id)
        {
            try
            {
                _importRepository.DeleteImport(id);
                return Json(new { success = true, message = "Shipment record successfully deleted." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private byte[] ReadFileBytes(HttpPostedFileBase file)
        {
            if (file == null || file.ContentLength == 0)
            {
                return null;
            }

            using (var reader = new BinaryReader(file.InputStream))
            {
                return reader.ReadBytes(file.ContentLength);
            }
        }

        private string GetPhysicalPath(string relativePath)
        {
            string normalizedPath = relativePath.Replace("\\", "/");
            if (normalizedPath.StartsWith("~/"))
            {
                normalizedPath = normalizedPath.Substring(1);
            }

            // Path in current running application directory (bin\app.publish\Uploads\Imports)
            string runningPath = Server.MapPath("~" + normalizedPath);
            if (System.IO.File.Exists(runningPath))
            {
                return runningPath;
            }

            // Fallback: if running from bin\app.publish, resolve the file from project root directory
            string appPath = HttpRuntime.AppDomainAppPath;
            if (appPath.IndexOf(@"\bin\app.publish", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string projectRoot = Path.GetFullPath(Path.Combine(appPath, "..", ".."));
                string fallbackPath = Path.GetFullPath(Path.Combine(projectRoot, normalizedPath.TrimStart('/')));
                if (System.IO.File.Exists(fallbackPath))
                {
                    return fallbackPath;
                }
            }

            return runningPath;
        }
    }
}
