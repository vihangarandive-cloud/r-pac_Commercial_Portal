// /Repositories/ImportRepository.cs
using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using RpacCommercial.Helpers;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public class ImportRepository : IImportRepository
    {
        public IEnumerable<ImportShipment> GetAllImports(string status = null, DateTime? fromDate = null, DateTime? toDate = null, int? supplierId = null, string search = null)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT i.*, s.SupplierName, 
                               u1.FullName as CreatedByFullName, 
                               u2.FullName as ApprovedByFullName
                               FROM Imports i WITH (NOLOCK)
                               INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                               INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                               LEFT JOIN Users u2 WITH (NOLOCK) ON i.ApprovedBy = u2.UserId
                               WHERE 1=1";

                var p = new DynamicParameters();

                if (!string.IsNullOrEmpty(status))
                {
                    sql += " AND i.Status = @Status";
                    p.Add("Status", status);
                }

                if (fromDate.HasValue)
                {
                    sql += " AND i.ShipmentDate >= @FromDate";
                    p.Add("FromDate", fromDate.Value.Date);
                }

                if (toDate.HasValue)
                {
                    sql += " AND i.ShipmentDate < @ToDate";
                    p.Add("ToDate", toDate.Value.Date.AddDays(1));
                }

                if (supplierId.HasValue)
                {
                    sql += " AND i.SupplierId = @SupplierId";
                    p.Add("SupplierId", supplierId.Value);
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    sql += " AND (i.PONumber LIKE @Search OR s.SupplierName LIKE @Search OR i.Remarks LIKE @Search OR i.MaterialCategory LIKE @Search)";
                    p.Add("Search", "%" + search.Trim() + "%");
                }

                sql += " ORDER BY i.CreatedAt DESC";

                return conn.Query<ImportShipment>(sql, p);
            }
        }

        public ImportShipment GetImportById(int importId)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT i.*, s.SupplierName, 
                               u1.FullName as CreatedByFullName, 
                               u2.FullName as ApprovedByFullName
                               FROM Imports i WITH (NOLOCK)
                               INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                               INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                               LEFT JOIN Users u2 WITH (NOLOCK) ON i.ApprovedBy = u2.UserId
                               WHERE i.ImportId = @ImportId";
                return conn.QueryFirstOrDefault<ImportShipment>(sql, new { ImportId = importId });
            }
        }

        public void AddImport(ImportShipment s)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"INSERT INTO Imports (
                                ShipmentDate, PONumber, SupplierId, ShippingMode, Incoterm, 
                                WeightValue, WeightUnit, MaterialCategory, ScanwellQtyPath, ScanwellQtyFileName, 
                                FGXPath, FGXFileName, TranswingPath, TranswingFileName, 
                                InsurancePath, InsuranceFileName,
                                ETD, ETA, ActualFreightCost, ActualFreightCostForwarder, ApproximateFreightCost, ClearanceDate, 
                                ApproximateClearanceAmount, ActualClearanceAmount, Remarks, 
                                Status, CreatedBy, CreatedAt
                               ) VALUES (
                                @ShipmentDate, @PONumber, @SupplierId, @ShippingMode, @Incoterm, 
                                @WeightValue, @WeightUnit, @MaterialCategory, @ScanwellQtyPath, @ScanwellQtyFileName, 
                                @FGXPath, @FGXFileName, @TranswingPath, @TranswingFileName, 
                                @InsurancePath, @InsuranceFileName,
                                @ETD, @ETA, @ActualFreightCost, @ActualFreightCostForwarder, @ApproximateFreightCost, @ClearanceDate, 
                                @ApproximateClearanceAmount, @ActualClearanceAmount, @Remarks, 
                                CASE WHEN @ClearanceDate IS NOT NULL THEN 'Cleared' ELSE 'Pending Clearance' END, @CreatedBy, GETDATE()
                               )
                            ";
                conn.Execute(sql, s);
            }
        }

        public void UpdateImport(ImportShipment s)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"UPDATE Imports SET 
                                ShipmentDate = @ShipmentDate, 
                                PONumber = @PONumber, 
                                SupplierId = @SupplierId, 
                                ShippingMode = @ShippingMode, 
                                Incoterm = @Incoterm, 
                                WeightValue = @WeightValue, 
                                WeightUnit = @WeightUnit,
                                MaterialCategory = @MaterialCategory,
                                ScanwellQtyPath = ISNULL(@ScanwellQtyPath, ScanwellQtyPath),
                                ScanwellQtyFileName = ISNULL(@ScanwellQtyFileName, ScanwellQtyFileName),
                                FGXPath = ISNULL(@FGXPath, FGXPath),
                                FGXFileName = ISNULL(@FGXFileName, FGXFileName),
                                TranswingPath = ISNULL(@TranswingPath, TranswingPath),
                                TranswingFileName = ISNULL(@TranswingFileName, TranswingFileName),
                                InsurancePath = ISNULL(@InsurancePath, InsurancePath),
                                InsuranceFileName = ISNULL(@InsuranceFileName, InsuranceFileName),
                                ETD = @ETD, 
                                ETA = @ETA, 
                                ActualFreightCost = @ActualFreightCost, 
                                ActualFreightCostForwarder = @ActualFreightCostForwarder,
                                ApproximateFreightCost = @ApproximateFreightCost,
                                ClearanceDate = @ClearanceDate, 
                                ApproximateClearanceAmount = @ApproximateClearanceAmount, 
                                ActualClearanceAmount = @ActualClearanceAmount, 
                                Remarks = @Remarks,
                                Status = CASE WHEN @ClearanceDate IS NOT NULL THEN 'Cleared' ELSE 'Pending Clearance' END
                               WHERE ImportId = @ImportId";
                conn.Execute(sql, s);
            }
        }

        public void UpdateStatus(int importId, string status, int approvedBy)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"UPDATE Imports 
                               SET Status = @Status, ApprovedBy = @ApprovedBy, ApprovedAt = GETDATE() 
                               WHERE ImportId = @ImportId";
                conn.Execute(sql, new { Status = status, ApprovedBy = approvedBy, ImportId = importId });
            }
        }

        public void ClearShipment(int importId, DateTime clearanceDate, decimal actualClearanceAmount)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"UPDATE Imports 
                               SET ClearanceDate = @ClearanceDate, 
                                   ActualClearanceAmount = @ActualClearanceAmount, 
                                   Status = 'Cleared'
                               WHERE ImportId = @ImportId";
                conn.Execute(sql, new { ClearanceDate = clearanceDate, ActualClearanceAmount = actualClearanceAmount, ImportId = importId });
            }
        }

        public void DeleteImport(int importId)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "DELETE FROM Imports WHERE ImportId = @ImportId";
                conn.Execute(sql, new { ImportId = importId });
            }
        }

        // Summary Statistics for Dashboard
        public int GetTotalCount()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Imports WITH (NOLOCK)");
            }
        }

        public int GetPendingCount()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Imports WITH (NOLOCK) WHERE Status = 'Pending Clearance'");
            }
        }

        public int GetApprovedCount()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Imports WITH (NOLOCK) WHERE Status = 'Cleared'");
            }
        }

        public decimal GetTotalClearanceAmount()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                return conn.ExecuteScalar<decimal>("SELECT ISNULL(SUM(ActualClearanceAmount), 0) FROM Imports WITH (NOLOCK) WHERE Status = 'Cleared'");
            }
        }

        public IEnumerable<dynamic> GetShippingModeBreakdown()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT ShippingMode as Label, COUNT(*) as Value 
                               FROM Imports WITH (NOLOCK) 
                               GROUP BY ShippingMode";
                return conn.Query(sql);
            }
        }

        public IEnumerable<dynamic> GetWeightUnitBreakdown()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT WeightUnit as Label, SUM(WeightValue) as Value 
                               FROM Imports WITH (NOLOCK) 
                               GROUP BY WeightUnit";
                return conn.Query(sql);
            }
        }

        public IEnumerable<dynamic> GetRecentImports(int count = 5)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT TOP (@Count) i.*, s.SupplierName 
                               FROM Imports i WITH (NOLOCK)
                               INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                               ORDER BY i.CreatedAt DESC";
                return conn.Query(sql, new { Count = count });
            }
        }

        // Morning Email Report Query (Cleared Shipments Yesterday)
        public IEnumerable<ImportShipment> GetYesterdaysClearedImports()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT i.*, s.SupplierName, 
                               u1.FullName as CreatedByFullName
                               FROM Imports i WITH (NOLOCK)
                               INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                               INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                               WHERE i.Status = 'Cleared' 
                               AND i.ClearanceDate >= CAST(DATEADD(day, -1, GETDATE()) AS DATE)
                               AND i.ClearanceDate < CAST(GETDATE() AS DATE)";
                return conn.Query<ImportShipment>(sql);
            }
        }

        // Current Month's Clearance (LKR) and Freight (USD) (resets/refreshes monthly)
        public void GetMonthlyFinancials(out decimal clearance, out decimal freight)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT 
                                 ISNULL(SUM(ActualClearanceAmount), 0) AS ClearanceSum,
                                 ISNULL(SUM(ActualFreightCost), 0) AS FreightSum
                               FROM Imports WITH (NOLOCK)
                               WHERE Status = 'Approved'
                               AND ApprovedAt >= DATEADD(month, DATEDIFF(month, 0, GETDATE()), 0)
                               AND ApprovedAt < DATEADD(month, DATEDIFF(month, 0, GETDATE()) + 1, 0)";
                
                var row = conn.QueryFirstOrDefault(sql);
                if (row != null)
                {
                    clearance = row.ClearanceSum != null ? (decimal)row.ClearanceSum : 0m;
                    freight = row.FreightSum != null ? (decimal)row.FreightSum : 0m;
                }
                else
                {
                    clearance = 0m;
                    freight = 0m;
                }
            }
        }

        public IEnumerable<ImportShipment> GetClearedImportsByMonth(int year, int month)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT i.*, s.SupplierName, 
                               u1.FullName as CreatedByFullName, 
                               u2.FullName as ApprovedByFullName
                               FROM Imports i WITH (NOLOCK)
                               INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                               INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                               LEFT JOIN Users u2 WITH (NOLOCK) ON i.ApprovedBy = u2.UserId
                               WHERE YEAR(i.ClearanceDate) = @Year AND MONTH(i.ClearanceDate) = @Month
                               ORDER BY i.ClearanceDate DESC";
                return conn.Query<ImportShipment>(sql, new { Year = year, Month = month });
            }
        }

        public void GetMonthlyFinancialsByMonth(int year, int month, out decimal clearance, out decimal freight)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT 
                                 ISNULL(SUM(ActualClearanceAmount), 0) AS ClearanceSum,
                                 ISNULL(SUM(ActualFreightCost), 0) AS FreightSum
                               FROM Imports WITH (NOLOCK)
                               WHERE YEAR(ClearanceDate) = @Year AND MONTH(ClearanceDate) = @Month";
                
                var row = conn.QueryFirstOrDefault(sql, new { Year = year, Month = month });
                if (row != null)
                {
                    clearance = row.ClearanceSum != null ? (decimal)row.ClearanceSum : 0m;
                    freight = row.FreightSum != null ? (decimal)row.FreightSum : 0m;
                }
                else
                {
                    clearance = 0m;
                    freight = 0m;
                }
            }
        }

        public IEnumerable<dynamic> GetShippingModeBreakdownByMonth(int year, int month)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT ShippingMode as Label, COUNT(*) as Value 
                               FROM Imports WITH (NOLOCK) 
                               WHERE YEAR(ClearanceDate) = @Year AND MONTH(ClearanceDate) = @Month
                               GROUP BY ShippingMode";
                return conn.Query(sql, new { Year = year, Month = month });
            }
        }

        public IEnumerable<dynamic> GetWeightUnitBreakdownByMonth(int year, int month)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT WeightUnit as Label, SUM(WeightValue) as Value 
                               FROM Imports WITH (NOLOCK) 
                               WHERE YEAR(ClearanceDate) = @Year AND MONTH(ClearanceDate) = @Month
                               GROUP BY WeightUnit";
                return conn.Query(sql, new { Year = year, Month = month });
            }
        }

        public IEnumerable<dynamic> GetShippingModeCostBreakdownByMonth(int year, int month)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT ShippingMode as Label, SUM(ISNULL(ActualFreightCost, 0)) as Value 
                               FROM Imports WITH (NOLOCK) 
                               WHERE YEAR(ClearanceDate) = @Year AND MONTH(ClearanceDate) = @Month
                               GROUP BY ShippingMode";
                return conn.Query(sql, new { Year = year, Month = month });
            }
        }

        public int GetPendingClearanceCount()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "SELECT COUNT(*) FROM Imports WITH (NOLOCK) WHERE ClearanceDate IS NULL";
                return conn.ExecuteScalar<int>(sql);
            }
        }
    }
}
