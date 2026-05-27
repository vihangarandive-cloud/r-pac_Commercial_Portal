// /Models/AuditLog.cs
using System;

namespace RpacCommercial.Models
{
    public class AuditLog
    {
        public int LogId { get; set; }
        public int? UserId { get; set; }
        public string UserFullName { get; set; } // Joined from Users table for display
        public string Action { get; set; }
        public string TableAffected { get; set; }
        public int? RecordId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
