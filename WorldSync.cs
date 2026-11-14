using System;
using System.Globalization;
using PixelCrushers.DialogueSystem;
using UnityEngine;
using UnityEngine.AzureSky;

internal static class WorldSync
{
    public const float ClockInterval = 1f;
    public const float WeatherInterval = 5f;
    private const float StaleAfter = 5f;
    private const float Hours = 24f;
    private const float Progression = 0.4f;
    private const float RestoreWeatherSeconds = 5f;
    private const float MaxScale = 1000f;
    private const float MaxTransition = 600f;
    private const int MaxIndex = 1000;
    private const int MaxDayCounter = 10000000;
    private const string DayVariable = "Day";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static float SnapMinutes = 5f;
    public static float SmoothSeconds = 2f;
    private static TimeAndDay ownClock;
    private static int ownWeather;
    private static TimeOfDayAzure sessionTime;
    private static TimeOfDayAzure swapped;
    private static TimeAndDay liveClock;
    private static int liveDayCounter;
    private static int liveLuaDay;
    private static bool luaSwapped;
    private static float sampleTimeline;
    private static float sampleScale;
    private static bool samplePaused;
    private static float sampleAt;
    private static float shown;
    private static bool hasShown;
    private static bool haveSent;
    private static int sentYear;
    private static int sentMonth;
    private static int sentDay;
    private static int sentCounter;
    private static float sentScale;
    private static bool sentPaused;
    private static float nextClock;
    private static float nextWeather;
    private static bool weatherDue;
    private static float lastTransition;

    internal struct ClockSample
    {
        public float Timeline;
        public int Year;
        public int Month;
        public int Day;
        public int DayCounter;
        public float Scale;
        public bool Paused;
    }

    public static bool Following
    {
        get { return ownClock != null; }
    }

    public static bool Saving
    {
        get { return swapped != null; }
    }

    public static TimeAndDay OwnClock
    {
        get { return ownClock; }
    }

