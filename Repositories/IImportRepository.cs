// /Repositories/IImportRepository.cs
using System;
using System.Collections.Generic;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public interface IImportRepository
    {
        IEnumerable<ImportShipment> GetAllImports(string status = null, DateTime? fromDate = null, DateTime? toDate = null, int? supplierId = null, string search = null);
        ImportShipment GetImportById(int importId);
        void AddImport(ImportShipment shipment);
        void UpdateImport(ImportShipment shipment);
        void UpdateStatus(int importId, string status, int approvedBy);
        void ClearShipment(int importId, DateTime clearanceDate, decimal actualClearanceAmount);
        void DeleteImport(int importId);
        
        // Summary Dashboard Metrics
        int GetTotalCount();
        int GetPendingCount();
        int GetApprovedCount();
        decimal GetTotalClearanceAmount();
        IEnumerable<dynamic> GetShippingModeBreakdown();
        IEnumerable<dynamic> GetWeightUnitBreakdown();
        IEnumerable<dynamic> GetRecentImports(int count = 5);
        
        // Yesterday's Cleared Imports for Morning Outlook Report
        IEnumerable<ImportShipment> GetYesterdaysClearedImports();

        // Current Month's Clearance and Freight costs (resets/refreshes monthly)
        void GetMonthlyFinancials(out decimal clearance, out decimal freight);

        // Month-filtered and pending clearance queries
        IEnumerable<ImportShipment> GetClearedImportsByMonth(int year, int month);
        void GetMonthlyFinancialsByMonth(int year, int month, out decimal clearance, out decimal freight);
        IEnumerable<dynamic> GetShippingModeBreakdownByMonth(int year, int month);
        IEnumerable<dynamic> GetWeightUnitBreakdownByMonth(int year, int month);
        IEnumerable<dynamic> GetShippingModeCostBreakdownByMonth(int year, int month);
        int GetPendingClearanceCount();
    }
}
