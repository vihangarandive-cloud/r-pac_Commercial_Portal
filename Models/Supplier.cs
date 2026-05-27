// /Models/Supplier.cs
using System;

namespace RpacCommercial.Models
{
    public class Supplier
    {
        public int SupplierId { get; set; }
        public string BPCode { get; set; }
        public string SupplierName { get; set; }
        public string ContactPerson { get; set; }
        public string Email { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
