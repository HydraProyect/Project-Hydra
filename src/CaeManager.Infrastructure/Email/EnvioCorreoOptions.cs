using System.Net.Mail;
using CaeManager.Domain.Common;

namespace CaeManager.Infrastructure.Email;

/// <summary>Hasta dónde deja salir correo real la guarda de envío.</summary>
public enum ModoEnvioCorreo
{
    /// <summary>No sale ningún correo. Es el valor por defecto en todo entorno.</summary>
    Cerrado,

    /// <summary>Solo salen correos a las direcciones exactas de <see cref="EnvioCorreoOptions.DestinatariosPermitidos"/>.</summary>
    SoloPermitidos,

    /// <summary>Sale todo. Es el modo de producción.</summary>
    Abierto,
}

/// <summary>
/// Guarda de envío (C-04, decisión D-2): la salida de correo real por SMTP y por Microsoft 365
/// nace cerrada y solo se abre con configuración explícita, validada al arrancar. No se decide por
/// <c>IHostEnvironment</c> porque staging arranca como Production y no se distinguiría.
///
/// <para>
/// La variable de entorno es <c>EnvioCorreo__Modo</c>. Sin ella la instancia arranca cerrada y lo
/// avisa (<see cref="AvisoGuardaEnvioCorreoHostedService"/>); con un valor que no se reconoce, o con
/// una lista incoherente con el modo, no arranca: un error de escritura en el modo no puede
/// convertirse en una salida abierta ni en una cerrada sin que nadie lo sepa.
/// </para>
/// </summary>
public sealed class EnvioCorreoOptions
{
    public const string SeccionConfiguracion = "EnvioCorreo";

    public const string CodigoErrorEnvioCerrado = "Email.EnvioCerrado";

    /// <summary>
    /// Inicio de la línea de log de cada bloqueo. El paso «Comprobar que el correo está apagado» de
    /// <c>seguridad-codigo.yml</c> la busca literalmente.
    /// </summary>
    public const string TextoBloqueo = "Envío de correo bloqueado por la guarda";

    /// <summary><c>Cerrado</c> (o sin valor), <c>SoloPermitidos</c> o <c>Abierto</c>, sin distinguir mayúsculas.</summary>
    public string? Modo { get; set; }

    /// <summary>
    /// Direcciones exactas a las que deja salir <see cref="ModoEnvioCorreo.SoloPermitidos"/>. No admite
    /// dominios ni nombre visible, para que comparar sea igualdad de cadenas y no un análisis de direcciones.
    /// </summary>
    public string[] DestinatariosPermitidos { get; set; } = [];

    private ModoEnvioCorreo? ModoReconocido =>
        string.IsNullOrWhiteSpace(Modo)
            ? ModoEnvioCorreo.Cerrado
            : Modo.ToLowerInvariant() switch
            {
                "cerrado" => ModoEnvioCorreo.Cerrado,
                "solopermitidos" => ModoEnvioCorreo.SoloPermitidos,
                "abierto" => ModoEnvioCorreo.Abierto,
                _ => null,
            };

    /// <summary>Lo que no se reconoce cierra: si la validación no ha corrido, la guarda sigue sin abrir.</summary>
    public ModoEnvioCorreo ModoEfectivo => ModoReconocido ?? ModoEnvioCorreo.Cerrado;

    public bool EsValida()
    {
        var modo = ModoReconocido;
        if (modo is null || !DestinatariosPermitidos.All(EsDireccionExacta))
            return false;

        return modo switch
        {
            ModoEnvioCorreo.SoloPermitidos => DestinatariosPermitidos.Length > 0,
            // Con Abierto la lista no filtraría nada, y quien la lea creería lo contrario.
            ModoEnvioCorreo.Abierto => DestinatariosPermitidos.Length == 0,
            _ => true,
        };
    }

    /// <summary>
    /// Si puede salir un correo a todos estos destinatarios, juntos: basta uno fuera de la lista
    /// para que no salga ninguno. Sin destinatarios conocidos, solo sale con la guarda abierta.
    /// </summary>
    public bool PermiteEnvioA(IReadOnlyCollection<string> destinatarios) => ModoEfectivo switch
    {
        ModoEnvioCorreo.Abierto => true,
        ModoEnvioCorreo.SoloPermitidos => destinatarios.Count > 0 && destinatarios.All(EstaEnLaLista),
        _ => false,
    };

    public Error ErrorEnvioCerrado() =>
        Error.Crear(CodigoErrorEnvioCerrado, "El envío de correo está cerrado en este entorno.");

    private bool EstaEnLaLista(string destinatario) =>
        DestinatariosPermitidos.Contains(destinatario, StringComparer.OrdinalIgnoreCase);

    private static bool EsDireccionExacta(string entrada) =>
        MailAddress.TryCreate(entrada, out var direccion) && direccion.Address == entrada;
}
