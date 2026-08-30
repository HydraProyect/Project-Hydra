using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Auditoría módulo 6: Microsoft Graph puede asignar el MISMO
/// conversationId a un hilo en el que participan dos buzones conectados
/// distintos del mismo tenant (documentado por Microsoft: comparten
/// conversationId los participantes de Exchange Online de la misma
/// organización). El índice único y las búsquedas deben estar acotados por
/// ConexionIntegracionId, no solo por TenantId — si no, el segundo buzón no
/// podría tener su propia fila para ese mismo hilo, o peor, un mensaje de un
/// buzón se colaría en el hilo del otro.
/// </summary>
public class HiloExternoIdAcotadoPorConexionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_conexiones_distintas_pueden_tener_el_mismo_hilo_externo_sin_violar_el_indice_unico()
    {
        const string hiloCompartido = "graph-conversation-compartida";
        var conexionA = Guid.NewGuid();
        var conexionB = Guid.NewGuid();

        await using var contexto = CrearContexto();

        var conversacionA = new Conversacion("Hilo visto desde el buzón compartido del cliente");
        conversacionA.AsociarConexion(conexionA, hiloCompartido);
        contexto.Conversaciones.Add(conversacionA);

        var conversacionB = new Conversacion("El mismo hilo, visto desde el buzón personal del gestor");
        conversacionB.AsociarConexion(conexionB, hiloCompartido);
        contexto.Conversaciones.Add(conversacionB);

        var accion = async () => await contexto.SaveChangesAsync();

        await accion.Should().NotThrowAsync("dos buzones distintos deben poder tener su propia fila para el mismo conversationId de Graph");
    }

    [Fact]
    public async Task Busca_el_hilo_de_la_conexion_pedida_sin_devolver_el_de_otra_conexion_con_el_mismo_id()
    {
        const string hiloCompartido = "graph-conversation-compartida-2";
        var conexionA = Guid.NewGuid();
        var conexionB = Guid.NewGuid();
        Guid conversacionAId, conversacionBId;

        await using (var contexto = CrearContexto())
        {
            var conversacionA = new Conversacion("Hilo del buzón A");
            conversacionA.AsociarConexion(conexionA, hiloCompartido);
            contexto.Conversaciones.Add(conversacionA);

            var conversacionB = new Conversacion("Hilo del buzón B");
            conversacionB.AsociarConexion(conexionB, hiloCompartido);
            contexto.Conversaciones.Add(conversacionB);

            await contexto.SaveChangesAsync();
            conversacionAId = conversacionA.Id;
            conversacionBId = conversacionB.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var repositorio = new ConversacionRepository(contexto);

            var encontradaParaA = await repositorio.ObtenerPorHiloExternoAsync(conexionA, hiloCompartido);
            var encontradaParaB = await repositorio.ObtenerPorHiloExternoAsync(conexionB, hiloCompartido);

            encontradaParaA!.Id.Should().Be(conversacionAId);
            encontradaParaB!.Id.Should().Be(conversacionBId);
        }
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
