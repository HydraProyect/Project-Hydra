using CaeManager.IntegrationTests.Tenants;

namespace CaeManager.Architecture.Tests;

public class InspeccionTextoPoliticaRlsTests
{
    [Theory]
    [InlineData("a OR b", true)]
    [InlineData("a or b", true)]
    [InlineData("x = 1 OR true", true)]
    [InlineData("(a)OR(b)", true)]
    [InlineData("TenantOrigenId", false)]
    [InlineData("MotivoRechazo", false)]
    [InlineData("app_ctx_tenant_id()", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void DetectaDisyuncion(string? expresion, bool esperado) =>
        Assert.Equal(esperado, InspeccionTextoPoliticaRls.TieneDisyuncion(expresion));

    [Theory]
    [InlineData("TenantId = app_ctx_tenant_id()", true)]
    [InlineData("app_ctx_tenant_id() = TenantId", true)]
    [InlineData("TenantId", false)]
    [InlineData("app_ctx_tenant_id()", false)]
    [InlineData("tenantid = app_ctx_tenant_id()", false)]
    [InlineData("TenantId = APP_CTX_TENANT_ID()", false)]
    [InlineData("TenantId = app_ctx_tenant_id() OR current_setting('x')", false)]
    [InlineData("TenantId = app_ctx_tenant_id() OR CURRENT_SETTING('x')", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void DetectaMencionDelAislamiento(string? expresion, bool esperado) =>
        Assert.Equal(esperado, InspeccionTextoPoliticaRls.MencionaElAislamiento(expresion));
}
