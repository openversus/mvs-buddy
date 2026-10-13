namespace MvsBuddy.Catalog;

/// <summary>Which servers a release is for.</summary>
public enum Channel
{
    /// <summary>The production servers. A release whose tag is exactly a version, such as <c>2026.04.08.14</c>.</summary>
    Prod,

    /// <summary>The testing instance. A release whose tag ends in <c>-testing</c>, such as <c>2026.10.12.01-testing</c>.</summary>
    Testing,
}
