// /Controllers/AccountController.cs
using System;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using RpacCommercial.Helpers;
using RpacCommercial.Models;
using RpacCommercial.Repositories;

namespace RpacCommercial.Controllers
{
    public class AccountController : Controller
    {
        private readonly IUserRepository _userRepository;

        public AccountController(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        [HttpGet]
        public ActionResult Login()
        {
            if (User.Identity.IsAuthenticated)
            {
                FormsAuthentication.SignOut();
                SessionHelper.Clear();
            }
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Login(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ViewBag.Error = "Please enter username and password.";
                return View();
            }

            // Plain text validation
            var user = _userRepository.ValidateUser(username, password);
            if (user != null)
            {
                FormsAuthentication.SetAuthCookie(user.Username, false);
                SessionHelper.UserId = user.UserId;
                SessionHelper.Username = user.Username;
                SessionHelper.FullName = user.FullName;
                SessionHelper.UserRole = user.RoleName;

                return RedirectToAction("Index", "Dashboard");
            }

            ViewBag.Error = "Invalid username or password.";
            return View();
        }

        [Authorize]
        public ActionResult Logout()
        {
            FormsAuthentication.SignOut();
            SessionHelper.Clear();
            return RedirectToAction("Login", "Account");
        }

    }
}
