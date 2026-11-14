using CaeManager.Domain.CatalogoBase;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.CatalogoBase;

/// <summary>
/// Invariantes del catálogo base de Plataforma (B-13): catálogo global sin Tenant, códigos estables, Actividades
/// ligadas a códigos oficiales sin duplicados (CA-2), riesgos cerrados con opción «otro» (CA-4) y requisitos de base
/// con fundamento y nivel. Las entidades de prueba son inventadas: el contenido real espera al técnico habilitado (CA-7).
/// </summary>
public class CatalogoBaseTests
{
    [Theory]
    [InlineData(typeof(ActividadCatalogo))]
    [InlineData(typeof(CodigoOficialActividad))]
    [InlineData(typeof(RiesgoCatalogo))]
    [InlineData(typeof(RequisitoBase))]
    [InlineData(typeof(ReglaRiesgoRequisito))]
    public void Es_catalogo_global_sin_Tenant(Type entidad)
    {
        entidad.IsAssignableTo(typeof(Entity)).Should().BeTrue();
        entidad.IsAssignableTo(typeof(EntidadConTenant)).Should().BeFalse(
            "el catálogo base es plano de Plataforma, el mismo para todos los Tenants");
    }

    [Fact]
    public void Una_Actividad_nace_activa_y_sin_codigos()
    {
        var actividad = new ActividadCatalogo("actividad-prueba", "  Actividad de prueba  ");

        actividad.Codigo.Should().Be("actividad-prueba");
        actividad.Nombre.Should().Be("Actividad de prueba");
        actividad.Activa.Should().BeTrue();
        actividad.CodigosOficiales.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Con-Mayusculas")]
    [InlineData("con espacio")]
    [InlineData("-guion-inicial")]
    [InlineData("guion-final-")]
    [InlineData("doble--guion")]
    [InlineData("salto-de-linea-final\n")]
    public void El_codigo_es_un_slug_estable(string codigo)
    {
        var crear = () => new ActividadCatalogo(codigo, "Actividad");

        crear.Should().Throw<ArgumentException>().WithParameterName("codigo");
    }

    [Fact]
    public void El_codigo_tiene_un_tope_de_longitud()
    {
        var alTope = new string('a', ReglasCatalogoBase.LongitudMaximaCodigo);
        new ActividadCatalogo(alTope, "Actividad").Codigo.Should().Be(alTope);

        var crear = () => new ActividadCatalogo(alTope + "a", "Actividad");
        crear.Should().Throw<ArgumentException>().WithParameterName("codigo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void El_nombre_es_obligatorio(string? nombre)
    {
        var crear = () => new ActividadCatalogo("actividad", nombre!);

        crear.Should().Throw<ArgumentException>().WithParameterName("nombre");
    }

    [Fact]
    public void El_nombre_tiene_un_tope_de_longitud()
    {
        var crear = () => RiesgoCatalogo.Nuevo("riesgo", new string('n', ReglasCatalogoBase.LongitudMaximaNombre + 1));

        crear.Should().Throw<ArgumentException>().WithParameterName("nombre");
        RiesgoCatalogo.Nuevo("riesgo", new string('n', ReglasCatalogoBase.LongitudMaximaNombre)).Nombre
            .Should().HaveLength(ReglasCatalogoBase.LongitudMaximaNombre);
    }

    [Fact]
    public void Una_Actividad_se_liga_a_varios_codigos_oficiales_de_sistemas_distintos_o_del_mismo()
    {
        var actividad = new ActividadCatalogo("actividad", "Actividad");

        var cnae = actividad.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, " 00.01 ");
        actividad.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, "00.02");
        actividad.LigarCodigoOficial(SistemaCodigoOficial.Cno11, "0001");

        cnae.ActividadId.Should().Be(actividad.Id);
        cnae.Codigo.Should().Be("00.01");
        actividad.CodigosOficiales.Select(c => (c.Sistema, c.Codigo)).Should().Equal(
            (SistemaCodigoOficial.Cnae2025, "00.01"), (SistemaCodigoOficial.Cnae2025, "00.02"), (SistemaCodigoOficial.Cno11, "0001"));
    }

    [Fact]
    public void El_mismo_codigo_oficial_no_se_liga_dos_veces()
    {
        var actividad = new ActividadCatalogo("actividad", "Actividad");
        actividad.LigarCodigoOficial(SistemaCodigoOficial.Regcon, "0001");

        var repetir = () => actividad.LigarCodigoOficial(SistemaCodigoOficial.Regcon, " 0001 ");

        repetir.Should().Throw<InvalidOperationException>();
        actividad.CodigosOficiales.Should().ContainSingle();
        actividad.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, "0001").Sistema
            .Should().Be(SistemaCodigoOficial.Cnae2025, "el mismo texto en otro sistema es otro código");
    }

