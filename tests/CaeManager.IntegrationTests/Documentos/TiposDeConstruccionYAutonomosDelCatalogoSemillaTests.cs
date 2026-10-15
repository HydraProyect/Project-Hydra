using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Congela los tres tipos reales que se añadieron al catálogo semilla el
/// 2026-10-15 por decisión del propietario: el Certificado de inscripción en
/// el REA, el Libro de Subcontratación y el Recibo de cuota de autónomos
/// (RETA). Los identificadores y nombres exactos los consume el sembrador del
/// piloto, así que un cambio accidental aquí rompería a quien los referencie.
/// </summary>
public class TiposDeConstruccionYAutonomosDelCatalogoSemillaTests
{
    private const string NombreRea = "Certificado de inscripción en el REA";
    private const string NombreLibro = "Libro de Subcontratación";
    private const string NombreReta = "Recibo de cuota de autónomos (RETA)";

    private static readonly Guid IdRea = new("80000000-0000-0000-0000-000000000001");
    private static readonly Guid IdLibro = new("80000000-0000-0000-0000-000000000002");
    private static readonly Guid IdReta = new("80000000-0000-0000-0000-000000000003");

    private static TipoDocumento Copia(string nombre) =>
        TipoDocumentoSeedData.CrearCopiasParaTenant().Single(t => t.Nombre == nombre);

    [Fact]
    public void Los_identificadores_y_nombres_exactos_estan_en_el_catalogo()
    {
        var porId = TipoDocumentoSeedData.Datos.ToDictionary(d => d.Id, d => d.Nombre);

        porId[IdRea].Should().Be(NombreRea);
        porId[IdLibro].Should().Be(NombreLibro);
        porId[IdReta].Should().Be(NombreReta);
    }

    [Fact]
    public void El_REA_es_de_Empresa_con_validez_de_tres_anios_y_solo_condicional_a_obra_de_construccion()
    {
        var rea = Copia(NombreRea);

        rea.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Empresa);
        rea.VigenciaMeses.Should().Be(36);
        rea.AplicaVencimientoAutomatico.Should().BeTrue();
        rea.Requerido.Should().Be(RequisitoDocumental.Condicional,
            "solo aplica a construcción: no puede contar para el cumplimiento de quien no pisa una obra");
        rea.CuentaParaCumplimiento.Should().BeFalse();
        rea.Naturaleza.Should().Be(NaturalezaJuridica.ObligacionCondicionada);
        rea.Sensibilidad.Should().Be(SensibilidadDocumental.SinDatosPersonales);
    }

    [Fact]
    public void El_Libro_de_Subcontratacion_es_de_Proyecto_sin_caducidad_y_condicional_a_obra_de_construccion()
    {
        var libro = Copia(NombreLibro);

        libro.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Proyecto);
        libro.VigenciaMeses.Should().BeNull();
        libro.AplicaVencimientoAutomatico.Should().BeFalse();
        libro.Requerido.Should().Be(RequisitoDocumental.Condicional);
        libro.CuentaParaCumplimiento.Should().BeFalse();
        libro.Naturaleza.Should().Be(NaturalezaJuridica.ObligacionCondicionada);
        libro.Sensibilidad.Should().Be(SensibilidadDocumental.DatosPersonales);
    }

    [Fact]
    public void El_recibo_de_RETA_es_de_Empresa_mensual_y_opcional()
    {
        var reta = Copia(NombreReta);

        reta.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Empresa);
        reta.VigenciaMeses.Should().Be(1);
        reta.AplicaVencimientoAutomatico.Should().BeTrue();
        reta.Requerido.Should().Be(RequisitoDocumental.No, "no se marca obligatorio por defecto para nadie");
        reta.CuentaParaCumplimiento.Should().BeFalse();
        reta.Naturaleza.Should().Be(NaturalezaJuridica.RequisitoCliente,
            "no se ha verificado contra fuente oficial que ninguna norma lo exija como documento CAE");
        reta.Sensibilidad.Should().Be(SensibilidadDocumental.DatosPersonales);
    }

    [Fact]
    public void Ninguno_de_los_tres_cuenta_para_el_cumplimiento_por_defecto()
    {
        var flags = TipoDocumentoSeedData.Datos
            .Where(d => d.Id == IdRea || d.Id == IdLibro || d.Id == IdReta)
            .Select(d => d.EsObligatorio);

        flags.Should().HaveCount(3).And.OnlyContain(esObligatorio => !esObligatorio);
    }

    [Fact]
    public void El_orden_dentro_de_cada_ambito_del_catalogo_no_se_repite()
    {
        var repetidos = TipoDocumentoSeedData.Datos
            .GroupBy(d => (d.Ambito, d.Orden))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Ambito}/{g.Key.Orden}: {string.Join(", ", g.Select(x => x.Nombre))}")
            .ToList();

        repetidos.Should().BeEmpty();
    }
}
