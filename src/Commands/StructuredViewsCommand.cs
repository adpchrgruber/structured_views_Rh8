using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace StructuredViews
{
    /// <summary>Opens the Structured Views panel.</summary>
    public class StructuredViewsCommand : Command
    {
        public StructuredViewsCommand()
        {
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static StructuredViewsCommand Instance { get; private set; }

        public override string EnglishName => "StructuredViews";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(StructuredViewsPanel.PanelId);
            return Result.Success;
        }
    }
}
