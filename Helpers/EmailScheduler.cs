// /Helpers/EmailScheduler.cs
using System;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using Dapper;
using RpacCommercial.Repositories;

namespace RpacCommercial.Helpers
{
    public static class EmailScheduler
    {
        private static Timer _timer;
        private static bool _isRunning = false;
        private static readonly object _lock = new object();

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer == null)
                {
                    // Run every 1 hour (3600000 milliseconds), starting immediately
                    _timer = new Timer(CheckAndSendMorningReport, null, 0, 3600000);
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        private static void CheckAndSendMorningReport(object state)
        {
            // Prevent multiple threads from running simultaneously
            if (_isRunning) return;

            try
            {
                _isRunning = true;

                // We want to dispatch precisely at the 6:00 AM hour slot
                int currentHour = DateTime.Now.Hour;
                if (currentHour != 6)
                {
                    return; // Skip if it's not the 6:00 AM hour block
                }

                using (var conn = MSSQLHelper.GetConnection())
                {
                    // Check if a report was already dispatched today to survive IIS recycling
                    string checkSql = "SELECT COUNT(*) FROM EmailLogs WHERE SentDate = CAST(GETDATE() AS DATE)";
                    int alreadySent = conn.ExecuteScalar<int>(checkSql);

                    if (alreadySent > 0)
                    {
                        return; // Already sent today, skip duplicate dispatch
                    }

                    // Retrieve yesterday's cleared imports
                    var importRepository = new ImportRepository();
                    var clearedImports = importRepository.GetYesterdaysClearedImports().ToList();

                    // Query monthly shipments and monthly stats from database
                    DateTime yesterday = DateTime.Today.AddDays(-1);
                    var monthStart = new DateTime(yesterday.Year, yesterday.Month, 1);
                    var monthEnd = monthStart.AddMonths(1);

                    var monthlyShipments = conn.Query<Models.ImportShipment>(@"
                        SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                        FROM Imports i WITH (NOLOCK)
                        INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                        INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                        WHERE i.ShipmentDate >= @MonthStart AND i.ShipmentDate < @MonthEnd
                        ORDER BY i.ShipmentDate ASC", new { MonthStart = monthStart, MonthEnd = monthEnd }).ToList();

                    DateTime prevMonthStart = monthStart.AddMonths(-1);
                    DateTime prevMonthEnd = monthStart;

                    var prevMonthlyShipments = conn.Query<Models.ImportShipment>(@"
                        SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                        FROM Imports i WITH (NOLOCK)
                        INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                        INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                        WHERE i.ShipmentDate >= @PrevMonthStart AND i.ShipmentDate < @PrevMonthEnd
                        ORDER BY i.ShipmentDate ASC", new { PrevMonthStart = prevMonthStart, PrevMonthEnd = prevMonthEnd }).ToList();

                    // Load exact cleared shipments lists by ClearanceDate for the dashboard-aligned chart
                    var chartCurrentCleared = conn.Query<Models.ImportShipment>(@"
                        SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                        FROM Imports i WITH (NOLOCK)
                        INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                        INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                        WHERE YEAR(i.ClearanceDate) = @Year AND MONTH(i.ClearanceDate) = @Month
                        ORDER BY i.ClearanceDate DESC", new { Year = yesterday.Year, Month = yesterday.Month }).ToList();

                    var chartPrevCleared = conn.Query<Models.ImportShipment>(@"
                        SELECT i.*, s.SupplierName, u1.FullName as CreatedByFullName
                        FROM Imports i WITH (NOLOCK)
                        INNER JOIN Suppliers s WITH (NOLOCK) ON i.SupplierId = s.SupplierId
                        INNER JOIN Users u1 WITH (NOLOCK) ON i.CreatedBy = u1.UserId
                        WHERE YEAR(i.ClearanceDate) = @Year AND MONTH(i.ClearanceDate) = @Month
                        ORDER BY i.ClearanceDate DESC", new { Year = yesterday.AddMonths(-1).Year, Month = yesterday.AddMonths(-1).Month }).ToList();

                    var stats = new MonthlyStats();
                    var statsData = conn.Query<dynamic>(@"
                        SELECT 
                            COUNT(*) AS TotalCount,
                            ISNULL(SUM(CASE WHEN Status = 'Cleared' THEN 1 ELSE 0 END), 0) AS ClearedCount,
                            ISNULL(SUM(CASE WHEN Status = 'Pending Clearance' THEN 1 ELSE 0 END), 0) AS PendingCount
                        FROM Imports WITH (NOLOCK)
                        WHERE ShipmentDate >= @MonthStart AND ShipmentDate < @MonthEnd", new { MonthStart = monthStart, MonthEnd = monthEnd }).FirstOrDefault();

                    if (statsData != null)
                    {
                        stats.TotalCount = (int)statsData.TotalCount;
                        stats.ClearedCount = (int)statsData.ClearedCount;
                        stats.PendingCount = (int)statsData.PendingCount;
                    }

                    // Fetch active email recipients from database
                    var activeRecipients = conn.Query<dynamic>("SELECT EmailAddress, RecipientType FROM ReportRecipients WHERE IsActive = 1").ToList();

                    // Generate the beautiful HTML report content
                    string emailBody = BuildHtmlReport(clearedImports, yesterday, stats, chartCurrentCleared, chartPrevCleared);

                    // Read SMTP configurations from web.config AppSettings
                    string smtpServer = ConfigurationManager.AppSettings["SmtpServer"] ?? "smtp.office365.com";
                    int smtpPort = int.Parse(ConfigurationManager.AppSettings["SmtpPort"] ?? "587");
                    string smtpUser = ConfigurationManager.AppSettings["SmtpUser"] ?? "";
                    string smtpPassword = ConfigurationManager.AppSettings["SmtpPassword"] ?? "";
                    string smtpSender = ConfigurationManager.AppSettings["SmtpSender"] ?? smtpUser;
                    string smtpReceiver = ConfigurationManager.AppSettings["SmtpReceiver"] ?? "";

                    if (string.IsNullOrEmpty(smtpUser))
                    {
                        // Log warning to System Diagnostics Trace if settings are incomplete
                        System.Diagnostics.Trace.WriteLine("EmailScheduler Warning: SMTP settings are not fully configured in web.config.");
                        return;
                    }

                    // Dispatch Outlook/SMTP mail
                    using (var mail = new MailMessage())
                    {
                        mail.From = new MailAddress(smtpSender, "r-pac Commercial Dept");
                        
                        bool hasRecipients = false;
                        foreach (var rec in activeRecipients)
                        {
                            string email = rec.EmailAddress;
                            string type = rec.RecipientType;
                            if (type.Equals("Cc", StringComparison.OrdinalIgnoreCase))
                            {
                                mail.CC.Add(new MailAddress(email.Trim()));
                            }
                            else
                            {
                                mail.To.Add(new MailAddress(email.Trim()));
                                hasRecipients = true;
                            }
                        }

                        // Fallback to web.config receiver list if no active recipients in database
                        if (!hasRecipients && !string.IsNullOrEmpty(smtpReceiver))
                        {
                            var receivers = smtpReceiver.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var receiver in receivers)
                            {
                                mail.To.Add(new MailAddress(receiver.Trim()));
                            }
                        }

                        if (mail.To.Count == 0)
                        {
                            System.Diagnostics.Trace.WriteLine("EmailScheduler Warning: No recipients configured to send email.");
                            return;
                        }

                        mail.Subject = string.Format("r-pac Lanka Commercial: Cleared Imports Report ({0:yyyy-MM-dd})", yesterday);
                        mail.Body = emailBody;
                        mail.IsBodyHtml = true;
                        mail.BodyEncoding = Encoding.UTF8;

                        using (var client = new SmtpClient(smtpServer, smtpPort))
                        {
                            client.UseDefaultCredentials = false;
                            client.Credentials = new NetworkCredential(smtpUser, smtpPassword);
                            client.EnableSsl = true;
                            client.Send(mail);
                        }
                    }

                    // Log dispatch in database to ensure duplicate prevention
                    string logSql = "INSERT INTO EmailLogs (SentDate, SentAt) VALUES (CAST(GETDATE() AS DATE), GETDATE())";
                    conn.Execute(logSql);

                    System.Diagnostics.Trace.WriteLine(string.Format("EmailScheduler: Morning report sent successfully at {0}", DateTime.Now));
                }
            }
            catch (Exception ex)
            {
                // Gracefully log errors
                System.Diagnostics.Trace.WriteLine("EmailScheduler Error: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                _isRunning = false;
            }
        }

        private static string BuildHtmlReport(System.Collections.Generic.List<Models.ImportShipment> shipments, DateTime reportDate, MonthlyStats stats, System.Collections.Generic.List<Models.ImportShipment> currentMonthCleared, System.Collections.Generic.List<Models.ImportShipment> prevMonthCleared)
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
            sb.AppendLine("              This is an Auto Generated Mail From r-pac Lanka Commercial Report System Developed By r-pac IT Department<br />");
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

        private class MonthlyStats
        {
            public int TotalCount { get; set; }
            public int ClearedCount { get; set; }
            public int PendingCount { get; set; }
        }
    }
}
