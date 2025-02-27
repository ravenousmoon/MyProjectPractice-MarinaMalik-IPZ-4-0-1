using System;
using System.Collections.Generic;

namespace PresenceTabMalik.Models
{
    public class ScheduleViewModel
    {
        public DateTime CurrentDate { get; set; }
        public List<Schedule> Schedules { get; set; } = new List<Schedule>();
        public double TotalHours { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }
}