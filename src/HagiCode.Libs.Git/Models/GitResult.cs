using System.Diagnostics.CodeAnalysis;

namespace HagiCode.Libs.Git;

/// <summary>
/// The outcome of an operation without a payload. <see cref="Success" /> is true when <see cref="Failure" /> is null.
/// </summary>
public sealed record GitResult
{
    private GitResult()
    {
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Success => Failure is null;

    /// <summary>Gets the expected failure, or <see langword="null" /> on success.</summary>
    public GitFailure? Failure { get; private init; }

    /// <summary>Creates a successful result.</summary>
    public static GitResult Ok() => new();

    /// <summary>Creates a failed result.</summary>
    public static GitResult Fail(GitFailure failure) => new() { Failure = failure ?? throw new ArgumentNullException(nameof(failure)) };
}

/// <summary>
/// The outcome of an operation that produces a value. On failure <see cref="Value" /> is <see langword="null" />.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed record GitResult<T>
{
    private GitResult()
    {
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Success => Failure is null;

    /// <summary>Gets the payload on success; <see langword="null" /> otherwise.</summary>
    public T? Value { get; private init; }

    /// <summary>Gets the expected failure, or <see langword="null" /> on success.</summary>
    public GitFailure? Failure { get; private init; }

    /// <summary>Creates a successful result carrying <paramref name="value" />.</summary>
    public static GitResult<T> Ok(T? value) => new() { Value = value };

    /// <summary>Creates a failed result.</summary>
    public static GitResult<T> Fail(GitFailure failure) => new() { Failure = failure ?? throw new ArgumentNullException(nameof(failure)) };
}
