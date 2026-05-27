using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace RpacCommercial.Helpers
{
    public static class MSSQLHelper
    {
        private static readonly string ConnectionString = ConfigurationManager.ConnectionStrings["MSSQLServer"].ConnectionString;

        public static IDbConnection GetConnection()
        {
            var connection = new SqlConnection(ConnectionString);
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }
            return connection;
        }
    }
}

