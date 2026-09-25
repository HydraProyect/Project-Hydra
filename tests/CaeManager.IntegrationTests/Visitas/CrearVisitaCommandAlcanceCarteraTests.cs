using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Alcance de cartera al CREAR una Visita. Existir en el Tenant no basta: un Gestor CAE solo
/// crea Visitas sobre Centros dentro de su Asignación de Cartera, con el mismo criterio con el
/// que las lecturas de Visita deciden si se ve (el Centro). Los Trabajadores siguen saliendo de
/// la base general del Tenant (<c>AlcanceSelectorTrabajadores.BaseGeneralDelTenant</c>): acotarlos
/// es una decisión de producto aún no tomada, y el último caso fija que este incremento no la toma.
///
/// <see cref="AlcanceDatosService"/> real sobre PostgreSQL, sin fakes de alcance: la cartera sale
/// de una <see cref="AsignacionCartera"/> de verdad.
///
/// Escenario, Tenant propietario único:
/// <list type="bullet">
/// <item>Cliente empresarial dentro (en la cartera del Gestor CAE) con un Centro donde trabaja la
/// Empresa propia, y un Trabajador de la Empresa propia asignado a ese Centro.</item>
/// <item>Cliente empresarial fuera (sin cartera) con un Centro donde trabaja una Subcontrata sin
/// Relación Empresarial con el dentro, y un Trabajador de esa Subcontrata asignado a él.</item>
/// </list>
/// </summary>
public class CrearVisitaCommandAlcanceCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _clienteDentro;
    private Guid _centroDentro;
    private Guid _trabajadorDentro;
    private Guid _centroFuera;
    private Guid _trabajadorFuera;
    private Guid _gestor;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var clienteDentro = Empresa.CrearComoCliente("Cliente empresarial dentro", "B10380186", false, null, null);
        var clienteFuera = Empresa.CrearComoCliente("Cliente empresarial fuera", "B10380194", false, null, null);
        var propia = new Empresa("Empresa propia", "B10380202");
        var subcontrataFuera = Empresa.CrearComoSubcontrata("Subcontrata fuera", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataFuera);

        var centroDentro = new Centro(clienteDentro.Id, propia.Id, "Centro dentro");
        var centroFuera = new Centro(clienteFuera.Id, subcontrataFuera.Id, "Centro fuera");
        contexto.Centros.AddRange(centroDentro, centroFuera);

        var trabajadorDentro = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        var trabajadorFuera = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", "33445566R");
        contexto.Trabajadores.AddRange(trabajadorDentro, trabajadorFuera);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajadorDentro.Id, centroDentro.Id, new DateOnly(2026, 1, 1)));
        contexto.Asignaciones.Add(new Asignacion(trabajadorFuera.Id, centroFuera.Id, new DateOnly(2026, 1, 1)));

        _gestor = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, _gestor, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora));
        await contexto.SaveChangesAsync();

        (_clienteDentro, _centroDentro, _trabajadorDentro) = (clienteDentro.Id, centroDentro.Id, trabajadorDentro.Id);
        (_centroFuera, _trabajadorFuera) = (centroFuera.Id, trabajadorFuera.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Control positivo: la misma composición y el mismo Gestor CAE SÍ crean la Visita cuando el
    /// Centro está en su cartera. Sin él, los negativos podrían estar en verde por un escenario
    /// que nunca llega a la comprobación de alcance.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_crea_la_Visita_de_un_Centro_dentro_de_su_cartera()
    {
        var resultado = await CrearAsync(_gestor, "GestorCae", _centroDentro, _trabajadorDentro);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await ContarVisitasAsync(_centroDentro)).Should().Be(1);
    }

    [Fact]
    public async Task Gestor_CAE_no_crea_la_Visita_de_un_Centro_fuera_de_su_cartera()
    {
        var resultado = await CrearAsync(_gestor, "GestorCae", _centroFuera, _trabajadorFuera);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.CentroNoEncontrado",
            "fuera de cartera se responde igual que si no existiera, sin revelar qué hay fuera del alcance");
        (await ContarVisitasAsync(_centroFuera)).Should().Be(0,
            "el Centro existe en el Tenant, pero no está en la Asignación de Cartera del Gestor CAE");
    }

    /// <summary>
    /// Roles de Propiedad: sin restricción de cartera, igual que antes de este cambio.
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Un_rol_de_Propiedad_crea_la_Visita_de_cualquier_Centro_del_Tenant(string rol)
    {
        var resultado = await CrearAsync(Guid.NewGuid(), rol, _centroFuera, _trabajadorFuera);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await ContarVisitasAsync(_centroFuera)).Should().Be(1);
    }

    /// <summary>
    /// Un Gestor CAE sin ninguna Asignación de Cartera (p. ej. el de un Operador CAE delegado
    /// al que aún no se le ha repartido cartera) tiene alcance cero: lista vacía, no «sin
    /// restricción». Tampoco crea sobre el Centro que para otro Gestor CAE sí está dentro.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_crea_ninguna_Visita()
    {
        var resultado = await CrearAsync(Guid.NewGuid(), "GestorCae", _centroDentro, _trabajadorDentro);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.CentroNoEncontrado");
        (await ContarVisitasAsync(_centroDentro)).Should().Be(0, "sin cartera el alcance es [] (falla cerrado)");
    }

    /// <summary>
    /// El alcance es el de GESTIÓN, no el de lectura: un usuario de portal (rol Cliente) ve los
    /// Centros de su propio Cliente empresarial, pero no opera sobre ellos. Se llama al handler
    /// directamente, sin el behavior de rol, para que esta barrera se pruebe por sí sola: con
    /// <c>CentroVisibleAsync</c> en lugar de <c>CentroParaGestionVisibleAsync</c> este caso
    /// crearía la Visita.
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_no_pasa_el_alcance_de_gestion_ni_sobre_su_propio_Centro()
    {
        var usuarioPortal = Guid.NewGuid();
        await using (var contexto = CrearContexto())
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioPortal,
                UserName = $"portal-{usuarioPortal:N}@ejemplo.test",
                Email = $"portal-{usuarioPortal:N}@ejemplo.test",
                ClienteId = _clienteDentro,
                TenantId = _tenant
            });
            await contexto.SaveChangesAsync();
        }

        var resultado = await CrearAsync(usuarioPortal, "Cliente", _centroDentro, _trabajadorDentro);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.CentroNoEncontrado");
        (await ContarVisitasAsync(_centroDentro)).Should().Be(0);
    }

    /// <summary>
    /// Fija lo que este incremento NO cambia: los Trabajadores de la Visita no se acotan a la
    /// cartera. Un Trabajador de una Subcontrata que el Gestor CAE todavía no ve puede acudir a
    /// un Centro de su cartera, que es por lo que el formulario ofrece la base general del
    /// Tenant. Si producto decide acotarlos, este test es el que tiene que cambiar.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_lleva_a_su_Centro_un_Trabajador_fuera_de_su_cartera()
    {
        var resultado = await CrearAsync(_gestor, "GestorCae", _centroDentro, _trabajadorFuera);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await ContarVisitasAsync(_centroDentro)).Should().Be(1);
    }

    private async Task<Result<Guid>> CrearAsync(Guid usuarioId, string rol, Guid centroId, Guid trabajadorId)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenant);
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        await using var contexto = CrearContexto();

        var handler = new CrearVisitaCommandHandler(
            new VisitaRepository(contexto), new VisitaTrabajadorRepository(contexto),
            contexto, contexto,
            new SugerenciaVisitaCorreoRepository(contexto), contexto,
            new PaqueteDocumentalNulo(), new EvaluadorExpedienteNulo(), usuario, new PublicadorNulo(), contexto,
            NullLogger<CrearVisitaCommandHandler>.Instance,
            new AlcanceDatosService(contexto, usuario, tenantActual, new SesionPrivilegiadaAusente()));

        var dia = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        return await handler.Handle(new CrearVisitaCommand(centroId, dia, dia, [trabajadorId], Notas: null), CancellationToken.None);
    }

    private async Task<int> ContarVisitasAsync(Guid centroId)
    {
        await using var contexto = CrearContexto();
        return await contexto.Visitas.CountAsync(v => v.CentroId == centroId);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class PaqueteDocumentalNulo : IPaqueteDocumentalVisitaService
    {
        public Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PaqueteDocumentalZip?> ConstruirAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaqueteDocumentalZip?>(null);
    }

    private sealed class EvaluadorExpedienteNulo : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PublicadorNulo : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
