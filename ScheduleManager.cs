using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public class ScheduleManager
{
    private readonly string _connectionString;
    private List<TimeSlot> _schedule;

    public ScheduleManager(string connectionString)
    {
        _connectionString = connectionString;
        _schedule = new List<TimeSlot>();
    }

    public void LoadSchedule()
    {
        string scheduleString = FetchScheduleFromDb();
        _schedule = ParseSchedule(scheduleString);
    }

    private string FetchScheduleFromDb()
    {
        const string query = "SELECT [Value] FROM [dbo].[AppConfig] WHERE [Key] ='LNAB_Schedule'";
        using (var connection = new SqlConnection(_connectionString))
        {
            using (var command = new SqlCommand(query, connection))
            {
                connection.Open();
                var result = command.ExecuteScalar();
                return result?.ToString() ?? string.Empty;
            }
        }
    }

    private List<TimeSlot> ParseSchedule(string scheduleString)
    {
        var schedule = new List<TimeSlot>();
        if (string.IsNullOrWhiteSpace(scheduleString))
        {
            return schedule;
        }

        var parts = scheduleString.Split('/');
        foreach (var part in parts)
        {
            var slotParts = part.Split(';');
            if (slotParts.Length == 3 &&
                int.TryParse(slotParts[0], out int dayOfWeek) &&
                TimeSpan.TryParseExact(slotParts[1], "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan startTime) &&
                TimeSpan.TryParseExact(slotParts[2], "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan endTime))
            {
                schedule.Add(new TimeSlot
                {
                    DayOfWeek = (DayOfWeek)dayOfWeek,
                    StartTime = startTime,
                    EndTime = endTime
                });
            }
        }

        // Apply Monday's schedule to all weekdays if it exists
        var mondaySchedule = schedule.FirstOrDefault(s => s.DayOfWeek == DayOfWeek.Monday);
        if (mondaySchedule != null)
        {
            // Remove any existing schedules for Tuesday through Friday
            schedule.RemoveAll(s => s.DayOfWeek >= DayOfWeek.Tuesday && s.DayOfWeek <= DayOfWeek.Friday);

            // Add Monday's schedule for Tuesday through Friday
            for (int i = (int)DayOfWeek.Tuesday; i <= (int)DayOfWeek.Friday; i++)
            {
                schedule.Add(new TimeSlot
                {
                    DayOfWeek = (DayOfWeek)i,
                    StartTime = mondaySchedule.StartTime,
                    EndTime = mondaySchedule.EndTime
                });
            }
        }

        return schedule;
    }

    public bool IsWithinScheduledTime()
    {
        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;
        var currentDay = now.DayOfWeek;

        return _schedule.Any(slot =>
            slot.DayOfWeek == currentDay &&
            currentTime >= slot.StartTime &&
            currentTime <= slot.EndTime);
    }

    public TimeSpan GetTimeToNextTransition()
    {
        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;
        var currentDay = now.DayOfWeek;

        var nextTransitions = new List<TimeSpan>();

        // Find next start or end time on the same day
        foreach (var slot in _schedule.Where(s => s.DayOfWeek == currentDay))
        {
            if (slot.StartTime > currentTime)
            {
                nextTransitions.Add(slot.StartTime - currentTime);
            }
            if (slot.EndTime > currentTime)
            {
                nextTransitions.Add(slot.EndTime - currentTime);
            }
        }

        // If no more transitions today, find the next one in the coming days
        if (!nextTransitions.Any())
        {
            for (int i = 1; i <= 7; i++)
            {
                var nextDay = (DayOfWeek)(((int)currentDay + i) % 7);
                var nextDaySlot = _schedule.OrderBy(s => s.StartTime).FirstOrDefault(s => s.DayOfWeek == nextDay);
                if (nextDaySlot != null)
                {
                    var timeUntilNextDay = TimeSpan.FromDays(i) - currentTime + nextDaySlot.StartTime;
                    nextTransitions.Add(timeUntilNextDay);
                    break;
                }
            }
        }

        // Return the smallest TimeSpan, or a default if no schedule is found
        return nextTransitions.Any() ? nextTransitions.Min() : TimeSpan.FromMinutes(20); // Default check
    }
}

public class TimeSlot
{
    public DayOfWeek DayOfWeek { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
}