    [Theory]
    [InlineData("")]
    [InlineData("00-01")]
    [InlineData("00 01")]
    [InlineData("123456789012345678901")]
    public void El_codigo_oficial_solo_lleva_letras_digitos_y_puntos_con_tope(string codigo)
    {
        var actividad = new ActividadCatalogo("actividad", "Actividad");

        var ligar = () => actividad.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, codigo);

        ligar.Should().Throw<ArgumentException>().WithParameterName("codigo");
        actividad.CodigosOficiales.Should().BeEmpty();
    }

    [Fact]
    public void El_sistema_del_codigo_oficial_es_del_catalogo()
    {
        var actividad = new ActividadCatalogo("actividad", "Actividad");

        var ligar = () => actividad.LigarCodigoOficial((SistemaCodigoOficial)99, "0001");

        ligar.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("sistema");
    }

    [Fact]
    public void Una_Actividad_se_retira_y_se_reactiva_sin_borrarse()
    {
        var actividad = new ActividadCatalogo("actividad", "Actividad");

        actividad.Retirar();
        actividad.Activa.Should().BeFalse();
        actividad.Reactivar();
        actividad.Activa.Should().BeTrue();
    }

    [Fact]
    public void El_catalogo_de_riesgos_distingue_la_opcion_otro()
    {
        var riesgo = RiesgoCatalogo.Nuevo("riesgo-prueba", "Riesgo de prueba");
        var otro = RiesgoCatalogo.Otro("otro", "Otro riesgo");

        riesgo.EsOtro.Should().BeFalse();
        otro.EsOtro.Should().BeTrue();
        otro.Activo.Should().BeTrue();

        otro.Retirar();
        otro.Activo.Should().BeFalse();
    }

    [Fact]
    public void Un_requisito_de_base_lleva_fundamento_y_nivel()
    {
        var requisito = new RequisitoBase("requisito-prueba", "Requisito de prueba", "  Norma de prueba, art. 1  ", NivelExigencia.N2);

        requisito.Fundamento.Should().Be("Norma de prueba, art. 1");
        requisito.Nivel.Should().Be(NivelExigencia.N2);
        requisito.Activo.Should().BeTrue();
    }

    [Fact]
    public void Un_requisito_de_base_sin_fundamento_no_se_crea()
    {
        var crear = () => new RequisitoBase("requisito", "Requisito", " ", NivelExigencia.N1);

        crear.Should().Throw<ArgumentException>().WithParameterName("fundamento");
    }

    [Fact]
    public void El_fundamento_tiene_un_tope_de_longitud()
    {
        var crear = () => new RequisitoBase("requisito", "Requisito",
            new string('f', ReglasCatalogoBase.LongitudMaximaFundamento + 1), NivelExigencia.N1);

        crear.Should().Throw<ArgumentException>().WithParameterName("fundamento");
    }

    [Fact]
    public void El_nivel_de_un_requisito_de_base_es_del_catalogo()
    {
        var crear = () => new RequisitoBase("requisito", "Requisito", "Norma", (NivelExigencia)0);

        crear.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("nivel");
    }

    [Fact]
    public void Una_regla_liga_un_riesgo_con_un_requisito_de_base()
    {
        var riesgo = RiesgoCatalogo.Nuevo("riesgo", "Riesgo");
        var requisito = new RequisitoBase("requisito", "Requisito", "Norma", NivelExigencia.N1);

        var regla = new ReglaRiesgoRequisito(riesgo, requisito);

        regla.RiesgoId.Should().Be(riesgo.Id);
        regla.RequisitoId.Should().Be(requisito.Id);
    }
}
