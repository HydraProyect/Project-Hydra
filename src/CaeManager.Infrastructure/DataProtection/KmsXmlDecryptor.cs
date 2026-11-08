using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Contrapartida de <see cref="KmsXmlEncryptor"/>. Data Protection lo
/// instancia a través del contenedor de servicios usando el
/// <c>decryptorType</c> que quedó escrito en el XML, por eso recibe
/// <see cref="IServiceProvider"/> y no las opciones directamente.
///
/// No hace falta indicar la clave: el texto cifrado de KMS lleva dentro qué
/// clave lo cifró, así que descifrar sigue funcionando después de rotarla —
/// que es justo lo que permite rotar sin quedarse sin poder leer lo anterior.
/// </summary>
public class KmsXmlDecryptor : IXmlDecryptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _limite;
    private readonly TaskScheduler _planificador;

    // Data Protection construye el descifrador con este constructor público
    // (recibe IServiceProvider); los internos solo existen para acotar el límite
    // de tiempo y elegir el planificador en los tests.
    public KmsXmlDecryptor(IServiceProvider serviceProvider)
        : this(serviceProvider, LlamadaSincronaAws.LimitePorDefecto)
    {
    }

    internal KmsXmlDecryptor(IServiceProvider serviceProvider, TimeSpan limite)
        : this(serviceProvider, limite, TaskScheduler.Default)
    {
    }

    internal KmsXmlDecryptor(IServiceProvider serviceProvider, TimeSpan limite, TaskScheduler planificador)
    {
        _serviceProvider = serviceProvider;
        _limite = limite;
        _planificador = planificador;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var kms = _serviceProvider.GetService(typeof(IAmazonKeyManagementService)) as IAmazonKeyManagementService
            ?? throw new InvalidOperationException(
                "Hay claves de Data Protection cifradas con KMS pero KMS no está configurado. " +
                "Sin él no se pueden descifrar las credenciales guardadas: revisa DataProtection:Kms en la configuración.");

        var cifrado = Convert.FromBase64String(encryptedElement.Value);

        // Data Protection descifra el descriptor de una clave la primera vez que
        // la usa, en el hilo de quien la usa (que puede ser un circuito de
        // Blazor): ver LlamadaSincronaAws para el porqué del puente.
        // Si la respuesta llega después del límite, nadie la usa: su texto plano se
        // borra en cuanto llega en vez de quedar en una tarea abandonada.
        var respuesta = LlamadaSincronaAws.Ejecutar(
            token => kms.DecryptAsync(new DecryptRequest
            {
                CiphertextBlob = new MemoryStream(cifrado)
            }, token),
            _limite,
            "El descifrado con KMS de una clave de Data Protection",
            static tardia => _ = BorrarFlujo(tardia.Plaintext),
            liberarEntrada: null,
            _planificador);

        var textoPlano = respuesta.Plaintext.ToArray();
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(textoPlano));
        }
        finally
        {
            // El XElement devuelto es lo que Data Protection necesita; las copias
            // intermedias en bytes no tienen por qué sobrevivir a esta llamada.
            CryptographicOperations.ZeroMemory(textoPlano);
            _ = BorrarFlujo(respuesta.Plaintext);
        }
    }

    /// <summary>
    /// Sobrescribe con ceros el contenido de un flujo en memoria y lo libera. Primero
    /// intenta el búfer subyacente (<see cref="MemoryStream.TryGetBuffer"/>), que se puede
    /// borrar aunque el flujo sea de solo lectura; si no está expuesto, sobrescribe a
    /// través del flujo cuando admite escritura y desplazamiento. El <c>Plaintext</c> que
    /// construye AWSSDK.KeyManagementService 4.0.100.14 al deserializar la respuesta queda
    /// a ceros por una de esas dos vías (lo comprueba un test contra el cliente real, con
    /// un manejador HTTP de prueba). Un flujo de solo lectura cuyo búfer no esté expuesto
    /// no se puede borrar sin reflexión: se libera y se devuelve <c>false</c>. Las copias
    /// que el SDK o la pila HTTP hagan del cuerpo de la respuesta (el Base64) quedan fuera.
    ///
    /// No lanza, tampoco si falla <see cref="Stream.Dispose()"/>: se usa en un
    /// <c>finally</c>, donde una excepción sustituiría al resultado de
    /// <see cref="Decrypt"/>, y sobre respuestas tardías, donde nadie la observaría.
    /// </summary>
    /// <returns><c>true</c> si el contenido visible del flujo quedó a ceros.</returns>
    internal static bool BorrarFlujo(MemoryStream? flujo)
    {
        if (flujo is null)
        {
            return true;
        }

        var borrado = false;
        try
        {
            if (flujo.TryGetBuffer(out var bufer))
            {
                CryptographicOperations.ZeroMemory(bufer.AsSpan());
                borrado = true;
            }
            else if (flujo.CanWrite && flujo.CanSeek)
            {
                var longitud = checked((int)flujo.Length);
                flujo.Position = 0;
                flujo.Write(new byte[longitud]);
                borrado = true;
            }
        }
        catch (Exception)
        {
            // Borrado de mejor esfuerzo: la excepción se observa aquí y el flujo se
            // libera igualmente.
            borrado = false;
        }

        try
        {
            flujo.Dispose();
        }
        catch (Exception)
        {
            // Liberar es de mejor esfuerzo: un fallo aquí no altera el resultado.
        }

        return borrado;
    }
}
