# RPAC Commercial – Import Shipment Ledger

An internal web application for managing commercial import shipments, supplier records, and daily reporting for the R-PAC Commercial department.

---

## Features

- **Import Shipment Ledger** – Create, view, edit, and track import shipments with status management (Pending / Cleared / On Hold)
- **Supplier & Master Data** – Manage supplier records and lookup tables used across the application
- **Dashboard** – Live summary of shipment statuses, cost breakdowns, and month-over-month comparisons with a bar chart
- **Reports** – Filter and export shipment data by date range, supplier, or status to Excel
- **Automated Daily Email Reports** – A console utility (`Company.DailyReporter`) runs via Windows Task Scheduler each morning and emails the previous day's cleared shipments summary with an attached Excel sheet
- **Role-based Access** – Admin and read-only user roles with session-based authentication

---

## Tech Stack

| Layer | Technology |
|---|---|
| Web Framework | ASP.NET MVC 5 (.NET Framework 4.8) |
| ORM / Data | Dapper + SQL Server |
| Front-end | Bootstrap 4, jQuery, Chart.js |
| Email | MailKit (SMTP) |
| Excel Export | EPPlus |
| Reporter | .NET 8 Console App (self-contained) |

---

## Getting Started

### Prerequisites
- Visual Studio 2022
- SQL Server (2016 or later)
- .NET Framework 4.8 SDK
- .NET 8 SDK (for the reporter project)

### 1. Database Setup

Run `RpacCommercialDB.sql` against your SQL Server instance to create the schema and seed reference data.

### 2. Web Application Configuration

Open `web.config` and update:
```xml
<connectionStrings>
  <add name="MSSQLServer" connectionString="Data Source=...;Initial Catalog=...;User ID=...;Password=...;" />
</connectionStrings>
```

### 3. Daily Reporter Configuration

Copy the example config and fill in your values:
```
Company.DailyReporter\appsettings.example.json  →  Company.DailyReporter\appsettings.json
```

Edit `appsettings.json`:
- `ConnectionStrings.MSSQLServer` – same DB connection as the web app
- `SmtpSettings` – Office 365 (or other) SMTP credentials
- `ReportSettings.TempDirectory` – writable folder for temporary Excel files (e.g. `C:\CompanyReports\Temp\`)

### 4. Build & Run

Open `RpacCommercial.sln` in Visual Studio 2022 and press **F5**.

---

## Daily Reporter – Windows Task Scheduler Setup

The `Company.DailyReporter` project is a self-contained console app that sends an automated email each morning.

### Publish

```bash
dotnet publish Company.DailyReporter\Company.DailyReporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output will be at: `Company.DailyReporter\bin\Release\net8.0\win-x64\publish\`

### Deploy

1. Create `C:\CompanyReports\DailyReporter\` on the target server
2. Copy the publish output there
3. Place your configured `appsettings.json` in the same folder

### Schedule

Open **Task Scheduler** → **Create Task**:

| Setting | Value |
|---|---|
| Name | `Company.DailyReporter` |
| Trigger | Daily at 6:00 AM |
| Action | `C:\CompanyReports\DailyReporter\Company.DailyReporter.exe` |
| **Start In** | `C:\CompanyReports\DailyReporter` (must be set — the app reads `appsettings.json` relative to its working directory) |
| Run as | Service/admin account with DB and network access |
| Run whether user is logged on | ✅ Yes |
| Run with highest privileges | ✅ Yes |

> **Note:** The "Start In" field is critical. Without it, the task runs from `C:\Windows\System32` and cannot find `appsettings.json`.

---

## Project Structure

```
RpacCommercial/
├── App_Start/              # MVC route, bundle, filter config
├── Company.DailyReporter/  # Standalone daily email reporter (NET 8)
├── Content/                # CSS and static assets
├── Controllers/            # MVC controllers
├── Helpers/                # Email scheduler, session, auth helpers
├── Models/                 # Domain models
├── Repositories/           # Data access layer (Dapper)
├── Scripts/                # JS libraries
├── Views/                  # Razor views
├── RpacCommercialDB.sql    # Database creation script
└── web.config              # App configuration
```

---

## Notes

- `appsettings.json` (reporter credentials) is excluded from source control — use `appsettings.example.json` as a template
- Uploaded documents in `Uploads/` are excluded from source control
- The `Uploads/Imports/` folder must exist and be writable on the deployment server
