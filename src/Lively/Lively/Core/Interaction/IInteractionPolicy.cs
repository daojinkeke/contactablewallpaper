using Lively.Models.Interaction;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Interaction forwarding policy, see docs/contracts/05-module-interfaces.md §4.1
    /// and docs/contracts/06-interaction-spec.md §4.2.
    /// </summary>
    public interface IInteractionPolicy
    {
        bool IsEnabled { get; }
        bool ShouldForward(InteractionContext context);
    }
}
