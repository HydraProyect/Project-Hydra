using CaeManager.Application.Plantillas;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Defecto 2026-09-08 (cierre del vacío-por-filtro, PR #507): el badge de la
/// pestaña "Generados" notificaba <c>_documentosGenerados.Count</c> —la
/// lista YA filtrada del panel— así que aplicar un filtro hacía bajar un
/// número que Plantillas TALVEG.dc.html define como "N documentos generados
/// con avisos, pendientes de revisar", ajeno al filtro de la tabla. Este
/// handler es la mitad de Application del arreglo: no acepta ningún filtro,
/// así que estructuralmente no puede reproducir ese defecto — la otra mitad
/// (que el panel deje de mandar el conteo filtrado) vive en
/// <see cref="CaeManager.Web.Tests.DocumentosGeneradosPanelVacioPorFiltroTests"/>.
/// </summary>
public class ObtenerTotalDocumentosGeneradosConAvisosQueryHandlerTests
{
    private static PlantillaDocumentoVersion CrearVersion(PlantillasQueryContextFalso contexto)
    {
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        return version;
    }

    private readonly DocumentosQueryContextFalso _documentos = new();

    private ObtenerTotalDocumentosGeneradosConAvisosQueryHandler Handler(PlantillasQueryContextFalso contexto, AlcanceDatosServiceFalso alcance) =>
        new(contexto, _documentos, new ProyectosQueryContextFalso(), alcance);

    /// <summary>Con su Documento real (legible), para que solo decidan las coordenadas del registro.</summary>
    private DocumentoGenerado Generado(
        Guid versionId, EstadoDocumentoGenerado estado, Guid? trabajadorId = null, Guid? empresaId = null, Guid? centroId = null)
    {
        var documento = trabajadorId is { } trabajador
            ? Documento.DeTrabajador(trabajador, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf")
            : Documento.DeEmpresa(empresaId ?? Guid.NewGuid(), Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new(versionId, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorId, empresaId: empresaId,
            centroId: centroId, conAvisos: estado == EstadoDocumentoGenerado.GeneradoConAvisos);
    }

    /// <summary>Generado con avisos desde una plantilla de ámbito Cliente sin Centro: sin Trabajador, Empresa ni Centro.</summary>
    private DocumentoGenerado GeneradoDeClienteEmpresarial(Guid versionId, Guid clienteEmpresarialId)
    {
        var documento = Documento.DeCliente(clienteEmpresarialId, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new(versionId, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, conAvisos: true);
    }

    [Fact]
    public async Task Cuenta_solo_los_generados_con_avisos()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.Generado),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso()).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(2);
    }

    [Fact]
    public async Task Sin_avisos_devuelve_cero()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.Add(Generado(version.Id, EstadoDocumentoGenerado.Generado));

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso()).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(0);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_datos_del_usuario()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var trabajadorVisible = Guid.NewGuid();
        var trabajadorAjeno = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajadorVisible),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajadorAjeno),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorVisible]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    [Fact]
    public async Task No_cuenta_el_generado_sin_Trabajador_ni_Empresa_de_un_Cliente_empresarial_fuera_de_la_Cartera()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var clienteDeCartera = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoDeClienteEmpresarial(version.Id, clienteDeCartera),
            GeneradoDeClienteEmpresarial(version.Id, Guid.NewGuid()),
        ]);
        var gestorConCarteraLimitada = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteDeCartera]);

        var resultado = await Handler(contexto, gestorConCarteraLimitada).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    /// <summary>
    /// Hallazgo de Codex (ronda 2): el Trabajador y su Documento están en la Cartera, y lo único que deja fuera el
    /// registro es su Centro. Si el contador dejara de mirar el Centro, este caso contaría dos.
    /// </summary>
    [Fact]
    public async Task No_cuenta_el_generado_de_un_Centro_fuera_de_la_Cartera_aunque_su_Documento_sea_legible()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var trabajador = Guid.NewGuid();
        var centroDeCartera = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, centroId: centroDeCartera),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, centroId: Guid.NewGuid()),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    /// <summary>
    /// Revisión de Codex (ronda 2 de la PR #2222, baja): en el caso de arriba el recorte en SQL ya quita el registro del
    /// Centro ajeno, así que no prueba que la puerta final mire el Centro. Este va directo a la puerta, sin recorte
    /// previo: si <see cref="AlcanceDeDocumentosGenerados.DocumentosGeneradosVisiblesAsync"/> dejara de mirar el Centro,
    /// daría los dos.
    /// </summary>
    [Fact]
    public async Task La_puerta_final_rechaza_por_si_misma_el_generado_de_un_Centro_fuera_de_la_Cartera()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var trabajador = Guid.NewGuid();
        var centroDeCartera = Guid.NewGuid();
        var dentro = Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, centroId: centroDeCartera);
        var fuera = Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, centroId: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador], centroIdsVisibles: [centroDeCartera]);

        var visibles = await alcance.DocumentosGeneradosVisiblesAsync(
            [Coordenadas(dentro), Coordenadas(fuera)], contexto, _documentos, new ProyectosQueryContextFalso(), CancellationToken.None);

        visibles.Should().BeEquivalentTo([dentro.Id]);
    }

    private static CoordenadasDocumentoGenerado Coordenadas(DocumentoGenerado d) =>
        new(d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId);

    /// <summary>
    /// Revisión de Codex (ronda 2 de la PR #2222, media): el contador ya no es una consulta propia, sino la función de
    /// la lista. En un universo mixto (dentro y fuera de la Cartera por Trabajador, Empresa, Centro, plantilla y
    /// Documento, con y sin avisos), el contador da exactamente los «con avisos» que la lista enseña.
    /// </summary>
    [Fact]
    public async Task El_contador_coincide_con_los_con_avisos_que_ensena_la_lista_en_un_universo_mixto()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var plantillaAjena = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha ajena", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual,
            Guid.NewGuid(), clienteId: Guid.NewGuid());
        var versionAjena = new PlantillaDocumentoVersion(plantillaAjena.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantillaAjena);
        contexto.ListaPlantillasDocumentoVersion.Add(versionAjena);
        var (trabajador, trabajadorAjeno, empresa, centro) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var clienteDeCartera = Guid.NewGuid();
        var sinDocumento = Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador);
        _documentos.ListaDocumentos.RemoveAll(d => d.Id == sinDocumento.DocumentoId);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, empresaId: empresa, centroId: centro),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador),
            Generado(version.Id, EstadoDocumentoGenerado.Generado, trabajadorId: trabajador, centroId: centro),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajadorAjeno),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, empresaId: Guid.NewGuid()),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, centroId: Guid.NewGuid()),
            Generado(versionAjena.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador),
            GeneradoDeClienteEmpresarial(version.Id, clienteDeCartera),
            GeneradoDeClienteEmpresarial(version.Id, Guid.NewGuid()),
            sinDocumento,
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteDeCartera], trabajadorIdsVisibles: [trabajador],
            empresaIdsVisibles: [empresa], centroIdsVisibles: [centro]);
        var lista = await new ObtenerDocumentosGeneradosQueryHandler(contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(),
                _documentos, new ProyectosQueryContextFalso(), alcance)
            .Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        var contador = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        var conAvisosEnLaLista = lista.Count(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);
        conAvisosEnLaLista.Should().Be(3, "dentro quedan el de Empresa y Centro de la Cartera, el solo de Trabajador y el del Cliente empresarial");
        contador.Should().Be(conAvisosEnLaLista);
    }

    /// <summary>Lo mismo con la Empresa: Trabajador y Documento en la Cartera, Empresa del registro fuera.</summary>
    [Fact]
    public async Task No_cuenta_el_generado_de_una_Empresa_fuera_de_la_Cartera_aunque_su_Documento_sea_legible()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        var trabajador = Guid.NewGuid();
        var empresaDeCartera = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, empresaId: empresaDeCartera),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador, empresaId: Guid.NewGuid()),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador], empresaIdsVisibles: [empresaDeCartera]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    [Fact]
    public async Task No_cuenta_el_generado_de_una_plantilla_atada_a_un_Cliente_empresarial_fuera_de_la_Cartera()
    {
        var contexto = new PlantillasQueryContextFalso();
        var versionGeneral = CrearVersion(contexto);
        var plantillaAjena = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha ajena", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual,
            Guid.NewGuid(), clienteId: Guid.NewGuid());
        var versionAjena = new PlantillaDocumentoVersion(plantillaAjena.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantillaAjena);
        contexto.ListaPlantillasDocumentoVersion.Add(versionAjena);
        var trabajador = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            Generado(versionGeneral.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador),
            Generado(versionAjena.Id, EstadoDocumentoGenerado.GeneradoConAvisos, trabajadorId: trabajador),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], trabajadorIdsVisibles: [trabajador]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(1);
    }

    [Fact]
    public async Task Con_acceso_total_cuenta_todos_los_generados_con_avisos()
    {
        var contexto = new PlantillasQueryContextFalso();
        var version = CrearVersion(contexto);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoDeClienteEmpresarial(version.Id, Guid.NewGuid()),
            Generado(version.Id, EstadoDocumentoGenerado.GeneradoConAvisos),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true))
            .Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);

        resultado.Should().Be(2);
    }
}
