using System.ComponentModel.DataAnnotations;

namespace PresenceTabMalik.Models
{
    public class Tab
    {
        [Required(ErrorMessage = "Время прихода обязательно")]
        [Display(Name = "Время прихода")]
        public TimeSpan ArrivalTime { get; set; }

        [Display(Name = "Время ухода")]
        public TimeSpan? LeavingTime { get; set; }

        // Метод для проверки корректности временного диапазона
        public bool IsValidTimeRange()
        {
            if (!LeavingTime.HasValue) return true;
            return LeavingTime.Value > ArrivalTime;
        }

        // Безопасные методы форматирования времени
        public string GetFormattedArrivalTime()
        {
            try
            {
                return ArrivalTime.ToString(@"hh\:mm");
            }
            catch
            {
                return "--:--";
            }
        }

        public string GetFormattedLeavingTime()
        {
            if (!LeavingTime.HasValue) return "--:--";

            try
            {
                return LeavingTime.Value.ToString(@"hh\:mm");
            }
            catch
            {
                return "--:--";
            }
        }
    }
}