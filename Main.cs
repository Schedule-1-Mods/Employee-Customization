using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MyFirstMod.Core), "Employee Customization", "1.0.0", "Cassy")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MyFirstMod
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Employee Customization loaded! Look at an employee and press E to edit their appearance.");
            MelonEvents.OnGUI.Subscribe(EmployeeEditor.Draw);
        }

        public override void OnUpdate()
        {
            EmployeeEditor.UpdateVisibility();
            EmployeeEditor.TickPendingReapply();
            MenuOutfit.Tick();
            UmbrellaFix.Tick();

            if (Input.GetKeyDown(KeyCode.E))
            {
                EmployeeEditor.TryInteract();
            }

            if (Input.GetKeyDown(KeyCode.Escape) && EmployeeEditor.Visible)
            {
                EmployeeEditor.TryClose();
            }
        }

        // Fires after a scene finishes loading (e.g. loading into a save) - arms a retry window
        // (drained by TickPendingReapply every frame) that restores any saved employee
        // customisation the game's own save file doesn't preserve, even if employees are still
        // spawning in when this fires.
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            EmployeeEditor.OnSceneLoaded();
            MenuOutfit.OnSceneLoaded(sceneName);
        }
    }
}
