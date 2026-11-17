using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Issue #2016: el Administrador del Tenant propietario ve las lecturas que Soporte TALVEG hizo
/// sobre su Tenant bajo Sesión Privilegiada, y solo las de su Tenant. Barrera de Application con
/// dobles: el contexto falso no aplica ni filtro global ni RLS, así que lo que no se devuelve lo
/// está ocultando el handler. La barrera de la base la mide
/// <c>ActividadSoporteDelTenantPropietarioBajoRuntimeTests</c> (integración, solo en CI).
/// </summary>
public class ObtenerActividadSoporteQueryTests
{
    private static readonly Guid TenantPropietario = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();
    private static readonly Guid Tecnico = Guid.NewGuid();
    private static readonly Guid Sesion = Guid.NewGuid();

    // ── Por Sesión Privilegiada ─────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_ve_las_lecturas_de_la_sesion_sobre_su_Tenant_sin_la_identidad_del_tecnico()
    {
        var contexto = new TenantsQueryContextFalso();
        var antigua = Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "ObtenerHistorialImportacionesQuery"), TenantPropietario, minutos: -30);
        var reciente = Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"), TenantPropietario, minutos: -5);
        contexto.ListaRegistrosActividadSoporte.AddRange([antigua, reciente]);
        // Otra sesión sobre el mismo Tenant y una fila de la vía heredada: no son de esta visita.
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Guid.NewGuid(), TipoActividadSoporte.Lectura, "AnalizarPlantillaMaestraQuery"), TenantPropietario));
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorViaHeredada(
            Tecnico, Guid.NewGuid(), TipoActividadSoporte.Navegacion, "/documentos"), TenantPropietario));

        var actividad = await HandlerDelAdministrador(contexto).Handle(
            ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None);

        actividad.Select(a => a.Id).Should().Equal([reciente.Id, antigua.Id], "la más reciente primero, y solo las de esta sesión");
        actividad.Should().OnlyContain(a => a.SesionPrivilegiadaId == Sesion && a.Tipo == TipoActividadSoporte.Lectura);
        actividad.Should().OnlyContain(a => a.UsuarioSoporteId == null,
            "el Tenant propietario ve «Soporte TALVEG», no a la persona (ADR-011 § 8.7, minimización)");
        actividad[0].Detalle.Should().Be("EjecutarImportacionCommand", "la operación leída viaja en el detalle");
    }

    [Fact]
    public async Task Nunca_devuelve_lecturas_de_otro_Tenant_aunque_nombren_la_misma_sesion()
    {
        // Sin RLS ni filtro global (contexto falso): solo el filtro explícito por Tenant objetivo
        // separa estas filas. Es la defensa si el contexto de datos apuntara a otro Tenant.
        var contexto = new TenantsQueryContextFalso();
        var propia = Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"), TenantPropietario);
        contexto.ListaRegistrosActividadSoporte.Add(propia);
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "AnalizarImportacionExcelQuery"), OtroTenant));

        var actividad = await HandlerDelAdministrador(contexto).Handle(
            ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None);

        actividad.Should().ContainSingle().Which.Id.Should().Be(propia.Id);
    }

    [Fact]
    public async Task El_Administrador_de_otro_Tenant_no_ve_las_lecturas_de_una_sesion_ajena()
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"), TenantPropietario));
        var handler = new ObtenerActividadSoporteQueryHandler(
            contexto,
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: OtroTenant),
            AutorizacionDelegacionFalsa.AdministradorDe(OtroTenant));

        (await handler.Handle(ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None))
            .Should().BeEmpty("es Administrador, pero de su Tenant: la sesión apunta al de otro");
    }

    [Fact]
    public async Task Sin_ser_Administrador_del_Tenant_de_origen_no_ve_nada()
    {
        var contexto = ContextoConUnaLectura();
        var handler = new ObtenerActividadSoporteQueryHandler(
            contexto,
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            new AutorizacionDelegacionFalsa(autoriza: false));

        (await handler.Handle(ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None))
            .Should().BeEmpty("solo el Administrador del Tenant propietario ve las lecturas de Soporte TALVEG");
    }

    [Fact]
    public async Task Pregunta_la_autoridad_por_el_Tenant_de_origen()
    {
        var autorizacion = AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario);
        var handler = new ObtenerActividadSoporteQueryHandler(
            ContextoConUnaLectura(),
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            autorizacion);

        (await handler.Handle(ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None))
            .Should().ContainSingle();
        autorizacion.UltimoTenantConsultado.Should().Be(TenantPropietario);
    }

    [Fact]
    public async Task Sin_usuario_o_sin_Tenant_de_origen_no_ve_nada()
    {
        var sinTenant = new ObtenerActividadSoporteQueryHandler(
            ContextoConUnaLectura(),
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: null),
            new AutorizacionDelegacionFalsa(autoriza: true));
        var sinUsuario = new ObtenerActividadSoporteQueryHandler(
            ContextoConUnaLectura(),
            new CurrentUserServiceFalso(usuarioId: null, tenantOrigenId: TenantPropietario),
            new AutorizacionDelegacionFalsa(autoriza: true));

        (await sinTenant.Handle(ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None)).Should().BeEmpty();
        (await sinUsuario.Handle(ObtenerActividadSoporteQuery.PorSesionPrivilegiada(Sesion), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_los_dos_agrupadores_o_con_ninguno_no_ve_nada()
    {
        var handler = HandlerDelAdministrador(ContextoConUnaLectura());

        (await handler.Handle(new ObtenerActividadSoporteQuery(Guid.NewGuid(), Sesion), CancellationToken.None))
            .Should().BeEmpty("una visita cuelga de exactamente un agrupador, como el registro");
        (await handler.Handle(new ObtenerActividadSoporteQuery(null), CancellationToken.None)).Should().BeEmpty();
    }

    // ── Por Delegación de Tenant de Soporte (vía heredada, sin cambios) ─────

    [Fact]
    public async Task El_Tenant_propietario_visitado_sigue_viendo_la_actividad_de_la_delegacion_y_no_la_de_las_sesiones()
    {
        var plataforma = new Tenant("Plataforma");
        plataforma.MarcarComoPlataforma();
        var delegacion = DelegacionTenant.ParaSoporte(plataforma.Id, TenantPropietario);
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(plataforma);
        contexto.ListaDelegacionesTenant.Add(delegacion);
        var navegacion = Registro(RegistroActividadSoporte.PorViaHeredada(
            Tecnico, delegacion.Id, TipoActividadSoporte.Navegacion, "/documentos"), TenantPropietario);
        contexto.ListaRegistrosActividadSoporte.Add(navegacion);
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"), TenantPropietario));

        // Cualquier usuario del Tenant propietario visitado, como antes: la vía heredada no pide rol.
        var handler = new ObtenerActividadSoporteQueryHandler(
            contexto,
            new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: TenantPropietario),
            new AutorizacionDelegacionFalsa(autoriza: false));

        var actividad = await handler.Handle(ObtenerActividadSoporteQuery.PorViaHeredada(delegacion.Id), CancellationToken.None);

        var fila = actividad.Should().ContainSingle().Subject;
        fila.Id.Should().Be(navegacion.Id);
        fila.UsuarioSoporteId.Should().Be(Tecnico, "la vía heredada conserva lo que devolvía");
        fila.SesionPrivilegiadaId.Should().BeNull();
    }

    [Fact]
    public async Task Un_Tenant_ajeno_a_la_delegacion_no_ve_su_actividad()
    {
        var plataforma = new Tenant("Plataforma");
        plataforma.MarcarComoPlataforma();
        var delegacion = DelegacionTenant.ParaSoporte(plataforma.Id, TenantPropietario);
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(plataforma);
        contexto.ListaDelegacionesTenant.Add(delegacion);
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorViaHeredada(
            Tecnico, delegacion.Id, TipoActividadSoporte.Navegacion, "/documentos"), TenantPropietario));

        var handler = new ObtenerActividadSoporteQueryHandler(
            contexto,
            new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: OtroTenant),
            new AutorizacionDelegacionFalsa(autoriza: true));

        (await handler.Handle(ObtenerActividadSoporteQuery.PorViaHeredada(delegacion.Id), CancellationToken.None))
            .Should().BeEmpty();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private static ObtenerActividadSoporteQueryHandler HandlerDelAdministrador(TenantsQueryContextFalso contexto) =>
        new(contexto,
            new CurrentUserServiceFalso(Administrador, tenantOrigenId: TenantPropietario),
            AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario));

    private static TenantsQueryContextFalso ContextoConUnaLectura()
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaRegistrosActividadSoporte.Add(Registro(RegistroActividadSoporte.PorSesionPrivilegiada(
            Tecnico, Sesion, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"), TenantPropietario));
        return contexto;
    }

    private static RegistroActividadSoporte Registro(RegistroActividadSoporte registro, Guid tenant, int minutos = 0)
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(registro, tenant);
        typeof(RegistroActividadSoporte).GetProperty(nameof(RegistroActividadSoporte.OcurridaEnUtc))!
            .SetValue(registro, DateTime.UtcNow.AddMinutes(minutos));
        return registro;
    }
}
