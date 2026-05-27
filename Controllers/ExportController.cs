// /Controllers/ExportController.cs
using System;
using System.Web.Mvc;

namespace RpacCommercial.Controllers
{
    [Authorize]
    public class ExportController : Controller
    {
        public ActionResult Index()
        {
            // Blocked from access - returns a completely blank response showing nothing
            return Content("");
        }
    }
}
