using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// El listado de Documentos materializaba todos los del tenant en cada carga
/// para poder paginar en memoria, porque el semáforo se calcula en memoria.
/// El filtro por Estado se traduce ahora a un rango de fechas y la paginación
/// baja a SQL.
///
/// Lo que estos tests protegen no es la paginación en sí, sino que las dos
/// definiciones de "estado" no se separen: la de
/// <see cref="CalculadoraEstadoDocumento"/>, que sigue siendo la fuente única
/// de verdad y decide lo que se pinta, y la traducción a fechas que decide lo
/// que SQL devuelve. Si divergen, la pantalla mostraría un semáforo que no
/// corresponde con el filtro aplicado — un fallo silencioso.
/// </summary>
public class DocumentosPaginacionEnSqlTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
        else
            parametros.Actualizar(UmbralAmbarDias, UmbralRojoDias);

        var cliente = Empresa.CrearComoCliente("Documentada S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var tipo = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, esObligatorio: true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        // Un documento por cada estado posible, colocando la fecha de
        // vencimiento justo dentro de cada franja.
        var vencimientos = new DateOnly?[]
        {
            null,                                   // NoAplica
            _hoy.AddDays(-1),                       // Vencido
            _hoy.AddDays(UmbralRojoDias - 1),       // Urgente
            _hoy.AddDays(UmbralAmbarDias - 1),      // Proximo
            _hoy.AddDays(UmbralAmbarDias + 60)      // Vigente
        };

        foreach (var vencimiento in vencimientos)
        {
            var documento = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-200), vencimiento);
            contexto.Documentos.Add(documento);
        }

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(EstadoDocumento.NoAplica)]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Vigente)]
    public async Task El_filtro_en_SQL_devuelve_exactamente_lo_que_calcula_la_calculadora(EstadoDocumento estado)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contexto, contexto);

        var resultado = await handler.Handle(
            new ObtenerDocumentosQuery(null, null, null, estado, Pagina: 1, TamanoPagina: 50), CancellationToken.None);

        resultado.Elementos.Should().NotBeEmpty("cada estado tiene un documento sembrado");

        // La comprobación que importa: lo que SQL seleccionó coincide con lo
        // que la calculadora dice de cada fila devuelta.
        foreach (var documento in resultado.Elementos)
        {
            CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, _hoy, UmbralAmbarDias, UmbralRojoDias)
                .Should().Be(estado);
            documento.Estado.Should().Be(estado);
        }
    }

    [Fact]
    public async Task Sin_filtro_de_estado_se_devuelven_todos_con_su_semaforo()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contexto, contexto);

        var resultado = await handler.Handle(
            new ObtenerDocumentosQuery(null, null, null, null, Pagina: 1, TamanoPagina: 50), CancellationToken.None);

        resultado.TotalElementos.Should().Be(5);
        resultado.Elementos.Select(d => d.Estado).Distinct().Should().HaveCount(5, "cada documento cae en un estado distinto");
    }

    [Fact]
    public async Task El_total_refleja_el_filtro_y_no_el_tamano_de_pagina()
    {
        // Con la paginación en SQL, total y página salen de dos consultas
        // distintas: si el filtro se aplicara solo a una, el paginador
        // mentiría.
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contexto, contexto);

        var resultado = await handler.Handle(
            new ObtenerDocumentosQuery(null, null, null, null, Pagina: 1, TamanoPagina: 2), CancellationToken.None);

        resultado.TotalElementos.Should().Be(5);
        resultado.Elementos.Should().HaveCount(2);
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
