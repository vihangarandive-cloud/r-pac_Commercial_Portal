// /Controllers/ReportsController.cs
using System;
using System.Text;
using System.Web.Mvc;
using RpacCommercial.Models;
using RpacCommercial.Repositories;

namespace RpacCommercial.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly IImportRepository _importRepository;
        private readonly ISupplierRepository _supplierRepository;

        public ReportsController(IImportRepository importRepository, ISupplierRepository supplierRepository)
        {
            _importRepository = importRepository;
            _supplierRepository = supplierRepository;
        }

        public ActionResult Index(string status = null, int? supplierId = null, string fromDate = null, string toDate = null, string tab = "cleared", string search = null)
        {
            ViewBag.ActiveModule = "Reports";
            ViewBag.Title = "Commercial Analytics & Reports";
            ViewBag.Suppliers = _supplierRepository.GetAllSuppliers(true);
            ViewBag.ActiveTab = tab;

            // Parse dates
            DateTime? parsedFrom = null;
            DateTime? parsedTo = null;

            if (!string.IsNullOrEmpty(fromDate))
            {
                DateTime df;
                if (DateTime.TryParse(fromDate, out df)) parsedFrom = df;
            }
            if (!string.IsNullOrEmpty(toDate))
            {
                DateTime dt;
                if (DateTime.TryParse(toDate, out dt)) parsedTo = dt;
            }

            ViewBag.SelectedStatus = status;
            ViewBag.SelectedSupplierId = supplierId;
            ViewBag.SelectedFromDate = fromDate;
            ViewBag.SelectedToDate = toDate;
            ViewBag.SearchText = search;

            // Load raw data
            var rawData = _importRepository.GetAllImports(status, parsedFrom, parsedTo, supplierId, search);

            // Separate data based on selected sub-tab
            System.Collections.Generic.IEnumerable<ImportShipment> data;
            if (tab == "pending")
            {
                data = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(rawData, x => !x.ClearanceDate.HasValue));
            }
            else
            {
                data = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(rawData, x => x.ClearanceDate.HasValue));
            }

            // Load aggregate charts based strictly on the selected tab's dataset
            var modeChartList = new System.Collections.Generic.List<dynamic>();
            foreach (var g in System.Linq.Enumerable.GroupBy(data, x => x.ShippingMode))
            {
                var d = new System.Dynamic.ExpandoObject() as System.Collections.Generic.IDictionary<string, object>;
                d["Label"] = g.Key;
                d["Value"] = System.Linq.Enumerable.Count(g);
                modeChartList.Add((dynamic)d);
            }
            ViewBag.ShippingModeChart = modeChartList;

            var costChartList = new System.Collections.Generic.List<dynamic>();
            foreach (var g in System.Linq.Enumerable.GroupBy(data, x => x.ShippingMode))
            {
                var d = new System.Dynamic.ExpandoObject() as System.Collections.Generic.IDictionary<string, object>;
                d["Label"] = g.Key;
                d["Value"] = System.Linq.Enumerable.Sum(g, x => x.ActualFreightCost ?? 0);
                costChartList.Add((dynamic)d);
            }
            ViewBag.ShippingModeCostChart = costChartList;

            return View(data);
        }

        public ActionResult ExportCsv(string status = null, int? supplierId = null, string fromDate = null, string toDate = null, string tab = "cleared", string search = null)
        {
            DateTime? parsedFrom = null;
            DateTime? parsedTo = null;

            if (!string.IsNullOrEmpty(fromDate))
            {
                DateTime df;
                if (DateTime.TryParse(fromDate, out df))
                {
                    parsedFrom = df;
                }
            }
            if (!string.IsNullOrEmpty(toDate))
            {
                DateTime dt;
                if (DateTime.TryParse(toDate, out dt))
                {
                    parsedTo = dt;
                }
            }

            var list = _importRepository.GetAllImports(status, parsedFrom, parsedTo, supplierId, search);

            // Filter list based on selected tab
            if (tab == "pending")
            {
                list = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(list, x => !x.ClearanceDate.HasValue));
            }
            else
            {
                list = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(list, x => x.ClearanceDate.HasValue));
            }

            var sb = new StringBuilder();
            sb.AppendLine("Shipment Requested Date,PO Number,Supplier,Material Category,Shipping Mode,Incoterm,Weight Value,Weight Unit,ETD,ETA,Actual Freight Cost (USD),Freight Forwarder,Clearance Date,Actual Clearance Amt (LKR),Remarks,Status,Created By,Created At");

            foreach (var item in list)
            {
                sb.AppendLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17}",
                    item.ShipmentDate.ToString("yyyy-MM-dd"),
                    EscapeCsv(item.PONumber),
                    EscapeCsv(item.SupplierName),
                    EscapeCsv(item.MaterialCategory),
                    EscapeCsv(item.ShippingMode),
                    EscapeCsv(item.Incoterm),
                    item.WeightValue.ToString("F2"),
                    item.WeightUnit,
                    item.ETD.ToString("yyyy-MM-dd"),
                    item.ETA.ToString("yyyy-MM-dd"),
                    item.ActualFreightCost.HasValue ? item.ActualFreightCost.Value.ToString("F2") : "",
                    EscapeCsv(item.ActualFreightCostForwarder ?? "None"),
                    item.ClearanceDate.HasValue ? item.ClearanceDate.Value.ToString("yyyy-MM-dd") : "",
                    item.ActualClearanceAmount.HasValue ? item.ActualClearanceAmount.Value.ToString("F2") : "",
                    EscapeCsv(item.Remarks),
                    item.Status,
                    EscapeCsv(item.CreatedByFullName),
                    item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                ));
            }

            string fileName = "ImportReport_" + tab + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
        }

        private string EscapeCsv(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            if (str.Contains(",") || str.Contains("\"") || str.Contains("\r") || str.Contains("\n"))
            {
                return "\"" + str.Replace("\"", "\"\"") + "\"";
            }
            return str;
        }
    }
}
