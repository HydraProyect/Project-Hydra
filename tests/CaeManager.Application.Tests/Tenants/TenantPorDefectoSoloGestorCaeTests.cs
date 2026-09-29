using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Decisión 7 quater del propietario del producto (2026-09-29, opción A): el Tenant
/// beneficiario por defecto de la decisión 5 se aplica solo cuando la Asignación de
/// Cartera es de rol Gestor CAE.
/// </summary>
public class TenantPorDefectoSoloGestorCaeTests
{
    private static readonly ClienteAutorizadoDto Origen =
        new(Guid.NewGuid(), "Operador CAE externo", EsOrigen: true);

    private static ClienteAutorizadoDto Externo(bool gestorCae) =>
        new(Guid.NewGuid(), "Tenant beneficiario", EsOrigen: false,
            EsGestionadoPorOperacion: true, EsCarteraGestorCae: gestorCae);

    [Fact]
    public void Un_Tenant_externo_por_cartera_de_Gestor_CAE_es_el_defecto()
    {
        var externo = Externo(gestorCae: true);

        ClientesAutorizados.TenantPorDefecto([Origen, externo]).Should().Be(externo);
    }

    [Fact]
    public void Un_Tenant_externo_por_cartera_de_otro_rol_no_es_el_defecto()
    {
        var consulta = Externo(gestorCae: false);

        ClientesAutorizados.TenantPorDefecto([Origen, consulta]).Should().BeNull(
            "el Operador delegado entra en su Tenant de origen y ve el selector");
    }

    [Fact]
    public void Con_cartera_de_otro_rol_el_selector_se_ve_tambien_dentro_del_Tenant_externo()
    {
        var consulta = Externo(gestorCae: false);

        ClientesAutorizados.SelectorVisible([Origen, consulta], Origen).Should().BeTrue("entra en el origen y elige");
        ClientesAutorizados.SelectorVisible([Origen, consulta], consulta).Should().BeTrue(
            "dentro del Tenant externo es su único control para volver al origen");
    }

    [Fact]
    public void Con_cartera_de_Gestor_CAE_y_un_unico_Tenant_el_selector_sigue_oculto()
    {
        var gestor = Externo(gestorCae: true);

        ClientesAutorizados.SelectorVisible([Origen, gestor], gestor).Should().BeFalse();
    }

    [Fact]
    public void Con_dos_Tenants_externos_no_hay_defecto_aunque_ambos_sean_de_Gestor_CAE()
    {
        ClientesAutorizados.TenantPorDefecto([Origen, Externo(true), Externo(true)]).Should().BeNull();
    }
}
