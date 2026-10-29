using CaeManager.Application.Integraciones;
using CaeManager.Web.Features.Comunicaciones.Recursos;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>Motivo por el que <see cref="AdjuntosCorreo.AgregarAsync"/> no ha adjuntado alguno de los archivos elegidos.</summary>
public enum RechazoAdjunto
{
    Ninguno = 0,
    CargaEnCurso,
    DemasiadosArchivos,
    ExcedeElTotal,
    NoSePudoLeer,
}

/// <summary>
/// Acumula en memoria los adjuntos de un correo que se está redactando (Buzón y Bandeja) sin pasarse del
/// límite del comando de envío: <see cref="LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes"/> en total
/// y <see cref="MaximoArchivos"/> archivos, contando lo ya adjuntado en selecciones anteriores. El presupuesto
/// restante se calcula ANTES de abrir cada flujo: un archivo cuyo tamaño declarado lo supera se rechaza sin
/// abrirlo, y la lectura de los demás se acota al presupuesto, sin fiarse solo del tamaño declarado por el
/// navegador. Lo rechazado nunca llega a la lista ni se conserva en memoria
/// (OWASP API4:2023, ASVS 4.0.3 V12.1.1, CWE-400).
/// </summary>
public sealed class AdjuntosCorreo
{
    /// <summary>Número máximo de adjuntos acumulados en un mismo correo.</summary>
    public const int MaximoArchivos = 5;

    private const int TamanoBufferBytes = 81920;
    private bool _leyendo;

    /// <summary>
    /// Añade a <paramref name="adjuntos"/> los archivos de <paramref name="seleccion"/> que caben en el
    /// presupuesto restante. Devuelve el motivo del último rechazo, o <see cref="RechazoAdjunto.Ninguno"/> si
    /// todos se han adjuntado.
    /// </summary>
    public async Task<RechazoAdjunto> AgregarAsync(InputFileChangeEventArgs seleccion, List<AdjuntoParaEnviarDto> adjuntos)
    {
        if (_leyendo) return RechazoAdjunto.CargaEnCurso;
        if (seleccion.FileCount > MaximoArchivos) return RechazoAdjunto.DemasiadosArchivos;

        _leyendo = true;
        try
        {
            var rechazo = RechazoAdjunto.Ninguno;
            foreach (var archivo in seleccion.GetMultipleFiles(MaximoArchivos))
            {
                if (adjuntos.Count >= MaximoArchivos)
                {
                    rechazo = RechazoAdjunto.DemasiadosArchivos;
                    break;
                }

                var restante = LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes - adjuntos.Sum(a => a.Contenido.LongLength);
                if (archivo.Size < 0 || archivo.Size > restante)
                {
                    rechazo = RechazoAdjunto.ExcedeElTotal;
                    continue;
                }

                try
                {
                    await using var flujo = archivo.OpenReadStream(restante);
                    using var memoria = new MemoryStream();
                    var buffer = new byte[TamanoBufferBytes];
                    int leidos;
                    // Se pide como máximo un byte más que el presupuesto para detectar un flujo mayor que el declarado.
                    while ((leidos = await flujo.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, restante - memoria.Length + 1)))) > 0)
                    {
                        if (leidos > restante - memoria.Length) throw new IOException();
                        await memoria.WriteAsync(buffer.AsMemory(0, leidos));
                    }
                    adjuntos.Add(new AdjuntoParaEnviarDto(archivo.Name, archivo.ContentType, memoria.ToArray()));
                }
                catch (IOException)
                {
                    rechazo = RechazoAdjunto.NoSePudoLeer;
                }
            }
            return rechazo;
        }
        finally { _leyendo = false; }
    }

    /// <summary>Texto localizado para el usuario del rechazo; <c>null</c> si no hubo rechazo.</summary>
    public static string? Mensaje(RechazoAdjunto rechazo, IStringLocalizer<TextosErroresConversaciones> textos)
    {
        var megabytes = LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes / (1024 * 1024);
        return rechazo switch
        {
            RechazoAdjunto.CargaEnCurso => textos["ErrorAdjuntosCargaEnCurso"].Value,
            RechazoAdjunto.DemasiadosArchivos => textos["ErrorAdjuntosMaximoArchivos", MaximoArchivos].Value,
            RechazoAdjunto.ExcedeElTotal => textos["ErrorAdjuntosExcedenElTotal", megabytes].Value,
            RechazoAdjunto.NoSePudoLeer => textos["ErrorAdjuntoNoSePudoLeer", megabytes].Value,
            _ => null,
        };
    }
}
