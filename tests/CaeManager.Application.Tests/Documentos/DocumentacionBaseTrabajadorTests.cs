using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class DocumentacionBaseTrabajadorTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 1);
    private const int Ambar = 30;
    private const int Rojo = 7;

    private static DocumentoParaDocumentacionBase Doc(string tipo, EstadoVigenciaDocumento vigencia, DateOnly? vence, DateOnly? emision = null) =>
        new(Guid.NewGuid(), tipo, vigencia, vence, emision ?? new DateOnly(2026, 1, 1));

    private static DocumentoParaDocumentacionBase Vence(string tipo, int dias, DateOnly? emision = null) =>
        Doc(tipo, EstadoVigenciaDocumento.VenceEnFecha, Hoy.AddDays(dias), emision);

    private static EstadoIndicadorBase Estado(DocumentacionBaseTrabajadorDto d, TipoDocumentoBase t) =>
        d.Indicadores.Single(i => i.Tipo == t).Estado;

    private static DocumentacionBaseTrabajadorDto Calcular(params DocumentoParaDocumentacionBase[] docs) =>
        DocumentacionBaseTrabajador.Calcular(docs, Hoy, Ambar, Rojo);

    [Fact]
    public void Siempre_hay_cuatro_indicadores_y_sin_documentos_todos_faltan()
    {
        var d = Calcular();
        d.Indicadores.Select(i => i.Tipo).Should().Equal(
            TipoDocumentoBase.AptitudMedica, TipoDocumentoBase.FormacionArt19, TipoDocumentoBase.InformacionArt18, TipoDocumentoBase.EntregaEpi);
        d.Indicadores.Should().OnlyContain(i => i.Estado == EstadoIndicadorBase.Falta && i.DocumentoId == null);
        d.AlDia.Should().BeFalse();
    }

    [Theory]
    [InlineData("Certificado de aptitud médica")]
    [InlineData("Reconocimiento médico")]
    public void Aptitud_y_Reconocimiento_medico_son_el_mismo_tipo(string nombre)
    {
        Estado(Calcular(Vence(nombre, 200)), TipoDocumentoBase.AptitudMedica).Should().Be(EstadoIndicadorBase.Vigente);
    }

    [Fact]
    public void Un_tipo_fuera_de_los_cuatro_no_cuenta()
    {
        Calcular(Vence("Formación PRL 60h", 200)).Indicadores.Should().OnlyContain(i => i.Estado == EstadoIndicadorBase.Falta);
    }

    [Theory]
    [InlineData(200, EstadoIndicadorBase.Vigente)]
    [InlineData(20, EstadoIndicadorBase.ProximoAVencer)]
    [InlineData(3, EstadoIndicadorBase.ProximoAVencer)]
    [InlineData(-1, EstadoIndicadorBase.Vencido)]
    public void El_estado_sale_de_la_calculadora_de_vigencia_existente(int dias, EstadoIndicadorBase esperado)
    {
        Estado(Calcular(Vence("Entrega de EPI", dias)), TipoDocumentoBase.EntregaEpi).Should().Be(esperado);
    }

    [Fact]
    public void Informacion_Art_18_sin_caducidad_esta_presente()
    {
        Estado(Calcular(Doc("Información Art. 18", EstadoVigenciaDocumento.NoCaduca, null)), TipoDocumentoBase.InformacionArt18)
            .Should().Be(EstadoIndicadorBase.Vigente);
    }

    [Fact]
    public void Una_vigencia_sin_confirmar_no_es_vigente_pero_cuenta_como_al_dia_con_aviso()
    {
        // Decisión del propietario (2026-10-01): el documento está; lo pendiente es su fecha.
        var d = Calcular(
            Vence("Certificado de aptitud médica", 200), Vence("Formación Art. 19", 200),
            Doc("Información Art. 18", EstadoVigenciaDocumento.NoCaduca, null),
            Doc("Entrega de EPI", EstadoVigenciaDocumento.SinConfirmar, null));
        Estado(d, TipoDocumentoBase.EntregaEpi).Should().Be(EstadoIndicadorBase.SinConfirmar);
        d.AlDia.Should().BeTrue();
        d.TieneVigenciaSinConfirmar.Should().BeTrue();
    }

    [Fact]
    public void Sin_confirmar_no_tapa_un_vencido_ni_una_falta()
    {
        var conVencido = Calcular(
            Doc("Certificado de aptitud médica", EstadoVigenciaDocumento.SinConfirmar, null), Vence("Formación Art. 19", -5),
            Doc("Información Art. 18", EstadoVigenciaDocumento.NoCaduca, null), Vence("Entrega de EPI", 200));
        var conFalta = Calcular(
            Doc("Certificado de aptitud médica", EstadoVigenciaDocumento.SinConfirmar, null), Vence("Formación Art. 19", 200),
            Doc("Información Art. 18", EstadoVigenciaDocumento.NoCaduca, null));
        conVencido.AlDia.Should().BeFalse();
        conFalta.AlDia.Should().BeFalse();
        conVencido.TieneVigenciaSinConfirmar.Should().BeTrue();
    }

    [Fact]
    public void Al_dia_son_los_cuatro_correctos_y_un_proximo_a_vencer_aun_vale()
    {
        var d = Calcular(
            Vence("Certificado de aptitud médica", 20), Vence("Formación Art. 19", 200),
            Doc("Información Art. 18", EstadoVigenciaDocumento.NoCaduca, null), Vence("Entrega de EPI", 200));
        d.AlDia.Should().BeTrue();
        d.TieneVigenciaSinConfirmar.Should().BeFalse();
    }

    [Theory]
    [InlineData("Certificado de aptitud médica")]
    [InlineData("Formación Art. 19")]
    [InlineData("Información Art. 18")]
    [InlineData("Entrega de EPI")]
    public void Basta_un_indicador_vencido_o_ausente_para_no_estar_al_dia(string malo)
    {
        var tipos = new[] { "Certificado de aptitud médica", "Formación Art. 19", "Información Art. 18", "Entrega de EPI" };
        var vencido = tipos.Select(t => Vence(t, t == malo ? -5 : 200)).ToArray();
        var ausente = tipos.Where(t => t != malo).Select(t => Vence(t, 200)).ToArray();
        Calcular(vencido).AlDia.Should().BeFalse();
        Calcular(ausente).AlDia.Should().BeFalse();
    }

    [Fact]
    public void Con_dos_documentos_del_mismo_tipo_representa_el_no_vencido_aunque_sea_mas_antiguo()
    {
        var d = Calcular(
            Vence("Entrega de EPI", 100, new DateOnly(2026, 2, 1)),
            Vence("Entrega de EPI", -30, new DateOnly(2026, 6, 1)));
        // Orden de entrada inverso al de preferencia: elegir el último o el primero daría Vencido.
        Estado(Calcular(Vence("Entrega de EPI", -30, new DateOnly(2026, 6, 1)), Vence("Entrega de EPI", 100, new DateOnly(2026, 2, 1))), TipoDocumentoBase.EntregaEpi)
            .Should().Be(EstadoIndicadorBase.Vigente);
        Estado(d, TipoDocumentoBase.EntregaEpi).Should().Be(EstadoIndicadorBase.Vigente);
    }

    [Fact]
    public void Traducir_no_promete_vigencia_ante_un_estado_no_calculable()
    {
        DocumentacionBaseTrabajador.Traducir(EstadoDocumento.Faltante).Should().Be(EstadoIndicadorBase.Falta);
        DocumentacionBaseTrabajador.Traducir((EstadoDocumento)99).Should().Be(EstadoIndicadorBase.Falta);
    }
}
