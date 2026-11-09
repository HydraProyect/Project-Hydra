using System.Text.Json;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Contactos;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Gestiones;
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
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;

/// <summary>
/// No encola detección/verificación IA (el contenido lo escribe Hydra desde
/// datos ya conocidos, no hace falta releerlo), a diferencia de
/// <c>CrearDocumentoCommandHandler</c>, que sí lo hace para un PDF subido a
/// mano. Las acreditaciones de plataforma sí nacen igual que en el alta manual
/// (<see cref="IAltaAcreditacionesPlataformaService"/>), y con ellas la Gestión automática
/// (<see cref="INacimientoGestionAutomaticaService"/>): es un alta con archivo.
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
    ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IUnitOfWork unitOfWork,
    INacimientoGestionAutomaticaService gestionAutomatica,
    ILogger<GenerarDocumentoIndividualCommandHandler> logger,
    // Opcional solo mientras la PR #2104 cambia los tests que construyen este handler: el contenedor siempre lo resuelve
    // (InvalidadorAlcanceInerte por defecto, el AlcanceDatosService del circuito en la Web). Issue #2118.
    IInvalidadorAlcance? invalidadorAlcance = null)
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
                // Defensa en profundidad (REC-149): inalcanzable para el rol
                // Cliente vía AutorizacionEscrituraBehavior; alcance de
                // gestión como segunda barrera independiente.
                && await alcanceDatos.EmpresaParaGestionVisibleAsync(request.OwnerId, cancellationToken)
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

        // El flujo se cierra en cuanto se copia: retiene su reserva del presupuesto de memoria hasta
        // cerrarse, y el GuardarAsync de abajo reserva por su cuenta.
        byte[] contenidoOriginal;
        await using (var flujoOriginal = await almacenamientoArchivos.AbrirAsync(version.ArchivoOriginalUrl, cancellationToken))
        {
            using var memoria = new MemoryStream();
            await flujoOriginal.CopyToAsync(memoria, cancellationToken);
            contenidoOriginal = memoria.ToArray();
        }

        if (!ValidadorFirmaArchivo.EsDocumentoValido(contenidoOriginal, "plantilla.pdf"))
            return Fallo("Plantilla.ArchivoNoValido", ValidadorFirmaArchivo.MensajePlantillaNoValida);

        var elementosRelleno = valoresPorElemento
            .Select(par => new ElementoRellenoPlantilla(
                par.Key.Tipo, par.Key.NombreCampoAcroForm, par.Key.Pagina, par.Key.X, par.Key.Y, par.Key.Ancho, par.Key.Alto, par.Value,
                ElementoId: par.Key.Id))
            .ToList();

        var resultadoRelleno = rellenador.Rellenar(contenidoOriginal, plantilla.FormatoOrigen, elementosRelleno);
        var contenidoRelleno = resultadoRelleno.Pdf;

        if (!ValidadorFirmaArchivo.EsDocumentoValido(contenidoRelleno, "documento-generado.pdf"))
            return Fallo("Plantilla.ArchivoNoValido", ValidadorFirmaArchivo.MensajeDocumentoNoValido);

        using var flujoRelleno = new MemoryStream(contenidoRelleno);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujoRelleno, "documento-generado.pdf", cancellationToken);

        var fechaEmision = DiaDeNegocio.De(ahoraUtc);
        // Un documento generado desde plantilla no trae vigencia anotada: si
        // el tipo no vence automáticamente, nace sin vigencia confirmada.
        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            tipoDocumento.AplicaVencimientoAutomatico, tipoDocumento.VigenciaMeses,
            fechaEmision, fechaVencimientoManual: null, noCaducaConfirmado: false);

        var documento = plantilla.AmbitoAplicacion switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, vigencia, archivoUrl),
            AmbitoAplicacion.Cliente => Documento.DeCliente(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, vigencia, archivoUrl),
            _ => Documento.DeEmpresa(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, vigencia, archivoUrl)
        };

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

        // Un Documento de Empresa se acredita en los accesos de plataforma de los Centros de su Empresa (regla A-1): su
        // alta se serializa con la de un acceso de plataforma, igual que en CrearDocumentoCommandHandler (ver
        // ISerializacionAltasAcreditacionEmpresa).
        var guardado = plantilla.AmbitoAplicacion == AmbitoAplicacion.Empresa
            ? await serializacionAltas.EjecutarAsync(async ct =>
            {
                // Bajo el candado se vuelve a decidir la Asignación de Cartera sobre la Empresa y el Centro: pudo
                // revocarse mientras se esperaba, y el alcance memoizado (en Blazor Server, el del circuito) es de
                // antes; se descarta, igual que en BloqueoRespuestaSugerida.ComprobarEnvioPorBuzonAsync (issue #2118).
                // Un rechazo aquí deja el PDF ya guardado sin ningún Documento que lo referencie: se elimina abajo.
                invalidadorAlcance?.Invalidar();
                if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.OwnerId, ct))
                    return Result.Fallo(Error.Crear("Plantilla.PropietarioNoEncontrado", "No encontramos a quién pertenece este documento."));
                if (request.CentroId is { } centroBajoCandado && !await alcanceDatos.CentroVisibleAsync(centroBajoCandado, ct))
                    return Result.Fallo(Error.Crear("Plantilla.CentroNoEncontrado", "No encontramos este centro."));
                return await GuardarAsync(documento, documentoGenerado, ct);
            }, cancellationToken)
            : await GuardarAsync(documento, documentoGenerado, cancellationToken);
        if (guardado.EsFallido)
        {
            // GuardarAsync solo devuelve éxito o lanza: un fallo aquí es el rechazo de la Asignación de Cartera bajo el
            // candado, y la transacción no llegó a crear el Documento. El PDF guardado arriba no lo referencia nadie y
            // puede llevar datos de la Empresa y de sus contactos a los que quien lo pidió ya no tiene acceso.
            // Límite aceptado: si un commit se confirma en la base pero devuelve un error transitorio, la estrategia de
            // reintentos repite la operación; si en ese reintento la cartera ya está revocada, se rechaza y se borra el PDF
            // de un Documento que sí quedó confirmado. Exige un fallo justo en el commit y una revocación en esa ventana.
            // Las excepciones tras guardar el PDF no se compensan aquí: un commit ambiguo podría haber confirmado el Documento.
            await EliminarPdfHuerfanoAsync(archivoUrl, version.Id, cancellationToken);
            return Result.Fallo<GenerarDocumentoIndividualResultadoDto>(guardado.Error);
        }

        return Result.Exito(new GenerarDocumentoIndividualResultadoDto(
            documentoGenerado.Id, documento.Id, camposObligatoriosVacios, valoresNoReconocidos));
    }

    /// <summary>
    /// El Documento, sus acreditaciones y su registro de generación en un solo guardado: o se confirman juntos o
    /// ninguno. Puede ejecutarse más de una vez dentro de la serialización (reintento de la transacción, que empieza con
    /// el contexto vacío): todo lo que guarda se agrega aquí dentro.
    /// </summary>
    private async Task<Result> GuardarAsync(Documento documento, DocumentoGenerado documentoGenerado, CancellationToken cancellationToken)
    {
        documentoRepositorio.Agregar(documento);
        await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] }, cancellationToken);

        // Generar desde plantilla es un alta con archivo: hace nacer la Gestión automática igual que el alta manual,
        // con las acreditaciones recién agregadas y en el mismo guardado. Va aquí dentro, después de
        // AgregarPendientesAsync, para que la vea y para que un reintento de la serialización la vuelva a agregar.
        await gestionAutomatica.AgregarAsync(documento, sustituido: null, cancellationToken);
        documentoGeneradoRepositorio.Agregar(documentoGenerado);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private async Task EliminarPdfHuerfanoAsync(string archivoUrl, Guid versionId, CancellationToken cancellationToken)
    {
        try
        {
            await almacenamientoArchivos.EliminarAsync(archivoUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No se propaga: el fallo real ya es el rechazo de la cartera, y perderlo por un problema al limpiar dejaría
            // al llamador sin explicación. Mismo patrón de compensación best-effort que AgregarVersionPlantillaCommandHandler:
            // si la limpieza falla, el PDF queda huérfano sin más reintento. Se registra la versión de plantilla por Id y
            // el tipo de la excepción: ni la clave del archivo ni el mensaje de la excepción deben acabar en los registros.
            logger.LogWarning(
                "No se pudo eliminar el PDF generado desde la versión de plantilla {PlantillaDocumentoVersionId} tras rechazarse su alta ({TipoExcepcion}).",
                versionId, ex.GetType().Name);
        }
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
            FuenteDatoPlantilla.DocumentoFechaGeneracion => DiaDeNegocio.De(ahoraUtc).ToString(elemento.Formato ?? "dd/MM/yyyy"),
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
