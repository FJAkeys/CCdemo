namespace Unity.Build
{
    /// <summary>
    /// List of possible build types.
    /// </summary>
    public enum BuildType : int
    {
        /// <summary>
        /// Enables debug information, asserts and development code.
        /// </summary>
        Debug = 1,

        /// <summary>
        /// Enables optimizations and development code.
        /// </summary>
        Develop = 2,

        /// <summary>
        /// Enables optimizations.
        /// </summary>
        Release = 3
    }
}
