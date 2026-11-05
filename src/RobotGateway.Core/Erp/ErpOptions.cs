using System.ComponentModel.DataAnnotations;

namespace RobotGateway.Core;

/// <summary>
/// Interface with the customer's ERP (section "Erp"). The ERP drops text files with article codes in
/// <see cref="ImportFolder"/>; the gateway reads them and deletes each file once its codes are stored
/// (deletion = acknowledgment to the ERP). Production data goes back through GET /api/erp/production.
/// </summary>
public sealed class ErpOptions
{
    public const string SectionName = "Erp";

    /// <summary>False: the folder is never scanned automatically (the manual scan still works if a folder is set).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Folder written by the ERP: a local path, or a UNC share (\\server\share\folder) readable and writable by
    /// the account the gateway runs as. Relative paths are resolved against the content root.
    /// Empty = import disabled.
    /// </summary>
    public string ImportFolder { get; set; } = "data/erp-import";

    /// <summary>Files to read in <see cref="ImportFolder"/> (subfolders are ignored).</summary>
    public string FilePattern { get; set; } = "*.txt";

    /// <summary>Time between two automatic scans.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "1.00:00:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Automatic scan: a file modified more recently than this is left for the next scan, the ERP may still be
    /// writing it. A manual scan (page, API) does not wait; a file still open for writing is skipped in both cases.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan MinFileAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True: in the code editor (and POST/PUT /api/jobs) a job can only use an article code received from the ERP.
    /// Service codes (e.g. HOME) are exempt. False: any code can be typed (tests on the robot without the ERP).
    /// </summary>
    public bool OnlyImportedCodes { get; set; } = true;

    /// <summary>Delete each file after its codes are stored. Keep true: the ERP reads the deletion as "codes loaded".</summary>
    public bool DeleteAfterImport { get; set; } = true;
}
