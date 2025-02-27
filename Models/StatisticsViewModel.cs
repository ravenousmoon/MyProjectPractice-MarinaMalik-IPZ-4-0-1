namespace PresenceTabMalik.Models
{
    public class StatisticsViewModel
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public Dictionary<string, int> StatusCounts { get; set; }
        public decimal PeriodSalary { get; set; }
        public List<MonthlySalary> MonthlySalaries { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }
}