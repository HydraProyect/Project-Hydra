using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Una revocación hecha desde FUERA del circuito del afectado —un Coordinador CAE cierra la
/// Asignación de Cartera de un Gestor CAE, o la Asignación llega a su fin de vigencia sin que nadie
/// escriba— no pasa por ningún Command de ese circuito, así que la invalidación tras cada Command
/// no la ve. La cota es la caducidad de la memoización de <see cref="AlcanceDatosService"/>
/// (<see cref="CaducidadAlcanceOptions"/>): una misma instancia (= un circuito de Blazor Server)
/// deja de servir la cartera revocada en cuanto pasa esa ventana, sin esperar a que el circuito
/// muera. El reloj de la memoización es manual; la vigencia de la Asignación la lee la base con la
/// hora real.
/// </summary>
public class RevocacionCarteraEnCircuitoVivoTests : IAsyncLifetime
{
    private static readonly TimeSpan Caducidad = TimeSpan.FromSeconds(60);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private sealed class RelojManual : TimeProvider
    {
        // La caducidad se mide con el reloj monotónico (GetTimestamp), no con el de pared.
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Avanzar(TimeSpan intervalo) => _ticks += intervalo.Ticks;
    }

    /// <summary>Cliente empresarial y Empresa propia; la propia es visible por cartera (D-8).</summary>
    private async Task<(Guid Cliente, Guid Propia)> SembrarTenantAsync(Guid tenant, string cifCliente, string cifPropia)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente("Cliente empresarial", cifCliente, false, null, null);
        var propia = new Empresa("Empresa propia", cifPropia);
        contexto.Empresas.AddRange(cliente, propia);
        await contexto.SaveChangesAsync();
        return (cliente.Id, propia.Id);
    }

    private async Task<Guid> OtorgarCarteraAsync(Guid tenant, Guid usuarioId, Guid clienteId, DateTime? vigenciaHasta = null)
    {
        await using var contexto = CrearContexto(tenant);
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        var cartera = AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, vigenciaHasta, ahora);
        contexto.AsignacionesCartera.Add(cartera);
        await contexto.SaveChangesAsync();
        return cartera.Id;
    }

    /// <summary>La revocación la hace otro contexto: otro circuito, otro usuario (un Coordinador CAE).</summary>
    private async Task RevocarDesdeOtroCircuitoAsync(Guid tenant, Guid carteraId)
    {
        await using var contexto = CrearContexto(tenant);
        var cartera = await contexto.AsignacionesCartera.SingleAsync(a => a.Id == carteraId);
        cartera.Cerrar(MotivoCierreAsignacion.Revocada, DateTime.UtcNow);
        await contexto.SaveChangesAsync();
    }

    [Fact]
    public async Task La_cartera_revocada_desde_otro_circuito_deja_de_verse_al_caducar_la_memoizacion()
    {
        var (cliente, propia) = await SembrarTenantAsync(_tenant, "B10380186", "B10380194");
        var gestor = Guid.NewGuid();
        var cartera = await OtorgarCarteraAsync(_tenant, gestor, cliente);
        var reloj = new RelojManual();

        await using var contextoCircuito = CrearContexto(_tenant);
        var ambitoTenant = new TenantActualAmbiental { TenantId = _tenant };
        var circuito = CrearServicio(contextoCircuito, gestor, ambitoTenant, reloj);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal(cliente);
        (await circuito.ObtenerEmpresaIdsVisiblesAsync()).Should().Contain(propia);

        await RevocarDesdeOtroCircuitoAsync(_tenant, cartera);

        // Control positivo: la revocación es observable (una instancia nueva ya no ve nada)...
        await using (var contextoNuevo = CrearContexto(_tenant))
        {
            var nuevo = CrearServicio(contextoNuevo, gestor, new TenantActualAmbiental { TenantId = _tenant }, reloj);
            (await nuevo.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
            (await nuevo.ObtenerEmpresaIdsVisiblesAsync()).Should().BeEmpty();
        }

        // ...y dentro de la ventana la instancia viva sigue memoizando: lo que la refresca es la
        // caducidad, no otra cosa (sin esta comprobación el test pasaría también sin memoización).
        reloj.Avanzar(Caducidad - TimeSpan.FromSeconds(1));
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([cliente],
            "dentro de la cota declarada la memoización se conserva por rendimiento");

        reloj.Avanzar(TimeSpan.FromSeconds(1));
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty(
            "pasada la caducidad, el mismo circuito deja de servir la cartera revocada");
        (await circuito.ObtenerEmpresaIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty(
            "la caducidad descarta los siete alcances del Tenant a la vez, no solo el que se pidió primero");
    }

    [Fact]
    public async Task La_cartera_que_vence_sin_escritura_deja_de_verse_al_caducar_la_memoizacion()
    {
        var (cliente, _) = await SembrarTenantAsync(_tenant, "B10380186", "B10380194");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, cliente, vigenciaHasta: DateTime.UtcNow.AddSeconds(2));
        var reloj = new RelojManual();

        await using var contextoCircuito = CrearContexto(_tenant);
        var circuito = CrearServicio(contextoCircuito, gestor, new TenantActualAmbiental { TenantId = _tenant }, reloj);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal(cliente);

        // Nadie escribe nada: la Asignación de Cartera simplemente deja de estar vigente.
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([cliente],
            "sin avanzar el reloj de la memoización, sigue sirviendo lo memoizado");

        reloj.Avanzar(Caducidad);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty(
            "un sello de versión que solo cambiara al escribir no vería este vencimiento; la caducidad sí");
    }

    [Fact]
    public async Task Un_Gestor_no_revocado_conserva_su_alcance_tras_la_caducidad()
    {
        var (cliente, propia) = await SembrarTenantAsync(_tenant, "B10380186", "B10380194");
        var revocado = Guid.NewGuid();
        var conservado = Guid.NewGuid();
        var carteraRevocada = await OtorgarCarteraAsync(_tenant, revocado, cliente);
        await OtorgarCarteraAsync(_tenant, conservado, cliente);
        var reloj = new RelojManual();

        await using var contextoCircuito = CrearContexto(_tenant);
        var circuito = CrearServicio(contextoCircuito, conservado, new TenantActualAmbiental { TenantId = _tenant }, reloj);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal(cliente);

        await RevocarDesdeOtroCircuitoAsync(_tenant, carteraRevocada);
        reloj.Avanzar(Caducidad);

        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([cliente],
            "revocar la cartera de otro Gestor CAE no quita nada a este; la caducidad solo vuelve a resolver");
        (await circuito.ObtenerEmpresaIdsVisiblesAsync()).Should().Contain(propia);
    }

    [Fact]
    public async Task La_caducidad_es_por_Tenant_y_la_revocacion_en_uno_no_toca_el_alcance_del_otro()
    {
        var (clienteA, _) = await SembrarTenantAsync(_tenant, "B10380186", "B10380194");
        var (clienteB, _) = await SembrarTenantAsync(_otroTenant, "B10380210", "B10380228");
        var gestor = Guid.NewGuid();
        var carteraA = await OtorgarCarteraAsync(_tenant, gestor, clienteA);
        await OtorgarCarteraAsync(_otroTenant, gestor, clienteB);
        var reloj = new RelojManual();

        // Una sola instancia que cambia de Tenant, como el fan-out multi-Tenant de los paneles.
        var ambitoTenant = new TenantActualAmbiental { TenantId = _tenant };
        await using var contextoCircuito = CrearContexto(ambitoTenant);
        var circuito = CrearServicio(contextoCircuito, gestor, ambitoTenant, reloj);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal(clienteA);

        reloj.Avanzar(Caducidad / 2);
        ambitoTenant.TenantId = _otroTenant;
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([clienteB],
            "cada Tenant tiene su propia clave; A no se sirve a B");

        await RevocarDesdeOtroCircuitoAsync(_tenant, carteraA);

        // La generación de A empezó antes que la de B: caduca A y B sigue dentro de su ventana.
        reloj.Avanzar(Caducidad / 2);
        ambitoTenant.TenantId = _tenant;
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty(
            "la revocación en el Tenant A se ve en A al caducar su generación");
        ambitoTenant.TenantId = _otroTenant;
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([clienteB],
            "la revocación en A no quita la cartera vigente en B");

        reloj.Avanzar(Caducidad);
        (await circuito.ObtenerClienteIdsVisiblesAsync()).Should().Equal([clienteB],
            "ni siquiera tras volver a resolver B");
    }

    private static AlcanceDatosService CrearServicio(
        CaeManagerDbContext contexto, Guid usuarioId, TenantActualAmbiental ambitoTenant, TimeProvider reloj) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: ambitoTenant.TenantId),
            ambitoTenant, new SesionPrivilegiadaAusente(), vistaDemo: null, reloj,
            Options.Create(new CaducidadAlcanceOptions { Caducidad = Caducidad }));

    private CaeManagerDbContext CrearContexto(Guid tenantId) =>
        CrearContexto(new TenantActualAmbiental { TenantId = tenantId });

    private CaeManagerDbContext CrearContexto(TenantActualAmbiental tenantActual)
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
