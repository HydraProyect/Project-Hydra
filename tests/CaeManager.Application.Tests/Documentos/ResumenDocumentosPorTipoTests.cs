using System.Reflection;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerResumenDocumentosPorTipo;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Lo que el resumen por Tipo de documento hace en memoria con las filas que devuelve el <c>GROUP BY</c> —montar
/// un grupo por tipo y ordenarlos— y lo que ata sus filtros a los del listado. Que cuente los Documentos correctos
/// lo mide <c>ObtenerResumenDocumentosPorTipoQueryBajoRlsTests</c> contra PostgreSQL.
/// </summary>
public class ResumenDocumentosPorTipoTests
{
    private static readonly Guid Seguro = Guid.NewGuid();
    private static readonly Guid Formacion = Guid.NewGuid();
    private static readonly Guid Itv = Guid.NewGuid();

    [Fact]
    public void Monta_un_grupo_por_tipo_con_su_total_y_su_recuento_por_estado()
    {
        var resumen = ResumenDocumentosPorTipo.Montar(
        [
            new(Seguro, "Seguro RC", ClaveDeEstado: 0, Filas: 2),
            new(Seguro, "Seguro RC", ClaveDeEstado: 4, Filas: 3),
            new(Seguro, "Seguro RC", ClaveDeEstado: 5, Filas: 1),
        ]);

        var grupo = resumen.Should().ContainSingle().Subject;
        grupo.TipoDocumentoId.Should().Be(Seguro);
        grupo.TipoDocumentoNombre.Should().Be("Seguro RC");
        grupo.Total.Should().Be(6);
        grupo.PorEstado.Should().BeEquivalentTo(new Dictionary<EstadoDocumento, int>
        {
            [EstadoDocumento.Vencido] = 2,
            [EstadoDocumento.Vigente] = 3,
            [EstadoDocumento.SinCaducidad] = 1,
        });
    }

    [Fact]
    public void Ordena_primero_el_tipo_cuyo_peor_documento_es_mas_grave()
    {
        // Llegan por nombre, como los devuelve la base. Formación solo tiene vigentes; ITV, uno urgente entre
        // vigentes; Seguro, uno vencido: el orden de salida lo decide el peor de cada tipo, no cuántos tiene.
        var resumen = ResumenDocumentosPorTipo.Montar(
        [
            new(Formacion, "Formación", ClaveDeEstado: 4, Filas: 9),
            new(Itv, "ITV", ClaveDeEstado: 1, Filas: 1),
            new(Itv, "ITV", ClaveDeEstado: 4, Filas: 5),
            new(Seguro, "Seguro RC", ClaveDeEstado: 0, Filas: 1),
        ]);

        resumen.Select(grupo => grupo.TipoDocumentoNombre).Should().Equal("Seguro RC", "ITV", "Formación");
    }

    [Fact]
    public void A_igual_gravedad_conserva_el_orden_por_nombre_con_el_que_llegan()
    {
        var resumen = ResumenDocumentosPorTipo.Montar(
        [
            new(Formacion, "Formación", ClaveDeEstado: 0, Filas: 1),
            new(Itv, "ITV", ClaveDeEstado: 4, Filas: 1),
            new(Seguro, "Seguro RC", ClaveDeEstado: 0, Filas: 4),
        ]);

        resumen.Select(grupo => grupo.TipoDocumentoNombre).Should().Equal("Formación", "Seguro RC", "ITV");
    }

    [Fact]
    public void Dos_tipos_con_el_mismo_nombre_no_se_funden()
    {
        var resumen = ResumenDocumentosPorTipo.Montar(
        [
            new(Seguro, "Seguro", ClaveDeEstado: 4, Filas: 1),
            new(Itv, "Seguro", ClaveDeEstado: 4, Filas: 2),
        ]);

        resumen.Select(grupo => (grupo.TipoDocumentoId, grupo.Total)).Should().Equal((Seguro, 1), (Itv, 2));
    }

    [Fact]
    public void Sin_filas_no_hay_ningun_grupo() =>
        ResumenDocumentosPorTipo.Montar([]).Should().BeEmpty();

    /// <summary>La clave 6 es la de lo que un Documento nunca tiene (Faltante, En tolerancia): si la base la devolviera, la clave de SQL y el vocabulario se habrían separado.</summary>
    [Fact]
    public void Una_clave_que_no_es_de_un_estado_de_documento_no_se_cuenta_en_silencio()
    {
        var montar = () => ResumenDocumentosPorTipo.Montar([new(Seguro, "Seguro RC", ClaveDeEstado: 6, Filas: 1)]);

        montar.Should().Throw<InvalidOperationException>().WithMessage("*clave de estado 6*");
    }

