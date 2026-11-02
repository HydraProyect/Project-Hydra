using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

public sealed class EstadoDivisionPdfTests
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid SubcontrataId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();
    private static readonly Guid HaciendaId = Guid.NewGuid();
    private static readonly Guid FormacionId = Guid.NewGuid();
    private static readonly DateOnly Emision = new(2026, 1, 15);

    private static readonly CatalogoDivisionPdf Catalogo = new(
        new EmpresaDivisionPdf(EmpresaId, "Montajes Norte S.L.", null),
        [new EmpresaDivisionPdf(SubcontrataId, "Pintura Sur S.L.", null)],
        [new TrabajadorDivisionPdf(AnaId, "Ana", "Pérez", null, SubcontrataId)], [],
        [
            new TipoDocumentoDivisionPdf(HaciendaId, "Certificado de Hacienda", AmbitoAplicacion.Empresa, false, null, []),
            new TipoDocumentoDivisionPdf(FormacionId, "Formación PRL", AmbitoAplicacion.Trabajador, false, null, []),
        ]);

    private static readonly TramoDetectado[] Detectados =
    [
        new(1, 2, HaciendaId, "Certificado de Hacienda", ClaseTitular.EmpresaGestionada, EmpresaId, "Montajes Norte S.L.", MotivoDeteccion.CifEmpresaGestionada, 1),
        new(3, 3, FormacionId, "Formación PRL", ClaseTitular.Trabajador, AnaId, "Ana Pérez", MotivoDeteccion.Nombre, 3),
        new(4, 5, null, null, null, null, null, MotivoDeteccion.SinTexto, null),
    ];

    private static EstadoDivisionPdf Estado() => new(5, Detectados, Catalogo);

    private static string[] Claves(EstadoDivisionPdf estado) => estado.Rangos.Select(r => r.Clave).ToArray();

    [Fact]
    public void Los_tramos_detectados_son_cortes_sin_confirmar()
    {
        var estado = Estado();

        Claves(estado).Should().Equal("1-2", "3-3", "4-5");
        estado.PropuestasSinConfirmar.Should().Be(2);
        estado.CorteTras(2).Should().Be(OrigenCorte.Sugerido);
        estado.CorteTras(5).Should().BeNull("tras la última página no hay nada que cortar");
    }

    [Fact]
    public void Quitar_un_corte_y_volver_a_ponerlo_no_pierde_lo_asignado()
    {
        var estado = Estado();
        var asignacion = estado.Asignacion(new RangoPaginas(1, 2));
        asignacion.FechaEmision = Emision;

        estado.AlternarCorte(2);
        Claves(estado).Should().Equal("1-3", "4-5");
        estado.Asignacion(new RangoPaginas(1, 3)).Sugerencia.Should().BeNull("un rango nuevo no hereda ninguna sugerencia");

        estado.AlternarCorte(2);

        estado.CorteTras(2).Should().Be(OrigenCorte.Usuario);
        estado.Asignacion(new RangoPaginas(1, 2)).Should().BeSameAs(asignacion);
        asignacion.FechaEmision.Should().Be(Emision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(99)]
    public void No_se_puede_cortar_fuera_del_PDF(int pagina)
    {
        var estado = Estado();

        estado.AlternarCorte(pagina);

        Claves(estado).Should().Equal("1-2", "3-3", "4-5");
    }

    [Fact]
    public void Confirmar_las_propuestas_deja_cero_sin_confirmar_y_los_mismos_cortes()
    {
        var estado = Estado();

        estado.ConfirmarPropuestas();

        estado.PropuestasSinConfirmar.Should().Be(0);
        Claves(estado).Should().Equal("1-2", "3-3", "4-5");
    }

    [Fact]
    public void La_sugerencia_rellena_tipo_titular_y_la_Empresa_del_Trabajador()
    {
        var asignacion = Estado().Asignacion(new RangoPaginas(3, 3));

        asignacion.TipoDocumentoId.Should().Be(FormacionId);
        asignacion.TitularId.Should().Be(AnaId);
        asignacion.EmpresaDelTitularId.Should().Be(SubcontrataId);
        asignacion.CoincideConSugerencia.Should().BeTrue();
    }

    [Fact]
    public void Cambiar_Es_de_quita_el_tipo_de_otro_ambito_y_volver_a_la_sugerencia_lo_recupera()
    {
        var estado = Estado();
        var asignacion = estado.Asignacion(new RangoPaginas(3, 3));

        estado.ElegirClase(asignacion, ClaseTitular.EmpresaGestionada);

        asignacion.TitularId.Should().Be(EmpresaId, "la Empresa gestionada viene del selector y no se elige");
        asignacion.TipoDocumentoId.Should().BeNull("Formación PRL es de ámbito Trabajador");
        asignacion.CoincideConSugerencia.Should().BeFalse();

        estado.VolverASugerencia(asignacion);

        asignacion.CoincideConSugerencia.Should().BeTrue();
    }

    [Fact]
    public void Cambiar_la_Empresa_del_Trabajador_obliga_a_elegir_otro_Trabajador()
    {
        var estado = Estado();
        var asignacion = estado.Asignacion(new RangoPaginas(3, 3));

        estado.ElegirEmpresaDelTitular(asignacion, EmpresaId);

        asignacion.TitularId.Should().BeNull();
        estado.TrabajadoresDe(EmpresaId).Should().BeEmpty();
        estado.TrabajadoresDe(SubcontrataId).Select(t => t.Id).Should().Equal(AnaId);
    }

    [Fact]
    public void Solo_llega_al_alta_lo_completo_y_no_omitido()
    {
        var estado = Estado();
        estado.Asignacion(new RangoPaginas(1, 2)).FechaEmision = Emision;
        estado.Asignacion(new RangoPaginas(1, 2)).SinCaducidad = true;
        var omitido = estado.Asignacion(new RangoPaginas(3, 3));
        omitido.FechaEmision = Emision;
        omitido.Omitido = true;
        var sinTitular = estado.Asignacion(new RangoPaginas(4, 5));
        sinTitular.FechaEmision = Emision;

        sinTitular.Pendientes().Should().Equal(DatoPendiente.Titular, DatoPendiente.Tipo);
        estado.TramosACrear().Should().ContainSingle().Which.Should().Be(
            new TramoACrear(1, 2, ClaseTitular.EmpresaGestionada, EmpresaId, HaciendaId, Emision, null, true));
    }

    [Fact]
    public void Sin_fecha_de_emision_no_se_crea_aunque_haya_sugerencia()
    {
        var estado = Estado();

        estado.Asignacion(new RangoPaginas(1, 2)).Pendientes().Should().Equal(DatoPendiente.FechaEmision);
        estado.TramosACrear().Should().BeEmpty();
    }
}
