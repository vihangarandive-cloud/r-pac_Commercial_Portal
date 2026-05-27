// /Models/ImportShipment.cs
using System;

namespace RpacCommercial.Models
{
    public class ImportShipment
    {
        public int ImportId { get; set; }
        
        // 1. Date (Date shipment was logged / created)
        public DateTime ShipmentDate { get; set; }
        
        // 2. PO Number
        public string PONumber { get; set; }
        
        // 3. Supplier (Dropdown)
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } // Display property joined from Suppliers table
        
        // 4. Shipping Mode (Dropdown) [Air/Sea/Courier]
        public string ShippingMode { get; set; }
        
        // 5. Incoterm (Dropdown) [FOB/CIF/Ex Work/C&F/DAP]
        public string Incoterm { get; set; }
        
        // 6. Weight (Dropdown) [Kg/CBM] - Value and Unit
        public decimal WeightValue { get; set; }
        public string WeightUnit { get; set; } // 'Kg' or 'CBM'
        
        // Material Category (Multi-select saved as comma-separated string)
        public string MaterialCategory { get; set; }
        
        // 7. Scanwell Qty (Email attachment)
        public string ScanwellQtyPath { get; set; }
        public string ScanwellQtyFileName { get; set; }
        
        // 8. FGX (Email attachment)
        public string FGXPath { get; set; }
        public string FGXFileName { get; set; }
        
        // 9. Transwing (Email attachment)
        public string TranswingPath { get; set; }
        public string TranswingFileName { get; set; }
        
        // Insurance (Email attachment)
        public string InsurancePath { get; set; }
        public string InsuranceFileName { get; set; }
        
        // 10. ETD (date)
        public DateTime ETD { get; set; }
        
        // 11. ETA (date)
        public DateTime ETA { get; set; }
        
        // 12. Actual Freight Cost (Type)
        public decimal? ActualFreightCost { get; set; }
        public string ActualFreightCostForwarder { get; set; }
        
        // 12b. Approximate Freight Cost (Type)
        public decimal? ApproximateFreightCost { get; set; }
        
        // 13. Clearance Date (date)
        public DateTime? ClearanceDate { get; set; }
        
        // 14. Approximate Clearance Amount (Type)
        public decimal? ApproximateClearanceAmount { get; set; }
        
        // 15. Actual Clearance Amount (Type)
        public decimal? ActualClearanceAmount { get; set; }
        
        // 16. Remarks (type)
        public string Remarks { get; set; }
        
        // 17. Status & Management Approval
        public string Status { get; set; } // 'Pending', 'Approved', 'Rejected'
        public int? ApprovedBy { get; set; }
        public string ApprovedByFullName { get; set; } // Joined from Users table for display
        public DateTime? ApprovedAt { get; set; }
        
        public int CreatedBy { get; set; }
        public string CreatedByFullName { get; set; } // Joined from Users table for display
        public DateTime CreatedAt { get; set; }
    }
}
