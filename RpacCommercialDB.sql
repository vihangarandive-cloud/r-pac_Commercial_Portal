-- RCOMM.sql
-- Create and seed the database for the Rpac Commercial Web Application

USE [master];
GO

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'RCOMM')
BEGIN
    CREATE DATABASE [RCOMM];
END
GO

USE [RCOMM];
GO

-- 1. Roles Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Roles]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Roles](
        [RoleId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [RoleName] [varchar](50) NOT NULL UNIQUE
    );
END
GO

-- 2. Users Table (Plain text passwords as requested)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Users]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Users](
        [UserId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [FullName] [varchar](100) NOT NULL,
        [Username] [varchar](50) NOT NULL UNIQUE,
        [Password] [varchar](256) NOT NULL, -- Plain text password
        [RoleId] [int] NOT NULL FOREIGN KEY REFERENCES [dbo].[Roles]([RoleId]),
        [IsActive] [bit] NOT NULL DEFAULT 1,
        [CreatedAt] [datetime] NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 3. Suppliers Table (dropdown source)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Suppliers]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Suppliers](
        [SupplierId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SupplierName] [varchar](150) NOT NULL UNIQUE,
        [ContactPerson] [varchar](100) NULL,
        [Email] [varchar](100) NULL,
        [IsActive] [bit] NOT NULL DEFAULT 1,
        [CreatedAt] [datetime] NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 4. Imports Table (replicates all flowchart nodes)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Imports]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Imports](
        [ImportId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [ShipmentDate] [datetime] NOT NULL,
        [PONumber] [varchar](50) NOT NULL,
        [SupplierId] [int] NOT NULL FOREIGN KEY REFERENCES [dbo].[Suppliers]([SupplierId]),
        [ShippingMode] [varchar](20) NOT NULL, -- Air, Sea, Courier
        [Incoterm] [varchar](20) NOT NULL, -- FOB, CIF, Ex Work, C&F, DAP
        [WeightValue] [decimal](18, 2) NOT NULL,
        [WeightUnit] [varchar](10) NOT NULL, -- Kg, CBM
        [MaterialCategory] [varchar](500) NULL,
        [ScanwellQtyPath] [varchar](255) NULL,
        [ScanwellQtyFileName] [varchar](150) NULL,
        [ScanwellQtyContent] [varbinary](max) NULL,
        [FGXPath] [varchar](255) NULL,
        [FGXFileName] [varchar](150) NULL,
        [FGXContent] [varbinary](max) NULL,
        [TranswingPath] [varchar](255) NULL,
        [TranswingFileName] [varchar](150) NULL,
        [TranswingContent] [varbinary](max) NULL,
        [InsurancePath] [varchar](255) NULL,
        [InsuranceFileName] [varchar](150) NULL,
        [InsuranceContent] [varbinary](max) NULL,
        [ETD] [datetime] NULL,
        [ETA] [datetime] NULL,
        [ActualFreightCost] [decimal](18, 2) NULL,
        [ActualFreightCostForwarder] [varchar](50) NULL,
        [ClearanceDate] [datetime] NULL,
        [ApproximateClearanceAmount] [decimal](18, 2) NULL,
        [ActualClearanceAmount] [decimal](18, 2) NULL,
        [Remarks] [varchar](max) NULL,
        [Status] [varchar](20) NOT NULL DEFAULT 'Pending Clearance', -- Pending Clearance, Cleared
        [ApprovedBy] [int] NULL FOREIGN KEY REFERENCES [dbo].[Users]([UserId]),
        [ApprovedAt] [datetime] NULL,
        [CreatedBy] [int] NOT NULL FOREIGN KEY REFERENCES [dbo].[Users]([UserId]),
        [CreatedAt] [datetime] NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 5. EmailLogs Table (Ensures no duplicate morning reports on AppPool restarts)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EmailLogs]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[EmailLogs](
        [SentDate] [date] NOT NULL PRIMARY KEY,
        [SentAt] [datetime] NOT NULL
    );
END
GO

-- 6. AuditLog Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AuditLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[AuditLog](
        [LogId] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [UserId] [int] NULL FOREIGN KEY REFERENCES [dbo].[Users]([UserId]),
        [Action] [varchar](100) NOT NULL,
        [TableAffected] [varchar](100) NULL,
        [RecordId] [int] NULL,
        [Timestamp] [datetime] NOT NULL DEFAULT GETDATE()
    );
END
GO

-- Seed Data Insertion

-- Seed Roles
IF NOT EXISTS (SELECT * FROM [dbo].[Roles])
BEGIN
    INSERT INTO [dbo].[Roles] ([RoleName]) VALUES ('Admin');
    INSERT INTO [dbo].[Roles] ([RoleName]) VALUES ('Management');
    INSERT INTO [dbo].[Roles] ([RoleName]) VALUES ('Commercial User');
END
GO

-- Seed Users (Plain text password: admin123, manager123, user123)
IF NOT EXISTS (SELECT * FROM [dbo].[Users])
BEGIN
    DECLARE @AdminRoleId INT = (SELECT [RoleId] FROM [dbo].[Roles] WHERE [RoleName] = 'Admin');
    DECLARE @ManagerRoleId INT = (SELECT [RoleId] FROM [dbo].[Roles] WHERE [RoleName] = 'Management');
    DECLARE @UserRoleId INT = (SELECT [RoleId] FROM [dbo].[Roles] WHERE [RoleName] = 'Commercial User');

    INSERT INTO [dbo].[Users] ([FullName], [Username], [Password], [RoleId])
    VALUES ('System Administrator', 'admin', 'admin123', @AdminRoleId);

    INSERT INTO [dbo].[Users] ([FullName], [Username], [Password], [RoleId])
    VALUES ('Management Manager', 'manager', 'manager123', @ManagerRoleId);

    INSERT INTO [dbo].[Users] ([FullName], [Username], [Password], [RoleId])
    VALUES ('Commercial Officer', 'user', 'user123', @UserRoleId);
END
GO

-- Seed Suppliers
IF NOT EXISTS (SELECT * FROM [dbo].[Suppliers])
BEGIN
    INSERT INTO [dbo].[Suppliers] ([SupplierName], [ContactPerson], [Email])
    VALUES ('Scanwell Logistics Pvt Ltd', 'John Doe', 'scanwell@rpac.com');

    INSERT INTO [dbo].[Suppliers] ([SupplierName], [ContactPerson], [Email])
    VALUES ('FGX Worldwide Inc.', 'Jane Smith', 'fgx@rpac.com');

    INSERT INTO [dbo].[Suppliers] ([SupplierName], [ContactPerson], [Email])
    VALUES ('Transwing Shipping Agent', 'Alan Walker', 'transwing@rpac.com');

    INSERT INTO [dbo].[Suppliers] ([SupplierName], [ContactPerson], [Email])
    VALUES ('Apex Cargo Lanka', 'Robert Brown', 'apex@rpac.com');

    INSERT INTO [dbo].[Suppliers] ([SupplierName], [ContactPerson], [Email])
    VALUES ('Expeditors Lanka Ltd', 'Lisa Ann', 'expeditors@rpac.com');
END
GO

-- Seed Shipments (Approved, Pending, and Rejected samples to make the dashboard alive instantly)
IF NOT EXISTS (SELECT * FROM [dbo].[Imports])
BEGIN
    DECLARE @AdminUserId INT = (SELECT [UserId] FROM [dbo].[Users] WHERE [Username] = 'admin');
    DECLARE @ManagerUserId INT = (SELECT [UserId] FROM [dbo].[Users] WHERE [Username] = 'manager');
    DECLARE @OfficerUserId INT = (SELECT [UserId] FROM [dbo].[Users] WHERE [Username] = 'user');

    DECLARE @ScanwellId INT = (SELECT [SupplierId] FROM [dbo].[Suppliers] WHERE [SupplierName] = 'Scanwell Logistics Pvt Ltd');
    DECLARE @FgxId INT = (SELECT [SupplierId] FROM [dbo].[Suppliers] WHERE [SupplierName] = 'FGX Worldwide Inc.');
    DECLARE @TranswingId INT = (SELECT [SupplierId] FROM [dbo].[Suppliers] WHERE [SupplierName] = 'Transwing Shipping Agent');

    -- 1. Approved Sea Shipment
    INSERT INTO [dbo].[Imports] (
        [ShipmentDate], [PONumber], [SupplierId], [ShippingMode], [Incoterm], 
        [WeightValue], [WeightUnit], [ETD], [ETA], [ActualFreightCost], 
        [ClearanceDate], [ApproximateClearanceAmount], [ActualClearanceAmount], 
        [Remarks], [Status], [ApprovedBy], [ApprovedAt], [CreatedBy], [CreatedAt]
    ) VALUES (
        DATEADD(day, -5, GETDATE()), 'PO-2026-0001', @ScanwellId, 'Sea', 'FOB', 
        15.5, 'CBM', DATEADD(day, -4, GETDATE()), DATEADD(day, 10, GETDATE()), 450000.00, 
        DATEADD(day, -1, GETDATE()), 120000.00, 125000.00, 
        'Bulk packaging shipment approved by management.', 'Cleared', @ManagerUserId, DATEADD(day, -1, GETDATE()), @OfficerUserId, DATEADD(day, -5, GETDATE())
    );

    -- 2. Pending Air Shipment
    INSERT INTO [dbo].[Imports] (
        [ShipmentDate], [PONumber], [SupplierId], [ShippingMode], [Incoterm], 
        [WeightValue], [WeightUnit], [ETD], [ETA], [ActualFreightCost], 
        [ApproximateClearanceAmount], [Remarks], [Status], [CreatedBy], [CreatedAt]
    ) VALUES (
        DATEADD(day, -2, GETDATE()), 'PO-2026-0002', @FgxId, 'Air', 'CIF', 
        280.00, 'Kg', DATEADD(day, -1, GETDATE()), DATEADD(day, 2, GETDATE()), 180000.00, 
        75000.00, 'Urgent trim labels shipment.', 'Pending Clearance', @OfficerUserId, DATEADD(day, -2, GETDATE())
    );

    -- 3. Another Pending Clearance Shipment
    INSERT INTO [dbo].[Imports] (
        [ShipmentDate], [PONumber], [SupplierId], [ShippingMode], [Incoterm], 
        [WeightValue], [WeightUnit], [ETD], [ETA], [ActualFreightCost], 
        [Remarks], [Status], [CreatedBy], [CreatedAt]
    ) VALUES (
        DATEADD(day, -6, GETDATE()), 'PO-2026-0003', @TranswingId, 'Courier', 'Ex Work', 
        45.00, 'Kg', DATEADD(day, -5, GETDATE()), DATEADD(day, -2, GETDATE()), 35000.00, 
        'Weight discrepancy found on invoice.', 'Pending Clearance', @OfficerUserId, DATEADD(day, -6, GETDATE())
    );
END
GO
