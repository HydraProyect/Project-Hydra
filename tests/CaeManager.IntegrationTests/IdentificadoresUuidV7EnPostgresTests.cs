using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Visitas.Eventos;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// P1-M3: el Id v7 que genera <c>IdentificadorEntidad</c> en .NET tiene que
/// llegar a PostgreSQL como v7, con la marca de tiempo en los bytes altos. Es
/// lo que da la localidad en el índice de la clave primaria: si el orden de
/// bytes se invirtiera por el camino (el <see cref="Guid"/> de .NET guarda los
/// tres primeros campos en little-endian), el Id seguiría siendo único pero
/// PostgreSQL vería un valor sin versión 7 y sin orden temporal. Esa propiedad
/// solo se observa en la base: el test de arquitectura mira el valor en .NET.
/// </summary>
public class IdentificadoresUuidV7EnPostgresTests : IAsyncLifetime
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
    public async Task Una_entidad_guardada_tiene_en_PostgreSQL_un_Id_v7_con_su_hora_de_creacion_y_en_orden_temporal()
    {
        var conversacionId = Guid.NewGuid();
        var antes = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            await using var contexto = CrearContexto();
            var handler = new RegistrarEventoVisitaCreadaHandler(
                new EventoConversacionRepository(contexto), contexto, NullLogger<RegistrarEventoVisitaCreadaHandler>.Instance);
            await handler.Handle(new VisitaCreadaEvent(conversacionId, Guid.NewGuid()), CancellationToken.None);
            // Milisegundos distintos: dentro del mismo milisegundo v7 no fija orden.
            await Task.Delay(5);
        }

        var despues = DateTimeOffset.UtcNow;

        string tabla;
        await using (var contexto = CrearContexto())
            tabla = contexto.Model.FindEntityType(typeof(EventoConversacion))!.GetTableName()!;

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        // La marca se saca a mano de los 48 bits altos y no con
        // uuid_extract_timestamp(): en PostgreSQL 17 (el clúster local de
        // desarrollo) esa función solo entiende v1 y devuelve NULL para v7;
        // CI y producción van en 18.
        comando.CommandText =
            $"""
             SELECT uuid_extract_version("Id"),
                    to_timestamp((('x' || substr(replace("Id"::text, '-', ''), 1, 12))::bit(48)::bigint) / 1000.0)
             FROM "{tabla}" WHERE "ConversacionId" = @conversacion ORDER BY "Id"
             """;
        comando.Parameters.AddWithValue("conversacion", conversacionId);

        var filas = new List<(int Version, DateTime Marca)>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                filas.Add((lector.GetInt32(0), lector.GetDateTime(1)));
        }

        filas.Should().HaveCount(3);
        filas.Should().OnlyContain(f => f.Version == 7, "PostgreSQL tiene que leer el Id como UUID v7");
        filas.Should().OnlyContain(
            f => f.Marca >= antes.UtcDateTime.AddMilliseconds(-1) && f.Marca <= despues.UtcDateTime.AddMilliseconds(1),
            "la marca de tiempo del Id es la hora de creación de la entidad, en los bytes que PostgreSQL ordena");
        filas.Select(f => f.Marca).Should().BeInAscendingOrder(
            "ordenar por Id en PostgreSQL es ordenar por hora de creación: es la localidad del índice que busca P1-M3");
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
