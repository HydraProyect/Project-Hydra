using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// R1, R2 y R3 de la decisión del propietario (2026-10-03) sobre la función pura que las aplica, sin base de datos:
/// quién queda bloqueado, en qué Centros y por qué requisito. El aislamiento entre Tenants y la carga real de datos
/// los prueba <c>CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests</c> (Integración, Postgres con RLS).
/// </summary>
public class CalculoBloqueoDeAccesoDeTrabajadoresTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    private static readonly Guid CentroA = Guid.NewGuid();
    private static readonly Guid CentroB = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Beto = Guid.NewGuid();
    private static readonly Guid EmpresaX = Guid.NewGuid();
    private static readonly Guid EmpresaY = Guid.NewGuid();
    private static readonly Guid TipoPss = Guid.NewGuid();
    private static readonly Guid TipoApto = Guid.NewGuid();
    private static readonly Guid TipoCertificadoSs = Guid.NewGuid();

    private static IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> TiposDeTrabajador(params (Guid Centro, Guid[] Tipos)[] filas) =>
        filas.ToDictionary(f => f.Centro, f => (IReadOnlySet<Guid>)f.Tipos.ToHashSet());

    private static IReadOnlySet<Guid> TiposDeEmpresa(params Guid[] tipos) => tipos.ToHashSet();

    private static DocumentoParaBloqueo DeTrabajador(Guid trabajador, Guid tipo, VigenciaDocumento vigencia) =>
        new(trabajador, null, tipo, vigencia);

    private static DocumentoParaBloqueo DeEmpresa(Guid empresa, Guid tipo, VigenciaDocumento vigencia) =>
        new(null, empresa, tipo, vigencia);

    private static IReadOnlyList<BloqueoDeAccesoDeTrabajador> Calcular(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> tiposDeTrabajador,
        IReadOnlySet<Guid> tiposDeEmpresa,
        params DocumentoParaBloqueo[] documentos) =>
        CalculoBloqueoDeAccesoDeTrabajadores.Calcular(asignaciones, tiposDeTrabajador, tiposDeEmpresa, documentos, Hoy);

    // ---------- R1: documento bloqueante de Trabajador ----------

    [Fact]
    public void R1_un_bloqueante_de_Trabajador_ausente_bloquea_a_ese_Trabajador()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa());

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.TrabajadorId.Should().Be(Ana);
        bloqueo.CentroId.Should().Be(CentroA);
        bloqueo.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente);
        bloqueo.EsAltaNueva.Should().BeTrue("no tiene ningun documento bloqueante valido: alta sin completar");
    }

    [Fact]
    public void R1_un_bloqueante_de_Trabajador_vencido_bloquea_igual_que_el_ausente()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-1))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        bloqueo.EsAltaNueva.Should().BeTrue("un vencido deja de ser valido: ningun bloqueante vale hoy");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(400)]
    public void R1_Proximo_Urgente_o_Vigente_validos_hoy_no_bloquean(int diasHastaVencer)
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(diasHastaVencer))))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_Sin_confirmar_y_No_caduca_no_bloquean_se_conserva_lo_que_hacia_Mi_trabajo()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)],
            TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.SinConfirmar),
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.NoCaduca))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_el_vencido_con_su_renovacion_valida_no_bloquea()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(-30))),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(335))))
            .Should().BeEmpty();
    }

    [Fact]
    public void R1_solo_el_Trabajador_sin_el_documento_queda_bloqueado_su_companero_no()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)],
            TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(),
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(100))));

        bloqueos.Should().ContainSingle().Which.TrabajadorId.Should().Be(Ana);
    }

    [Fact]
    public void R1_el_requisito_del_Trabajador_es_el_que_marca_la_fila_de_su_Centro()
    {
        // PSS es bloqueante solo en el Centro A: en el B, Ana no esta bloqueada por no tenerlo.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)],
            TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa());

        bloqueos.Should().ContainSingle().Which.CentroId.Should().Be(CentroA);
    }

    [Fact]
    public void R1_es_alta_nueva_solo_si_no_hay_ningun_bloqueante_valido_del_Centro()
    {
        // Ana tiene el Apto valido y le falta el PSS: sigue de alta, no es una alta nueva.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador((CentroA, [TipoPss, TipoApto])), TiposDeEmpresa(),
            DeTrabajador(Ana, TipoApto, VigenciaDocumento.VenceEl(Hoy.AddDays(60))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.TipoDocumentoId.Should().Be(TipoPss);
        bloqueo.EsAltaNueva.Should().BeFalse();
    }

    // ---------- R2: documento bloqueante de Empresa ----------

    [Fact]
    public void R2_un_bloqueante_de_Empresa_ausente_bloquea_a_todos_sus_Trabajadores_aunque_su_documentacion_personal_este_completa()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaX)],
            TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(TipoCertificadoSs),
            DeTrabajador(Ana, TipoPss, VigenciaDocumento.VenceEl(Hoy.AddDays(200))),
            DeTrabajador(Beto, TipoPss, VigenciaDocumento.NoCaduca));

        bloqueos.Should().HaveCount(2);
        bloqueos.Select(b => b.TrabajadorId).Should().BeEquivalentTo([Ana, Beto]);
        bloqueos.Should().OnlyContain(b =>
            b.Ambito == AmbitoAplicacion.Empresa
            && b.EmpresaId == EmpresaX
            && b.TipoDocumentoId == TipoCertificadoSs
            && b.Situacion == SituacionDeRequisitoBloqueante.Ausente
            && !b.EsAltaNueva);
    }

    [Fact]
    public void R2_un_bloqueante_de_Empresa_vencido_bloquea_igual_que_el_ausente()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs),
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy.AddDays(-1))));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        bloqueo.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    [Fact]
    public void R2_alcanza_a_TODOS_los_Centros_donde_el_Trabajador_esta_asignado_aunque_la_fila_sea_de_uno_solo()
    {
        // El tipo de Empresa esta marcado en el Centro A (cualquier fila del Tenant lo declara), y Ana tambien
        // esta asignada al B, donde ninguna fila del Centro lo marca: queda bloqueada en los dos.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Ana, EmpresaX)],
            TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs));

        bloqueos.Select(b => b.CentroId).Should().BeEquivalentTo([CentroA, CentroB]);
    }

    [Fact]
    public void R2_un_bloqueante_de_Empresa_valido_hoy_no_bloquea_a_nadie()
    {
        Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroB, Beto, EmpresaX)],
            TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs),
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.VenceEl(Hoy)))
            .Should().BeEmpty();
    }

    [Fact]
    public void R2_el_documento_de_una_Empresa_no_cumple_el_requisito_de_otra()
    {
        // X cumple, Y no: solo los Trabajadores de Y quedan bloqueados.
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX), new(CentroA, Beto, EmpresaY)],
            TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs),
            DeEmpresa(EmpresaX, TipoCertificadoSs, VigenciaDocumento.NoCaduca));

        var bloqueo = bloqueos.Should().ContainSingle().Subject;
        bloqueo.TrabajadorId.Should().Be(Beto);
        bloqueo.EmpresaId.Should().Be(EmpresaY);
    }

    [Fact]
    public void R2_un_documento_de_Trabajador_no_cumple_un_requisito_de_Empresa_aunque_sea_del_mismo_tipo()
    {
        // El sujeto del requisito de Empresa es la Empresa: un documento colgado del Trabajador no lo satisface.
        Calcular(
                [new(CentroA, Ana, EmpresaX)], TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs),
                DeTrabajador(Ana, TipoCertificadoSs, VigenciaDocumento.NoCaduca))
            .Should().ContainSingle().Which.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    [Fact]
    public void R2_un_Trabajador_sin_Empresa_conocida_no_queda_bloqueado_por_un_requisito_de_Empresa()
    {
        Calcular([new(CentroA, Ana, null)], TiposDeTrabajador(), TiposDeEmpresa(TipoCertificadoSs)).Should().BeEmpty();
    }

    [Fact]
    public void R1_y_R2_se_suman_el_Trabajador_aparece_por_cada_requisito_que_le_bloquea()
    {
        var bloqueos = Calcular(
            [new(CentroA, Ana, EmpresaX)],
            TiposDeTrabajador((CentroA, [TipoPss])), TiposDeEmpresa(TipoCertificadoSs));

        bloqueos.Select(b => (b.TipoDocumentoId, b.Ambito)).Should().BeEquivalentTo(
        [
            (TipoPss, AmbitoAplicacion.Trabajador),
            (TipoCertificadoSs, AmbitoAplicacion.Empresa)
        ]);
    }

    [Fact]
    public void Sin_requisitos_bloqueantes_nadie_queda_bloqueado()
    {
        Calcular([new(CentroA, Ana, EmpresaX)], TiposDeTrabajador(), TiposDeEmpresa()).Should().BeEmpty();
    }
}
