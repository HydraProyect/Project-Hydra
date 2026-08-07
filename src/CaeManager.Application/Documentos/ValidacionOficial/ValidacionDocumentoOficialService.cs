using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.ValidacionOficial.Parsers;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.ValidacionOficial;

/// <summary>
/// El pipeline de la épica "Documentación mensual auto-validada"
/// (PLAN-FIRMA-DIGITAL-PDF.md): verifica criptográficamente la firma del
/// PDF, extrae los campos del perfil con un parser determinista (sin IA) y
/// coteja contra lo registrado. Si todo cuadra, el documento queda
/// <see cref="DecisionValidacionOficial.AutoValidado"/> — la excepción
/// aprobada a "el análisis nunca decide solo", reservada a documentos con
/// firma válida de emisor confiable. Cualquier otra cosa deja constancia
/// con motivos y (desde el PR-5 del plan, cuando la bandeja de revisión
/// sepa mostrar documentos de Empresa) una revisión pendiente.
///
/// Mismo criterio de "mejor esfuerzo" que el resto de la cola: un fallo
/// aquí jamás invalida el Documento ya guardado. Y el resultado siempre
/// refleja el archivo vigente: se borra el anterior y se recrea en la misma
/// transacción (delete-and-recreate).
/// </summary>
public class ValidacionDocumentoOficialService(
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    IClientesQueryContext clientesContext,
    IFileStorageService almacenamiento,
    IVerificadorFirmaPdfService verificadorFirma,
    IExtractorTextoDigitalService extractorTexto,
    IParserDocumentoOficialRegistry parsers,
    IFirmaDigitalDocumentoRepository firmasRepositorio,
    IVerificacionDocumentoOficialRepository verificacionesRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    IRevisionIaDocumentoRepository revisionRepositorio,
    IUnitOfWork unitOfWork,
    ILogger<ValidacionDocumentoOficialService> logger) : IValidacionDocumentoOficialService
{
    /// <summary>
    /// Umbral de auto-validación (decisión § 6.1/§ 4.4 del plan): también con
    /// revocación no comprobable — el emisor sigue siendo una CA fijada de la
    /// Administración, y un OCSP caído una mañana no debe generar revisiones
    /// espurias justo en la mensualidad. El nivel queda visible en el sello.
    /// </summary>
    private const NivelConfianzaDocumental NivelMinimoParaAutoValidar = NivelConfianzaDocumental.FirmaValidaSinRevocacion;

    public async Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
    {
        var documento = await documentosContext.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, cancellationToken);
        if (documento is null || string.IsNullOrWhiteSpace(documento.ArchivoUrl))
            return;

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null || tipoDocumento.PerfilDocumentoOficial == PerfilDocumentoOficial.Ninguno)
            return;

        byte[] contenido;
        try
        {
            await using var archivo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            using var buffer = new MemoryStream();
            await archivo.CopyToAsync(buffer, cancellationToken);
            contenido = buffer.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo abrir el archivo del Documento {DocumentoId} para validación oficial.", documentoId);
            return;
        }

        var perfil = tipoDocumento.PerfilDocumentoOficial;
        var resultadoFirmas = await verificadorFirma.VerificarAsync(contenido, cancellationToken);

        VerificacionDocumentoOficial verificacion;
        IReadOnlyList<FirmaPdfVerificada> firmas = [];

        if (resultadoFirmas.EsFallido)
        {
            verificacion = new VerificacionDocumentoOficial(
                documentoId, perfil, NivelConfianzaDocumental.SoloLectura,
                codigoVerificacion: null, cifDetectado: null, razonSocialDetectada: null,
                fechaEmisionDetectada: null, periodoDetectado: null,
                ResultadoCotejoDocumentoOficial.NoAplicable, DecisionValidacionOficial.SinFirmaValida,
                "El archivo no se pudo leer como PDF.");
        }
        else
        {
            firmas = resultadoFirmas.Valor.Firmas;
            verificacion = Decidir(
                documento, perfil, resultadoFirmas.Valor, contenido,
                await ObtenerCifPropietarioAsync(documento, cancellationToken));
        }

        await ReemplazarResultadosAsync(documentoId, verificacion, firmas, cancellationToken);

        if (verificacion.Decision == DecisionValidacionOficial.AutoValidado)
        {
            // Confianza 100: el cotejo es determinista (firma criptográfica +
            // texto digital exacto), no una estimación de un LLM — mantiene
            // comparables las estadísticas de AprobacionDocumento existentes.
            aprobacionRepositorio.Agregar(AprobacionDocumento.CrearAutomatica(documentoId, 100));
        }
        else
        {
            // Todo lo que no se auto-valida pasa por la bandeja de revisión
            // humana existente. Idempotencia igual que en
            // VerificacionIaDocumentoService: si ya hay una revisión sin
            // resolver de este documento, no se apila otra. Confianza 0 a
            // propósito: mantiene estas revisiones fuera del "Confirmar
            // todos los ≥85%" de la bandeja — un documento manipulado no
            // debe poder confirmarse en lote.
            var yaHayRevisionPendiente = await documentosContext.RevisionesIaDocumento
                .AnyAsync(r => r.DocumentoId == documentoId && !r.Resuelta, cancellationToken);
            if (!yaHayRevisionPendiente)
            {
                revisionRepositorio.Agregar(RevisionIaDocumento.Crear(
                    documentoId, confianzaGeneral: 0, tipoDetectado: null,
                    verificacion.FechaEmisionDetectada, fechaVencimientoDetectada: null,
                    tieneFirmaDetectada: verificacion.NivelConfianza > NivelConfianzaDocumental.SoloLectura
                        || verificacion.NivelConfianza == NivelConfianzaDocumental.Manipulado,
                    verificacion.Motivos));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private VerificacionDocumentoOficial Decidir(
        Documento documento, PerfilDocumentoOficial perfil, ResultadoVerificacionFirmasPdf firmas,
        byte[] contenido, string? cifPropietario)
    {
        var documentoId = documento.Id;
        var nivel = firmas.Nivel;

        // La extracción corre SIEMPRE que haya capa de texto, también sin
        // firma suficiente: el CEA/huella/periodo extraídos son el insumo de
        // la verificación oficial contra el organismo (épica 3) — decisión
        // del usuario: una reimpresión cuyo código el API del organismo
        // confirme podrá llegar a VerificadoOficialmente. Calibrado con
        // muestras reales de gestorías, donde la reimpresión es la norma.
        DatosDocumentoOficialExtraidos? extraidoPrevio = null;
        var parserDelPerfil = parsers.Obtener(perfil);
        var textoPrevio = extractorTexto.ExtraerTextoPorPagina(contenido);
        if (textoPrevio.EsExitoso && parserDelPerfil is not null)
        {
            var normalizado = ParserDocumentoOficialBase.NormalizarTexto(textoPrevio.Valor);
            if (normalizado.Length > 0)
                extraidoPrevio = parserDelPerfil.Extraer(normalizado);
        }

        if (nivel < NivelMinimoParaAutoValidar)
        {
            var motivo = nivel switch
            {
                NivelConfianzaDocumental.Manipulado =>
                    "La firma digital no cuadra con el contenido: el documento fue alterado después de firmarse.",
                NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable =>
                    "La firma es íntegra pero su emisor no es una CA reconocida de la Administración.",
                _ => firmas.AparentaReimpresion
                    ? "El archivo es una reimpresión o escaneo (imprimir a PDF destruye la firma digital) — pide el original descargado de la Sede."
                    : "El documento no está firmado digitalmente.",
            };
            return new VerificacionDocumentoOficial(
                documentoId, perfil, nivel,
                extraidoPrevio?.CodigoVerificacion, extraidoPrevio?.Cif, extraidoPrevio?.RazonSocial,
                extraidoPrevio?.FechaEmision, extraidoPrevio?.Periodo,
                ResultadoCotejoDocumentoOficial.NoAplicable, DecisionValidacionOficial.SinFirmaValida, motivo);
        }

        var resultadoTexto = textoPrevio;
        if (resultadoTexto.EsFallido)
        {
            return new VerificacionDocumentoOficial(
                documentoId, perfil, nivel, codigoVerificacion: null, cifDetectado: null,
                razonSocialDetectada: null, fechaEmisionDetectada: null, periodoDetectado: null,
                ResultadoCotejoDocumentoOficial.ParserSinDatos, DecisionValidacionOficial.RevisionRequerida,
                "No se pudo extraer el texto digital del documento.");
        }

        if (parserDelPerfil is null)
        {
            logger.LogError("No hay parser registrado para el perfil {Perfil}.", perfil);
            return new VerificacionDocumentoOficial(
                documentoId, perfil, nivel, codigoVerificacion: null, cifDetectado: null,
                razonSocialDetectada: null, fechaEmisionDetectada: null, periodoDetectado: null,
                ResultadoCotejoDocumentoOficial.ParserSinDatos, DecisionValidacionOficial.RevisionRequerida,
                "El perfil de documento oficial no tiene parser disponible.");
        }

        if (extraidoPrevio is null)
        {
            // Firmado pero sin ninguna capa de texto: no debería pasar con un
            // original de la Sede — a revisión, nunca auto-validar a ciegas.
            return new VerificacionDocumentoOficial(
                documentoId, perfil, nivel, codigoVerificacion: null, cifDetectado: null,
                razonSocialDetectada: null, fechaEmisionDetectada: null, periodoDetectado: null,
                ResultadoCotejoDocumentoOficial.ParserSinDatos, DecisionValidacionOficial.RevisionRequerida,
                "El documento firmado no tiene capa de texto que cotejar.");
        }

        var extraido = extraidoPrevio;
        var motivos = new List<string>();
        var cotejo = ResultadoCotejoDocumentoOficial.Coincide;

        if (extraido.CamposObligatoriosFaltantes.Count > 0)
        {
            cotejo = ResultadoCotejoDocumentoOficial.ParserSinDatos;
            motivos.Add($"No se reconocieron en el documento: {string.Join(", ", extraido.CamposObligatoriosFaltantes)}.");
        }
        else
        {
            if (extraido.ResultadoPositivo == false)
            {
                cotejo = ResultadoCotejoDocumentoOficial.Discrepancia;
                motivos.Add("El certificado está emitido EN NEGATIVO — declara que la empresa no está al corriente.");
            }

            if (string.IsNullOrWhiteSpace(cifPropietario))
            {
                // El cotejo de identidad es el corazón de la auto-validación
                // — sin CIF registrado no hay contra qué cotejar, así que
                // nunca se auto-valida (plan § 3.2).
                cotejo = ResultadoCotejoDocumentoOficial.NoAplicable;
                motivos.Add("El propietario del documento no tiene CIF registrado en la plataforma.");
            }
            else if (extraido.Cif is null)
            {
                // Calibración con muestras reales: RNT/RLC/ITA identifican a
                // la empresa por CCC, no traen CIF en el texto. Sin CIF
                // legible no hay cotejo de identidad — a revisión hasta que
                // exista el cotejo por CCC (pendiente: añadir CCC a Empresa).
                cotejo = ResultadoCotejoDocumentoOficial.NoAplicable;
                motivos.Add("El documento no trae un CIF legible con el que cotejar la identidad del propietario.");
            }
            else if (!string.Equals(extraido.Cif, ParserDocumentoOficialBase.NormalizarCif(cifPropietario), StringComparison.OrdinalIgnoreCase))
            {
                cotejo = ResultadoCotejoDocumentoOficial.Discrepancia;
                motivos.Add($"El CIF del documento ({extraido.Cif}) no coincide con el del propietario ({cifPropietario}).");
            }

            if (extraido.FechaEmision is { } fechaDetectada && fechaDetectada != documento.FechaEmision)
            {
                cotejo = ResultadoCotejoDocumentoOficial.Discrepancia;
                motivos.Add("La fecha de emisión introducida no coincide con la del documento.");
            }
        }

        var decision = motivos.Count == 0 ? DecisionValidacionOficial.AutoValidado : DecisionValidacionOficial.RevisionRequerida;

        return new VerificacionDocumentoOficial(
            documentoId, perfil, nivel, extraido.CodigoVerificacion, extraido.Cif,
            extraido.RazonSocial, extraido.FechaEmision, extraido.Periodo,
            cotejo, decision, string.Join(" ", motivos));
    }

    /// <summary>Los perfiles oficiales viven en tipos de Ámbito Empresa o Cliente — el CIF sale del propietario correspondiente.</summary>
    private async Task<string?> ObtenerCifPropietarioAsync(Documento documento, CancellationToken cancellationToken) =>
        documento.EmpresaId is { } empresaId
            ? await empresasContext.Empresas.Where(e => e.Id == empresaId).Select(e => e.Cif).FirstOrDefaultAsync(cancellationToken)
            : documento.ClienteId is { } clienteId
                ? await clientesContext.Clientes.Where(c => c.Id == clienteId).Select(c => c.Cif).FirstOrDefaultAsync(cancellationToken)
                : null;

    private async Task ReemplazarResultadosAsync(
        Guid documentoId, VerificacionDocumentoOficial verificacion,
        IReadOnlyList<FirmaPdfVerificada> firmas, CancellationToken cancellationToken)
    {
        var verificacionAnterior = await verificacionesRepositorio.ObtenerPorDocumentoAsync(documentoId, cancellationToken);
        if (verificacionAnterior is not null)
            verificacionesRepositorio.Eliminar(verificacionAnterior);

        var firmasAnteriores = await firmasRepositorio.ObtenerPorDocumentoAsync(documentoId, cancellationToken);
        if (firmasAnteriores.Count > 0)
            firmasRepositorio.EliminarDeDocumento(firmasAnteriores);

        verificacionesRepositorio.Agregar(verificacion);

        foreach (var firma in firmas)
        {
            // Restricción RGPD del plan (PR-4): nombre/NIF del firmante solo
            // se persisten cuando el certificado es un sello de órgano —
            // dato del organismo, no de una persona física. Al confirmarse
            // el tratamiento, esta condición desaparece.
            var esPersistible = firma.EsSelloDeOrgano;
            firmasRepositorio.Agregar(new FirmaDigitalDocumento(
                documentoId, firma.Indice, firma.Estado, firma.CadenaConfiable, firma.Revocacion,
                firma.EmisorCertificado, firma.NumeroSerieCertificado, firma.EsSelloDeOrgano,
                esPersistible ? firma.FirmanteNombre : null,
                esPersistible ? firma.FirmanteNif : null,
                firma.FechaFirmaUtc, firma.TieneSelloDeTiempo, firma.CubreDocumentoCompleto, firma.MotivoInvalidez));
        }
    }
}
