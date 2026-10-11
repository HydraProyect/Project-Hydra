using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    public static TheoryData<EstadoDocumento?, int, int, string> EtiquetasAccesiblesPorCausa => new()
    {
        { EstadoDocumento.Vencido, 1, 0, "1 vencido. 1 documento vencido: 1 de empresa" },
        { EstadoDocumento.Faltante, 1, 0, "1 pendiente. 1 documento pendiente: 1 de empresa" },
        { null, 1, 0, "1 bloqueo de plataforma. 1 documento con bloqueo de plataforma: 1 de empresa" },
        { EstadoDocumento.Proximo, 1, 0, "1 por vencer. 1 documento próximo a vencer: 1 de empresa" },
        { EstadoDocumento.Vencido, 0, 1, "1 vencido. 1 documento vencido: 1 de trabajadores" },
        { EstadoDocumento.Faltante, 0, 1, "1 pendiente. 1 documento pendiente: 1 de trabajadores" },
        { null, 0, 1, "1 bloqueo de plataforma. 1 documento con bloqueo de plataforma: 1 de trabajadores" },
        { EstadoDocumento.Proximo, 0, 1, "1 por vencer. 1 documento próximo a vencer: 1 de trabajadores" },
        { EstadoDocumento.Vencido, 2, 3, "5 vencidos. 5 documentos vencidos: 2 de empresa y 3 de trabajadores" },
        { EstadoDocumento.Faltante, 2, 3, "5 pendientes. 5 documentos pendientes: 2 de empresa y 3 de trabajadores" },
        { null, 2, 3, "5 bloqueos de plataforma. 5 documentos con bloqueo de plataforma: 2 de empresa y 3 de trabajadores" },
        { EstadoDocumento.Proximo, 2, 3, "5 por vencer. 5 documentos próximo a vencer: 2 de empresa y 3 de trabajadores" }
    };

    [Theory]
    [MemberData(nameof(EtiquetasAccesiblesPorCausa))]
    public void La_etiqueta_accesible_fija_el_texto_y_el_reparto_y_coincide_con_el_listado(
        EstadoDocumento? estado, int deEmpresa, int deTrabajadores, string etiquetaEsperada)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var incidencias = Enumerable.Range(0, deEmpresa)
            .Select(i => Incidencia($"Documento de empresa {i}", AmbitoCausa.Empresa) with { Estado = estado })
            .Concat(Enumerable.Range(0, deTrabajadores)
                .Select(i => Incidencia($"Documento de trabajador {i}") with { Estado = estado }))
            .ToArray();
        var resumen = estado == EstadoDocumento.Proximo
            ? Resumen(id, "Centro Norte", proximas: incidencias)
            : Resumen(id, "Centro Norte", vencidas: incidencias);
        mediador.Resumenes[id] = resumen;

        var cut = Renderizar(id);
        // VentanaContexto no interactiva expone Etiqueta en el span con role=note,
        // no en la badge visible ni en el título del detalle.
        var etiquetaFicha = cut.Find(".centro360-indicadores .ventana-contexto[role='note']")
            .GetAttribute("aria-label");
        etiquetaFicha.Should().Be(etiquetaEsperada);

        using var contextoListado = new CentrosListaGen2Tests();
        var listado = contextoListado.Renderizar(resumen);
        var etiquetaListado = listado.Find(".tarjeta-fila-acordeon-indicadores .ventana-contexto[role='note'], .tarjeta-fila-acordeon-indicadores .ventana-contexto-disparador")
            .GetAttribute("aria-label");
        etiquetaListado.Should().Be(etiquetaEsperada);
        etiquetaFicha.Should().Be(etiquetaListado);
    }
    [Fact]
    public void La_ficha_usa_las_mismas_causas_del_listado_sin_rotular_los_faltantes_como_vencidos()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var resumen = Resumen(id, "Centro Norte", vencidas:
        [
            Incidencia("Seguro vencido"), Incidencia("Formación vencida"),
            Incidencia("Aptitud pendiente") with { Estado = EstadoDocumento.Faltante },
            Incidencia("Rechazo en plataforma") with { Estado = null }
        ], proximas:
        [
            Incidencia("Seguro por vencer") with { Estado = EstadoDocumento.Proximo },
            Incidencia("Formación por vencer") with { Estado = EstadoDocumento.Urgente },
            Incidencia("Aptitud por vencer") with { Estado = EstadoDocumento.Proximo }
        ]);
        mediador.Resumenes[id] = resumen;

        var cut = Renderizar(id);
        var ventanas = cut.FindAll(".centro360-indicadores .ventana-contexto");
        ventanas.Select(v => v.QuerySelector(".badge")!.TextContent.Trim()).Should()
            .Equal("2 vencidos", "1 pendiente", "1 bloqueo de plataforma", "3 por vencer");
        var grupos = new IReadOnlyList<IncidenciaCentroDto>[]
        {
            resumen.Recuentos.VencidasPorFecha, resumen.Recuentos.Pendientes,
            resumen.Recuentos.Bloqueos, resumen.Recuentos.Proximas
        };
        for (var i = 0; i < grupos.Length; i++)
            ventanas[i].QuerySelectorAll(".ventana-linea").Select(e => e.TextContent.Trim())
                .Should().Equal(grupos[i].Select(c => c.Descripcion));
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, "1 pendiente")]
    [InlineData(EstadoDocumento.Vencido, "1 vencido")]
    [InlineData(EstadoDocumento.Urgente, "1 por vencer")]
    public void Una_sola_causa_no_pinta_los_grupos_vacios(EstadoDocumento estado, string texto)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var incidencia = Incidencia("Incidencia única") with { Estado = estado };
        mediador.Resumenes[id] = estado == EstadoDocumento.Urgente
            ? Resumen(id, "Centro Norte", proximas: [incidencia])
            : Resumen(id, "Centro Norte", vencidas: [incidencia]);

        var cut = Renderizar(id);
        cut.FindAll(".centro360-indicadores .ventana-contexto").Should().ContainSingle()
            .Which.QuerySelector(".badge")!.TextContent.Trim().Should().Be(texto);
    }
}
