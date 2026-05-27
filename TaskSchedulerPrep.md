# Windows Task Scheduler & Deployment Guide

This guide describes how to publish the **Company.DailyReporter** console utility as a self-contained Windows executable and configure it inside the Windows Task Scheduler ecosystem for automated, off-thread daily email reports.

---

## 1. Publishing as a Self-Contained Executable

To ensure the console utility runs reliably on any Windows server without requiring a pre-installed .NET runtime, publish it as a **Self-Contained (win-x64)** executable. This bundles the core .NET runtime, Dapper, MailKit, and EPPlus directly into the published output folder.

Open your command line in the project folder and run the following command:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

### Command Flags Decoded:
* `-c Release`: Compiles the application in release mode with optimizations.
* `-r win-x64`: Targets 64-bit Windows environments.
* `--self-contained true`: Bundles the .NET runtime with the application.
* `-p:PublishSingleFile=true`: Packages the application binaries into a single executable file (`Company.DailyReporter.exe`).
* `-p:PublishReadyToRun=true`: Compiles assemblies at publish time to improve startup performance.

The output executable and its configuration file (`appsettings.json`) will be generated in:
`.\Company.DailyReporter\bin\Release\net8.0\win-x64\publish\`

---

## 2. Deploying to the Server

1. **Create Directory**: Create a production directory on your target server, for example:
   `C:\CompanyReports\DailyReporter\`
2. **Copy Files**: Copy all files from the publish folder (`win-x64\publish`) into your production directory.
   Ensure that:
   - `Company.DailyReporter.exe` is present.
   - `appsettings.json` is present.
3. **Configure Settings**: Open `appsettings.json` in the production directory and modify the credentials, connection strings, and the temporary report directory (`C:\CompanyReports\Temp\`) to match your server environment.

---

## 3. Configuring Windows Task Scheduler

To execute this report automatically every morning (e.g., at 6:00 AM), set up a scheduled task on your Windows Server:

### Step-by-Step Task Configuration:

1. **Open Task Scheduler**:
   Press `Win + R`, type `taskschd.msc`, and press **Enter**.

2. **Create a New Task**:
   Click **Create Task...** on the right side panel (do *not* use "Create Basic Task" to ensure full control over privileges).

3. **General Tab**:
   - **Name**: `Company.DailyReporter`
   - **Description**: `Automated daily reporting utility that generates and emails yesterday's cleared shipments report.`
   - **Security options**: 
     - Configure it to run under a Service Account or an administrator account with access to the target SQL database and network permissions.
     - Select **Run whether user is logged on or not**.
     - Check **Run with highest privileges** (crucial for write operations in system folders if required).

4. **Triggers Tab**:
   - Click **New...**.
   - **Begin the task**: `On a schedule`
   - **Settings**: `Daily`
   - **Start**: Set the date and the time to `06:00:00 AM` (or your preferred morning trigger slot).
   - **Recur every**: `1` day.
   - Click **OK**.

5. **Actions Tab**:
   - Click **New...**.
   - **Action**: `Start a program`
   - **Program/script**: Browse and select the absolute path to your executable, for example:
     `C:\CompanyReports\DailyReporter\Company.DailyReporter.exe`
   - **Start in (optional)**: 
     > [!IMPORTANT]
     > You **MUST** fill this field with the absolute path of the directory containing your executable (do *not* wrap it in quotes):
     > `C:\CompanyReports\DailyReporter`
     >
     > **Why is this critical?** 
     > When Windows Task Scheduler fires an action, its default working directory is `C:\Windows\System32`. If the "Start in" field is left blank, the application will search for `appsettings.json` in `System32` instead of its deployment directory. This will lead to connection string parsing failures and program crashes.

6. **Settings Tab**:
   - Check **Allow task to be run on demand** (useful for immediate manual testing).
   - Check **If the running task does not end on request, force it to stop**.
   - Set **Stop the task if it runs longer than**: `1 hour` (to prevent hung SMTP connections from blocking resources permanently).
   - Click **OK**.

7. **Save & Authorize**:
   You will be prompted to enter the password for the configured service/user account to securely store the task.

---

## 4. Troubleshooting and Verification

* **Run on Demand**: Right-click the newly created task in Task Scheduler and click **Run**.
* **Check Logs**:
  - The application outputs details to the console which are written to Task Scheduler's history.
  - Check the configured temp directory (`C:\CompanyReports\Temp\`) to verify the timestamped file `DailyReport_yyyyMMdd.xlsx` was created and successfully deleted.
  - Review your email inbox for the styled HTML message with the attached spreadsheet.
* **Error Codes**:
  - `0x0`: Task completed successfully.
  - `0x1` / `0x80070002`: File not found or working directory ("Start in") path is misconfigured.
