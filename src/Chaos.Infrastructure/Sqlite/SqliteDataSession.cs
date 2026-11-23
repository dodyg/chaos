using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>SQLite implementations of the typed stores bound to one session connection (PLAN §4).</summary>
public sealed class SqliteDataSession(SqliteConnection connection, Chaos.Infrastructure.Activity.ActivityNotes notes) : IDataSession
{
    public SqliteDataSession(SqliteConnection connection) : this(connection, new Chaos.Infrastructure.Activity.ActivityNotes())
    {
    }

    // Stores are created on first use: most reads touch two or three of them.
    private IProfileStore? _profiles;
    private ISpaceStore? _spaces;
    private IPeopleStore? _people;
    private IContainerStore? _containers;
    private ISectionStore? _sections;
    private ITaskStore? _tasks;
    private ISubtaskStore? _subtasks;
    private ITemplateStore? _templates;
    private ITagStore? _tags;
    private ITaskQueryStore? _queries;
    private ITaskHistoryStore? _history;
    private IDiaryStore? _diary;
    private IUndoStore? _undo;
    private IChecklistConversionStore? _checklistConversions;
    private IPadStore? _pad;
    private IPageStore? _pages;
    private IRecurrenceStore? _recurrence;
    private IReminderRuleStore? _reminderRules;
    private IWaitingStore? _waiting;
    private IDelegationStore? _delegations;
    private ISpaceUpdateStore? _spaceUpdates;
    private INotificationJobStore? _notifications;
    private IDayPlanStore? _dayPlans;
    private IWeekPlanStore? _weeks;
    private ISummaryStore? _summaries;
    private IOperationReceiptStore? _receipts;
    private IBackupCatalogStore? _backups;
    private IImportStore? _imports;
    private IGitHubStore? _gitHub;
    private IBoardStore? _boards;
    private ISavedViewStore? _savedViews;
    private ICalendarStore? _calendars;
    private IAttachmentStore? _attachments;

    /// <summary>The open connection (inside the caller's transaction for writes), for storage-level work such as purge.</summary>
    public SqliteConnection Connection => connection;

    public IProfileStore Profiles => _profiles ??= new SqliteProfileStore(connection);
    public ISpaceStore Spaces => _spaces ??= new SqliteSpaceStore(connection);
    public IPeopleStore People => _people ??= new SqlitePeopleStore(connection);
    public IContainerStore Containers => _containers ??= new SqliteContainerStore(connection);
    public ISectionStore Sections => _sections ??= new SqliteSectionStore(connection);
    public ITaskStore Tasks => _tasks ??= new SqliteTaskStore(connection);
    public ISubtaskStore Subtasks => _subtasks ??= new SqliteSubtaskStore(connection);
    public ITemplateStore Templates => _templates ??= new SqliteTemplateStore(connection);
    public ITagStore Tags => _tags ??= new SqliteTagStore(connection);
    public ITaskQueryStore Queries => _queries ??= new Chaos.Infrastructure.Search.SqliteTaskQueryStore(connection);
    public ITaskHistoryStore History => _history ??= new SqliteTaskHistoryStore(connection);
    public IDiaryStore Diary => _diary ??= new SqliteDiaryStore(connection);
    public IUndoStore Undo => _undo ??= new SqliteUndoStore(connection);
    public IChecklistConversionStore ChecklistConversions => _checklistConversions ??= new SqliteChecklistConversionStore(connection);
    public IPadStore Pad => _pad ??= new SqlitePadStore(connection);
    public IPageStore Pages => _pages ??= new SqlitePageStore(connection);
    public IRecurrenceStore Recurrence => _recurrence ??= new SqliteRecurrenceStore(connection);
    public IReminderRuleStore ReminderRules => _reminderRules ??= new SqliteReminderRuleStore(connection);
    public IWaitingStore Waiting => _waiting ??= new SqliteWaitingStore(connection);
    public IDelegationStore Delegations => _delegations ??= new SqliteDelegationStore(connection);
    public ISpaceUpdateStore SpaceUpdates => _spaceUpdates ??= new SqliteSpaceUpdateStore(connection);
    public INotificationJobStore Notifications => _notifications ??= new SqliteNotificationJobStore(connection, notes);
    public IDayPlanStore DayPlans => _dayPlans ??= new SqliteDayPlanStore(connection);
    public IWeekPlanStore Weeks => _weeks ??= new SqliteWeekPlanStore(connection);
    public ISummaryStore Summaries => _summaries ??= new SqliteSummaryStore(connection);
    public IOperationReceiptStore Receipts => _receipts ??= new SqliteOperationReceiptStore(connection);
    public IBackupCatalogStore Backups => _backups ??= new SqliteBackupCatalogStore(connection);
    public IImportStore Imports => _imports ??= new SqliteImportStore(connection);
    public IGitHubStore GitHub => _gitHub ??= new SqliteGitHubStore(connection);
    public IBoardStore Boards => _boards ??= new SqliteBoardStore(connection);
    public ISavedViewStore SavedViews => _savedViews ??= new SqliteSavedViewStore(connection, notes);
    public ICalendarStore Calendars => _calendars ??= new SqliteCalendarStore(connection, notes);
    public IAttachmentStore Attachments => _attachments ??= new SqliteAttachmentStore(connection);

    /// <summary>Device-only writers note their changes here; the store reads the notes inside the same transaction.</summary>
    public Chaos.Application.Activity.IActivityRecorder Activity => notes;

    internal Chaos.Infrastructure.Activity.ActivityNotes Notes => notes;
}
