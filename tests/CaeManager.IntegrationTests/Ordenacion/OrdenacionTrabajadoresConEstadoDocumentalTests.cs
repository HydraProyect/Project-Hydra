using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Ordenacion;

/// <summary>
/// <see cref="ObtenerTrabajadoresQueryHandler"/> tiene dos caminos: el normal
/// pagina en SQL, y el de "estado documental" materializa todo porque el
/// estado no es una columna sino un cálculo sobre los Documentos. Hasta el
/// 2026-09-11 el segundo camino ordenaba siempre por estado, apellidos, nombre
/// e Id <b>sin mirar <c>OrdenarPor</c></b>: con un filtro de estado activo en
/// <c>/trabajadores</c>, pulsar «Nombre», «DNI» o «Empresa / Subcontrata»
/// movía la flecha de la cabecera y no reordenaba nada — cuatro de las cinco
/// columnas ordenables eran decorativas en cuanto se filtraba. Peor:
/// <c>Descendente</c> se aplicaba a la clave de estado, así que pedir «Nombre
/// descendente» cambiaba el orden por uno que el usuario no había pedido.
///
/// Es exactamente la clase de fallo que <see cref="OrdenacionListadosTests"/>
/// fijó para el listado de Documentos (Fase 85), reaparecida en el camino que
/// aquel no cubría. Estos tests la fijan en la capa que la garantiza —
/// PostgreSQL de verdad, porque el orden lo emite su <c>ORDER BY</c> y lo que
/// se prueba es que sobrevive al filtrado en memoria.
///
/// El dato está construido para que ninguna columna pueda "acertar" por
/// coincidencia: los apellidos van al revés que los nombres, los DNI en un
/// tercer orden, y dos trabajadores son homónimos exactos para que el
/// desempate por Id tenga algo que desempatar.
/// </summary>
public class OrdenacionTrabajadoresConEstadoDocumentalTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    /// <summary>Los seis Vencidos: lo que devuelve el filtro <c>?estado=Vencido</c>.</summary>
    private const int TrabajadoresVencidos = 6;

    /// <summary>Los seis Vencidos más los dos Vigentes de ruido.</summary>
    private const int TrabajadoresTotales = 8;

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

        // Dos empleadores para que "Empresa / Subcontrata" tenga dos valores
        // que ordenar. Una Empresa contraparte y una Subcontrata, que es como
        // el handler los resuelve (EmpleadorNombre sale de uno u otro join).
        var empresa = Empresa.CrearComoCliente("Alfa Montajes S.L.", "B12345674", false, null, null);
        var subcontrata = Empresa.CrearComoSubcontrata("Beta Servicios S.L.", null, "Estandar");
        contexto.Empresas.AddRange(empresa, subcontrata);

        var tipo = new TipoDocumento("Reconocimiento medico", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        // Nombres, apellidos y DNI deliberadamente en tres órdenes distintos:
        // apellidos ascendente da Elena…Ana, nombre ascendente da Ana…Elena y
        // el DNI un tercero. Todo ASCII y distinto ya en la primera letra, para
        // que el ORDER BY de PostgreSQL y el comparador de .NET no puedan
        // discrepar por intercalación.
        var vencidos = new[]
        {
            Trabajador.DeEmpresa(subcontrata.Id, "Elena", "Prado", "33333333P"),
            Trabajador.DeEmpresa(empresa.Id, "Diana", "Quesada", "22222222J"),
            Trabajador.DeEmpresa(subcontrata.Id, "Carlos", "Robles", "11111111H"),
            Trabajador.DeEmpresa(empresa.Id, "Bruno", "Sotelo", "12884021L"),
            // Homónimos exactos: mismo nombre y mismos apellidos. Sin el
            // desempate por Id, PostgreSQL puede devolverlos en distinto orden
            // en cada consulta y al paginar uno se repite y el otro no sale.
            Trabajador.DeEmpresa(subcontrata.Id, "Ana", "Torres", "00000000T"),
            Trabajador.DeEmpresa(empresa.Id, "Ana", "Torres", "21223344W")
        };

        // Ruido: dos Vigentes en los dos extremos de todos los órdenes. Si el
        // filtro dejara de filtrar, aparecerían el primero y el último de cada
        // lista y ningún test podría pasar por casualidad.
        var vigentes = new[]
        {
            Trabajador.DeEmpresa(empresa.Id, "Alba", "Pinto", "11223344B"),
            Trabajador.DeEmpresa(subcontrata.Id, "Zoe", "Zurita", "22334455Y")
        };

        contexto.Trabajadores.AddRange(vencidos);
        contexto.Trabajadores.AddRange(vigentes);
        await contexto.SaveChangesAsync();

        foreach (var trabajador in vencidos)
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id, _hoy.AddDays(-400), _hoy.AddDays(-1)));

        foreach (var trabajador in vigentes)
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(nameof(TrabajadorListaDto.Nombre))]
    [InlineData(nameof(TrabajadorListaDto.Apellidos))]
    [InlineData(nameof(TrabajadorListaDto.Dni))]
    [InlineData(nameof(TrabajadorListaDto.EmpleadorNombre))]
    public async Task Con_filtro_de_estado_documental_cada_columna_ordena_de_verdad_en_los_dos_sentidos(string columna)
    {
        var ascendente = await ObtenerAsync(columna, descendente: false, estado: nameof(EstadoDocumento.Vencido));
        var descendente = await ObtenerAsync(columna, descendente: true, estado: nameof(EstadoDocumento.Vencido));

        ascendente.Should().HaveCount(TrabajadoresVencidos);
        descendente.Should().HaveCount(TrabajadoresVencidos);

        // No se compara "descendente == inverso de ascendente": el desempate
        // final es siempre ascendente por Id, deliberadamente, para que la
        // paginación sea estable. Lo que debe cumplirse es el orden de la clave.
        ClaveDe(ascendente, columna).Should().BeInAscendingOrder();
        ClaveDe(descendente, columna).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task El_filtro_de_estado_documental_quita_filas_pero_no_cambia_el_orden_pedido()
    {
        // La propiedad de fondo, y la que el defecto rompía: filtrar es
        // quedarse con una subsecuencia. Un trabajador no puede adelantar a
        // otro solo porque se haya activado un filtro de estado.
        var sinFiltro = await ObtenerAsync(nameof(TrabajadorListaDto.Nombre), descendente: false, estado: null);
        var conFiltro = await ObtenerAsync(nameof(TrabajadorListaDto.Nombre), descendente: false, estado: nameof(EstadoDocumento.Vencido));

        sinFiltro.Should().HaveCount(TrabajadoresTotales);
        conFiltro.Should().HaveCount(TrabajadoresVencidos);

        var filtrados = conFiltro.Select(t => t.Id).ToHashSet();
        conFiltro.Select(t => t.Id)
            .Should().Equal(sinFiltro.Where(t => filtrados.Contains(t.Id)).Select(t => t.Id));
    }

    [Fact]
    public async Task Ordenar_por_estado_documental_sigue_poniendo_primero_lo_que_mas_urge()
    {
        // El camino que sí funcionaba: ordenar por la columna calculada. Se
        // fija para que arreglar las otras cuatro no lo rompa.
        var elementos = await ObtenerAsync(nameof(TrabajadorListaDto.EstadoDocumental), descendente: false, estado: null);

        elementos.Should().HaveCount(TrabajadoresTotales);
        elementos.Select(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental))
            .Should().BeInAscendingOrder();
        elementos.Take(TrabajadoresVencidos)
            .Should().OnlyContain(t => t.EstadoDocumental == EstadoDocumento.Vencido);
    }

    [Fact]
    public async Task Con_filtro_de_estado_documental_la_paginacion_no_pierde_ni_repite_filas()
    {
        var recorridas = new List<Guid>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var elementos = await ObtenerAsync(
                nameof(TrabajadorListaDto.Nombre), descendente: false, estado: nameof(EstadoDocumento.Vencido),
                tamanoPagina: 2, pagina: pagina);
            recorridas.AddRange(elementos.Select(t => t.Id));
        }

        recorridas.Should().HaveCount(TrabajadoresVencidos);
        recorridas.Should().OnlyHaveUniqueItems("ninguna fila puede repetirse entre páginas");

        var deUnaVez = await ObtenerAsync(nameof(TrabajadorListaDto.Nombre), descendente: false, estado: nameof(EstadoDocumento.Vencido));
        recorridas.Should().Equal(deUnaVez.Select(t => t.Id));
    }

    [Fact]
    public async Task El_recuento_con_filtro_de_estado_documental_cuenta_todos_los_que_pasan_el_filtro()
    {
        // El total no se acota a una página de candidatos: el filtro se aplica
        // sobre todos los visibles, así que el paginador de la pantalla dice un
        // número exacto y no uno truncado.
        await using var contexto = CrearContexto();
        var resultado = await CrearHandler(contexto).Handle(
            new ObtenerTrabajadoresQuery(
                null, Pagina: 1, TamanoPagina: 2,
                OrdenarPor: nameof(TrabajadorListaDto.Nombre), Descendente: false,
                EstadoDocumental: nameof(EstadoDocumento.Vencido)),
            CancellationToken.None);

        resultado.Elementos.Should().HaveCount(2);
        resultado.TotalElementos.Should().Be(TrabajadoresVencidos);
    }

    private static IEnumerable<IComparable?> ClaveDe(IEnumerable<TrabajadorListaDto> elementos, string columna) =>
        columna switch
        {
            nameof(TrabajadorListaDto.Nombre) => elementos.Select(t => (IComparable?)t.Nombre),
            nameof(TrabajadorListaDto.Apellidos) => elementos.Select(t => (IComparable?)t.Apellidos),
            nameof(TrabajadorListaDto.Dni) => elementos.Select(t => (IComparable?)t.Dni),
            nameof(TrabajadorListaDto.EmpleadorNombre) => elementos.Select(t => (IComparable?)t.EmpleadorNombre),
            _ => throw new ArgumentOutOfRangeException(nameof(columna), columna, "Columna no cubierta por el test.")
        };

    private async Task<List<TrabajadorListaDto>> ObtenerAsync(
        string? columna, bool descendente, string? estado, int tamanoPagina = 50, int pagina = 1)
    {
        await using var contexto = CrearContexto();

        var resultado = await CrearHandler(contexto).Handle(
            new ObtenerTrabajadoresQuery(
                null, Pagina: pagina, TamanoPagina: tamanoPagina,
                OrdenarPor: columna, Descendente: descendente, EstadoDocumental: estado),
            CancellationToken.None);

        return resultado.Elementos.ToList();
    }

    private static ObtenerTrabajadoresQueryHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(contexto, contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(contexto, contexto));

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
