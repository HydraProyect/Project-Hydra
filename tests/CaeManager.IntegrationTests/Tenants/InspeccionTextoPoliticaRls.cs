namespace CaeManager.IntegrationTests.Tenants;

internal static class InspeccionTextoPoliticaRls
{
    internal static bool TieneDisyuncion(string? expresion) =>
        expresion is not null && System.Text.RegularExpressions.Regex.IsMatch(expresion, @"\bOR\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool MencionaElAislamiento(string? expresion) =>
        expresion is not null
        && expresion.Contains("TenantId", StringComparison.Ordinal)
        && expresion.Contains("app_ctx_tenant_id()", StringComparison.Ordinal)
        && !expresion.Contains("current_setting(", StringComparison.Ordinal);
}
