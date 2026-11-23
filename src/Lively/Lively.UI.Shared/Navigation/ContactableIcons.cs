namespace Lively.UI.Shared.Navigation
{
    /// <summary>
    /// Product owned shell icon geometries (24x24 viewbox, filled path).
    /// Black hole family: every glyph is an accretion ring around an event horizon,
    /// the module mark sits inside the lens. Intentionally unrelated to Lively artwork.
    /// </summary>
    public static class ContactableIcons
    {
        /// <summary>Accretion ring: outer disc minus the event horizon lens.</summary>
        private const string Ring =
            "M2.1 12 A9.9 9.9 0 1 1 21.9 12 A9.9 9.9 0 1 1 2.1 12 Z " +
            "M5 12 A7 7 0 1 0 19 12 A7 7 0 1 0 5 12 Z ";

        /// <summary>Lens mark of the create module: a plus drawn inside the horizon.</summary>
        public const string Create =
            "F1 " + Ring +
            "M11 7.4 H13 V11 H16.6 V13 H13 V16.6 H11 V13 H7.4 V11 H11 Z";

        /// <summary>Lens mark of the library module: three receding bands.</summary>
        public const string Library =
            "F1 " + Ring +
            "M7.2 8.6 H16.8 V10.1 H7.2 Z M7.95 11.25 H16.05 V12.75 H7.95 Z M8.7 13.9 H15.3 V15.4 H8.7 Z";

        /// <summary>Lens mark of the preview module: a play wedge.</summary>
        public const string Preview =
            "F1 " + Ring +
            "M10.1 8.4 L15.9 12 L10.1 15.6 Z";

        /// <summary>Lens mark of the settings module: two sliders with knobs.</summary>
        public const string Settings =
            "F1 " + Ring +
            "M7.2 9 H16.8 V10.35 H7.2 Z M7.2 13.65 H16.8 V15 H7.2 Z " +
            "M9.3 7.5 H11 V11.85 H9.3 Z M13 12.15 H14.7 V16.5 H13 Z";
    }
}
