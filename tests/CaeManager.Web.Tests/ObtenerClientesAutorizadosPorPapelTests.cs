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

    private sealed class Usuaria(Guid origen, string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
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

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Sin_perfil_Gestor_CAE_la_lista_por_papel_es_la_de_siempre(string rol)
    {
        var e = await CrearAsync();
        await using var _ = e.Db;
        Outbound(e, e.Norte, conCartera: false);
        Inbound(e, e.Sur);
        await e.Db.SaveChangesAsync();

        var porPapel = await Pedir(e, porPapel: true, rol);
        var porTenant = await Pedir(e, porPapel: false, rol);

        porPapel.Should().Equal(porTenant, "el Administrador del Tenant y los demás perfiles no tienen selector de papel");
        porPapel.Should().OnlyContain(c => c.Servicio == null);
    }
}
