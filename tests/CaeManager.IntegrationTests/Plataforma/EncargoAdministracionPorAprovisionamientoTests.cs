using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// El registro y la retirada del Encargo de administración por la vía de
/// plataforma (decisión D-8, 2026-10-08), componiendo el flujo completo como
/// <see cref="AprovisionamientoDeExtremoAExtremoTests"/>: <see cref="IMediator"/>
/// real, <see cref="ISesionPrivilegiadaActual"/> real, la elevación real y el
/// <see cref="TenantRlsConnectionInterceptor"/> real contra PostgreSQL.
///
/// <para>
/// Lo que solo esta composición prueba: que dentro de una Sesión Privilegiada
/// la conexión lee como <c>cae_app_soporte</c> y que el comando, por ser
/// <see cref="IComandoDeAprovisionamiento"/>, escribe como
/// <c>cae_app_aprovisionamiento</c>, con los privilegios que la migración del
/// encargo le concedió (insertar, retirar y leer la operación a la que se liga).
/// Con dobles, un privilegio que faltara no se vería.
/// </para>
/// </summary>
public class EncargoAdministracionPorAprovisionamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _soporte = Guid.NewGuid();
    private readonly Tenant _plataforma = new("Organización de plataforma de prueba");
    private readonly Tenant _operador = new("Operador CAE externo de prueba");
    private readonly Tenant _propietario = new("Tenant propietario de prueba");

    private Guid _operacionId;
    private Guid _sesionDeAprovisionamiento;
    private Guid _sesionDeLectura;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using var contexto = CrearContexto(_propietario.Id, new SinSeleccion());

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_plataforma, _operador, _propietario);
        var operacion = AsignacionOperacion.Externa(
            _propietario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);

        var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
            _soporte, CapacidadPrivilegio.Aprovisionamiento, [_propietario.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var lectura = ConcesionPrivilegio.SobreTenants(
            _soporte, CapacidadPrivilegio.SoporteLectura, [_propietario.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        contexto.ConcesionesPrivilegio.AddRange(aprovisionamiento, lectura);

        var sesionDeAprovisionamiento = SesionPrivilegiada.Abrir(
            aprovisionamiento, _propietario.Id, "Registro del encargo de administración", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        var sesionDeLectura = SesionPrivilegiada.Abrir(
            lectura, _propietario.Id, "Consulta de soporte", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        contexto.SesionesPrivilegiadas.AddRange(sesionDeAprovisionamiento, sesionDeLectura);

        await contexto.SaveChangesAsync();
        _operacionId = operacion.Id;
        _sesionDeAprovisionamiento = sesionDeAprovisionamiento.Id;
        _sesionDeLectura = sesionDeLectura.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_sesion_de_Aprovisionamiento_registra_lee_y_retira_el_encargo_por_el_pipeline_completo()
    {
        // Sin esto el test no distinguiría la elevación: si el rol de lectura de la sesión pudiera
        // insertar, el registro saldría bien aunque el comando no llegara a elevarse.
        (await TienePrivilegioAsync("cae_app_soporte", "INSERT")).Should().BeFalse(
            "control: la sesión lee como cae_app_soporte, que no escribe encargos");

        Guid encargoId;
        await using (var proveedor = ConstruirProveedor(_sesionDeAprovisionamiento))
        {
            var registro = await proveedor.GetRequiredService<IMediator>().Send(
                new RegistrarEncargoAdministracionCommand(_operacionId, "  Cláusula 7.ª del contrato de servicio  ", null));
            registro.EsExitoso.Should().BeTrue(registro.EsFallido ? $"código: {registro.Error.Codigo}" : string.Empty);
            encargoId = registro.Valor;
        }

        await using (var proveedor = ConstruirProveedor(_sesionDeAprovisionamiento))
        {
            var lista = await proveedor.GetRequiredService<IMediator>().Send(new ObtenerEncargosAdministracionQuery());
            var fila = lista.Should().ContainSingle().Subject;
            fila.Id.Should().Be(encargoId);
            fila.OperadorNombre.Should().Be(_operador.Nombre);
            fila.ClausulaContrato.Should().Be("Cláusula 7.ª del contrato de servicio");
            fila.Origen.Should().Be(OrigenEncargoAdministracion.AprovisionamientoDePlataforma);
            fila.RegistradoPorUsuarioId.Should().Be(_soporte, "queda el Actor real de la plataforma, no un usuario del Tenant");
            fila.Estado.Should().Be(EstadoEncargoAdministracion.Vigente);
        }

        await using (var proveedor = ConstruirProveedor(_sesionDeAprovisionamiento))
        {
            var retirada = await proveedor.GetRequiredService<IMediator>().Send(new RetirarEncargoAdministracionCommand(encargoId));
            retirada.EsExitoso.Should().BeTrue(retirada.EsFallido ? $"código: {retirada.Error.Codigo}" : string.Empty);
        }

        await using var comprobacion = CrearContexto(_propietario.Id, new SinSeleccion());
        var encargo = await comprobacion.EncargosAdministracion.SingleAsync(e => e.Id == encargoId);
        encargo.RetiradoPorUsuarioId.Should().Be(_soporte);
        encargo.RetiradoEnUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Una_sesion_de_soporte_de_solo_lectura_no_registra_el_encargo()
    {
        await using var proveedor = ConstruirProveedor(_sesionDeLectura);

        var registro = await proveedor.GetRequiredService<IMediator>().Send(
            new RegistrarEncargoAdministracionCommand(_operacionId, "Cláusula 7.ª del contrato de servicio", null));

        registro.EsFallido.Should().BeTrue("la capacidad de la sesión no es Aprovisionamiento");

        await using var comprobacion = CrearContexto(_propietario.Id, new SinSeleccion());
        (await comprobacion.EncargosAdministracion.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Corrección C4 (2026-10-09), con el otro rol que escribe encargos. El positivo de los dos
    /// disparadores bajo <c>cae_app_aprovisionamiento</c> es el test del pipeline completo de
    /// arriba (registra y retira); aquí va lo que tienen que rechazar, con el rol adoptado a mano
    /// porque ningún comando de producción intenta estas escrituras.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_de_aprovisionamiento_la_retirada_no_se_deshace_y_el_Operador_CAE_tiene_que_ser_el_de_la_operacion()
    {
        var ahora = DateTime.UtcNow;
        Guid retiradoId;
        await using (var siembra = CrearContexto(_propietario.Id, new SinSeleccion()))
        {
            var operacion = await siembra.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionId);
            var retirado = EncargoAdministracion.Registrar(
                operacion, "Cláusula 7.ª del contrato de servicio", EncargoAdministracion.VersionTextoVigente,
                OrigenEncargoAdministracion.AprovisionamientoDePlataforma, _soporte, ahora.AddHours(-12), vigenciaHasta: null);
            retirado.Retirar(_soporte, ahora.AddHours(-6));
            siembra.EncargosAdministracion.Add(retirado);
            await siembra.SaveChangesAsync();
            retiradoId = retirado.Id;
        }

        // El rol puede actualizar la retirada: si saliera 42501 no se estaría midiendo el disparador.
        (await TienePrivilegioDeColumnaAsync("cae_app_aprovisionamiento", "RetiradoEnUtc", "UPDATE")).Should().BeTrue();

        (await SqlStateComoAprovisionamientoAsync(
                $"""UPDATE public."EncargosAdministracion" SET "RetiradoEnUtc" = NULL, "RetiradoPorUsuarioId" = NULL WHERE "Id" = '{retiradoId}';"""))
            .Should().Be((PostgresErrorCodes.CheckViolation, -1), "una retirada no se deshace");
        (await SqlStateComoAprovisionamientoAsync(
                $"""UPDATE public."EncargosAdministracion" SET "RetiradoPorUsuarioId" = '{Guid.NewGuid()}' WHERE "Id" = '{retiradoId}';"""))
            .Should().Be((PostgresErrorCodes.CheckViolation, -1), "el autor de la retirada no se reescribe");

        // Las columnas salen de la migración que crea la tabla, no de memoria.
        string Insertar(Guid operadorTenantId) =>
            $"""
            INSERT INTO public."EncargosAdministracion"
                ("Id", "PropietarioTenantId", "OperadorTenantId", "AsignacionOperacionId", "ClausulaContrato",
                 "VersionTexto", "Origen", "RegistradoPorUsuarioId", "RegistradoEnUtc", "VigenciaDesde", "Version")
            VALUES
                ('{Guid.NewGuid()}', '{_propietario.Id}', '{operadorTenantId}', '{_operacionId}', 'Cláusula de prueba',
                 '{EncargoAdministracion.VersionTextoVigente}', '{OrigenEncargoAdministracion.AprovisionamientoDePlataforma}',
                 '{_soporte}', now(), now(), '{Guid.NewGuid()}');
            """;

        (await SqlStateComoAprovisionamientoAsync(Insertar(_plataforma.Id)))
            .Should().Be((PostgresErrorCodes.CheckViolation, -1),
                "el encargo nombra al Operador CAE de su Asignación de Operación, no a otro Tenant");
        // Control positivo: la misma sentencia con el Operador CAE de la operación inserta una fila.
        // Sin él, el rojo anterior podría ser de un disparador que no ve la operación bajo RLS.
        (await SqlStateComoAprovisionamientoAsync(Insertar(_operador.Id)))
            .Should().Be(((string?)null, 1));

        await using var comprobacion = CrearContexto(_propietario.Id, new SinSeleccion());
        var fila = await comprobacion.EncargosAdministracion.AsNoTracking().SingleAsync();
        fila.Id.Should().Be(retiradoId, "todas las escrituras de arriba se revierten: solo queda la fila sembrada");
        fila.RetiradoPorUsuarioId.Should().Be(_soporte);
        fila.RetiradoEnUtc.Should().NotBeNull();
    }

    /// <summary>
    /// Ejecuta <paramref name="sql"/> como <c>cae_app_aprovisionamiento</c> con el Tenant propietario
    /// como Tenant activo, dentro de una transacción que siempre se revierte. Devuelve el SQLSTATE
    /// del rechazo (y -1 filas) o, si entró, <c>null</c> y las filas afectadas.
    /// </summary>
    private async Task<(string? SqlState, int Filas)> SqlStateComoAprovisionamientoAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        await using (var preparar = conexion.CreateCommand())
        {
            preparar.Transaction = transaccion;
            preparar.CommandText =
                "SELECT set_config('app.tenant_id', @tenant, true); SET LOCAL ROLE cae_app_aprovisionamiento;";
            preparar.Parameters.AddWithValue("tenant", _propietario.Id.ToString());
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var comprobarRol = conexion.CreateCommand())
        {
            comprobarRol.Transaction = transaccion;
            comprobarRol.CommandText = "SELECT current_user::text;";
            ((string)(await comprobarRol.ExecuteScalarAsync())!).Should().Be("cae_app_aprovisionamiento");
        }

        try
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = sql;
            return (null, await comando.ExecuteNonQueryAsync());
        }
        catch (PostgresException ex)
        {
            return (ex.SqlState, -1);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private async Task<bool> TienePrivilegioDeColumnaAsync(string rol, string columna, string privilegio)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_column_privilege(@rol, 'public.\"EncargosAdministracion\"', @columna, @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("columna", columna);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<bool> TienePrivilegioAsync(string rol, string privilegio)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege(@rol, 'public.\"EncargosAdministracion\"', @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private ServiceProvider ConstruirProveedor(Guid sesionSeleccionada)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        // El Actor de Plataforma es de la organización de plataforma: ni del Tenant propietario ni del Operador CAE.
        var currentUser = new CurrentUserServiceFalso(_soporte, rol: null, tenantOrigenId: _plataforma.Id);
        var seleccion = new SeleccionConSesion(_propietario.Id, sesionSeleccionada);
        var dbContext = CrearContexto(_propietario.Id, seleccion, currentUser);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(currentUser);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        servicios.AddSingleton(dbContext);
        servicios.AddSingleton(TimeProvider.System);

        servicios.AddSingleton<IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ISesionPrivilegiadaActual>(sp => new SesionPrivilegiadaActual(
            sp.GetRequiredService<IPlataformaQueryContext>(), seleccion, currentUser));
        servicios.AddSingleton<IElevacionEscrituraPrivilegiada>(sp =>
            new ElevacionEscrituraPrivilegiada(sp.GetRequiredService<CaeManagerDbContext>()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargosAdministracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargoAdministracionRepository, EncargoAdministracionRepository>();

        servicios.AddSingleton<IActorAuditoria>(new ActorFijo(_soporte));
        // Con una Sesión Privilegiada abierta, la vía del Administrador propio ni se consulta.
        servicios.AddSingleton<IAdministradorDelTenantPropietario>(new NadieEsAdministradorPropio());
        servicios.AddSingleton<AutoridadSobreElEncargo>();

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado seleccion, ICurrentUserService? currentUserService = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion,
                    currentUserService ?? new CurrentUserServiceFalso(_soporte, rol: null, tenantOrigenId: tenantId),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SeleccionConSesion(Guid tenantSeleccionado, Guid sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }

    private sealed class ActorFijo(Guid actorReal) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(actorReal));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(actorReal);
    }

    private sealed class NadieEsAdministradorPropio : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
