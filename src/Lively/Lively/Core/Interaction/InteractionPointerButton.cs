namespace Lively.Models.Interaction
{
    /// <summary>
    /// Pointer button identity shared by <c>IInputInjector</c>, <c>IInteractionRouter</c>
    /// and <c>DragTracker</c>.
    /// Values match the JS bridge <c>buttons</c> bitmask
    /// (docs/contracts/06-interaction-spec.md §6.3: 1 left, 2 right, 4 middle).
    /// </summary>
    /// <remarks>
    /// Contract namespace is Lively.Models.Interaction but the physical file lives under
    /// Core/Interaction to keep the A3 directory boundary.
    /// </remarks>
    public enum InteractionPointerButton
    {
        none = 0,
        left = 1,
        right = 2,
        middle = 4
    }
}
