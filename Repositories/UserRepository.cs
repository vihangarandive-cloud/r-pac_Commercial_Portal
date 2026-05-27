// /Repositories/UserRepository.cs
using System.Collections.Generic;
using System.Data;
using Dapper;
using RpacCommercial.Helpers;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public class UserRepository : IUserRepository
    {
        public UserAccount ValidateUser(string username, string password)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                // In plan text as requested
                string sql = @"SELECT u.*, 
                               CASE WHEN r.RoleName IS NULL OR r.RoleName = '' THEN 'Commercial User' ELSE r.RoleName END as RoleName 
                               FROM Users u WITH (NOLOCK) 
                               LEFT JOIN Roles r ON u.RoleId = r.RoleId 
                               WHERE u.Username = @Username AND u.Password = @Password AND u.IsActive = 1";
                var user = conn.QueryFirstOrDefault<UserAccount>(sql, new { Username = username, Password = password });
                return user;
            }
        }

        public UserAccount GetUserByUsername(string username)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "SELECT * FROM Users WITH (NOLOCK) WHERE Username = @Username";
                var user = conn.QueryFirstOrDefault<UserAccount>(sql, new { Username = username });
                if (user != null)
                {
                    user.RoleName = conn.ExecuteScalar<string>("SELECT RoleName FROM Roles WITH (NOLOCK) WHERE RoleId = @RoleId", new { RoleId = user.RoleId });
                }
                return user;
            }
        }

        public IEnumerable<UserAccount> GetAllUsers()
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"SELECT 
                                 u.UserId, u.FullName, u.Username, u.RoleId, u.IsActive, u.Password,
                                 CASE WHEN r.RoleName IS NULL OR r.RoleName = '' THEN 'Commercial User' ELSE r.RoleName END as RoleName 
                               FROM Users u WITH (NOLOCK)
                               LEFT JOIN Roles r WITH (NOLOCK) ON u.RoleId = r.RoleId";
                return conn.Query<UserAccount>(sql);
            }
        }

        public void AddUser(UserAccount user)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"INSERT INTO Users (FullName, Username, Password, RoleId, IsActive) 
                               VALUES (@FullName, @Username, @Password, @RoleId, 1)";
                conn.Execute(sql, user);
            }
        }

        public void UpdateUser(UserAccount user)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"UPDATE Users SET FullName = @FullName, RoleId = @RoleId, Password = @Password WHERE Username = @Username";
                conn.Execute(sql, user);
            }
        }

        public void ChangePassword(string username, string newPassword)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "UPDATE Users SET Password = @Password WHERE Username = @Username";
                conn.Execute(sql, new { Password = newPassword, Username = username });
            }
        }

        public void ToggleStatus(string username)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "UPDATE Users SET IsActive = 1 - IsActive WHERE Username = @Username";
                conn.Execute(sql, new { Username = username });
            }
        }
    }
}
