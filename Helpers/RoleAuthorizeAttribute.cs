// /Helpers/RoleAuthorizeAttribute.cs
using System;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;

namespace RpacCommercial.Helpers
{
    public class RoleAuthorizeAttribute : AuthorizeAttribute
    {
        private readonly string[] _targetRoles;

        public RoleAuthorizeAttribute(params string[] roles)
        {
            _targetRoles = roles;
        }

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            if (!httpContext.User.Identity.IsAuthenticated) return false;

            var userRole = SessionHelper.UserRole;
            
            // If session is lost but user is authenticated, try to restore session
            if (string.IsNullOrEmpty(userRole))
            {
                var username = httpContext.User.Identity.Name;
                var userRepo = new RpacCommercial.Repositories.UserRepository();
                var user = userRepo.GetUserByUsername(username);
                if (user != null)
                {
                    SessionHelper.UserId = user.UserId;
                    SessionHelper.Username = user.Username;
                    SessionHelper.FullName = user.FullName;
                    SessionHelper.UserRole = user.RoleName;
                    userRole = SessionHelper.UserRole;
                }
                else
                {
                    return false;
                }
            }

            // Admin or Management always has full access
            if (userRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) || 
                userRole.Equals("Management", StringComparison.OrdinalIgnoreCase) ||
                userRole.Equals("Manager", StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var role in _targetRoles)
            {
                if (userRole.Equals(role, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            filterContext.Result = new RedirectToRouteResult(new RouteValueDictionary(new { controller = "Account", action = "Login" }));
        }
    }
}

