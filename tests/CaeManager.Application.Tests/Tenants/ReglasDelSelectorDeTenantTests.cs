using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Reglas puras del selector de Tenant beneficiario (contrato, decisiones 1, 5 y
/// 7 bis): quién ve el selector, qué cuenta como cartera, cuál es el Tenant activo
/// y cuándo hay que pedir que se elija uno. La lista ya es el conjunto autorizado
/// (mismo predicado que el POST y la revalidación); aquí solo se decide qué se
/// enseña, nunca qué se autoriza.
/// </summary>
public class ReglasDelSelectorDeTenantTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid Delegado = Guid.NewGuid();

    /// <summary>Visibilidad con el activo que resultaría de la selección (null = sin selección: el origen).</summary>
    private static bool Visible(ClienteAutorizadoDto[] lista, Guid? seleccion = null) =>
        ClientesAutorizados.SelectorVisible(lista, ClientesAutorizados.Activo(lista, seleccion));

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Origen", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto PorCartera(Guid id, string nombre) =>
        new(id, nombre, EsOrigen: false, EsGestionadoPorOperacion: true);

    private static ClienteAutorizadoDto PorDelegacion() =>
        new(Delegado, "Delegado", EsOrigen: false, EsGestionadoPorOperacion: false);

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_el_selector()
    {
        Visible([Propio()]).Should().BeFalse();
        Visible([]).Should().BeFalse();
    }

    [Fact]
    public void Con_un_solo_Tenant_de_cartera_y_el_origen_sin_gestionar_el_selector_sigue_oculto()
    {
        // Decisión 5: ese Tenant es el activo por defecto y el selector no aparece.
        Visible([Propio(), PorCartera(A, "A")], seleccion: A).Should().BeFalse();
    }

    [Fact]
    public void Con_dos_Tenants_de_cartera_el_selector_es_visible()
    {
        Visible([Propio(), PorCartera(A, "A"), PorCartera(B, "B")]).Should().BeTrue();
    }

    [Fact]
    public void El_origen_cuenta_solo_si_el_Gestor_tiene_cartera_sobre_el()
    {
        Visible([Propio(gestionado: true), PorCartera(A, "A")]).Should().BeTrue(
            "origen gestionado + un Tenant externo son dos Tenants de cartera");
        Visible([Propio(gestionado: false), PorCartera(A, "A")], seleccion: A).Should().BeFalse();
    }

    [Fact]
    public void Quien_solo_alcanza_un_Tenant_por_la_via_heredada_conserva_el_selector()
    {
        // Es su único control para pasar a ese Tenant y volver a su origen; lo veía
        // antes del cambio y no lo concede: la lista ya es el conjunto autorizado.
        Visible([Propio(), PorDelegacion()]).Should().BeTrue();
    }

    [Fact]
    public void El_total_de_la_cartera_son_los_Tenants_externos_y_no_el_origen()
    {
        ClientesAutorizados.TotalCartera([Propio(gestionado: true), PorCartera(A, "A"), PorCartera(B, "B")])
            .Should().Be(2, "el origen va aparte, fijo bajo la lista, como «Tu organización»");
    }

    [Fact]
    public void El_activo_es_el_seleccionado_si_sigue_autorizado_y_si_no_el_origen()
    {
        var lista = new[] { Propio(), PorCartera(A, "A"), PorCartera(B, "B") };

        ClientesAutorizados.Activo(lista, B)!.TenantId.Should().Be(B);
        ClientesAutorizados.Activo(lista, null)!.TenantId.Should().Be(Origen);
        ClientesAutorizados.Activo(lista, Guid.NewGuid())!.TenantId.Should().Be(Origen,
            "una selección cuya Asignación caducó ya no está en la lista: se vuelve al origen");
    }

    [Fact]
    public void El_Tenant_por_defecto_es_el_unico_externo_alcanzado_por_cartera_con_el_origen_sin_gestionar()
    {
        ClientesAutorizados.TenantPorDefecto([Propio(), PorCartera(A, "A")])!.TenantId.Should().Be(A);
        ClientesAutorizados.TenantPorDefecto([Propio(gestionado: true), PorCartera(A, "A")]).Should().BeNull("su origen está en su cartera");
        ClientesAutorizados.TenantPorDefecto([Propio(), PorCartera(A, "A"), PorCartera(B, "B")]).Should().BeNull("con dos, elegir uno sería decidir por el usuario");
        ClientesAutorizados.TenantPorDefecto([Propio(), PorCartera(A, "A"), PorDelegacion()]).Should().BeNull("alcanza además otro Tenant por la vía heredada");
    }

    [Fact]
    public void Decision_7_bis_sin_Asignacion_de_Cartera_un_unico_Tenant_externo_no_es_el_por_defecto()
    {
        // Administrador del Operador CAE externo: sin cartera alcanza a lo sumo un Tenant por la
        // vía heredada (soporte / Operador Delegado). Sigue entrando en su Tenant de origen.
        ClientesAutorizados.TenantPorDefecto([Propio(), PorDelegacion()]).Should().BeNull();
    }

    [Fact]
    public void Con_un_unico_Tenant_de_cartera_el_selector_reaparece_mientras_el_contexto_efectivo_es_el_origen()
    {
        // Volvió al origen a propósito (la preferencia impide el Tenant por defecto 8 h) y su cartera
        // bajó a un solo Tenant: sin control no tendría cómo elegirlo.
        Visible([Propio(), PorCartera(A, "A")], seleccion: null).Should().BeTrue();
        Visible([Propio(), PorCartera(A, "A")], seleccion: A).Should().BeFalse("con el Tenant único activo se oculta (decisión 5)");
        Visible([Propio()], seleccion: null).Should().BeFalse("sin Tenants externos no hay nada que elegir");
    }

    [Fact]
    public void Se_pide_elegir_empresa_solo_a_quien_gestiona_por_Operacion_y_tiene_activo_un_origen_sin_gestionar()
    {
        var conCartera = new[] { Propio(), PorCartera(A, "A"), PorCartera(B, "B") };
        ClientesAutorizados.SinEmpresaSeleccionada(conCartera, ClientesAutorizados.Activo(conCartera, null))
            .Should().BeTrue("estado 4a del mockup");
        ClientesAutorizados.SinEmpresaSeleccionada(conCartera, ClientesAutorizados.Activo(conCartera, A))
            .Should().BeFalse("ya eligió una empresa");

        var origenGestionado = new[] { Propio(gestionado: true), PorCartera(A, "A") };
        ClientesAutorizados.SinEmpresaSeleccionada(origenGestionado, ClientesAutorizados.Activo(origenGestionado, null))
            .Should().BeFalse("su origen está en su cartera");

        var soloHeredada = new[] { Propio(), PorDelegacion() };
        ClientesAutorizados.SinEmpresaSeleccionada(soloHeredada, ClientesAutorizados.Activo(soloHeredada, null))
            .Should().BeFalse("sin cartera por Operación (Administrador del Operador CAE): su origen es su sitio, decisión 7 bis");
    }
}
