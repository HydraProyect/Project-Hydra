using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.CancelarVisitas;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Alcance de cartera al EDITAR, CANCELAR y REACTIVAR una Visita (uno a uno y en lote), con el mismo
/// criterio que <c>CrearVisitaCommand</c> desde #889: existir en el Tenant no basta; el Centro de
/// la Visita tiene que estar dentro del alcance de GESTIÓN de quien la toca (su Asignación de
/// Cartera, si es Gestor CAE). Fuera de alcance se responde igual que "no existe".
///
/// <para>
/// Los handlers corren sobre una conexión autenticada como <c>cae_app_runtime</c> (login, no
/// superusuario ni <c>SET ROLE</c>) con los interceptores de sellado y de sesión RLS, y con el
/// <see cref="AlcanceDatosService"/> real: la cartera sale de una <see cref="AsignacionCartera"/>
/// de verdad. RLS aísla Tenants, no cartera —las dos Visitas del Tenant son visibles para el rol
/// de runtime—, así que la barrera que estos tests ponen en rojo es la de Application, no la de
/// la base. La siembra y las comprobaciones usan una conexión propietaria aparte.
/// </para>
///
/// <para>
/// Escenario, Tenant propietario único (más un segundo Tenant solo para el control de RLS):
/// Cliente empresarial dentro (en la cartera del Gestor CAE) con un Centro y una Visita, y
/// Cliente empresarial fuera (sin cartera) con un Centro y una Visita. Cada Visita lleva ya un
/// Trabajador de su Centro.
/// </para>
/// </summary>
public class EditarCancelarVisitaAlcanceCarteraBajoRlsTests : IAsyncLifetime
{
    private static readonly DateOnly FechaOriginal = new(2026, 1, 1);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;

