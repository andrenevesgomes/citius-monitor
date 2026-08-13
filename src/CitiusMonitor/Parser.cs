using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace CitiusMonitor;

/// <summary>
/// Pure HTML parsing and matching — no network access whatsoever, so it can be
/// unit-tested against saved fixtures. The selectors below were captured from the
/// live Citius page:
///   - dropdown ...  id="ctl00_ContentPlaceHolder1_ddlTribunais" (placeholder value="0")
///   - count .....  id="ctl00_ContentPlaceHolder1_lblRecordCount"  "60 Processos encontrados"
///   - grid ......  id="ctl00_ContentPlaceHolder1_grdView"
///   - per row ...  spans ..._grdView_ctlNN_lblNProcesso / _lblDataEntrada / _lblDataDistrib /
///                  _lblUnOrganica / _lblEspecie / _lblValor / _lblObservacoes
///   - parties ...  ..._DataList_ctlKK_lblDesignacao (role "Réu:") + _lblNomeInterv (name)
///   - paging ....  id="ctl00_ContentPlaceHolder1_Pager1_lnkNext"
/// </summary>
public static class Parser
{
    public const string DdlTribunaisId = "ctl00_ContentPlaceHolder1_ddlTribunais";
    public const string LblRecordCountId = "ctl00_ContentPlaceHolder1_lblRecordCount";
    public const string GridId = "ctl00_ContentPlaceHolder1_grdView";
    public const string PagerNextLinkId = "ctl00_ContentPlaceHolder1_Pager1_lnkNext";
    public const string PagerNextEventTarget = "ctl00$ContentPlaceHolder1$Pager1$lnkNext";
    public const string DefendantLabel = "reu"; // Normalised form of "Réu".

    public static bool IsErrorPage(string html) =>
        html.Contains("<h1>Erro</h1>", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Ocorreu um erro inesperado", StringComparison.OrdinalIgnoreCase);

    // ---- Text normalisation ----------------------------------------------
    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PunctRe = new(@"[.,]", RegexOptions.Compiled);

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Normalize(NormalizationForm.FormKC)
            .Replace('\u00a0', ' ');
        return WsRe.Replace(text, " ").Trim();
    }

