using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Npgsql;
using PresenceTabMalik.Models;
using System.Linq;
using System.Globalization;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Diagnostics;

namespace PresenceTabMalik.Controllers
{
    public class ManagerController : Controller
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private string ConnectionString => _httpContextAccessor.HttpContext?.Session.GetString("ConnectionString");

        public ManagerController(IHttpContextAccessor httpContextAccessor)
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
        public async Task<IActionResult> CreateSchedule(int employeeId, DateTime beginDate, DateTime? endDate, string status)
        {
            try
            {
                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    using (var cmd = new NpgsqlCommand(@"
                INSERT INTO StInstEmp (idEmployee, beginDate, finalDate, status)
                VALUES (@employeeId, @beginDate, @endDate, @status::status_presence)", conn))
                    {
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
                        cmd.Parameters.AddWithValue("beginDate", beginDate);
                        cmd.Parameters.AddWithValue("endDate", endDate.HasValue ? endDate.Value : (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("status", status);
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
    }
}
