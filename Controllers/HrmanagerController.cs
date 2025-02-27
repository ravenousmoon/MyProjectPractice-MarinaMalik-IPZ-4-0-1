using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Npgsql;
using PresenceTabMalik.Models;
using System.Linq;
using System.Globalization;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace PresenceTabMalik.Controllers
{
    public class HrmanagerController : Controller
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private string ConnectionString => _httpContextAccessor.HttpContext?.Session.GetString("ConnectionString");

        public HrmanagerController(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public IActionResult Index()
        {
            return View();
        }

        public async Task<IActionResult> Schedule(int? employeeId = null, string searchTerm = null, string statusToSet = null, DateTime? dateToChange = null, int? year = null, int? month = null)
        {
            try
            {
                var username = HttpContext.Session.GetString("Username");
                if (string.IsNullOrEmpty(username))
                    return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    int currentUserId;
                    using (var cmd = new NpgsqlCommand(@"
                SELECT id FROM Employee WHERE username = @username", conn))
                    {
                        cmd.Parameters.AddWithValue("username", username);
                        currentUserId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }

                    ViewBag.IsCurrentUser = !employeeId.HasValue || employeeId.Value == currentUserId;

                    // Если выбран статус для установки, сохраняем его во ViewBag
                    if (!string.IsNullOrEmpty(statusToSet))
                    {
                        ViewBag.StatusToSet = statusToSet;
                    }

                    // Если есть дата для изменения и статус во ViewBag, меняем статус
                    if (dateToChange.HasValue && employeeId.HasValue && !string.IsNullOrEmpty(ViewBag.StatusToSet))
                    {
                        using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO Schedule (idEmployee, date, status)
                    VALUES (@employeeId, @date, @status::status_presence)
                    ON CONFLICT (idEmployee, date) 
                    DO UPDATE SET status = @status::status_presence", conn))
                        {
                            cmd.Parameters.AddWithValue("employeeId", employeeId.Value);
                            cmd.Parameters.AddWithValue("date", dateToChange.Value);
                            cmd.Parameters.AddWithValue("status", ViewBag.StatusToSet);
                            await cmd.ExecuteNonQueryAsync();
                        }
                        return RedirectToAction("Schedule", new { employeeId });
                    }

                    var currentDate = year.HasValue && month.HasValue
                        ? new DateTime(year.Value, month.Value, 1)
                        : DateTime.Now;

                    // Поиск сотрудников, если есть поисковый запрос
                    if (!string.IsNullOrEmpty(searchTerm))
                    {
                        using (var cmd = new NpgsqlCommand(@"
                    SELECT id, fullName 
                    FROM Employee 
                    WHERE LOWER(fullName) LIKE LOWER(@term || '%') 
                    LIMIT 5", conn))
                        {
                            cmd.Parameters.AddWithValue("term", searchTerm);
                            var searchResults = new List<Employee>();

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    searchResults.Add(new Employee
                                    {
                                        Id = reader.GetInt32(0),
                                        FullName = reader.GetString(1)
                                    });
                                }
                            }

                            ViewBag.SearchResults = searchResults;
                            ViewBag.SearchTerm = searchTerm;
                        }
                    }

                    // Получаем ID сотрудника
                    int selectedEmployeeId = 0; 

                    if (employeeId.HasValue)
                    {
                        selectedEmployeeId = employeeId.Value;

                        // Получаем имя выбранного сотрудника
                        using (var cmd = new NpgsqlCommand(@"
        SELECT fullName FROM Employee WHERE id = @id", conn))
                        {
                            cmd.Parameters.AddWithValue("id", selectedEmployeeId);
                            var employeeName = await cmd.ExecuteScalarAsync() as string;
                            ViewBag.EmployeeName = employeeName;
                        }
                    }
                    else if (statusToSet == null) // Только если не задан employeeId И не выбирается статус
                    {
                        // Если сотрудник не выбран и это не выбор статуса, берем текущего пользователя
                        using (var cmd = new NpgsqlCommand(@"
        SELECT id, fullName FROM Employee WHERE username = @username", conn))
                        {
                            cmd.Parameters.AddWithValue("username", username);
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    selectedEmployeeId = reader.GetInt32(0);
                                    ViewBag.EmployeeName = reader.GetString(1);
                                }
                                else
                                {
                                    return Content("Помилка: Користувач не знайдений в базі даних.");
                                }
                            }
                        }
                    }

                    ViewBag.CurrentEmployeeId = selectedEmployeeId;

                    var firstDay = new DateTime(currentDate.Year, currentDate.Month, 1);
                    var lastDay = firstDay.AddMonths(1).AddDays(-1);

                    // Получаем расписание
                    var schedules = new List<Schedule>();
                    using (var cmd = new NpgsqlCommand(@"
                SELECT s.date, s.status, t.arrivaltime, t.leavingtime 
                FROM Schedule s 
                LEFT JOIN Tab t ON s.id = t.id
                WHERE s.idEmployee = @employeeId 
                AND s.date BETWEEN @startDate AND @endDate
                ORDER BY s.date", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", selectedEmployeeId);
                        cmd.Parameters.AddWithValue("startDate", firstDay);
                        cmd.Parameters.AddWithValue("endDate", lastDay);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var schedule = new Schedule
                                {
                                    Date = reader.GetDateTime(0),
                                    Status = reader.GetString(1),
                                    Tab = !reader.IsDBNull(2) ? new Tab
                                    {
                                        ArrivalTime = reader.GetTimeSpan(2),
                                        LeavingTime = !reader.IsDBNull(3) ? reader.GetTimeSpan(3) : null
                                    } : null
                                };
                                schedules.Add(schedule);
                            }
                        }
                    }

                    // Заполняем дни без статуса
                    var allDates = Enumerable.Range(0, (lastDay - firstDay).Days + 1)
                        .Select(offset => firstDay.AddDays(offset));

                    var scheduledDates = schedules.Select(s => s.Date.Date);
                    var missingDates = allDates.Except(scheduledDates);

                    foreach (var date in missingDates)
                    {
                        schedules.Add(new Schedule
                        {
                            Date = date,
                            Status = null
                        });
                    }

                    // Сортируем все даты
                    schedules = schedules.OrderBy(s => s.Date).ToList();

                    // Подсчитываем общее количество отработанных часов
                    double totalHours = schedules
                        .Where(s => s.Status == "Робочий" && s.Tab?.LeavingTime != null)
                        .Sum(s => (s.Tab.LeavingTime.Value - s.Tab.ArrivalTime).TotalHours);

                    var viewModel = new ScheduleViewModel
                    {
                        CurrentDate = currentDate,
                        Schedules = schedules,
                        TotalHours = totalHours,
                        LastUpdateTime = DateTime.Now
                    };

                    return View(viewModel);
                }
            }
            catch (NpgsqlException ex)
            {
                return View(new ScheduleViewModel
                {
                    CurrentDate = year.HasValue && month.HasValue
                        ? new DateTime(year.Value, month.Value, 1)
                        : DateTime.Now,
                    Schedules = new List<Schedule>(),
                    TotalHours = 0,
                    LastUpdateTime = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return View(new ScheduleViewModel
                {
                    CurrentDate = year.HasValue && month.HasValue
                        ? new DateTime(year.Value, month.Value, 1)
                        : DateTime.Now,
                    Schedules = new List<Schedule>(),
                    TotalHours = 0,
                    LastUpdateTime = DateTime.Now
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> SearchEmployees(string term)
        {
            try
            {
                if (string.IsNullOrEmpty(term))
                    return Json(new object[] { });

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"
                SELECT id, fullName 
                FROM Employee 
                WHERE LOWER(fullName) LIKE LOWER('%' || @term || '%') 
                ORDER BY fullName 
                LIMIT 5", conn))
                    {
                        cmd.Parameters.AddWithValue("term", term);
                        var results = new List<object>();

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new
                                {
                                    id = reader.GetInt32(0),
                                    fullName = reader.GetString(1)
                                });
                            }
                        }

                        return Json(results);
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ChangeStatus(int employeeId, DateTime date, string status)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Сначала проверяем существование сотрудника
                    using (var checkCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM Employee WHERE id = @employeeId", conn))
                    {
                        checkCmd.Parameters.AddWithValue("employeeId", employeeId);
                        var exists = (long)await checkCmd.ExecuteScalarAsync() > 0;

                        if (!exists)
                        {
                            return Content("Помилка: Співробітник не знайдений");
                        }
                    }

                    // Если сотрудник существует, проверяем существование записи в Schedule
                    using (var cmd = new NpgsqlCommand(@"
                SELECT id FROM Schedule 
                WHERE idEmployee = @employeeId AND date = @date", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        cmd.Parameters.AddWithValue("date", date);
                        var existingId = await cmd.ExecuteScalarAsync();

                        if (existingId != null)
                        {
                            // Если запись существует - обновляем
                            using (var updateCmd = new NpgsqlCommand(@"
                        UPDATE Schedule 
                        SET status = @status::status_presence 
                        WHERE idEmployee = @employeeId AND date = @date", conn))
                            {
                                updateCmd.Parameters.AddWithValue("employeeId", employeeId);
                                updateCmd.Parameters.AddWithValue("date", date);
                                updateCmd.Parameters.AddWithValue("status", status);
                                await updateCmd.ExecuteNonQueryAsync();
                            }
                        }
                        else
                        {
                            // Если записи нет - создаем новую
                            using (var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO Schedule (idEmployee, date, status)
                        VALUES (@employeeId, @date, @status::status_presence)", conn))
                            {
                                insertCmd.Parameters.AddWithValue("employeeId", employeeId);
                                insertCmd.Parameters.AddWithValue("date", date);
                                insertCmd.Parameters.AddWithValue("status", status);
                                await insertCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }

                    return RedirectToAction("Schedule", new { employeeId });
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        public IActionResult CreateSchedule()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateSchedule(DateTime instDate, bool status)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var cmd = new NpgsqlCommand(@"
                INSERT INTO StInstAll (InstDate, isWorkDay)
                VALUES (@instDate, @isWorkDay)", conn))
                    {
                        cmd.Parameters.AddWithValue("instDate", instDate);
                        cmd.Parameters.AddWithValue("isWorkDay", status);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    return RedirectToAction("Schedule");
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> ArrivalMark()
        {
            try
            {
                var username = HttpContext.Session.GetString("Username");
                if (string.IsNullOrEmpty(username))
                    return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Получаем ID сотрудника
                    int employeeId;
                    using (var cmd = new NpgsqlCommand(@"
               SELECT id 
               FROM Employee 
               WHERE username = @username", conn))
                    {
                        cmd.Parameters.AddWithValue("username", username);
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач '{username}' не знайдений в базі даних.");
                        employeeId = Convert.ToInt32(result);
                    }

                    // Проверяем, не отмечался ли уже приход сегодня
                    using (var cmd = new NpgsqlCommand(@"
               SELECT COUNT(*)
               FROM Schedule s
               JOIN Tab t ON s.id = t.id
               WHERE s.idEmployee = @employeeId 
               AND s.date = CURRENT_DATE", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        if (count > 0)
                            return Content("Ви вже відзначили прихід сьогодні.");
                    }

                    // Создаем запись в Schedule и Tab
                    using (var cmd = new NpgsqlCommand(@"
               WITH new_schedule AS (
                   INSERT INTO Schedule (idEmployee, date, status)
                   VALUES (@employeeId, CURRENT_DATE, 'Робочий')
                   RETURNING id
               )
               INSERT INTO Tab (id, arrivalTime)
               SELECT id, CURRENT_TIME
               FROM new_schedule", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    return RedirectToAction("Schedule");
                }
            }
            catch (NpgsqlException ex)
            {
                return Content($"Помилка бази даних: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Content($"Неочікувана помилка: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> LeavingMark()
        {
            try
            {
                var username = HttpContext.Session.GetString("Username");
                if (string.IsNullOrEmpty(username))
                    return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Получаем ID сотрудника
                    int employeeId;
                    using (var cmd = new NpgsqlCommand(@"
               SELECT id 
               FROM Employee 
               WHERE username = @username", conn))
                    {
                        cmd.Parameters.AddWithValue("username", username);
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач '{username}' не знайдений в базі даних.");
                        employeeId = Convert.ToInt32(result);
                    }

                    // Проверяем, отмечался ли приход сегодня
                    using (var cmd = new NpgsqlCommand(@"
              SELECT COUNT(*)
              FROM Schedule s
              JOIN Tab t ON s.id = t.id
              WHERE s.idEmployee = @employeeId 
              AND s.date = CURRENT_DATE", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        if (count == 0)
                            return Content("Спочатку потрібно відзначити прихід.");
                    }

                    // Проверяем, не отмечался ли уже уход сегодня
                    using (var cmd = new NpgsqlCommand(@"
              SELECT COUNT(*)
              FROM Schedule s
              JOIN Tab t ON s.id = t.id
              WHERE s.idEmployee = @employeeId 
              AND s.date = CURRENT_DATE
              AND t.leavingTime IS NOT NULL", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        if (count > 0)
                            return Content("Ви вже відзначили вихід сьогодні.");
                    }

                    // Обновляем время ухода
                    using (var cmd = new NpgsqlCommand(@"
              UPDATE Tab
              SET leavingTime = CURRENT_TIME
              WHERE id IN (
                  SELECT id 
                  FROM Schedule 
                  WHERE idEmployee = @employeeId 
                  AND date = CURRENT_DATE
              )", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var rowsAffected = await cmd.ExecuteNonQueryAsync();
                        if (rowsAffected == 0)
                            return Content("Помилка: Не вдалося оновити час виходу.");
                    }

                    return RedirectToAction("Schedule");
                }
            }
            catch (NpgsqlException ex)
            {
                return Content($"Помилка бази даних: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Content($"Неочікувана помилка: {ex.Message}");
            }
        }

        public async Task<IActionResult> Statistics(int? employeeId = null, DateTime? startDate = null, DateTime? endDate = null)
        {
            Console.WriteLine($"Controller: {ControllerContext.RouteData.Values["controller"]}, Action: {ControllerContext.RouteData.Values["action"]}");
            ViewBag.EmployeeId = employeeId;
            try
            {
                if (!startDate.HasValue)
                    startDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                if (!endDate.HasValue)
                    endDate = startDate.Value.AddMonths(1).AddDays(-1);

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    int userId;
                    if (employeeId.HasValue)
                    {
                        userId = employeeId.Value;
                    }
                    else
                    {
                        var username = HttpContext.Session.GetString("Username");
                        if (string.IsNullOrEmpty(username))
                            return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                        // Получаем ID текущего пользователя
                        using (var cmd = new NpgsqlCommand(@"
                    SELECT id FROM Employee WHERE username = @username", conn))
                        {
                            cmd.Parameters.AddWithValue("username", username);
                            var result = await cmd.ExecuteScalarAsync();
                            if (result == null)
                                return Content($"Помилка: Користувач не знайдений в базі даних.");
                            userId = Convert.ToInt32(result);
                        }
                    }

                    // Получаем статистику по статусам
                    var statusCounts = new Dictionary<string, int>();
                    using (var cmd = new NpgsqlCommand(@"
                SELECT status, COUNT(*) as count
                FROM Schedule
                WHERE idEmployee = @employeeId 
                AND date BETWEEN @startDate AND @endDate
                GROUP BY status", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", userId);
                        cmd.Parameters.AddWithValue("startDate", startDate);
                        cmd.Parameters.AddWithValue("endDate", endDate);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                statusCounts.Add(reader.GetString(0), reader.GetInt32(1));
                            }
                        }
                    }

                    // Получаем зарплату за период
                    decimal periodSalary = 0;
                    using (var cmd = new NpgsqlCommand(@"
                SELECT COALESCE(
                    SUM(
                        EXTRACT(EPOCH FROM (t.leavingTime - t.arrivalTime)) / 3600 * jr.ratePerHour
                    ), 
                    0
                ) as salary
                FROM Schedule s 
                JOIN Tab t ON s.id = t.id  
                JOIN EmpNJob enj ON s.idEmployee = enj.idEmployee 
                    AND s.date BETWEEN enj.recruitmentDate AND COALESCE(enj.dismissalDate, CURRENT_DATE)
                JOIN JobRate jr ON enj.idJob = jr.idJob 
                    AND s.date BETWEEN jr.approvalDate AND COALESCE(jr.finalDate, CURRENT_DATE)
                WHERE s.idEmployee = @employeeId 
                AND s.date BETWEEN @startDate AND @endDate
                AND s.status = 'Робочий'", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", userId);
                        cmd.Parameters.AddWithValue("startDate", startDate);
                        cmd.Parameters.AddWithValue("endDate", endDate);

                        periodSalary = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
                    }

                    // Получаем зарплату по месяцам
                    var monthlySalaries = new List<MonthlySalary>();
                    using (var cmd = new NpgsqlCommand(@"
                WITH RECURSIVE months AS (
                    SELECT DATE_TRUNC('month', @startDate::date)::date as date
                    UNION ALL
                    SELECT (date + interval '1 month')::date
                    FROM months
                    WHERE date < DATE_TRUNC('month', @endDate::date)::date
                ),
                month_range AS (
                    SELECT date as month
                    FROM months
                )
                SELECT 
                    mr.month,
                    COALESCE(
                        SUM(
                            EXTRACT(EPOCH FROM (t.leavingTime - t.arrivalTime)) / 3600 * jr.ratePerHour
                        ), 
                        0
                    ) as salary
                FROM month_range mr
                LEFT JOIN Schedule s ON DATE_TRUNC('month', s.date) = mr.month 
                    AND s.idEmployee = @employeeId 
                    AND s.status = 'Робочий'
                LEFT JOIN Tab t ON s.id = t.id
                LEFT JOIN EmpNJob enj ON s.idEmployee = enj.idEmployee 
                    AND s.date BETWEEN enj.recruitmentDate AND COALESCE(enj.dismissalDate, CURRENT_DATE)
                LEFT JOIN JobRate jr ON enj.idJob = jr.idJob 
                    AND s.date BETWEEN jr.approvalDate AND COALESCE(jr.finalDate, CURRENT_DATE)
                GROUP BY mr.month
                ORDER BY mr.month", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", userId);
                        cmd.Parameters.AddWithValue("startDate", startDate);
                        cmd.Parameters.AddWithValue("endDate", endDate);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                monthlySalaries.Add(new MonthlySalary
                                {
                                    Month = reader.GetDateTime(0),
                                    Salary = reader.GetDecimal(1)
                                });
                            }
                        }
                    }

                    var viewModel = new StatisticsViewModel
                    {
                        StartDate = startDate.Value,
                        EndDate = endDate.Value,
                        StatusCounts = statusCounts,
                        PeriodSalary = periodSalary,
                        MonthlySalaries = monthlySalaries,
                        LastUpdateTime = DateTime.Now
                    };

                    return View(viewModel);
                }
            }
            catch (NpgsqlException ex)
            {
                return Content($"Помилка бази даних: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Content($"Неочікувана помилка: {ex.Message}");
            }
        }

        public IActionResult Employees()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> SearchAllEmployees(string term = "", string sortField = "fullName", string sortDir = "asc")
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    var orderBy = sortField.ToLower() switch
                    {
                        "fullname" => "E.fullName",
                        "birthdate" => "E.birthdayDate",
                        "hiredate" => "ENJ.recruitmentDate",
                        "position" => "J.name",
                        "phone" => "E.phoneNumber",
                        "email" => "E.email",
                        _ => "E.fullName"
                    };

                    using (var cmd = new NpgsqlCommand($@"
                SELECT DISTINCT
                    E.id,
                    E.fullName,
                    E.birthdayDate,
                    ENJ.recruitmentDate,
                    J.name as position,
                    E.phoneNumber,
                    E.email
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee
                JOIN Job J ON ENJ.idJob = J.id
                WHERE ENJ.dismissalDate IS NULL
                AND (
                    @term = '' OR
                    LOWER(E.fullName) LIKE LOWER(@term)
                    OR LOWER(J.name) LIKE LOWER(@term)
                    OR E.phoneNumber LIKE @term
                    OR LOWER(E.email) LIKE LOWER(@term)
                    OR EXTRACT(YEAR FROM E.birthdayDate)::text LIKE @term
                    OR EXTRACT(YEAR FROM ENJ.recruitmentDate)::text LIKE @term
                    OR CASE 
                        WHEN J.name = 'Admin' AND LOWER('HR-Менеджер') LIKE LOWER('%' || @termWithoutPercent || '%') THEN TRUE
                        WHEN J.name = 'Manager' AND LOWER('Керівник') LIKE LOWER('%' || @termWithoutPercent || '%') THEN TRUE
                        WHEN J.name = 'Worker' AND LOWER('Співробітник') LIKE LOWER('%' || @termWithoutPercent || '%') THEN TRUE
                        ELSE FALSE
                    END
                )
                ORDER BY {orderBy} {(sortDir.ToLower() == "desc" ? "DESC" : "ASC")}", conn))
                    {
                        cmd.Parameters.AddWithValue("term", $"%{term}%");
                        cmd.Parameters.AddWithValue("termWithoutPercent", term); 
                        var results = new List<object>();

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string position = reader.GetString(4);
                                switch (position)
                                {
                                    case "Admin": position = "HR-Менеджер"; break;
                                    case "Manager": position = "Керівник"; break;
                                    case "Worker": position = "Співробітник"; break;
                                }

                                results.Add(new
                                {
                                    id = reader.GetInt32(0),
                                    fullName = reader.GetString(1),
                                    birthDate = reader.GetDateTime(2).ToString("dd.MM.yyyy"),
                                    hireDate = reader.GetDateTime(3).ToString("dd.MM.yyyy"),
                                    position = position,
                                    phone = reader.GetString(5),
                                    email = reader.GetString(6)
                                });
                            }
                        }

                        return Json(results);
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetEmployeeDetails(int employeeId)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    var details = new Dictionary<string, object>();
                    using (var cmd = new NpgsqlCommand(@"
                SELECT E.fullName, 
                       E.birthdayDate, 
                       ENJ.recruitmentDate, 
                       J.name as position,
                       E.phoneNumber, 
                       E.email,
                       COALESCE(S.status, NULL) as status,
                       COALESCE(
                           (SELECT SUM(EXTRACT(EPOCH FROM (t.leavingTime - t.arrivalTime)) / 3600)
                            FROM Schedule s2
                            JOIN Tab t ON s2.id = t.id
                            WHERE s2.idEmployee = E.id 
                            AND s2.date BETWEEN DATE_TRUNC('month', CURRENT_DATE) AND CURRENT_DATE
                            AND s2.status = 'Робочий'), 0) as workedHours
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee 
                JOIN Job J ON ENJ.idJob = J.id
                LEFT JOIN Schedule S ON E.id = S.idEmployee AND S.date = CURRENT_DATE
                WHERE E.id = @employeeId 
                AND ENJ.dismissalDate IS NULL", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                details["fullName"] = reader.GetString(0);
                                details["birthDate"] = reader.GetDateTime(1).ToString("dd.MM.yyyy");
                                details["hireDate"] = reader.GetDateTime(2).ToString("dd.MM.yyyy");

                                var position = reader.GetString(3);
                                switch (position)
                                {
                                    case "Admin": position = "HR-Менеджер"; break;
                                    case "Manager": position = "Керівник"; break;
                                    case "Worker": position = "Співробітник"; break;
                                }
                                details["position"] = position;

                                details["phone"] = reader.GetString(4);
                                details["email"] = reader.GetString(5);
                                details["status"] = !reader.IsDBNull(6) ? reader.GetString(6) : null;
                                details["workedHours"] = Math.Round(reader.GetDouble(7), 2);
                            }
                        }
                    }

                    return Json(details);
                }
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        public IActionResult AddEmployee()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> AddEmployee(string fullName, DateTime birthdayDate,
    string jobName, string phoneNumber, string email, string username)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var transaction = await conn.BeginTransactionAsync())
                    {
                        try
                        {
                            // Генерируем случайный пароль из 4 цифр
                            var random = new Random();
                            string password = random.Next(1000, 9999).ToString();

                            // Добавляем сотрудника
                            int employeeId;
                            using (var cmd = new NpgsqlCommand(@"
                        INSERT INTO Employee (fullName, birthdayDate, phoneNumber, email, username)
                        VALUES (@fullName, @birthdayDate, @phoneNumber, @email, @username)
                        RETURNING id", conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("fullName", fullName);
                                cmd.Parameters.AddWithValue("birthdayDate", birthdayDate);
                                cmd.Parameters.AddWithValue("phoneNumber", phoneNumber);
                                cmd.Parameters.AddWithValue("email", email);
                                cmd.Parameters.AddWithValue("username", username);

                                employeeId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                            }

                            // Получаем ID должности
                            int jobId;
                            using (var cmd = new NpgsqlCommand(@"
                        SELECT id FROM Job WHERE name = @jobName", conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("jobName", jobName);
                                jobId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                            }

                            // Добавляем запись в EmpNJob
                            using (var cmd = new NpgsqlCommand(@"
                        INSERT INTO EmpNJob (idEmployee, idJob, recruitmentDate)
                        VALUES (@employeeId, @jobId, CURRENT_DATE)", conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("employeeId", employeeId);
                                cmd.Parameters.AddWithValue("jobId", jobId);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // Создаем пользователя через хранимую процедуру
                            using (var cmd = new NpgsqlCommand(
                                "SELECT create_user_with_role(@username, @password, @role)",
                                conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("username", username);
                                cmd.Parameters.AddWithValue("password", password);
                                cmd.Parameters.AddWithValue("role", GetRoleForJob(jobName));
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // Сохраняем данные для отображения
                            TempData["NewUsername"] = username;
                            TempData["NewPassword"] = password;

                            await transaction.CommitAsync();
                            return RedirectToAction("Employees");
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        private string GetRoleForJob(string jobName) => jobName switch
        {
            "Admin" => "hrmanager",
            "Manager" => "manager",
            "Worker" => "employee",
            _ => throw new ArgumentException("Невідома посада")
        };

        [HttpGet]
        public async Task<IActionResult> EditEmployee(int id)
        {
            try
            {
                if (id <= 0)
                {
                    return RedirectToAction("Employees");
                }

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var cmd = new NpgsqlCommand(@"
                SELECT E.id,
                       E.fullName, 
                       E.birthdayDate, 
                       J.name as jobName,
                       E.phoneNumber, 
                       E.email,
                       E.username
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee
                JOIN Job J ON ENJ.idJob = J.id
                WHERE E.id = @id
                AND ENJ.dismissalDate IS NULL", conn))
                    {
                        cmd.Parameters.AddWithValue("id", id);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                ViewBag.Employee = new
                                {
                                    Id = reader.GetInt32(0),
                                    FullName = reader.GetString(1),
                                    BirthdayDate = reader.GetDateTime(2),
                                    JobName = reader.GetString(3),
                                    PhoneNumber = reader.GetString(4),
                                    Email = reader.GetString(5),
                                    Username = reader.GetString(6)
                                };

                                return View();
                            }
                        }
                    }

                    return RedirectToAction("Employees");
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> EditEmployee(int id, string fullName, DateTime birthdayDate,
    string jobName, string phoneNumber, string email)  
        {
            try
            {
                if (id <= 0)
                {
                    return Content("Помилка: Не вказано ID співробітника");
                }

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var transaction = await conn.BeginTransactionAsync())
                    {
                        try
                        {
                            // Обновляем данные в таблице Employee
                            using (var cmd = new NpgsqlCommand(@"
                        UPDATE Employee 
                        SET fullName = @fullName,
                            birthdayDate = @birthdayDate,
                            phoneNumber = @phoneNumber,
                            email = @email
                        WHERE id = @id", conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("id", id);
                                cmd.Parameters.AddWithValue("fullName", fullName);
                                cmd.Parameters.AddWithValue("birthdayDate", birthdayDate);
                                cmd.Parameters.AddWithValue("phoneNumber", phoneNumber);
                                cmd.Parameters.AddWithValue("email", email);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // Обновляем должность в EmpNJob
                            using (var cmd = new NpgsqlCommand(@"
                        UPDATE EmpNJob 
                        SET idJob = (SELECT id FROM Job WHERE name = @jobName)
                        WHERE idEmployee = @id 
                        AND dismissalDate IS NULL", conn, transaction))
                            {
                                cmd.Parameters.AddWithValue("id", id);
                                cmd.Parameters.AddWithValue("jobName", jobName);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await transaction.CommitAsync();
                            return RedirectToAction("Employees");
                        }
                        catch (Exception ex)
                        {
                            await transaction.RollbackAsync();
                            throw new Exception($"Помилка при оновленні даних: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> DismissEmployee(int employeeId)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Сначала проверим существование сотрудника и его текущий статус
                    using (var checkCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) 
                FROM EmpNJob 
                WHERE idEmployee = @employeeId 
                AND dismissalDate IS NULL", conn))
                    {
                        checkCmd.Parameters.AddWithValue("employeeId", employeeId);
                        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

                        if (count == 0)
                        {
                            return Json(new { success = false, message = "Сотрудник не найден или уже уволен" });
                        }
                    }

                    // Если сотрудник найден, увольняем его
                    using (var cmd = new NpgsqlCommand(@"
                UPDATE EmpNJob 
                SET dismissalDate = CURRENT_DATE
                WHERE idEmployee = @employeeId 
                AND dismissalDate IS NULL
                RETURNING id", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var result = await cmd.ExecuteScalarAsync();

                        if (result != null)
                        {
                            return Json(new { success = true });
                        }
                        else
                        {
                            return Json(new { success = false, message = "Не удалось установить дату увольнения" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ошибка: {ex.Message}" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Tabulation(string date = null, string searchTerm = null, string sortField = "fullName", string sortDir = "asc")
        {
            try
            {
                DateTime selectedDate = string.IsNullOrEmpty(date) ?
                    DateTime.Today :
                    DateTime.Parse(date);

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    var orderBy = sortField.ToLower() switch
                    {
                        "fullname" => "E.fullName",
                        "arrivaltime" => "T.arrivalTime",
                        "leavingtime" => "T.leavingTime",
                        _ => "E.fullName"
                    };

                    var sql = @"
                SELECT DISTINCT 
                    E.fullName,
                    T.arrivalTime,
                    T.leavingTime
                FROM Employee E
                JOIN Schedule S ON E.id = S.idEmployee
                JOIN Tab T ON S.id = T.id
                WHERE CAST(S.date AS DATE) = @date
                AND S.status = 'Робочий'
                AND (@searchTerm IS NULL OR @searchTerm = '' OR LOWER(E.fullName) LIKE '%' || LOWER(@searchTerm) || '%')
                ORDER BY " + orderBy + " " + (sortDir.ToLower() == "desc" ? "DESC" : "ASC");

                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("date", selectedDate.Date);
                        cmd.Parameters.AddWithValue("searchTerm", searchTerm ?? "");

                        Debug.WriteLine($"Executing query for date: {selectedDate.Date}");
                        var employees = new List<TabViewModel>();

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                employees.Add(new TabViewModel
                                {
                                    FullName = reader.GetString(0),
                                    ArrivalTime = reader.GetTimeSpan(1),
                                    LeavingTime = !reader.IsDBNull(2) ? reader.GetTimeSpan(2) : null
                                });
                            }
                        }

                        ViewBag.SelectedDate = selectedDate;
                        ViewBag.SearchTerm = searchTerm;
                        ViewBag.SortField = sortField;
                        ViewBag.SortDir = sortDir;

                        Debug.WriteLine($"Found {employees.Count} employees");
                        return View(employees);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
                ViewBag.SelectedDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date);
                return View(new List<TabViewModel>());
            }
        }

        public IActionResult Job()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> SearchJobs(string term = "", string sortField = "name", string sortDir = "asc")
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    var orderBy = sortField.ToLower() switch
                    {
                        "name" => "J.name",
                        "rateperhour" => "JR.ratePerHour",
                        "approvaldate" => "JR.approvalDate",
                        _ => "J.name"
                    };

                    using (var cmd = new NpgsqlCommand($@"
                SELECT DISTINCT
                    J.name,
                    JR.ratePerHour,
                    JR.approvalDate
                FROM Job J
                JOIN JobRate JR ON J.id = JR.idJob
                WHERE JR.finalDate IS NULL
                AND (
                    @term = '' OR
                    LOWER(CASE 
                        WHEN J.name = 'Admin' THEN 'HR-Менеджер'
                        WHEN J.name = 'Manager' THEN 'Керівник'
                        WHEN J.name = 'Worker' THEN 'Співробітник'
                    END) LIKE LOWER('%' || @term || '%')  -- Изменили условие поиска здесь
                )
                ORDER BY {orderBy} {(sortDir.ToLower() == "desc" ? "DESC" : "ASC")}", conn))
                    {
                        cmd.Parameters.AddWithValue("term", term);
                        var results = new List<object>();

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string position = reader.GetString(0);
                                switch (position)
                                {
                                    case "Admin": position = "HR-Менеджер"; break;
                                    case "Manager": position = "Керівник"; break;
                                    case "Worker": position = "Співробітник"; break;
                                }

                                results.Add(new
                                {
                                    name = position,
                                    ratePerHour = reader.GetDecimal(1),
                                    approvalDate = reader.GetDateTime(2).ToString("dd.MM.yyyy")
                                });
                            }
                        }

                        return Json(results);
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        public IActionResult Notoriety()
        {
            return View();
        }

        [HttpPost]
        public IActionResult GenerateNotoriety(string period, int? employeeId, string jobName, DateTime? startDate, DateTime? endDate)
        {
            try
            {
                // Подготовим параметры URL
                var routeValues = new Dictionary<string, object>();
                routeValues.Add("period", period);

                if (employeeId.HasValue)
                    routeValues.Add("employeeId", employeeId.Value);

                if (!string.IsNullOrEmpty(jobName))
                    routeValues.Add("jobName", jobName);

                // Для произвольного периода добавляем даты
                if (period == "custom" && startDate.HasValue && endDate.HasValue)
                {
                    routeValues.Add("start", startDate.Value.ToString("yyyy-MM-dd"));
                    routeValues.Add("end", endDate.Value.ToString("yyyy-MM-dd"));
                }

                // Перенаправляем на GET-метод с параметрами в URL
                return RedirectToAction("NotorietyPreview", routeValues);
            }
            catch (Exception ex)
            {
                return Content($"Помилка: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetJobsList()
        {
            try
            {
                var jobs = new List<object>
        {
            new { id = "", name = "Всі посади" },
            new { id = "Admin", name = "HR-Менеджер" },
            new { id = "Manager", name = "Керівник" },
            new { id = "Worker", name = "Співробітник" }
        };

                return Json(jobs);
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetEmployeeJob(int employeeId)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var cmd = new NpgsqlCommand(@"
                SELECT J.name 
                FROM Job J
                JOIN EmpNJob ENJ ON J.id = ENJ.idJob
                WHERE ENJ.idEmployee = @employeeId
                AND ENJ.dismissalDate IS NULL", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        var jobName = await cmd.ExecuteScalarAsync() as string;

                        return Json(new { success = true, jobName });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> NotorietyPreview(string period, int? employeeId, string jobName, string start = null, string end = null)
        {
            try
            {
                // Определяем даты на основе входных параметров
                DateTime startDate;
                DateTime endDate;

                // Обработка периодов
                if (period == "custom" && !string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end))
                {
                    if (DateTime.TryParse(start, out startDate) && DateTime.TryParse(end, out endDate))
                    {
                        // Используем полученные даты
                    }
                    else
                    {
                        // Если не удалось разобрать даты, используем текущий месяц
                        var now = DateTime.Now;
                        startDate = new DateTime(now.Year, now.Month, 1);
                        endDate = startDate.AddMonths(1).AddDays(-1);
                    }
                }
                else if (period == "current_month")
                {
                    var now = DateTime.Now;
                    startDate = new DateTime(now.Year, now.Month, 1);
                    endDate = startDate.AddMonths(1).AddDays(-1);
                }
                else if (period == "half_year")
                {
                    endDate = DateTime.Now;
                    startDate = endDate.AddMonths(-6);
                }
                else if (period == "year")
                {
                    endDate = DateTime.Now;
                    startDate = endDate.AddYears(-1);
                }
                else if (period == "all_time")
                {
                    startDate = new DateTime(2000, 1, 1);
                    endDate = DateTime.Now;
                }
                else
                {
                    // По умолчанию - текущий месяц
                    var now = DateTime.Now;
                    startDate = new DateTime(now.Year, now.Month, 1);
                    endDate = startDate.AddMonths(1).AddDays(-1);
                }

                // Сохраняем параметры в ViewBag
                ViewBag.Period = period;
                ViewBag.EmployeeId = employeeId;
                ViewBag.JobName = jobName;
                ViewBag.StartDate = startDate;
                ViewBag.EndDate = endDate;

                var results = new List<NotorietyViewModel>();

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    string baseSql = @"
                SELECT 
                    E.fullName,
                    CASE 
                        WHEN J.name = 'Admin' THEN 'HR-Менеджер'
                        WHEN J.name = 'Manager' THEN 'Керівник'
                        WHEN J.name = 'Worker' THEN 'Співробітник'
                        ELSE J.name
                    END AS position,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600) AS hoursWorked,
                    JR.ratePerHour,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600 * JR.ratePerHour) AS salary
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee
                JOIN Job J ON ENJ.idJob = J.id
                JOIN Schedule S ON E.id = S.idEmployee
                JOIN Tab T ON S.id = T.id
                JOIN JobRate JR ON J.id = JR.idJob 
                WHERE S.date BETWEEN @startDate AND @endDate
                AND S.status = 'Робочий'
                AND ENJ.dismissalDate IS NULL
                AND S.date BETWEEN ENJ.recruitmentDate AND COALESCE(ENJ.dismissalDate, CURRENT_DATE)
                AND S.date BETWEEN JR.approvalDate AND COALESCE(JR.finalDate, CURRENT_DATE)";

                    string whereClause = "";
                    List<NpgsqlParameter> parameters = new List<NpgsqlParameter>
            {
                new NpgsqlParameter("startDate", startDate),
                new NpgsqlParameter("endDate", endDate)
            };

                    if (employeeId.HasValue)
                    {
                        whereClause += " AND E.id = @employeeId";
                        parameters.Add(new NpgsqlParameter("employeeId", employeeId.Value));
                    }

                    if (!string.IsNullOrEmpty(jobName))
                    {
                        whereClause += " AND J.name = @jobName";
                        parameters.Add(new NpgsqlParameter("jobName", jobName));
                    }

                    string groupBy = @"
                GROUP BY E.fullName, J.name, JR.ratePerHour
                ORDER BY E.fullName, JR.ratePerHour DESC";

                    string sql = baseSql + whereClause + groupBy;

                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        // Добавляем все параметры
                        foreach (var param in parameters)
                        {
                            cmd.Parameters.Add(param);
                        }

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new NotorietyViewModel
                                {
                                    FullName = reader.GetString(0),
                                    Position = reader.GetString(1),
                                    HoursWorked = Math.Round(reader.GetDouble(2), 2),
                                    RatePerHour = reader.GetDecimal(3),
                                    Salary = Math.Round(reader.GetDecimal(4), 2)
                                });
                            }
                        }
                    }
                }

                // Сумма всей зарплаты
                ViewBag.TotalSalary = results.Sum(r => r.Salary);

                // Текущая дата для отображения в ведомости
                ViewBag.CurrentDate = DateTime.Now;

                return View(results);
            }
            catch (Exception ex)
            {
                return Content($"Помилка при формуванні відомості: {ex.Message}. " +
                               $"Period: {period}, Start: {start}, End: {end}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> DownloadNotorietyForEmployee(int employeeId, string period, DateTime? startDate, DateTime? endDate)
        {
            try
            {
                // Определяем даты на основе периода
                if (startDate == null || endDate == null)
                {
                    if (period == "current_month")
                    {
                        var now = DateTime.Now;
                        startDate = new DateTime(now.Year, now.Month, 1);
                        endDate = startDate.Value.AddMonths(1).AddDays(-1);
                    }
                    else if (period == "half_year")
                    {
                        endDate = DateTime.Now;
                        startDate = endDate.Value.AddMonths(-6);
                    }
                    else if (period == "year")
                    {
                        endDate = DateTime.Now;
                        startDate = endDate.Value.AddYears(-1);
                    }
                    else if (period == "all_time")
                    {
                        startDate = new DateTime(2000, 1, 1);
                        endDate = DateTime.Now;
                    }
                    else
                    {
                        // По умолчанию - текущий месяц
                        var now = DateTime.Now;
                        startDate = new DateTime(now.Year, now.Month, 1);
                        endDate = startDate.Value.AddMonths(1).AddDays(-1);
                    }
                }

                // Получаем данные для конкретного сотрудника
                var results = new List<NotorietyViewModel>();

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    string sql = @"
                SELECT 
                    E.fullName,
                    CASE 
                        WHEN J.name = 'Admin' THEN 'HR-Менеджер'
                        WHEN J.name = 'Manager' THEN 'Керівник'
                        WHEN J.name = 'Worker' THEN 'Співробітник'
                        ELSE J.name
                    END AS position,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600) AS hoursWorked,
                    JR.ratePerHour,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600 * JR.ratePerHour) AS salary
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee
                JOIN Job J ON ENJ.idJob = J.id
                JOIN Schedule S ON E.id = S.idEmployee
                JOIN Tab T ON S.id = T.id
                JOIN JobRate JR ON J.id = JR.idJob 
                WHERE S.date BETWEEN @startDate AND @endDate
                AND S.status = 'Робочий'
                AND E.id = @employeeId
                AND ENJ.dismissalDate IS NULL
                AND S.date BETWEEN ENJ.recruitmentDate AND COALESCE(ENJ.dismissalDate, CURRENT_DATE)
                AND S.date BETWEEN JR.approvalDate AND COALESCE(JR.finalDate, CURRENT_DATE)
                GROUP BY E.fullName, J.name, JR.ratePerHour
                ORDER BY E.fullName, JR.ratePerHour DESC";

                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("startDate", startDate);
                        cmd.Parameters.AddWithValue("endDate", endDate);
                        cmd.Parameters.AddWithValue("employeeId", employeeId);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new NotorietyViewModel
                                {
                                    FullName = reader.GetString(0),
                                    Position = reader.GetString(1),
                                    HoursWorked = Math.Round(reader.GetDouble(2), 2),
                                    RatePerHour = reader.GetDecimal(3),
                                    Salary = Math.Round(reader.GetDecimal(4), 2)
                                });
                            }
                        }
                    }
                }

                // Сумма всей зарплаты
                decimal totalSalary = results.Sum(r => r.Salary);

                // Создаем PDF документ
                using (MemoryStream ms = new MemoryStream())
                {
                    // Создаем документ iTextSharp
                    var document = new iTextSharp.text.Document(iTextSharp.text.PageSize.A4, 50, 50, 50, 50);
                    var writer = iTextSharp.text.pdf.PdfWriter.GetInstance(document, ms);
                    document.Open();

                    // Устанавливаем шрифт для поддержки кириллицы
                    string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                    var baseFont = iTextSharp.text.pdf.BaseFont.CreateFont(fontPath, iTextSharp.text.pdf.BaseFont.IDENTITY_H, iTextSharp.text.pdf.BaseFont.EMBEDDED);
                    var font = new iTextSharp.text.Font(baseFont, 10, iTextSharp.text.Font.NORMAL);
                    var boldFont = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.BOLD);
                    var headerFont = new iTextSharp.text.Font(baseFont, 20, iTextSharp.text.Font.BOLD); 

                    // Добавляем заголовок
                    var header = new iTextSharp.text.Paragraph(
                        "Відомість нарахування заробітної плати", headerFont);
                    header.Alignment = iTextSharp.text.Element.ALIGN_CENTER;
                    header.SpacingAfter = 48; // 48px отступ как в CSS
                    document.Add(header);

                    // Создаем таблицу 
                    var table = new iTextSharp.text.pdf.PdfPTable(6);
                    table.WidthPercentage = 100;
                    table.SetWidths(new float[] { 5, 28, 18, 20, 13, 16 });
                    table.SpacingAfter = 30; 

                    // Добавляем заголовки столбцов 
                    var headerBgColor = new iTextSharp.text.BaseColor(248, 248, 248); 

                    var cellHeader1 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("№", boldFont));
                    var cellHeader2 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("ПІБ", boldFont));
                    var cellHeader3 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Посада", boldFont));
                    var cellHeader4 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Кількість відпрацьованих годин", boldFont));
                    var cellHeader5 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Ставка за годину", boldFont));
                    var cellHeader6 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Заробітна плата", boldFont));

                    // Стилизуем заголовки
                    cellHeader1.BackgroundColor = headerBgColor;
                    cellHeader2.BackgroundColor = headerBgColor;
                    cellHeader3.BackgroundColor = headerBgColor;
                    cellHeader4.BackgroundColor = headerBgColor;
                    cellHeader5.BackgroundColor = headerBgColor;
                    cellHeader6.BackgroundColor = headerBgColor;

                    // Устанавливаем отступы внутри ячеек
                    cellHeader1.Padding = 10;
                    cellHeader2.Padding = 10;
                    cellHeader3.Padding = 10;
                    cellHeader4.Padding = 10;
                    cellHeader5.Padding = 10;
                    cellHeader6.Padding = 10;

                    // Выравнивание текста влево
                    cellHeader1.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader2.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader3.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader4.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader5.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader6.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;

                    // Границы ячеек
                    var borderColor = new iTextSharp.text.BaseColor(229, 229, 229); 
                    cellHeader1.BorderColor = borderColor;
                    cellHeader2.BorderColor = borderColor;
                    cellHeader3.BorderColor = borderColor;
                    cellHeader4.BorderColor = borderColor;
                    cellHeader5.BorderColor = borderColor;
                    cellHeader6.BorderColor = borderColor;

                    table.AddCell(cellHeader1);
                    table.AddCell(cellHeader2);
                    table.AddCell(cellHeader3);
                    table.AddCell(cellHeader4);
                    table.AddCell(cellHeader5);
                    table.AddCell(cellHeader6);

                    // Добавляем данные
                    for (int i = 0; i < results.Count; i++)
                    {
                        var item = results[i];

                        var cell1 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase((i + 1).ToString(), font));
                        var cell2 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(item.FullName, font));
                        var cell3 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(item.Position, font));
                        var cell4 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.HoursWorked} год.", font));
                        var cell5 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.RatePerHour} грн.", font));
                        var cell6 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.Salary} грн.", font));

                        // Отступы 
                        cell1.Padding = 10;
                        cell2.Padding = 10;
                        cell3.Padding = 10;
                        cell4.Padding = 10;
                        cell5.Padding = 10;
                        cell6.Padding = 10;

                        // Выравнивание влево
                        cell1.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell2.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell3.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell4.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell5.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell6.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;

                        // Границы ячеек 
                        cell1.BorderColor = borderColor;
                        cell2.BorderColor = borderColor;
                        cell3.BorderColor = borderColor;
                        cell4.BorderColor = borderColor;
                        cell5.BorderColor = borderColor;
                        cell6.BorderColor = borderColor;

                        table.AddCell(cell1);
                        table.AddCell(cell2);
                        table.AddCell(cell3);
                        table.AddCell(cell4);
                        table.AddCell(cell5);
                        table.AddCell(cell6);
                    }

                    // Создаем строку итога
                    // Добавляем пустые ячейки для первых 4 колонок
                    var emptyCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(""));
                    emptyCell.Border = 0;

                    // Ячейка "Загальна сума:" с выравниванием вправо на 5 колонок
                    var totalLabelCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Загальна сума:", boldFont));
                    totalLabelCell.Colspan = 5;
                    totalLabelCell.HorizontalAlignment = iTextSharp.text.Element.ALIGN_RIGHT;
                    totalLabelCell.Padding = 10;
                    totalLabelCell.Border = iTextSharp.text.Rectangle.BOX;
                    totalLabelCell.BorderColor = borderColor;
                    table.AddCell(totalLabelCell);

                    // Значение суммы зеленым цветом в последней колонке
                    var totalColor = new iTextSharp.text.BaseColor(0, 191, 165);
                    var totalFont = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.BOLD, totalColor);
                    var totalValueCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{totalSalary} грн.", totalFont));
                    totalValueCell.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    totalValueCell.Padding = 10;
                    totalValueCell.Border = iTextSharp.text.Rectangle.BOX;
                    totalValueCell.BorderColor = borderColor;
                    table.AddCell(totalValueCell);

                    document.Add(table);

                    // Добавляем подписи 
                    var signatureBlock = new iTextSharp.text.Paragraph();
                    signatureBlock.SpacingBefore = 50;
                    signatureBlock.Add(new iTextSharp.text.Chunk("Директор ______________________", font));
                    document.Add(signatureBlock);

                    var accountantSignature = new iTextSharp.text.Paragraph();
                    accountantSignature.SpacingBefore = 30; 
                    accountantSignature.Add(new iTextSharp.text.Chunk("Головний бухгалтер ______________________", font));
                    document.Add(accountantSignature);

                    document.Close();

                    // Определяем имя файла
                    string periodDescription;
                    if (period == "current_month")
                    {
                        periodDescription = startDate.Value.ToString("yyyy-MM");
                    }
                    else if (period == "half_year")
                    {
                        periodDescription = "half-year";
                    }
                    else if (period == "year")
                    {
                        periodDescription = "year";
                    }
                    else if (period == "all_time")
                    {
                        periodDescription = "all-time";
                    }
                    else
                    {
                        periodDescription = $"{startDate.Value:yyyy-MM-dd}_{endDate.Value:yyyy-MM-dd}";
                    }

                    string fileName = $"employee_{employeeId}_payroll_{periodDescription}.pdf";

                    // Возвращаем PDF файл
                    return File(ms.ToArray(), "application/pdf", fileName);
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка при завантаженні відомості: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> DownloadNotorietyForAll(string period, string jobName, DateTime? startDate, DateTime? endDate)
        {
            try
            {
                // Определяем даты на основе периода
                if (startDate == null || endDate == null)
                {
                    if (period == "current_month")
                    {
                        var now = DateTime.Now;
                        startDate = new DateTime(now.Year, now.Month, 1);
                        endDate = startDate.Value.AddMonths(1).AddDays(-1);
                    }
                    else if (period == "half_year")
                    {
                        endDate = DateTime.Now;
                        startDate = endDate.Value.AddMonths(-6);
                    }
                    else if (period == "year")
                    {
                        endDate = DateTime.Now;
                        startDate = endDate.Value.AddYears(-1);
                    }
                    else if (period == "all_time")
                    {
                        startDate = new DateTime(2000, 1, 1);
                        endDate = DateTime.Now;
                    }
                    else
                    {
                        // По умолчанию - текущий месяц
                        var now = DateTime.Now;
                        startDate = new DateTime(now.Year, now.Month, 1);
                        endDate = startDate.Value.AddMonths(1).AddDays(-1);
                    }
                }

                // Получаем данные для всех сотрудников или по должности
                var results = new List<NotorietyViewModel>();

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    string sql = @"
                SELECT 
                    E.fullName,
                    CASE 
                        WHEN J.name = 'Admin' THEN 'HR-Менеджер'
                        WHEN J.name = 'Manager' THEN 'Керівник'
                        WHEN J.name = 'Worker' THEN 'Співробітник'
                        ELSE J.name
                    END AS position,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600) AS hoursWorked,
                    JR.ratePerHour,
                    SUM(EXTRACT(EPOCH FROM (T.leavingTime - T.arrivalTime)) / 3600 * JR.ratePerHour) AS salary
                FROM Employee E
                JOIN EmpNJob ENJ ON E.id = ENJ.idEmployee
                JOIN Job J ON ENJ.idJob = J.id
                JOIN Schedule S ON E.id = S.idEmployee
                JOIN Tab T ON S.id = T.id
                JOIN JobRate JR ON J.id = JR.idJob 
                WHERE S.date BETWEEN @startDate AND @endDate
                AND S.status = 'Робочий'";

                    if (!string.IsNullOrEmpty(jobName))
                    {
                        sql += " AND J.name = @jobName";
                    }

                    sql += @"
                AND ENJ.dismissalDate IS NULL
                AND S.date BETWEEN ENJ.recruitmentDate AND COALESCE(ENJ.dismissalDate, CURRENT_DATE)
                AND S.date BETWEEN JR.approvalDate AND COALESCE(JR.finalDate, CURRENT_DATE)
                GROUP BY E.fullName, J.name, JR.ratePerHour
                ORDER BY E.fullName, JR.ratePerHour DESC";

                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("startDate", startDate);
                        cmd.Parameters.AddWithValue("endDate", endDate);

                        if (!string.IsNullOrEmpty(jobName))
                        {
                            cmd.Parameters.AddWithValue("jobName", jobName);
                        }

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new NotorietyViewModel
                                {
                                    FullName = reader.GetString(0),
                                    Position = reader.GetString(1),
                                    HoursWorked = Math.Round(reader.GetDouble(2), 2),
                                    RatePerHour = reader.GetDecimal(3),
                                    Salary = Math.Round(reader.GetDecimal(4), 2)
                                });
                            }
                        }
                    }
                }

                // Сумма всей зарплаты
                decimal totalSalary = results.Sum(r => r.Salary);

                // Создаем PDF документ
                using (MemoryStream ms = new MemoryStream())
                {
                    // Создаем документ iTextSharp
                    var document = new iTextSharp.text.Document(iTextSharp.text.PageSize.A4, 50, 50, 50, 50);
                    var writer = iTextSharp.text.pdf.PdfWriter.GetInstance(document, ms);
                    document.Open();

                    // Устанавливаем шрифт для поддержки кириллицы
                    string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
                    var baseFont = iTextSharp.text.pdf.BaseFont.CreateFont(fontPath, iTextSharp.text.pdf.BaseFont.IDENTITY_H, iTextSharp.text.pdf.BaseFont.EMBEDDED);
                    var font = new iTextSharp.text.Font(baseFont, 10, iTextSharp.text.Font.NORMAL);
                    var boldFont = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.BOLD);
                    var headerFont = new iTextSharp.text.Font(baseFont, 20, iTextSharp.text.Font.BOLD); 

                    // Добавляем заголовок с большим отступом 
                    var header = new iTextSharp.text.Paragraph(
                        "Відомість нарахування заробітної плати", headerFont);
                    header.Alignment = iTextSharp.text.Element.ALIGN_CENTER;
                    header.SpacingAfter = 48; 
                    document.Add(header);

                    // Создаем таблицу 
                    var table = new iTextSharp.text.pdf.PdfPTable(6);
                    table.WidthPercentage = 100;
                    table.SetWidths(new float[] { 5, 28, 18, 20, 13, 16 });
                    table.SpacingAfter = 30; 

                    // Добавляем заголовки столбцов с светло-серым фоном 
                    var headerBgColor = new iTextSharp.text.BaseColor(248, 248, 248); 

                    var cellHeader1 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("№", boldFont));
                    var cellHeader2 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("ПІБ", boldFont));
                    var cellHeader3 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Посада", boldFont));
                    var cellHeader4 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Кількість відпрацьованих годин", boldFont));
                    var cellHeader5 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Ставка за годину", boldFont));
                    var cellHeader6 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Заробітна плата", boldFont));

                    // Стилизуем заголовки
                    cellHeader1.BackgroundColor = headerBgColor;
                    cellHeader2.BackgroundColor = headerBgColor;
                    cellHeader3.BackgroundColor = headerBgColor;
                    cellHeader4.BackgroundColor = headerBgColor;
                    cellHeader5.BackgroundColor = headerBgColor;
                    cellHeader6.BackgroundColor = headerBgColor;

                    // Устанавливаем отступы внутри ячеек 
                    cellHeader1.Padding = 10;
                    cellHeader2.Padding = 10;
                    cellHeader3.Padding = 10;
                    cellHeader4.Padding = 10;
                    cellHeader5.Padding = 10;
                    cellHeader6.Padding = 10;

                    // Выравнивание текста влево
                    cellHeader1.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader2.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader3.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader4.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader5.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    cellHeader6.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;

                    // Границы ячеек 
                    var borderColor = new iTextSharp.text.BaseColor(229, 229, 229);
                    cellHeader1.BorderColor = borderColor;
                    cellHeader2.BorderColor = borderColor;
                    cellHeader3.BorderColor = borderColor;
                    cellHeader4.BorderColor = borderColor;
                    cellHeader5.BorderColor = borderColor;
                    cellHeader6.BorderColor = borderColor;

                    table.AddCell(cellHeader1);
                    table.AddCell(cellHeader2);
                    table.AddCell(cellHeader3);
                    table.AddCell(cellHeader4);
                    table.AddCell(cellHeader5);
                    table.AddCell(cellHeader6);

                    // Добавляем данные
                    for (int i = 0; i < results.Count; i++)
                    {
                        var item = results[i];

                        var cell1 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase((i + 1).ToString(), font));
                        var cell2 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(item.FullName, font));
                        var cell3 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(item.Position, font));
                        var cell4 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.HoursWorked} год.", font));
                        var cell5 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.RatePerHour} грн.", font));
                        var cell6 = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{item.Salary} грн.", font));

                        // Отступы 
                        cell1.Padding = 10;
                        cell2.Padding = 10;
                        cell3.Padding = 10;
                        cell4.Padding = 10;
                        cell5.Padding = 10;
                        cell6.Padding = 10;

                        // Выравнивание влево
                        cell1.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell2.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell3.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell4.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell5.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                        cell6.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;

                        // Границы ячеек
                        cell1.BorderColor = borderColor;
                        cell2.BorderColor = borderColor;
                        cell3.BorderColor = borderColor;
                        cell4.BorderColor = borderColor;
                        cell5.BorderColor = borderColor;
                        cell6.BorderColor = borderColor;

                        table.AddCell(cell1);
                        table.AddCell(cell2);
                        table.AddCell(cell3);
                        table.AddCell(cell4);
                        table.AddCell(cell5);
                        table.AddCell(cell6);
                    }

                    // Создаем строку итога
                    // Добавляем пустые ячейки для первых 4 колонок
                    var emptyCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase(""));
                    emptyCell.Border = 0;

                    // Ячейка "Загальна сума:" с выравниванием вправо на 5 колонок
                    var totalLabelCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase("Загальна сума:", boldFont));
                    totalLabelCell.Colspan = 5; 
                    totalLabelCell.HorizontalAlignment = iTextSharp.text.Element.ALIGN_RIGHT;
                    totalLabelCell.Padding = 10;
                    totalLabelCell.Border = iTextSharp.text.Rectangle.BOX;
                    totalLabelCell.BorderColor = borderColor;
                    table.AddCell(totalLabelCell);

                    // Значение суммы зеленым цветом в последней колонке
                    var totalColor = new iTextSharp.text.BaseColor(0, 191, 165); 
                    var totalFont = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.BOLD, totalColor);
                    var totalValueCell = new iTextSharp.text.pdf.PdfPCell(new iTextSharp.text.Phrase($"{totalSalary} грн.", totalFont));
                    totalValueCell.HorizontalAlignment = iTextSharp.text.Element.ALIGN_LEFT;
                    totalValueCell.Padding = 10;
                    totalValueCell.Border = iTextSharp.text.Rectangle.BOX;
                    totalValueCell.BorderColor = borderColor;
                    table.AddCell(totalValueCell);

                    document.Add(table);

                    // Добавляем подписи 
                    var signatureBlock = new iTextSharp.text.Paragraph();
                    signatureBlock.SpacingBefore = 50; 
                    signatureBlock.Add(new iTextSharp.text.Chunk("Директор ______________________", font));
                    document.Add(signatureBlock);

                    var accountantSignature = new iTextSharp.text.Paragraph();
                    accountantSignature.SpacingBefore = 30; 
                    accountantSignature.Add(new iTextSharp.text.Chunk("Головний бухгалтер ______________________", font));
                    document.Add(accountantSignature);

                    document.Close();

                    // Определяем имя файла
                    string periodDescription;
                    if (period == "current_month")
                    {
                        periodDescription = startDate.Value.ToString("yyyy-MM");
                    }
                    else if (period == "half_year")
                    {
                        periodDescription = "half-year";
                    }
                    else if (period == "year")
                    {
                        periodDescription = "year";
                    }
                    else if (period == "all_time")
                    {
                        periodDescription = "all-time";
                    }
                    else
                    {
                        periodDescription = $"{startDate.Value:yyyy-MM-dd}_{endDate.Value:yyyy-MM-dd}";
                    }

                    string fileName = $"all_employees_payroll_{periodDescription}.pdf";

                    // Возвращаем PDF файл
                    return File(ms.ToArray(), "application/pdf", fileName);
                }
            }
            catch (Exception ex)
            {
                return Content($"Помилка при завантаженні відомості: {ex.Message}");
            }
        }
    }
}