    private static string Deaccent(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Strict key: normalised + lower-cased (accents preserved).</summary>
    public static string CanonicalStrict(string? value) =>
        NormalizeText(value).ToLowerInvariant();

    /// <summary>Lenient key: accents stripped, '.'/',' removed, whitespace collapsed, lower-cased.</summary>
    public static string CanonicalVariation(string? value)
    {
        var text = Deaccent(NormalizeText(value));
        text = PunctRe.Replace(text, "");
        return WsRe.Replace(text, " ").Trim().ToLowerInvariant();
    }

    public static string NormalizeLabel(string? label) =>
        Deaccent(NormalizeText(label)).ToLowerInvariant().TrimEnd(':').Trim();

    // ---- Extract span inner text by id -----------------------------------
    private static string? SpanTextById(string html, string id)
    {
        var m = Regex.Match(
            html,
            $"id=\"{Regex.Escape(id)}\"[^>]*>(.*?)</span>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var inner = Regex.Replace(m.Groups[1].Value, "<.*?>", " ", RegexOptions.Singleline);
        var text = NormalizeText(HttpUtility.HtmlDecode(inner));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // ---- ASP.NET hidden fields -------------------------------------------
    public static Dictionary<string, string> ParseHiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (var name in new[]
                 {
                     "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__VIEWSTATEENCRYPTED", "__EVENTVALIDATION"
                 })
        {
            var m = Regex.Match(
                html,
                $"name=\"{Regex.Escape(name)}\"[^>]*?value=\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            if (m.Success) fields[name] = HttpUtility.HtmlDecode(m.Groups[1].Value);
        }
        if (!fields.ContainsKey("__VIEWSTATE"))
            throw new InvalidOperationException("__VIEWSTATE not found — the page structure changed.");
        return fields;
    }

    // ---- Courts dropdown -------------------------------------------------
    public static List<CourtOption> ParseCourtOptions(string html)
    {
        var selectMatch = Regex.Match(
            html,
            $"<select[^>]*id=\"{Regex.Escape(DdlTribunaisId)}\".*?</select>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!selectMatch.Success)
            throw new InvalidOperationException("Courts dropdown not found — the page structure changed.");

        var courts = new List<CourtOption>();
        foreach (Match opt in Regex.Matches(
                     selectMatch.Value,
                     "<option[^>]*value=\"([^\"]*)\"[^>]*>(.*?)</option>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var value = opt.Groups[1].Value.Trim();
            var name = NormalizeText(HttpUtility.HtmlDecode(opt.Groups[2].Value));
            if (value is "0" or "" ) continue;
            if (name.StartsWith("-Seleccione", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Indique", StringComparison.OrdinalIgnoreCase)) continue;
            courts.Add(new CourtOption(value, name));
        }
        if (courts.Count == 0)
            throw new InvalidOperationException("No courts parsed — the dropdown structure changed.");
        return courts;
    }

    // ---- Record count / paging -------------------------------------------
    public static int? ParseRecordCount(string html)
    {
        var text = SpanTextById(html, LblRecordCountId);
        if (text is null) return null;
        var m = Regex.Match(text, @"\d+");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    public static bool HasNextPage(string html) =>
        html.Contains($"id=\"{PagerNextLinkId}\"", StringComparison.OrdinalIgnoreCase);

    // ---- Results grid ----------------------------------------------------
    public static List<ProcessRecord> ParseResults(string html)
    {
        if (IsErrorPage(html))
            throw new InvalidOperationException("Citius returned a generic error page.");

        if (!html.Contains($"id=\"{GridId}\"", StringComparison.OrdinalIgnoreCase))
        {
            var count = ParseRecordCount(html);
            if (count is null or 0) return new List<ProcessRecord>();
            throw new InvalidOperationException("Results grid missing even though records were reported.");
        }

        // Distinct row indexes, detected by the presence of a process number.
        var rowPrefixes = new List<string>();
        foreach (Match m in Regex.Matches(
                     html,
                     Regex.Escape(GridId) + @"_(ctl\d+)_lblNProcesso",
                     RegexOptions.IgnoreCase))
        {
            var prefix = m.Groups[1].Value;
            if (!rowPrefixes.Contains(prefix)) rowPrefixes.Add(prefix);
        }

        var records = new List<ProcessRecord>();
        foreach (var row in rowPrefixes)
        {
            var baseId = $"{GridId}_{row}";
            var rec = new ProcessRecord
            {
                ProcessNumber = SpanTextById(html, $"{baseId}_lblNProcesso"),
                EntryDate = SpanTextById(html, $"{baseId}_lblDataEntrada"),
                DistributionDate = SpanTextById(html, $"{baseId}_lblDataDistrib"),
                OrganizationalUnit = SpanTextById(html, $"{baseId}_lblUnOrganica"),
                CaseType = SpanTextById(html, $"{baseId}_lblEspecie"),
                Amount = SpanTextById(html, $"{baseId}_lblValor"),
                Observations = SpanTextById(html, $"{baseId}_lblObservacoes"),
            };

            // Entry number: "(NNNN)" right after the entry date.
            var entradaSeg = Regex.Match(
                html,
                $"{Regex.Escape(baseId)}_lblDataEntrada.*?\\((\\d{{4,}})\\)",
                RegexOptions.Singleline);
            if (entradaSeg.Success) rec.EntryNumber = entradaSeg.Groups[1].Value;

            // Parties: designation/name pairs inside this row's DataList.
            var listId = $"{baseId}_DataList";
            var designacoes = Regex.Matches(
                html,
                $"{Regex.Escape(listId)}_(ctl\\d+)_lblDesignacao\"[^>]*>(.*?)</span>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match d in designacoes)
            {
                var partIdx = d.Groups[1].Value;
                var roleRaw = NormalizeText(HttpUtility.HtmlDecode(
                    Regex.Replace(d.Groups[2].Value, "<.*?>", " ")));
                var nameRaw = SpanTextById(html, $"{listId}_{partIdx}_lblNomeInterv") ?? "";
                rec.Parties.Add(new Party(roleRaw, NormalizeLabel(roleRaw), nameRaw));
            }

            if (rec.ProcessNumber is null && rec.Parties.Count == 0) continue;
            records.Add(rec);
        }
        return records;
    }

    // ---- Defendant matching ----------------------------------------------
    public static bool MatchDefendant(ProcessRecord record, string target, string mode = "variation")
    {
        var autor = record.Parties.FirstOrDefault(p =>
            p.RoleNormalized is "autor" or "exequente" or "requerente");
        if (autor is not null) record.PlaintiffRaw = autor.NameRaw;

        var reuParties = record.Parties.Where(p => p.RoleNormalized == DefendantLabel).ToList();
        if (reuParties.Count == 0) return false;

        Func<string?, string> compare = mode == "strict" ? CanonicalStrict : CanonicalVariation;
        var targetKey = compare(target);

        foreach (var p in reuParties)
        {
            if (compare(p.NameRaw) == targetKey)
            {
                record.Matched = true;
                record.MatchMethod = mode;
                record.DefendantRaw = p.NameRaw;
                return true;
            }
        }
        return false;
    }
}
