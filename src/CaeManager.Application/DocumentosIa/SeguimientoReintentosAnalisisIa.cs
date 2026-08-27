using CaeManager.Application.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Política de reintentos + reporte a Sentry para un <see cref="TrabajoAnalisisDocumento"/>
/// (D3, decisión del propietario del producto sobre el ruido en Sentry):
/// cada intento fallido deja una miga de pan
/// (<see cref="IAlertaOperativa.DejarMigaDePan"/>) en vez de generar su
/// propio evento, y solo se captura una excepción de verdad
/// (<see cref="IAlertaOperativa.CapturarExcepcion"/>) cuando el trabajo se
/// agota (<see cref="TrabajoAnalisisDocumento.MaximoIntentos"/>) o cuando el
/// fallo es definitivo desde el primer intento — así tres reintentos
/// transitorios del mismo documento generan UNA sola alerta, con el detalle
/// de los tres adjunto, en vez de tres alertas idénticas.
///
/// El aislamiento por trabajo (<see cref="AlEmpezarIntento"/>) es la parte
/// que no es gratis: <see cref="IAlertaOperativa.IniciarAmbitoDeCaptura"/>
/// abre un ámbito de Sentry nuevo solo cuando cambia el <c>Guid</c> del
/// trabajo respecto a la última llamada — mientras siga siendo el mismo
/// (reintentos consecutivos del mismo documento), se reutiliza el ámbito
/// para que las migas de los intentos anteriores sigan vivas cuando el
/// intento que agota captura el evento. Sin esto, las migas de un documento
/// se mezclarían con las del anterior o el siguiente en el mismo ámbito
/// global de Sentry.
///
/// Vive en Application, no en <c>ProcesadorAnalisisDocumentoHostedService</c>
/// (Infrastructure) que la usa: solo depende de <see cref="IAlertaOperativa"/>
/// y de <see cref="TrabajoAnalisisDocumento"/> — es política de reintento,
/// no mecánica de sondeo — así que se puede probar sin contenedor DI ni
/// Postgres real.
/// </summary>
public sealed class SeguimientoReintentosAnalisisIa(IAlertaOperativa alertaOperativa) : IDisposable
{
    private Guid? _trabajoIdEnCurso;
    private IDisposable? _ambitoCaptura;

    /// <summary>
    /// Se llama con CADA trabajo que se va a intentar, antes de ejecutarlo —
    /// abre un ámbito de captura nuevo (y cierra el anterior) solo cuando
    /// <paramref name="trabajoId"/> cambia respecto al último intento.
    /// </summary>
    public void AlEmpezarIntento(Guid trabajoId)
    {
        if (trabajoId == _trabajoIdEnCurso) return;

        _ambitoCaptura?.Dispose();
        _ambitoCaptura = alertaOperativa.IniciarAmbitoDeCaptura();
        _trabajoIdEnCurso = trabajoId;
    }

    /// <summary>
    /// Registra el fallo en <paramref name="trabajo"/> (<see cref="TrabajoAnalisisDocumento.RegistrarFalloDefinitivo"/>
    /// para <see cref="FileNotFoundException"/>, <see cref="TrabajoAnalisisDocumento.RegistrarFallo"/>
    /// para cualquier otra excepción) y decide si genera el evento de Sentry
    /// ahora (fallo definitivo, o el trabajo acaba de agotar sus reintentos)
    /// o solo deja una miga de pan (todavía quedan reintentos).
    /// </summary>
    public void RegistrarFallo(TrabajoAnalisisDocumento trabajo, Exception excepcion)
    {
        if (excepcion is FileNotFoundException)
        {
            trabajo.RegistrarFalloDefinitivo(excepcion.Message);
            alertaOperativa.CapturarExcepcion(excepcion);
            return;
        }

        trabajo.RegistrarFallo(excepcion.Message);

        if (trabajo.Estado == EstadoTrabajoAnalisisDocumento.Fallido)
        {
            alertaOperativa.CapturarExcepcion(excepcion);
        }
        else
        {
            alertaOperativa.DejarMigaDePan(
                $"Intento {trabajo.Intentos}/{TrabajoAnalisisDocumento.MaximoIntentos} de {trabajo.Tipo} " +
                $"falló para el documento {trabajo.DocumentoId}: {excepcion.GetType().Name} — {excepcion.Message}");
        }
    }

    public void Dispose() => _ambitoCaptura?.Dispose();
}
