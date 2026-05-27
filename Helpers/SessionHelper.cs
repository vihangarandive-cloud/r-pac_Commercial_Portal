// /Helpers/SessionHelper.cs
using System.Web;

namespace RpacCommercial.Helpers
{
    public static class SessionHelper
    {
        public static int UserId
        {
            get 
            { 
                if (HttpContext.Current == null) return 0;
                
                if (HttpContext.Current.Session != null)
                {
                    if (HttpContext.Current.Session["UserId"] != null)
                    {
                        return (int)HttpContext.Current.Session["UserId"];
                    }
                    if (HttpContext.Current.Items["UserId"] != null)
                    {
                        var val = (int)HttpContext.Current.Items["UserId"];
                        HttpContext.Current.Session["UserId"] = val;
                        return val;
                    }
                }
                else if (HttpContext.Current.Items["UserId"] != null)
                {
                    return (int)HttpContext.Current.Items["UserId"];
                }
                
                // Try to recover session if user is logged in via FormsAuthentication
                if (RestoreSessionIfAuthenticated())
                {
                    if (HttpContext.Current.Session != null && HttpContext.Current.Session["UserId"] != null)
                    {
                        return (int)HttpContext.Current.Session["UserId"];
                    }
                    if (HttpContext.Current.Items["UserId"] != null)
                    {
                        return (int)HttpContext.Current.Items["UserId"];
                    }
                }
                return 0;
            }
            set 
            { 
                if (HttpContext.Current != null)
                {
                    if (HttpContext.Current.Session != null) HttpContext.Current.Session["UserId"] = value;
                    HttpContext.Current.Items["UserId"] = value;
                }
            }
        }

        public static string Username
        {
            get 
            { 
                if (HttpContext.Current == null) return null;
                
                if (HttpContext.Current.Session != null)
                {
                    if (HttpContext.Current.Session["Username"] != null)
                    {
                        return HttpContext.Current.Session["Username"].ToString();
                    }
                    if (HttpContext.Current.Items["Username"] != null)
                    {
                        var val = HttpContext.Current.Items["Username"].ToString();
                        HttpContext.Current.Session["Username"] = val;
                        return val;
                    }
                }
                else if (HttpContext.Current.Items["Username"] != null)
                {
                    return HttpContext.Current.Items["Username"].ToString();
                }
                
                // Try to recover session if user is logged in via FormsAuthentication
                if (RestoreSessionIfAuthenticated())
                {
                    if (HttpContext.Current.Session != null && HttpContext.Current.Session["Username"] != null)
                    {
                        return HttpContext.Current.Session["Username"].ToString();
                    }
                    if (HttpContext.Current.Items["Username"] != null)
                    {
                        return HttpContext.Current.Items["Username"].ToString();
                    }
                }
                return null;
            }
            set 
            { 
                if (HttpContext.Current != null)
                {
                    if (HttpContext.Current.Session != null) HttpContext.Current.Session["Username"] = value;
                    HttpContext.Current.Items["Username"] = value;
                }
            }
        }

        public static string FullName
        {
            get 
            { 
                if (HttpContext.Current == null) return null;
                
                if (HttpContext.Current.Session != null)
                {
                    if (HttpContext.Current.Session["FullName"] != null)
                    {
                        return HttpContext.Current.Session["FullName"].ToString();
                    }
                    if (HttpContext.Current.Items["FullName"] != null)
                    {
                        var val = HttpContext.Current.Items["FullName"].ToString();
                        HttpContext.Current.Session["FullName"] = val;
                        return val;
                    }
                }
                else if (HttpContext.Current.Items["FullName"] != null)
                {
                    return HttpContext.Current.Items["FullName"].ToString();
                }
                
                // Try to recover session if user is logged in via FormsAuthentication
                if (RestoreSessionIfAuthenticated())
                {
                    if (HttpContext.Current.Session != null && HttpContext.Current.Session["FullName"] != null)
                    {
                        return HttpContext.Current.Session["FullName"].ToString();
                    }
                    if (HttpContext.Current.Items["FullName"] != null)
                    {
                        return HttpContext.Current.Items["FullName"].ToString();
                    }
                }
                return null;
            }
            set 
            { 
                if (HttpContext.Current != null)
                {
                    if (HttpContext.Current.Session != null) HttpContext.Current.Session["FullName"] = value;
                    HttpContext.Current.Items["FullName"] = value;
                }
            }
        }

