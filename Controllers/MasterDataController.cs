// /Controllers/MasterDataController.cs
using System;
using System.Web.Mvc;
using RpacCommercial.Helpers;
using RpacCommercial.Models;
using RpacCommercial.Repositories;

namespace RpacCommercial.Controllers
{
    [RoleAuthorize("Admin", "Management", "Manager", "Commercial User")]
    public class MasterDataController : Controller
    {
        private readonly ISupplierRepository _supplierRepository;
        private readonly IUserRepository _userRepository;

        public MasterDataController(ISupplierRepository supplierRepository, IUserRepository userRepository)
        {
            _supplierRepository = supplierRepository;
            _userRepository = userRepository;
        }

        public ActionResult Index()
        {
            ViewBag.ActiveModule = "MasterData";
            ViewBag.Title = "Master Data Management";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers();
            
            if (SessionHelper.UserRole == "Admin" || SessionHelper.UserRole == "Management" || SessionHelper.UserRole == "Manager")
            {
                ViewBag.Users = _userRepository.GetAllUsers();
            }
            else
            {
                ViewBag.Users = new System.Collections.Generic.List<UserAccount>();
            }
            
            return View();
        }

        // Supplier CRUD
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult AddSupplier(Supplier s)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    _supplierRepository.AddSupplier(s);
                    TempData["SuccessMessage"] = "Supplier added successfully.";
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Error: " + ex.Message;
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EditSupplier(Supplier s)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    _supplierRepository.UpdateSupplier(s);
                    TempData["SuccessMessage"] = "Supplier details updated successfully.";
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Error: " + ex.Message;
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public JsonResult ToggleSupplier(int id)
        {
            try
            {
                _supplierRepository.ToggleStatus(id);
                return Json(new { success = true, message = "Supplier status toggled." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // User CRUD (Plain Text passwords)
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RoleAuthorize("Admin", "Management", "Manager")]
        public ActionResult AddUser(UserAccount u)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    // Plain text password saved
                    _userRepository.AddUser(u);
                    TempData["SuccessMessage"] = "User account created successfully.";
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Error: " + ex.Message;
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RoleAuthorize("Admin", "Management", "Manager")]
        public ActionResult EditUser(UserAccount u)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    // Update user details (using plain text password amendable in DB)
                    _userRepository.UpdateUser(u);
                    TempData["SuccessMessage"] = "User details updated successfully.";
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Error: " + ex.Message;
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [RoleAuthorize("Admin", "Management", "Manager")]
        public JsonResult ToggleUser(string username)
        {
            try
            {
                _userRepository.ToggleStatus(username);
                return Json(new { success = true, message = "User status toggled." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
