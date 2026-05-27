// /Models/UserAccount.cs
using System;

namespace RpacCommercial.Models
{
    public class UserAccount
    {
        public int UserId { get; set; }
        public string FullName { get; set; }
        public string Username { get; set; }
        public string Password { get; set; } // Plain text password
        public int RoleId { get; set; }
        public string RoleName { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
