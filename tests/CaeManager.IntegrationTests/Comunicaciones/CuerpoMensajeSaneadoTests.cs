using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
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
/// Regresión del hallazgo N-1 de INFORME-AUDITORIA-2.md a través del handler
/// real, no solo del sanitizador aislado
/// (<see cref="GanssSanitizadorHtmlServiceTests"/> cubre eso). Lo que aquí se
/// garantiza es la cadena entera: un cuerpo malicioso guardado en base de
/// datos no puede salir de la capa de aplicación tal cual, porque
/// <c>Bandeja.razor</c> lo renderiza como <c>MarkupString</c> confiando en
/// que ya viene saneado.
///
/// El escenario es el de la ingesta por Microsoft Graph que anuncia
/// <see cref="Conversacion"/>: el cuerpo lo escribe quien envía el
/// correo, así que se simula insertándolo directamente, sin pasar por
/// ningún comando del producto.
/// </summary>
public class CuerpoMensajeSaneadoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _conversacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var conversacion = new Conversacion("Certificado pendiente");
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "externo@remitente.test",
            "<p>Buenos días</p><img src=x onerror=\"alert(document.cookie)\"><script>alert(1)</script>");

        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        _conversacionId = conversacion.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task El_cuerpo_sale_del_handler_sin_nada_ejecutable()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(), new GanssSanitizadorHtmlService(),
            new CurrentUserServiceFalso(rol: "GestorCae"));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(_conversacionId), CancellationToken.None);

        var cuerpo = detalle!.Mensajes.Single().CuerpoHtml;

        cuerpo.Should().NotContain("onerror").And.NotContain("<script").And.NotContain("alert");
        cuerpo.Should().Contain("Buenos días", "el saneado no puede llevarse por delante el texto del correo");
    }

    [Fact]
    public async Task Lo_guardado_conserva_el_original()
    {
        // El saneado es una decisión de presentación: el correo tal como
        // llegó se conserva, porque las reglas de la lista blanca cambian y
        // destruir el original no se puede deshacer.
        await using var contexto = CrearContexto();

        var almacenado = await contexto.Mensajes
            .Where(m => m.ConversacionId == _conversacionId)
            .Select(m => m.CuerpoHtml)
            .SingleAsync();

        almacenado.Should().Contain("onerror");
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
