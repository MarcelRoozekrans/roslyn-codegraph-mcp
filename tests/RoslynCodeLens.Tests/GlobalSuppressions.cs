// HLQ005: Assert.Single() is the correct xUnit assertion for "exactly one element" — not a performance concern in test code
// EPS06: ImmutableArray LINQ in test code — allocations irrelevant in test context
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("NetFabric.Hyperlinq", "HLQ005:AvoidSingleAnalyzer", Justification = "Assert.Single() is the correct xUnit assertion for exactly-one-element checks")]
[assembly: SuppressMessage("ErrorProne.NET", "EPS06:HiddenStructCopy", Justification = "ImmutableArray LINQ allocations are acceptable in test code")]
