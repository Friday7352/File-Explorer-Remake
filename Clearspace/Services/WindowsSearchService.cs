using System.Data.OleDb;
using System.Diagnostics;
using System.Text;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>
/// Queries the Windows Search index.
///
/// The index is already built and already maintained by Windows, and crucially it
/// has run IFilters over file contents, so it can answer questions a directory
/// walk never could: which documents contain a word. Results come back in
/// milliseconds because nothing is being enumerated.
///
/// It is not a replacement for the walk. The index only covers locations that have
/// been added to indexing options, which by default is roughly your user profile
/// and not whole drives. So this runs alongside the crawl rather than instead of
/// it: the index answers instantly, the crawl guarantees completeness.
/// </summary>
public static class WindowsSearchService
{
    // The Windows Search OLE DB provider. Present on every supported Windows build,
    // though the service itself can be disabled.
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";

    private static bool? _available;
    private static DateTime _lastUnavailableCheck = DateTime.MinValue;

    /// <summary>
    /// How long a failed check is trusted before trying again. The Windows Search
    /// service can still be starting up when Clearspace launches, or can be
    /// restarted while Clearspace stays open; caching "unavailable" forever meant
    /// the "Use the Windows index" toggle stayed stuck off for the rest of the
    /// session with no way back short of relaunching. A short cooldown costs one
    /// cheap connection attempt every so often and fixes itself instead.
    /// </summary>
    private static readonly TimeSpan UnavailableRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Result rows, kept minimal: the path is all the caller needs.</summary>
    public readonly record struct Hit(string Path, bool MatchedContents);

    /// <summary>
    /// Whether the index can be queried at all. A success is cached for the rest
    /// of the process, since that can only become stale in ways a relaunch already
    /// handles. A failure is only cached briefly, so a service that was still
    /// starting up (or was restarted mid-session) becomes usable again on its own.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available == true)
                return true;

            if (_available == false && DateTime.UtcNow - _lastUnavailableCheck < UnavailableRetryInterval)
                return false;

            try
            {
                using var connection = new OleDbConnection(ConnectionString);
                connection.Open();
                _available = true;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: Windows Search unavailable. {exception.Message}");
                _available = false;
                _lastUnavailableCheck = DateTime.UtcNow;
            }

            return _available.Value;
        }
    }

    public static string? LastError { get; private set; }

    /// <summary>
    /// Runs a query against the index, limited to the given roots.
    ///
    /// Name and content matches are ORed per term, so typing a word finds files
    /// called that and documents containing it. Structural filters (extension,
    /// tags, folder type) are left to the caller, since the index knows nothing
    /// about Clearspace's own tags.
    /// </summary>
    public static IReadOnlyList<Hit> Search(
        SearchQuery query,
        IReadOnlyList<string> roots,
        int maxResults,
        CancellationToken token)
    {
        if (!IsAvailable || query.Terms.Count == 0 || roots.Count == 0)
            return [];

        var sql = BuildSql(query, roots, maxResults);

        if (sql is null)
            return [];

        var hits = new List<Hit>();

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 20;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();

                if (reader.IsDBNull(0))
                    continue;

                hits.Add(new Hit(reader.GetString(0), MatchedContents: false));

                if (hits.Count >= maxResults)
                    break;
            }

            LastError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A malformed predicate or a stopped service should degrade to the
            // crawl, never break searching.
            LastError = exception.Message;
            Trace.WriteLine($"Clearspace: index query failed. {exception}");
            return [];
        }

        return hits;
    }

    private static string? BuildSql(SearchQuery query, IReadOnlyList<string> roots, int maxResults)
    {
        var scopes = roots
            .Where(root => !root.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(root => $"SCOPE='file:{Escape(root)}'")
            .ToList();

        if (scopes.Count == 0)
            return null;

        var sql = new StringBuilder();
        sql.Append("SELECT TOP ").Append(maxResults).Append(' ');
        sql.Append("System.ItemPathDisplay FROM SystemIndex WHERE (");
        sql.Append(string.Join(" OR ", scopes));
        sql.Append(')');

        foreach (var term in query.Terms)
        {
            var like = Escape(term);
            var contains = Escape(term.Replace("\"", string.Empty));

            if (contains.Length == 0)
                continue;

            // FREETEXT over Contents covers document bodies; the LIKE keeps plain
            // filename matches working for files with no content filter.
            sql.Append(" AND (System.FileName LIKE '%").Append(like).Append("%'");
            sql.Append(" OR CONTAINS(System.Search.Contents, '\"").Append(contains).Append("*\"')");
            sql.Append(')');
        }

        foreach (var extension in query.Extensions)
            sql.Append(" AND System.FileExtension = '").Append(Escape(extension)).Append('\'');

        return sql.ToString();
    }

    /// <summary>Doubles single quotes; the provider takes no parameters.</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    /// <summary>Opens the Windows indexing options control panel.</summary>
    public static bool OpenIndexingOptions()
    {
        // The canonical name works on Windows 10 and 11; the rundll32 form is the
        // older route and is kept as a fallback.
        var attempts = new (string File, string Arguments)[]
        {
            ("control.exe", "/name Microsoft.IndexingOptions"),
            ("rundll32.exe", "shell32.dll,Control_RunDLL srchadmin.dll")
        };

        foreach (var attempt in attempts)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = attempt.File,
                    Arguments = attempt.Arguments,
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception)
            {
                // Try the next form.
            }
        }

        return false;
    }
}
