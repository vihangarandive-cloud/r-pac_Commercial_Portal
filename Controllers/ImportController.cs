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

        public ActionResult Index(string status = null, int? supplierId = null, string search = null)
        {
            ViewBag.ActiveModule = "Import";
            ViewBag.Title = "Import Management";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            ViewBag.SelectedStatus = status;
            ViewBag.SelectedSupplierId = supplierId;
            ViewBag.SearchText = search;

            var shipments = _importRepository.GetAllImports(status, null, null, supplierId, search);
            return View(shipments);
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
                    // Create Uploads directory if it doesn't exist
                    string uploadDir = Server.MapPath("~/Uploads/Imports/");
                    if (!Directory.Exists(uploadDir))
                    {
                        Directory.CreateDirectory(uploadDir);
                    }

                    // 1. Scanwell Attachment
                    if (scanwellQtyFile != null && scanwellQtyFile.ContentLength > 0)
                    {
                        string fileName = "Scanwell_" + DateTime.Now.Ticks + Path.GetExtension(scanwellQtyFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        scanwellQtyFile.SaveAs(fullPath);
                        shipment.ScanwellQtyPath = "/Uploads/Imports/" + fileName;
                        shipment.ScanwellQtyFileName = scanwellQtyFile.FileName;
                    }

                    // 2. EGX Attachment
                    if (egxFile != null && egxFile.ContentLength > 0)
                    {
                        string fileName = "EGX_" + DateTime.Now.Ticks + Path.GetExtension(egxFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        egxFile.SaveAs(fullPath);
                        shipment.FGXPath = "/Uploads/Imports/" + fileName;
                        shipment.FGXFileName = egxFile.FileName;
                    }

                    // 3. Transwing Attachment
                    if (transwingFile != null && transwingFile.ContentLength > 0)
                    {
                        string fileName = "Transwing_" + DateTime.Now.Ticks + Path.GetExtension(transwingFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        transwingFile.SaveAs(fullPath);
                        shipment.TranswingPath = "/Uploads/Imports/" + fileName;
                        shipment.TranswingFileName = transwingFile.FileName;
                    }

                    // 4. Insurance Attachment (.msg)
                    if (insuranceFile != null && insuranceFile.ContentLength > 0)
                    {
                        string fileName = "Insurance_" + DateTime.Now.Ticks + Path.GetExtension(insuranceFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        insuranceFile.SaveAs(fullPath);
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

                    string uploadDir = Server.MapPath("~/Uploads/Imports/");
                    if (!Directory.Exists(uploadDir))
                    {
                        Directory.CreateDirectory(uploadDir);
                    }

                    // 1. Scanwell Attachment
                    if (scanwellQtyFile != null && scanwellQtyFile.ContentLength > 0)
                    {
                        string fileName = "Scanwell_" + DateTime.Now.Ticks + Path.GetExtension(scanwellQtyFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        scanwellQtyFile.SaveAs(fullPath);
                        shipment.ScanwellQtyPath = "/Uploads/Imports/" + fileName;
                        shipment.ScanwellQtyFileName = scanwellQtyFile.FileName;
                    }

                    // 2. EGX Attachment
                    if (egxFile != null && egxFile.ContentLength > 0)
                    {
                        string fileName = "EGX_" + DateTime.Now.Ticks + Path.GetExtension(egxFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        egxFile.SaveAs(fullPath);
                        shipment.FGXPath = "/Uploads/Imports/" + fileName;
                        shipment.FGXFileName = egxFile.FileName;
                    }

                    // 3. Transwing Attachment
                    if (transwingFile != null && transwingFile.ContentLength > 0)
                    {
                        string fileName = "Transwing_" + DateTime.Now.Ticks + Path.GetExtension(transwingFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        transwingFile.SaveAs(fullPath);
                        shipment.TranswingPath = "/Uploads/Imports/" + fileName;
                        shipment.TranswingFileName = transwingFile.FileName;
                    }

                    // 4. Insurance Attachment (.msg)
                    if (insuranceFile != null && insuranceFile.ContentLength > 0)
                    {
                        string fileName = "Insurance_" + DateTime.Now.Ticks + Path.GetExtension(insuranceFile.FileName);
                        string fullPath = Path.Combine(uploadDir, fileName);
                        insuranceFile.SaveAs(fullPath);
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
    }
}