    [Fact]
    public void EstadoDeClave_es_la_inversa_de_ClaveOrden_para_los_estados_de_un_documento()
    {
        EstadoDocumento[] estadosDeDocumento =
        [
            EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Proximo, EstadoDocumento.SinConfirmar,
            EstadoDocumento.Vigente, EstadoDocumento.SinCaducidad,
        ];

        foreach (var estado in estadosDeDocumento)
            EstadoDocumentalFiltro.EstadoDeClave(EstadoDocumentalFiltro.ClaveOrden(estado)).Should().Be(estado);

        EstadoDocumentalFiltro.EstadoDeClave(EstadoDocumentalFiltro.ClaveOrden(EstadoDocumento.Faltante)).Should().BeNull();
        EstadoDocumentalFiltro.EstadoDeClave(EstadoDocumentalFiltro.ClaveOrden(EstadoDocumento.EnTolerancia)).Should().BeNull();
        EstadoDocumentalFiltro.EstadoDeClave(-1).Should().BeNull();
    }

    // ---------- Los filtros del resumen son los del listado ----------

    /// <summary>Lo que <see cref="ObtenerDocumentosQuery"/> lleva y NO es un filtro de vista.</summary>
    private static readonly string[] ParametrosDelListadoQueNoFiltran =
    [
        nameof(ObtenerDocumentosQuery.Pagina),
        nameof(ObtenerDocumentosQuery.TamanoPagina),
        nameof(ObtenerDocumentosQuery.OrdenarPor),
        nameof(ObtenerDocumentosQuery.Descendente),
        nameof(ObtenerDocumentosQuery.ConRecuentosPorEstado),
    ];

    /// <summary>
    /// Un filtro que se añada al listado y no al resumen haría que la cabecera de un grupo contara Documentos que
    /// al abrirlo no salen. Este test obliga a decidirlo: o el parámetro nuevo es un filtro (y va también al resumen
    /// y a <see cref="FiltrosDeDocumentos"/>), o se declara en <see cref="ParametrosDelListadoQueNoFiltran"/>.
    /// </summary>
    [Fact]
    public void El_resumen_lleva_exactamente_los_filtros_de_vista_del_listado()
    {
        var delListado = Parametros(typeof(ObtenerDocumentosQuery))
            .Where(parametro => !ParametrosDelListadoQueNoFiltran.Contains(parametro.Nombre))
            .ToList();

        ParametrosDelListadoQueNoFiltran.Should().BeSubsetOf(
            Parametros(typeof(ObtenerDocumentosQuery)).Select(parametro => parametro.Nombre),
            "control: la lista de exclusiones nombra parámetros que existen");
        delListado.Should().NotBeEmpty();
        Parametros(typeof(ObtenerResumenDocumentosPorTipoQuery)).Should().BeEquivalentTo(delListado);
        Parametros(typeof(FiltrosDeDocumentos)).Should().BeEquivalentTo(delListado);
    }

    /// <summary>Cada filtro llega a la composición compartida en su sitio: dos <c>Guid?</c> cruzados compilarían.</summary>
    [Fact]
    public void Las_dos_consultas_entregan_cada_filtro_en_su_sitio()
    {
        var trabajador = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var documento = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var plataforma = Guid.NewGuid();
        var hasta = new DateOnly(2026, 12, 31);
        EstadoDocumento[] estados = [EstadoDocumento.Urgente, EstadoDocumento.Proximo];

        var esperado = new FiltrosDeDocumentos(
            trabajador, AmbitoAplicacion.Vehiculo, "grúa", EstadoDocumento.Vencido, estados, propietario, hasta,
            documento, tipo, plataforma);

        new ObtenerDocumentosQuery(
                trabajador, AmbitoAplicacion.Vehiculo, "grúa", EstadoDocumento.Vencido, Pagina: 3, TamanoPagina: 7,
                PropietarioId: propietario, OrdenarPor: "Estado", Descendente: true, FechaVencimientoHasta: hasta,
                Estados: estados, ConRecuentosPorEstado: true, DocumentoId: documento, TipoDocumentoId: tipo,
                ProveedorPlataformaCaeId: plataforma)
            .Filtros().Should().Be(esperado);

        new ObtenerResumenDocumentosPorTipoQuery(
                TrabajadorId: trabajador, Ambito: AmbitoAplicacion.Vehiculo, Busqueda: "grúa",
                Estado: EstadoDocumento.Vencido, Estados: estados, PropietarioId: propietario,
                FechaVencimientoHasta: hasta, DocumentoId: documento, TipoDocumentoId: tipo,
                ProveedorPlataformaCaeId: plataforma)
            .Filtros().Should().Be(esperado);
    }

    /// <summary>El tipo va como texto: es lo que se compara y lo que se lee en el mensaje si el test falla.</summary>
    private sealed record Parametro(string Nombre, string Tipo);

    private static List<Parametro> Parametros(Type registro) =>
        registro.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(constructor => constructor.GetParameters().Length > 1)
            .GetParameters()
            .Select(parametro => new Parametro(parametro.Name!, parametro.ParameterType.ToString()))
            .ToList();
}
