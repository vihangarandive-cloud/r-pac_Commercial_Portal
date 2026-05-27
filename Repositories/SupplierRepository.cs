// /Repositories/SupplierRepository.cs
using System.Collections.Generic;
using System.Data;
using Dapper;
using RpacCommercial.Helpers;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public class SupplierRepository : ISupplierRepository
    {
        public IEnumerable<Supplier> GetAllSuppliers(bool activeOnly = false)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "SELECT * FROM Suppliers WITH (NOLOCK)";
                if (activeOnly)
                {
                    sql += " WHERE IsActive = 1";
                }
                sql += " ORDER BY SupplierId ASC";
                return conn.Query<Supplier>(sql);
            }
        }

        public Supplier GetSupplierById(int supplierId)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "SELECT * FROM Suppliers WITH (NOLOCK) WHERE SupplierId = @SupplierId";
                return conn.QueryFirstOrDefault<Supplier>(sql, new { SupplierId = supplierId });
            }
        }

        public void AddSupplier(Supplier supplier)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"INSERT INTO Suppliers (BPCode, SupplierName, ContactPerson, Email, IsActive) 
                               VALUES (@BPCode, @SupplierName, @ContactPerson, @Email, 1)";
                conn.Execute(sql, supplier);
            }
        }

        public void UpdateSupplier(Supplier supplier)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = @"UPDATE Suppliers 
                               SET BPCode = @BPCode, SupplierName = @SupplierName, ContactPerson = @ContactPerson, Email = @Email 
                               WHERE SupplierId = @SupplierId";
                conn.Execute(sql, supplier);
            }
        }

        public void ToggleStatus(int supplierId)
        {
            using (var conn = MSSQLHelper.GetConnection())
            {
                string sql = "UPDATE Suppliers SET IsActive = 1 - IsActive WHERE SupplierId = @SupplierId";
                conn.Execute(sql, new { SupplierId = supplierId });
            }
        }
    }
}
