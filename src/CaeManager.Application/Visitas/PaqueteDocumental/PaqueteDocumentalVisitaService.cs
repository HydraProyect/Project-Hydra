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
    private const string RemitenteAutomaticoEmail = "hydra-automatico@sistema.local";

    private record DocumentoCandidatoDto(
        Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl, DateOnly FechaEmision,
        EstadoVigenciaDocumento EstadoVigencia, DateOnly? FechaVencimiento);
    private record DocumentoParaZipDto(Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl);
    private record TrabajadorNombreDto(string Nombre, string Apellidos);

    /// <summary>
    /// Un grupo por (titular, tipo) con al menos una copia no vencida — sus copias en orden
    /// de preferencia, de las que viajará UNA —, los pares que se quedan fuera por tener solo
    /// copias vencidas y los pares que viajan sin que nadie haya confirmado su vigencia.
    /// </summary>
    private record SeleccionPaquete(
        IReadOnlyList<IReadOnlyList<DocumentoParaZipDto>> Enviar,
        IReadOnlyList<(Guid? TrabajadorId, Guid TipoDocumentoId)> SoloVencidos,
        IReadOnlyList<(Guid? TrabajadorId, Guid TipoDocumentoId)> SoloSinConfirmar);

    public async Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default)
    {
        var visita = await visitasContext.Visitas
            .Where(v => v.Id == visitaId)
            .Select(v => new { v.Id, v.CentroId, v.FechaInicio, v.FechaFin })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => new { c.Id, c.Nombre, c.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var candidatos = await documentosContext.Documentos
            .Where(d => d.ArchivoUrl != null && (d.EmpresaId == centro.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .Select(d => new DocumentoCandidatoDto(d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl!, d.FechaEmision, d.EstadoVigencia, d.FechaVencimiento))
            .ToListAsync(cancellationToken);

        if (candidatos.Count == 0)
        {
            logger.LogInformation("Visita {VisitaId}: sin documentos de empresa/trabajadores disponibles, no se genera paquete documental.", visitaId);
            return;
        }

        var seleccion = SeleccionarDocumentos(candidatos, DateOnly.FromDateTime(DateTime.UtcNow));
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

        if (seleccion.SoloSinConfirmar.Count > 0)
        {
            // Viajan porque no están vencidos y no hay otra copia, pero nadie ha confirmado
            // hasta cuándo valen: que conste, igual que lo omitido por vencido.
            logger.LogWarning(
                "Visita {VisitaId}: {Cantidad} documento(s) del paquete documental se envían sin vigencia confirmada (tipo/titular): {SinConfirmar}.",
                visitaId,
                seleccion.SoloSinConfirmar.Count,
                string.Join(", ", seleccion.SoloSinConfirmar.Select(o => $"{o.TipoDocumentoId}/{(o.TrabajadorId is { } t ? t.ToString() : "empresa")}")));
        }

        if (gruposAEnviar.Count == 0)
        {
            logger.LogWarning("Visita {VisitaId}: ningún documento vigente que enviar, no se genera paquete documental.", visitaId);
            return;
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
        if (zip is null) return; // ningún archivo pudo abrirse — no tiene sentido adjuntar un zip vacío.
        var (zipBytes, documentosAdjuntos) = zip.Value;

        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(conversacionId, cancellationToken);
        if (conversacion is null) return;

        var nombreZip = $"documentacion-visita-{centro.Nombre.Replace(' ', '-')}-{visita.FechaInicio:yyyyMMdd}.zip";
        using var flujoZip = new MemoryStream(zipBytes);
        var archivoUrlZip = await almacenamiento.GuardarAsync(flujoZip, nombreZip, cancellationToken);

        var cuerpo =
            $"""
            <p>Adjuntamos automáticamente la documentación disponible en la plataforma para la visita en <strong>{centro.Nombre}</strong>
            del {visita.FechaInicio:dd/MM/yyyy} al {visita.FechaFin:dd/MM/yyyy} ({documentosAdjuntos} documento(s)).</p>
            """;

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, RemitenteAutomaticoEmail, cuerpo);
        mensaje.AgregarAdjunto(nombreZip, "application/zip", zipBytes.LongLength, archivoUrlZip);
    }

    /// <summary>
    /// Regla del propietario (2026-09-20): al Cliente empresarial se le envían todos los
    /// documentos vigentes, y uno de cada uno; nunca los vencidos.
    ///
    /// <para>
    /// Un documento por (titular, tipo). Una copia con vigencia <b>comprobada</b> —vence en
    /// una fecha que no ha pasado, o confirmada como que no caduca— gana siempre a una
    /// <see cref="EstadoVigenciaDocumento.SinConfirmar"/>, sea cual sea su fecha de emisión:
    /// al Centro no se le manda un documento cuya vigencia nadie ha confirmado en lugar del
    /// que sí la tiene. Entre copias del mismo nivel gana la de mayor vigencia
    /// (<c>FechaVencimiento</c> más lejana; <see cref="EstadoVigenciaDocumento.NoCaduca"/>
    /// —p. ej. Formación 60h— cuenta como vigencia máxima), y a igualdad la más reciente
    /// (<c>FechaEmision</c>). Si aún empatan, el orden de la ruta del archivo: solo para
    /// que la elección no dependa del orden en que devuelva las filas la base.
    /// </para>
    ///
    /// <para>
    /// Si de un (titular, tipo) solo hay copias sin confirmar (y quizá vencidas), viaja la
    /// mejor sin confirmar —no está vencida, y es lo que hay— y el par se devuelve en
    /// <see cref="SeleccionPaquete.SoloSinConfirmar"/> para que conste en el log.
    /// </para>
    ///
    /// <para>
    /// Vencido es <c>FechaVencimiento &lt; hoy</c> (el umbral ámbar/rojo no interviene:
    /// Próximo y Urgente siguen vigentes), evaluado con <see cref="CalculadoraEstadoDocumento"/>
    /// para no duplicar la regla. Si de un (titular, tipo) solo hay copias vencidas no se
    /// envía ninguna y el par se devuelve en <see cref="SeleccionPaquete.SoloVencidos"/>:
    /// nunca se manda el vencido «por si acaso».
    /// </para>
    ///
    /// <para>
    /// Los candidatos ya vienen filtrados a los que tienen archivo: un documento sin
    /// archivo no puede viajar y no compite por el puesto. Cada grupo conserva todas sus
    /// copias vigentes en orden de preferencia: si el archivo de la primera no se puede
    /// abrir en el almacenamiento, <see cref="ConstruirZipAsync"/> prueba la siguiente —
    /// sigue siendo un documento por titular y tipo, y nunca uno vencido.
    /// </para>
    /// </summary>
    private static SeleccionPaquete SeleccionarDocumentos(IReadOnlyList<DocumentoCandidatoDto> candidatos, DateOnly hoy)
    {
        var enviar = new List<IReadOnlyList<DocumentoParaZipDto>>();
        var soloVencidos = new List<(Guid? TrabajadorId, Guid TipoDocumentoId)>();
        var soloSinConfirmar = new List<(Guid? TrabajadorId, Guid TipoDocumentoId)>();

        foreach (var grupo in candidatos.GroupBy(d => (d.TrabajadorId, d.TipoDocumentoId)))
        {
            // Los umbrales no afectan a "Vencido"; 0/0 basta y evita leer ParametrosSistema.
            var vigentes = grupo
                .Where(d => CalculadoraEstadoDocumento.Calcular(d.EstadoVigencia, d.FechaVencimiento, hoy, 0, 0) != EstadoDocumento.Vencido)
                .OrderByDescending(d => d.EstadoVigencia != EstadoVigenciaDocumento.SinConfirmar)
                .ThenByDescending(d => d.FechaVencimiento ?? DateOnly.MaxValue)
                .ThenByDescending(d => d.FechaEmision)
                .ThenBy(d => d.ArchivoUrl, StringComparer.Ordinal)
                .ToList();

            if (vigentes.Count == 0)
            {
                soloVencidos.Add(grupo.Key);
                continue;
            }

            if (vigentes[0].EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
                soloSinConfirmar.Add(grupo.Key);

            enviar.Add(vigentes.Select(d => new DocumentoParaZipDto(d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl)).ToList());
        }

        return new SeleccionPaquete(enviar, soloVencidos, soloSinConfirmar);
    }

    /// <summary>
    /// Devuelve null si ningún documento pudo abrirse (storage inconsistente) — mejor no adjuntar
    /// nada que adjuntar un zip vacío. De cada grupo entra UNA copia: la primera cuyo archivo se
    /// pueda abrir; si ninguna se abre, el grupo queda fuera (con un aviso por cada intento).
    /// Devuelve también cuántos documentos entraron de verdad, que es la cifra que anuncia el correo.
    /// </summary>
    private async Task<(byte[] Bytes, int Documentos)?> ConstruirZipAsync(
        IReadOnlyList<IReadOnlyList<DocumentoParaZipDto>> grupos,
        IReadOnlyDictionary<Guid, string> nombresTipoDocumento,
        string? razonSocialEmpresa,
        IReadOnlyDictionary<Guid, TrabajadorNombreDto> trabajadoresPorId,
        CancellationToken cancellationToken)
    {
        using var memoria = new MemoryStream();
        var nombresUsados = new HashSet<string>();
        var agregados = 0;

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

                    agregados++;
                    break; // uno por titular y tipo
                }
            }
        }

        return agregados > 0 ? (memoria.ToArray(), agregados) : null;
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
