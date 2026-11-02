using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>Las reglas de la detección, sobre palabras con posición. Con PDF reales: DivisionSobrePdfRealTests (Web.Tests).</summary>
public class DetectorTramosPdfTests
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid SubcontrataId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();
    private static readonly Guid LuisId = Guid.NewGuid();
    private static readonly Guid FurgonetaId = Guid.NewGuid();
    private static readonly Guid HaciendaId = Guid.NewGuid();
    private static readonly Guid FormacionId = Guid.NewGuid();
    private static readonly Guid ItvId = Guid.NewGuid();

    private static readonly CatalogoDivisionPdf Catalogo = new(
        new EmpresaDivisionPdf(EmpresaId, "Montajes Norte S.L.", "B12345678"),
        [new EmpresaDivisionPdf(SubcontrataId, "Pintura Sur S.L.", "B87654321")],
        [
            new TrabajadorDivisionPdf(AnaId, "Ana", "Pérez Gil", "12345678Z", EmpresaId),
            new TrabajadorDivisionPdf(LuisId, "Luis", "Mora", null, SubcontrataId),
        ],
        [new VehiculoDivisionPdf(FurgonetaId, "Furgoneta", "1234 KLM", EmpresaId)],
        [
            new TipoDocumentoDivisionPdf(HaciendaId, "Certificado de Hacienda", AmbitoAplicacion.Empresa, false, null, []),
            new TipoDocumentoDivisionPdf(FormacionId, "Formación PRL", AmbitoAplicacion.Trabajador, false, null, ["Curso de prevención"]),
            new TipoDocumentoDivisionPdf(ItvId, "ITV", AmbitoAplicacion.Vehiculo, false, null, []),
        ]);

    private static PaginaPdf Pagina(int numero, params string[] lineas)
    {
        var palabras = new List<PalabraPdf>();
        for (var i = 0; i < lineas.Length; i++)
        {
            var y = 800 - (i * 20);
            var alto = i == 0 ? 18 : 10;
            var x = 40d;
            foreach (var palabra in lineas[i].Split(' '))
            {
                palabras.Add(new PalabraPdf(palabra, x, x + 30, y, y + alto));
                x += 40;
            }
        }

        return new PaginaPdf(numero, palabras, 0);
    }

    private static IReadOnlyList<TramoDetectado> Detectar(params PaginaPdf[] paginas) =>
        DetectorTramosPdf.Detectar(paginas, paginas.Length, Catalogo);

    [Fact]
    public void Separa_documentos_de_tipo_distinto_y_propone_tipo_y_titular_con_su_motivo()
    {
        var tramos = Detectar(
            Pagina(1, "Certificado de Hacienda", "Titular Montajes Norte", "CIF B-12345678"),
            Pagina(2, "Formación PRL", "Alumna Ana Pérez Gil", "texto del curso"));

        tramos.Should().HaveCount(2);
        tramos[0].Should().BeEquivalentTo(new TramoDetectado(
            1, 1, HaciendaId, "Certificado de Hacienda", ClaseTitular.EmpresaGestionada, EmpresaId, "Montajes Norte S.L.",
            MotivoDeteccion.CifEmpresaGestionada, 1));
        tramos[1].Should().BeEquivalentTo(new TramoDetectado(
            2, 2, FormacionId, "Formación PRL", ClaseTitular.Trabajador, AnaId, "Ana Pérez Gil", MotivoDeteccion.Nombre, 2));
    }

    [Fact]
    public void Una_pagina_sin_texto_queda_sin_identificar_y_no_se_adivina()
    {
        var tramos = Detectar(
            Pagina(1, "Certificado de Hacienda", "CIF B12345678", "texto"),
            new PaginaPdf(2, [], 1),
            new PaginaPdf(3, [], 1));

        tramos.Should().HaveCount(2);
        tramos[1].Should().BeEquivalentTo(new TramoDetectado(2, 3, null, null, null, null, null, MotivoDeteccion.SinTexto, null));
    }

    [Fact]
    public void Las_paginas_que_el_lector_no_devolvio_cuentan_como_sin_texto()
    {
        var tramos = DetectorTramosPdf.Detectar([], 4, Catalogo);

        tramos.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TramoDetectado(1, 4, null, null, null, null, null, MotivoDeteccion.SinTexto, null));
    }

    [Fact]
    public void Pagina_k_de_N_continua_el_documento_aunque_cambie_el_encabezado()
    {
        var tramos = Detectar(
            Pagina(1, "Certificado de Hacienda", "Página 1 de 2", "CIF B12345678"),
            Pagina(2, "Anexo de deudas", "Página 2 de 2", "sin deudas"));

        tramos.Should().ContainSingle().Which.PaginaFinal.Should().Be(2);
    }

    [Fact]
    public void Pagina_1_de_N_abre_documento_aunque_no_se_reconozca_ningun_tipo()
    {
        var tramos = Detectar(
            Pagina(1, "Hoja cualquiera", "uno dos tres", "cuatro cinco seis"),
            Pagina(2, "Hoja cualquiera", "Página 1 de 3", "siete ocho nueve"));

        tramos.Select(t => (t.PaginaInicial, t.PaginaFinal)).Should().Equal((1, 1), (2, 2));
    }

    [Fact]
    public void Un_cambio_de_encabezado_con_titulo_destacado_abre_documento_y_sin_cambio_no()
    {
        var tramos = Detectar(
            Pagina(1, "Contrato de obra", "cláusulas del contrato", "más texto"),
            Pagina(2, "Contrato de obra", "siguen las cláusulas", "más texto"),
            Pagina(3, "Parte de entrega", "lista de material", "más texto"));

        tramos.Select(t => (t.PaginaInicial, t.PaginaFinal)).Should().Equal((1, 2), (3, 3));
    }

    [Fact]
    public void El_mismo_tipo_con_otro_titular_es_otro_documento()
    {
        var tramos = Detectar(
            Pagina(1, "Formación PRL", "Alumna Ana Pérez Gil", "texto"),
            Pagina(2, "Formación PRL", "Alumno Luis Mora", "texto"));

        tramos.Select(t => t.TitularId).Should().Equal(AnaId, LuisId);
    }

    [Fact]
    public void Dos_Trabajadores_en_el_mismo_tramo_no_se_resuelven_por_azar()
    {
        var tramo = Detectar(Pagina(1, "Formación PRL", "Asistentes Ana Pérez Gil y Luis Mora", "texto")).Single();

        tramo.TitularId.Should().BeNull();
        tramo.Motivo.Should().Be(MotivoDeteccion.VariosTitulares);
        tramo.TipoDocumentoId.Should().Be(FormacionId, "el tipo sigue siendo una sugerencia válida");
    }

    [Fact]
    public void El_DNI_gana_al_nombre_y_el_tipo_decide_la_clase_del_titular()
    {
        var tramo = Detectar(Pagina(1, "Formación PRL", "DNI 12345678-Z", "Impartido a Luis Mora", "CIF B12345678")).Single();

        tramo.Should().BeEquivalentTo(new TramoDetectado(
            1, 1, FormacionId, "Formación PRL", ClaseTitular.Trabajador, AnaId, "Ana Pérez Gil", MotivoDeteccion.Dni, 1));
    }

    [Fact]
    public void Tipo_reconocido_sin_titular_lo_dice_y_no_rellena_con_el_CIF_de_otra_clase()
    {
        var tramo = Detectar(Pagina(1, "ITV", "Inspección periódica", "CIF B12345678")).Single();

        tramo.TipoDocumentoId.Should().Be(ItvId);
        tramo.TitularId.Should().BeNull();
        tramo.Motivo.Should().Be(MotivoDeteccion.TipoSinTitular);
    }

    [Fact]
    public void Sin_tipo_lo_mas_concreto_gana_y_el_CIF_de_la_Subcontrata_se_distingue()
    {
        var matricula = Detectar(Pagina(1, "Ficha técnica", "Matrícula 1234-KLM", "CIF B12345678")).Single();
        var subcontrata = Detectar(Pagina(1, "Hoja de empresa", "Razón social", "CIF B87654321")).Single();

        matricula.Should().BeEquivalentTo(new { Clase = ClaseTitular.Vehiculo, TitularId = FurgonetaId, Motivo = MotivoDeteccion.Matricula });
        subcontrata.Should().BeEquivalentTo(new { Clase = ClaseTitular.Subcontrata, TitularId = SubcontrataId, Motivo = MotivoDeteccion.CifSubcontrata });
    }

    [Fact]
    public void Reconoce_por_alias_y_solo_en_la_zona_de_titulo()
    {
        var porAlias = Detectar(Pagina(1, "Curso de prevención", "uno dos", "tres cuatro")).Single();
        var cuerpo = Enumerable.Repeat("texto normal de relleno", 14).Append("ver certificado de Hacienda").ToArray();

        porAlias.TipoDocumentoId.Should().Be(FormacionId);
        Detectar(Pagina(1, cuerpo)).Single().TipoDocumentoId.Should().BeNull();
    }

    [Fact]
    public void Dos_tipos_empatados_no_se_desempatan()
    {
        var catalogo = Catalogo with
        {
            Tipos =
            [
                new TipoDocumentoDivisionPdf(Guid.NewGuid(), "Seguro", AmbitoAplicacion.Empresa, false, null, []),
                new TipoDocumentoDivisionPdf(Guid.NewGuid(), "Póliza", AmbitoAplicacion.Empresa, false, null, []),
            ]
        };

        var tramo = DetectorTramosPdf.Detectar([Pagina(1, "Seguro y póliza", "uno dos", "tres")], 1, catalogo).Single();

        tramo.TipoDocumentoId.Should().BeNull();
    }
}
