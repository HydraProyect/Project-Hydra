using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// PR 10 del estado materializado: la fracción de cumplimiento en contexto Empresa que el Cuadro de dirección lee de los pares
/// guardados (<see cref="LectorCumplimientoDeDireccion.Sumar"/>) es la misma que da el cálculo de siempre
/// (<see cref="ObtenerCumplimientoExigidoTenantQueryHandler.DeEmpresasPropias"/>) sobre los mismos pares, y se niega a
/// decidir cuando la tabla no es fiable.
/// </summary>
public class LectorCumplimientoDeDireccionTests
{
    private static readonly Guid PropiaA = Guid.NewGuid();
    private static readonly Guid PropiaB = Guid.NewGuid();
    private static readonly Guid CentroUno = Guid.NewGuid();
    private static readonly Guid CentroDos = Guid.NewGuid();

    private static ParDocumentalExigido Par(Guid? empresa, Guid centro, EstadoDocumento estado, bool renuncia = false) =>
        new(centro, Guid.NewGuid(), empresa, Guid.NewGuid(), Guid.NewGuid(), estado, PendienteEnPlataforma: false, EsRenuncia: renuncia);

    /// <summary>Lo que guardan las tablas de esos pares: un grupo por Empresa, Centro y estado, y el de los que están en renuncia.</summary>
    private static (List<GrupoDeParesExigidos> Grupos, List<GrupoDeParesExigidos> Renuncias) ComoTablas(IEnumerable<ParDocumentalExigido> pares)
    {
        var deEmpresa = pares.Where(p => p.EmpresaId is not null).ToList();
        GrupoDeParesExigidos Grupo(IGrouping<(Guid, Guid, EstadoDocumento), ParDocumentalExigido> g) =>
            new(g.Key.Item1, g.Key.Item2, g.Key.Item3.ComoEstadoDeRequisito(), g.Count(), 0, 0);
        return (
            deEmpresa.GroupBy(p => (p.EmpresaId!.Value, p.CentroId, p.Estado)).Select(Grupo).ToList(),
            deEmpresa.Where(p => p.EsRenuncia).GroupBy(p => (p.EmpresaId!.Value, p.CentroId, p.Estado)).Select(Grupo).ToList());
    }

    [Fact]
    public void Lo_leido_de_los_pares_guardados_da_lo_mismo_que_el_calculo_con_todos_los_estados_y_renuncias()
    {
        var estados = Enum.GetValues<EstadoDocumento>();
        var azar = new Random(20261024);
        Guid?[] empresas = [PropiaA, PropiaB, Guid.NewGuid(), null];
        Guid[] centros = [CentroUno, CentroDos, Guid.NewGuid()];
        var pares = Enumerable.Range(0, 400)
            .Select(_ => Par(
                empresas[azar.Next(empresas.Length)], centros[azar.Next(centros.Length)],
                estados[azar.Next(estados.Length)], renuncia: azar.Next(5) == 0))
            .ToList();
        var centrosPorEmpresa = new HashSet<(Guid, Guid)> { (PropiaA, CentroUno), (PropiaA, CentroDos), (PropiaB, CentroDos) };

        var (grupos, renuncias) = ComoTablas(pares);

        LectorCumplimientoDeDireccion.Sumar(grupos, renuncias, centrosPorEmpresa)
            .Should().Be(ObtenerCumplimientoExigidoTenantQueryHandler.DeEmpresasPropias(pares, centrosPorEmpresa),
                "la tabla da la misma fracción que el cálculo sobre los mismos pares");
    }

    [Fact]
    public void Una_renuncia_nunca_esta_al_dia_aunque_su_documento_valga()
    {
        var pares = new[]
        {
            Par(PropiaA, CentroUno, EstadoDocumento.Vigente),
            Par(PropiaA, CentroUno, EstadoDocumento.Vigente, renuncia: true),
            Par(PropiaA, CentroUno, EstadoDocumento.Vencido, renuncia: true),
        };
        var (grupos, renuncias) = ComoTablas(pares);

        LectorCumplimientoDeDireccion.Sumar(grupos, renuncias, new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) })
            .Should().Be(new FraccionCumplimiento(1, 3));
    }

    [Fact]
    public void Un_par_de_un_Centro_donde_la_Empresa_no_tiene_Asignacion_no_cuenta()
    {
        var (grupos, renuncias) = ComoTablas([Par(PropiaA, CentroUno, EstadoDocumento.Vigente), Par(PropiaA, CentroDos, EstadoDocumento.Faltante)]);

        LectorCumplimientoDeDireccion.Sumar(grupos, renuncias, new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) })
            .Should().Be(new FraccionCumplimiento(1, 1));
    }

    [Theory]
    [InlineData(1, 0, "una frontera ya llegada: el estado guardado es de un día anterior")]
    [InlineData(0, 1, "otra versión de la regla")]
    public void Un_grupo_que_no_es_fiable_manda_al_calculo(int vencidas, int deOtraVersion, string porque)
    {
        var grupos = new List<GrupoDeParesExigidos>
        {
            new(PropiaA, CentroUno, EstadoTalvegRequisito.Vigente, 3, 0, 0),
            new(PropiaA, CentroUno, EstadoTalvegRequisito.Urgente, 2, vencidas, deOtraVersion),
        };

        LectorCumplimientoDeDireccion.Sumar(grupos, [], new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) })
            .Should().BeNull(porque);
    }

    [Fact]
    public void Un_estado_que_no_es_el_de_un_documento_manda_al_calculo()
    {
        var grupos = new List<GrupoDeParesExigidos> { new(PropiaA, CentroUno, EstadoTalvegRequisito.NoAplica, 1, 0, 0) };

        LectorCumplimientoDeDireccion.Sumar(grupos, [], new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) }).Should().BeNull();
    }

    [Fact]
    public void Un_grupo_no_fiable_de_una_Empresa_o_Centro_que_no_cuenta_no_bloquea_la_lectura()
    {
        var grupos = new List<GrupoDeParesExigidos>
        {
            new(PropiaA, CentroUno, EstadoTalvegRequisito.Vigente, 4, 0, 0),
            new(PropiaB, CentroDos, EstadoTalvegRequisito.Vigente, 9, 9, 9),
        };

        LectorCumplimientoDeDireccion.Sumar(grupos, [], new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) })
            .Should().Be(new FraccionCumplimiento(4, 4));
    }

    [Fact]
    public async Task Sin_lector_nunca_se_lee_y_todo_se_calcula()
    {
        var sin = SinLectorDeCumplimientoDeDireccion.Instancia;

        (await sin.CumplimientoDeCentrosAsync([Guid.NewGuid()], CancellationToken.None)).Should().BeEmpty();
        (await sin.CumplimientoExigidoAsync(new HashSet<(Guid, Guid)> { (PropiaA, CentroUno) }, CancellationToken.None)).Should().BeNull();
    }
}
