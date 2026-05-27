// /Global.asax.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using RpacCommercial.App_Start;

namespace RpacCommercial
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            // Register Custom Dependency Resolver
            DependencyResolver.SetResolver(new App_Start.SimpleDependencyResolver());

            // Start Morning Outlook Email Scheduler
            Helpers.EmailScheduler.Start();
        }

        protected void Application_End()
        {
            Helpers.EmailScheduler.Stop();
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            var exception = Server.GetLastError();
            
            // Traverse the exception chain to see if HttpAntiForgeryException is present at any depth
            bool isAntiForgeryException = false;
            var currentEx = exception;
            while (currentEx != null)
            {
                if (currentEx is HttpAntiForgeryException)
                {
                    isAntiForgeryException = true;
                    break;
                }
                currentEx = currentEx.InnerException;
            }
            
            if (isAntiForgeryException)
            {
                Server.ClearError();
                
                // Perform a clean sign-out and clear session
                System.Web.Security.FormsAuthentication.SignOut();
                if (Context.Session != null)
                {
                    Context.Session.Clear();
                    Context.Session.Abandon();
                }
                
                // Redirect to Login page cleanly
                Response.Redirect("~/Account/Login");
            }
        }
    }
}

