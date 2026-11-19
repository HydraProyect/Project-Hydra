using System.Globalization;
using System.Text.RegularExpressions;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;

/// <summary>
/// Conector de la API oficial de CTAIMACAE.net. Lo elige la puerta
/// (<c>OperacionesConectorPlataformaCae</c>) por <see cref="Codigo"/>, que es el del proveedor sembrado
/// con el dominio <c>ctaimacae.net</c>: Twind y e-coordina son proveedores aparte aunque compartan grupo
/// (ver <see cref="ProveedorPlataformaCae"/>), y la definición OpenAPI de esta API nombra CTAIMACAE.net.
///
/// <para>
/// Nace sin poder llamar: el interruptor del proveedor está apagado en la semilla y, aunque se
/// encendiera, el <c>tenantId</c> que la API exige no llega todavía al conector (ver
/// <see cref="DarDeAltaTrabajadorAsync(CredencialApiConector, SolicitudAltaTrabajador, CancellationToken)"/>).
/// No repite ninguna comprobación de la puerta y no lee la base. <c>internal</c>: fuera de
/// Infrastructure solo se alcanza como <see cref="IConectorPlataformaCae"/>, por la puerta.
/// </para>
/// </summary>
internal sealed partial class ConectorCtaimaCae(CtaimaCaeClient cliente) : IConectorPlataformaCae
{
    public const string Codigo = "ctaimacae-legacy";

    public const string TipoDocumentoNif = "NIF";
    public const string TipoDocumentoOtro = "Others";

    public string CodigoProveedor => Codigo;

    // shortcut: el estado de un documento en CTAIMACAE.net se lee por el CIF de la Empresa cliente en la
    // plataforma, el NIF del Trabajador y el requirementId (employee-invalid-documents,
    // client-employee-requirements), y SolicitudLecturaEstado solo trae los Id de TALVEG. Hasta que se decida
    // cómo llegan esos datos, no se llama.
    public Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<EstadoDocumentoEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes));

    // shortcut: client-employee-document exige el CIF de la Empresa cliente en la plataforma, el NIF del
    // Trabajador, el CIF de la sociedad contratista, el requirementId y las fechas de expedición y caducidad, y
    // SolicitudSubidaDocumento solo trae los Id de TALVEG y el contenido. Hasta que se decida cómo llegan esos
    // datos, no se llama.
    public Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes));

    // shortcut: ConexionPlataformaCae no guarda el tenantId de CTAIMACAE.net y la puerta solo pasa la
    // credencial; hasta que se decida de dónde sale, el alta por la puerta no llama.
    public Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken) =>
        DarDeAltaTrabajadorAsync(credencial, tenantIdCtaima: null, solicitud, cancellationToken);

    /// <summary>
    /// El alta completa en la gestión interna del contratista que identifica <paramref name="tenantIdCtaima"/>.
    /// Solo da de alta; vincular el Trabajador a una Empresa cliente o a un contrato en la plataforma es otra
    /// operación de la API. <c>internal</c> para que el <c>tenantId</c>, que elige la cuenta sobre la que se
    /// escribe, no lo pueda pasar nadie más que este conector hasta que la puerta lo entregue.
    /// </summary>
    internal async Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, string? tenantIdCtaima, SolicitudAltaTrabajador solicitud,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantIdCtaima)
            || string.IsNullOrWhiteSpace(solicitud.Dni)
            || string.IsNullOrWhiteSpace(solicitud.Nombre)
            || string.IsNullOrWhiteSpace(solicitud.Apellidos))
            return ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.DatosInsuficientes);

        var resultado = await cliente.CrearTrabajadorInternoAsync(
            credencial, tenantIdCtaima, PeticionDeAlta(solicitud), cancellationToken);

        if (resultado.Fallo is { } fallo)
            return ResultadoConector<ReferenciaEnPlataforma>.Falla(fallo);

        // employeeId es el id que piden las rutas /api/internal-employees/{id}; el guid, la alternativa. Lo que
        // no sea ninguno de los dos no se devuelve: sería texto del tercero camino de guardarse.
        var creado = resultado.Valor;
        if (creado.EmployeeId > 0)
            return ResultadoConector<ReferenciaEnPlataforma>.Exito(
                new ReferenciaEnPlataforma(creado.EmployeeId.ToString(CultureInfo.InvariantCulture)));

        return Guid.TryParse(creado.Guid, out var guid) && guid != Guid.Empty
            ? ResultadoConector<ReferenciaEnPlataforma>.Exito(new ReferenciaEnPlataforma(guid.ToString("D")))
            : ResultadoConector<ReferenciaEnPlataforma>.Falla(MotivoUsoConector.Inesperado);
    }

    /// <summary>
    /// DNI y NIE son NIF en CTAIMACAE.net, y se envían sin espacios, guiones ni puntos para que casen por NIF
    /// en las demás rutas de la API. Cualquier otro documento va tal cual, como «Others», porque desde el texto
    /// no se distingue un pasaporte de otro identificador.
    /// </summary>
    public static TrabajadorInternoCtaimaPeticion PeticionDeAlta(SolicitudAltaTrabajador solicitud)
    {
        // shortcut: Apellidos llega en un solo campo y va entero a firstSurname. Partirlo en dos apellidos
        // rompería los compuestos («de la Fuente»); falta el dato separado en el Trabajador.
        var documento = solicitud.Dni.Trim().ToUpperInvariant();
        var sinSeparadores = Separadores().Replace(documento, string.Empty);
        var esNif = PatronNif().IsMatch(sinSeparadores);

        return new TrabajadorInternoCtaimaPeticion(
            IdentityCardNumber: esNif ? sinSeparadores : documento,
            IdentityCardType: esNif ? TipoDocumentoNif : TipoDocumentoOtro,
            FirstName: solicitud.Nombre.Trim(),
            FirstSurname: solicitud.Apellidos.Trim(),
            SecondSurname: null,
            Enabled: true);
    }

    [GeneratedRegex("^([0-9]{8}|[XYZ][0-9]{7})[A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex PatronNif();

    [GeneratedRegex("[\\s.\\-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separadores();
}