    public static int OwnWeather
    {
        get { return ownWeather; }
    }

    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (ownClock != null)
            {
                if (!Session.IsInLobby || Session.IsHost || now - sampleAt > StaleAfter) End();
                else Validate();
            }
            if (WorldHooks.Timing)
            {
                BedVote.Tick(now);
                TimeSpeed.Tick(now);
            }
            if (Session.IsHost)
            {
                TimeSpeed.ApplyHostScale();
                SendHost(now);
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldSync.Tick", e);
        }
    }

    public static void Handle(ulong sender, string text)
    {
        if (text.StartsWith("CLK|", StringComparison.Ordinal)) HandleClock(sender, text);
        else if (text.StartsWith("WEA|", StringComparison.Ordinal)) HandleWeather(sender, text);
        else if (text.StartsWith("BED", StringComparison.Ordinal) || text == "WAKE" || text.StartsWith("SPEED|", StringComparison.Ordinal) || text.StartsWith("FAST|", StringComparison.Ordinal)) HandleTiming(sender, text);
    }

    private static void HandleTiming(ulong sender, string text)
    {
        if (!WorldHooks.Timing) return;
        BedVote.Handle(sender, text);
        TimeSpeed.Handle(sender, text);
    }

    public static void HandleClock(ulong sender, string text)
    {
        try
        {
            if (!IsHostPeer(sender)) return;
            ClockSample sample;
            if (!ParseClock(text, out sample))
            {
                CoopLog.Log.Debug("world.clock", "ignored malformed CLK from " + sender);
                return;
            }
            if (ownClock != null) Validate();
            if (ownClock == null && !Begin()) return;
            sampleTimeline = sample.Timeline;
            sampleScale = sample.Scale;
            samplePaused = sample.Paused;
            sampleAt = Time.unscaledTime;
            ApplyDate(sample);
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldSync.HandleClock", e);
        }
    }

    public static void HandleWeather(ulong sender, string text)
    {
        try
        {
            if (ownClock == null || !IsHostPeer(sender)) return;
            int index;
            float transition;
            if (!ParseWeather(text, out index, out transition))
            {
                CoopLog.Log.Debug("world.weather", "ignored malformed WEA from " + sender);
                return;
            }
            WeatherManager weather = WeatherManager.instance;
            if (weather == null || index >= weather.WeatherCount() || weather.WeatherName(index) == null || weather.ActiveWeatherIndex() == index) return;
            weather.SetWeather(index, true, transition);
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldSync.HandleWeather", e);
        }
    }

    public static void OverrideClock(AzureTimeController controller)
    {
        if (ownClock == null || !Validate()) return;
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        if (time.TimeController != controller) return;
        if (time.updateTimeDisabled)
        {
            hasShown = false;
            return;
        }
        float rate = samplePaused ? 0f : RatePerSecond(sampleScale);
        float predicted = Wrap(sampleTimeline + rate * (Time.unscaledTime - sampleAt));
        if (!hasShown)
        {
            shown = predicted;
            hasShown = true;
        }
        else
        {
            float gap = WrapGap(predicted - shown);
            if (Mathf.Abs(gap) > SnapMinutes / 60f)
            {
                shown = predicted;
            }
            else
            {
                float blend = SmoothSeconds > 0f ? Mathf.Min(1f, Time.unscaledDeltaTime / SmoothSeconds) : 1f;
                shown = Wrap(shown + rate * Time.unscaledDeltaTime + gap * blend);
            }
        }
        controller.SetTimeline(shown);
    }

    public static void HostWeatherSet(float transitionTime)
    {
        if (!Session.IsHost) return;
        lastTransition = transitionTime;
        weatherDue = true;
    }

    public static void LoadedClock(TimeAndDay saved)
    {
        if (ownClock != null && saved != null && saved.year != 0) ownClock = saved.CopyValues();
    }

    public static void LoadedWeather(int index)
    {
        if (ownClock != null) ownWeather = index;
    }

    public static bool BeginSave()
    {
        if (swapped != null || !Validate()) return false;
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        swapped = time;
        liveClock = time.currentTimeAndDay;
        liveDayCounter = time.dayCounter;
        try
        {
            time.currentTimeAndDay = ownClock.CopyValues();
            time.dayCounter = ownClock.currentDay;
            liveLuaDay = DialogueLua.GetVariable(DayVariable).AsInt;
            luaSwapped = true;
            SetLuaDay(ownClock.currentDay);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldSync.BeginSave", e);
            EndSave();
            return false;
        }
    }

    public static void EndSave()
    {
        TimeOfDayAzure time = swapped;
        if (time == null) return;
        TimeAndDay clock = liveClock;
        int counter = liveDayCounter;
        int luaDay = liveLuaDay;
        bool restoreLua = luaSwapped;
        swapped = null;
        liveClock = null;
        luaSwapped = false;
        time.currentTimeAndDay = clock;
        time.dayCounter = counter;
        try
        {
            if (restoreLua) SetLuaDay(luaDay);
        }
        catch (Exception e)
        {
            CoopLog.Once("WorldSync.EndSave", e);
        }
    }

    internal static bool ParseClock(string text, out ClockSample sample)
    {
        sample = new ClockSample();
        string[] parts = text.Split('|');
        if (parts.Length != 8 || parts[0] != "CLK") return false;
        float timeline, scale;
        int year, month, day, dayCounter, paused;
        if (!ReadFloat(parts[1], out timeline) || !ReadFloat(parts[6], out scale)) return false;
        if (!ReadInt(parts[2], out year) || !ReadInt(parts[3], out month) || !ReadInt(parts[4], out day)) return false;
        if (!ReadInt(parts[5], out dayCounter) || !ReadInt(parts[7], out paused)) return false;
        if (timeline < 0f || timeline >= Hours + 1f || scale < 0f || scale > MaxScale) return false;
        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        if (dayCounter < 0 || dayCounter > MaxDayCounter || (paused != 0 && paused != 1)) return false;
        sample.Timeline = timeline;
        sample.Year = year;
        sample.Month = month;
        sample.Day = day;
        sample.DayCounter = dayCounter;
        sample.Scale = scale;
        sample.Paused = paused == 1;
        return true;
    }

    internal static bool ParseWeather(string text, out int index, out float transition)
    {
        index = -1;
        transition = 0f;
        string[] parts = text.Split('|');
        if (parts.Length != 3 || parts[0] != "WEA") return false;
        return ReadInt(parts[1], out index) && index >= 0 && index <= MaxIndex
            && ReadFloat(parts[2], out transition) && transition >= 0f && transition <= MaxTransition;
    }

    private static bool ReadFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, Inv, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool ReadInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, Inv, out value);
    }

    private static bool IsHostPeer(ulong sender)
    {
        return Session.IsInLobby && !Session.IsHost && sender == Session.HostId;
    }

    private static bool Validate()
    {
        if (ownClock == null) return false;
        if (sessionTime != null && sessionTime == TimeOfDayAzure.instance) return true;
        ownClock = null;
        sessionTime = null;
        hasShown = false;
        CoopLog.Log.Info("guest clock session dropped: the world was replaced");
        return false;
    }

    private static bool Begin()
    {
        if (!WorldHooks.Ready)
        {
            CoopLog.Once("WorldSync.hooks", "clock sync refused: the world hooks are not all installed");
            return false;
        }
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        WeatherManager weather = WeatherManager.instance;
        if (time == null || time.TimeController == null || weather == null || time.updateTimeDisabled) return false;
        ownClock = new TimeAndDay();
        ownClock.SetToCurrentTimeAndDay();
        ownWeather = weather.ActiveWeatherIndex();
        sessionTime = time;
        hasShown = false;
        CoopLog.Log.Info("guest clock session started; local clock held for the session");
        return true;
    }

    private static void End()
    {
        if (!Validate()) return;
        TimeAndDay own = ownClock;
        int weather = ownWeather;
        TimeOfDayAzure time = sessionTime;
        ownClock = null;
        sessionTime = null;
        hasShown = false;
        WeatherManager manager = WeatherManager.instance;
        if (time.TimeController == null) return;
        time.SetTimeline(own.GetTimeLine());
        time.SetDate(own.year, own.month, own.day);
        time.dayCounter = own.currentDay;
        SetLuaDay(own.currentDay);
        if (manager != null && weather >= 0 && weather < manager.WeatherCount() && manager.ActiveWeatherIndex() != weather)
        {
            manager.SetWeather(weather, true, RestoreWeatherSeconds);
        }
        CoopLog.Log.Info("guest clock session ended; local clock and weather restored");
    }

    private static void SetLuaDay(int day)
    {
        if (DialogueLua.GetVariable(DayVariable).AsInt != day) DialogueLua.SetVariable(DayVariable, day);
    }

    private static void ApplyDate(ClockSample sample)
    {
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        if (time == null || time.updateTimeDisabled) return;
        if (time.GetYear() != sample.Year || time.GetMonth() != sample.Month || time.GetDay() != sample.Day)
        {
            time.SetDate(sample.Year, sample.Month, sample.Day);
        }
        time.dayCounter = sample.DayCounter;
    }

    private static float RatePerSecond(float scale)
    {
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        if (time == null || time.dayLenghtInMinutes <= 0f) return 0f;
        return Progression * scale / time.dayLenghtInMinutes;
    }

    private static float Wrap(float hour)
    {
        return hour - Hours * Mathf.Floor(hour / Hours);
    }

    private static float WrapGap(float gap)
    {
        return gap - Hours * Mathf.Floor((gap + Hours / 2f) / Hours);
    }

    private static void SendHost(float now)
    {
        TimeOfDayAzure time = TimeOfDayAzure.instance;
        if (time == null || time.TimeController == null) return;
        int year = time.GetYear();
        int month = time.GetMonth();
        int day = time.GetDay();
        int counter = time.dayCounter;
        float scale = time.timeSpeedOverrideFactor;
        bool paused = Time.timeScale <= 0f || time.updateTimeDisabled;
        bool changed = !haveSent || year != sentYear || month != sentMonth || day != sentDay || counter != sentCounter || !scale.Equals(sentScale) || paused != sentPaused;
        if (changed || now >= nextClock)
        {
            haveSent = true;
            sentYear = year;
            sentMonth = month;
            sentDay = day;
            sentCounter = counter;
            sentScale = scale;
            sentPaused = paused;
            nextClock = now + ClockInterval;
            Broadcast(string.Format(Inv, "CLK|{0:F4}|{1}|{2}|{3}|{4}|{5:F4}|{6}", time.CurrentTimelineValue, year, month, day, counter, scale, paused ? 1 : 0), changed);
        }
        WeatherManager weather = WeatherManager.instance;
        if (weather != null && (weatherDue || now >= nextWeather))
        {
            weatherDue = false;
            nextWeather = now + WeatherInterval;
            Broadcast(string.Format(Inv, "WEA|{0}|{1:F2}", weather.ActiveWeatherIndex(), lastTransition), true);
        }
    }

    private static void Broadcast(string text, bool reliable)
    {
        Session.SendAll("world", text, reliable);
    }
}
