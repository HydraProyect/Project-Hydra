using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// "Sin respuesta" como estado derivado (docs/COMUNICACIONES.md § 16.4): lo que
/// se comprueba aquí es que la condición se calcula bien contra los mensajes
/// reales del hilo, y que nadie aparece marcado sin motivo.
/// </summary>
public class ReclamacionesSinRespuestaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_reclamacion_antigua_sin_entrante_posterior_aparece_como_sin_respuesta()
    {
        var (clienteId, _) = await SembrarReclamacionAsync(
            "Sin Respuesta S.L.", "B10380186", diasAtras: 10, conRespuestaDelCliente: false, totalDocumentos: 2);

        var resultado = await EjecutarAsync();

        var fila = resultado.Should().ContainSingle().Which;
        fila.ClienteId.Should().Be(clienteId);
        fila.TotalDocumentos.Should().Be(2);
        fila.DiasTranscurridos.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task Si_el_cliente_contesto_en_el_mismo_hilo_deja_de_aparecer()
    {
        await SembrarReclamacionAsync(
            "Contestó S.L.", "B10380194", diasAtras: 10, conRespuestaDelCliente: true, totalDocumentos: 1);

        var resultado = await EjecutarAsync();

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_reclamacion_reciente_todavia_no_cuenta_como_sin_respuesta()
    {
        await SembrarReclamacionAsync(
            "Recién Reclamado S.L.", "B12345674", diasAtras: 2, conRespuestaDelCliente: false, totalDocumentos: 1);

        var resultado = await EjecutarAsync();

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_reclamacion_sin_conversacion_nunca_aparece()
    {
        // Salió por IEmailService (sin buzón conectado): no hay hilo donde mirar,
        // así que afirmar "sin respuesta" sería inventárselo.
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Sin Conversación S.L.", "B87654323", false, null, null);
            contexto.Empresas.Add(cliente);
            await contexto.SaveChangesAsync();

            contexto.ReclamacionesDocumentales.Add(new ReclamacionDocumental(
                cliente.Id, Guid.NewGuid(), "portal@cliente.local", DateTime.UtcNow.AddDays(-30), [Guid.NewGuid()]));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        resultado.Should().BeEmpty();
    }

    private async Task<(Guid ClienteId, Guid ConversacionId)> SembrarReclamacionAsync(
        string razonSocial, string cif, int diasAtras, bool conRespuestaDelCliente, int totalDocumentos)
    {
        await using var contexto = CrearContexto();

        var cliente = Empresa.CrearComoCliente(razonSocial, cif, false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var fechaEnvio = DateTime.UtcNow.AddDays(-diasAtras);

        var conversacion = new Conversacion($"Documentación pendiente de {razonSocial}", cliente.Id);
        conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@buzon.local", "<p>Reclamación</p>", fechaEnvio);

        if (conRespuestaDelCliente)
        {
            conversacion.AgregarMensaje(
                DireccionMensaje.Entrante, CanalConversacion.Correo, "portal@cliente.local", "<p>Recibido</p>",
                fechaEnvio.AddDays(1));
        }

        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var documentoIds = Enumerable.Range(0, totalDocumentos).Select(_ => Guid.NewGuid()).ToList();
        contexto.ReclamacionesDocumentales.Add(new ReclamacionDocumental(
            cliente.Id, Guid.NewGuid(), "portal@cliente.local", fechaEnvio, documentoIds, conversacion.Id));
        await contexto.SaveChangesAsync();

        return (cliente.Id, conversacion.Id);
    }

    private async Task<IReadOnlyList<ReclamacionSinRespuestaDto>> EjecutarAsync()
    {
        await using var lectura = CrearContexto();
        var handler = new ObtenerReclamacionesSinRespuestaQueryHandler(
            lectura, lectura, lectura, new AlcanceDatosServiceFalso());

        return await handler.Handle(new ObtenerReclamacionesSinRespuestaQuery(), CancellationToken.None);
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
