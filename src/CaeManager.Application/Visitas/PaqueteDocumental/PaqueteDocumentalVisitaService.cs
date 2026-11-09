using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using System.Net;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Auditoria;
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
    IAlcanceDatosService alcanceDatos,
    IRegistroAccesoDocumentoSensibleService registroAcceso,
    ILogger<PaqueteDocumentalVisitaService> logger) : IPaqueteDocumentalVisitaService
{
    // Remitente distinto del de ResponderConversacionCommand
    // (equipo-cae@buzon-simulado.local) a propósito — este mensaje no lo
    // escribió ninguna persona, y el hilo debe dejarlo claro.
    private const string RemitenteAutomaticoEmail = "talveg-automatico@sistema.local";


    private record TrabajadorNombreDto(string Nombre, string Apellidos);

    public async Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default)
    {
        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(conversacionId, cancellationToken);
        if (conversacion is null) return;

        var paquete = await ConstruirAsync(visitaId, cancellationToken);
        if (paquete is null) return;

        using var flujoZip = new MemoryStream(paquete.Contenido);
        var archivoUrlZip = await almacenamiento.GuardarAsync(flujoZip, paquete.NombreArchivo, cancellationToken);

        var cuerpo =
            $"""
            <p>Adjuntamos automáticamente la documentación disponible en la plataforma para la visita en <strong>{WebUtility.HtmlEncode(paquete.CentroNombre)}</strong>
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

        var trabajadorIdsDeLaVisita = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        // Defensa en profundidad (S-B-1): los comandos de la Visita ya solo admiten Trabajadores de la
        // Empresa gestionada activa y visibles para quien opera, pero una Visita anterior a esa regla
        // —o creada por otro usuario con más alcance— puede llevar a alguien que quien genera el
        // paquete no ve. Su documentación no entra: el mismo criterio con el que se abre cada
        // Documento suelto (ObtenerDocumentoPorIdQuery, TrabajadorVisibleAsync).
        var trabajadorIds = await TrabajadoresAdmisiblesEnVisita.FiltrarAsync(
            trabajadorIdsDeLaVisita, centro.EmpresaId, trabajadoresContext, empresasContext, alcanceDatos, cancellationToken);
        if (trabajadorIds.Count < trabajadorIdsDeLaVisita.Count)
            logger.LogWarning(
                "Visita {VisitaId}: {Cantidad} trabajador(es) quedan fuera del paquete documental por no ser de la empresa de la visita o no estar en el alcance de quien lo genera.",
                visitaId, trabajadorIdsDeLaVisita.Count - trabajadorIds.Count);

        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => d.ArchivoUrl != null && (d.EmpresaId == centro.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .Select(d => new ArmadoPaqueteDocumental.Candidato(d.Id, d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl!, d.FechaEmision, d.EstadoVigencia, d.FechaVencimiento, d.CreadoEnUtc, d.EsRenuncia))
            .ToListAsync(cancellationToken);

        if (candidatos.Count == 0)
        {
            logger.LogInformation("Visita {VisitaId}: sin documentos de empresa/trabajadores disponibles, no se genera paquete documental.", visitaId);
            return null;
        }

        var seleccion = ArmadoPaqueteDocumental.Seleccionar(candidatos, DiaDeNegocio.Hoy());
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
        // DEC-36: solo los documentos incorporados cuentan como aperturas. El registro es
        // obligatorio antes de poner el paquete a disposición de cualquiera de las dos vías.
        foreach (var documento in incluidos)
            await registroAcceso.RegistrarSiSensibleAsync(documento.DocumentoId, TipoAccesoDocumentoSensible.Apertura, cancellationToken);

        return new PaqueteDocumentalZip(nombreZip, zipBytes, incluidos, centro.Nombre, visita.FechaInicio, visita.FechaFin);
    }


    /// <summary>
    /// La regla de selección (vigentes, uno por titular y tipo, el de emisión más reciente, nunca vencidos) y el armado del
    /// zip viven en <see cref="ArmadoPaqueteDocumental"/>, compartidos con el «Preparar paquete» de Empresa 360. Aquí solo se
    /// decide dónde va cada copia: «Trabajadores/» o «Empresa/», con el nombre «Tipo - titular». Lo usan el adjunto automático
    /// al buzón (<see cref="GenerarYEnviarAsync"/>) y la descarga manual del ZIP (<see cref="ConstruirAsync"/>), y los dos
    /// registran el acceso a lo sensible.
    /// </summary>
    private async Task<(byte[] Bytes, IReadOnlyList<DocumentoEnPaquete> Documentos)?> ConstruirZipAsync(
        IReadOnlyList<IReadOnlyList<ArmadoPaqueteDocumental.Copia>> grupos,
        IReadOnlyDictionary<Guid, string> nombresTipoDocumento,
        string? razonSocialEmpresa,
        IReadOnlyDictionary<Guid, TrabajadorNombreDto> trabajadoresPorId,
        CancellationToken cancellationToken)
    {
        var zip = await ArmadoPaqueteDocumental.ConstruirZipAsync(grupos, documento =>
        {
            var tipoNombre = nombresTipoDocumento.GetValueOrDefault(documento.TipoDocumentoId, "Documento");
            var carpeta = documento.TrabajadorId is not null ? "Trabajadores" : "Empresa";
            var titular = documento.TrabajadorId is not null && trabajadoresPorId.TryGetValue(documento.TrabajadorId.Value, out var trabajador)
                ? $"{trabajador.Nombre} {trabajador.Apellidos}"
                : razonSocialEmpresa ?? "Empresa";
            return (carpeta, $"{tipoNombre} - {titular}");
        }, almacenamiento, logger, cancellationToken);

        if (zip is null) return null;
        return (zip.Value.Bytes, zip.Value.Documentos.Select(d => new DocumentoEnPaquete(d.DocumentoId, d.TipoDocumentoId)).ToList());
    }
}
