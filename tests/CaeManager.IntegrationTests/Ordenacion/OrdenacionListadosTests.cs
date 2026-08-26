using CaeManager.Application.Documentos;
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

namespace CaeManager.IntegrationTests.Ordenacion;

/// <summary>
/// Hasta la Fase 85 la ordenación de las tablas era decorativa: las columnas
/// llevaban <c>Sortable="true"</c>, pero el <c>ItemsProvider</c> ignoraba el
/// orden pedido y la Query traía siempre el mismo <c>OrderBy</c> fijo — hacer
/// clic en una cabecera movía el indicador y no reordenaba nada.
///
/// Estos tests fijan las tres propiedades que hacen que eso no vuelva a pasar
/// en silencio: que cada columna ordene de verdad en ambos sentidos, que un
/// <c>OrdenarPor</c> desconocido caiga al orden por defecto en vez de fallar o
/// acabar interpolado en SQL, y — lo más delicado — que ordenar por "Estado",
/// que no es una columna sino un cálculo, coincida con lo que dice
/// <see cref="CalculadoraEstadoDocumento"/>, la fuente única de verdad.
/// </summary>
public class OrdenacionListadosTests : IAsyncLifetime
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

        var cliente = Empresa.CrearComoCliente("Zeta Industrial S.A.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        // Nombres deliberadamente fuera de orden alfabético respecto al de
        // emisión, para que ordenar por una columna no pueda "acertar" por
        // coincidir con el orden por defecto.
        var tipoA = new TipoDocumento("Alta en Seguridad Social", 12, true, 1, AmbitoAplicacion.Cliente, true);
        var tipoM = new TipoDocumento("Modelo 190", 12, true, 2, AmbitoAplicacion.Cliente, true);
        var tipoZ = new TipoDocumento("Zona de trabajo", 12, true, 3, AmbitoAplicacion.Cliente, true);
        contexto.TiposDocumento.AddRange(tipoA, tipoM, tipoZ);
        await contexto.SaveChangesAsync();

        // Un documento por estado, y la fecha de emisión en orden inverso al
        // del tipo, para que los dos criterios se distingan.
        contexto.Documentos.AddRange(
            Documento.DeCliente(cliente.Id, tipoZ.Id, _hoy.AddDays(-10), _hoy.AddDays(-1)),                     // Vencido
            Documento.DeCliente(cliente.Id, tipoM.Id, _hoy.AddDays(-20), _hoy.AddDays(UmbralRojoDias - 1)),     // Urgente
            Documento.DeCliente(cliente.Id, tipoA.Id, _hoy.AddDays(-30), _hoy.AddDays(UmbralAmbarDias - 1)),    // Proximo
            Documento.DeCliente(cliente.Id, tipoA.Id, _hoy.AddDays(-40), _hoy.AddDays(UmbralAmbarDias + 60)),   // Vigente
            Documento.DeCliente(cliente.Id, tipoM.Id, _hoy.AddDays(-50), null));                                // NoAplica

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(nameof(DocumentoListaDto.TipoDocumentoNombre))]
    [InlineData(nameof(DocumentoListaDto.FechaEmision))]
    [InlineData(nameof(DocumentoListaDto.FechaVencimiento))]
    public async Task Cada_columna_ordenable_ordena_de_verdad_en_los_dos_sentidos(string columna)
    {
        var ascendente = await ObtenerDtosAsync(columna, descendente: false);
        var descendente = await ObtenerDtosAsync(columna, descendente: true);

        ascendente.Should().HaveCount(5);

        // No se compara "descendente == inverso de ascendente": el desempate
        // final es siempre ThenBy(Id) ascendente — deliberadamente, para que la
        // paginación sea estable — así que con valores repetidos el inverso
        // exacto no se cumple. Lo que sí debe cumplirse es el orden de la clave.
        //
        // Los nulos se excluyen: PostgreSQL los coloca al final en ascendente y
        // al principio en descendente, y eso es lo que la pantalla enseña — un
        // documento sin vencimiento no es "el más antiguo".
        ClaveDe(ascendente, columna).Where(c => c is not null).Should().BeInAscendingOrder();
        ClaveDe(descendente, columna).Where(c => c is not null).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Los_documentos_sin_vencimiento_quedan_al_final_al_ordenar_por_vencimiento()
    {
        var elementos = await ObtenerDtosAsync(nameof(DocumentoListaDto.FechaVencimiento), descendente: false);

        elementos.Last().FechaVencimiento.Should().BeNull();
        elementos.SkipLast(1).Should().OnlyContain(d => d.FechaVencimiento != null);
    }

    private static IEnumerable<IComparable?> ClaveDe(IEnumerable<DocumentoListaDto> elementos, string columna) =>
        columna switch
        {
            nameof(DocumentoListaDto.TipoDocumentoNombre) => elementos.Select(d => (IComparable?)d.TipoDocumentoNombre),
            nameof(DocumentoListaDto.FechaEmision) => elementos.Select(d => (IComparable?)d.FechaEmision),
            nameof(DocumentoListaDto.FechaVencimiento) => elementos.Select(d => (IComparable?)d.FechaVencimiento),
            _ => throw new ArgumentOutOfRangeException(nameof(columna), columna, "Columna no cubierta por el test.")
        };

    [Fact]
    public async Task El_desempate_por_Id_hace_estable_la_paginacion_con_valores_repetidos()
    {
        // Dos documentos comparten TipoDocumento: sin un criterio total,
        // PostgreSQL podría devolverlos en distinto orden en cada consulta y
        // una fila aparecería dos veces o no aparecería nunca al paginar.
        var primera = await ObtenerDtosAsync(nameof(DocumentoListaDto.TipoDocumentoNombre), false, tamanoPagina: 2, pagina: 1);
        var segunda = await ObtenerDtosAsync(nameof(DocumentoListaDto.TipoDocumentoNombre), false, tamanoPagina: 2, pagina: 2);
        var tercera = await ObtenerDtosAsync(nameof(DocumentoListaDto.TipoDocumentoNombre), false, tamanoPagina: 2, pagina: 3);

        var recorridas = primera.Concat(segunda).Concat(tercera).Select(d => d.Id).ToList();

        recorridas.Should().HaveCount(5);
        recorridas.Should().OnlyHaveUniqueItems("ninguna fila puede repetirse entre páginas");

        var deUnaVez = await ObtenerDtosAsync(nameof(DocumentoListaDto.TipoDocumentoNombre), false);
        recorridas.Should().Equal(deUnaVez.Select(d => d.Id));
    }

    [Fact]
    public async Task Ordena_por_tipo_de_documento_alfabeticamente_y_no_por_el_orden_por_defecto()
    {
        var elementos = await ObtenerDtosAsync(nameof(DocumentoListaDto.TipoDocumentoNombre), descendente: false);

        elementos.Select(d => d.TipoDocumentoNombre)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Ordenar_por_estado_coincide_con_la_calculadora_y_pone_primero_lo_que_mas_urge()
    {
        var elementos = await ObtenerDtosAsync(nameof(DocumentoListaDto.Estado), descendente: false);

        // El orden se resuelve en SQL con un CASE sobre FechaVencimiento; lo
        // que se comprueba es que ese CASE dice lo mismo que la calculadora.
        var esperado = new[]
        {
            EstadoDocumento.Vencido,
            EstadoDocumento.Urgente,
            EstadoDocumento.Proximo,
            EstadoDocumento.Vigente,
            EstadoDocumento.NoAplica
        };

        elementos.Select(d => d.Estado).Should().Equal(esperado);

        foreach (var documento in elementos)
        {
            CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, _hoy, UmbralAmbarDias, UmbralRojoDias)
                .Should().Be(documento.Estado);
        }
    }

    [Fact]
    public async Task Un_orden_desconocido_cae_al_orden_por_defecto_sin_fallar()
    {
        // Lo que protege: la lista blanca. Si el nombre de columna acabara
        // interpolado en SQL o se lanzara una excepción, la pantalla se caería
        // con solo manipular la URL.
        var conBasura = await ObtenerDtosAsync("'; DROP TABLE \"Documentos\"; --", descendente: false);
        var porDefecto = await ObtenerDtosAsync(null, descendente: false);

        conBasura.Select(d => d.Id).Should().Equal(porDefecto.Select(d => d.Id));
        porDefecto.Select(d => d.FechaEmision).Should().BeInDescendingOrder();
    }

    private async Task<List<DocumentoListaDto>> ObtenerDtosAsync(
        string? columna, bool descendente, int tamanoPagina = 50, int pagina = 1)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contexto, contexto);

        var resultado = await handler.Handle(
            new ObtenerDocumentosQuery(
                null, null, null, null, Pagina: pagina, TamanoPagina: tamanoPagina,
                OrdenarPor: columna, Descendente: descendente),
            CancellationToken.None);

        return resultado.Elementos.ToList();
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
