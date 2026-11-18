using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="AlcanceDatosService.TriajeVisibleAsync"/> (D-6 b, R-3a): la cola de triaje de Comunicaciones (un hilo sin
/// Cliente ni Empresa contraparte) no tiene cartera que la acote, así que solo la ve quien tiene acceso total en el Tenant o
/// un rol de gestión de Comunicaciones con un ámbito efectivo del Tenant entero. Falla cerrado: rol nulo, rol desconocido,
/// ámbito acotado o una Sesión Privilegiada fuera de su Tenant objetivo no la ven. Estas pruebas deciden antes de leer la
/// base: el contexto apunta a un servidor que no existe, así que una consulta las haría fallar. El Gestor CAE con una
/// Asignación de Cartera universal bajo una Asignación de Operación acotada, que sí lee la base, se prueba bajo PostgreSQL
/// en <c>AlcanceCarteraUniversalTenantCompletoTests</c> (integración, solo en CI).
/// </summary>
public class TriajeVisibleTests
{
    private static readonly Guid TenantActual = Guid.NewGuid();

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Con_acceso_total_se_ve_el_triaje(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Outbound));

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue("control: el perfil de organización tiene acceso total");
        (await alcance.TriajeVisibleAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Cliente")]
    [InlineData("RolInventado")]
    [InlineData("")]
    public async Task Sin_un_rol_de_gestion_de_Comunicaciones_no_se_ve_el_triaje(string? rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Outbound));

        (await alcance.TriajeVisibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Un_rol_de_la_lista_que_alcanza_el_Tenant_entero_sin_acceso_total_ve_el_triaje()
    {
        // Coordinador CAE por la vía de Operación con papel Inbound dentro de una petición de Inbound: cartera universal,
        // sin acceso total (AlcanceConPapelInboundTests).
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("CoordinadorCae", PapelActivo.Inbound));

        using (AmbitoPeticionInbound.Abrir())
        {
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("control: el Coordinador CAE no tiene acceso total");
            (await alcance.TriajeVisibleAsync()).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Administrador")]
    public async Task Un_rol_de_la_lista_sin_ningun_ambito_no_ve_el_triaje(string rol)
    {
        // Papel Inbound fuera de una petición de Inbound: ningún perfil tiene alcance, así que tampoco triaje.
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Inbound));

        (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeFalse("control: sin ámbito");
        (await alcance.TriajeVisibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Con_una_operacion_que_ya_no_autoriza_no_se_ve_el_triaje()
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("GestorCae", PapelActivo.OperacionQueYaNoAutoriza));

        (await alcance.TriajeVisibleAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Una_Sesion_Privilegiada_de_lectura_en_su_Tenant_objetivo_ve_el_triaje(CapacidadPrivilegio capacidad)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(null, PapelActivo.Outbound), new ConSesion(TenantActual, capacidad));

        (await alcance.TriajeVisibleAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_de_Aprovisionamiento_en_su_Tenant_objetivo_ve_el_triaje_por_su_acceso_total()
    {
        // Aprovisionamiento (PD-A3) tiene acceso total real en su Tenant objetivo, como SoporteLectura y BreakGlass, para
        // el alta de contenido CAE; sin lente de demo, ese es también el acceso total efectivo, y con él se ve el triaje.
        // Sin rol de negocio: decide el acceso total, no la lista de roles.
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(null, PapelActivo.Outbound),
            new ConSesion(TenantActual, CapacidadPrivilegio.Aprovisionamiento));

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue("control: Aprovisionamiento tiene acceso total en su Tenant objetivo");
        (await alcance.TriajeVisibleAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("CoordinadorCae", true)]
    [InlineData("Cliente", false)]
    public async Task Con_la_misma_identidad_y_el_mismo_ambito_solo_el_rol_decide_que_el_rol_Cliente_no_ve_el_triaje(
        string rol, bool esperado)
    {
        // El rol es la única variable: el mismo doble, por la vía de Operación, con papel Inbound dentro de una petición de
        // Inbound y sin Sesión Privilegiada. Con CoordinadorCae ese ámbito es el Tenant entero sin acceso total (control
        // del instrumento). Con Cliente, TriajeVisibleAsync contesta sin leer la base (el contexto no tiene servidor y
        // la cartera del rol Cliente sí la leería), así que ninguna cartera, ni la total ni la del Tenant entero, puede
        // abrirle la cola: la cierra el rol antes de mirar el ámbito.
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Inbound));

        using (AmbitoPeticionInbound.Abrir())
        {
            if (esperado)
            {
                (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("control: no es el acceso total el que lo abre");
                (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeTrue("control: este ámbito es el Tenant entero");
            }

            (await alcance.TriajeVisibleAsync()).Should().Be(esperado);
        }
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura, "Administrador")]
    [InlineData(CapacidadPrivilegio.BreakGlass, "GestorCae")]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento, "Administrador")]
    public async Task Una_Sesion_Privilegiada_abierta_para_otro_Tenant_no_ve_el_triaje_aqui_ni_con_un_rol_de_la_lista(
        CapacidadPrivilegio capacidad, string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Outbound), new ConSesion(Guid.NewGuid(), capacidad));

        (await alcance.TriajeVisibleAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_Sesion_Privilegiada_sin_acceso_total_aqui_corta_el_triaje_aunque_el_rol_alcance_el_Tenant_entero(
        CapacidadPrivilegio? capacidad)
    {
        // El corte explícito de la Sesión Privilegiada sin acceso total en este Tenant: con el mismo doble, un Coordinador
        // CAE por la vía de Operación con papel Inbound alcanza el Tenant entero (control, sin sesión). Con una sesión
        // abierta para otro Tenant (SoporteLectura) o sin capacidad de lectura aquí (AdminPlataforma), el rol y el ámbito
        // darían triaje, y solo el corte lo cierra. Con un Administrador estos casos no medirían el corte, porque sin
        // acceso total su ámbito es ninguno.
        await using var contexto = ContextoSinServidor();
        ISesionPrivilegiadaActual sesion = capacidad switch
        {
            null => new ConSesion(null),
            CapacidadPrivilegio.AdminPlataforma => new ConSesion(TenantActual, CapacidadPrivilegio.AdminPlataforma),
            { } otra => new ConSesion(Guid.NewGuid(), otra),
        };
        var alcance = Alcance(contexto, new Usuario("CoordinadorCae", PapelActivo.Inbound), sesion);

        using (AmbitoPeticionInbound.Abrir())
        {
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("control: sin acceso total en este Tenant");
            (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeTrue("control: el rol y el ámbito darían triaje");
            (await alcance.TriajeVisibleAsync()).Should().Be(capacidad is null);
        }
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    public async Task Una_Sesion_Privilegiada_sin_capacidad_de_lectura_no_ve_el_triaje_ni_con_un_rol_de_la_lista(
        CapacidadPrivilegio capacidad)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("Administrador", PapelActivo.Outbound),
            new ConSesion(TenantActual, capacidad));

        (await alcance.TriajeVisibleAsync()).Should().BeFalse();
    }

    private static AlcanceDatosService Alcance(
        CaeManagerDbContext contexto, ICurrentUserService usuario, ISesionPrivilegiadaActual? sesion = null) =>
        new(contexto, usuario, new TenantActualAmbiental { TenantId = TenantActual }, sesion ?? new ConSesion(null));

    /// <summary>Un contexto que nunca conecta: el puerto 1 de localhost no tiene ningún PostgreSQL.</summary>
    private static CaeManagerDbContext ContextoSinServidor()
    {
        var tenant = new TenantActualAmbiental { TenantId = TenantActual };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ninguna;Timeout=1")
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    /// <summary>Una persona que entra por la Asignación de Operación de su organización, como en <c>AlcanceConPapelInboundTests</c>.</summary>
    private sealed class Usuario(string? rol, PapelActivo papel) : ICurrentUserService, IEncargoDeAdministracionActual
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
        public Task<bool> OperaPorAsignacionDeOperacionAsync() => Task.FromResult(true);
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);
        public Task<Guid?> EncargoPlenoAsync() => Task.FromResult<Guid?>(null);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;
        public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => false;
    }

    private sealed class ConSesion(Guid? tenantObjetivo, CapacidadPrivilegio capacidad = CapacidadPrivilegio.SoporteLectura)
        : ISesionPrivilegiadaActual
    {
        private SesionPrivilegiadaActiva? Sesion => tenantObjetivo is { } tenant
            ? new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), tenant, capacidad, null)
            : null;

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(Sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(Sesion);
    }
}
