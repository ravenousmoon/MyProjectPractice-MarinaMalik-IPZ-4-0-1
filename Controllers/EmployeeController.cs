using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PresenceTabMalik.Models;
using System.Linq;

namespace PresenceTabMalik.Controllers
{
    public class EmployeeController : Controller
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private string ConnectionString => _httpContextAccessor.HttpContext?.Session.GetString("ConnectionString");

        public EmployeeController(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public IActionResult Index()
        {
            return View();
        }

        public async Task<IActionResult> Schedule(int? year, int? month)
        {
            try
            {
                var username = HttpContext.Session.GetString("Username");
                if (string.IsNullOrEmpty(username))
                    return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                var currentDate = year.HasValue && month.HasValue
                    ? new DateTime(year.Value, month.Value, 1)
                    : DateTime.Now;

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Получаем ID сотрудника
                    int employeeId;
                    using (var cmd = new NpgsqlCommand(@"
   SELECT id 
   FROM employeeUser 
   LIMIT 1", conn))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач не знайдений в базі даних.");
                        employeeId = Convert.ToInt32(result);
                    }

                    var firstDay = new DateTime(currentDate.Year, currentDate.Month, 1);
                    var lastDay = firstDay.AddMonths(1).AddDays(-1);

                    var currentWeekStart = firstDay;
                    var dayOfWeek = (int)currentWeekStart.DayOfWeek;
                    if (dayOfWeek == 0) 
                        currentWeekStart = currentWeekStart.AddDays(-6); 
                    else
                        currentWeekStart = currentWeekStart.AddDays(-(dayOfWeek - 1)); 

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
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
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
                return Content($"Помилка бази даних: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Content($"Неочікувана помилка: {ex.Message}");
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
                FROM employeeUser 
                LIMIT 1", conn))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач не знайдений в базі даних.");
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
                FROM employeeUser 
                LIMIT 1", conn))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач не знайдений в базі даних.");
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

        public async Task<IActionResult> Statistics(DateTime? startDate = null, DateTime? endDate = null)
        {
            try
            {
                var username = HttpContext.Session.GetString("Username");
                if (string.IsNullOrEmpty(username))
                    return Content("Помилка: Сесія користувача не знайдена. Необхідно увійти в систему.");

                startDate ??= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                endDate ??= startDate.Value.AddMonths(1).AddDays(-1);

                using (var conn = new NpgsqlConnection(ConnectionString))
                {
                    await conn.OpenAsync();

                    // Получаем ID сотрудника
                    int employeeId;
                    using (var cmd = new NpgsqlCommand(@"
   SELECT id 
   FROM employeeUser 
   LIMIT 1", conn))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return Content($"Помилка: Користувач не знайдений в базі даних.");
                        employeeId = Convert.ToInt32(result);
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
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
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
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
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
                        cmd.Parameters.AddWithValue("employeeId", employeeId);
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
    }
}
