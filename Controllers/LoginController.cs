using PresenceTabMalik.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PresenceTabMalik.Controllers
{
    public class LoginController : Controller
    {
        private readonly IConfiguration _configuration;
        public LoginController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Index(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);
            try
            {
                var userConnString = $"Host=localhost;Database=PresenceTab;Username={model.Username};Password={model.Password}";
                using (var conn = new NpgsqlConnection(userConnString))
                {
                    await conn.OpenAsync();
                    HttpContext.Session.SetString("Username", model.Username);
                    HttpContext.Session.SetString("ConnectionString", userConnString);
                    // Определяем роль и перенаправляем
                    var role = DetermineUserRole(conn, model.Username);
                    HttpContext.Session.SetString("Role", role);
                    // Перенаправляем на нужную страницу в зависимости от роли
                    switch (role)
                    {
                        case "hrmanager":
                            return RedirectToAction("Schedule", "Hrmanager");
                        case "manager":
                            return RedirectToAction("Schedule", "Manager");
                        case "employee":
                            return RedirectToAction("Schedule", "Employee");
                        default:
                            return RedirectToAction("Index", "Login");
                    }
                }
            }
            catch
            {
                ModelState.AddModelError("", "Неправильний логін або пароль");
                return View(model);
            }
        }

        private string DetermineUserRole(NpgsqlConnection conn, string username)
        {
            try
            {
                using (var cmd = new NpgsqlCommand(@"
                    SELECT DISTINCT r.rolname 
                    FROM pg_roles r 
                    JOIN pg_auth_members m ON r.oid = m.roleid
                    JOIN pg_roles u ON m.member = u.oid
                    WHERE u.rolname = @username 
                    AND NOT r.rolcanlogin", conn))
                {
                    cmd.Parameters.AddWithValue("username", username);
                    var role = cmd.ExecuteScalar()?.ToString();
                    Console.WriteLine($"Определена роль: {role}"); // Логирование роли
                    return role ?? "user";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка определения роли: {ex.Message}"); // Логирование ошибки
                return "user";
            }
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }
    }
}