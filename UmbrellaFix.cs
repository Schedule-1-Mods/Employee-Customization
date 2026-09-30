using System.Reflection;
using Il2CppScheduleOne.Employees;
using MelonLoader;
using UnityEngine;

namespace MyFirstMod
{
    // Each employee only has a per-personality chance (WeatherBehaviour.UseUmbrellaChance) of
    // actually using an umbrella when caught in the rain, so some are seen standing out in it with
    // none. This forces every employee's HasUmbrella flag on whenever they're actually exposed to
    // rain (not just sheltering indoors), so they always have one.
    //
    // Asking the weather system about one employee isn't free, and there can be dozens of them: a
    // pass over all of them in one frame was a hitch of 100 ms or more, every two seconds. So a
    // pass is spread over many frames (a few employees per frame), then rests before the next.
    internal static class UmbrellaFix
    {
        private const float RainThreshold = 0.05f;
        private const float CheckInterval = 2f;   // rest between passes
        private const int PerFrame = 2;           // employees looked at per frame
        private static float _nextCheck;
        private static int _cursor = -1;          // -1: resting; else the next employee of this pass

        // HasUmbrella's setter is private in the game's own code - Employee inherits it publicly
        // readable from NPC, so this reflects into the same setter the game's own code uses.
        private static readonly MethodInfo _setHasUmbrella = typeof(Employee).GetProperty("HasUmbrella")?.GetSetMethod(true);

        public static void Tick()
        {
            if (_setHasUmbrella == null || !EmployeeManager.InstanceExists) return;
            if (_cursor < 0)
            {
                if (Time.unscaledTime < _nextCheck) return;
                _cursor = 0;
            }

            var all = EmployeeManager.Instance.AllEmployees;
            int end = System.Math.Min(all.Count, _cursor + PerFrame);
            for (; _cursor < end; _cursor++)
            {
                Employee employee = all[_cursor];
                if (employee == null || employee.HasUmbrella) continue;

                var conditions = employee.GetCurrentWeatherConditionsForEnitty();
                if (conditions == null || conditions.Rainy < RainThreshold) continue;

                try { _setHasUmbrella.Invoke(employee, new object[] { true }); }
                catch (System.Exception ex) { MelonLogger.Msg("[UmbrellaFix] " + ex.Message); }
            }

            if (_cursor >= all.Count)
            {
                _cursor = -1;
                _nextCheck = Time.unscaledTime + CheckInterval;
            }
        }
    }
}
