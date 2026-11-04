using System;
using Xunit;

namespace ReleaseSync.Core.Tests;

/// <summary>A test of how Windows itself behaves, such as drive letters or refusing to delete an open file. It shows as skipped elsewhere and runs in the Windows build.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Only applies on Windows"; }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Only applies on Windows"; }
}
