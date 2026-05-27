// /Repositories/ISupplierRepository.cs
using System.Collections.Generic;
using RpacCommercial.Models;

namespace RpacCommercial.Repositories
{
    public interface ISupplierRepository
    {
        IEnumerable<Supplier> GetAllSuppliers(bool activeOnly = false);
        Supplier GetSupplierById(int supplierId);
        void AddSupplier(Supplier supplier);
        void UpdateSupplier(Supplier supplier);
        void ToggleStatus(int supplierId);
    }
}