        public static string UserRole
        {
            get 
            { 
                if (HttpContext.Current == null) return null;
                
                string role = null;
                if (HttpContext.Current.Session != null)
                {
                    if (HttpContext.Current.Session["UserRole"] != null)
                    {
                        role = HttpContext.Current.Session["UserRole"].ToString();
                    }
                    else if (HttpContext.Current.Items["UserRole"] != null)
                    {
                        role = HttpContext.Current.Items["UserRole"].ToString();
                        HttpContext.Current.Session["UserRole"] = role;
                    }
                }
                else if (HttpContext.Current.Items["UserRole"] != null)
                {
                    role = HttpContext.Current.Items["UserRole"].ToString();
                }
                
                if (role == null)
                {
                    // Try to recover session if user is logged in via FormsAuthentication
                    if (RestoreSessionIfAuthenticated())
                    {
                        if (HttpContext.Current.Session != null && HttpContext.Current.Session["UserRole"] != null)
                        {
                            role = HttpContext.Current.Session["UserRole"].ToString();
                        }
                        else if (HttpContext.Current.Items["UserRole"] != null)
                        {
                            role = HttpContext.Current.Items["UserRole"].ToString();
                        }
                    }
                }
                
                if (role != null)
                {
                    if (role.Equals("Planner", System.StringComparison.OrdinalIgnoreCase)) return "Production Planner";
                    if (role.Equals("Management", System.StringComparison.OrdinalIgnoreCase)) return "Manager";
                }
                return role;
            }
            set 
            { 
                if (HttpContext.Current != null)
                {
                    string role = value;
                    if (role != null)
                    {
                        if (role.Equals("Planner", System.StringComparison.OrdinalIgnoreCase)) role = "Production Planner";
                        if (role.Equals("Management", System.StringComparison.OrdinalIgnoreCase)) role = "Manager";
                    }
                    
                    if (HttpContext.Current.Session != null) HttpContext.Current.Session["UserRole"] = role; 
                    HttpContext.Current.Items["UserRole"] = role;
                }
            }
        }

        public static void Clear()
        {
            if (HttpContext.Current != null)
            {
                if (HttpContext.Current.Session != null)
                {
                    HttpContext.Current.Session.Clear();
                    HttpContext.Current.Session.Abandon();
                }
                HttpContext.Current.Items.Clear();
            }
        }

        private static bool RestoreSessionIfAuthenticated()
        {
            if (HttpContext.Current == null) return false;
            
            if (HttpContext.Current.User != null && HttpContext.Current.User.Identity.IsAuthenticated)
            {
                string username = HttpContext.Current.User.Identity.Name;
                if (!string.IsNullOrEmpty(username))
                {
                    try
                    {
                        var repo = System.Web.Mvc.DependencyResolver.Current.GetService(typeof(Repositories.IUserRepository)) as Repositories.IUserRepository;
                        if (repo != null)
                        {
                            var user = repo.GetUserByUsername(username);
                            if (user != null)
                            {
                                if (HttpContext.Current.Session != null)
                                {
                                    HttpContext.Current.Session["UserId"] = user.UserId;
                                    HttpContext.Current.Session["Username"] = user.Username;
                                    HttpContext.Current.Session["FullName"] = user.FullName;
                                    
                                    string role = user.RoleName;
                                    if (role != null)
                                    {
                                        if (role.Equals("Planner", System.StringComparison.OrdinalIgnoreCase)) role = "Production Planner";
                                        if (role.Equals("Management", System.StringComparison.OrdinalIgnoreCase)) role = "Manager";
                                    }
                                    HttpContext.Current.Session["UserRole"] = role;
                                }
                                
                                HttpContext.Current.Items["UserId"] = user.UserId;
                                HttpContext.Current.Items["Username"] = user.Username;
                                HttpContext.Current.Items["FullName"] = user.FullName;
                                
                                string roleItems = user.RoleName;
                                if (roleItems != null)
                                {
                                    if (roleItems.Equals("Planner", System.StringComparison.OrdinalIgnoreCase)) roleItems = "Production Planner";
                                    if (roleItems.Equals("Management", System.StringComparison.OrdinalIgnoreCase)) roleItems = "Manager";
                                }
                                HttpContext.Current.Items["UserRole"] = roleItems;
                                
                                return true;
                            }
                        }
                    }
                    catch {}
                }
            }
            return false;
        }
    }
}
