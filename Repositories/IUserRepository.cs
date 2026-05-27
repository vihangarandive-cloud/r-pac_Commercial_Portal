// /Repositories/IUserRepository.cs
using System.Collections.Generic;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public interface IUserRepository
    {
        UserAccount ValidateUser(string username, string password);
        UserAccount GetUserByUsername(string username);
        IEnumerable<UserAccount> GetAllUsers();
        void AddUser(UserAccount user);
        void UpdateUser(UserAccount user);
        void ChangePassword(string username, string newPassword);
        void ToggleStatus(string username);
    }
}
