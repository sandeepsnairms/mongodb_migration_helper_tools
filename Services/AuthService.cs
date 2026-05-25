using Microsoft.Extensions.Configuration;

namespace MongoTestTools.Services
{
    public class AuthService
    {
        private readonly IConfiguration _configuration;
        private bool _isAuthenticated = false;

        public AuthService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool IsAuthenticated => _isAuthenticated;

        public bool Login(string password)
        {
            var configPassword = _configuration["APP_PASSWORD"] ?? Environment.GetEnvironmentVariable("APP_PASSWORD");
            
            if (string.IsNullOrEmpty(configPassword))
            {
                // If no password is configured, allow access (for development)
                _isAuthenticated = true;
                return true;
            }

            if (password == configPassword)
            {
                _isAuthenticated = true;
                return true;
            }

            return false;
        }

        public void Logout()
        {
            _isAuthenticated = false;
        }
    }
}
