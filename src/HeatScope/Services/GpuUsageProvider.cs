using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HeatScope.Services;

/// <summary>
/// Загрузка GPU по процессам через счётчики производительности "GPU Engine".
/// Работает для NVIDIA / AMD / Intel на Windows 10+ без vendor-SDK.
/// </summary>
public sealed class GpuUsageProvider : IDisposable
{
    private const string CategoryName = "GPU Engine";
    private const string CounterName = "Utilization Percentage";

    private readonly Dictionary<string, PerformanceCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex PidRegex = new(@"^pid_(\d+)_", RegexOptions.Compiled);

    private PerformanceCounterCategory? _category;
    private DateTime _categoryProbeUtc = DateTime.MinValue;

    /// <summary>pid -> максимальная загрузка среди движков GPU (%).</summary>
    public Dictionary<int, double> Read()
    {
        var result = new Dictionary<int, double>();
        try
        {
            if (_category is null && (DateTime.UtcNow - _categoryProbeUtc).TotalSeconds > 30)
            {
                _categoryProbeUtc = DateTime.UtcNow;
                try { _category = new PerformanceCounterCategory(CategoryName); }
                catch { _category = null; }
            }
            if (_category is null) return result;

            string[] instances;
            try { instances = _category.GetInstanceNames(); }
            catch { _category = null; return result; }

            var alive = new HashSet<string>(instances, StringComparer.OrdinalIgnoreCase);

            foreach (var instance in instances)
            {
                var m = PidRegex.Match(instance);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int pid) || pid <= 0) continue;

                if (!_counters.TryGetValue(instance, out var counter))
                {
                    try
                    {
                        counter = new PerformanceCounter(CategoryName, CounterName, instance, readOnly: true);
                        _counters[instance] = counter;
                    }
                    catch { continue; }
                }

                double value;
                try { value = counter.NextValue(); }
                catch { continue; }
                if (value <= 0) continue;

                if (result.TryGetValue(pid, out double current))
                    result[pid] = Math.Max(current, value);
                else
                    result[pid] = value;
            }

            // убрать исчезнувшие инстансы
            var dead = _counters.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (var key in dead)
            {
                try { _counters[key].Dispose(); } catch { }
                _counters.Remove(key);
            }
        }
        catch
        {
            // счётчики GPU недоступны — не критично
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values)
        {
            try { c.Dispose(); } catch { }
        }
        _counters.Clear();
    }
}
