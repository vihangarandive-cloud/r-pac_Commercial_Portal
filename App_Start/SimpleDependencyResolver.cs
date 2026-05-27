using System;
using System.Collections.Generic;
using System.Web.Mvc;
using RpacCommercial.Repositories;
using RpacCommercial.Controllers;

namespace RpacCommercial.App_Start
{
    public class SimpleDependencyResolver : IDependencyResolver
    {
        private readonly Dictionary<Type, Func<object>> _registrations = new Dictionary<Type, Func<object>>();

        public SimpleDependencyResolver()
        {
            // 1. Repositories (Lazy initialization)
            var userRepo = new Lazy<IUserRepository>(() => new UserRepository());
            var supplierRepo = new Lazy<ISupplierRepository>(() => new SupplierRepository());
            var importRepo = new Lazy<IImportRepository>(() => new ImportRepository());

            _registrations[typeof(IUserRepository)] = () => userRepo.Value;
            _registrations[typeof(ISupplierRepository)] = () => supplierRepo.Value;
            _registrations[typeof(IImportRepository)] = () => importRepo.Value;

            // 2. Controllers
            _registrations[typeof(AccountController)] = () => new AccountController(userRepo.Value);
            _registrations[typeof(DashboardController)] = () => new DashboardController(importRepo.Value);
            _registrations[typeof(ImportController)] = () => new ImportController(importRepo.Value, supplierRepo.Value);
            _registrations[typeof(ExportController)] = () => new ExportController();
            _registrations[typeof(ReportsController)] = () => new ReportsController(importRepo.Value, supplierRepo.Value);
            _registrations[typeof(MasterDataController)] = () => new MasterDataController(supplierRepo.Value, userRepo.Value);
        }

        public object GetService(Type serviceType)
        {
            if (_registrations.ContainsKey(serviceType))
            {
                return _registrations[serviceType]();
            }

            try
            {
                return Activator.CreateInstance(serviceType);
            }
            catch
            {
                return null;
            }
        }

        public IEnumerable<object> GetServices(Type serviceType)
        {
            return new List<object>();
        }
    }
}
