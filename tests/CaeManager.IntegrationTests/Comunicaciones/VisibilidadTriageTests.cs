using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Regresión del hallazgo N-2 de INFORME-AUDITORIA-2.md. La cola de triage
/// (conversaciones con ClienteId null) quedaba visible para cualquier usuario
/// autenticado del tenant, rol <c>Cliente</c> incluido: un contacto de una
/// empresa cliente externa podía leer el correo entrante sin triar del resto
/// de clientes escribiendo /comunicaciones en la barra de direcciones.
///
/// La página lleva ahora <c>[Authorize(Roles = RolesComunicaciones.Gestion)]</c>,
/// pero eso cierra la puerta, no el dato: esto cubre la query, que es lo que
/// protege a cualquier UI o API futura.
/// </summary>
public class VisibilidadTriageTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clientePropio;
    private Guid _conversacionSinCliente;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Contrata Propia S.L.", "B12345674", esCritico: false);
        contexto.Clientes.Add(cliente);

        var propia = new ConversacionCorreo("Mi propia consulta", clienteId: cliente.Id);
        propia.AgregarMensaje(DireccionMensaje.Entrante, "yo@propia.test", "<p>Hola</p>");

        var sinTriar = new ConversacionCorreo("Correo de otra empresa sin triar");
        sinTriar.AgregarMensaje(DireccionMensaje.Entrante, "otro@ajena.test", "<p>Datos de otra empresa</p>");

        contexto.ConversacionesCorreo.AddRange(propia, sinTriar);
        await contexto.SaveChangesAsync();

        _clientePropio = cliente.Id;
        _conversacionSinCliente = sinTriar.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task El_rol_Cliente_no_ve_la_cola_de_triage_en_la_lista()
    {
        var conversaciones = await ListarComo("Cliente");

        conversaciones.Should().ContainSingle("solo debe ver lo suyo")
            .Which.ClienteId.Should().Be(_clientePropio);
    }

    [Fact]
    public async Task El_rol_Cliente_no_abre_un_hilo_sin_triar_aunque_tenga_el_Guid()
    {
        // Acotar solo el listado dejaba el hilo accesible por Id.
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            contexto, new AlcanceDatosServiceFalso(clienteIds: [_clientePropio]),
            new GanssSanitizadorHtmlService(), new CurrentUserServiceFalso(rol: "Cliente"));

        var detalle = await handler.Handle(
            new ObtenerConversacionPorIdQuery(_conversacionSinCliente), CancellationToken.None);

        detalle.Should().BeNull();
    }

    [Fact]
    public async Task Un_gestor_con_cartera_acotada_si_ve_la_cola_de_triage()
    {
        // Contrapeso imprescindible: si el triaje deja de verse para los roles
        // de gestión, nadie puede triar y el módulo se queda sin su función
        // principal (§ 12.4).
        var conversaciones = await ListarComo("GestorCae");

        conversaciones.Should().HaveCount(2);
        conversaciones.Should().Contain(c => c.ClienteId == null);
    }

    private async Task<IReadOnlyList<ConversacionListaDto>> ListarComo(string rol)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionesQueryHandler(
            contexto,
            new AlcanceDatosServiceFalso(clienteIds: [_clientePropio]),
            new CurrentUserServiceFalso(rol: rol));

        return await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None);
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
