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
    internal static class UmbrellaFix
    {
        private const float RainThreshold = 0.05f;
        private const float CheckInterval = 2f;
        private static float _nextCheck;

        // HasUmbrella's setter is private in the game's own code - Employee inherits it publicly
        // readable from NPC, so this reflects into the same setter the game's own code uses.
        private static readonly MethodInfo _setHasUmbrella = typeof(Employee).GetProperty("HasUmbrella")?.GetSetMethod(true);

        public static void Tick()
        {
            if (_setHasUmbrella == null || Time.unscaledTime < _nextCheck || !EmployeeManager.InstanceExists) return;
            _nextCheck = Time.unscaledTime + CheckInterval;

            foreach (Employee employee in EmployeeManager.Instance.AllEmployees)
            {
                if (employee == null || employee.HasUmbrella) continue;

                var conditions = employee.GetCurrentWeatherConditionsForEnitty();
                if (conditions == null || conditions.Rainy < RainThreshold) continue;

                try { _setHasUmbrella.Invoke(employee, new object[] { true }); }
                catch (System.Exception ex) { MelonLogger.Msg("[UmbrellaFix] " + ex.Message); }
            }
        }
    }
}
