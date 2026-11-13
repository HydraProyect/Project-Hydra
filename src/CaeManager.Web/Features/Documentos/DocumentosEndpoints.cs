using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Importacion;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Services;
using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve el PDF adjunto de un Documento vía un endpoint autenticado — nunca
/// como archivo estático público, precisamente porque IFileStorageService
/// guarda fuera de wwwroot (ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md, "Archivos").
/// </summary>
public static class DocumentosEndpoints
{
    public static IEndpointRouteBuilder MapDocumentosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Solo sesión interactiva: el iframe del visor y los enlaces «Ver PDF». La extensión de navegador
        // no entra por aquí con su token: recibe el archivo por /extension/acreditaciones/{id}/archivo,
        // ligado a una Acreditación pendiente, con límite de ritmo y kill switch comprobados en el servidor
        // (EntregaArchivoExtensionEndpoints). ObtenerDocumentoPorIdQuery filtra por Tenant y por alcance de
        // cartera con el rol real de quien pide.
        endpoints.MapGet("/documentos/{id:guid}/archivo", ServirArchivoAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });

        // Revisión IA (D-49, opción C): el visor de la revisión seleccionada se carga solo, y su acceso queda como
        // «Revisión IA», una fila por persona y Documento. El origen lo da esta ruta, ligada a una revisión pendiente, nunca
        // un dato del cliente (query, cabecera, Referer): /documentos/{id}/archivo sigue registrando una Apertura en cada GET.
        // Solo cookie de sesión y los roles de la pantalla (el claim; dentro de un Workspace operativo derivado ya es el rol
        // efectivo, y bajo una Sesión Privilegiada no hay ninguno). ObtenerRevisionIaParaVisorQuery vuelve a decidir con el
        // rol efectivo, la cola (pendiente, Tenant propietario, Asignación de Cartera) y, al abrir, DocumentoLegibleAsync.
        endpoints.MapGet("/documentos/revision-ia/{revisionId:guid}/archivo", ServirArchivoDeRevisionIaAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = IdentityConstants.ApplicationScheme,
                Roles = string.Join(',', ColaRevisionIa.RolesQueAccedenALaPestana),
            });

        // Solo se enlaza desde /importacion (Importacion.razor: Roles = ConCargaMasivaCsv: Administrador, Dirección CAE y Coordinador CAE)
        // — mismo motivo que el RequireAuthorization de /clientes/plantilla.xlsx,
        // aunque aquí el archivo es una plantilla en blanco sin datos de tenant.
        endpoints.MapGet("/documentos/plantilla.xlsx", (IPlantillaDocumentosService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-documentos.xlsx"))
        .RequireAuthorization(policy => policy.RequireRole(CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae))
        .ExcluidoDelEncargoDeAdministracion();

        // Mismo patrón de referencia que ClientesEndpoints. La columna
        // «Plataformas CAE» (D-20) sale de las acreditaciones que
        // ObtenerDocumentosQueryHandler ya resuelve en una consulta por
        // página (no por fila), así que cada lote de PaginadorExportacion
        // no añade consultas: solo nombre y estado, nunca el motivo del
        // rechazo ni la dirección del portal.
        // Lleva el listado completo de Documentos del tenant (propietario,
        // tipo, fechas): mismo criterio de caché que el PDF.
        // Sin SoloRolesDelListado(), como /trabajadores/exportar.xlsx: Documentos.razor
        // declara [Authorize] sin roles y el menú del rol Cliente la ofrece
        // (CatalogoSeccionesMenu.RolesDeDocumentos, AdmisionDeDestinos.RutasDelMenuDeCliente).
        // Quien exporta el listado es quien lo ve; el alcance lo aplica ObtenerDocumentosQuery,
        // la misma consulta de la página.
        endpoints.MapGet("/documentos/exportar.xlsx", ExportarAsync);

        return endpoints;
    }

    public static async Task<IResult> ExportarAsync(
        HttpContext contexto, IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion,
        CancellationToken cancellationToken,
        string? q = null, string? ambito = null, string? estado = null, string? orden = null, bool desc = false,
        string? tipo = null, string? plataforma = null, string? propietario = null, string? agrupar = null,
        string? estadoPlataforma = null, string? venceEn = null, string? pendienteRevisionIa = null,
        Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosDocumentos>? textos = null)
    {
        // «Vence en» (D-21): solo los plazos que ofrece el listado, como número de días; cualquier otro valor se ignora.
        int? venceEnDias = int.TryParse(venceEn, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var plazo)
            && ObtenerDocumentosQuery.PlazosVenceEnDias.Contains(plazo)
            && plazo.ToString(System.Globalization.CultureInfo.InvariantCulture) == venceEn ? plazo : null;
        // «Pendiente de revisión IA»: solo «1». Si se aplica lo decide el handler, con el rol EFECTIVO de cada llamada (con un rol sin
        // acceso a la cola lo ignora); el endpoint no lo vuelve a decidir: anota lo que dijo el primer lote
        // (ResultadoPaginado.FiltroRevisionIaAplicado) y, si un lote posterior dice otra cosa (el rol efectivo cambió a mitad de la
        // exportación), aborta en vez de servir un fichero con lotes filtrados y sin filtrar.
        var revisionIaPedida = pendienteRevisionIa == "1";
        bool? revisionIaAplicada = null;
        // Solo el nombre exacto de un estado, como el listado: ni números ni listas con coma.
        EstadoEnPlataformaFiltro? estadoPlataformaFiltro =
            estadoPlataforma is not null && Enum.GetNames<EstadoEnPlataformaFiltro>().Contains(estadoPlataforma, StringComparer.Ordinal)
                ? Enum.Parse<EstadoEnPlataformaFiltro>(estadoPlataforma) : null;
        // Estado 4a: la página no muestra los Documentos del Tenant de origen a quien tiene que
        // elegir una empresa de su cartera (ContextoEmpresaActiva.ResolverAsync); el endpoint no
        // los consulta, no genera el archivo ni deja rastro. Misma guarda que Trabajadores.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/documentos");

        CabecerasArchivoSensible.ProhibirCache(contexto);

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Documentos");

        hoja.Cell(1, 1).Value = "Propietario";
        hoja.Cell(1, 2).Value = "Ámbito";
        hoja.Cell(1, 3).Value = "Tipo de documento";
        hoja.Cell(1, 4).Value = "Emisión";
        hoja.Cell(1, 5).Value = "Vencimiento";
        hoja.Cell(1, 6).Value = "Estado";
        // D-20: las Plataformas CAE del Documento y el estado de su acreditación en cada una («Nalanda: Pendiente de subir; Dokify:
        // Validada»), la misma regla que la columna del listado por tipo. Nunca el motivo del rechazo: la hoja no lo lleva.
        hoja.Cell(1, 7).Value = "Plataformas CAE";
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        // El handler calcula con el día de negocio de cada lote la franja de estado, «Vence en» y el estado de cada fila: si el día
        // cambia a mitad de la exportación, los lotes se calcularían con dos fechas y el Skip podría saltarse filas.
        var diaDeLaExportacion = CaeManager.Domain.Common.DiaDeNegocio.Hoy();
        string? criterioCambiado = null;
        await foreach (var documento in PaginadorExportacion.PaginarAsync(async (pagina, tamanoPagina) =>
        {
            var lote = await mediator.Send(
                new ObtenerDocumentosQuery(
                    TrabajadorId: null,
                    Ambito: Enum.TryParse<AmbitoAplicacion>(ambito, out var ambitoFiltro) ? ambitoFiltro : null,
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    // La selección de la franja de estado: varios nombres separados por coma.
                    Estado: null,
                    Estados: SeleccionEstados.Separar<EstadoDocumento>(estado) is { Count: > 0 } estados ? estados : null,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc,
                    // Un valor que no es un Id se ignora, igual que en el listado: no es un 400.
                    PropietarioId: Guid.TryParse(propietario, out var entidadAsociadaId) ? entidadAsociadaId : null,
                    TipoDocumentoId: Guid.TryParse(tipo, out var tipoFiltro) ? tipoFiltro : null,
                    ProveedorPlataformaCaeId: Guid.TryParse(plataforma, out var plataformaFiltro) ? plataformaFiltro : null,
                    AgruparPorAmbito: agrupar == "ambito",
                    // El filtro «Estado en la plataforma» del listado; la agrupación por vínculo no se exporta (repetiría filas).
                    EstadosEnPlataforma: estadoPlataformaFiltro is { } estadoEnPlataforma ? [estadoEnPlataforma] : null,
                    VenceEnDias: venceEnDias,
                    PendienteDeRevisionIa: revisionIaPedida),
                cancellationToken);
            revisionIaAplicada ??= lote.FiltroRevisionIaAplicado;
            if (lote.FiltroRevisionIaAplicado != revisionIaAplicada)
                criterioCambiado = "ExportacionCriterioCambiado";
            else if (CaeManager.Domain.Common.DiaDeNegocio.Hoy() != diaDeLaExportacion)
                criterioCambiado = "ExportacionDiaCambiado";
            else
                return lote;

            // Un lote vacío corta el paginador: las filas de este lote ni se escriben.
            return new ResultadoPaginado<DocumentoListaDto>([], lote.TotalElementos, pagina, tamanoPagina);
        }))
        {
            hoja.Cell(fila, 1).Value = documento.PropietarioNombre;
            hoja.Cell(fila, 2).Value = documento.Ambito.ToString();
            hoja.Cell(fila, 3).Value = documento.TipoDocumentoNombre;
            hoja.Cell(fila, 4).Value = documento.FechaEmision.ToDateTime(TimeOnly.MinValue);
            if (documento.FechaVencimiento is not null)
                hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
            hoja.Cell(fila, 6).Value = EstadoDocumentoUi.Texto(documento.Estado);
            // SetValue(string): texto tal cual, sin que un nombre de plataforma que empiece por «=» se lea como fórmula.
            hoja.Cell(fila, 7).SetValue(PlataformasCaeDelDocumento.TextoParaExcel(documento.Acreditaciones));
            fila++;
        }

        if (criterioCambiado is not null)
        {
            // Ni fichero ni rastro en el registro: lo exportado hasta aquí mezclaría dos criterios. Quien exporta puede repetirlo.
            return Results.Problem(textos?[criterioCambiado].Value, statusCode: StatusCodes.Status409Conflict);
        }

        hoja.Columns().AdjustToContents();

        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;

        try
        {
            var estadosAplicados = SeleccionEstados.Separar<EstadoDocumento>(estado);
            await registroExportacion.RegistrarAsync(
                nameof(Documento), fila - 2,
                CriteriosExportacion.Desde(
                    ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                    ("ambito", Enum.TryParse<AmbitoAplicacion>(ambito, out var ambitoAplicado) && Enum.IsDefined(ambitoAplicado) ? ambitoAplicado.ToString() : null),
                    ("estado", estadosAplicados.Count > 0 ? string.Join(",", estadosAplicados) : null),
                    ("tipo", Guid.TryParse(tipo, out var tipoAplicado) ? tipoAplicado.ToString() : null),
                    ("plataforma", Guid.TryParse(plataforma, out var plataformaAplicada) ? plataformaAplicada.ToString() : null),
                    ("propietario", Guid.TryParse(propietario, out var propietarioAplicado) ? propietarioAplicado.ToString() : null),
                    ("agrupar", agrupar == "ambito" ? "ambito" : null),
                    ("estadoPlataforma", estadoPlataformaFiltro?.ToString()),
                    ("venceEnDias", venceEnDias?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("pendienteRevisionIa", revisionIaAplicada == true ? "true" : null),
                    ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
                cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return Results.File(
            stream,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "documentos.xlsx");
    }

    /// <summary>
    /// Manejador de <c>GET /documentos/{id}/archivo</c>. Método con nombre, y no una lambda, para
    /// poder probarlo sin levantar el host (mismo criterio que <c>LogoTenantEndpoints.ServirAsync</c>).
    /// </summary>
    public static async Task<IResult> ServirArchivoAsync(
        Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        var abierto = await AbrirConRegistroAsync(
            id, TipoAccesoDocumentoSensible.Apertura, contexto, mediator, almacenamiento, registroAcceso, fabricaRegistro, cancellationToken);
        if (abierto.Rechazo is not null)
            return abierto.Rechazo;

        return ServirEnLinea(contexto, abierto.Flujo!, abierto.Documento!);
    }

    /// <summary>
    /// Manejador de <c>GET /documentos/revision-ia/{revisionId}/archivo</c>: el documento de una revisión IA pendiente, para
    /// el visor que la pantalla Revisión IA carga sola (D-49, opción C). Sirve el Documento de ESA revisión —el cliente no
    /// lo elige— y lo registra como <see cref="TipoAccesoDocumentoSensible.RevisionIa"/>, una vez por persona y Documento
    /// (<see cref="IRegistroAccesoDocumentoSensibleService.RegistrarRevisionIaSiSensibleAsync"/>). Una revisión que quien
    /// pide no alcanza en la cola da 404, igual que un Id que no existe.
    /// </summary>
    public static async Task<IResult> ServirArchivoDeRevisionIaAsync(
        Guid revisionId, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        RevisionIaParaVisorDto? revision;
        try
        {
            revision = await mediator.Send(new ObtenerRevisionIaParaVisorQuery(revisionId), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsContextoNoAutorizado(ex))
        {
            return RechazoIncrustable(contexto, ErroresLecturaSensible.Responder(contexto, ex, revisionId,
                fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!)));
        }
        if (revision is null)
            return RechazoIncrustable(contexto, Results.NotFound());

        var abierto = await AbrirConRegistroAsync(
            revision.DocumentoId,
            (documentoId, ct) => registroAcceso.RegistrarRevisionIaSiSensibleAsync(documentoId, revision.CreadaEnUtc, ct),
            contexto, mediator, almacenamiento, fabricaRegistro, cancellationToken);
        if (abierto.Rechazo is not null)
            return RechazoIncrustable(contexto, abierto.Rechazo);

        return ServirEnLinea(contexto, abierto.Flujo!, abierto.Documento!);
    }

    /// <summary>
    /// El rechazo del visor de Revisión IA (la revisión se resolvió en otra pestaña, salió del alcance, el registro se
    /// rechazó): el mismo código, sin cuerpo ni dato alguno, pero incrustable en el mismo origen como la respuesta buena,
    /// para que el <c>&lt;iframe&gt;</c> quede en blanco en vez de enseñar la página de error del navegador por
    /// <c>X-Frame-Options: DENY</c>. Sin páginas de estado: <c>UseStatusCodePagesWithReExecute("/not-found")</c> metería en
    /// el visor el HTML de la aplicación, con las cabeceras por defecto. Solo esta ruta; <c>/documentos/{id}/archivo</c> no
    /// cambia.
    /// </summary>
    private static IResult RechazoIncrustable(HttpContext contexto, IResult rechazo)
    {
        if (contexto.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } paginasDeEstado)
            paginasDeEstado.Enabled = false;
        contexto.PermitirIncrustacionEnMismoOrigen();
        return rechazo;
    }

    /// <summary>El archivo vigente, en línea y por rangos, incrustable solo en el mismo origen: lo que muestra el visor.</summary>
    private static IResult ServirEnLinea(HttpContext contexto, Stream flujo, DocumentoDetalleDto documento)
    {
        // Se muestra en el navegador, no se descarga: VisorDocumento lo incrusta en un <iframe> y los
        // enlaces «Ver PDF» lo abren en una pestaña. Pasar el nombre a Results.File como
        // fileDownloadName habría añadido Content-Disposition: attachment (el defecto que forzaba la
        // descarga); aquí va en una cabecera `inline` propia, que el ejecutor del resultado respeta
        // porque no recibe nombre. Quien quiere descargar sigue pudiendo: el enlace «Descargar PDF» del
        // panel del Documento lleva el atributo `download`, y el navegador lo aplica sobre `inline`.
        if (!string.IsNullOrWhiteSpace(documento.NombreArchivoDescarga))
        {
            var disposicion = new ContentDispositionHeaderValue("inline");
            disposicion.SetHttpFileName(documento.NombreArchivoDescarga);
            contexto.Response.Headers.ContentDisposition = disposicion.ToString();
        }
        else
        {
            contexto.Response.Headers.ContentDisposition = "inline";
        }

        // X-Frame-Options: DENY y frame-ancestors 'none' son el valor por defecto de toda la
        // aplicación (UseCabecerasSeguridad). Solo esta respuesta se relaja, y solo al mismo origen.
        contexto.PermitirIncrustacionEnMismoOrigen();

        // enableRangeProcessing: el visor de PDF del navegador pide por
        // rangos al paginar/buscar en vez de volver a traer el archivo
        // entero en cada petición. No reduce el coste del servidor —
        // DiskFileStorageService ya descifra el archivo completo en
        // memoria antes de servirlo (medición de Módulo 2, PR #360) — la
        // lectura por rangos que sí lo haría exige un formato cifrado por
        // bloques, pendiente de decisión.
        return Results.File(flujo, "application/pdf", enableRangeProcessing: true);
    }

    /// <summary>
    /// Lo que comparte todo lo que sirve el archivo vigente de un Documento (esta ruta y la entrega a la extensión):
    /// lectura con alcance, apertura y registro de acceso. Devuelve el flujo abierto y el Documento, o la respuesta de
    /// rechazo.
    /// </summary>
    internal static Task<(Stream? Flujo, DocumentoDetalleDto? Documento, IResult? Rechazo)> AbrirConRegistroAsync(
        Guid id, TipoAccesoDocumentoSensible tipoAcceso, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken) =>
        AbrirConRegistroAsync(
            id, (documentoId, ct) => registroAcceso.RegistrarSiSensibleAsync(documentoId, tipoAcceso, ct),
            contexto, mediator, almacenamiento, fabricaRegistro, cancellationToken);

    /// <summary>
    /// <see cref="AbrirConRegistroAsync(Guid, TipoAccesoDocumentoSensible, HttpContext, IMediator, IFileStorageService, IRegistroAccesoDocumentoSensibleService, ILoggerFactory, CancellationToken)"/>
    /// con el registro que decide quien llama (la Revisión IA deduplica por persona y Documento). Mismo orden: lectura con
    /// alcance, apertura del blob y registro antes de entregar nada; si el registro falla, no se sirve.
    /// </summary>
    private static async Task<(Stream? Flujo, DocumentoDetalleDto? Documento, IResult? Rechazo)> AbrirConRegistroAsync(
        Guid id, Func<Guid, CancellationToken, Task> registrar, HttpContext contexto, IMediator mediator,
        IFileStorageService almacenamiento, ILoggerFactory fabricaRegistro, CancellationToken cancellationToken)
    {
        DocumentoDetalleDto? documento;
        try
        {
            documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsContextoNoAutorizado(ex))
        {
            return (null, null, ErroresLecturaSensible.Responder(contexto, ex, id,
                fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!)));
        }
        if (documento?.ArchivoUrl is null)
            return (null, null, Results.NotFound());

        CabecerasArchivoSensible.ProhibirCache(contexto);

        // DEC-36 (REC-099): se abre primero y se registra después de que
        // AbrirAsync confirme que el archivo existe — si el blob no
        // estuviera (storage inconsistente), no queda un registro de un
        // acceso que nunca entregó contenido (Codex, HO-099-01).
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // El Documento existe y dice tener adjunto, pero el almacén no lo tiene: 404, como los
            // demás endpoints de contenido (AuditoriaEndpoints, LogoTenantEndpoints), y no la
            // excepción sin controlar. A diferencia de ellos se avisa, porque aquí es una
            // inconsistencia que alguien tiene que mirar. El aviso lleva el Id del Documento y no la
            // excepción ni la clave del fichero: la ruta del almacén no sale ni en la respuesta ni
            // en el registro.
            fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!).LogWarning(
                "El Documento {DocumentoId} tiene adjunto registrado pero su fichero no está en el almacén; se responde 404.",
                documento.Id);
            return (null, null, Results.NotFound());
        }

        // Falla en cerrado: ningún rechazo ni fallo de registro entrega contenido.
        try
        {
            await registrar(documento.Id, cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            await flujo.DisposeAsync();
            return (null, null, ErroresLecturaSensible.Responder(contexto, ex, documento.Id,
                fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!)));
        }
        catch
        {
            await flujo.DisposeAsync();
            throw;
        }

        return (flujo, documento, null);
    }

}
