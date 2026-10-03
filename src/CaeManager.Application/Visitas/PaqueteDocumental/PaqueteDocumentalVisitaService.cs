using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using System.IO.Compression;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.PaqueteDocumental;

public class PaqueteDocumentalVisitaService(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IConversacionRepository conversacionRepositorio,
    IFileStorageService almacenamiento,
    ILogger<PaqueteDocumentalVisitaService> logger) : IPaqueteDocumentalVisitaService
{
    // Remitente distinto del de ResponderConversacionCommand
    // (equipo-cae@buzon-simulado.local) a propósito — este mensaje no lo
    // escribió ninguna persona, y el hilo debe dejarlo claro.
    private const string RemitenteAutomaticoEmail = "talveg-automatico@sistema.local";

    private record DocumentoCandidatoDto(
        Guid Id, Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl, DateOnly FechaEmision,
        EstadoVigenciaDocumento EstadoVigencia, DateOnly? FechaVencimiento, DateTime CreadoEnUtc);
    private record DocumentoParaZipDto(Guid Id, Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl);
    private record TrabajadorNombreDto(string Nombre, string Apellidos);

    /// <summary>
    /// Un grupo por (titular, tipo) con al menos una copia no vencida — sus copias en orden
    /// de preferencia, de las que viajará UNA — y los pares que se quedan fuera por tener solo
    /// copias vencidas.
    /// </summary>
    private record SeleccionPaquete(
        IReadOnlyList<IReadOnlyList<DocumentoParaZipDto>> Enviar,
        IReadOnlyList<(Guid? TrabajadorId, Guid TipoDocumentoId)> SoloVencidos);

    public async Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default)
    {
        var paquete = await ConstruirAsync(visitaId, cancellationToken);
        if (paquete is null) return;

        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(conversacionId, cancellationToken);
        if (conversacion is null) return;

        using var flujoZip = new MemoryStream(paquete.Contenido);
        var archivoUrlZip = await almacenamiento.GuardarAsync(flujoZip, paquete.NombreArchivo, cancellationToken);

        var cuerpo =
            $"""
            <p>Adjuntamos automáticamente la documentación disponible en la plataforma para la visita en <strong>{paquete.CentroNombre}</strong>
            del {paquete.FechaInicio:dd/MM/yyyy} al {paquete.FechaFin:dd/MM/yyyy} ({paquete.Documentos.Count} documento(s)).</p>
            """;

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, RemitenteAutomaticoEmail, cuerpo);
        mensaje.AgregarAdjunto(paquete.NombreArchivo, "application/zip", paquete.Contenido.LongLength, archivoUrlZip);
    }

    public async Task<PaqueteDocumentalZip?> ConstruirAsync(Guid visitaId, CancellationToken cancellationToken = default)
    {
        var visita = await visitasContext.Visitas
            .Where(v => v.Id == visitaId)
            .Select(v => new { v.Id, v.CentroId, v.FechaInicio, v.FechaFin })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return null;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => new { c.Id, c.Nombre, c.EmpresaId, c.GestionCae })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return null;

        // P1-X2: un Centro sin gestión CAE no pide acreditación — no se le
        // envía documentación; la Visita se comunica con el aviso copiable
        // (ObtenerAvisoVisitaQuery).
        if (centro.GestionCae == Domain.Centros.ModalidadGestionCae.SinGestionCae)
        {
            logger.LogInformation("Visita {VisitaId}: el Centro no requiere gestión CAE, no se genera paquete documental.", visitaId);
            return null;
        }

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => d.ArchivoUrl != null && (d.EmpresaId == centro.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .Select(d => new DocumentoCandidatoDto(d.Id, d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl!, d.FechaEmision, d.EstadoVigencia, d.FechaVencimiento, d.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        if (candidatos.Count == 0)
        {
            logger.LogInformation("Visita {VisitaId}: sin documentos de empresa/trabajadores disponibles, no se genera paquete documental.", visitaId);
            return null;
        }

        var seleccion = SeleccionarDocumentos(candidatos, DiaDeNegocio.Hoy());
        var gruposAEnviar = seleccion.Enviar;

        if (seleccion.SoloVencidos.Count > 0)
        {
            // Es una salida hacia un tercero: lo vencido no viaja, pero su ausencia no puede
            // ser silenciosa. Solo identificadores en el log — el nombre de un trabajador es
            // dato personal y el log no lo necesita.
            logger.LogWarning(
                "Visita {VisitaId}: {Cantidad} documento(s) del paquete documental no se envían porque solo existen copias vencidas (tipo/titular): {Omitidos}.",
                visitaId,
                seleccion.SoloVencidos.Count,
                string.Join(", ", seleccion.SoloVencidos.Select(o => $"{o.TipoDocumentoId}/{(o.TrabajadorId is { } t ? t.ToString() : "empresa")}")));
        }

        if (gruposAEnviar.Count == 0)
        {
            logger.LogWarning("Visita {VisitaId}: ningún documento vigente que enviar, no se genera paquete documental.", visitaId);
            return null;
        }

        var tiposDocumentoIds = gruposAEnviar.SelectMany(g => g).Select(d => d.TipoDocumentoId).Distinct().ToList();
        var nombresTipoDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => tiposDocumentoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var empresa = await empresasContext.Empresas
            .Where(e => e.Id == centro.EmpresaId)
            .Select(e => new { e.RazonSocial })
            .FirstOrDefaultAsync(cancellationToken);

        var trabajadoresPorId = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => new TrabajadorNombreDto(t.Nombre, t.Apellidos), cancellationToken);

        var zip = await ConstruirZipAsync(gruposAEnviar, nombresTipoDocumento, empresa?.RazonSocial, trabajadoresPorId, cancellationToken);
        if (zip is null) return null; // ningún archivo pudo abrirse — no tiene sentido ofrecer un zip vacío.
        var (zipBytes, incluidos) = zip.Value;

        // Lo que consta es lo que de verdad viajó: si la copia sin confirmar elegida no se pudo abrir
        // y entró otra, no hay nada sin confirmar que avisar (y al revés).
        var sinConfirmarEnviados = incluidos
            .Select(i => candidatos.First(c => c.Id == i.DocumentoId))
            .Where(c => c.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
            .ToList();
        if (sinConfirmarEnviados.Count > 0)
        {
            // La copia enviada no está vencida, pero nadie ha confirmado hasta cuándo vale:
            // que conste, igual que lo omitido por vencido.
            logger.LogWarning(
                "Visita {VisitaId}: {Cantidad} documento(s) del paquete documental se envían sin vigencia confirmada (tipo/titular): {SinConfirmar}.",
                visitaId,
                sinConfirmarEnviados.Count,
                string.Join(", ", sinConfirmarEnviados.Select(o => $"{o.TipoDocumentoId}/{(o.TrabajadorId is { } t ? t.ToString() : "empresa")}")));
        }

        var nombreZip = $"documentacion-visita-{centro.Nombre.Replace(' ', '-')}-{visita.FechaInicio:yyyyMMdd}.zip";
        return new PaqueteDocumentalZip(nombreZip, zipBytes, incluidos, centro.Nombre, visita.FechaInicio, visita.FechaFin);
    }

    /// <summary>
    /// Reglas del propietario (2026-09-20 y precisión del 2026-10-01): al Cliente empresarial
    /// se le envían los documentos vigentes, uno por (titular, tipo) y el de emisión más
    /// reciente; nunca los vencidos. El que viaja es el documento efectivo (<see cref="DocumentoEfectivo"/>), si es vigente.
    ///
    /// <para>
    /// Entre las copias vigentes de un (titular, tipo) viaja la de <c>FechaEmision</c> más
    /// reciente, aunque otra venza más tarde; el desempate es determinista y está en
    /// <see cref="DocumentoEfectivo"/>. «Sin confirmar» y
    /// <see cref="EstadoVigenciaDocumento.NoCaduca"/> (p. ej. Formación 60h) son vigentes y
    /// compiten por su emisión como cualquier otra copia.
    /// </para>
    ///
    /// <para>
    /// Si la copia que acaba viajando está sin confirmar —nadie ha anotado hasta cuándo vale,
    /// pero no está vencida—, <see cref="ConstruirAsync"/> lo deja en el log (una vez
    /// construido el zip, para que conste lo que de verdad se envió).
    /// </para>
    ///
    /// <para>
    /// Vencido es <c>FechaVencimiento &lt; hoy</c> (el umbral ámbar/rojo no interviene:
    /// Próximo y Urgente siguen vigentes), evaluado con
    /// <see cref="DocumentoEfectivo.ValidoHoy"/> para no duplicar la regla. Si de
    /// un (titular, tipo) solo hay copias vencidas no se envía ninguna y el par se devuelve en
    /// <see cref="SeleccionPaquete.SoloVencidos"/>: nunca se manda el vencido «por si acaso».
    /// </para>
    ///
    /// <para>
    /// Los candidatos ya vienen filtrados a los que tienen archivo: un documento sin
    /// archivo no puede viajar y no compite por el puesto. Cada grupo conserva todas sus
    /// copias vigentes en orden de preferencia: si el archivo de la primera no se puede
    /// abrir en el almacenamiento, <see cref="ConstruirZipAsync"/> prueba la siguiente —
    /// sigue siendo un documento por titular y tipo, y nunca uno vencido.
    /// </para>
    ///
    /// <para>
    /// Este es el único punto que elige copia para el paquete de la Visita: lo usan el
    /// adjunto automático al buzón (<see cref="GenerarYEnviarAsync"/>) y la descarga manual
    /// del ZIP (<see cref="ConstruirAsync"/>).
    /// </para>
    /// </summary>
    private static SeleccionPaquete SeleccionarDocumentos(IReadOnlyList<DocumentoCandidatoDto> candidatos, DateOnly hoy)
    {
        var enviar = new List<IReadOnlyList<DocumentoParaZipDto>>();
        var soloVencidos = new List<(Guid? TrabajadorId, Guid TipoDocumentoId)>();

        foreach (var grupo in candidatos.GroupBy(d => (d.TrabajadorId, d.TipoDocumentoId)))
        {
            // El documento efectivo del grupo es el primero (DocumentoEfectivo, el mismo orden que el estado y el
            // cumplimiento) y solo viaja si es vigente: los vencidos nunca se envían. Los válidos van antes que los
            // vencidos, así que las copias vigentes son el prefijo del orden: el efectivo y, detrás, las de reserva por si
            // su archivo no se puede abrir.
            var vigentes = DocumentoEfectivo.Ordenar(
                    grupo, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy)
                .TakeWhile(d => DocumentoEfectivo.ValidoHoy(d.EstadoVigencia, d.FechaVencimiento, hoy))
                .ToList();

            if (vigentes.Count == 0)
            {
                soloVencidos.Add(grupo.Key);
                continue;
            }

            enviar.Add(vigentes.Select(d => new DocumentoParaZipDto(d.Id, d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl)).ToList());
        }

        return new SeleccionPaquete(enviar, soloVencidos);
    }

    /// <summary>
    /// Devuelve null si ningún documento pudo abrirse (storage inconsistente) — mejor no adjuntar
    /// nada que adjuntar un zip vacío. De cada grupo entra UNA copia: la primera cuyo archivo se
    /// pueda abrir; si ninguna se abre, el grupo queda fuera (con un aviso por cada intento).
    /// Devuelve también qué documentos entraron de verdad: su cuenta es la cifra que anuncia el
    /// correo, y cada uno es un acceso a su contenido que la descarga manual debe registrar (DEC-36).
    /// </summary>
    private async Task<(byte[] Bytes, IReadOnlyList<DocumentoEnPaquete> Documentos)?> ConstruirZipAsync(
        IReadOnlyList<IReadOnlyList<DocumentoParaZipDto>> grupos,
        IReadOnlyDictionary<Guid, string> nombresTipoDocumento,
        string? razonSocialEmpresa,
        IReadOnlyDictionary<Guid, TrabajadorNombreDto> trabajadoresPorId,
        CancellationToken cancellationToken)
    {
        using var memoria = new MemoryStream();
        var nombresUsados = new HashSet<string>();
        var agregados = new List<DocumentoEnPaquete>();

        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var grupo in grupos)
            {
                foreach (var documento in grupo)
                {
                    Stream contenido;
                    try
                    {
                        contenido = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "No se pudo abrir el archivo {ArchivoUrl} para el paquete documental de la visita.", documento.ArchivoUrl);
                        continue;
                    }

                    await using (contenido)
                    {
                        var tipoNombre = nombresTipoDocumento.GetValueOrDefault(documento.TipoDocumentoId, "Documento");
                        var carpeta = documento.TrabajadorId is not null ? "Trabajadores" : "Empresa";
                        var titular = documento.TrabajadorId is not null && trabajadoresPorId.TryGetValue(documento.TrabajadorId.Value, out var trabajador)
                            ? $"{trabajador.Nombre} {trabajador.Apellidos}"
                            : razonSocialEmpresa ?? "Empresa";

                        var extension = Path.GetExtension(documento.ArchivoUrl);
                        var nombreEntrada = SanearNombreEntrada($"{carpeta}/{tipoNombre} - {titular}{extension}", nombresUsados);

                        var entrada = zip.CreateEntry(nombreEntrada, CompressionLevel.Fastest);
                        await using var flujoEntrada = entrada.Open();
                        await contenido.CopyToAsync(flujoEntrada, cancellationToken);
                    }

                    agregados.Add(new DocumentoEnPaquete(documento.Id, documento.TipoDocumentoId));
                    break; // uno por titular y tipo
                }
            }
        }

        return agregados.Count > 0 ? (memoria.ToArray(), agregados) : null;
    }

    private static string SanearNombreEntrada(string nombrePropuesto, HashSet<string> nombresUsados)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var limpio = string.Concat(nombrePropuesto.Select(c => invalidos.Contains(c) && c != '/' ? '_' : c));

        if (nombresUsados.Add(limpio)) return limpio;

        var extension = Path.GetExtension(limpio);
        var sinExtension = limpio[..^extension.Length];
        var contador = 2;
        string candidato;
        do
        {
            candidato = $"{sinExtension} ({contador}){extension}";
            contador++;
        } while (!nombresUsados.Add(candidato));

        return candidato;
    }
}
