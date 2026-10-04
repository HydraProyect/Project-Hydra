using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// D-7 plataforma (2026-10-04): <see cref="CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma"/> traduce un veredicto de la
/// plataforma del Cliente empresarial (vigencia vencida allí o Rechazada) en filas por Trabajador y Centro. Función pura: aquí se
/// fija quién queda bloqueado y quién no, sin base de datos.
/// </summary>
public class EvaluarPlataformaTests
{
    private static readonly DateOnly Vence = new(2026, 9, 30);
    private static readonly Guid CentroA = Guid.NewGuid();
    private static readonly Guid CentroB = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Beto = Guid.NewGuid();
    private static readonly Guid Cira = Guid.NewGuid();
    private static readonly Guid EmpresaX = Guid.NewGuid();
    private static readonly Guid EmpresaY = Guid.NewGuid();
    private static readonly Guid Tipo = Guid.NewGuid();
    private static readonly Guid Documento = Guid.NewGuid();

    private static AsignacionParaBloqueo Asignado(Guid centro, Guid trabajador, Guid? empresa) => new(centro, trabajador, empresa);

    private static AcreditacionEnPlataformaDeCentro DeTrabajador(
        Guid centro, Guid trabajador, VeredictoDePlataforma veredicto, Guid? documento = null) =>
        new(centro, documento ?? Documento, Tipo, "PSS", trabajador, null, veredicto, veredicto == VeredictoDePlataforma.Rechazada ? null : Vence);

    private static AcreditacionEnPlataformaDeCentro DeEmpresa(
        Guid centro, Guid empresa, VeredictoDePlataforma veredicto, Guid? documento = null) =>
        new(centro, documento ?? Documento, Tipo, "Certificado", null, empresa, veredicto, veredicto == VeredictoDePlataforma.Rechazada ? null : Vence);

    [Fact]
    public void Un_rechazo_de_Trabajador_bloquea_a_ese_Trabajador_en_ese_Centro_y_no_a_su_colega()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Ana, EmpresaX), Asignado(CentroA, Beto, EmpresaX)],
            [DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada)]);

        var fila = filas.Should().ContainSingle().Subject;
        fila.TrabajadorId.Should().Be(Ana);
        fila.CentroId.Should().Be(CentroA);
        fila.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
        fila.EmpresaId.Should().BeNull();
        fila.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.RechazadoPorPlataforma);
        fila.ToleranciaDias.Should().Be(0, "el portal no concede tolerancia");
        fila.DocumentoId.Should().Be(Documento, "la cola enlaza la acreditación con el Trabajador por el Documento");
    }

    [Fact]
    public void Una_vigencia_vencida_en_la_plataforma_bloquea_con_su_fecha()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Ana, EmpresaX)], [DeTrabajador(CentroA, Ana, VeredictoDePlataforma.VencidaEnPlataforma)]);

        var fila = filas.Should().ContainSingle().Subject;
        fila.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.VencidoEnPlataforma);
        fila.Resultado.VencimientoEfectivo.Should().Be(Vence);
        fila.Resultado.EnToleranciaHasta.Should().BeNull();
    }

    [Fact]
    public void Una_acreditacion_de_Empresa_bloquea_a_todos_sus_Trabajadores_del_Centro_y_a_ningun_otro()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [
                Asignado(CentroA, Ana, EmpresaX), Asignado(CentroA, Beto, EmpresaX), Asignado(CentroA, Cira, EmpresaY),
                Asignado(CentroB, Ana, EmpresaX)
            ],
            [DeEmpresa(CentroA, EmpresaX, VeredictoDePlataforma.Rechazada)]);

        filas.Select(f => f.TrabajadorId).Should().BeEquivalentTo([Ana, Beto],
            "Cira es de otra Empresa y Ana no queda bloqueada en el Centro B, donde no hay veredicto");
        filas.Should().OnlyContain(f => f.CentroId == CentroA && f.Ambito == AmbitoAplicacion.Empresa && f.EmpresaId == EmpresaX);
    }

    [Fact]
    public void Una_acreditacion_de_Empresa_sin_Trabajadores_suyos_en_el_Centro_no_bloquea_a_nadie()
    {
        CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Cira, EmpresaY)], [DeEmpresa(CentroA, EmpresaX, VeredictoDePlataforma.VencidaEnPlataforma)])
            .Should().BeEmpty();
    }

    [Fact]
    public void Un_Trabajador_sin_Asignacion_en_el_Centro_de_la_plataforma_no_queda_bloqueado()
    {
        // Solo tiene Asignación (activa) en B; la acreditación es de la plataforma de A.
        CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroB, Ana, EmpresaX)], [DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada)])
            .Should().BeEmpty();
    }

    [Fact]
    public void Una_acreditacion_sin_sujeto_no_bloquea_a_nadie()
    {
        var sinSujeto = new AcreditacionEnPlataformaDeCentro(
            CentroA, Documento, Tipo, "Otro ámbito", null, null, VeredictoDePlataforma.Rechazada, null);

        CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma([Asignado(CentroA, Ana, EmpresaX)], [sinSujeto]).Should().BeEmpty();
    }

    [Fact]
    public void El_mismo_Documento_acreditado_en_dos_canales_del_Centro_da_una_sola_fila()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Ana, EmpresaX)],
            [DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada), DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada)]);

        filas.Should().ContainSingle();
    }

    [Fact]
    public void Un_rechazo_y_una_vencida_de_documentos_distintos_son_dos_causas_del_mismo_Trabajador()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Ana, EmpresaX)],
            [
                DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada, Guid.NewGuid()),
                DeTrabajador(CentroA, Ana, VeredictoDePlataforma.VencidaEnPlataforma, Guid.NewGuid())
            ]);

        filas.Select(f => f.Resultado.Situacion).Should().BeEquivalentTo(
            [SituacionDeRequisitoBloqueante.RechazadoPorPlataforma, SituacionDeRequisitoBloqueante.VencidoEnPlataforma]);
        filas.Select(f => f.TrabajadorId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Dos_Asignaciones_del_mismo_Trabajador_al_Centro_no_duplican_la_fila()
    {
        var filas = CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(
            [Asignado(CentroA, Ana, EmpresaX), Asignado(CentroA, Ana, EmpresaX)],
            [DeTrabajador(CentroA, Ana, VeredictoDePlataforma.Rechazada)]);

        filas.Should().ContainSingle();
    }

    [Theory]
    [InlineData(SituacionDeRequisitoBloqueante.VencidoEnPlataforma)]
    [InlineData(SituacionDeRequisitoBloqueante.RechazadoPorPlataforma)]
    public void La_regla_de_bloqueo_cuenta_las_dos_situaciones_de_plataforma_como_bloqueantes(SituacionDeRequisitoBloqueante situacion) =>
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeTrue();
}
