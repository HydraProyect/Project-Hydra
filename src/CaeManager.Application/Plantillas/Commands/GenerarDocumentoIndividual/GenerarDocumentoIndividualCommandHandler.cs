using System.Text.Json;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;

/// <summary>
/// No encola detección/verificación IA (el contenido lo escribe Hydra desde
/// datos ya conocidos, no hace falta releerlo) ni deriva canales de
/// acreditación de plataforma todavía — a diferencia de
/// <c>CrearDocumentoCommandHandler</c>, que sí hace ambas cosas para un PDF
/// subido a mano. Queda como mejora pendiente, no como omisión silenciosa:
/// sin ella, un documento generado por plantilla no aparece con acreditación
/// pendiente aunque su TipoDocumento la requiera.
/// </summary>
public class GenerarDocumentoIndividualCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository plantillaRepositorio,
    IDocumentoRepository documentoRepositorio,
    IDocumentoGeneradoRepository documentoGeneradoRepositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    IContactosAgendaQueryContext contactosContext,
    IRellenadorPlantillaPdfService rellenador,
    IFileStorageService almacenamientoArchivos,
    ICurrentUserService usuarioActual,
    IAlcanceDatosService alcanceDatos,
    IAsignacionRepository asignacionRepositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GenerarDocumentoIndividualCommand, Result<GenerarDocumentoIndividualResultadoDto>>
{
    public async Task<Result<GenerarDocumentoIndividualResultadoDto>> Handle(
        GenerarDocumentoIndividualCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return Fallo("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla.");
        if (version.EstadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada)
            return Fallo("Plantilla.VersionNoConfirmada", "Esta versión de plantilla todavía no está confirmada.");
        if (string.IsNullOrWhiteSpace(version.ArchivoOriginalUrl))
            return Fallo("Plantilla.SinArchivoOriginal", "Esta versión de plantilla no tiene un PDF original.");

        var plantilla = await plantillaRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (plantilla is null)
            return Fallo("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión.");

        // Solo Trabajador/Cliente/Empresa: los únicos ámbitos que ofrece el
        // alta de plantillas (ConfigurarPlantilla.razor) — Vehículo/Proyecto
        // son válidos en Documento, pero esta generación no resuelve
        // contexto para ellos (sin caso de uso CAE detrás, ver ADR-010 § 1.1).
        if (plantilla.AmbitoAplicacion is not (AmbitoAplicacion.Trabajador or AmbitoAplicacion.Cliente or AmbitoAplicacion.Empresa))
            return Fallo("Plantilla.AmbitoNoSoportado", "La generación de documentos solo soporta plantillas de Trabajador, Cliente o Empresa.");

        // Cierre de IDOR (auditoría de seguridad del módulo, 2026-08-30): la
        // existencia por tenant no basta — un operador podía generar
        // documentos para un propietario fuera de su cartera con solo
        // conocer/adivinar el Guid (mismo hallazgo que motivó
        // AlcanceDatosServiceExtensions en las consultas *PorId*, ADR-010
        // § 1.2 ya señalaba IAlcanceDatosService como reutilizable aquí).
        var propietarioVisible = plantilla.AmbitoAplicacion switch
        {
            AmbitoAplicacion.Trabajador => await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == request.OwnerId, cancellationToken)
                && await alcanceDatos.TrabajadorVisibleAsync(request.OwnerId, cancellationToken),
            AmbitoAplicacion.Cliente => await empresasContext.Empresas.AnyAsync(c => c.Id == request.OwnerId, cancellationToken)
                && await alcanceDatos.ClienteVisibleAsync(request.OwnerId, cancellationToken),
            _ => await empresasContext.Empresas.AnyAsync(e => e.Id == request.OwnerId, cancellationToken)
                && await alcanceDatos.EmpresaVisibleAsync(request.OwnerId, cancellationToken)
        };
        if (!propietarioVisible)
            return Fallo("Plantilla.PropietarioNoEncontrado", "No encontramos a quién pertenece este documento.");

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == plantilla.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Fallo("Plantilla.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento de esta plantilla.");

        // Resuelto ANTES de generar/guardar el blob (auditoría de seguridad
        // del módulo, 2026-08-30): un fallo aquí después de GuardarAsync
        // dejaba un PDF huérfano en el almacenamiento — nunca referenciado
        // por ningún Documento porque el fallo aborta el SaveChangesAsync
        // que lo habría anclado.
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Fallo("Plantilla.SinUsuarioActual", "No pudimos identificar quién genera este documento.");

        var trabajadorId = plantilla.AmbitoAplicacion == AmbitoAplicacion.Trabajador ? request.OwnerId : (Guid?)null;
        var empresaIdDirecta = plantilla.AmbitoAplicacion == AmbitoAplicacion.Empresa ? request.OwnerId : (Guid?)null;
        var clienteIdDirecto = plantilla.AmbitoAplicacion == AmbitoAplicacion.Cliente ? request.OwnerId : (Guid?)null;

        // La plantilla puede estar acotada a un Centro/Cliente concreto
        // (ADR-010 § 2.9) — generar a través de ella con un CentroId distinto
        // mezclaría datos de un ámbito BPO con el formulario de otro.
        if (plantilla.CentroId is { } centroIdDePlantilla && request.CentroId != centroIdDePlantilla)
            return Fallo("Plantilla.CentroNoEncontrado", "No encontramos este centro.");

        Centro? centro = null;
        if (request.CentroId is { } centroId)
        {
            centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == centroId, cancellationToken);
            if (centro is null || !await alcanceDatos.CentroVisibleAsync(centroId, cancellationToken))
                return Fallo("Plantilla.CentroNoEncontrado", "No encontramos este centro.");

            // Defensa en profundidad para el ámbito Trabajador: el propietario
            // ya se comprobó visible por cartera, pero sin esto un Trabajador
            // visible de OTRO centro podía combinarse con este CentroId — la
            // plantilla acabaría rellenando datos del centro equivocado.
            if (trabajadorId is { } idTrabajadorParaCentro
                && !await asignacionRepositorio.ExisteActivaAsync(idTrabajadorParaCentro, centroId, cancellationToken))
                return Fallo("Plantilla.TrabajadorSinAsignacionEnCentro", "Este trabajador no tiene una asignación activa en este centro.");
        }

        if (plantilla.ClienteId is { } clienteIdDePlantilla
            && clienteIdDePlantilla != (clienteIdDirecto ?? centro?.ClienteId))
            return Fallo("Plantilla.PropietarioNoEncontrado", "No encontramos a quién pertenece este documento.");

        var empresaId = empresaIdDirecta ?? centro?.EmpresaId;
        var clienteId = clienteIdDirecto ?? centro?.ClienteId;

        var trabajador = trabajadorId is { } tId
            ? await trabajadoresContext.Trabajadores.FirstOrDefaultAsync(t => t.Id == tId, cancellationToken)
            : null;
        var empresa = empresaId is { } eId
            ? await empresasContext.Empresas.FirstOrDefaultAsync(e => e.Id == eId, cancellationToken)
            : null;
        var cliente = clienteId is { } clId
            ? await empresasContext.Empresas.FirstOrDefaultAsync(c => c.Id == clId, cancellationToken)
            : null;

        var contactosPorRol = empresaId is { } idParaContactos
            ? await ResolverContactosPorRolAsync(idParaContactos, cancellationToken)
            : new Dictionary<RolContacto, string>();

        var ahoraUtc = DateTime.UtcNow;
        var valoresPorElemento = version.Elementos
            .Where(e => e.Tipo != TipoElementoPlantilla.Firma)
            .ToDictionary(e => e, e => ResolverValor(e, request.ValoresManuales, empresa, trabajador, centro, cliente, contactosPorRol, ahoraUtc));

        using var flujoOriginal = await almacenamientoArchivos.AbrirAsync(version.ArchivoOriginalUrl, cancellationToken);
        using var memoria = new MemoryStream();
        await flujoOriginal.CopyToAsync(memoria, cancellationToken);
        var contenidoOriginal = memoria.ToArray();

        var elementosRelleno = valoresPorElemento
            .Select(par => new ElementoRellenoPlantilla(
                par.Key.Tipo, par.Key.NombreCampoAcroForm, par.Key.Pagina, par.Key.X, par.Key.Y, par.Key.Ancho, par.Key.Alto, par.Value,
                ElementoId: par.Key.Id))
            .ToList();

        var resultadoRelleno = rellenador.Rellenar(contenidoOriginal, plantilla.FormatoOrigen, elementosRelleno);
        var contenidoRelleno = resultadoRelleno.Pdf;

        using var flujoRelleno = new MemoryStream(contenidoRelleno);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujoRelleno, "documento-generado.pdf", cancellationToken);

        var fechaEmision = DateOnly.FromDateTime(ahoraUtc);
        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(fechaEmision, tipoDocumento.VigenciaMeses)
            : null;

        var documento = plantilla.AmbitoAplicacion switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl),
            AmbitoAplicacion.Cliente => Documento.DeCliente(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl),
            _ => Documento.DeEmpresa(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl)
        };
        documentoRepositorio.Agregar(documento);

        var datosUtilizadosJson = JsonSerializer.Serialize(
            valoresPorElemento.ToDictionary(par => par.Key.EtiquetaVisible, par => par.Value));

        // DEC-5 (propietario, 2026-09-02): "generar con aviso visible; bloquear
        // rompe lotes enteros por un campo". Se recorre version.Elementos y no
        // el diccionario para que el aviso salga en el orden de la plantilla
        // ante los mismos datos, en vez de en el que decida un Dictionary.
        //
        // Las firmas quedan fuera A PROPÓSITO, no por descuido: la firma no es
        // un valor que esta generación resuelva — se estampa después, con
        // IEstampadoFirmaEnCampoPdfService (ADR-010 § 2.7). Un elemento Firma
        // marcado Obligatorio está SIEMPRE sin firmar en este punto, así que
        // incluirlo aquí daría un aviso en CADA documento generado y
        // convertiría "falta un dato" en "falta una firma", que es otra cosa.
        var camposObligatoriosVacios = version.Elementos
            .Where(e => e.Tipo != TipoElementoPlantilla.Firma
                && e.Obligatorio
                && EsValorVacio(valoresPorElemento[e]))
            .Select(e => e.EtiquetaVisible)
            .ToList();

        // DEC-32 (REC-115): aviso distinto de un obligatorio vacío — un valor
        // SÍ presente que el campo no reconoce (radio sin esa opción, checkbox
        // fuera de contrato). El filler solo conoce PlantillaElemento.Id (no
        // conoce EtiquetaVisible); se recorre version.Elementos, no el
        // diccionario de avisos, por el mismo motivo que camposObligatoriosVacios:
        // orden estable de plantilla, no el que decida un Dictionary interno.
        var avisoPorElementoId = resultadoRelleno.ValoresNoReconocidos.ToDictionary(a => a.ElementoId);
        var valoresNoReconocidos = version.Elementos
            .Where(e => avisoPorElementoId.ContainsKey(e.Id))
            .Select(e =>
            {
                var aviso = avisoPorElementoId[e.Id];
                return new AvisoValorNoReconocidoDto(e.EtiquetaVisible, aviso.ValorRecibido, aviso.OpcionesDisponibles);
            })
            .ToList();

        var documentoGenerado = new DocumentoGenerado(
            version.Id, documento.Id, datosUtilizadosJson, idUsuario, ahoraUtc,
            trabajadorId: trabajadorId, empresaId: empresaId, centroId: request.CentroId,
            conAvisos: camposObligatoriosVacios.Count > 0 || valoresNoReconocidos.Count > 0);
        documentoGeneradoRepositorio.Agregar(documentoGenerado);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new GenerarDocumentoIndividualResultadoDto(
            documentoGenerado.Id, documento.Id, camposObligatoriosVacios, valoresNoReconocidos));
    }

    /// <summary>Si varios contactos comparten el mismo rol, se queda con el primero — comportamiento razonable para MVP, sin criterio de desempate.</summary>
    private async Task<Dictionary<RolContacto, string>> ResolverContactosPorRolAsync(Guid empresaId, CancellationToken cancellationToken)
    {
        var contactos = await contactosContext.ContactosAgenda
            .Where(c => c.EmpresaId == empresaId)
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);
        if (contactos.Count == 0) return [];

        var idsContactos = contactos.Select(c => c.Id).ToList();
        var roles = await contactosContext.ContactosAgendaRoles
            .Where(r => idsContactos.Contains(r.ContactoAgendaId))
            .ToListAsync(cancellationToken);

        return roles
            .GroupBy(r => r.Rol)
            .ToDictionary(g => g.Key, g => contactos.First(c => c.Id == g.First().ContactoAgendaId).Nombre);
    }

    private static string? ResolverValor(
        PlantillaElemento elemento,
        IReadOnlyDictionary<Guid, string>? valoresManuales,
        Empresa? empresa,
        Trabajador? trabajador,
        Centro? centro,
        Empresa? cliente,
        IReadOnlyDictionary<RolContacto, string> contactosPorRol,
        DateTime ahoraUtc) => elemento.FuenteDato switch
        {
            FuenteDatoPlantilla.Constante => elemento.ValorConstante,
            FuenteDatoPlantilla.Manual => valoresManuales?.GetValueOrDefault(elemento.Id),
            FuenteDatoPlantilla.EmpresaRazonSocial => empresa?.RazonSocial,
            FuenteDatoPlantilla.EmpresaCif => empresa?.Cif,
            FuenteDatoPlantilla.TrabajadorNombreCompleto => trabajador?.NombreCompleto,
            FuenteDatoPlantilla.TrabajadorDni => trabajador?.Dni,
            FuenteDatoPlantilla.TrabajadorPuesto => trabajador?.Puesto,
            FuenteDatoPlantilla.CentroNombre => centro?.Nombre,
            FuenteDatoPlantilla.CentroDireccion => centro?.Direccion,
            FuenteDatoPlantilla.ClienteRazonSocial => cliente?.RazonSocial,
            FuenteDatoPlantilla.ClienteCif => cliente?.Cif,
            FuenteDatoPlantilla.DocumentoFechaGeneracion => DateOnly.FromDateTime(ahoraUtc).ToString(elemento.Formato ?? "dd/MM/yyyy"),
            FuenteDatoPlantilla.EmpresaResponsablePrl => contactosPorRol.GetValueOrDefault(RolContacto.ResponsablePrl),
            FuenteDatoPlantilla.EmpresaRepresentanteLegal => contactosPorRol.GetValueOrDefault(RolContacto.RepresentanteLegal),
            FuenteDatoPlantilla.EmpresaContactoCae => contactosPorRol.GetValueOrDefault(RolContacto.ContactoCae),
            _ => null
        };

    /// <summary>
    /// La única definición de "vacío" de este camino (DEC-5): null, cadena vacía
    /// y solo espacios. Vale igual para un Checkbox — "no marcado" se resuelve
    /// como "false"/"no", que SÍ es un dato: quien contestó que no, contestó.
    /// Lo que falta es el elemento que nadie resolvió.
    /// </summary>
    private static bool EsValorVacio(string? valor) => string.IsNullOrWhiteSpace(valor);

    private static Result<GenerarDocumentoIndividualResultadoDto> Fallo(string codigo, string mensaje) =>
        Result.Fallo<GenerarDocumentoIndividualResultadoDto>(Error.Crear(codigo, mensaje));
}
