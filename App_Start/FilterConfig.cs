// /App_Start/FilterConfig.cs
using System.Web;
using System.Web.Mvc;

namespace RpacCommercial.App_Start
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
        }
    }
}

