using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using MimeKit;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace Company.DailyReporter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("          r-pac Commercial Daily Report System            ");
            Console.WriteLine("==========================================================");
            Console.WriteLine($"Execution Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            int exitCode = 0;
            string? tempExcelPath = null;
            string? tempMonthlyExcelPath = null;

            try
            {
                // 1. Load Configurations
                Console.WriteLine("Loading configuration settings...");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                var configuration = builder.Build();

                string? dbConnectionString = configuration.GetConnectionString("MSSQLServer");
                if (string.IsNullOrEmpty(dbConnectionString))
                {
                    throw new InvalidOperationException("Database Connection String 'MSSQLServer' is missing in configurations.");
                }

                // Retrieve SMTP configs
                string smtpServer = configuration["SmtpSettings:Server"] ?? "smtp.office365.com";
                int smtpPort = int.Parse(configuration["SmtpSettings:Port"] ?? "587");
                string smtpUser = configuration["SmtpSettings:Username"] ?? "";
                string smtpPassword = configuration["SmtpSettings:Password"] ?? "";
                string smtpSenderEmail = configuration["SmtpSettings:SenderEmail"] ?? smtpUser;
                string smtpSenderName = configuration["SmtpSettings:SenderName"] ?? "r-pac Commercial Dept";

                // Retrieve report output temp path
                string tempDirectory = configuration["ReportSettings:TempDirectory"] ?? @"C:\CompanyReports\Temp\";

                // 2. Initialize and Seed Database Tables
                Console.WriteLine("Checking and verifying database structure...");
                SetupRecipientsTable(dbConnectionString);

                // 3. Calculate absolute Date Range for "Yesterday"
                // Start timestamp: Today at 00:00:00 minus 1 day.
                // End timestamp: Today at 23:59:59 minus 1 day.
                DateTime yesterday = DateTime.Today.AddDays(-1);
                DateTime startTimestamp = yesterday; // 00:00:00 of yesterday
                DateTime endTimestamp = DateTime.Today; // 00:00:00 of today (strictly exclusive)

                Console.WriteLine($"Calculated Reporting Date: {yesterday:yyyy-MM-dd}");
                Console.WriteLine($"Date Range: [{startTimestamp:yyyy-MM-dd HH:mm:ss}] to [{endTimestamp:yyyy-MM-dd HH:mm:ss}] (exclusive)");

                // 4. Retrieve Cleared Shipments falling strictly in Yesterday's Date Range
                Console.WriteLine("Retrieving cleared shipments from database...");
                List<ImportShipment> clearedShipments = GetClearedShipments(dbConnectionString, startTimestamp, endTimestamp);
                Console.WriteLine($"Successfully loaded {clearedShipments.Count} shipment records cleared yesterday.");

                // 5. Generate Excel Spreadsheet (EPPlus)
                Console.WriteLine("Generating beautiful Excel spreadsheet report...");
                byte[] excelBytes = GenerateExcelReport(clearedShipments, yesterday);

                // 5.2 Retrieve Month's Shipments (Total calendar month)
                Console.WriteLine("Retrieving monthly shipments from database...");
                List<ImportShipment> monthlyShipments = GetMonthlyShipments(dbConnectionString, yesterday);
                Console.WriteLine($"Successfully loaded {monthlyShipments.Count} shipment records for the current month.");

                List<ImportShipment> prevMonthlyShipments = GetMonthlyShipments(dbConnectionString, yesterday.AddMonths(-1));
                Console.WriteLine($"Successfully loaded {prevMonthlyShipments.Count} shipment records for the previous month.");

                // Load exact cleared shipments lists by ClearanceDate for the dashboard-aligned chart
                Console.WriteLine("Retrieving monthly cleared shipments by ClearanceDate for the dashboard chart...");
                List<ImportShipment> chartCurrentCleared = GetClearedImportsByMonth(dbConnectionString, yesterday.Year, yesterday.Month);
                List<ImportShipment> chartPrevCleared = GetClearedImportsByMonth(dbConnectionString, yesterday.AddMonths(-1).Year, yesterday.AddMonths(-1).Month);
                Console.WriteLine($"Chart data loaded: Current Month Cleared={chartCurrentCleared.Count}, Previous Month Cleared={chartPrevCleared.Count}");

                // 5.4 Generate Monthly Excel Spreadsheet (EPPlus)
                Console.WriteLine("Generating beautiful Monthly Excel spreadsheet ledger...");
                byte[] monthlyExcelBytes = GenerateMonthlyExcelReport(monthlyShipments, yesterday);

                // Ensure temporary output directory exists
                if (!Directory.Exists(tempDirectory))
                {
                    Console.WriteLine($"Creating temporary report directory: {tempDirectory}");
                    Directory.CreateDirectory(tempDirectory);
                }

                string fileName = $"DailyReport_{yesterday:yyyyMMdd}.xlsx";
                tempExcelPath = Path.Combine(tempDirectory, fileName);
                Console.WriteLine($"Saving temporary Excel report to: {tempExcelPath}");
                File.WriteAllBytes(tempExcelPath, excelBytes);

                string monthlyFileName = $"MonthlyReport_{yesterday:yyyyMM}.xlsx";
                tempMonthlyExcelPath = Path.Combine(tempDirectory, monthlyFileName);
                Console.WriteLine($"Saving temporary Monthly Excel ledger to: {tempMonthlyExcelPath}");
                File.WriteAllBytes(tempMonthlyExcelPath, monthlyExcelBytes);

                // 6. Fetch Active Report Recipients from Database Config
                Console.WriteLine("Retrieving email recipients...");
                List<ReportRecipient> activeRecipients = GetActiveRecipients(dbConnectionString);
                if (activeRecipients.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Warning: No active recipients were found in the database. Adding default recipient as backup.");
                    Console.ResetColor();
                    activeRecipients.Add(new ReportRecipient 
                    { 
                        EmailAddress = "commercial-management@rpac.com", 
                        RecipientType = "To",
                        IsActive = true 
                    });
                }

                Console.WriteLine($"Recipients to dispatch: {string.Join(", ", activeRecipients.Select(r => r.EmailAddress))}");

                // 6.5 Retrieve Monthly Summary Statistics
                Console.WriteLine("Retrieving monthly summary statistics...");
                MonthlyStats monthlyStats = GetMonthlyStats(dbConnectionString, yesterday);
                Console.WriteLine($"Monthly Stats loaded: Total={monthlyStats.TotalCount}, Cleared={monthlyStats.ClearedCount}, Pending={monthlyStats.PendingCount}");

                // 7. Compose and Dispatch Email via SMTP
                Console.WriteLine($"Connecting to SMTP server {smtpServer}:{smtpPort} via Secure StartTls...");
                SendEmailReport(smtpServer, smtpPort, smtpUser, smtpPassword, smtpSenderEmail, smtpSenderName, activeRecipients, clearedShipments, yesterday, tempExcelPath, tempMonthlyExcelPath, monthlyStats, chartCurrentCleared, chartPrevCleared);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Report successfully generated and emailed!");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Execution Failed!");
                Console.WriteLine($"Error Details: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
                exitCode = 1;
            }
            finally
            {
                // 8. Safely Clean Up local temporary files to save disk space
                string[] filesToDelete = { tempExcelPath, tempMonthlyExcelPath };
                foreach (var filePath in filesToDelete)
                {
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        try
                        {
                            Console.WriteLine($"Cleaning up: Deleting temporary Excel file: {filePath}");
                            File.Delete(filePath);
                            Console.WriteLine("Cleanup completed successfully.");
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"Warning: Failed to clean up file {filePath}. Error: {ex.Message}");
                            Console.ResetColor();
                        }
                    }
                }
                Console.WriteLine("Daily reporting process finished.");
                Console.WriteLine("==========================================================");
            }

            if (exitCode != 0)
            {
                Environment.Exit(exitCode);
            }
        }

        private static void SetupRecipientsTable(string connectionString)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // Create ReportRecipients Table if not exists, or add RecipientType column if missing
                string tableCreateSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ReportRecipients]') AND type in (N'U'))
                    BEGIN
                        CREATE TABLE [dbo].[ReportRecipients](
                            [RecipientId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [EmailAddress] [varchar](100) NOT NULL UNIQUE,
                            [FullName] [varchar](100) NULL,
                            [RecipientType] [varchar](10) NOT NULL DEFAULT 'To',
                            [IsActive] [bit] NOT NULL DEFAULT 1,
                            [CreatedAt] [datetime] NOT NULL DEFAULT GETDATE()
                        );
                    END
                    ELSE
                    BEGIN
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[ReportRecipients]') AND name = 'RecipientType')
                        BEGIN
                            ALTER TABLE [dbo].[ReportRecipients] ADD [RecipientType] [varchar](10) NOT NULL DEFAULT 'To';
                        END
                    END";

                using (var cmd = new SqlCommand(tableCreateSql, conn))
                {
                    cmd.ExecuteNonQuery();
                }

                // Seed default recipients if table is empty
                int count = 0;
                using (var cmd = new SqlCommand("SELECT COUNT(*) FROM [ReportRecipients]", conn))
                {
                    count = (int)cmd.ExecuteScalar();
                }

                if (count == 0)
                {
                    Console.WriteLine("Seeding default recipients in ReportRecipients...");
                    string seedSql = @"
                        INSERT INTO [dbo].[ReportRecipients] ([FullName], [EmailAddress], [RecipientType], [IsActive])
                        VALUES ('Commercial Management', 'commercial-management@rpac.com', 'To', 1),
                               ('Commercial Copy', 'commercial-cc@rpac.com', 'Cc', 1)";
                    using (var cmd = new SqlCommand(seedSql, conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static List<ImportShipment> GetClearedShipments(string connectionString, DateTime start, DateTime end)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string sql = @"
                    SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                    FROM Imports i WITH (NOLOCK)
                    INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                    INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                    WHERE i.Status = 'Cleared' 
                      AND i.ClearanceDate >= @StartTimestamp
                      AND i.ClearanceDate < @EndTimestamp
                    ORDER BY i.ClearanceDate DESC";

                return conn.Query<ImportShipment>(sql, new { StartTimestamp = start, EndTimestamp = end }).ToList();
            }
        }

        private static List<ReportRecipient> GetActiveRecipients(string connectionString)
        {
            var activeRecipients = new List<ReportRecipient>();
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand("SELECT EmailAddress, RecipientType FROM ReportRecipients WHERE IsActive = 1", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            activeRecipients.Add(new ReportRecipient
                            {
                                EmailAddress = reader.GetString(0),
                                RecipientType = reader.GetString(1),
                                IsActive = true
                            });
                        }
                    }
                }
            }
            return activeRecipients;
        }

        private static List<ImportShipment> GetMonthlyShipments(string connectionString, DateTime monthDate)
        {
            var monthStart = new DateTime(monthDate.Year, monthDate.Month, 1);
            var monthEnd = monthStart.AddMonths(1);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string sql = @"
                    SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                    FROM Imports i WITH (NOLOCK)
                    INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                    INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                    WHERE i.ShipmentDate >= @MonthStart AND i.ShipmentDate < @MonthEnd
                    ORDER BY i.ShipmentDate ASC";

                return conn.Query<ImportShipment>(sql, new { MonthStart = monthStart, MonthEnd = monthEnd }).ToList();
            }
        }

        private static List<ImportShipment> GetClearedImportsByMonth(string connectionString, int year, int month)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string sql = @"
                    SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                    FROM Imports i WITH (NOLOCK)
                    INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                    INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                    WHERE YEAR(i.ClearanceDate) = @Year AND MONTH(i.ClearanceDate) = @Month
                    ORDER BY i.ClearanceDate DESC";

                return conn.Query<ImportShipment>(sql, new { Year = year, Month = month }).ToList();
            }
        }

        private static MonthlyStats GetMonthlyStats(string connectionString, DateTime monthDate)
        {
            var stats = new MonthlyStats();
            var monthStart = new DateTime(monthDate.Year, monthDate.Month, 1);
            var monthEnd = monthStart.AddMonths(1);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string sql = @"
                    SELECT 
                        COUNT(*) AS TotalCount,
                        ISNULL(SUM(CASE WHEN Status = 'Cleared' THEN 1 ELSE 0 END), 0) AS ClearedCount,
                        ISNULL(SUM(CASE WHEN Status = 'Pending Clearance' THEN 1 ELSE 0 END), 0) AS PendingCount
                    FROM Imports WITH (NOLOCK)
                    WHERE ShipmentDate >= @MonthStart AND ShipmentDate < @MonthEnd";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("MonthStart", monthStart);
                    cmd.Parameters.AddWithValue("MonthEnd", monthEnd);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            stats.TotalCount = reader.GetInt32(0);
                            stats.ClearedCount = reader.GetInt32(1);
                            stats.PendingCount = reader.GetInt32(2);
                        }
                    }
                }
            }
            return stats;
        }

        private static byte[] GenerateExcelReport(List<ImportShipment> shipments, DateTime reportDate)
        {
            ExcelPackage.License.SetNonCommercialOrganization("r-pac Lanka (Pvt) Ltd");
            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Daily Cleared Shipments");

                // Show grid lines explicitly
                worksheet.View.ShowGridLines = true;

                // Title Block
                worksheet.Cells["A1:R1"].Merge = true;
                worksheet.Cells["A1"].Value = $"r-pac Lanka - Commercial Cleared Shipments Report ({reportDate:yyyy-MM-dd})";
                worksheet.Cells["A1"].Style.Font.Name = "Segoe UI";
                worksheet.Cells["A1"].Style.Font.Size = 15;
                worksheet.Cells["A1"].Style.Font.Bold = true;
                worksheet.Cells["A1"].Style.Font.Color.SetColor(Color.White);
                worksheet.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
                worksheet.Cells["A1"].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(15, 23, 42)); // Slate-900 (Elegant Dark)
                worksheet.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                worksheet.Cells["A1"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                worksheet.Row(1).Height = 35;

                // Column Headers
                string[] headers = {
                    "Shipment Date", "PO Number", "Supplier", "Material Category", "Shipping Mode",
                    "Incoterm", "Weight Value", "Weight Unit", "ETD", "ETA",
                    "Freight Cost (USD)", "Freight Forwarder", "Clearance Date", "Actual Clearance Amt (LKR)",
                    "Remarks", "Status", "Created By", "Logged At"
                };

                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = worksheet.Cells[3, i + 1];
                    cell.Value = headers[i];
                    cell.Style.Font.Name = "Segoe UI";
                    cell.Style.Font.Size = 10;
                    cell.Style.Font.Bold = true;
                    cell.Style.Font.Color.SetColor(Color.White);
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(225, 29, 72)); // Rose-600 (Vibrant highlight accent)
                    cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                }
                worksheet.Row(3).Height = 24;

                // Populate Rows
                int row = 4;
                if (shipments.Count == 0)
                {
                    worksheet.Cells["A4:R4"].Merge = true;
                    worksheet.Cells["A4"].Value = "No shipments were cleared on this date.";
                    worksheet.Cells["A4"].Style.Font.Name = "Segoe UI";
                    worksheet.Cells["A4"].Style.Font.Italic = true;
                    worksheet.Cells["A4"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    worksheet.Cells["A4"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                    worksheet.Row(4).Height = 20;
                }
                else
                {
                    foreach (var item in shipments)
                    {
                        worksheet.Cells[row, 1].Value = item.ShipmentDate;
                        worksheet.Cells[row, 2].Value = item.PONumber;
                        worksheet.Cells[row, 3].Value = item.SupplierName;
                        worksheet.Cells[row, 4].Value = item.MaterialCategory;
                        worksheet.Cells[row, 5].Value = item.ShippingMode;
                        worksheet.Cells[row, 6].Value = item.Incoterm;
                        worksheet.Cells[row, 7].Value = item.WeightValue;
                        worksheet.Cells[row, 8].Value = item.WeightUnit;
                        worksheet.Cells[row, 9].Value = item.ETD;
                        worksheet.Cells[row, 10].Value = item.ETA;
                        worksheet.Cells[row, 11].Value = item.ActualFreightCost;
                        worksheet.Cells[row, 12].Value = item.ActualFreightCostForwarder;
                        worksheet.Cells[row, 13].Value = item.ClearanceDate;
                        worksheet.Cells[row, 14].Value = item.ActualClearanceAmount;
                        worksheet.Cells[row, 15].Value = item.Remarks;
                        worksheet.Cells[row, 16].Value = item.Status;
                        worksheet.Cells[row, 17].Value = item.CreatedByFullName;
                        worksheet.Cells[row, 18].Value = item.CreatedAt;

                        // Alignments
                        worksheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 5].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 8].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 9].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 10].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 13].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 16].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

                        // Format Dates
                        worksheet.Cells[row, 1].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 9].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 10].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 13].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 18].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";

                        // Format Numeric columns
                        worksheet.Cells[row, 7].Style.Numberformat.Format = "#,##0.00";
                        worksheet.Cells[row, 11].Style.Numberformat.Format = "$#,##0.00";
                        worksheet.Cells[row, 14].Style.Numberformat.Format = "₨#,##0.00";

                        // Elegant Zebra Striping
                        if (row % 2 == 0)
                        {
                            for (int col = 1; col <= 18; col++)
                            {
                                worksheet.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                                worksheet.Cells[row, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(248, 250, 252)); // Slate-50 (Very light gray-blue)
                            }
                        }

                        // Style Borders and fonts
                        for (int col = 1; col <= 18; col++)
                        {
                            var cell = worksheet.Cells[row, col];
                            cell.Style.Font.Name = "Segoe UI";
                            cell.Style.Font.Size = 9.5F;
                            cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                            cell.Style.Border.Bottom.Color.SetColor(Color.FromArgb(226, 232, 240)); // Slate-200
                        }

                        worksheet.Row(row).Height = 20;
                        row++;
                    }
                }

                // Autofit columns to avoid clipping
                worksheet.Cells[3, 1, Math.Max(4, row - 1), 18].AutoFitColumns();

                // Add buffer width to column size for nicer rendering
                for (int col = 1; col <= 18; col++)
                {
                    worksheet.Column(col).Width += 3.5;
                }

                return package.GetAsByteArray();
            }
        }

        private static byte[] GenerateMonthlyExcelReport(List<ImportShipment> shipments, DateTime reportDate)
        {
            ExcelPackage.License.SetNonCommercialOrganization("r-pac Lanka (Pvt) Ltd");
            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Monthly Ledger");

                worksheet.View.ShowGridLines = true;

                // Title Block
                worksheet.Cells["A1:R1"].Merge = true;
                string monthName = reportDate.ToString("MMMM yyyy");
                worksheet.Cells["A1"].Value = $"r-pac Lanka - Monthly Shipments Ledger ({monthName})";
                worksheet.Cells["A1"].Style.Font.Name = "Segoe UI";
                worksheet.Cells["A1"].Style.Font.Size = 15;
                worksheet.Cells["A1"].Style.Font.Bold = true;
                worksheet.Cells["A1"].Style.Font.Color.SetColor(Color.White);
                worksheet.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
                worksheet.Cells["A1"].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(30, 58, 138)); // Dark Blue (Indigo-900)
                worksheet.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                worksheet.Cells["A1"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                worksheet.Row(1).Height = 35;

                // Column Headers
                string[] headers = {
                    "Shipment Date", "PO Number", "Supplier", "Material Category", "Shipping Mode",
                    "Incoterm", "Weight Value", "Weight Unit", "ETD", "ETA",
                    "Freight Cost (USD)", "Freight Forwarder", "Clearance Date", "Actual Clearance Amt (LKR)",
                    "Remarks", "Status", "Created By", "Logged At"
                };

                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = worksheet.Cells[3, i + 1];
                    cell.Value = headers[i];
                    cell.Style.Font.Name = "Segoe UI";
                    cell.Style.Font.Size = 10;
                    cell.Style.Font.Bold = true;
                    cell.Style.Font.Color.SetColor(Color.White);
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(79, 70, 229)); // Indigo-600
                    cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                }
                worksheet.Row(3).Height = 24;

                int row = 4;
                if (shipments.Count == 0)
                {
                    worksheet.Cells["A4:R4"].Merge = true;
                    worksheet.Cells["A4"].Value = "No shipments registered in this month.";
                    worksheet.Cells["A4"].Style.Font.Name = "Segoe UI";
                    worksheet.Cells["A4"].Style.Font.Italic = true;
                    worksheet.Cells["A4"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    worksheet.Row(4).Height = 20;
                }
                else
                {
                    foreach (var item in shipments)
                    {
                        worksheet.Cells[row, 1].Value = item.ShipmentDate;
                        worksheet.Cells[row, 2].Value = item.PONumber;
                        worksheet.Cells[row, 3].Value = item.SupplierName;
                        worksheet.Cells[row, 4].Value = item.MaterialCategory;
                        worksheet.Cells[row, 5].Value = item.ShippingMode;
                        worksheet.Cells[row, 6].Value = item.Incoterm;
                        worksheet.Cells[row, 7].Value = item.WeightValue;
                        worksheet.Cells[row, 8].Value = item.WeightUnit;
                        worksheet.Cells[row, 9].Value = item.ETD;
                        worksheet.Cells[row, 10].Value = item.ETA;
                        worksheet.Cells[row, 11].Value = item.ActualFreightCost;
                        worksheet.Cells[row, 12].Value = item.ActualFreightCostForwarder;
                        worksheet.Cells[row, 13].Value = item.ClearanceDate;
                        worksheet.Cells[row, 14].Value = item.ActualClearanceAmount;
                        worksheet.Cells[row, 15].Value = item.Remarks;
                        worksheet.Cells[row, 16].Value = item.Status;
                        worksheet.Cells[row, 17].Value = item.CreatedByFullName;
                        worksheet.Cells[row, 18].Value = item.CreatedAt;

                        // Alignments
                        worksheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 5].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 8].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 9].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 10].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 13].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        worksheet.Cells[row, 16].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

                        // Format Dates
                        worksheet.Cells[row, 1].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 9].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 10].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 13].Style.Numberformat.Format = "yyyy-MM-dd";
                        worksheet.Cells[row, 18].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";

                        // Format Numbers
                        worksheet.Cells[row, 7].Style.Numberformat.Format = "#,##0.00";
                        worksheet.Cells[row, 11].Style.Numberformat.Format = "$#,##0.00";
                        worksheet.Cells[row, 14].Style.Numberformat.Format = "₨#,##0.00";

                        // Status Color Highlighting
                        if (item.Status.Equals("Cleared", StringComparison.OrdinalIgnoreCase))
                        {
                            worksheet.Cells[row, 16].Style.Font.Color.SetColor(Color.FromArgb(5, 150, 105)); // Green text
                        }
                        else
                        {
                            worksheet.Cells[row, 16].Style.Font.Color.SetColor(Color.FromArgb(217, 119, 6)); // Amber text
                        }

                        // Zebra Striping
                        if (row % 2 == 0)
                        {
                            for (int col = 1; col <= 18; col++)
                            {
                                worksheet.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                                worksheet.Cells[row, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(248, 250, 252));
                            }
                        }

                        // Style Borders and fonts
                        for (int col = 1; col <= 18; col++)
                        {
                            var cell = worksheet.Cells[row, col];
                            cell.Style.Font.Name = "Segoe UI";
                            cell.Style.Font.Size = 9.5F;
                            cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                            cell.Style.Border.Bottom.Color.SetColor(Color.FromArgb(226, 232, 240));
                        }

                        worksheet.Row(row).Height = 20;
                        row++;
                    }
                }

                worksheet.Cells[3, 1, Math.Max(4, row - 1), 18].AutoFitColumns();
                for (int col = 1; col <= 18; col++)
                {
                        worksheet.Column(col).Width += 3.5;
                }

                return package.GetAsByteArray();
            }
        }

        private static string BuildHtmlReport(List<ImportShipment> shipments, DateTime reportDate, MonthlyStats stats, List<ImportShipment> currentMonthCleared, List<ImportShipment> prevMonthCleared)
        {
            int clearedPercentage = stats.TotalCount > 0 ? (int)Math.Round((double)stats.ClearedCount / stats.TotalCount * 100) : 0;
            int pendingPercentage = stats.TotalCount > 0 ? (int)Math.Round((double)stats.PendingCount / stats.TotalCount * 100) : 0;
            string monthName = reportDate.ToString("MMMM yyyy");

            // Process Monthly Shipments data to aggregate freight costs by material category (current month vs previous month)
            var standardCategories = new[] { "RFID", "Paper Board", "Satin Ribbon", "Ink/Chemical/Ink Foil", "Adhesive", "Consumable", "Accessories" };
            
            var currentFreight = standardCategories.ToDictionary(cat => cat, cat => 0m, StringComparer.OrdinalIgnoreCase);
            var prevFreight = standardCategories.ToDictionary(cat => cat, cat => 0m, StringComparer.OrdinalIgnoreCase);

            foreach (var item in currentMonthCleared)
            {
                if (item.ActualFreightCost.HasValue && item.ActualFreightCost.Value > 0)
                {
                    decimal cost = item.ActualFreightCost.Value;
                    var cats = string.IsNullOrEmpty(item.MaterialCategory)
                        ? new string[0]
                        : item.MaterialCategory.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                    var trimmedCats = cats.Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
                    foreach (var cat in trimmedCats)
                    {
                        if (currentFreight.ContainsKey(cat))
                            currentFreight[cat] += cost;
                        else
                            currentFreight[cat] = cost;
                    }
                }
            }

            foreach (var item in prevMonthCleared)
            {
                if (item.ActualFreightCost.HasValue && item.ActualFreightCost.Value > 0)
                {
                    decimal cost = item.ActualFreightCost.Value;
                    var cats = string.IsNullOrEmpty(item.MaterialCategory)
                        ? new string[0]
                        : item.MaterialCategory.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                    var trimmedCats = cats.Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
                    foreach (var cat in trimmedCats)
                    {
                        if (prevFreight.ContainsKey(cat))
                            prevFreight[cat] += cost;
                        else
                            prevFreight[cat] = cost;
                    }
                }
            }

            decimal maxVal = 0;
            if (currentFreight.Values.Any() || prevFreight.Values.Any())
            {
                decimal maxCurrent = currentFreight.Values.Any() ? currentFreight.Values.Max() : 0m;
                decimal maxPrev = prevFreight.Values.Any() ? prevFreight.Values.Max() : 0m;
                maxVal = Math.Max(maxCurrent, maxPrev);
            }
            if (maxVal == 0) maxVal = 1;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
            sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />");
            sb.AppendLine("  <title>r-pac Lanka Commercial Report</title>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body style=\"margin: 0; padding: 0; background-color: #f8fafc;\">");
            
            sb.AppendLine("  <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" bgcolor=\"#f8fafc\" style=\"background-color: #f8fafc; padding: 20px 0;\">");
            sb.AppendLine("    <tr>");
            sb.AppendLine("      <td align=\"center\">");
            
            sb.AppendLine("        <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"960\" bgcolor=\"#ffffff\" style=\"background-color: #ffffff; border-collapse: collapse; width: 960px;\">");
            
            sb.AppendLine("          <tr>");
            sb.AppendLine("            <td align=\"center\" bgcolor=\"#0f172a\" style=\"background-color: #0f172a; padding: 12px; border-bottom: 4px solid #e11d48;\">");
            sb.AppendLine("              <span style=\"color: #ffffff; font-family: Arial, Helvetica, sans-serif; font-size: 18px; font-weight: bold; text-transform: uppercase; letter-spacing: 1px; line-height: 22px;\">r-pac Lanka (Pvt) Ltd</span><br />");
            sb.AppendLine("              <span style=\"color: #94a3b8; font-family: Arial, Helvetica, sans-serif; font-size: 10px; font-weight: bold; text-transform: uppercase; letter-spacing: 1px; line-height: 14px;\">Commercial Shipments Daily Cleared Report</span>");
            sb.AppendLine("            </td>");
            sb.AppendLine("          </tr>");
            
            sb.AppendLine("          <tr>");
            sb.AppendLine("            <td style=\"padding: 30px; font-family: Arial, Helvetica, sans-serif;\">");
            sb.AppendLine("              <p style=\"font-family: Arial, Helvetica, sans-serif; font-size: 14px; line-height: 20px; color: #0f172a; margin-top: 0; margin-bottom: 15px; font-weight: bold;\">Dear Team,</p>");
            sb.AppendLine($"              <p style=\"font-family: Arial, Helvetica, sans-serif; font-size: 13.5px; line-height: 20px; color: #334155; margin-bottom: 25px;\">Please find attached the daily spreadsheet detailing import shipments cleared on <strong>{reportDate:yyyy-MM-dd}</strong>.<br/><strong>Total shipments cleared yesterday: {shipments.Count}</strong></p>");
            
            sb.AppendLine("              <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"width: 100%; margin-top: 15px; margin-bottom: 15px; border-collapse: collapse;\">");
            sb.AppendLine("                <tr>");
            sb.AppendLine("                  <td width=\"47%\" valign=\"bottom\" style=\"width: 47%; border-bottom: 2px solid #e2e8f0; padding-bottom: 8px;\">");
            sb.AppendLine($"                    <div style=\"font-family: Arial, Helvetica, sans-serif; font-size: 14px; font-weight: bold; color: #0f172a; text-transform: uppercase; letter-spacing: 0.5px; margin: 0; line-height: 18px;\">Monthly Shipments Status Overview ({monthName})</div>");
            sb.AppendLine("                  </td>");
            sb.AppendLine("                  <td width=\"4%\" style=\"width: 4%; border-bottom: 2px solid #e2e8f0;\">&nbsp;</td>");
            sb.AppendLine("                  <td width=\"49%\" valign=\"bottom\" style=\"width: 49%; border-bottom: 2px solid #e2e8f0; padding-bottom: 8px;\">");
            sb.AppendLine($"                    <div style=\"font-family: Arial, Helvetica, sans-serif; font-size: 14px; font-weight: bold; color: #0f172a; text-transform: uppercase; letter-spacing: 0.5px; margin: 0; line-height: 18px;\">Actual Freight Cost Comparison (USD)</div>");
            sb.AppendLine("                  </td>");
            sb.AppendLine("                </tr>");
            sb.AppendLine("              </table>");
            
            sb.AppendLine("              <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"width: 100%; border-collapse: collapse;\">");
            sb.AppendLine("                <tr>");
            sb.AppendLine("                  <td width=\"47%\" valign=\"top\" style=\"width: 47%;\">");
            sb.AppendLine("                    <table border=\"1\" cellpadding=\"12\" cellspacing=\"0\" width=\"100%\" bordercolor=\"#e2e8f0\" style=\"border: 1px solid #e2e8f0; border-collapse: collapse; font-family: Arial, Helvetica, sans-serif; font-size: 13.5px; width: 100%;\">");
            sb.AppendLine("                      <thead>");
            sb.AppendLine("                        <tr bgcolor=\"#0f172a\" style=\"background-color: #0f172a; color: #ffffff;\">");
            sb.AppendLine("                          <th align=\"left\" style=\"color: #ffffff; font-weight: bold; text-transform: uppercase; font-size: 11px; padding: 12px;\">Status Category</th>");
            sb.AppendLine("                          <th align=\"center\" style=\"color: #ffffff; font-weight: bold; text-transform: uppercase; font-size: 11px; padding: 12px;\">Count</th>");
            sb.AppendLine("                          <th align=\"right\" style=\"color: #ffffff; font-weight: bold; text-transform: uppercase; font-size: 11px; padding: 12px;\">Completion Rate</th>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                      </thead>");
            sb.AppendLine("                      <tbody>");
            sb.AppendLine("                        <tr bgcolor=\"#ffffff\" style=\"background-color: #ffffff;\">");
            sb.AppendLine("                          <td style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-size: 13px;\">Total Scheduled Volume</td>");
            sb.AppendLine($"                          <td align=\"center\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-weight: bold; font-size: 14px;\">{stats.TotalCount}</td>");
            sb.AppendLine("                          <td align=\"right\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; color: #475569; font-size: 13px;\">100% of Month</td>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                        <tr bgcolor=\"#f8fafc\" style=\"background-color: #f8fafc;\">");
            sb.AppendLine("                          <td style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; color: #059669; font-weight: bold; font-size: 13px;\">Successfully Cleared</td>");
            sb.AppendLine($"                          <td align=\"center\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-weight: bold; color: #059669; font-size: 14px;\">{stats.ClearedCount}</td>");
            sb.AppendLine($"                          <td align=\"right\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-weight: bold; color: #059669; font-size: 13px;\">{clearedPercentage}% Cleared</td>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                        <tr bgcolor=\"#ffffff\" style=\"background-color: #ffffff;\">");
            sb.AppendLine("                          <td style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; color: #d97706; font-weight: bold; font-size: 13px;\">Pending Clearance</td>");
            sb.AppendLine($"                          <td align=\"center\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-weight: bold; color: #d97706; font-size: 14px;\">{stats.PendingCount}</td>");
            sb.AppendLine($"                          <td align=\"right\" style=\"padding: 12px; font-family: Arial, Helvetica, sans-serif; font-weight: bold; color: #d97706; font-size: 13px;\">{pendingPercentage}% Pending</td>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                      </tbody>");
            sb.AppendLine("                    </table>");
            sb.AppendLine("                  </td>");
            sb.AppendLine("                  <td width=\"4%\" style=\"width: 4%;\">&nbsp;</td>");
            sb.AppendLine("                  <td width=\"49%\" valign=\"top\" style=\"width: 49%;\">");
            sb.AppendLine("                    <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" align=\"center\" style=\"margin: 0 auto 16px auto; border-collapse: collapse; font-family: Arial, Helvetica, sans-serif; font-size: 11px;\">");
            sb.AppendLine("                      <tr>");
            sb.AppendLine("                        <td style=\"padding: 0 10px; font-weight: bold; color: #3b82f6;\">");
            sb.AppendLine($"                          <span style=\"display: inline-block; width: 10px; height: 10px; background-color: #3b82f6; border-radius: 2px; margin-right: 6px; vertical-align: middle;\"></span>{reportDate:MMM yyyy}");
            sb.AppendLine("                        </td>");
            sb.AppendLine("                        <td style=\"padding: 0 10px; font-weight: bold; color: #10b981;\">");
            sb.AppendLine($"                          <span style=\"display: inline-block; width: 10px; height: 10px; background-color: #10b981; border-radius: 2px; margin-right: 6px; vertical-align: middle;\"></span>{reportDate.AddMonths(-1):MMM yyyy}");
            sb.AppendLine("                        </td>");
            sb.AppendLine("                      </tr>");
            sb.AppendLine("                    </table>");

            decimal niceMax = 100m;
            if (maxVal > 0)
            {
                if (maxVal <= 100) niceMax = 100m;
                else if (maxVal <= 250) niceMax = 250m;
                else if (maxVal <= 500) niceMax = 500m;
                else if (maxVal <= 1000) niceMax = 1000m;
                else niceMax = Math.Ceiling(maxVal / 500m) * 500m;
            }
            decimal tickStep = niceMax / 5m;

            string tick5 = string.Format("${0:N0}", niceMax);
            string tick4 = string.Format("${0:N0}", niceMax - tickStep);
            string tick3 = string.Format("${0:N0}", niceMax - tickStep * 2);
            string tick2 = string.Format("${0:N0}", niceMax - tickStep * 3);
            string tick1 = string.Format("${0:N0}", niceMax - tickStep * 4);
            string tick0 = "$0";

            sb.AppendLine("                    <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse: collapse; font-family: Arial, Helvetica, sans-serif; width: 100%;\">");
            
            // ROW 1: Bar Chart
            sb.AppendLine("                      <tr height=\"110\" valign=\"bottom\">");
            sb.AppendLine("                        <td width=\"45\" valign=\"bottom\" style=\"width: 45px; border-right: 1.5px solid #cbd5e1; padding-right: 6px; padding-bottom: 0px;\">");
            sb.AppendLine("                          <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" height=\"110\" style=\"height: 110px; width: 100%; border-collapse: collapse; font-family: Arial, Helvetica, sans-serif; font-size: 9px; color: #64748b; font-weight: bold;\">");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"top\" style=\"height: 22px; line-height: 1;\">" + tick5 + "</td></tr>");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"top\" style=\"height: 22px; line-height: 1;\">" + tick4 + "</td></tr>");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"top\" style=\"height: 22px; line-height: 1;\">" + tick3 + "</td></tr>");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"top\" style=\"height: 22px; line-height: 1;\">" + tick2 + "</td></tr>");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"top\" style=\"height: 22px; line-height: 1;\">" + tick1 + "</td></tr>");
            sb.AppendLine("                            <tr><td align=\"right\" valign=\"bottom\" style=\"height: 0px; line-height: 1;\">" + tick0 + "</td></tr>");
            sb.AppendLine("                          </table>");
            sb.AppendLine("                        </td>");
            
            foreach (var cat in standardCategories)
            {
                decimal curVal = currentFreight[cat];
                decimal pVal = prevFreight[cat];
                int curHeight = curVal > 0 ? Math.Max(2, (int)Math.Round((double)(curVal / niceMax) * 110)) : 0;
                int prevHeight = pVal > 0 ? Math.Max(2, (int)Math.Round((double)(pVal / niceMax) * 110)) : 0;
                
                sb.AppendLine("                        <td align=\"center\" valign=\"bottom\" style=\"padding: 0 3px; border-bottom: 1.5px solid #cbd5e1; background-image: linear-gradient(to bottom, #f1f5f9 1px, transparent 1px); background-size: 100% 22px; height: 110px;\">");
                sb.AppendLine("                          <table align=\"center\" border=\"0\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin: 0 auto; border-collapse: collapse;\">");
                sb.AppendLine("                            <tr>");
                
                sb.AppendLine("                              <td valign=\"bottom\" style=\"padding: 0 1px;\">");
                if (curHeight > 0)
                {
                    sb.AppendLine("                                <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"24\" style=\"width: 24px; border-collapse: collapse;\">");
                    sb.AppendLine("                                  <tr>");
                    sb.AppendLine("                                    <td height=\"" + curHeight + "\" bgcolor=\"#3b82f6\" style=\"background-color: #3b82f6; height: " + curHeight + "px; border-radius: 2px 2px 0 0; width: 24px;\">&nbsp;</td>");
                    sb.AppendLine("                                  </tr>");
                    sb.AppendLine("                                </table>");
                }
                else
                {
                    sb.AppendLine("                                <div style=\"width: 24px; height: 1px;\"></div>");
                }
                sb.AppendLine("                              </td>");
                
                sb.AppendLine("                              <td valign=\"bottom\" style=\"padding: 0 1px;\">");
                if (prevHeight > 0)
                {
                    sb.AppendLine("                                <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" width=\"24\" style=\"width: 24px; border-collapse: collapse;\">");
                    sb.AppendLine("                                  <tr>");
                    sb.AppendLine("                                    <td height=\"" + prevHeight + "\" bgcolor=\"#10b981\" style=\"background-color: #10b981; height: " + prevHeight + "px; border-radius: 2px 2px 0 0; width: 24px;\">&nbsp;</td>");
                    sb.AppendLine("                                  </tr>");
                    sb.AppendLine("                                </table>");
                }
                else
                {
                    sb.AppendLine("                                <div style=\"width: 24px; height: 1px;\"></div>");
                }
                sb.AppendLine("                              </td>");
                
                sb.AppendLine("                            </tr>");
                sb.AppendLine("                          </table>");
                sb.AppendLine("                        </td>");
            }
            sb.AppendLine("                      </tr>");

            // ROW 2: Category Labels
            sb.AppendLine("                      <tr>");
            sb.AppendLine("                        <td style=\"width: 45px; border-right: 1.5px solid #cbd5e1;\">&nbsp;</td>");
            foreach (var cat in standardCategories)
            {
                string displayName = cat;
                if (displayName.Equals("Paper Board", StringComparison.OrdinalIgnoreCase)) displayName = "Paper Brd";
                else if (displayName.Equals("Satin Ribbon", StringComparison.OrdinalIgnoreCase)) displayName = "Satin Rbn";
                else if (displayName.Equals("Ink/Chemical/Ink Foil", StringComparison.OrdinalIgnoreCase)) displayName = "Ink/Chem";
                else if (displayName.Equals("Accessories", StringComparison.OrdinalIgnoreCase)) displayName = "Accessory";
                
                sb.AppendLine("                        <td align=\"center\" style=\"padding-top: 8px; font-size: 8px; font-weight: bold; color: #64748b; line-height: 10px;\" valign=\"top\">");
                sb.AppendLine("                          " + displayName);
                sb.AppendLine("                        </td>");
            }
            sb.AppendLine("                      </tr>");
            sb.AppendLine("                    </table>");
            sb.AppendLine("                  </td>");
            sb.AppendLine("                </tr>");
            sb.AppendLine("              </table>");
            sb.AppendLine("            </td>");
            sb.AppendLine("          </tr>");
            
            sb.AppendLine("          <tr>");
            sb.AppendLine("            <td align=\"center\" bgcolor=\"#f8fafc\" style=\"background-color: #f8fafc; padding: 20px; text-align: center; border-top: 1px solid #e2e8f0; font-family: Arial, Helvetica, sans-serif; font-size: 11px; color: #6c757d;\">");
            sb.AppendLine("              This is an Auto Generated Mail From r-pac Mail System Developed By r-pac IT Department<br />");
            sb.AppendLine($"              [Generated Date: {DateTime.Now:yyyy-MM-dd} Time: {DateTime.Now:HH:mm:ss} - From r-pac IT]");
            sb.AppendLine("            </td>");
            sb.AppendLine("          </tr>");
            
            sb.AppendLine("        </table>");
            sb.AppendLine("      </td>");
            sb.AppendLine("    </tr>");
            sb.AppendLine("  </table>");
            
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private static void SendEmailReport(string smtpServer, int smtpPort, string smtpUser, string smtpPassword, 
            string senderEmail, string senderName, List<ReportRecipient> recipients, List<ImportShipment> shipments, 
            DateTime reportDate, string dailyAttachmentPath, string monthlyAttachmentPath, MonthlyStats stats, List<ImportShipment> currentMonthCleared, List<ImportShipment> prevMonthCleared)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(senderName, senderEmail));
            
            foreach (var recipient in recipients)
            {
                var address = new MailboxAddress(recipient.EmailAddress, recipient.EmailAddress);
                if (recipient.RecipientType.Equals("To", StringComparison.OrdinalIgnoreCase))
                {
                    message.To.Add(address);
                }
                else if (recipient.RecipientType.Equals("Cc", StringComparison.OrdinalIgnoreCase))
                {
                    message.Cc.Add(address);
                }
            }

            if (message.To.Count == 0)
            {
                Console.WriteLine("Warning: No 'To' recipients resolved. Defaulting the first active recipient to 'To' to avoid SMTP errors.");
                var firstCc = recipients.FirstOrDefault();
                if (firstCc != null)
                {
                    message.To.Add(new MailboxAddress(firstCc.EmailAddress, firstCc.EmailAddress));
                }
            }

            message.Subject = $"r-pac Lanka Commercial: Cleared Imports Report ({reportDate:yyyy-MM-dd})";

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = BuildHtmlReport(shipments, reportDate, stats, currentMonthCleared, prevMonthCleared)
            };

            // Attach the Excel Reports
            if (File.Exists(dailyAttachmentPath))
            {
                bodyBuilder.Attachments.Add(dailyAttachmentPath);
            }
            else
            {
                throw new FileNotFoundException("Cannot locate generated Daily Excel report to attach.", dailyAttachmentPath);
            }

            if (File.Exists(monthlyAttachmentPath))
            {
                bodyBuilder.Attachments.Add(monthlyAttachmentPath);
            }
            else
            {
                throw new FileNotFoundException("Cannot locate generated Monthly Excel report to attach.", monthlyAttachmentPath);
            }

            message.Body = bodyBuilder.ToMessageBody();

            using (var client = new SmtpClient())
            {
                // Accept all SSL certificates (since we are on a private server/test environment)
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;

                client.Connect(smtpServer, smtpPort, SecureSocketOptions.StartTls);
                
                if (!string.IsNullOrEmpty(smtpUser))
                {
                    client.Authenticate(smtpUser, smtpPassword);
                }

                client.Send(message);
                client.Disconnect(true);
            }
        }
    }

    public class ImportShipment
    {
        public int ImportId { get; set; }
        public DateTime ShipmentDate { get; set; }
        public string PONumber { get; set; } = string.Empty;
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string ShippingMode { get; set; } = string.Empty;
        public string Incoterm { get; set; } = string.Empty;
        public decimal WeightValue { get; set; }
        public string WeightUnit { get; set; } = string.Empty;
        public string? MaterialCategory { get; set; }
        public DateTime ETD { get; set; }
        public DateTime ETA { get; set; }
        public decimal? ActualFreightCost { get; set; }
        public string? ActualFreightCostForwarder { get; set; }
        public DateTime? ClearanceDate { get; set; }
        public decimal? ApproximateClearanceAmount { get; set; }
        public decimal? ActualClearanceAmount { get; set; }
        public string? Remarks { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CreatedByFullName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class ReportRecipient
    {
        public string EmailAddress { get; set; } = string.Empty;
        public string RecipientType { get; set; } = string.Empty; // Can be "To" or "Cc"
        public bool IsActive { get; set; }
    }

    public class MonthlyStats
    {
        public int TotalCount { get; set; }
        public int ClearedCount { get; set; }
        public int PendingCount { get; set; }
    }
}
