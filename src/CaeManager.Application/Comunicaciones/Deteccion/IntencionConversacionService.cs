using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Centros;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Deteccion;

public record PropuestaIntencionCae(IntencionCae Intencion, ConfianzaIntencion Confianza);

public interface IDeteccionIntencionCaeService
{
    Task<Result<PropuestaIntencionCae>> DetectarAsync(string texto, CancellationToken cancellationToken = default);
}

public interface IIntencionConversacionService
{
    Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default);
}

public class IntencionConversacionService(
    IDeteccionIntencionCaeService deteccion,
    IIntencionConversacionRepository repositorio,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    ITrabajadoresQueryContext trabajadores,
    IEmpresasQueryContext empresas,
    ICentrosQueryContext centros,
    ITiposDocumentoQueryContext tiposDocumento,
    ILogger<IntencionConversacionService> logger) : IIntencionConversacionService
{
    // Nunca rompe la ingesta: un fallo de la detección se registra y el mensaje entra igual.
    public async Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default)
    {
        try { await ProcesarSinAislarAsync(conversacion, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "La detección de intención CAE de la conversación {ConversacionId} falló; la ingesta sigue.", conversacion.Id);
        }
    }

    private async Task ProcesarSinAislarAsync(Conversacion conversacion, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId) return;
        // Solo lo que la cartera atiende: no sale al proveedor un hilo sin Cliente empresarial ni Empresa.
        if (conversacion.ClienteId is null && conversacion.EmpresaId is null)
            return;
        var entrantes = conversacion.Mensajes.Where(m => m.Direccion == DireccionMensaje.Entrante).ToList();
        if (entrantes.Count != 1 || entrantes[0].EsAutomatico
            || await repositorio.ObtenerPorConversacionIdAsync(conversacion.Id, cancellationToken) is not null)
            return;

        var mensaje = entrantes[0];
        var texto = WebUtility.HtmlDecode(Regex.Replace(mensaje.CuerpoHtml, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        texto = (conversacion.Asunto + "\n" + texto);
        texto = texto[..Math.Min(texto.Length, 12000)];
        // Fallo cerrado: sin Nivel 0 o sin respuesta del proveedor no se propone nada. Las reglas por palabras
        // no sustituyen a la IA: con confianza Alta contaminarían las métricas sin que nadie las confirmara.
        if (!await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return;
        var resultado = await deteccion.DetectarAsync(texto, cancellationToken);
        if (resultado.EsFallido) return;
        var propuesta = resultado.Valor;

        var candidatos = new List<CandidatoEntidadIntencion>();
        candidatos.AddRange(await trabajadores.Trabajadores.Select(t => new CandidatoEntidadIntencion(t.Id, TipoEntidadIntencion.Trabajador, t.Nombre + " " + t.Apellidos)).ToListAsync(cancellationToken));
        candidatos.AddRange(await empresas.Empresas.Select(e => new CandidatoEntidadIntencion(e.Id, TipoEntidadIntencion.Empresa, e.RazonSocial)).ToListAsync(cancellationToken));
        candidatos.AddRange(await centros.Centros.Select(c => new CandidatoEntidadIntencion(c.Id, TipoEntidadIntencion.Centro, c.Nombre)).ToListAsync(cancellationToken));
        candidatos.AddRange(await tiposDocumento.TiposDocumento.Select(t => new CandidatoEntidadIntencion(t.Id, TipoEntidadIntencion.TipoDocumento, t.Nombre)).ToListAsync(cancellationToken));
        var entidad = ResolverEntidad(texto, candidatos);
        repositorio.Agregar(new IntencionConversacion(conversacion.Id, mensaje.Id, propuesta.Intencion, propuesta.Confianza, entidad?.Tipo, entidad?.Id));
    }

    public static PropuestaIntencionCae ClasificarPorReglas(string texto)
    {
        var normalizado = Normalizar(texto);
        var reglas = new (IntencionCae Intencion, string[] Palabras)[]
        {
            (IntencionCae.UrgenciaAcceso, ["emergencia", "acceso bloqueado", "urgencia de acceso"]),
            (IntencionCae.AltaContratoAccesoCentro, ["alta de contrato", "procedimiento de acceso"]),
            (IntencionCae.DocumentacionMensual, ["documentacion mensual", "documentos mensuales"]),
            (IntencionCae.ConsultaRequisitos, ["que falta", "trabajador extranjero", "consulta de requisitos"]),
            (IntencionCae.CopiaProyectoObra, ["en copia", "hilo de proyecto", "hilo de obra"]),
            (IntencionCae.CoordinacionAcceso, ["coordinar acceso", "coordinacion de acceso", "solicitud de acceso"])
        };
        var coincidencias = reglas.Where(r => r.Palabras.Any(normalizado.Contains)).ToList();
        if (coincidencias.Count == 0) return new(IntencionCae.Otras, ConfianzaIntencion.Baja);
        return new(coincidencias[0].Intencion, coincidencias.Count == 1 ? ConfianzaIntencion.Alta : ConfianzaIntencion.Media);
    }

    public record CandidatoEntidadIntencion(Guid Id, TipoEntidadIntencion Tipo, string Nombre);

    public static CandidatoEntidadIntencion? ResolverEntidad(string texto, IReadOnlyList<CandidatoEntidadIntencion> candidatos)
    {
        var normalizado = Normalizar(texto);
        var coincidencias = candidatos.Where(c => c.Nombre.Trim().Length >= 4 &&
            Regex.IsMatch(normalizado, @"(?<!\w)" + Regex.Escape(Normalizar(c.Nombre.Trim())) + @"(?!\w)", RegexOptions.None, TimeSpan.FromSeconds(1))).ToList();
        return coincidencias.Count == 1 ? coincidencias[0] : null;
    }

    private static string Normalizar(string texto) => new string(texto.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
}
