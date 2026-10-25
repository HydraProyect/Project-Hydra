using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using FluentAssertions;

namespace CaeManager.Application.Tests.EstadoMaterializado;

/// <summary>
/// La comparación del cotejo nocturno: cuenta lo que falta, sobra o es distinto entre un lote del recálculo y lo guardado,
/// y da la raíz que lo rehace (diseño § 7.2 y § 13.12).
/// </summary>
public class CotejoEstadoMaterializadoTests
{
    private static readonly DateOnly Dia = new(2026, 10, 25);
    private static readonly Guid Centro = Guid.NewGuid();
    private static readonly Guid Trabajador = Guid.NewGuid();
    private static readonly Guid Empresa = Guid.NewGuid();
    private static readonly Guid Tipo = Guid.NewGuid();
    private static readonly Guid Canal = Guid.NewGuid();

    private static FilaEstadoRequisito Par(AmbitoAplicacion ambito, Guid titular, int estado = 1, DateOnly? proximo = null) =>
        new(Centro, ambito, titular, Tipo, null, (EstadoTalvegRequisito)estado, false, true, null, null, proximo);

    private static FilaEstadoEntidad Entidad(AmbitoEstadoEntidad ambito, Guid id, short estado = 0) =>
        new(ambito, id, estado, null, 0, 0, 0, 0, 0, 0, 0, 1, 1, null);

    private static FilaEstadoEnPlataforma Acreditacion(Guid titular, DateOnly? vencimiento = null) =>
        new(Centro, Canal, AmbitoAplicacion.Trabajador, titular, Tipo, default, default, vencimiento, null);

    private static CalculoEstadoMaterializado Calculo(
        IReadOnlyList<FilaEstadoRequisito>? pares = null, IReadOnlyList<FilaEstadoEntidad>? entidades = null,
        IReadOnlyList<FilaEstadoEnPlataforma>? plataforma = null) =>
        new(Dia, [Centro], [], [], [Centro], pares ?? [], entidades ?? [], plataforma ?? []);

    private static CotejoEstadoMaterializado.Guardada<T> Guardada<T>(T fila, int version = CalculoEstadoMaterializado.VersionDeLaRegla) =>
        new(fila, version);

    private static DiferenciasCotejo Comparar(
        CalculoEstadoMaterializado calculo,
        IEnumerable<CotejoEstadoMaterializado.Guardada<FilaEstadoRequisito>>? pares = null,
        IEnumerable<CotejoEstadoMaterializado.Guardada<FilaEstadoEnPlataforma>>? plataforma = null,
        IEnumerable<CotejoEstadoMaterializado.Guardada<FilaEstadoEntidad>>? entidades = null)
    {
        var diferencias = new DiferenciasCotejo();
        CotejoEstadoMaterializado.CompararLote(calculo, pares ?? [], plataforma ?? [], entidades ?? [], diferencias);
        return diferencias;
    }

    [Fact]
    public void Lo_guardado_igual_al_recalculo_no_da_diferencias()
    {
        var par = Par(AmbitoAplicacion.Trabajador, Trabajador);
        var entidad = Entidad(AmbitoEstadoEntidad.Centro, Centro);
        var acreditacion = Acreditacion(Trabajador);

        var diferencias = Comparar(Calculo([par], [entidad], [acreditacion]), [Guardada(par)], [Guardada(acreditacion)], [Guardada(entidad)]);

        diferencias.Total.Should().Be(0);
        diferencias.Raices.EstaVacia.Should().BeTrue();
    }

    [Fact]
    public void Un_par_distinto_cuenta_como_distinto_y_lo_rehace_su_Trabajador()
    {
        var calculado = Par(AmbitoAplicacion.Trabajador, Trabajador, estado: 2);
        var guardado = Par(AmbitoAplicacion.Trabajador, Trabajador, estado: 1);

        var diferencias = Comparar(Calculo([calculado]), [Guardada(guardado)]);

        diferencias.Pares.Distintas.Should().Be(1);
        diferencias.Total.Should().Be(1);
        diferencias.Raices.TrabajadorIds.Should().Equal(Trabajador);
        diferencias.Raices.CentroIds.Should().BeEmpty();
    }

