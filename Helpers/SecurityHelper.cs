using System;
using System.Security.Cryptography;
using System.Linq;

namespace RpacCommercial.Helpers
{
    public static class SecurityHelper
    {
        private const int SaltSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 10000;

        public static string HashPassword(string password)
        {
            return password; // Plain text as requested
        }

        public static bool VerifyPassword(string password, string storedPassword)
        {
            if (password == null || storedPassword == null) return false;
            return password == storedPassword; // Direct plain text comparison
        }
    }
}

