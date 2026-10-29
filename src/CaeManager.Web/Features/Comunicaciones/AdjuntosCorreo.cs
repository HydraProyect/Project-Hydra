using CaeManager.Application.Integraciones;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Comunicaciones;

public sealed class AdjuntosCorreo
{
    public const int MaximoArchivos = 5;
    private bool _leyendo;

    public async Task<string?> AgregarAsync(InputFileChangeEventArgs seleccion, List<AdjuntoParaEnviarDto> adjuntos)
    {
        if (_leyendo) return "Espera a que termine la carga de los adjuntos.";
        if (seleccion.FileCount > MaximoArchivos) return $"Puedes adjuntar un máximo de {MaximoArchivos} archivos.";

        _leyendo = true;
        try
        {
            string? error = null;
            foreach (var archivo in seleccion.GetMultipleFiles(MaximoArchivos))
            {
                if (adjuntos.Count >= MaximoArchivos)
                {
                    error = $"Puedes adjuntar un máximo de {MaximoArchivos} archivos.";
                    break;
                }

                var restante = LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes - adjuntos.Sum(a => a.Contenido.LongLength);
                if (archivo.Size < 0 || archivo.Size > restante)
                {
                    error = "Los adjuntos no pueden superar 3 MB en total.";
                    continue;
                }

                try
                {
                    await using var flujo = archivo.OpenReadStream(restante);
                    using var memoria = new MemoryStream();
                    var buffer = new byte[81920];
                    int leidos;
                    while ((leidos = await flujo.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, restante - memoria.Length + 1)))) > 0)
                    {
                        if (leidos > restante - memoria.Length) throw new IOException();
                        await memoria.WriteAsync(buffer.AsMemory(0, leidos));
                    }
                    adjuntos.Add(new AdjuntoParaEnviarDto(archivo.Name, archivo.ContentType, memoria.ToArray()));
                }
                catch (IOException)
                {
                    error = "No se pudo leer el adjunto o supera el espacio disponible de los 3 MB.";
                }
            }
            return error;
        }
        finally { _leyendo = false; }
    }
}