    [Fact]
    public void Un_ProximoCambioEl_distinto_tambien_es_una_diferencia()
    {
        var calculado = Par(AmbitoAplicacion.Trabajador, Trabajador, proximo: Dia.AddDays(3));
        var guardado = Par(AmbitoAplicacion.Trabajador, Trabajador, proximo: Dia.AddDays(4));

        Comparar(Calculo([calculado]), [Guardada(guardado)]).Pares.Distintas.Should().Be(1);
    }

    [Fact]
    public void Una_fila_escrita_con_otra_version_de_la_regla_es_distinta_aunque_sus_columnas_coincidan()
    {
        var par = Par(AmbitoAplicacion.Trabajador, Trabajador);

        Comparar(Calculo([par]), [Guardada(par, version: CalculoEstadoMaterializado.VersionDeLaRegla - 1)])
            .Pares.Distintas.Should().Be(1);
    }

    [Fact]
    public void Lo_que_falta_y_lo_que_sobra_se_cuentan_aparte()
    {
        var falta = Par(AmbitoAplicacion.Trabajador, Trabajador);
        var sobra = Par(AmbitoAplicacion.Empresa, Empresa);

        var diferencias = Comparar(Calculo([falta]), [Guardada(sobra)]);

        diferencias.Pares.Faltan.Should().Be(1);
        diferencias.Pares.Sobran.Should().Be(1);
        diferencias.Raices.TrabajadorIds.Should().Equal(Trabajador);
        diferencias.Raices.EmpresaIds.Should().Equal(Empresa);
    }

    [Fact]
    public void Un_par_de_otro_ambito_lo_rehace_su_Centro()
    {
        var proyecto = Guid.NewGuid();
        var diferencias = Comparar(Calculo([Par(AmbitoAplicacion.Proyecto, proyecto)]));

        diferencias.Pares.Faltan.Should().Be(1);
        diferencias.Raices.CentroIds.Should().Equal(Centro);
        diferencias.Raices.TrabajadorIds.Should().BeEmpty();
    }

    [Fact]
    public void Una_acreditacion_distinta_la_rehace_su_Centro()
    {
        var diferencias = Comparar(
            Calculo(plataforma: [Acreditacion(Trabajador, Dia.AddDays(10))]),
            plataforma: [Guardada(Acreditacion(Trabajador, Dia.AddDays(9)))]);

        diferencias.Plataforma.Distintas.Should().Be(1);
        diferencias.Raices.CentroIds.Should().Equal(Centro);
    }

    [Fact]
    public void Una_fila_de_entidad_distinta_la_rehace_su_entidad_y_una_Subcontrata_como_Empresa()
    {
        var subcontrata = Guid.NewGuid();
        var diferencias = Comparar(
            Calculo(entidades: [Entidad(AmbitoEstadoEntidad.Trabajador, Trabajador, estado: 2)]),
            entidades: [Guardada(Entidad(AmbitoEstadoEntidad.Trabajador, Trabajador, estado: 1)), Guardada(Entidad(AmbitoEstadoEntidad.Subcontrata, subcontrata))]);

        diferencias.Entidades.Distintas.Should().Be(1);
        diferencias.Entidades.Sobran.Should().Be(1);
        diferencias.Raices.TrabajadorIds.Should().Equal(Trabajador);
        diferencias.Raices.EmpresaIds.Should().Equal(subcontrata);
    }

    [Fact]
    public void Las_diferencias_de_varios_lotes_se_suman_y_las_raices_no_se_repiten()
    {
        var diferencias = new DiferenciasCotejo();
        var par = Par(AmbitoAplicacion.Trabajador, Trabajador);
        CotejoEstadoMaterializado.CompararLote(Calculo([par]), [], [], [], diferencias);
        CotejoEstadoMaterializado.CompararLote(Calculo([par]), [], [], [], diferencias);
        diferencias.Huerfanas = 2;

        diferencias.Pares.Faltan.Should().Be(2);
        diferencias.Total.Should().Be(4);
        diferencias.Raices.Cuantas.Should().Be(1);
    }

    [Fact]
    public void El_resumen_de_la_alerta_lleva_solo_recuentos()
    {
        var diferencias = Comparar(Calculo([Par(AmbitoAplicacion.Trabajador, Trabajador)]));

        var texto = diferencias.ToString();

        texto.Should().Contain("1 faltan");
        texto.Should().NotContain(Trabajador.ToString());
        texto.Should().NotContain(Centro.ToString());
    }
}
