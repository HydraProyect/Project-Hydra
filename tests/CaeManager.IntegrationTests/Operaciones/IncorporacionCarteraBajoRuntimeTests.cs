using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Solicitud de incorporación a cartera contra PostgreSQL real, autenticando
/// como <c>cae_app_runtime</c> (RLS siempre aplica) y con el
/// <see cref="TenantRlsConnectionInterceptor"/> real fijando las coordenadas
/// de sesión. Los handlers y el catálogo son los de producción.
///
/// <para>
/// Lo que solo esta capa puede probar: que un Gestor CAE de otro Operador CAE
/// no alcanza candidatos ni solicitudes ajenas aunque el código le pasara el
/// Operador CAE equivocado; que aceptar escribe en una sola transacción la
/// cartera (política del Tenant propietario) y la solicitud (política del
/// Operador CAE de origen); y que dos aceptaciones a la vez dejan una sola
/// cartera.
/// </para>
/// </summary>
public class IncorporacionCarteraBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _operador = new("Operador CAE de prueba");
    private readonly Tenant _otroOperador = new("Otro Operador CAE de prueba");
    private readonly Tenant _empresa = new("Empresa propietaria de prueba");

    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _gestorAjeno = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Guid _otroCoordinador = Guid.NewGuid();

    private Guid _operacionId;
    private Guid _vinculoId;

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_operador, _otroOperador, _empresa);

        var operacion = AsignacionOperacion.Externa(
            _empresa.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);

        var vinculo = new DelegacionTenant(_operador.Id, _empresa.Id);
        contexto.DelegacionesTenant.Add(vinculo);

        await contexto.SaveChangesAsync();
        _operacionId = operacion.Id;
        _vinculoId = vinculo.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Aislamiento entre Operadores CAE ──────────────────────────────────

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_ve_los_candidatos_ajenos_ni_nombrando_ese_Operador_CAE()
    {
        // Control positivo: el Gestor CAE del Operador CAE sí lo ve.
        await using (var propio = ContextoRuntime(_gestor, _operador.Id, "GestorCae"))
        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            (await Catalogo(propio).ObtenerCandidatosAsync(_operador.Id, _gestor))
                .Should().ContainSingle().Which.PropietarioTenantId.Should().Be(_empresa.Id);
        }

        // Aunque un handler defectuoso le pasara el Operador CAE ajeno, RLS no
        // le deja leer la operación: no está en ninguna de sus dos posiciones.
        await using var ajeno = ContextoRuntime(_gestorAjeno, _otroOperador.Id, "GestorCae");
        using (AmbitoTenantExplicito.Establecer(_otroOperador.Id))
        {
            (await Catalogo(ajeno).ObtenerCandidatosAsync(_operador.Id, _gestorAjeno)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_ve_ni_crea_solicitudes_ajenas()
    {
        var solicitudId = await SolicitarAsync();

        await using var ajeno = ContextoRuntime(_coordinador, _otroOperador.Id, "CoordinadorCae");
        using (AmbitoTenantExplicito.Establecer(_otroOperador.Id))
        {
            var repositorio = new SolicitudIncorporacionCarteraRepository(ajeno);
            (await repositorio.ListarPendientesAsync(_operador.Id)).Should().BeEmpty(
                "la política operador_de_la_solicitud filtra por el Operador CAE de la cuenta, no por el parámetro");
            (await repositorio.ObtenerPorIdAsync(solicitudId, _operador.Id)).Should().BeNull();

            // Escribir a nombre del Operador CAE ajeno: lo corta el WITH CHECK.
            await using var propietario = ContextoPropietario();
            var operacion = await propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionId);
            repositorio.Agregar(SolicitudIncorporacionCartera.Crear(operacion, _gestorAjeno, "Colarse", DateTime.UtcNow));

            var accion = async () => await ajeno.SaveChangesAsync();
            (await accion.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    // ── Aceptar y revocar, de extremo a extremo ───────────────────────────

    [Fact]
    public async Task Aceptar_crea_cartera_fila_heredada_y_aviso_y_revocar_los_retira()
    {
        var solicitudId = await SolicitarAsync();

        await using (var contexto = ContextoRuntime(_coordinador, _operador.Id, "CoordinadorCae"))
        {
            var resultado = await Aceptar(contexto)
                .Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitudId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        }

        await using (var propietario = ContextoPropietario())
        {
            var solicitud = await propietario.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
            solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
            solicitud.ResueltaPorUsuarioId.Should().Be(_coordinador);

            var cartera = await propietario.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.UsuarioId == _gestor);
            cartera.Id.Should().Be(solicitud.AsignacionCarteraId!.Value);
            cartera.PropietarioTenantId.Should().Be(_empresa.Id);
            cartera.OperadorTenantId.Should().Be(_operador.Id);
            cartera.Estado.Should().Be(EstadoAsignacion.Vigente);
            cartera.Rol.Should().Be("GestorCae");
            cartera.AmbitoCentroId.Should().BeNull();
            cartera.AmbitoRelacionClienteId.Should().BeNull();

            var fila = await propietario.AsignacionesOperadorDelegado.AsNoTracking().SingleAsync(a => a.UsuarioId == _gestor);
            fila.DelegacionTenantId.Should().Be(_vinculoId);
            fila.Id.Should().Be(solicitud.AsignacionOperadorDelegadoId!.Value);

            // El aviso es best-effort: si RLS lo rechazara, el handler lo
            // registraría y seguiría. Por eso se comprueba aquí que existe.
            var aviso = await propietario.NotificacionesUsuario.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(n => n.UsuarioDestinatarioId == _gestor);
            aviso.TenantId.Should().Be(_operador.Id, "el aviso es del Gestor CAE: se sella con su organización");
        }

        await using (var contexto = ContextoRuntime(_gestor, _operador.Id, "GestorCae"))
        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            (await Catalogo(contexto).ObtenerCandidatosAsync(_operador.Id, _gestor)).Should().BeEmpty(
                "ya lo tiene en cartera");
        }

        await using (var contexto = ContextoRuntime(_coordinador, _operador.Id, "CoordinadorCae"))
        {
            var resultado = await Revocar(contexto)
                .Handle(new RevocarIncorporacionCarteraCommand(solicitudId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        }

        await using (var propietario = ContextoPropietario())
        {
            var solicitud = await propietario.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
            solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Revocada);
            solicitud.RevocadaPorUsuarioId.Should().Be(_coordinador);

            var cartera = await propietario.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.UsuarioId == _gestor);
            cartera.Estado.Should().Be(EstadoAsignacion.Cerrada);
            cartera.MotivoCierre.Should().Be(MotivoCierreAsignacion.RetiradaPorElOperador);

            (await propietario.AsignacionesOperadorDelegado.AnyAsync(a => a.UsuarioId == _gestor)).Should().BeFalse(
                "sin la fila heredada el Tenant sale del selector y del fan-out");
        }

        await using (var contexto = ContextoRuntime(_gestor, _operador.Id, "GestorCae"))
        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            (await Catalogo(contexto).ObtenerCandidatosAsync(_operador.Id, _gestor)).Should().ContainSingle(
                "revocada la incorporación, la Empresa vuelve a ser candidata");
        }
    }

    [Fact]
    public async Task El_repositorio_filtra_por_Operador_CAE_aunque_la_conexion_no_aplique_RLS()
    {
        var solicitudId = await SolicitarAsync();

        // Conexión propietaria de la tabla: RLS no actúa. Lo que queda es el
        // filtro del repositorio, la segunda barrera si alguna vez se consulta
        // desde un contexto sin coordenadas (tarea de fondo, retirada de demo).
        await using var propietario = ContextoPropietario();
        var repositorio = new SolicitudIncorporacionCarteraRepository(propietario);

        (await repositorio.ObtenerPorIdAsync(solicitudId, _operador.Id)).Should().NotBeNull("control positivo");
        (await repositorio.ObtenerPorIdAsync(solicitudId, _otroOperador.Id)).Should().BeNull();

        (await repositorio.ListarPendientesAsync(_operador.Id)).Should().ContainSingle(s => s.Id == solicitudId, "control positivo");
        (await repositorio.ListarPendientesAsync(_otroOperador.Id)).Should().BeEmpty();
    }

    // ── Concurrencia ─────────────────────────────────────────────────────

    [Fact]
    public async Task Dos_Coordinadores_CAE_que_aceptan_intercalados_dejan_una_sola_cartera()
    {
        var solicitudId = await SolicitarAsync();

        await using var primero = ContextoRuntime(_coordinador, _operador.Id, "CoordinadorCae");
        await using var segundo = ContextoRuntime(_otroCoordinador, _operador.Id, "CoordinadorCae");

        // Los dos cargan la solicitud pendiente y preparan la incorporación
        // antes de que ninguno guarde: el peor intercalado posible.
        var catalogoA = await PrepararAceptacionAsync(primero, _coordinador, solicitudId);
        var catalogoB = await PrepararAceptacionAsync(segundo, _otroCoordinador, solicitudId);

        bool ganaA, ganaB;
        using (AmbitoTenantExplicito.Establecer(_empresa.Id))
        {
            ganaA = await catalogoA.GuardarDetectandoCarreraAsync();
            ganaB = await catalogoB.GuardarDetectandoCarreraAsync();
        }

        ganaA.Should().BeTrue();
        ganaB.Should().BeFalse("la versión de la solicitud y los índices únicos dejan pasar una sola aceptación");
        segundo.ChangeTracker.Entries().Should().BeEmpty("lo que perdió la carrera no puede colarse en el siguiente guardado");

        await AfirmarUnaSolaIncorporacionAsync(solicitudId);
    }

    [Fact]
    public async Task Un_rechazo_intercalado_con_una_aceptacion_deja_pasar_solo_uno()
    {
        var solicitudId = await SolicitarAsync();

        await using var acepta = ContextoRuntime(_coordinador, _operador.Id, "CoordinadorCae");
        await using var rechaza = ContextoRuntime(_otroCoordinador, _operador.Id, "CoordinadorCae");

        // Aquí ningún índice único separa a los dos: el rechazo no crea
        // cartera. Solo la Version de la solicitud impide que la aceptación
        // preparada antes del rechazo se guarde encima de él.
        var catalogoAcepta = await PrepararAceptacionAsync(acepta, _coordinador, solicitudId);

        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            var solicitud = (await new SolicitudIncorporacionCarteraRepository(rechaza).ObtenerPorIdAsync(solicitudId, _operador.Id))!;
            solicitud.Rechazar(_otroCoordinador, DateTime.UtcNow);
            (await Catalogo(rechaza).GuardarDetectandoCarreraAsync()).Should().BeTrue();
        }

        bool ganaAceptacion;
        using (AmbitoTenantExplicito.Establecer(_empresa.Id))
        {
            ganaAceptacion = await catalogoAcepta.GuardarDetectandoCarreraAsync();
        }

        ganaAceptacion.Should().BeFalse("la solicitud ya estaba rechazada cuando llegó la aceptación");

        await using var propietario = ContextoPropietario();
        (await propietario.AsignacionesCartera.CountAsync(c => c.UsuarioId == _gestor)).Should().Be(0,
            "la cartera iba en el mismo guardado que la aceptación perdida");
        (await propietario.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == _gestor)).Should().Be(0);
        (await propietario.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync(s => s.Id == solicitudId))
            .Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Rechazada);
    }

    [Fact]
    public async Task Dos_Coordinadores_CAE_que_aceptan_a_la_vez_dejan_una_sola_cartera()
    {
        var solicitudId = await SolicitarAsync();

        await using var primero = ContextoRuntime(_coordinador, _operador.Id, "CoordinadorCae");
        await using var segundo = ContextoRuntime(_otroCoordinador, _operador.Id, "CoordinadorCae");

        var resultados = await Task.WhenAll(
            Task.Run(() => Aceptar(primero).Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitudId), CancellationToken.None)),
            Task.Run(() => Aceptar(segundo).Handle(new AceptarSolicitudIncorporacionCarteraCommand(solicitudId), CancellationToken.None)));

        resultados.Count(r => r.EsExitoso).Should().Be(1);
        resultados.Single(r => r.EsFallido).Error.Codigo.Should().Be("SolicitudCartera.YaResuelta");

        await AfirmarUnaSolaIncorporacionAsync(solicitudId);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Guid> SolicitarAsync()
    {
        await using var contexto = ContextoRuntime(_gestor, _operador.Id, "GestorCae");
        var usuario = UsuarioDe(contexto);
        var resultado = await new SolicitarIncorporacionCarteraCommandHandler(
                usuario, Catalogo(contexto), new SolicitudIncorporacionCarteraRepository(contexto))
            .Handle(new SolicitarIncorporacionCarteraCommand(_empresa.Id, "Llevo sus centros"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        return resultado.Valor;
    }

    private async Task<CatalogoIncorporacionCartera> PrepararAceptacionAsync(
        CaeManagerDbContext contexto, Guid coordinador, Guid solicitudId)
    {
        var catalogo = Catalogo(contexto);
        SolicitudIncorporacionCartera solicitud;
        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            solicitud = (await new SolicitudIncorporacionCarteraRepository(contexto).ObtenerPorIdAsync(solicitudId, _operador.Id))!;
        }

        using (AmbitoTenantExplicito.Establecer(_empresa.Id))
        {
            var incorporacion = await catalogo.IncorporarAsync(solicitud);
            incorporacion.MotivoAnulacion.Should().BeNull();
            solicitud.Aceptar(coordinador, incorporacion.Cartera!, incorporacion.AsignacionOperadorDelegadoId, DateTime.UtcNow);
        }

        return catalogo;
    }

    private async Task AfirmarUnaSolaIncorporacionAsync(Guid solicitudId)
    {
        await using var propietario = ContextoPropietario();
        (await propietario.AsignacionesCartera.CountAsync(c => c.UsuarioId == _gestor)).Should().Be(1);
        (await propietario.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == _gestor)).Should().Be(1);

        var solicitud = await propietario.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
        (await propietario.AsignacionesCartera.AnyAsync(c => c.Id == solicitud.AsignacionCarteraId)).Should().BeTrue(
            "la solicitud apunta a la cartera que sobrevivió, no a la del perdedor");
    }

    private CatalogoIncorporacionCartera Catalogo(CaeManagerDbContext contexto) =>
        new(contexto, UsuarioDe(contexto));

    private CurrentUserServicePorAmbito UsuarioDe(CaeManagerDbContext contexto) =>
        _usuarios[contexto];

    private AceptarSolicitudIncorporacionCarteraCommandHandler Aceptar(CaeManagerDbContext contexto) =>
        new(UsuarioDe(contexto), Catalogo(contexto), new SolicitudIncorporacionCarteraRepository(contexto),
            new DirectorioSolicitanteActivo(), new NotificacionUsuarioRepository(contexto), contexto, contexto,
            NullLogger<AceptarSolicitudIncorporacionCarteraCommandHandler>.Instance);

    private RevocarIncorporacionCarteraCommandHandler Revocar(CaeManagerDbContext contexto) =>
        new(UsuarioDe(contexto), Catalogo(contexto), new SolicitudIncorporacionCarteraRepository(contexto),
            new NotificacionUsuarioRepository(contexto), contexto, contexto,
            NullLogger<RevocarIncorporacionCarteraCommandHandler>.Instance);

    /// <summary>Qué usuario lleva cada contexto de runtime, para construir sus handlers.</summary>
    private readonly Dictionary<CaeManagerDbContext, CurrentUserServicePorAmbito> _usuarios = new(ReferenceEqualityComparer.Instance);

    private CaeManagerDbContext ContextoRuntime(Guid usuarioId, Guid origen, string rolEnOrigen)
    {
        var usuario = new CurrentUserServicePorAmbito(usuarioId, origen, rolEnOrigen);
        var tenantActual = new TenantSegunAmbito(origen);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // Los mismos interceptores de escritura que producción
            // (ConfiguracionDeContexto): sin ConcurrenciaOptimistaInterceptor la
            // Version no se renueva y la carrera solo la frenan los índices únicos.
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(), usuario),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        var contexto = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        _usuarios.Add(contexto, usuario);
        return contexto;
    }

    private CaeManagerDbContext ContextoPropietario()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _operador.Id });
    }

    /// <summary>Como el <c>TenantActual</c> de la web: el ámbito explícito manda sobre el de la sesión.</summary>
    private sealed class TenantSegunAmbito(Guid tenantDeSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeSesion;
    }

    /// <summary>Como el <c>CurrentUserService</c> de la web: el rol de sesión solo dentro del origen.</summary>
    private sealed class CurrentUserServicePorAmbito(Guid usuarioId, Guid origen, string rolEnOrigen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolActualAsync() =>
            Task.FromResult(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito != origen ? null : rolEnOrigen);

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    /// <summary>La vigencia de la cuenta del solicitante se prueba en Application; aquí siempre está activa.</summary>
    private sealed class DirectorioSolicitanteActivo : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<bool> EsCuentaActivaConRolAsync(
            Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
