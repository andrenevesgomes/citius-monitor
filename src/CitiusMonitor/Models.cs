namespace CitiusMonitor;

/// <summary>A selectable option from the "Tribunal" (court) dropdown.</summary>
public sealed record CourtOption(string CourtId, string Name);

/// <summary>A party in a proceeding together with its role (e.g. "Réu:").</summary>
/// <param name="RoleLabel">The role exactly as shown on the page (e.g. "Réu:").</param>
/// <param name="RoleNormalized">Accent-free, lower-cased role used for matching (e.g. "reu").</param>
/// <param name="NameRaw">The party name exactly as shown on the page.</param>
public sealed record Party(string RoleLabel, string RoleNormalized, string NameRaw);

/// <summary>
/// A single row of the results grid (one proceeding). Field names are English;
/// their values are the raw Portuguese strings shown on the Citius page.
/// </summary>
public sealed class ProcessRecord
{
    public string CourtId { get; set; } = "";
    public string CourtName { get; set; } = "";

    /// <summary>The searched date range, for traceability in the report.</summary>
    public string SearchDate { get; set; } = "";

    public string? EntryDate { get; set; }
    public string? DistributionDate { get; set; }
    public string? EntryNumber { get; set; }
    public string? ProcessNumber { get; set; }
    public string? OrganizationalUnit { get; set; }
    public string? CaseType { get; set; }
    public string? Amount { get; set; }
    public string? Observations { get; set; }

    public List<Party> Parties { get; } = new();

    public bool Matched { get; set; }

    /// <summary>Which comparison mode produced the match ("strict" or "variation").</summary>
    public string MatchMethod { get; set; } = "";

    /// <summary>The matched defendant name (the "Réu"), verbatim.</summary>
    public string? DefendantRaw { get; set; }

    /// <summary>The first claimant name found (Autor/Exequente/Requerente), verbatim.</summary>
    public string? PlaintiffRaw { get; set; }

    /// <summary>Stable key used to drop duplicate proceedings across pages/courts.</summary>
    public string DedupKey =>
        $"{(ProcessNumber ?? "").Trim().ToUpperInvariant()}|{CourtId.Trim()}|{(DistributionDate ?? "").Trim()}";
}

/// <summary>Aggregated run outcome, surfaced in the console summary and the report.</summary>
public sealed class RunSummary
{
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public string DateFrom { get; set; } = "";
    public string DateTo { get; set; } = "";
    public string TargetDefendant { get; set; } = "";
    public string MatchMode { get; set; } = "";
    public int CourtsDiscovered { get; set; }
    public int CourtsSearched { get; set; }
    public int CourtsWithResults { get; set; }
    public int CourtsWithMatches { get; set; }
    public int CourtsFailed { get; set; }
    public int TotalMatches { get; set; }

    /// <summary>Overall status: "OK", "PARCIAL" (partial) or "FALHOU" (failed).</summary>
    public string Status { get; set; } = "OK";

    public List<string> Notes { get; } = new();
}

/// <summary>An isolated failure while processing a single court.</summary>
/// <param name="Stage">Where it failed (e.g. "pesquisa").</param>
/// <param name="ErrorSummary">A short, human-readable error description.</param>
public sealed record CourtFailure(
    string CourtId,
    string CourtName,
    string Stage,
    string ErrorSummary);
