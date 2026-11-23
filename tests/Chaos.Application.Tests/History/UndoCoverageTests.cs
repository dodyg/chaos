using System.Reflection;
using Chaos.Application.Abstractions;
using Chaos.Application.History;

namespace Chaos.Application.Tests.History;

/// <summary>
/// Which stores take part in undo is a decision, not an accident: <see cref="RecordingDataSession"/> wraps the stores whose writes
/// are recorded and passes the rest straight through. A new store has to be put on one list or the other, so a mutating store
/// cannot silently miss history and undo.
/// </summary>
public sealed class UndoCoverageTests
{
    /// <summary>Stores whose writes are recorded for history and undo.</summary>
    private static readonly string[] Recorded =
    [
        "Containers", "Sections", "Tasks", "Subtasks", "Templates", "Tags", "Diary", "Pad", "Pages", "Recurrence",
        "ReminderRules", "Waiting", "Attachments", "Spaces", "Delegations",
    ];

    /// <summary>
    /// Stores that pass through on purpose: read models, history and undo bookkeeping themselves, device-local or synced
    /// state that is not undone with a task edit, and the plumbing around a transaction.
    /// </summary>
    private static readonly string[] PassThroughOnPurpose =
    [
        "Profiles", "People", "Queries", "History", "Undo", "ChecklistConversions", "Notifications", "DayPlans", "Weeks",
        "Summaries", "Receipts", "Backups", "Imports", "GitHub", "Boards", "SavedViews", "Calendars", "Activity",
    ];

    [Test]
    public async Task EveryStoreIsEitherRecordedOrPassedThroughOnPurpose()
    {
        var properties = typeof(IDataSession).GetProperties();
        var inner = StubSession.Create();
        var recording = new RecordingDataSession(inner);

        var recordedNow = new List<string>();
        var passedThrough = new List<string>();
        foreach (var property in properties)
        {
            var same = ReferenceEquals(property.GetValue(recording), property.GetValue(inner));
            (same ? passedThrough : recordedNow).Add(property.Name);
        }

        await Assert.That(recordedNow.Order().ToArray()).IsEquivalentTo(Recorded.Order().ToArray());
        await Assert.That(passedThrough.Order().ToArray()).IsEquivalentTo(PassThroughOnPurpose.Order().ToArray());
    }

    /// <summary>An <see cref="IDataSession"/> whose every store is a distinct do-nothing proxy, so identity shows what got wrapped.</summary>
    public class StubSession : DispatchProxy
    {
        private readonly Dictionary<string, object> _stores = [];

        public static IDataSession Create() => DispatchProxy.Create<IDataSession, StubSession>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is { Name: var name } && name.StartsWith("get_", StringComparison.Ordinal))
            {
                if (!_stores.TryGetValue(name, out var store))
                {
                    store = StubStore.Create(targetMethod.ReturnType);
                    _stores[name] = store;
                }

                return store;
            }

            throw new NotSupportedException();
        }
    }

    public class StubStore : DispatchProxy
    {
        public static object Create(Type storeType) =>
            typeof(DispatchProxy).GetMethods().Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0).MakeGenericMethod(storeType, typeof(StubStore)).Invoke(null, null)!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }
}
