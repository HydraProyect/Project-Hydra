using System;
using System.Linq;
using System.Threading.Tasks;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;
using ContextoPrueba = CaeManager.Web.Tests.ClienteActivoEndpointsAutorizacionTests.ContextoPrueba;

namespace CaeManager.Web.Tests;

/// <summary>
/// Decisión D-4 (2026-11-16): con <see cref="ObtenerClientesAutorizadosQuery.PorPapel"/> el Gestor CAE de un Operador
/// CAE externo recibe una entrada por Asignación de Operación vigente (Tenant beneficiario y servicio), y todos los
/// demás lectores, que agrupan por Tenant, siguen recibiendo una entrada por Tenant y sin servicio. Es la capa de
/// Application (el handler real) sobre SQLite con el mismo contexto de prueba que el POST <c>/cuenta/cliente-activo</c>;
/// no sustituye los tests PostgreSQL/RLS de CI.
/// </summary>
public class ObtenerClientesAutorizadosPorPapelTests
{
    private static readonly Guid Usuario = Guid.NewGuid();

    private sealed class Usuaria(Guid origen, string rol, PapelActivo papel = PapelActivo.SinPapel) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
    }

    private sealed class TenantActivo(Guid id) : ITenantActual
    {
        public Guid? TenantId { get; } = id;
    }

    private sealed record Escenario(ContextoPrueba Db, Tenant Origen, Tenant Norte, Tenant Sur);

    private static async Task<Escenario> CrearAsync()
    {
        var db = await ClienteActivoEndpointsAutorizacionTests.CrearContexto();
        var origen = new Tenant("Operador CAE externo");
        var norte = new Tenant("Empresa Norte");
        var sur = new Tenant("Empresa Sur");
        db.AddRange(origen, norte, sur);
        await db.SaveChangesAsync();
        return new Escenario(db, origen, norte, sur);
    }

    /// <summary>Asignación de Operación Outbound amparada por su DelegacionTenant y, si se pide, la cartera del usuario.</summary>
    private static AsignacionOperacion Outbound(Escenario e, Tenant tenant, bool conCartera = true)
    {
        var ahora = DateTime.UtcNow;
        var delegacion = new DelegacionTenant(e.Origen.Id, tenant.Id);
        var operacion = AsignacionOperacion.Externa(tenant.Id, e.Origen.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        operacion.FijarOrigen(delegacion);
        e.Db.AddRange(delegacion, operacion);
        if (conCartera)
            e.Db.Add(AsignacionCartera.Externa(operacion, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        return operacion;
    }

    /// <summary>Asignación de Operación Inbound amparada por la línea Inbound del Tenant y con la cartera del usuario.</summary>
    private static AsignacionOperacion Inbound(Escenario e, Tenant tenant, bool cerrada = false, bool conLinea = true)
    {
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(tenant.Id, e.Origen.Id, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        e.Db.Add(operacion);
        e.Db.Add(AsignacionCartera.Externa(operacion, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        // Se cierra después de colgarle la cartera (el dominio no deja colgarla de una cerrada).
        if (cerrada) operacion.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
        if (conLinea) e.Db.TenantsConLineaInbound.Add(tenant.Id);
        return operacion;
    }

    private static Task<System.Collections.Generic.IReadOnlyList<ClienteAutorizadoDto>> Pedir(
        Escenario e, bool porPapel, string rol = "GestorCae") =>
        new ObtenerClientesAutorizadosQueryHandler(e.Db, e.Db, new Usuaria(e.Origen.Id, rol))
            .Handle(new ObtenerClientesAutorizadosQuery { PorPapel = porPapel }, default);

    [Fact]
    public async Task Con_Outbound_e_Inbound_sobre_el_mismo_Tenant_el_Gestor_CAE_recibe_dos_entradas()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        Inbound(e, e.Norte);
        await e.Db.SaveChangesAsync();

        var porPapel = await Pedir(e, porPapel: true);

        porPapel.Where(c => c.TenantId == e.Norte.Id).Select(c => c.Servicio)
            .Should().Equal(ServicioCae.Outbound, ServicioCae.Inbound);
        porPapel.Single(c => c.EsOrigen).Servicio.Should().BeNull("el Tenant de origen no tiene papel de Operación");

        // Los demás lectores (recuentos, Mi trabajo, Visión de cartera) siguen recibiendo una entrada por Tenant.
        var porTenant = await Pedir(e, porPapel: false);
        porTenant.Count(c => c.TenantId == e.Norte.Id).Should().Be(1);
        porTenant.Should().OnlyContain(c => c.Servicio == null);
    }

    [Fact]
    public async Task Con_solo_Outbound_el_Gestor_CAE_recibe_una_entrada_con_servicio_Outbound()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        await e.Db.SaveChangesAsync();

        (await Pedir(e, porPapel: true)).Where(c => !c.EsOrigen).Select(c => (c.TenantId, c.Servicio))
            .Should().Equal((e.Norte.Id, (ServicioCae?)ServicioCae.Outbound));
    }

    [Theory]
    [InlineData("cerrada")]
    [InlineData("sin-linea")]
    public async Task Una_Asignacion_de_Operacion_Inbound_que_no_autoriza_no_aparece(string caso)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        Inbound(e, e.Norte, cerrada: caso == "cerrada", conLinea: caso != "sin-linea");
        Inbound(e, e.Sur, cerrada: caso == "cerrada", conLinea: caso != "sin-linea");
        await e.Db.SaveChangesAsync();

        var entradas = await Pedir(e, porPapel: true);

        entradas.Should().NotContain(c => c.Servicio == ServicioCae.Inbound);
        entradas.Should().NotContain(c => c.TenantId == e.Sur.Id, "Empresa Sur solo se alcanzaba por esa operación Inbound");
    }

    [Fact]
    public async Task Un_Tenant_alcanzado_solo_por_Inbound_entra_en_el_selector_de_papel_y_no_en_la_lista_por_Tenant()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        (await Pedir(e, porPapel: true)).Should().ContainSingle(c => c.TenantId == e.Sur.Id && c.Servicio == ServicioCae.Inbound
                                                                    && c.Nombre == "Empresa Sur");
        (await Pedir(e, porPapel: false)).Should().NotContain(c => c.TenantId == e.Sur.Id,
            "la lista por Tenant de los demás lectores no se ensancha con la vía Inbound");
    }

    [Fact]
    public async Task La_via_heredada_y_una_Asignacion_de_Operacion_Inbound_sobre_el_mismo_Tenant_dan_dos_entradas()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        // Vía heredada: DelegacionTenant vigente con la fila del usuario, sin Asignación de Cartera Outbound.
        var delegacion = new DelegacionTenant(e.Origen.Id, e.Norte.Id);
        e.Db.AddRange(delegacion, new AsignacionOperadorDelegado(delegacion.Id, Usuario, "GestorCae"));
        Inbound(e, e.Norte);
        await e.Db.SaveChangesAsync();

        (await Pedir(e, porPapel: true)).Where(c => c.TenantId == e.Norte.Id).Select(c => c.Servicio)
            .Should().BeEquivalentTo(new ServicioCae?[] { null, ServicioCae.Inbound },
                "la vía heredada conserva su entrada sin papel junto a la de Inbound: el selector la ofrece y se vuelve a ella");
    }

    /// <summary>
    /// Decisión O-2 (b) de Chris (2026-11-17): el selector de papel es de quien llega por una Asignación de Operación del
    /// Tenant entero de su Operador CAE externo, sin cartera, igual que el POST le autoriza a abrirlo con ese servicio.
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task Con_otro_perfil_del_Operador_CAE_externo_que_llega_por_Asignacion_de_Operacion_recibe_las_entradas_con_papel(string rol)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte, conCartera: false);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        var porPapel = await Pedir(e, porPapel: true, rol);

        porPapel.Where(c => !c.EsOrigen).Select(c => (c.TenantId, c.Servicio)).Should().Equal(
            (e.Norte.Id, (ServicioCae?)ServicioCae.Outbound), (e.Sur.Id, (ServicioCae?)ServicioCae.Inbound));
        porPapel.Single(c => c.EsOrigen).Servicio.Should().BeNull("el Tenant de origen no tiene papel de Operación");
        porPapel.Should().OnlyContain(c => !c.EsCarteraGestorCae,
            "fuera del Gestor CAE ninguna entrada es de cartera: la decisión 7 quater no elige Tenant por defecto");
        ClientesAutorizados.TenantPorDefecto(porPapel).Should().BeNull();
        ClientesAutorizados.SelectorVisible(porPapel, ClientesAutorizados.Activo(porPapel, null)).Should().BeTrue();

        // La lista por Tenant de los demás lectores no cambia: sin papel y sin el Tenant que solo alcanza Inbound.
        (await Pedir(e, porPapel: false, rol)).Should().OnlyContain(c => c.Servicio == null)
            .And.NotContain(c => c.TenantId == e.Sur.Id);
    }

    /// <summary>
    /// El Administrador del Tenant propio (aquí, el de Empresa Norte, Tenant beneficiario que gestiona el Operador CAE
    /// externo) no llega a ningún Tenant por una Asignación de Operación: las de Empresa Norte son del Operador CAE, no
    /// suyas, y su lista por papel es la de siempre, sin selector de papel.
    /// </summary>
    [Fact]
    public async Task El_Administrador_del_Tenant_propio_sigue_sin_selector_de_papel()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte, conCartera: false);
        Inbound(e, e.Norte);
        await e.Db.SaveChangesAsync();

        var handler = new ObtenerClientesAutorizadosQueryHandler(e.Db, e.Db, new Usuaria(e.Norte.Id, "Administrador"));
        var porPapel = await handler.Handle(new ObtenerClientesAutorizadosQuery { PorPapel = true }, default);
        var porTenant = await handler.Handle(new ObtenerClientesAutorizadosQuery(), default);

        porPapel.Should().Equal(porTenant);
        porPapel.Should().OnlyContain(c => c.Servicio == null && c.EsOrigen && c.TenantId == e.Norte.Id);
    }

    /// <summary>La lista de los lectores del Tenant activo (<see cref="ObtenerClientesAutorizadosQuery.ParaTenantActivo"/>).</summary>
    private static Task<System.Collections.Generic.IReadOnlyList<ClienteAutorizadoDto>> PedirParaTenantActivo(
        Escenario e, Tenant activo, PapelActivo papel, string rol = "GestorCae", bool paraTenantActivo = true) =>
        new ObtenerClientesAutorizadosQueryHandler(e.Db, e.Db, new Usuaria(e.Origen.Id, rol, papel), tenantActual: new TenantActivo(activo.Id))
            .Handle(new ObtenerClientesAutorizadosQuery { ParaTenantActivo = paraTenantActivo }, default);

    [Fact]
    public async Task Para_el_Tenant_activo_con_papel_Inbound_la_lista_trae_el_Tenant_alcanzado_solo_por_Inbound_y_lo_resuelve_activo()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        var lista = await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound);

        lista.Where(c => c.Servicio is null).Select(c => c.TenantId).Should().Equal(
            (await Pedir(e, porPapel: false)).Select(c => c.TenantId), "la lista por Tenant de siempre va entera y sin papel");
        lista.Should().ContainSingle(c => c.Servicio == ServicioCae.Inbound).Which.TenantId.Should().Be(e.Sur.Id);
        var activo = ClientesAutorizados.Activo(lista, e.Sur.Id);
        activo!.Nombre.Should().Be("Empresa Sur", "el Tenant activo de la interfaz es el elegido con la entrada Inbound");
        ClientesAutorizados.SelectorVisible(lista, activo).Should().BeTrue("la entrada Inbound hace visible el selector de la cabecera");
        ClientesAutorizados.PideElegirEmpresa(lista, e.Sur.Id).Should().BeFalse("con Empresa Sur activa no hay estado 4a");

        // Control: la lista por Tenant de siempre no lo trae, y por eso los lectores caían al origen.
        var sinOpcion = await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound, paraTenantActivo: false);
        ClientesAutorizados.Activo(sinOpcion, e.Sur.Id)!.EsOrigen.Should().BeTrue();
    }

    [Fact]
    public async Task Para_el_Tenant_activo_alcanzado_solo_por_Inbound_como_unico_externo_el_selector_es_visible()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        var lista = await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound);

        lista.Select(c => c.TenantId).Should().Equal(e.Origen.Id, e.Sur.Id);
        ClientesAutorizados.SelectorVisible(lista, ClientesAutorizados.Activo(lista, e.Sur.Id)).Should()
            .BeTrue("sin otro Tenant gestionado, la entrada Inbound es la que hace visible el selector (D-4)");
    }

    [Fact]
    public async Task Para_el_Tenant_activo_no_basta_una_Asignacion_de_Operacion_Inbound_sobre_otro_Tenant()
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Inbound(e, e.Norte);
        await e.Db.SaveChangesAsync();

        (await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound)).Should().NotContain(c => c.TenantId == e.Sur.Id,
            "solo se añade el Tenant activo si una Asignación de Operación Inbound lo alcanza a él");
    }

    [Theory]
    [InlineData(PapelActivo.Outbound)]
    [InlineData(PapelActivo.SinPapel)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza)]
    public async Task Para_el_Tenant_activo_sin_papel_Inbound_la_lista_es_la_por_Tenant(PapelActivo papel)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        (await PedirParaTenantActivo(e, e.Sur, papel)).Should().Equal(await Pedir(e, porPapel: false),
            "solo el papel Inbound resuelto por el servidor trae el Tenant alcanzado solo por Inbound");
    }

    [Theory]
    [InlineData("Norte", PapelActivo.Outbound)]
    [InlineData("Norte", PapelActivo.Inbound)]
    [InlineData("Origen", PapelActivo.Inbound)]
    public async Task Para_el_Tenant_activo_que_ya_esta_en_la_lista_por_Tenant_nada_cambia(string activo, PapelActivo papel)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte);
        Inbound(e, e.Norte);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        (await PedirParaTenantActivo(e, activo == "Norte" ? e.Norte : e.Origen, papel))
            .Should().Equal(await Pedir(e, porPapel: false), "la entrada Outbound y la del origen siguen como en la lista por Tenant");
    }

    [Theory]
    [InlineData("Administrador", "cerrada")]
    [InlineData("DireccionCae", "sin-linea")]
    [InlineData("GestorCae", "cerrada")]
    [InlineData("GestorCae", "sin-linea")]
    public async Task Para_el_Tenant_activo_sin_Inbound_que_autorice_la_lista_es_la_por_Tenant(string rol, string caso)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte, conCartera: rol == "GestorCae");
        Inbound(e, e.Sur, cerrada: caso == "cerrada", conLinea: caso != "sin-linea");
        await e.Db.SaveChangesAsync();

        (await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound, rol)).Should().Equal(await Pedir(e, porPapel: false, rol),
            "una operación Inbound que no autoriza no trae nada, sea cual sea el perfil");
    }

    /// <summary>O-2 (b): con papel Inbound, los lectores del Tenant activo pintan el Tenant para cualquier perfil que llegue por la Asignación de Operación.</summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task Para_el_Tenant_activo_con_papel_Inbound_otro_perfil_del_Operador_CAE_externo_ve_el_Tenant(string rol)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte, conCartera: false);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        var lista = await PedirParaTenantActivo(e, e.Sur, PapelActivo.Inbound, rol);

        var sur = lista.Should().ContainSingle(c => c.Servicio == ServicioCae.Inbound).Which;
        sur.TenantId.Should().Be(e.Sur.Id);
        sur.EsCarteraGestorCae.Should().BeFalse("solo el Gestor CAE tiene entradas de cartera");
        var activo = ClientesAutorizados.Activo(lista, e.Sur.Id);
        activo!.Nombre.Should().Be("Empresa Sur", "la cabecera pinta el Tenant elegido con la entrada Inbound, no el origen");
        ClientesAutorizados.PideElegirEmpresa(lista, e.Sur.Id).Should().BeFalse();

        // El Administrador del Tenant propio de Empresa Sur no llega por esa operación: nada cambia para él.
        var propio = await new ObtenerClientesAutorizadosQueryHandler(e.Db, e.Db,
                new Usuaria(e.Sur.Id, "Administrador", PapelActivo.Inbound), tenantActual: new TenantActivo(e.Sur.Id))
            .Handle(new ObtenerClientesAutorizadosQuery { ParaTenantActivo = true }, default);
        propio.Should().OnlyContain(c => c.Servicio == null && c.EsOrigen);
    }
}
