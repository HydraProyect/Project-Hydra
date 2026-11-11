using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>
/// Límite de consultas por CIF del alta autónoma (§ 4.1 del plan: 3 al día; ASVS 4.0.3 V11.1.4): cuántas veces al día
/// puede llegar a la base un mismo CIF o NIF en el paso «Empresa», venga de la IP que venga. Frena el barrido de CIF para
/// averiguar cuáles ya están en TALVEG, que la limitación por IP sola no corta si se reparte entre muchas direcciones.
///
/// <para>
/// No es una política de <c>AddRateLimiter</c>: el CIF viaja en el cuerpo del formulario, y el limitador decide antes de
/// leerlo. Lo consulta la página, después de validar el CIF (formato y dígito de control) y antes de llamar a la base:
/// un CIF mal escrito no gasta cupo, porque no consulta nada.
/// </para>
///
/// <para>
/// La clave de cada partición es un HMAC-SHA256 del CIF normalizado con una clave aleatoria de esta instancia, nunca el
/// CIF en claro: el NIF de una persona física es un dato personal (DA-11), y sin la clave no se puede ir de la partición
/// al CIF probando los ~10^8 posibles. Los contadores viven en memoria por instancia y se reinician con cada despliegue,
/// como los del resto del alta (<see cref="CupoDiarioAlta"/>).
/// </para>
/// </summary>
public sealed class CupoConsultasCifAlta : IDisposable
{
    private readonly byte[] _clave = RandomNumberGenerator.GetBytes(32);
    private readonly PartitionedRateLimiter<string> _limitador;

    public CupoConsultasCifAlta(IOptions<OpcionesAntiabusoAlta> opciones)
        : this(opciones.Value.ConsultasPorDiaPorCif, TimeSpan.FromDays(1))
    {
    }

    /// <summary>Con la ventana a medida: solo para las pruebas.</summary>
    public CupoConsultasCifAlta(int consultasPorDia, TimeSpan ventana) =>
        _limitador = PartitionedRateLimiter.Create<string, string>(clave =>
            RateLimitPartition.Get(clave, _ => LimitadorAlta.VentanaFija(consultasPorDia, ventana)));

    /// <summary>
    /// Gasta una consulta del CIF ya normalizado. <c>false</c> si ese CIF agotó su cupo del día: entonces la página no
    /// consulta la base y responde lo mismo exista o no un Tenant propietario con ese CIF.
    /// </summary>
    public bool IntentarConsumir(string cifNormalizado)
    {
        ArgumentException.ThrowIfNullOrEmpty(cifNormalizado);
        using var concesion = _limitador.AttemptAcquire(Clave(cifNormalizado));
        return concesion.IsAcquired;
    }

    private string Clave(string cifNormalizado) =>
        Convert.ToHexString(HMACSHA256.HashData(_clave, Encoding.UTF8.GetBytes(cifNormalizado)));

    public void Dispose() => _limitador.Dispose();
}
