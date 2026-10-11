using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
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

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// Qué roles reciben la «Nota interna» en Empresa 360 (<c>ObtenerEmpresaPorIdQuery</c>), con el servicio de alcance
/// REAL contra PostgreSQL delante del handler. Gemelo de <c>NotaInternaDeClientePorRolTests</c>: es la misma nota
/// (<c>Empresa.Notas</c>) y el mismo criterio (<c>OperaDesdeElLadoDeGestionAsync</c>). El Usuario de Cliente recibe
/// la ficha, sin nota y sin tarjeta.
/// </summary>
public class NotaInternaDeEmpresaPorRolTests : IAsyncLifetime
{
    private const string Nota = "Nota interna de prueba";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();

        // El portal ve en Empresa 360 a la contratista (Empresa propia) que trabaja para su Cliente empresarial.
        var cliente = Empresa.CrearComoCliente("Cliente Repro S.L.", "B10380392", false, null, null);
        var contratista = new Empresa("Contratista Repro S.L.", "B12345674");
        contratista.FijarNotaInterna(Nota);
        contexto.Empresas.AddRange(cliente, contratista);
        contexto.RelacionesEmpresariales.Add(
            CaeManager.Domain.RelacionesEmpresariales.RelacionEmpresarial.Crear(contratista.Id, cliente.Id, DateTime.UtcNow));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _empresaId = contratista.Id;
    }

    private Guid _empresaId;

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Usuario_de_Cliente_recibe_la_ficha_de_la_contratista_sin_la_nota_interna()
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
        detalle.Should().NotBeNull("la contratista de su Cliente empresarial está en su alcance de lectura");
        detalle!.RazonSocial.Should().Be("Contratista Repro S.L.");
        detalle.Notas.Should().BeNull("la nota interna es del equipo de gestión, no contenido de portal");
        detalle.NotaInternaVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Control_positivo_el_lado_de_gestion_recibe_la_nota(string rol)
    {
        // Todos los roles menos Cliente: una regla «solo Gestor CAE y Consulta»
        // no pasaría. Gestor CAE y Coordinador CAE son roles de cartera y
        // necesitan Asignación de Cartera; para los demás sobra y no estorba.
        var usuario = await OtorgarCarteraAsync();

        var detalle = await ObtenerAsync(usuario, rol);

        detalle.Should().NotBeNull();
        detalle!.Notas.Should().Be(Nota);
        detalle.NotaInternaVisible.Should().BeTrue();
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

    private async Task<EmpresaDetalleDto?> ObtenerAsync(Guid usuario, string rol)
    {
        await using var contexto = CrearContexto(_tenant);
        var alcance = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        return await new ObtenerEmpresaPorIdQueryHandler(contexto, alcance)
            .Handle(new ObtenerEmpresaPorIdQuery(_empresaId), CancellationToken.None);
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
