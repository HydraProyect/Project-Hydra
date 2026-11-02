using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Un archivo subido por <c>InputFile</c> leído entero a memoria con su reserva del presupuesto del proceso
/// (<see cref="IPresupuestoMemoriaDocumentos"/>) ya hecha: ninguna pantalla debe cargar un archivo subido sin
/// reservar antes (OWASP API4:2023, ASVS 4.0.3 V12.1.1, CWE-400). El presupuesto es compartido entre Tenants, así
/// que una lectura sin reserva deja a todos sin memoria con unas pocas subidas simultáneas.
///
/// <para>
/// <b>Ciclo de la reserva.</b> <see cref="LeerAsync"/> reserva <c>Size × factor</c> ANTES de leer —el pico de la
/// lectura— y, una vez leído, la reduce a lo que el <see cref="Contenido"/> ocupa (<see cref="ReservaMemoriaDocumentos.ReducirA"/>).
/// Quien solo lee y sigue con el contenido (validarlo, guardarlo) suelta la reserva con <see cref="Dispose"/> ANTES
/// de llamar al almacenamiento, que reserva por su cuenta el claro y el cifrado y esperaría a esta reserva (y, con
/// otra petición mayor en cola entre las dos, se bloquearían hasta rechazarse). Quien retiene el contenido más allá de
/// la petición (el PDF elegido en el alta de una plantilla) conserva el objeto, y con él la reserva, hasta
/// sustituirlo, descartarlo o desmontarse.
/// </para>
/// </summary>
public sealed class ArchivoSubidoReservado : IDisposable
{
    /// <summary>
    /// Veces el tamaño declarado que la lectura reserva: el mismo factor que la Subida múltiple y el alta individual de
    /// documento (<see cref="Features.Documentos.Pages.SubidaMasiva.FactorMemoriaLectura"/>). Cubre el flujo, el búfer
    /// exacto y las comprobaciones que el llamador haga justo después sobre él.
    /// </summary>
    public const int FactorMemoriaLectura = Features.Documentos.Pages.SubidaMasiva.FactorMemoriaLectura;

    private readonly ReservaMemoriaDocumentos _reserva;

    private ArchivoSubidoReservado(byte[] contenido, ReservaMemoriaDocumentos reserva)
    {
        Contenido = contenido;
        _reserva = reserva;
    }

    public byte[] Contenido { get; }

    /// <summary>
    /// La reserva viva (tras la lectura, el tamaño del contenido). Quien retiene el contenido la conserva y la suelta él;
    /// <see cref="Dispose"/> hace lo mismo, así que no hace falta conservar también este objeto.
    /// </summary>
    public ReservaMemoriaDocumentos Reserva => _reserva;

    /// <summary>Devuelve la reserva al presupuesto; es idempotente. El <see cref="Contenido"/> sigue siendo del llamador.</summary>
    public void Dispose() => _reserva.Dispose();

    /// <summary>
    /// Reserva <c>Size × <see cref="FactorMemoriaLectura"/></c>, lee el archivo con tope en lo que declaró y devuelve el
    /// contenido con la reserva ajustada a su tamaño.
    /// </summary>
    /// <exception cref="PresupuestoMemoriaAgotadoException">No hubo hueco dentro de la espera máxima: no se leyó nada.</exception>
    /// <exception cref="IOException">
    /// El archivo supera <see cref="LimitesArchivoSubido.TamanoMaximoBytes"/>, o el flujo trajo más o menos bytes de los
    /// que declaró. La reserva ya hecha se devuelve antes de relanzar.
    /// </exception>
    public static async Task<ArchivoSubidoReservado> LeerAsync(
        IBrowserFile archivo, IPresupuestoMemoriaDocumentos presupuesto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        ArgumentNullException.ThrowIfNull(presupuesto);

        // Antes de reservar: un tamaño declarado absurdo no debe pedir (ni esperar) un presupuesto que no existe.
        if (archivo.Size < 0 || archivo.Size > LimitesArchivoSubido.TamanoMaximoBytes)
            throw new IOException($"El archivo supera los {LimitesArchivoSubido.TamanoMaximoMb} MB.");

        var reserva = await presupuesto.ReservarAsync(archivo.Size * FactorMemoriaLectura, cancellationToken);
        try
        {
            var contenido = new byte[archivo.Size];
            await using var flujo = archivo.OpenReadStream(LimitesArchivoSubido.TamanoMaximoBytes, cancellationToken);
            await flujo.ReadExactlyAsync(contenido, cancellationToken);

            // Un flujo que trae más de lo que declaró no se trunca en silencio: se rechaza.
            var sonda = new byte[1];
            if (await flujo.ReadAsync(sonda, cancellationToken) > 0)
                throw new IOException("El archivo trae más bytes de los que declaró.");

            // Pasado el pico de la lectura solo queda el búfer exacto.
            reserva.ReducirA(contenido.Length);
            return new ArchivoSubidoReservado(contenido, reserva);
        }
        catch
        {
            reserva.Dispose();
            throw;
        }
    }
}
