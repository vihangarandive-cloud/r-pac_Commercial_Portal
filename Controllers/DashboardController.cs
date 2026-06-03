// /Controllers/DashboardController.cs
using System;
using System.Linq;
using System.Web.Mvc;
using RpacCommercial.Helpers;
using RpacCommercial.Repositories;

namespace RpacCommercial.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly IImportRepository _importRepository;

        public DashboardController(IImportRepository importRepository)
        {
            _importRepository = importRepository;
        }

        public ActionResult Index(string month = null)
        {
            ViewBag.ActiveModule = "Dashboard";
            ViewBag.Title = "Commercial Dashboard";

            // Parse month-year parameter
            DateTime targetDate = DateTime.Today;
            if (!string.IsNullOrEmpty(month))
            {
                DateTime parsedDate;
                if (DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsedDate))
                {
                    targetDate = parsedDate;
                }
            }
            int year = targetDate.Year;
            int monthVal = targetDate.Month;

            ViewBag.SelectedMonthStr = targetDate.ToString("yyyy-MM");
            ViewBag.SelectedMonthName = targetDate.ToString("MMMM yyyy");
            ViewBag.SelectedYear = year;
            ViewBag.SelectedMonth = monthVal;

            // Prev / Next Month Strings
            ViewBag.PrevMonth = targetDate.AddMonths(-1).ToString("yyyy-MM");
            ViewBag.NextMonth = targetDate.AddMonths(1).ToString("yyyy-MM");

            // Month list generation for historical selector dropdown (18 months back, 1 month ahead, clamped to 2025 onwards)
            var monthYearList = new System.Collections.Generic.List<SelectListItem>();
            var start = DateTime.Today.AddMonths(-18);
            if (start < new DateTime(2025, 1, 1))
            {
                start = new DateTime(2025, 1, 1);
            }
            var end = DateTime.Today.AddMonths(1);
            for (var dt = start; dt <= end; dt = dt.AddMonths(1))
            {
                monthYearList.Add(new SelectListItem
                {
                    Value = dt.ToString("yyyy-MM"),
                    Text = dt.ToString("MMMM yyyy"),
                    Selected = (dt.Year == year && dt.Month == monthVal)
                });
            }
            ViewBag.MonthYearList = monthYearList;

            // Load month-specific datasets for Cleared Shipments
            var clearedImports = _importRepository.GetClearedImportsByMonth(year, monthVal);
            ViewBag.ClearedImportsList = clearedImports;
            ViewBag.TotalClearedCount = clearedImports.Count();

            // Count summary widgets
            ViewBag.TotalCount = _importRepository.GetTotalCountByMonth(year, monthVal); // Logged count for selected month scope
            ViewBag.PendingClearanceCount = _importRepository.GetPendingClearanceCountByMonth(year, monthVal); // Pending actual clearance for selected month scope

            decimal monthlyClearance;
            decimal monthlyFreight;
            _importRepository.GetMonthlyFinancialsByMonth(year, monthVal, out monthlyClearance, out monthlyFreight);
            ViewBag.MonthlyClearance = monthlyClearance;
            ViewBag.MonthlyFreight = monthlyFreight;

            // Load charts dataset (scoped strictly to selected month's cleared shipments)
            ViewBag.ShippingModes = _importRepository.GetShippingModeBreakdownByMonth(year, monthVal);
            ViewBag.ShippingModeCosts = _importRepository.GetShippingModeCostBreakdownByMonth(year, monthVal);
            ViewBag.WeightUnits = _importRepository.GetWeightUnitBreakdownByMonth(year, monthVal);

            // Load recent cleared shipments in selected month
            ViewBag.RecentImports = clearedImports.Take(6);

            // Grouped Bar Chart data: Material Category (X) vs Actual Freight Cost (Y) for Current & Previous Month
            DateTime prevTargetDate = targetDate.AddMonths(-1);
            var prevImports = _importRepository.GetClearedImportsByMonth(prevTargetDate.Year, prevTargetDate.Month);

            var standardCategories = new[] { "RFID", "Paper Board", "Satin Ribbon", "Ink/Chemical/Ink Foil", "Adhesive", "Consumable", "Accessories" };
            
            var currentFreightByCategory = standardCategories.ToDictionary(cat => cat, cat => 0m);
            var prevFreightByCategory = standardCategories.ToDictionary(cat => cat, cat => 0m);

            foreach (var imp in clearedImports)
            {
                if (string.IsNullOrEmpty(imp.MaterialCategory)) continue;
                var cats = imp.MaterialCategory.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Select(c => c.Trim());
                decimal cost = imp.ActualFreightCost ?? 0m;
                foreach (var cat in cats)
                {
                    if (currentFreightByCategory.ContainsKey(cat))
                    {
                        currentFreightByCategory[cat] += cost;
                    }
                    else
                    {
                        currentFreightByCategory[cat] = cost;
                    }
                }
            }

            foreach (var imp in prevImports)
            {
                if (string.IsNullOrEmpty(imp.MaterialCategory)) continue;
                var cats = imp.MaterialCategory.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Select(c => c.Trim());
                decimal cost = imp.ActualFreightCost ?? 0m;
                foreach (var cat in cats)
                {
                    if (prevFreightByCategory.ContainsKey(cat))
                    {
                        prevFreightByCategory[cat] += cost;
                    }
                    else
                    {
                        prevFreightByCategory[cat] = cost;
                    }
                }
            }

            var allCategoriesList = currentFreightByCategory.Keys.Union(prevFreightByCategory.Keys).ToList();

            var mappedLabels = allCategoriesList.Select(cat => {
                if (cat.Equals("Paper Board", StringComparison.OrdinalIgnoreCase)) return "Paper Brd";
                if (cat.Equals("Satin Ribbon", StringComparison.OrdinalIgnoreCase)) return "Satin Rbn";
                if (cat.Equals("Ink/Chemical/Ink Foil", StringComparison.OrdinalIgnoreCase)) return "Ink/Chem";
                if (cat.Equals("Accessories", StringComparison.OrdinalIgnoreCase)) return "Accessory";
                return cat;
            }).ToList();

            ViewBag.GroupedChartLabels = mappedLabels;
            ViewBag.GroupedChartCurrentData = allCategoriesList.Select(cat => currentFreightByCategory.ContainsKey(cat) ? currentFreightByCategory[cat] : 0m).ToList();
            ViewBag.GroupedChartPrevData = allCategoriesList.Select(cat => prevFreightByCategory.ContainsKey(cat) ? prevFreightByCategory[cat] : 0m).ToList();
            ViewBag.GroupedChartCurrentLabel = "Current Month (" + targetDate.ToString("MMM yyyy") + ")";
            ViewBag.GroupedChartPrevLabel = "Previous Month (" + prevTargetDate.ToString("MMM yyyy") + ")";

            return View();
        }

        [HttpGet]
        public JsonResult GetNotifications()
        {
            // Simple dynamic alerts if there are items pending clearance
            var list = new System.Collections.Generic.List<object>();

            int pendingCount = _importRepository.GetPendingClearanceCount();
            if (pendingCount > 0)
            {
                list.Add(new
                {
                    AlertId = 1,
                    AlertType = "Pending Clearance",
                    Message = pendingCount + " import shipments are currently pending actual clearance."
                });
            }

            return Json(list, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public JsonResult MarkNotificationRead(int id)
        {
            // Notifications are state-less/derived, return success
            return Json(new { success = true });
        }
    }
}
