using System.Security.Claims;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// D5 (DECISION_LOG del 2026-11-19): el menú del Tenant TEC ofrece Subcontratas, con su rótulo comercial y sin jerga
/// interna (D2: ni «Outbound», ni «Inbound», ni «TEC», ni «TCE», ni «Tenant»). El Tenant TEC es el Administrador de su
/// propio Tenant, sin línea Inbound; con autorizaciones de lectura concedidas ve además «Mis Clientes», y sin ellas
/// (todavía no ha concedido ninguna) no. En los dos casos, Subcontratas está en el grupo de su negocio.
/// </summary>
public class CatalogoMenuLateralSubcontratasTenantTecTests
{
    private static readonly string[] Jerga = ["Outbound", "Inbound", "TEC", "TCE", "Tenant"];

    private static ContextoMenuLateral TenantTec(bool conAutorizacionesConcedidas) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Administrador)], "prueba")),
            null, ComunicacionesActivo: true, EsAdministradorPlataforma: false, PerfilVocabularioTenant.ClienteDirecto,
            VariosTenants: false, LineaInboundVigente: false, EsTenantTecInbound: conAutorizacionesConcedidas);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_Tenant_TEC_ve_Subcontratas_con_su_rotulo_comercial(bool conAutorizacionesConcedidas)
    {
        var contexto = TenantTec(conAutorizacionesConcedidas);
        var visibles = CatalogoMenuLateral.Visibles(contexto);

        var grupo = visibles.Single(g => g.Enlaces.Any(e => e.Id == "subcontratas"));
        var enlace = grupo.Enlaces.Single(e => e.Id == "subcontratas");

        enlace.RutaPara(contexto).Should().Be("subcontratas");
        enlace.RotuloPara(contexto).Should().Be("Subcontratas");
        grupo.Grupo.Titulo.Should().Be("Negocio");
        visibles.Any(g => g.Grupo.Id == "inbound-tec").Should().Be(conAutorizacionesConcedidas,
            "control positivo: el contexto es el del Tenant TEC, que con autorizaciones concedidas ve «Mis Clientes»");

        foreach (var texto in new[] { enlace.RotuloPara(contexto), grupo.Grupo.Titulo })
            Jerga.Where(j => texto.Contains(j, StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("D2: ninguna jerga interna llega al usuario final («{0}»)", texto);
        CatalogoMenuLateral.RotulosDeLinea(visibles).Should().BeEmpty("sin línea Inbound no hay dos líneas que rotular");
    }
}
