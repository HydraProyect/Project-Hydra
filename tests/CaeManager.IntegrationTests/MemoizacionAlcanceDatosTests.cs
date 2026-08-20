using CaeManager.Domain.Centros;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// El caché de <see cref="AlcanceDatosService"/> se amplió de un alcance a
/// los seis. Un caché mal hecho aquí no es un problema de rendimiento sino de
/// aislamiento, así que lo que se comprueba es que memoizar no cambia ninguna
/// respuesta — en particular que "sin restricción" (null) y "cartera vacía"
/// ([]) siguen siendo distintos entre sí y estables entre llamadas.
/// </summary>
public class MemoizacionAlcanceDatosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Memoizada S.L.", "B12345674", esCritico: false);
        contexto.Clientes.Add(cliente);
        await contexto.SaveChangesAsync();

        var empresa = new Empresa("Contrata Memo S.L.", "B12345674");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        contexto.Centros.Add(new Centro(cliente.Id, empresa.Id, "Centro Memo", "Calle Falsa 1"));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Un_rol_con_acceso_total_sigue_devolviendo_null_en_los_seis_alcances()
    {
        // null es "sin restricción" y no debe confundirse nunca con "todavía
        // sin resolver": esa confusión sería un caché que abre el alcance.
        await using var contexto = CrearContexto();
        var servicio = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        for (var intento = 0; intento < 2; intento++)
        {
            (await servicio.ObtenerClienteIdsVisiblesAsync()).Should().BeNull();
            (await servicio.ObtenerCentroIdsVisiblesAsync()).Should().BeNull();
            (await servicio.ObtenerEmpresaIdsVisiblesAsync()).Should().BeNull();
            (await servicio.ObtenerSubcontrataIdsVisiblesAsync()).Should().BeNull();
            (await servicio.ObtenerTrabajadorIdsVisiblesAsync()).Should().BeNull();
            (await servicio.ObtenerVehiculoIdsVisiblesAsync()).Should().BeNull();
        }
    }

    [Fact]
    public async Task Un_gestor_sin_cartera_sigue_devolviendo_vacio_y_no_null()
    {
        // El caso que más importa: [] es "no ve nada". Si el caché lo
        // devolviera como null, ese usuario pasaría a verlo todo.
        await using var contexto = CrearContexto();
        var servicio = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", tenantOrigenId: _tenant), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        for (var intento = 0; intento < 2; intento++)
        {
            (await servicio.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
            (await servicio.ObtenerCentroIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
            (await servicio.ObtenerEmpresaIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
            (await servicio.ObtenerSubcontrataIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
            (await servicio.ObtenerTrabajadorIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
            (await servicio.ObtenerVehiculoIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
        }
    }

    [Fact]
    public async Task Con_cartera_asignada_el_alcance_derivado_es_estable_entre_llamadas()
    {
        await using var contextoAsignacion = CrearContexto();
        var cliente = await contextoAsignacion.Clientes.FirstAsync(c => c.Id == _clienteId);
        var usuarioId = Guid.NewGuid();

        // Las dos escrituras que hace el sistema al asignar cartera: la
        // proyección de compatibilidad y la asignación real, que es de donde
        // el alcance lee desde la conmutación de autorización (F1). Escribir
        // solo la proyección dejaría al gestor sin alcance, que es
        // precisamente lo que la doble escritura evita.
        cliente.AsignarEjecutivo(usuarioId);

        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
        contextoAsignacion.AsignacionesOperacion.Add(raiz);
        contextoAsignacion.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(_clienteId), ahora, null, ahora));

        await contextoAsignacion.SaveChangesAsync();

        await using var contexto = CrearContexto();
        var servicio = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: _tenant), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        var clientesPrimera = await servicio.ObtenerClienteIdsVisiblesAsync();
        var centrosPrimera = await servicio.ObtenerCentroIdsVisiblesAsync();
        var empresasPrimera = await servicio.ObtenerEmpresaIdsVisiblesAsync();

        clientesPrimera.Should().ContainSingle().Which.Should().Be(_clienteId);
        centrosPrimera.Should().ContainSingle();
        empresasPrimera.Should().ContainSingle();

        // Segunda tanda: mismos resultados servidos desde el caché.
        (await servicio.ObtenerClienteIdsVisiblesAsync()).Should().BeEquivalentTo(clientesPrimera);
        (await servicio.ObtenerCentroIdsVisiblesAsync()).Should().BeEquivalentTo(centrosPrimera);
        (await servicio.ObtenerEmpresaIdsVisiblesAsync()).Should().BeEquivalentTo(empresasPrimera);
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
}
