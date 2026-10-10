using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Clientes;

/// <summary>
/// Qué roles reciben la «Nota interna» de un Cliente empresarial, con el
/// servicio de alcance REAL contra PostgreSQL delante del handler.
///
/// <para>
/// La nota la escribe el equipo que gestiona la cartera sobre el propio Cliente
/// empresarial («Solo visible para tu equipo»). El Usuario de Cliente (rol
/// Cliente) tiene a su Cliente empresarial dentro del alcance de LECTURA, así
/// que la ficha le llega; la nota no. Mismo criterio que el resto de artefactos
/// internos de gestión: <c>ObtenerClienteIdsParaGestionAsync</c>.
/// </para>
/// </summary>
public class NotaInternaDeClientePorRolTests : IAsyncLifetime
{
    private const string Nota = "Nota interna de prueba";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Repro S.L.", "B10380392", false, Nota, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Usuario_de_Cliente_recibe_la_ficha_de_su_Cliente_sin_la_nota_interna()
    {
        var usuario = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenant))
        {
            contexto.Users.Add(new CaeManager.Infrastructure.Identity.ApplicationUser
            {
                Id = usuario,
                UserName = $"p-{usuario:N}@ejemplo.test",
                Email = $"p-{usuario:N}@ejemplo.test",
                ClienteId = _clienteId,
                TenantId = _tenant
            });
            await contexto.SaveChangesAsync();
        }

        var detalle = await ObtenerAsync(usuario, "Cliente");

        // La ficha sí: sin ella, el null de la nota podría ser una siembra que
        // no encuentra el Cliente y no la regla que se mide.
        detalle.Should().NotBeNull("su propio Cliente empresarial está en su alcance de lectura");
        detalle!.RazonSocial.Should().Be("Cliente Repro S.L.");
        detalle.Notas.Should().BeNull("la nota interna es del equipo de gestión, no contenido de portal");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Control_positivo_el_lado_de_gestion_recibe_la_nota(string rol)
    {
        // Gestor CAE: rol de cartera, necesita Asignación de Cartera. Consulta:
        // lectura total del Tenant propietario; la nota es lectura, no secreto.
        var usuario = await OtorgarCarteraAsync();

        var detalle = await ObtenerAsync(usuario, rol);

        detalle.Should().NotBeNull();
        detalle!.Notas.Should().Be(Nota);
    }

    private async Task<Guid> OtorgarCarteraAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.Universal, ahora, null, ahora));

        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private async Task<ClienteDetalleDto?> ObtenerAsync(Guid usuario, string rol)
    {
        await using var contexto = CrearContexto(_tenant);
        var alcance = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        return await new ObtenerClientePorIdQueryHandler(contexto, alcance)
            .Handle(new ObtenerClientePorIdQuery(_clienteId), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
