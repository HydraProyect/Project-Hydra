using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
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
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private readonly Dictionary<Guid, VigenciaDocumento> _vigenciaPorDocumento = [];
    private Guid _tipoId;

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

        var tipo = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();
        _tipoId = tipo.Id;

        // Un documento por cada estado posible, colocando la fecha de
        // vencimiento justo dentro de cada franja. Los dos sin fecha son
        // estados distintos: «no caduca» confirmado y «sin confirmar».
        var vigencias = new[]
        {
            VigenciaDocumento.NoCaduca,                                    // SinCaducidad
            VigenciaDocumento.SinConfirmar,                                // SinConfirmar
            VigenciaDocumento.VenceEl(_hoy.AddDays(-1)),                   // Vencido
            VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralRojoDias - 1)),   // Urgente
            VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias - 1)),  // Proximo
            VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias + 60))  // Vigente
        };

        foreach (var vigencia in vigencias)
        {
            var documento = Documento.DeCliente(cliente.Id, tipo.Id, _hoy.AddDays(-200), vigencia);
            contexto.Documentos.Add(documento);
            _vigenciaPorDocumento[documento.Id] = vigencia;
        }

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.SinConfirmar)]
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
            CalculadoraEstadoDocumento.Calcular(_vigenciaPorDocumento[documento.Id], _hoy, UmbralAmbarDias, UmbralRojoDias)
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

        resultado.TotalElementos.Should().Be(6);
        resultado.Elementos.Select(d => d.Estado).Distinct().Should().HaveCount(6, "cada documento cae en un estado distinto");
        resultado.Elementos.Should().OnlyContain(d => d.TipoDocumentoId == _tipoId,
            "la fila lleva el identificador de su tipo: es el destino del icono 360 del listado");
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

        resultado.TotalElementos.Should().Be(6);
        resultado.Elementos.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task La_pagina_y_sus_acreditaciones_usan_cuatro_lecturas_sin_consulta_separada_de_proveedores(int tamano)
    {
        var contador = new ContadorLecturas();
        await using var contexto = CrearContexto(contador);
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contexto, contexto);

        var resultado = await handler.Handle(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: tamano), default);

        resultado.Elementos.Should().HaveCount(tamano);
        contador.Total.Should().Be(4, "parámetros, total, página y acreditaciones con nombre de plataforma");
    }

    [Theory]
    [InlineData]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Vencido, EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.EnTolerancia)]
    public async Task Con_recuentos_el_total_sale_de_ellos_sin_otra_lectura_del_universo_y_no_cambia(params EstadoDocumento[] filtro)
    {
        // El listado pide siempre la franja de recuentos: el total se deriva de ella en vez de contar otra vez la
        // unión de las cinco ramas. El total y la página tienen que ser los mismos que sin recuentos.
        IReadOnlyList<EstadoDocumento>? estados = filtro.Length == 0 ? null : filtro;
        await using var referencia = CrearContexto();
        var sinRecuentos = await new ObtenerDocumentosQueryHandler(
                referencia, referencia, referencia, referencia, referencia, referencia, referencia,
                new AlcanceDatosServiceFalso(), referencia, referencia)
            .Handle(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 2, Estados: estados), default);

        var contador = new ContadorLecturas();
        await using var contexto = CrearContexto(contador);
        var conRecuentos = await new ObtenerDocumentosQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, contexto,
                new AlcanceDatosServiceFalso(), contexto, contexto)
            .Handle(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 2, Estados: estados, ConRecuentosPorEstado: true), default);

        conRecuentos.TotalElementos.Should().Be(sinRecuentos.TotalElementos);
        conRecuentos.Elementos.Select(d => d.Id).Should().Equal(sinRecuentos.Elementos.Select(d => d.Id));
        contador.Total.Should().Be(sinRecuentos.Elementos.Count == 0 ? 3 : 4,
            "parámetros, recuentos, página y (si hay filas) acreditaciones: el total ya no es una lectura aparte");
    }

    private sealed class ContadorLecturas : DbCommandInterceptor
    {
        public int Total { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Total++;
            return ValueTask.FromResult(result);
        }
    }

    private CaeManagerDbContext CrearContexto(DbCommandInterceptor? contador = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(contador is null ? [new TenantSelladoInterceptor(tenantActual)] : [new TenantSelladoInterceptor(tenantActual), contador])
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
