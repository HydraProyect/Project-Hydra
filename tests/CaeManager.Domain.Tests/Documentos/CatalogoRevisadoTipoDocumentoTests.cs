using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Catálogo revisado el 2026-10-17: <see cref="TipoDocumento.Archivar"/> (retirar un tipo con o sin sustituto) y los
/// datos adicionales que un tipo admite y que un <see cref="Documento"/> guarda (horas, nivel y renuncia).
/// </summary>
public class CatalogoRevisadoTipoDocumentoTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private static TipoDocumento Tipo(string nombre, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        RequisitoDocumental requerido = RequisitoDocumental.Si) =>
        new(nombre, null, false, 1, ambito, requerido, NaturalezaJuridica.RequisitoCliente, null);

    private static void SellarTenant(object entidad, Guid tenant) =>
        // El setter privado está declarado en EntidadConTenant: reflejarlo desde el tipo derivado no lo devuelve.
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);

    [Fact]
    public void Archivar_con_sustituto_retira_el_tipo_y_deja_de_pedirlo_de_serie()
    {
        var archivado = Tipo("Formación EPIs");
        var sustituto = Tipo("Entrega de EPI");

        archivado.Archivar(sustituto);

        archivado.Archivado.Should().BeTrue();
        archivado.SustitutoId.Should().Be(sustituto.Id);
        archivado.Requerido.Should().Be(RequisitoDocumental.No, "un tipo archivado nunca se pide de serie: se pide su sustituto");
    }

    [Fact]
    public void Archivar_sin_sustituto_es_valido()
    {
        var tipo = Tipo("Protocolo frente al acoso sexual y por razón de sexo", AmbitoAplicacion.Empresa);

        tipo.Archivar(null);

        tipo.Archivado.Should().BeTrue();
        tipo.SustitutoId.Should().BeNull();
    }

    [Fact]
    public void Un_tipo_no_puede_sustituirse_a_si_mismo()
    {
        var tipo = Tipo("RLC (TC1)", AmbitoAplicacion.Empresa);

        var act = () => tipo.Archivar(tipo);

        act.Should().Throw<ArgumentException>();
        tipo.Archivado.Should().BeFalse();
    }

    [Fact]
    public void El_sustituto_tiene_que_ser_del_mismo_Tenant_propietario()
    {
        var archivado = Tipo("Recibo de pago RLC/TC1", AmbitoAplicacion.Empresa);
        var deOtroTenant = Tipo("RLC (TC1)", AmbitoAplicacion.Empresa);
        SellarTenant(archivado, Guid.NewGuid());
        SellarTenant(deOtroTenant, Guid.NewGuid());

        var act = () => archivado.Archivar(deOtroTenant);

        act.Should().Throw<ArgumentException>();
        archivado.Archivado.Should().BeFalse();
    }

    [Fact]
    public void El_sustituto_tiene_que_ser_del_mismo_ambito()
    {
        var archivado = Tipo("Formación EPIs");
        var deEmpresa = Tipo("RLC (TC1)", AmbitoAplicacion.Empresa);

        var act = () => archivado.Archivar(deEmpresa);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_sustituto_no_puede_estar_archivado()
    {
        var sustitutoArchivado = Tipo("RLC/TC1 + Recibo de pago", AmbitoAplicacion.Empresa);
        sustitutoArchivado.Archivar(null);
        var archivado = Tipo("Recibo de pago RLC/TC1", AmbitoAplicacion.Empresa);

        var act = () => archivado.Archivar(sustitutoArchivado);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Los_datos_adicionales_del_tipo_descartan_bits_desconocidos()
    {
        var tipo = Tipo("Formación PRL de convenio");

        tipo.EstablecerDatosAdicionales(DatosAdicionalesDocumento.Horas | DatosAdicionalesDocumento.Nivel | (DatosAdicionalesDocumento)1024);

        tipo.DatosAdicionales.Should().Be(DatosAdicionalesDocumento.Horas | DatosAdicionalesDocumento.Nivel);
        tipo.Admite(DatosAdicionalesDocumento.Horas).Should().BeTrue();
        tipo.Admite(DatosAdicionalesDocumento.Renuncia).Should().BeFalse();
        tipo.Admite(DatosAdicionalesDocumento.Ninguno).Should().BeFalse();
    }

    private static (TipoDocumento Tipo, Documento Documento) DocumentoDe(DatosAdicionalesDocumento admite)
    {
        var tipo = Tipo("Tipo de prueba");
        tipo.EstablecerDatosAdicionales(admite);
        var documento = Documento.DeTrabajador(Guid.NewGuid(), tipo.Id, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddYears(1)),
            archivoUrl: $"archivos/{Guid.NewGuid()}.pdf");
        return (tipo, documento);
    }

    [Fact]
    public void El_documento_guarda_horas_y_nivel_que_su_tipo_admite()
    {
        var (tipo, documento) = DocumentoDe(DatosAdicionalesDocumento.Horas | DatosAdicionalesDocumento.Nivel);

        documento.EstablecerDatosAdicionales(tipo, 60, NivelFormacionPrl.Basico, esRenuncia: false);

        documento.Horas.Should().Be(60);
        documento.Nivel.Should().Be(NivelFormacionPrl.Basico);
        documento.EsRenuncia.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Las_horas_fuera_de_rango_se_rechazan(int horas)
    {
        var (tipo, documento) = DocumentoDe(DatosAdicionalesDocumento.Horas);

        var act = () => documento.EstablecerDatosAdicionales(tipo, horas, null, esRenuncia: false);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Un_dato_que_el_tipo_no_admite_se_rechaza_en_vez_de_guardarse_en_silencio()
    {
        var (tipo, documento) = DocumentoDe(DatosAdicionalesDocumento.Horas);

        var conNivel = () => documento.EstablecerDatosAdicionales(tipo, null, NivelFormacionPrl.Basico, esRenuncia: false);
        var conRenuncia = () => documento.EstablecerDatosAdicionales(tipo, null, null, esRenuncia: true);

        conNivel.Should().Throw<ArgumentException>();
        conRenuncia.Should().Throw<ArgumentException>();
        documento.Nivel.Should().BeNull();
        documento.EsRenuncia.Should().BeFalse();
    }

    [Fact]
    public void La_renuncia_solo_vale_en_un_tipo_que_la_admite()
    {
        var (tipo, documento) = DocumentoDe(DatosAdicionalesDocumento.Renuncia);

        documento.EstablecerDatosAdicionales(tipo, null, null, esRenuncia: true);

        documento.EsRenuncia.Should().BeTrue();
    }

    [Fact]
    public void Los_datos_adicionales_exigen_el_tipo_del_propio_documento()
    {
        var (_, documento) = DocumentoDe(DatosAdicionalesDocumento.Horas);
        var otroTipo = Tipo("Otro tipo");
        otroTipo.EstablecerDatosAdicionales(DatosAdicionalesDocumento.Horas);

        var act = () => documento.EstablecerDatosAdicionales(otroTipo, 8, null, esRenuncia: false);

        act.Should().Throw<ArgumentException>();
    }
}