    private Guid _tenant;
    private Guid _clienteDentro;
    private Guid _visitaDentro;
    private Guid _visitaFuera;
    private Guid _trabajadorDentro;
    private Guid _trabajadorFuera;
    private Guid _visitaDeOtroTenant;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);

        var tenant = new Tenant("Tenant propietario de prueba");
        var otroTenant = new Tenant("Otro Tenant propietario");
        _propietario.Tenants.AddRange(tenant, otroTenant);
        await _propietario.SaveChangesAsync();
        _tenant = tenant.Id;

        using (AmbitoTenantExplicito.Establecer(_tenant))
        {
            var clienteDentro = Empresa.CrearComoCliente("Cliente empresarial dentro", "B10380186", false, null, null);
            var clienteFuera = Empresa.CrearComoCliente("Cliente empresarial fuera", "B10380194", false, null, null);
            var propia = new Empresa("Empresa propia", "B10380202");
            var subcontrataFuera = Empresa.CrearComoSubcontrata("Subcontrata fuera", null, NivelServicioSubcontrata.Gestionada.ToString());
            _propietario.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataFuera);

            var centroDentro = new CaeManager.Domain.Centros.Centro(clienteDentro.Id, propia.Id, "Centro dentro");
            var centroFuera = new CaeManager.Domain.Centros.Centro(clienteFuera.Id, subcontrataFuera.Id, "Centro fuera");
            _propietario.Centros.AddRange(centroDentro, centroFuera);

            var trabajadorDentro = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
            var trabajadorFuera = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", "33445566R");
            _propietario.Trabajadores.AddRange(trabajadorDentro, trabajadorFuera);

            var visitaDentro = new Visita(centroDentro.Id, FechaOriginal, FechaOriginal.AddDays(1), null);
            var visitaFuera = new Visita(centroFuera.Id, FechaOriginal, FechaOriginal.AddDays(1), null);
            _propietario.Visitas.AddRange(visitaDentro, visitaFuera);
            await _propietario.SaveChangesAsync();

            _propietario.VisitasTrabajadores.AddRange(
                new VisitaTrabajador(visitaDentro.Id, trabajadorDentro.Id),
                new VisitaTrabajador(visitaFuera.Id, trabajadorFuera.Id));

            var ahora = DateTime.UtcNow;
            var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
            _propietario.AsignacionesOperacion.Add(raiz);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raiz, _gestor, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora));
            await _propietario.SaveChangesAsync();

            (_clienteDentro, _visitaDentro, _visitaFuera) = (clienteDentro.Id, visitaDentro.Id, visitaFuera.Id);
            (_trabajadorDentro, _trabajadorFuera) = (trabajadorDentro.Id, trabajadorFuera.Id);
        }

        using (AmbitoTenantExplicito.Establecer(otroTenant.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial de otro Tenant", "B10380210", false, null, null);
            var propia = new Empresa("Empresa propia de otro Tenant", "B10380228");
            _propietario.Empresas.AddRange(cliente, propia);
            var centro = new CaeManager.Domain.Centros.Centro(cliente.Id, propia.Id, "Centro de otro Tenant");
            _propietario.Centros.Add(centro);
            var visita = new Visita(centro.Id, FechaOriginal, FechaOriginal.AddDays(1), null);
            _propietario.Visitas.Add(visita);
            await _propietario.SaveChangesAsync();
            _visitaDeOtroTenant = visita.Id;
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: los handlers corren de verdad como <c>cae_app_runtime</c> y bajo
    /// RLS (la Visita de otro Tenant no existe para esa conexión, ni saltándose los filtros de EF),
    /// y RLS NO decide la cartera: las dos Visitas del Tenant, dentro y fuera, son visibles. Sin
    /// esto último, un rojo en los negativos podría venir de la base y no de Application.
    /// </summary>
    [Fact]
    public async Task Los_handlers_corren_como_runtime_bajo_RLS_que_no_filtra_por_cartera()
    {
        await using var runtime = CrearContextoRuntime(new CurrentUserServiceFalso(_gestor, "GestorCae", tenantOrigenId: _tenant));

        (await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_runtime");
        (await runtime.Visitas.IgnoreQueryFilters().CountAsync(v => v.Id == _visitaDeOtroTenant)).Should().Be(0,
            "RLS oculta la Visita de otro Tenant");
        (await runtime.Visitas.CountAsync(v => v.Id == _visitaDentro || v.Id == _visitaFuera)).Should().Be(2,
            "dentro del Tenant, RLS no acota por cartera: esa barrera es la de Application");
    }

    // ── Editar ────────────────────────────────────────────────────────────

    /// <summary>
    /// Control positivo: la misma composición y el mismo Gestor CAE SÍ editan la Visita de un
    /// Centro de su cartera. Añade además un Trabajador fuera de su cartera: los Trabajadores
    /// siguen saliendo de la base general del Tenant, como al crear (#889).
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_edita_la_Visita_de_un_Centro_dentro_de_su_cartera()
    {
        var resultado = await EditarAsync(_gestor, "GestorCae", _visitaDentro, [_trabajadorDentro, _trabajadorFuera]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await FechaInicioAsync(_visitaDentro)).Should().Be(FechaOriginal.AddDays(7));
        (await TrabajadoresDeAsync(_visitaDentro)).Should().BeEquivalentTo([_trabajadorDentro, _trabajadorFuera]);
    }

    /// <summary>
    /// El caso que motivó el incremento: con el Id de una Visita fuera de cartera, EditarVisita
    /// cambiaba fechas y AÑADÍA Trabajadores a una Visita que el Gestor CAE ni siquiera veía.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_no_edita_ni_anade_Trabajadores_a_una_Visita_fuera_de_su_cartera()
    {
        var resultado = await EditarAsync(_gestor, "GestorCae", _visitaFuera, [_trabajadorFuera, _trabajadorDentro]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada",
            "fuera de cartera se responde igual que si no existiera, sin revelar qué hay fuera del alcance");
        (await FechaInicioAsync(_visitaFuera)).Should().Be(FechaOriginal);
        (await TrabajadoresDeAsync(_visitaFuera)).Should().BeEquivalentTo([_trabajadorFuera],
            "la Visita existe en el Tenant, pero su Centro no está en la Asignación de Cartera del Gestor CAE");
    }

    /// <summary>
    /// Un Gestor CAE sin ninguna Asignación de Cartera tiene alcance cero: lista vacía, no «sin
    /// restricción». Tampoco edita la Visita que para otro Gestor CAE sí está dentro.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_edita_ninguna_Visita()
    {
        var resultado = await EditarAsync(Guid.NewGuid(), "GestorCae", _visitaDentro, [_trabajadorDentro]);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        (await FechaInicioAsync(_visitaDentro)).Should().Be(FechaOriginal, "sin cartera el alcance es [] (falla cerrado)");
    }

    /// <summary>
    /// El alcance es el de GESTIÓN, no el de lectura: un usuario de portal (rol Cliente) ve los
    /// Centros de su propio Cliente empresarial, pero no opera sobre ellos. Se llama al handler
    /// directamente, sin el behavior de rol, para que esta barrera se pruebe por sí sola.
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_no_pasa_el_alcance_de_gestion_ni_sobre_su_propio_Centro()
    {
        var usuarioPortal = Guid.NewGuid();
        using (AmbitoTenantExplicito.Establecer(_tenant))
        {
            _propietario.Users.Add(new ApplicationUser
            {
                Id = usuarioPortal,
                UserName = $"portal-{usuarioPortal:N}@ejemplo.test",
                Email = $"portal-{usuarioPortal:N}@ejemplo.test",
                ClienteId = _clienteDentro,
                TenantId = _tenant
            });
            await _propietario.SaveChangesAsync();
        }

        var edicion = await EditarAsync(usuarioPortal, "Cliente", _visitaDentro, [_trabajadorDentro]);
        var cancelacion = await CancelarAsync(usuarioPortal, "Cliente", _visitaDentro);

        edicion.Error.Codigo.Should().Be("Visita.NoEncontrada");
        cancelacion.Error.Codigo.Should().Be("Visita.NoEncontrada");
        (await FechaInicioAsync(_visitaDentro)).Should().Be(FechaOriginal);
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeFalse();
    }

    // ── Cancelar (FS-11: estado reversible, ya no borrado lógico) ────────

    [Fact]
    public async Task Gestor_CAE_cancela_la_Visita_de_un_Centro_dentro_de_su_cartera_sin_borrarla()
    {
        var resultado = await CancelarAsync(_gestor, "GestorCae", _visitaDentro);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeTrue();
        (await EstaEliminadaAsync(_visitaDentro)).Should().BeFalse("cancelar ya no es un borrado lógico");
    }

    [Fact]
    public async Task Gestor_CAE_no_cancela_una_Visita_fuera_de_su_cartera()
    {
        var resultado = await CancelarAsync(_gestor, "GestorCae", _visitaFuera);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeFalse();
    }

    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_cancela_ninguna_Visita()
    {
        var resultado = await CancelarAsync(Guid.NewGuid(), "GestorCae", _visitaDentro);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeFalse();
    }

    /// <summary>Roles de Propiedad: sin restricción de cartera, igual que antes de este cambio.</summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Un_rol_de_Propiedad_edita_y_cancela_la_Visita_de_cualquier_Centro_del_Tenant(string rol)
    {
        var edicion = await EditarAsync(Guid.NewGuid(), rol, _visitaFuera, [_trabajadorFuera]);
        var cancelacion = await CancelarAsync(Guid.NewGuid(), rol, _visitaFuera);

        edicion.EsExitoso.Should().BeTrue(edicion.EsFallido ? edicion.Error.Codigo : "");
        cancelacion.EsExitoso.Should().BeTrue(cancelacion.EsFallido ? cancelacion.Error.Codigo : "");
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_Visita_cancelada_no_se_edita()
    {
        (await CancelarAsync(_gestor, "GestorCae", _visitaDentro)).EsExitoso.Should().BeTrue("control positivo");

        var edicion = await EditarAsync(_gestor, "GestorCae", _visitaDentro, [_trabajadorDentro]);

        edicion.Error.Codigo.Should().Be("Visita.Cancelada");
        (await FechaInicioAsync(_visitaDentro)).Should().Be(FechaOriginal);
    }

    // ── Cancelar en lote ──────────────────────────────────────────────────

    /// <summary>
    /// En lote, la Visita fuera de cartera cuenta como una que ya no existía (éxito parcial) y la
    /// de dentro se cancela: la barrera es por Visita, no todo o nada.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_en_lote_solo_cancela_las_Visitas_de_su_cartera()
    {
        var resultado = await CancelarLoteAsync(_gestor, "GestorCae", [_visitaDentro, _visitaFuera]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        resultado.Valor.Canceladas.Should().Be(1);
        resultado.Valor.IdsCanceladas.Should().Equal([_visitaDentro]);
        resultado.Valor.Errores.Should().ContainSingle();
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeTrue();
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeFalse();
    }

    [Fact]
    public async Task Gestor_CAE_sin_cartera_en_lote_no_cancela_ninguna_Visita()
    {
        var resultado = await CancelarLoteAsync(Guid.NewGuid(), "GestorCae", [_visitaDentro, _visitaFuera]);

        resultado.Valor.Canceladas.Should().Be(0);
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeFalse();
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeFalse();
    }

    // ── Reactivar: mismos roles y alcance que cancelar (decisión de la coordinadora, 2026-09-26) ──

    [Fact]
    public async Task Gestor_CAE_reactiva_una_Visita_cancelada_de_su_cartera()
    {
        (await CancelarAsync(_gestor, "GestorCae", _visitaDentro)).EsExitoso.Should().BeTrue("control positivo");

        var resultado = await ReactivarAsync(_gestor, "GestorCae", _visitaDentro);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await EstaCanceladaAsync(_visitaDentro)).Should().BeFalse();
    }

    [Fact]
    public async Task Gestor_CAE_no_reactiva_una_Visita_cancelada_fuera_de_su_cartera()
    {
        (await CancelarAsync(Guid.NewGuid(), "Administrador", _visitaFuera)).EsExitoso.Should().BeTrue("control positivo");

        var resultado = await ReactivarAsync(_gestor, "GestorCae", _visitaFuera);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeTrue();
    }

    /// <summary>Simetría: cada rol de Propiedad que cancela fuera de cartera también reactiva.</summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Un_rol_de_Propiedad_que_cancela_una_Visita_la_reactiva(string rol)
    {
        (await CancelarAsync(Guid.NewGuid(), rol, _visitaFuera)).EsExitoso.Should().BeTrue("control positivo: puede cancelar");

        var resultado = await ReactivarAsync(Guid.NewGuid(), rol, _visitaFuera);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await EstaCanceladaAsync(_visitaFuera)).Should().BeFalse();
    }

    // ── Composición ───────────────────────────────────────────────────────

    private async Task<Result> EditarAsync(Guid usuarioId, string rol, Guid visitaId, IReadOnlyList<Guid> trabajadorIds)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenant);
        await using var runtime = CrearContextoRuntime(usuario);
        var handler = new EditarVisitaCommandHandler(
            new VisitaRepository(runtime), new VisitaTrabajadorRepository(runtime), runtime,
            new EvaluadorExpedienteNulo(), runtime, NullLogger<EditarVisitaCommandHandler>.Instance,
            CrearAlcance(runtime, usuario));

        return await handler.Handle(
            new EditarVisitaCommand(visitaId, FechaOriginal.AddDays(7), FechaOriginal.AddDays(8), trabajadorIds, "editada"),
            CancellationToken.None);
    }

    private async Task<Result> CancelarAsync(Guid usuarioId, string rol, Guid visitaId)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenant);
        await using var runtime = CrearContextoRuntime(usuario);
        var handler = new CancelarVisitaCommandHandler(new VisitaRepository(runtime), runtime, CrearAlcance(runtime, usuario));

        return await handler.Handle(new CancelarVisitaCommand(visitaId), CancellationToken.None);
    }

    private async Task<Result<ResultadoCancelacionLoteDto>> CancelarLoteAsync(Guid usuarioId, string rol, IReadOnlyList<Guid> visitaIds)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenant);
        await using var runtime = CrearContextoRuntime(usuario);
        var handler = new CancelarVisitasCommandHandler(new VisitaRepository(runtime), runtime, CrearAlcance(runtime, usuario));

        return await handler.Handle(new CancelarVisitasCommand(visitaIds), CancellationToken.None);
    }

    private async Task<Result> ReactivarAsync(Guid usuarioId, string rol, Guid visitaId)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenant);
        await using var runtime = CrearContextoRuntime(usuario);
        var handler = new ReactivarVisitaCommandHandler(
            new VisitaRepository(runtime), runtime, CrearAlcance(runtime, usuario), new EvaluadorExpedienteNulo(),
            NullLogger<ReactivarVisitaCommandHandler>.Instance);

        return await handler.Handle(new ReactivarVisitaCommand(visitaId), CancellationToken.None);
    }

    private AlcanceDatosService CrearAlcance(CaeManagerDbContext runtime, CurrentUserServiceFalso usuario) =>
        new(runtime, usuario, new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

    /// <summary>
    /// Conexión de LOGIN como <c>cae_app_runtime</c> con los interceptores de sellado y de sesión
    /// RLS, uno por operación, como el ámbito scoped de una petición real.
    /// </summary>
    private CaeManagerDbContext CrearContextoRuntime(CurrentUserServiceFalso usuario)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    // ── Lecturas de comprobación (conexión propietaria, sin filtros) ─────

    private async Task<DateOnly> FechaInicioAsync(Guid visitaId) =>
        await _propietario.Visitas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Id == visitaId).Select(v => v.FechaInicio).SingleAsync();

    private async Task<bool> EstaEliminadaAsync(Guid visitaId) =>
        await _propietario.Visitas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Id == visitaId).Select(v => v.EstaEliminado).SingleAsync();

    private async Task<bool> EstaCanceladaAsync(Guid visitaId) =>
        await _propietario.Visitas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Id == visitaId).Select(v => v.EstaCancelada).SingleAsync();

    private async Task<List<Guid>> TrabajadoresDeAsync(Guid visitaId) =>
        await _propietario.VisitasTrabajadores.IgnoreQueryFilters().AsNoTracking()
            .Where(vt => vt.VisitaId == visitaId).Select(vt => vt.TrabajadorId).ToListAsync();

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class EvaluadorExpedienteNulo : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
