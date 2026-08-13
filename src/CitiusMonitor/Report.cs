using ClosedXML.Excel;
using System.Globalization;

namespace CitiusMonitor;

/// <summary>
/// Builds the Excel (.xlsx) report with three worksheets. Sheet names and column
/// headers are in Portuguese on purpose — the report is read by Portuguese court
/// staff ("Resumo" = summary, "Resultados" = results, "Falhas" = failures).
/// </summary>
public static class Report
{
    public static void BuildWorkbook(
        string path,
        RunSummary summary,
        IReadOnlyList<ProcessRecord> findings,
        IReadOnlyList<CourtFailure> failures)
    {
        using var wb = new XLWorkbook();

        // --- "Resumo" (summary) sheet ---
        var resumo = wb.Worksheets.Add("Resumo");
        var summaryRows = new (string, string)[]
        {
            ("Início", summary.StartedAt),
            ("Fim", summary.FinishedAt),
            ("Data inicial", summary.DateFrom),
            ("Data final", summary.DateTo),
            ("Réu procurado", summary.TargetDefendant),
            ("Modo de correspondência", summary.MatchMode),
            ("Tribunais descobertos", summary.CourtsDiscovered.ToString(CultureInfo.InvariantCulture)),
            ("Tribunais pesquisados", summary.CourtsSearched.ToString(CultureInfo.InvariantCulture)),
            ("Tribunais com resultados", summary.CourtsWithResults.ToString(CultureInfo.InvariantCulture)),
            ("Tribunais com correspondências", summary.CourtsWithMatches.ToString(CultureInfo.InvariantCulture)),
            ("Tribunais com falha", summary.CourtsFailed.ToString(CultureInfo.InvariantCulture)),
            ("Total de correspondências", summary.TotalMatches.ToString(CultureInfo.InvariantCulture)),
            ("Estado", summary.Status),
            ("Notas", string.Join(" | ", summary.Notes)),
        };
        for (var i = 0; i < summaryRows.Length; i++)
        {
            resumo.Cell(i + 1, 1).Value = summaryRows[i].Item1;
            resumo.Cell(i + 1, 2).Value = summaryRows[i].Item2;
            resumo.Cell(i + 1, 1).Style.Font.Bold = true;
        }
        resumo.Columns().AdjustToContents();

        // --- "Resultados" (results/findings) sheet ---
        var res = wb.Worksheets.Add("Resultados");
        string[] cols =
        {
            "Tribunal", "ID Tribunal", "Processo", "Espécie", "Unidade Orgânica",
            "Data Entrada", "Nº Entrada", "Data Distribuição", "Réu (correspondido)",
            "Autor", "Valor", "Observações", "Método", "Intervalo",
        };
        for (var c = 0; c < cols.Length; c++)
        {
            res.Cell(1, c + 1).Value = cols[c];
            res.Cell(1, c + 1).Style.Font.Bold = true;
        }
        var r = 2;
        foreach (var f in findings)
        {
            res.Cell(r, 1).Value = f.CourtName;
            res.Cell(r, 2).Value = f.CourtId;
            res.Cell(r, 3).Value = f.ProcessNumber ?? "";
            res.Cell(r, 4).Value = f.CaseType ?? "";
            res.Cell(r, 5).Value = f.OrganizationalUnit ?? "";
            res.Cell(r, 6).Value = f.EntryDate ?? "";
            res.Cell(r, 7).Value = f.EntryNumber ?? "";
            res.Cell(r, 8).Value = f.DistributionDate ?? "";
            res.Cell(r, 9).Value = f.DefendantRaw ?? "";
            res.Cell(r, 10).Value = f.PlaintiffRaw ?? "";
            res.Cell(r, 11).Value = f.Amount ?? "";
            res.Cell(r, 12).Value = f.Observations ?? "";
            res.Cell(r, 13).Value = f.MatchMethod;
            res.Cell(r, 14).Value = f.SearchDate;
            r++;
        }
        res.SheetView.FreezeRows(1);
        res.Columns().AdjustToContents();

        // --- "Falhas" (per-court failures) sheet ---
        var fal = wb.Worksheets.Add("Falhas");
        string[] fcols = { "ID Tribunal", "Tribunal", "Fase", "Erro" };
        for (var c = 0; c < fcols.Length; c++)
        {
            fal.Cell(1, c + 1).Value = fcols[c];
            fal.Cell(1, c + 1).Style.Font.Bold = true;
        }
        var fr = 2;
        foreach (var f in failures)
        {
            fal.Cell(fr, 1).Value = f.CourtId;
            fal.Cell(fr, 2).Value = f.CourtName;
            fal.Cell(fr, 3).Value = f.Stage;
            fal.Cell(fr, 4).Value = f.ErrorSummary;
            fr++;
        }
        fal.SheetView.FreezeRows(1);
        fal.Columns().AdjustToContents();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        wb.SaveAs(path);
    }
}
