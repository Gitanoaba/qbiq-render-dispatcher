using Autodesk.Revit.UI;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// IExternalEventHandler invoked on the Revit main thread by the auto-poll timer
    /// (via ExternalEvent.Raise). Picks and processes the next pending ticket without
    /// showing any dialogs - suitable for unattended render server operation.
    /// </summary>
    public class AutoRenderHandler : IExternalEventHandler
    {
        public void Execute(UIApplication uiApp)
        {
            if (RenderCoordinator.IsBusy)
            {
                Logger.Info("AutoRender", "Skipping: render already in progress.");
                return;
            }

            Logger.Info("AutoRender", "ExternalEvent fired â€” starting TryAutoProcess.");
            ProcessNextCommand.TryAutoProcess(uiApp);
        }

        public string GetName()
        {
            return "QbiqAutoRenderHandler";
        }
    }
}
